#requires -Version 7
# Live verification of the named, isolated FE-003 development fixture only. No company accounts/data, redirects or TLS bypass.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$private = Join-Path $repo '.local/fe003-mysql/private'
& (Join-Path $PSScriptRoot 'fe003-local.ps1') -Action Verify
if ($LASTEXITCODE -ne 0) { throw 'Fixture verification failed.' }
$accounts = Get-Content -LiteralPath (Join-Path $private 'test-accounts.json') -Raw | ConvertFrom-Json
$script:checks = 0
$script:step = 'initialization'
function Check([bool]$Condition,[string]$Label) {
    $script:step = $Label
    if (-not $Condition) { throw "Check failed: $Label" }
    $script:checks++; Write-Host "PASS $Label"
}
function Browser {
    $handler=[Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect=$false
    $handler.CookieContainer=[Net.CookieContainer]::new()
    $browser=[Net.Http.HttpClient]::new($handler); $browser.BaseAddress=[uri]'https://localhost:5937'; $browser.Timeout=[TimeSpan]::FromSeconds(210)
    return $browser
}
function Request($Browser,[string]$Method,[string]$Path,$Body=$null,$Actor=$null,$Csrf=$null) {
    $script:step = "$Method " + ($Path -split '\?')[0]
    $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method),$Path)
    try {
        if ($null -ne $Body) { $request.Content=[Net.Http.StringContent]::new(($Body|ConvertTo-Json -Compress),[Text.Encoding]::UTF8,'application/json') }
        if ($Actor) { $request.Headers.Add('X-BC-Expected-User',$Actor) }
        if ($Csrf) { $request.Headers.Add('X-CSRF-TOKEN',$Csrf) }
        $response=$Browser.SendAsync($request).GetAwaiter().GetResult()
        try {
            $text=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $json=$null; if ($response.Content.Headers.ContentType.MediaType -eq 'application/json') { $json=ConvertFrom-Json -InputObject $text }
            $actorHeader=$null; if ($response.Headers.Contains('X-BC-User')) { $actorHeader=($response.Headers.GetValues('X-BC-User') -join ',') }
            return @{Status=[int]$response.StatusCode;Json=$json;Text=$text;Actor=$actorHeader;NoStore=$response.Headers.CacheControl.NoStore}
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}
function Token($Browser) {
    $result=Request $Browser GET '/api/auth/csrf'
    if ($result.Status -ne 200 -or $result.Json.token -notmatch '^[A-Za-z0-9_-]+$') { throw 'CSRF token unavailable.' }
    return $result.Json.token
}
function Login($Browser,$Account) {
    $result=Request $Browser POST '/api/auth/login' @{username=$Account.Username;password=$Account.Password} $null (Token $Browser)
    Check ($result.Status -eq 200) 'Synthetic Login succeeds through real MySQL authentication'
    Check ($result.Text -notmatch [regex]::Escape($Account.Password)) 'Login response does not expose submitted secret'
    return $result.Json.user.id
}
$a=Browser; $b=Browser; $anonymous=Browser
try {
    $script:step='public Login page'
    $loginPage=Request $anonymous GET '/login'
    Check ($loginPage.Status -eq 200 -and $loginPage.NoStore) 'Real Login page is HTTPS and no-store'
    Check ($loginPage.Text -match '/js/login.js' -and $loginPage.Text -notmatch 'auth-mock|auth-preview.js') 'Real Login does not silently use Mock'
    Check ((Request $anonymous GET '/').Status -eq 302) 'Anonymous protected view redirects to Login'
    Check ((Request $anonymous GET '/api/conversations').Status -eq 401) 'Anonymous history access denied as JSON401'
    $actorA=Login $a ($accounts | Where-Object State -eq 'active')
    $actorB=Login $b ($accounts | Where-Object State -eq 'locked')
    Check ($actorA -ne $actorB) 'Two independent synthetic identities'
    $me=Request $a GET '/api/auth/me' $null $actorA
    Check ($me.Status -eq 200 -and $me.Actor -eq $actorA -and $me.Json.user.id -eq $actorA) 'Profile body and response actor agree'
    $title='FE003 live API '+[guid]::NewGuid().ToString('N')
    $created=Request $a POST '/api/conversations' @{title=$title} $actorA (Token $a)
    Check ($created.Status -eq 201 -and $created.Actor -eq $actorA) 'Create persists with actual server actor'
    Check ($created.Text -notmatch 'ownerUserId|passwordHash|securityStamp') 'Public history DTO excludes persistence/security fields'
    $id=$created.Json.id
    $missing=Request $a POST '/api/conversations' @{title='must not write'} $actorA
    Check ($missing.Status -eq 403 -and $missing.Json.error.code -eq 'AUTH_CSRF_INVALID') 'Missing header CSRF blocks history mutation'
    $stale=Request $a POST '/api/conversations' @{title='must not write'} $actorB (Token $a)
    Check ($stale.Status -eq 401 -and $stale.Json.error.code -eq 'AUTH_SESSION_CHANGED') 'Wrong expected actor blocks mutation before persistence'
    $foreignToken=Request $b POST '/api/conversations' @{title='must not write'} $actorB (Token $a)
    Check ($foreignToken.Status -eq 403) 'CSRF token from another account is rejected'
    foreach ($method in @('GET','PATCH','DELETE')) {
        $body=$null; $csrf=$null
        if ($method -eq 'PATCH') { $body=@{title='must not rename'} }
        if ($method -ne 'GET') { $csrf=Token $b }
        $foreign=Request $b $method "/api/conversations/$id" $body $actorB $csrf
        Check ($foreign.Status -eq 404 -and $foreign.Json.error.code -eq 'CONVERSATION_NOT_FOUND') "Other user cannot $method the conversation"
    }
    Check ((Request $a GET "/api/conversations/$id" $null $actorA).Json.title -eq $title) 'Owner data remains unchanged after cross-user attempts'
    $renamed=Request $a PATCH "/api/conversations/$id" @{title='FE003 renamed API'} $actorA (Token $a)
    Check ($renamed.Status -eq 200 -and $renamed.Json.title -eq 'FE003 renamed API') 'Rename persists through real MySQL repository'
    $stream=Request $a POST '/api/chat/stream' @{message='ทักทายสั้น ๆ เป็นภาษาไทย';conversationId=$id} $actorA (Token $a)
    Check ($stream.Status -eq 200 -and $stream.Actor -eq $actorA) 'Real Ollama stream carries authenticated response actor'
    Check ($stream.Text.Contains('event: started') -and $stream.Text.Contains('event: completed') -and -not $stream.Text.Contains('event: error')) 'Real stream has successful terminal event'
    $started=@($stream.Text -split "`n`n" | Where-Object { $_.StartsWith('event: started') })[0]
    $metadata=($started -split "`n" | Where-Object { $_.StartsWith('data: ') }).Substring(6) | ConvertFrom-Json
    Check ($metadata.conversationId -eq $id -and $metadata.model -eq 'qwen3:4b') 'SSE metadata agrees with selected conversation and qwen3:4b'
    $messages=Request $a GET "/api/conversations/$id/messages?limit=20" $null $actorA
    Check ($messages.Json.items.Count -eq 2) 'Question and answer persisted as exactly one pair'
    Check (($messages.Json.items | Where-Object status -ne 'completed').Count -eq 0) 'Persisted pair has completed status only after terminal success'
    Check ($messages.Json.items[1].content -match '[ก-๙]') 'Real Ollama answer contains Thai'
    $pagingSeed=Request $a POST '/api/conversations' @{title='FE003 cursor seed '+[guid]::NewGuid().ToString('N')} $actorA (Token $a)
    Check ($pagingSeed.Status -eq 201) 'Pagination creates its own second conversation without relying on browser data'
    $page=Request $a GET '/api/conversations?limit=1' $null $actorA
    Check ($page.Json.items.Count -eq 1 -and $page.Json.nextCursor) 'Real cursor pagination first page is bounded'
    $next=Request $a GET ('/api/conversations?limit=1&cursor='+[uri]::EscapeDataString($page.Json.nextCursor)) $null $actorA
    Check ($next.Json.items.Count -eq 1 -and $next.Json.items[0].id -ne $page.Json.items[0].id) 'Cursor next page advances without duplicates'
    $staleLogout=Request $b POST '/API/AUTH/LOGOUT/' $null $actorA (Token $b)
    Check ($staleLogout.Status -eq 401) 'Stale logout precondition also protects case/trailing-slash route alias'
    Check ((Request $b GET '/api/auth/me' $null $actorB).Status -eq 200) 'Stale tab did not log out a different user'
    Check ((Request $b POST '/api/auth/logout' $null $actorB (Token $b)).Status -eq 204) 'Correct logout revokes server session'
    Check ((Request $b GET '/api/auth/me').Status -eq 401) 'Logged-out cookie can no longer read profile'
    Check ((Request $a GET '/api/auth/me' $null $actorA).Status -eq 200) 'Logging out B does not revoke A session'
    Check ((Request $a DELETE "/api/conversations/$id" $null $actorA (Token $a)).Status -eq 204) 'Owner soft-delete succeeds'
    Check ((Request $a GET "/api/conversations/$id" $null $actorA).Status -eq 404) 'Deleted conversation cannot be reopened'
    Check ((Request $a GET "/api/conversations/$id/messages" $null $actorA).Status -eq 404) 'Deleted messages are not readable through API'
    Check ((Request $a DELETE ("/api/conversations/"+$pagingSeed.Json.id) $null $actorA (Token $a)).Status -eq 204) 'Pagination seed is soft-deleted by its owner'
    Request $a POST '/api/auth/logout' $null $actorA (Token $a) | Out-Null
    Write-Output "FE-003 live verification passed: $script:checks checks; real MySQL3338/Ollama qwen3:4b; no secrets printed."
} catch { throw "FE-003 live verification failed during '$script:step'. No raw response, credential or database error printed." }
finally { $a.Dispose();$b.Dispose();$anonymous.Dispose() }
