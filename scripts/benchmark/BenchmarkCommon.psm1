# Shared engine for AI-004 benchmark scripts (ollama-benchmark.ps1, backend-benchmark.ps1).
# Sends predefined prompts at fixed concurrency levels, records start/end time of every
# request, classifies the outcome and samples CPU/RAM/GPU while the scenarios run.
#requires -Version 7.0

Set-StrictMode -Version 3.0

# Predefined prompts. Several per profile so identical prompts do not hit the prompt cache
# every time; all fit the AI-002 num_predict=256 budget when answered as instructed.
$script:Prompts = [ordered]@{
    short  = @(
        'What is the capital of Japan? Answer with one word.'
        'What is 12 + 30? Answer with the number only.'
        'Which planet is known as the Red Planet? Answer with one word.'
        'ประเทศไทยมีเมืองหลวงชื่ออะไร ตอบสั้น ๆ'
        'What is the opposite of hot? Answer with one word.'
        'What is the largest ocean on Earth? Answer with one word.'
        'What is 9 × 7? Answer with the number only.'
        'What color do you get by mixing blue and yellow? Answer with one word.'
        'How many days are in a week? Answer with the number only.'
        'What is the chemical symbol for gold? Answer with the symbol only.'
        'ดาวเคราะห์ที่ใหญ่ที่สุดในระบบสุริยะคือดาวอะไร ตอบสั้น ๆ'
        'What is the opposite of up? Answer with one word.'
        'What is 100 - 58? Answer with the number only.'
        'Which animal is known as the king of the jungle? Answer with one word.'
        'หนึ่งปีมีกี่เดือน ตอบเป็นตัวเลขอย่างเดียว'
    )
    medium = @(
        'Explain in exactly three sentences why the sky looks blue.'
        'In three sentences, explain what a database index is used for.'
        'อธิบายเป็นภาษาไทยสามประโยคว่าทำไมควรล้างมือก่อนรับประทานอาหาร'
        'In three sentences, describe how photosynthesis works.'
        'In three sentences, explain what an HTTP status code is.'
        'In three sentences, explain what a firewall does.'
        'In three sentences, describe how a bicycle stays upright.'
        'Explain in exactly three sentences what causes the seasons on Earth.'
        'In three sentences, explain why we need sleep.'
        'In three sentences, explain what an API is.'
        'อธิบายเป็นภาษาไทยสามประโยคว่าฝนเกิดขึ้นได้อย่างไร'
        'In three sentences, describe how a refrigerator keeps food cold.'
        'In three sentences, explain what inflation means.'
        'อธิบายเป็นภาษาไทยสามประโยคว่าทำไมต้องดื่มน้ำให้เพียงพอ'
        'In three sentences, explain what a computer processor does.'
    )
    long   = @(
        'Write one paragraph of about 120 words explaining how a web browser loads a page.'
        'Write one paragraph of about 120 words about the benefits of regular exercise.'
        'เขียนย่อหน้าภาษาไทยประมาณ 120 คำ อธิบายประโยชน์ของการอ่านหนังสือ'
        'Write one paragraph of about 120 words explaining what cloud computing is.'
        'Write one paragraph of about 120 words describing how rain forms.'
        'Write one paragraph of about 120 words describing how vaccines work.'
        'Write one paragraph of about 120 words explaining why recycling matters.'
        'Write one paragraph of about 120 words describing the water cycle.'
        'Write one paragraph of about 120 words explaining how electricity reaches a home.'
        'Write one paragraph of about 120 words describing the history of the bicycle.'
        'เขียนย่อหน้าภาษาไทยประมาณ 120 คำ อธิบายความสำคัญของการนอนหลับ'
        'Write one paragraph of about 120 words explaining how the internet works.'
        'Write one paragraph of about 120 words explaining what machine learning is.'
        'เขียนย่อหน้าภาษาไทยประมาณ 120 คำ อธิบายประโยชน์ของการออกกำลังกาย'
        'Write one paragraph of about 120 words describing how coffee is produced.'
    )
}

