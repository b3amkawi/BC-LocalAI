# AI-005: reads an Ollama streaming response (NDJSON) and classifies the outcome.
# Used by scripts/test-ollama-streaming.ps1; also the reference for the BE-004 implementation
# described in docs/ai/ollama-streaming-contract.md.
#requires -Version 7.0

Set-StrictMode -Version 3.0

# ---------------------------------------------------------------------------------------------
# Incremental decoder for  {"answer": "<text>"}  delivered as arbitrary text fragments.
# Ollama streams the constrained JSON token by token, so a fragment can end inside a key name
# (`a` `ns` `we` `r`), inside an escape (`\` then `"` in the next record) or inside a Thai
# grapheme (`กร` then `ุง`). Only the value of "answer" is emitted; the JSON scaffolding is not.
# ---------------------------------------------------------------------------------------------
function New-AnswerDecoder {
    param([int]$MaxAnswerChars = 1048576)
    [pscustomobject]@{
        State = 'start'; Key = [Text.StringBuilder]::new(); Answer = [Text.StringBuilder]::new()
        Escape = ''; Hex = ''; SeenAnswer = $false; Error = $null; Complete = $false
        MaxAnswerChars = $MaxAnswerChars
    }
}

function Add-AnswerFragment {
    <# Feeds one fragment; returns the new answer text it contributed (possibly empty). #>
    param([Parameter(Mandatory)]$Decoder, [AllowEmptyString()][string]$Text)

    $delta = [Text.StringBuilder]::new()
    foreach ($ch in $Text.ToCharArray()) {
        if ($Decoder.Error) { break }
        $isSpace = [char]::IsWhiteSpace($ch)
        switch ($Decoder.State) {
            'start' {
                if ($ch -ceq '{') { $Decoder.State = 'key_or_end' }
                elseif (-not $isSpace) { $Decoder.Error = 'not_a_json_object' }
            }
            'key_or_end' {
                if ($ch -ceq '"') { $Decoder.State = 'key'; $null = $Decoder.Key.Clear() }
                elseif ($ch -ceq '}') { $Decoder.Error = 'missing_answer' }
                elseif (-not $isSpace) { $Decoder.Error = 'unexpected_structure' }
            }
            'key' {
                if ($ch -ceq '"') { $Decoder.State = 'after_key' }
                elseif ($ch -ceq '\' -or $Decoder.Key.Length -ge 64) { $Decoder.Error = 'unexpected_structure' }
                else { $null = $Decoder.Key.Append($ch) }
            }
            'after_key' {
                if ($ch -ceq ':') {
                    if ($Decoder.Key.ToString() -cne 'answer' -or $Decoder.SeenAnswer) { $Decoder.Error = 'unexpected_field' }
                    else { $Decoder.State = 'value' }
                } elseif (-not $isSpace) { $Decoder.Error = 'unexpected_structure' }
            }
            'value' {
                if ($ch -ceq '"') { $Decoder.State = 'answer'; $Decoder.SeenAnswer = $true }
                elseif (-not $isSpace) { $Decoder.Error = 'answer_not_string' }
            }
            'answer' {
                if ($Decoder.Escape -ceq '') {
                    if ($ch -ceq '\') { $Decoder.Escape = '\' }
                    elseif ($ch -ceq '"') { $Decoder.State = 'after_answer' }
                    elseif ([int]$ch -lt 0x20) { $Decoder.Error = 'invalid_string_character' }
                    else { $null = $Decoder.Answer.Append($ch); $null = $delta.Append($ch) }
                } elseif ($Decoder.Escape -ceq '\') {
                    $mapped = switch -CaseSensitive ($ch) {
                        '"' { '"' } '\' { '\' } '/' { '/' } 'b' { "`b" } 'f' { "`f" }
                        'n' { "`n" } 'r' { "`r" } 't' { "`t" } default { $null }
                    }
                    if ($ch -ceq 'u') { $Decoder.Escape = 'u'; $Decoder.Hex = '' }
                    elseif ($null -ne $mapped) {
                        $null = $Decoder.Answer.Append($mapped); $null = $delta.Append($mapped); $Decoder.Escape = ''
                    } else { $Decoder.Error = 'invalid_escape' }
                } else {
                    # \uXXXX: a surrogate pair simply arrives as two \u escapes and stays valid UTF-16
                    if ($ch -notmatch '[0-9A-Fa-f]') { $Decoder.Error = 'invalid_escape' }
                    else {
                        $Decoder.Hex += $ch
                        if ($Decoder.Hex.Length -eq 4) {
                            $unit = [string][char][Convert]::ToUInt16($Decoder.Hex, 16)
                            $null = $Decoder.Answer.Append($unit); $null = $delta.Append($unit)
                            $Decoder.Escape = ''; $Decoder.Hex = ''
                        }
                    }
                }
                if ($Decoder.Answer.Length -gt $Decoder.MaxAnswerChars) { $Decoder.Error = 'answer_too_long' }
            }
            'after_answer' {
                if ($ch -ceq '}') { $Decoder.State = 'done'; $Decoder.Complete = $true }
                elseif ($ch -ceq ',') { $Decoder.Error = 'unexpected_field' }
                elseif (-not $isSpace) { $Decoder.Error = 'unexpected_structure' }
            }
            'done' { if (-not $isSpace) { $Decoder.Error = 'trailing_data' } }
        }
    }
    return $delta.ToString()
}

function Convert-AnswerFragments {
    <# Convenience for tests: feeds all fragments and returns the final decoder state with Deltas. #>
    param([Parameter(Mandatory)][AllowEmptyString()][string[]]$Fragments)
    $decoder = New-AnswerDecoder
    $deltas = [Collections.Generic.List[string]]::new()
    foreach ($fragment in $Fragments) { $deltas.Add((Add-AnswerFragment -Decoder $decoder -Text $fragment)) }
    [pscustomobject]@{ Answer = $decoder.Answer.ToString(); Complete = $decoder.Complete; Error = $decoder.Error; Deltas = $deltas.ToArray() }
}

# ---------------------------------------------------------------------------------------------
# Safe text and error-code mapping
# ---------------------------------------------------------------------------------------------
function ConvertTo-SafeText {
    <# Removes local endpoints and paths from upstream text (Ollama puts them in error messages). #>
    param([AllowNull()][string]$Text, [int]$MaxLength = 200)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $safe = [regex]::Replace($Text, '(?i)(127\.0\.0\.1|localhost|\[::1\]|::1)(:\d{2,5})?', '<loopback>')
    $safe = [regex]::Replace($safe, '(?i)\b[A-Z]:\\[^\s"'']+', '<path>')
    $safe = [regex]::Replace($safe, '\\u003e', '>')
    if ($safe.Length -gt $MaxLength) { $safe = $safe.Substring(0, $MaxLength) + '...' }
    return $safe
}

function Get-PublicErrorCode {
    <# Outcome -> application error code (names follow BC-LocalAI/Services/AiServiceException.cs). #>
    param([Parameter(Mandatory)][string]$Outcome)
    switch ($Outcome) {
        'completed' { $null }
        'cancelled' { $null }
        'unavailable' { 'AI_UNAVAILABLE' }
        'timeout' { 'AI_TIMEOUT' }
        'http_error' { 'AI_PROVIDER_ERROR' }
        'provider_error' { 'AI_PROVIDER_ERROR' }
        'truncated' { 'AI_RESPONSE_TOO_LARGE' }
        default { 'AI_INVALID_RESPONSE' }   # invalid, interrupted
    }
}

function Assert-LoopbackUrl([uri]$Url) {
    if (-not $Url.IsAbsoluteUri -or -not $Url.IsLoopback -or $Url.Scheme -notin @('http', 'https') -or
        $Url.UserInfo -or $Url.Query -or $Url.Fragment -or $Url.AbsolutePath -ne '/') {
        throw "BaseUrl must be a loopback HTTP(S) origin without credentials, path, query or fragment: $Url"
    }
}

# ---------------------------------------------------------------------------------------------
# Streaming request
# ---------------------------------------------------------------------------------------------
function Invoke-OllamaStream {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][uri]$BaseUrl,
        [hashtable]$Body,
        [byte[]]$RawBody,
        [string]$Path = '/api/generate',
        [string]$ExpectedModel = 'qwen3:4b',
        [ValidateRange(1, 3600)][int]$TotalTimeoutSec = 120,
        [ValidateRange(1, 3600)][int]$IdleTimeoutSec = 60,
        [int]$CancelAfterRecords = 0,
        [scriptblock]$OnRecord,
        [switch]$KeepRecords,
        # Keeps the .NET default (drain an unread response for up to 2 s before closing the connection).
        # Only for demonstrating the pitfall: Ollama keeps generating while the connection stays open.
        [switch]$DrainOnDispose
    )

    Assert-LoopbackUrl $BaseUrl
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $result = [ordered]@{
        outcome = $null; error_code = $null; http_status = $null; content_type = $null
        records = 0; deltas = 0; answer = ''; assembled_response = ''
        has_thinking_field = $false; thinking_chars = 0; leaked_reasoning_chars = 0
        done = $false; done_reason = $null; model = $null
        eval_count = $null; prompt_eval_count = $null; load_ms = $null; total_ms = $null
        first_record_ms = $null; first_answer_ms = $null; completed_ms = $null
        interval_mean_ms = $null; interval_max_ms = $null
        timeout_kind = $null; upstream_error = $null; decoder_error = $null; problems = @()
        record_samples = @()
    }
    $problems = [Collections.Generic.List[string]]::new()
    $samples = [Collections.Generic.List[object]]::new()
    $handler = [Net.Http.SocketsHttpHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseProxy = $false
    # Disposing a response that was not read to the end must close the connection at once. With the
    # default (MaxResponseDrainSize 1 MiB, ResponseDrainTimeout 2 s) .NET keeps it open for up to 2 s,
    # and Ollama keeps generating for that time.
    if (-not $DrainOnDispose) { $handler.MaxResponseDrainSize = 0 }
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $totalCts = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TotalTimeoutSec))
    $request = $null; $response = $null; $stream = $null
    $finish = {
        param($outcome)
        $result.outcome = $outcome
        $result.error_code = Get-PublicErrorCode $outcome
        $result.total_ms = [math]::Round($clock.Elapsed.TotalMilliseconds, 1)
        $result.problems = $problems.ToArray()
        if ($KeepRecords) { $result.record_samples = $samples.ToArray() }
        [pscustomobject]$result
    }

    try {
        try {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, [uri]::new($BaseUrl, $Path))
            if ($PSBoundParameters.ContainsKey('RawBody')) {
                $request.Content = [Net.Http.ByteArrayContent]::new($RawBody)
                $request.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/json')
            } else {
                $json = $Body | ConvertTo-Json -Depth 12 -Compress
                $request.Content = [Net.Http.StringContent]::new($json, [Text.UTF8Encoding]::new($false), 'application/json')
            }
            $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $totalCts.Token).GetAwaiter().GetResult()
        } catch [OperationCanceledException] {
            $result.timeout_kind = 'total'
            return (& $finish 'timeout')
        } catch {
            $problems.Add("connection failed: $($_.Exception.GetType().Name)")
            return (& $finish 'unavailable')
        }

        $result.http_status = [int]$response.StatusCode
        $result.content_type = [string]$response.Content.Headers.ContentType?.MediaType
        if ($result.http_status -ne 200) {
            $text = try { $response.Content.ReadAsStringAsync($totalCts.Token).GetAwaiter().GetResult() } catch { '' }
            try {
                $parsed = $text | ConvertFrom-Json -AsHashtable
                if ($parsed -is [Collections.IDictionary] -and $parsed.error -is [string]) { $result.upstream_error = ConvertTo-SafeText $parsed.error }
            } catch { }
            return (& $finish 'http_error')
        }
        if ($result.content_type -cne 'application/x-ndjson') { $problems.Add("unexpected content type '$($result.content_type)'") }

        $stream = $response.Content.ReadAsStreamAsync($totalCts.Token).GetAwaiter().GetResult()
        $decoder = New-AnswerDecoder
        $utf8 = [Text.UTF8Encoding]::new($false, $false).GetDecoder()   # stateful: keeps a split multi-byte character
        $bytes = [byte[]]::new(4096)
        $chars = [char[]]::new(4097)
        $line = [Text.StringBuilder]::new()
        $answer = [Text.StringBuilder]::new()
        $assembled = [Text.StringBuilder]::new()
        $intervals = [Collections.Generic.List[double]]::new()
        # Shared with the line-processing scriptblock (a child scope cannot assign the function's locals).
        $state = [pscustomobject]@{ Stop = $null; LastMs = $null }

        $processLine = {
            param([string]$text)
            if ([string]::IsNullOrWhiteSpace($text)) { return }
            if ($result.done) { $problems.Add('data after the final record'); $state.Stop = 'invalid'; return }
            try { $record = $text | ConvertFrom-Json -AsHashtable -NoEnumerate }
            catch { $problems.Add('a line is not valid JSON'); $state.Stop = 'invalid'; return }
            if ($record -isnot [Collections.IDictionary]) { $problems.Add('a line is not a JSON object'); $state.Stop = 'invalid'; return }
            $result.records++
            $now = $clock.Elapsed.TotalMilliseconds
            if ($null -eq $result.first_record_ms) { $result.first_record_ms = [math]::Round($now, 1) }
            if ($null -ne $state.LastMs) { $intervals.Add($now - $state.LastMs) }
            $state.LastMs = $now
            if ($record.ContainsKey('error')) {
                $result.upstream_error = ConvertTo-SafeText ([string]$record.error)
                $state.Stop = 'provider_error'; return
            }
            if ($record.model -isnot [string] -or $record.model -cne $ExpectedModel) { $problems.Add('model does not match'); $state.Stop = 'invalid'; return }
            if ($record.done -isnot [bool]) { $problems.Add('done is not a boolean'); $state.Stop = 'invalid'; return }
            $fragment = ''
            if ($record.ContainsKey('response')) {
                if ($record.response -isnot [string]) { $problems.Add('response is not a string'); $state.Stop = 'invalid'; return }
                $fragment = $record.response
            }
            if ($record.ContainsKey('thinking')) {
                $result.has_thinking_field = $true
                if ($record.thinking -is [string]) { $result.thinking_chars += $record.thinking.Length }   # counted, never forwarded
            }
            $null = $assembled.Append($fragment)
            $newText = Add-AnswerFragment -Decoder $decoder -Text $fragment
            if ($newText.Length) {
                $null = $answer.Append($newText)
                $result.deltas++
                if ($null -eq $result.first_answer_ms) { $result.first_answer_ms = [math]::Round($now, 1) }
            }
            if ($samples.Count -lt 40 -and $KeepRecords) {
                $samples.Add([ordered]@{ t_ms = [math]::Round($now, 1); response = $fragment; thinking = $(if ($record.ContainsKey('thinking')) { [string]$record.thinking } else { $null }); done = $record.done })
            }
            if ($decoder.Error) { $problems.Add("answer decoder: $($decoder.Error)"); $state.Stop = 'invalid'; return }
            if ($record.done) {
                $result.done = $true
                $result.done_reason = if ($record.done_reason -is [string]) { $record.done_reason } else { $null }
                foreach ($pair in @(@('eval_count', 'eval_count'), @('prompt_eval_count', 'prompt_eval_count'))) {
                    if ($record.ContainsKey($pair[0]) -and $record[$pair[0]] -is [ValueType]) { $result[$pair[1]] = [long]$record[$pair[0]] }
                }
                if ($record.ContainsKey('load_duration') -and $record.load_duration -is [ValueType]) { $result.load_ms = [math]::Round($record.load_duration / 1e6, 1) }
            }
            if ($null -ne $OnRecord) { & $OnRecord $result.records }
            if ($CancelAfterRecords -gt 0 -and $result.records -ge $CancelAfterRecords -and -not $result.done) { $state.Stop = 'cancelled' }
        }

        $eof = $false
        while (-not $eof -and -not $state.Stop) {
            $idle = [Threading.CancellationTokenSource]::CreateLinkedTokenSource($totalCts.Token)
            $idle.CancelAfter([TimeSpan]::FromSeconds($IdleTimeoutSec))
            try {
                $read = $stream.ReadAsync($bytes, 0, $bytes.Length, $idle.Token).GetAwaiter().GetResult()
            } catch [OperationCanceledException] {
                $result.timeout_kind = if ($totalCts.IsCancellationRequested) { 'total' } else { 'idle' }
                $result.answer = $answer.ToString(); $result.assembled_response = $assembled.ToString()
                return (& $finish 'timeout')
            } catch {
                $problems.Add("connection lost while reading: $($_.Exception.GetType().Name)")
                $eof = $true; $read = 0
            } finally { $idle.Dispose() }
            if ($read -eq 0) { $eof = $true; continue }
            $count = $utf8.GetChars($bytes, 0, $read, $chars, 0, $false)
            for ($i = 0; $i -lt $count -and -not $state.Stop; $i++) {
                if ($chars[$i] -ceq "`n") { & $processLine $line.ToString(); $null = $line.Clear() }
                else { $null = $line.Append($chars[$i]) }
            }
        }
        if (-not $state.Stop -and $line.Length -gt 0) { & $processLine $line.ToString(); $null = $line.Clear() }

        $result.answer = $answer.ToString()
        $result.assembled_response = $assembled.ToString()
        $result.decoder_error = $decoder.Error
        if ($intervals.Count) {
            $result.interval_mean_ms = [math]::Round(($intervals | Measure-Object -Average).Average, 1)
            $result.interval_max_ms = [math]::Round(($intervals | Measure-Object -Maximum).Maximum, 1)
        }

        if ($state.Stop) { return (& $finish $state.Stop) }
        if (-not $result.done) { $problems.Add('the stream ended before the final record (done=true)'); return (& $finish 'interrupted') }
        $result.completed_ms = [math]::Round($clock.Elapsed.TotalMilliseconds, 1)
        if ($result.done_reason -ceq 'length') { return (& $finish 'truncated') }
        if ($result.done_reason -cne 'stop') { $problems.Add("done_reason is '$($result.done_reason)', expected 'stop'"); return (& $finish 'invalid') }
        if (-not $decoder.Complete) { $problems.Add('the answer JSON object is incomplete'); return (& $finish 'invalid') }
        if ([string]::IsNullOrWhiteSpace($result.answer)) { $problems.Add('the answer is empty'); return (& $finish 'invalid') }
        if ($result.answer -match '(?i)</?think>') { $problems.Add('the answer contains a thinking marker'); return (& $finish 'invalid') }
        return (& $finish 'completed')
    } finally {
        foreach ($disposable in $stream, $response, $request, $client, $handler, $totalCts) { if ($disposable) { $disposable.Dispose() } }
    }
}

Export-ModuleMember -Function New-AnswerDecoder, Add-AnswerFragment, Convert-AnswerFragments, ConvertTo-SafeText, Get-PublicErrorCode, Assert-LoopbackUrl, Invoke-OllamaStream