function Get-BenchmarkPrompts { return $script:Prompts }

function Assert-LoopbackBaseUrl([uri]$BaseUrl, [switch]$RejectOllamaPort) {
    if (-not $BaseUrl.IsAbsoluteUri -or -not $BaseUrl.IsLoopback -or
        $BaseUrl.Scheme -notin @('http', 'https') -or
        $BaseUrl.UserInfo -or $BaseUrl.Query -or $BaseUrl.Fragment -or $BaseUrl.AbsolutePath -ne '/' -or
        ($RejectOllamaPort -and $BaseUrl.Port -eq 11434)) {
        throw "BaseUrl must be a loopback HTTP(S) origin without credentials, path, query or fragment: $BaseUrl"
    }
}

function ConvertTo-BenchmarkList {
    # `pwsh -File script.ps1 -Concurrency 1,2` passes one string "1,2"; split it explicitly
    # instead of letting [int[]] binding turn it into 12.
    param([string[]]$Values, [ValidateSet('int', 'profile')][string]$Kind)
    $items = @($Values | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ })
    if (-not $items.Count) { throw "At least one $Kind value is required." }
    if ($Kind -eq 'profile') {
        foreach ($item in $items) { if ($item -cnotin $script:Prompts.Keys) { throw "Unknown profile: $item (use short, medium, long)." } }
        return [string[]]$items
    }
    $numbers = foreach ($item in $items) {
        $number = 0
        if (-not [int]::TryParse($item, [ref]$number) -or $number -lt 1 -or $number -gt 32) { throw "Concurrency must be integers from 1 to 32: $item" }
        $number
    }
    return [int[]]$numbers
}

function Get-HostEnvironment {
    # Hardware/OS facts only: no user, machine or path names are recorded.
    $environment = [ordered]@{
        os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        powershell = $PSVersionTable.PSVersion.ToString()
        cpu = $null; cpu_cores = $null; cpu_threads = $null; ram_total_mb = $null; ram_available_mb = $null
        gpu = $null; vram_total_mb = $null; gpu_driver = $null
    }
    if ($IsWindows) {
        try {
            $cpu = @(Get-CimInstance Win32_Processor)[0]
            $os = Get-CimInstance Win32_OperatingSystem
            $environment.cpu = $cpu.Name.Trim(); $environment.cpu_cores = $cpu.NumberOfCores
            $environment.cpu_threads = $cpu.NumberOfLogicalProcessors
            $environment.ram_total_mb = [math]::Round($os.TotalVisibleMemorySize / 1024)
            $environment.ram_available_mb = [double](Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory).AvailableMBytes
        } catch { }
    }
    $nvidia = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if ($nvidia) {
        try {
            $gpu = (& $nvidia.Source --query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits | Select-Object -First 1) -split ',\s*'
            if ($gpu.Count -eq 3) { $environment.gpu = $gpu[0]; $environment.vram_total_mb = [double]$gpu[1]; $environment.gpu_driver = $gpu[2] }
        } catch { }
    }
    return $environment
}

function Get-Percentile([double[]]$Sorted, [double]$Percent) {
    # Nearest-rank percentile on an ascending array.
    if ($Sorted.Count -eq 0) { return $null }
    $rank = [math]::Ceiling($Sorted.Count * $Percent / 100.0)
    return $Sorted[[math]::Max(0, [int]$rank - 1)]
}

function Get-ErrorCode([string]$Raw) {
    try {
        $document = $Raw | ConvertFrom-Json -AsHashtable
        if ($document -is [Collections.IDictionary] -and $document.error -is [Collections.IDictionary] -and
            $document.error.code -is [string]) { return $document.error.code }
        if ($document -is [Collections.IDictionary] -and $document.error -is [string]) { return 'OLLAMA_ERROR' }
    } catch { }
    return $null
}

function ConvertTo-BenchmarkOutcome {
    param([string]$Target, [int]$Status, [string]$Raw, [string]$Model)

    $result = [ordered]@{
        outcome = 'invalid_response'; error_code = $null; response_chars = $null; done_reason = $null
        prompt_eval_count = $null; eval_count = $null; load_ms = $null; prompt_eval_ms = $null
        eval_ms = $null; total_ms = $null; tokens_per_sec = $null
    }
    if ($Status -ne 200) {
        $result.error_code = Get-ErrorCode $Raw
        $result.outcome = if ($Target -eq 'backend' -and $Status -eq 504) { 'server_timeout' } else { 'server_error' }
        return $result
    }
    try { $body = $Raw | ConvertFrom-Json -AsHashtable } catch { return $result }
    if ($body -isnot [Collections.IDictionary]) { return $result }

    if ($Target -eq 'backend') {
        if ($body.response -is [string] -and -not [string]::IsNullOrWhiteSpace($body.response) -and $body.model -ceq $Model) {
            $result.outcome = 'success'; $result.response_chars = $body.response.Length
        }
        return $result
    }

    # Direct Ollama /api/generate: same success rule as the AI-002 contract.
    foreach ($pair in @(@('load_duration', 'load_ms'), @('prompt_eval_duration', 'prompt_eval_ms'),
                        @('eval_duration', 'eval_ms'), @('total_duration', 'total_ms'))) {
        if ($body[$pair[0]] -is [long] -or $body[$pair[0]] -is [int]) { $result[$pair[1]] = [math]::Round($body[$pair[0]] / 1e6, 1) }
    }
    foreach ($name in 'prompt_eval_count', 'eval_count') {
        if ($body[$name] -is [long] -or $body[$name] -is [int]) { $result[$name] = [long]$body[$name] }
    }
    if ($result.eval_count -and $result.eval_ms) { $result.tokens_per_sec = [math]::Round($result.eval_count / ($result.eval_ms / 1000), 1) }
    $result.done_reason = $body.done_reason
    if ($body.done -ne $true -or $body.model -cne $Model -or $body.response -isnot [string]) { return $result }
    if ($body.done_reason -ceq 'length') { $result.outcome = 'truncated'; return $result }
    if ($body.done_reason -cne 'stop') { return $result }
    try {
        $answer = $body.response | ConvertFrom-Json -AsHashtable
        if ($answer -is [Collections.IDictionary] -and $answer.Count -eq 1 -and $answer.answer -is [string] -and
            -not [string]::IsNullOrWhiteSpace($answer.answer)) {
            $result.outcome = 'success'; $result.response_chars = $answer.answer.Length
        }
    } catch { }
    return $result
}

function Start-ResourceSampler([int]$IntervalMs) {
    $state = [pscustomobject]@{
        Samples = [Collections.Concurrent.ConcurrentQueue[object]]::new()
        Stop = [Threading.ManualResetEventSlim]::new($false)
        Job = $null
    }
    $state.Job = Start-ThreadJob -ArgumentList $state, $IntervalMs -ScriptBlock {
        param($state, $intervalMs)
        $nvidia = Get-Command nvidia-smi -ErrorAction SilentlyContinue
        do {
            $sample = [ordered]@{
                time_utc = [DateTimeOffset]::UtcNow; cpu_percent = $null; ram_available_mb = $null
                ollama_working_set_mb = $null; gpu_percent = $null; vram_used_mb = $null; vram_total_mb = $null
            }
            try {
                if ($IsWindows) {
                    $sample.cpu_percent = [double](Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime
                    # Available memory includes the standby cache; FreePhysicalMemory does not and
                    # reads far too low on a machine that has been running for a while.
                    $sample.ram_available_mb = [double](Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory).AvailableMBytes
                }
                # The Ollama server (ollama*.exe) and the model runner it starts (llama-server.exe,
                # which holds the model's CPU-side memory) both live under the Ollama install folder.
                $ollama = @(Get-Process -Name 'ollama*', 'llama-server' -ErrorAction SilentlyContinue |
                    Where-Object { $_.Path -like '*\Ollama\*' })
                if ($ollama.Count) { $sample.ollama_working_set_mb = [math]::Round(($ollama | Measure-Object WorkingSet64 -Sum).Sum / 1MB) }
                if ($nvidia) {
                    $gpu = (& $nvidia.Source --query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits | Select-Object -First 1) -split ',\s*'
                    if ($gpu.Count -eq 3) {
                        $sample.gpu_percent = [double]$gpu[0]; $sample.vram_used_mb = [double]$gpu[1]; $sample.vram_total_mb = [double]$gpu[2]
                    }
                }
            } catch { }
            $state.Samples.Enqueue([pscustomobject]$sample)
        } until ($state.Stop.Wait($intervalMs))
    }
    # Wait for the first sample so the low-RAM safety check works from the first round.
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ($state.Samples.IsEmpty -and [DateTime]::UtcNow -lt $deadline -and $state.Job.State -in @('NotStarted', 'Running')) { Start-Sleep -Milliseconds 50 }
    return $state
}

function Stop-ResourceSampler($Sampler) {
    if (-not $Sampler) { return @() }
    $Sampler.Stop.Set()
    $null = Wait-Job $Sampler.Job -Timeout 30
    Remove-Job $Sampler.Job -Force
    return @($Sampler.Samples.ToArray())
}

function Get-LatestAvailableMemoryMb($Sampler) {
    if (-not $Sampler) { return $null }
    $latest = @($Sampler.Samples.ToArray() | Where-Object { $null -ne $_.ram_available_mb } | Select-Object -Last 1)
    if ($latest.Count) { return $latest[0].ram_available_mb }
    return $null
}

function Invoke-RequestBatch {
    # Fires all bodies at once and records each request's own start and end time.
    param($Client, [uri]$Endpoint, [string[]]$Bodies, [Diagnostics.Stopwatch]$Clock)

    $pending = [Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $Bodies.Count; $i++) {
        $content = [Net.Http.StringContent]::new($Bodies[$i], [Text.Encoding]::UTF8, 'application/json')
        $item = [pscustomobject]@{
            Index = $i; Content = $content; StartUtc = [DateTimeOffset]::UtcNow; StartMs = $Clock.Elapsed.TotalMilliseconds
            Task = $null; EndUtc = $null; EndMs = $null; Status = $null; Raw = $null; Failure = $null
        }
        $item.Task = $Client.PostAsync($Endpoint, $content)
        $pending.Add($item)
    }
    $all = @($pending)
    while ($pending.Count) {
        $index = [Threading.Tasks.Task]::WaitAny([Threading.Tasks.Task[]]@($pending | ForEach-Object Task), 100)
        if ($index -lt 0) { continue }
        $done = $pending[$index]
        $done.EndUtc = [DateTimeOffset]::UtcNow; $done.EndMs = $Clock.Elapsed.TotalMilliseconds
        $pending.RemoveAt($index)
    }
    foreach ($item in $all) {
        try {
            $response = $item.Task.GetAwaiter().GetResult()
            try {
                $item.Status = [int]$response.StatusCode
                $item.Raw = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            } finally { $response.Dispose() }
        } catch [Threading.Tasks.TaskCanceledException] {
            $item.Failure = 'client_timeout'
        } catch [Net.Http.HttpRequestException] {
            $item.Failure = 'connection_error'
        } catch {
            $item.Failure = if ($_.Exception.InnerException -is [TimeoutException] -or
                                $_.Exception -is [OperationCanceledException]) { 'client_timeout' } else { 'connection_error' }
        } finally { $item.Content.Dispose() }
    }
    return $all
}

function Get-ScenarioResourceSummary {
    param([object[]]$Rows, [object[]]$Samples)

    $first = $Rows | ForEach-Object { [DateTimeOffset]$_.start_utc } | Sort-Object | Select-Object -First 1
    $last = $Rows | ForEach-Object { [DateTimeOffset]$_.end_utc } | Sort-Object | Select-Object -Last 1
    $window = @($Samples | Where-Object { $null -ne $_ -and $_.time_utc -ge $first.AddSeconds(-1) -and $_.time_utc -le $last.AddSeconds(1) })
    $stat = {
        param($name, $kind)
        $values = @($window | ForEach-Object $name | Where-Object { $null -ne $_ })
        if (-not $values.Count) { return $null }
        $measure = $values | Measure-Object -Average -Maximum -Minimum
        switch ($kind) { 'avg' { [math]::Round($measure.Average, 1) } 'max' { $measure.Maximum } 'min' { $measure.Minimum } }
    }
    return [ordered]@{
        cpu_percent_avg = & $stat 'cpu_percent' 'avg'; cpu_percent_max = & $stat 'cpu_percent' 'max'
        ram_available_mb_min = & $stat 'ram_available_mb' 'min'
        ollama_working_set_mb_max = & $stat 'ollama_working_set_mb' 'max'
        gpu_percent_avg = & $stat 'gpu_percent' 'avg'; gpu_percent_max = & $stat 'gpu_percent' 'max'
        vram_used_mb_max = & $stat 'vram_used_mb' 'max'
        resource_sample_count = $window.Count
    }
}

function Get-ScenarioSummary {
    param([object[]]$Rows, [object[]]$Samples, [double]$WallSeconds)

    $measured = @($Rows)
    $success = @($measured | Where-Object outcome -eq 'success')
    $latency = [double[]]@($success | ForEach-Object elapsed_ms | Sort-Object)
    $count = { param($name) @($measured | Where-Object outcome -eq $name).Count }
    $tokens = @($success | Where-Object { $null -ne $_.eval_count })
    $total = $measured.Count
    $summary = [ordered]@{
        requests = $total; success = $success.Count
        success_rate = if ($total) { [math]::Round($success.Count / $total, 4) } else { $null }
        client_timeout = & $count 'client_timeout'; server_timeout = & $count 'server_timeout'
        timeout_rate = if ($total) { [math]::Round(((& $count 'client_timeout') + (& $count 'server_timeout')) / $total, 4) } else { $null }
        server_error = & $count 'server_error'; truncated = & $count 'truncated'
        invalid_response = & $count 'invalid_response'; connection_error = & $count 'connection_error'
        latency_sample_size = $latency.Count
        latency_min_ms = if ($latency.Count) { $latency[0] } else { $null }
        latency_mean_ms = if ($latency.Count) { [math]::Round(($latency | Measure-Object -Average).Average, 1) } else { $null }
        latency_p50_ms = Get-Percentile $latency 50
        latency_p95_ms = Get-Percentile $latency 95
        latency_p99_ms = Get-Percentile $latency 99
        p99_reliable = $latency.Count -ge 100
        latency_max_ms = if ($latency.Count) { $latency[-1] } else { $null }
        wall_seconds = [math]::Round($WallSeconds, 2)
        throughput_rps = if ($WallSeconds -gt 0) { [math]::Round($success.Count / $WallSeconds, 3) } else { $null }
        output_tokens_per_sec = if ($tokens.Count -and $WallSeconds -gt 0) { [math]::Round((($tokens | Measure-Object eval_count -Sum).Sum) / $WallSeconds, 1) } else { $null }
        generation_tokens_per_sec_mean = if ($tokens.Count) { [math]::Round(($tokens | Where-Object tokens_per_sec | Measure-Object tokens_per_sec -Average).Average, 1) } else { $null }
        queue_wait_p95_ms = Get-Percentile ([double[]]@($tokens | Where-Object { $null -ne $_.queue_wait_ms } | ForEach-Object queue_wait_ms | Sort-Object)) 95
    }
    $resources = Get-ScenarioResourceSummary -Rows $measured -Samples $Samples
    foreach ($key in $resources.Keys) { $summary[$key] = $resources[$key] }
    return $summary
}

function Invoke-ChatBenchmark {
    [CmdletBinding()]
    param(
        [ValidateSet('ollama', 'backend')][string]$Target,
        [uri]$Endpoint,
        [scriptblock]$NewBody,
        [string]$Model = 'qwen3:4b',
        [int[]]$Concurrency,
        [string[]]$Profiles,
        [int]$Rounds,
        [int]$MinSamples = 0,
        [int]$WarmupRequests,
        [int]$TimeoutSec,
        [int]$ResourceIntervalMs,
        [int]$MinAvailableMemoryMB,
        [int]$MemoryRecoveryWaitSec = 120,
        [string]$ColdStartNote,
        [string]$OutputCsv,
        [switch]$AppendCsv,
        [string]$OutputJson,
        [hashtable]$Environment
    )

    foreach ($path in @($OutputCsv, $OutputJson)) {
        $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)
        if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($full))) { throw "Output directory must already exist: $path" }
    }
    $prompts = Get-BenchmarkPrompts
    foreach ($name in $Profiles) { if (-not $prompts.Contains($name)) { throw "Unknown profile: $name" } }

    # Invariant culture: a Thai locale would otherwise write a Buddhist-era year.
    $runId = [DateTimeOffset]::Now.ToString('yyyyMMddTHHmmss', [Globalization.CultureInfo]::InvariantCulture) + "-$Target"
    $handler = [Net.Http.SocketsHttpHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseProxy = $false
    $handler.MaxConnectionsPerServer = [math]::Max(16, ($Concurrency | Measure-Object -Maximum).Maximum * 2)
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds($TimeoutSec)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $sampler = if ($ResourceIntervalMs -gt 0) { Start-ResourceSampler $ResourceIntervalMs } else { $null }
    $rows = [Collections.Generic.List[object]]::new()
    $scenarios = [Collections.Generic.List[object]]::new()
    $resourceContexts = [Collections.Generic.List[object]]::new()
    $aborted = $null
    $memoryPauseSeconds = 0.0
    $memoryPauseCount = 0
    $promptCounter = [ref]0

    $toRow = {
        param($item, $phase, $profile, $level, $round, $promptId)
        $parsed = if ($item.Failure) {
            [ordered]@{ outcome = $item.Failure; error_code = $null; response_chars = $null; done_reason = $null
                        prompt_eval_count = $null; eval_count = $null; load_ms = $null; prompt_eval_ms = $null
                        eval_ms = $null; total_ms = $null; tokens_per_sec = $null }
        } else { ConvertTo-BenchmarkOutcome -Target $Target -Status $item.Status -Raw $item.Raw -Model $Model }
        $elapsed = [math]::Round($item.EndMs - $item.StartMs, 1)
        [pscustomobject][ordered]@{
            run_id = $runId; target = $Target; phase = $phase; profile = $profile; concurrency = $level; round = $round
            request_index = $item.Index; prompt_id = $promptId
            start_utc = $item.StartUtc.ToString('o'); end_utc = $item.EndUtc.ToString('o')
            start_offset_ms = [math]::Round($item.StartMs, 1); elapsed_ms = $elapsed
            http_status = $item.Status; outcome = $parsed.outcome; error_code = $parsed.error_code
            response_chars = $parsed.response_chars; done_reason = $parsed.done_reason
            prompt_eval_count = $parsed.prompt_eval_count; eval_count = $parsed.eval_count
            load_ms = $parsed.load_ms; prompt_eval_ms = $parsed.prompt_eval_ms; eval_ms = $parsed.eval_ms
            total_ms = $parsed.total_ms; tokens_per_sec = $parsed.tokens_per_sec
            # Ollama's total_duration already includes the time spent waiting in its queue, so the
            # wait is what is left after load + prompt evaluation + generation.
            queue_wait_ms = if ($null -ne $parsed.eval_ms -and $null -ne $parsed.prompt_eval_ms -and $null -ne $parsed.load_ms) { [math]::Max(0.0, [math]::Round($elapsed - $parsed.load_ms - $parsed.prompt_eval_ms - $parsed.eval_ms, 1)) } else { $null }
        }
    }
    $runBatch = {
        param($phase, $profile, $level, $round)
        $bodies = [Collections.Generic.List[string]]::new(); $ids = [Collections.Generic.List[string]]::new()
        for ($i = 0; $i -lt $level; $i++) {
            $pool = $prompts[$profile]
            $pick = $promptCounter.Value % $pool.Count; $promptCounter.Value++
            $ids.Add("$profile-$($pick + 1)")
            $bodies.Add((& $NewBody $pool[$pick]))
        }
        $items = Invoke-RequestBatch -Client $client -Endpoint $Endpoint -Bodies $bodies.ToArray() -Clock $clock
        $batch = foreach ($item in $items) { & $toRow $item $phase $profile $level $round $ids[$item.Index] }
        foreach ($row in $batch) {
            $rows.Add($row)
            Write-Host ("{0,-8} {1,-6} c={2,-2} r={3} #{4}: {5} HTTP {6} {7} ms" -f $phase, $profile, $level, $round, $row.request_index, $row.outcome, $row.http_status, $row.elapsed_ms)
        }
        $wall = (($items | Measure-Object EndMs -Maximum).Maximum - ($items | Measure-Object StartMs -Minimum).Minimum) / 1000
        return [pscustomobject]@{ Rows = @($batch); Wall = $wall }
    }

    try {
        # The first request is labelled cold only when the caller confirms the model was unloaded.
        $null = & $runBatch $(if ($ColdStartNote) { 'cold' } else { 'warmup' }) $Profiles[0] 1 0
        for ($w = 1; $w -le $WarmupRequests; $w++) { $null = & $runBatch 'warmup' $Profiles[0] 1 $w }
        :scenarioLoop foreach ($profile in $Profiles) {
            foreach ($level in $Concurrency) {
                $scenarioRows = [Collections.Generic.List[object]]::new(); $wall = 0.0
                # Enough rounds that every scenario sends at least $MinSamples requests (needed for P99).
                $roundsForLevel = [math]::Max($Rounds, [int][math]::Ceiling($MinSamples / $level))
                for ($round = 1; $round -le $roundsForLevel; $round++) {
                    $available = Get-LatestAvailableMemoryMb $sampler
                    # Other applications make available RAM dip briefly; wait for it to recover and stop
                    # only when it stays below the limit for -MemoryRecoveryWaitSec seconds.
                    $waitStarted = [DateTime]::UtcNow
                    while ($null -ne $available -and $available -lt $MinAvailableMemoryMB -and
                           ([DateTime]::UtcNow - $waitStarted).TotalSeconds -lt $MemoryRecoveryWaitSec) {
                        Start-Sleep -Seconds 2
                        $available = Get-LatestAvailableMemoryMb $sampler
                    }
                    $pausedSeconds = ([DateTime]::UtcNow - $waitStarted).TotalSeconds
                    if ($pausedSeconds -ge 2) { $memoryPauseSeconds += $pausedSeconds; $memoryPauseCount++ }
                    if ($null -ne $available -and $available -lt $MinAvailableMemoryMB) {
                        $aborted = "Stopped before $profile c=$level round $round`: available RAM $available MB < $MinAvailableMemoryMB MB for $MemoryRecoveryWaitSec s."
                        break
                    }
                    $result = & $runBatch 'measured' $profile $level $round
                    $result.Rows | ForEach-Object { $scenarioRows.Add($_) }
                    $wall += $result.Wall
                }
                if ($scenarioRows.Count) {
                    $snapshot = if ($sampler) { @($sampler.Samples.ToArray()) } else { @() }
                    $summary = Get-ScenarioSummary -Rows $scenarioRows.ToArray() -Samples $snapshot -WallSeconds $wall
                    $scenario = [ordered]@{ target = $Target; profile = $profile; concurrency = $level; rounds = $roundsForLevel }
                    foreach ($key in $summary.Keys) { $scenario[$key] = $summary[$key] }
                    $scenarios.Add($scenario)
                    $resourceContexts.Add([pscustomobject]@{ Scenario = $scenario; Rows = $scenarioRows.ToArray() })
                    Write-Host ("SCENARIO {0} {1} c={2}: {3}/{4} success, p50={5} p95={6} ms, {7} req/s" -f $Target, $profile, $level,
                        $summary.success, $summary.requests, $summary.latency_p50_ms, $summary.latency_p95_ms, $summary.throughput_rps)
                }
                if ($aborted) { break scenarioLoop }
            }
        }
    } finally {
        $samples = Stop-ResourceSampler $sampler
        $client.Dispose()
    }

    # A collector can timestamp a sample during a scenario but enqueue it only during shutdown.
    # Reattach resources from the drained queue, retaining the exact original window and request metrics.
    foreach ($context in $resourceContexts) {
        $resources = Get-ScenarioResourceSummary -Rows $context.Rows -Samples @($samples)
        foreach ($key in $resources.Keys) { $context.Scenario[$key] = $resources[$key] }
    }

    $csvRows = $rows | Select-Object *
    if ($AppendCsv -and (Test-Path -LiteralPath $OutputCsv)) {
        $csvRows | Export-Csv -LiteralPath $OutputCsv -Append -NoTypeInformation -Encoding utf8NoBOM -UseQuotes AsNeeded
    } else {
        $csvRows | Export-Csv -LiteralPath $OutputCsv -NoTypeInformation -Encoding utf8NoBOM -UseQuotes AsNeeded
    }
    $cold = @($rows | Where-Object phase -eq 'cold')
    $summaryDocument = [ordered]@{
        run_id = $runId; target = $Target; endpoint = $Endpoint.AbsoluteUri; tested_at = [DateTimeOffset]::Now.ToString('o')
        preliminary = $true
        parameters = [ordered]@{
            concurrency = $Concurrency; profiles = $Profiles; rounds = $Rounds; min_samples_per_scenario = $MinSamples; warmup_requests = $WarmupRequests
            client_timeout_sec = $TimeoutSec; resource_interval_ms = $ResourceIntervalMs; min_available_memory_mb = $MinAvailableMemoryMB
            memory_recovery_wait_sec = $MemoryRecoveryWaitSec
        }
        prompts = $prompts
        environment = $Environment
        cold_start_note = $ColdStartNote
        cold_start = if ($cold.Count) { $cold[0] } else { $null }
        aborted = $aborted
        memory_pauses = [ordered]@{ count = $memoryPauseCount; total_seconds = [math]::Round($memoryPauseSeconds, 1) }
        scenarios = $scenarios.ToArray()
        resource_samples = $samples
    }
    [IO.File]::WriteAllText($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputJson),
        ($summaryDocument | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    Write-Host "SUMMARY: target=$Target scenarios=$($scenarios.Count) requests=$($rows.Count) aborted=$([bool]$aborted)"
    return [pscustomobject]@{ Aborted = $aborted; Scenarios = $scenarios.ToArray(); Rows = $rows.ToArray() }
}

Export-ModuleMember -Function Get-BenchmarkPrompts, Assert-LoopbackBaseUrl, ConvertTo-BenchmarkList, Get-HostEnvironment, Get-Percentile, ConvertTo-BenchmarkOutcome, Invoke-ChatBenchmark
