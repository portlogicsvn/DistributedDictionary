param(
    [string]$RedisConnection = "localhost:6379",
    [int]$BackplaneTimeoutSeconds = 8
)

$ErrorActionPreference = "Stop"

$repoRoot = "e:\Software Engineer\PORTLOGICS JSC\DVP\FusionCache+RedLock"
$sampleProject = Join-Path $repoRoot "samples\DistributedConcurrentDictionary.Sample\DistributedConcurrentDictionary.Sample.csproj"
$testProject = Join-Path $repoRoot "tests\DistributedConcurrentDictionary.Tests\DistributedConcurrentDictionary.Tests.csproj"
$runId = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$channel = "e2e-$runId"
$resultsDir = Join-Path $repoRoot "tests\e2e\results\$runId"
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
$summaryPath = Join-Path $resultsDir "summary.log"
$stepsPath = Join-Path $resultsDir "steps.jsonl"
$watchRawPath = Join-Path $resultsDir "scenario1-B-watch.raw.log"
$raceAPath = Join-Path $resultsDir "scenario2-A.raw.log"
$raceBPath = Join-Path $resultsDir "scenario2-B.raw.log"
$scenario3Path = Join-Path $resultsDir "scenario3-dotnet-test.raw.log"
$scenario4Path = Join-Path $resultsDir "scenario4-dotnet-test.raw.log"

function Write-Summary([string]$line) {
    Write-Host $line
    Add-Content -Path $summaryPath -Value $line
}

function Write-Step(
    [string]$scenario,
    [string]$step,
    [string]$status,
    [string]$message,
    [string]$instance = "",
    [string]$command = "",
    [long]$elapsedMs = 0
) {
    $obj = [ordered]@{
        ts = (Get-Date).ToString("o")
        scenario = $scenario
        step = $step
        status = $status
        instance = $instance
        command = $command
        elapsed_ms = $elapsedMs
        message = $message
    }
    Add-Content -Path $stepsPath -Value ($obj | ConvertTo-Json -Compress)
}

Write-Summary "RunId: $runId"
Write-Summary "Channel: $channel"
Write-Summary "RedisConnection: $RedisConnection"
Write-Summary "ResultsDir: $resultsDir"

function Invoke-SampleCommand(
    [string]$instanceName,
    [string]$command,
    [string]$scenario,
    [string]$step
) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $old = $env:REDIS_CONNECTION
    $env:REDIS_CONNECTION = $RedisConnection
    try {
        $out = & dotnet run --project $sampleProject -- --backplane --instance=$instanceName --channel=$channel --cmd $command 2>&1 | Out-String
        $sw.Stop()
        Write-Step -scenario $scenario -step $step -status "PASS" -instance $instanceName -command $command -elapsedMs $sw.ElapsedMilliseconds -message "Command completed."
        return $out
    }
    catch {
        $sw.Stop()
        Write-Step -scenario $scenario -step $step -status "FAIL" -instance $instanceName -command $command -elapsedMs $sw.ElapsedMilliseconds -message $_.Exception.Message
        throw
    }
    finally {
        $env:REDIS_CONNECTION = $old
    }
}

function Start-WatchProcess([string]$instanceName, [string]$key) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "dotnet"
    $psi.WorkingDirectory = $repoRoot
    $psi.Arguments = "run --project `"$sampleProject`" -- --backplane --instance=$instanceName --channel=$channel --cmd `"watch $key`""
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.Environment["REDIS_CONNECTION"] = $RedisConnection
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $null = $proc.Start()
    Write-Step -scenario "S1" -step "watch-start" -status "PASS" -instance $instanceName -command "watch $key" -message ("PID=" + $proc.Id)
    return $proc
}

try {
Write-Summary "[Scenario 1] Backplane propagation"
$null = Invoke-SampleCommand "A" "clear" "S1" "clear"
$null = Invoke-SampleCommand "A" "set k1 v1" "S1" "seed-v1"
$warm = Invoke-SampleCommand "B" "get k1" "S1" "warm-get"
if ($warm -notmatch "GET k1 => Name=v1") {
    Write-Step -scenario "S1" -step "warm-assert" -status "FAIL" -instance "B" -command "get k1" -message "Expected Name=v1 not found."
    throw "Warm-up failed on B. Output: $warm"
}
Write-Step -scenario "S1" -step "warm-assert" -status "PASS" -instance "B" -command "get k1" -message "Warm-up matched Name=v1."

$watchProc = Start-WatchProcess "B" "k1"
Start-Sleep -Milliseconds 800
$null = Invoke-SampleCommand "A" "set k1 v2" "S1" "update-v2"
Start-Sleep -Seconds $BackplaneTimeoutSeconds
if (-not $watchProc.HasExited) {
    try { $watchProc.Kill($true) } catch {}
}
$watchOutput = ($watchProc.StandardOutput.ReadToEnd() + "`n" + $watchProc.StandardError.ReadToEnd())
Set-Content -Path $watchRawPath -Value $watchOutput
if ($watchOutput -notmatch "WATCH k1 change => Name=v2") {
    Write-Step -scenario "S1" -step "watch-assert" -status "FAIL" -instance "B" -command "watch k1" -message "Expected v2 change not found in raw output."
    throw "Backplane propagation assertion failed. Watch output:`n$watchOutput"
}
Write-Step -scenario "S1" -step "watch-assert" -status "PASS" -instance "B" -command "watch k1" -message "Detected v2 propagation."
Write-Summary "PASS: Backplane propagation detected."

Write-Summary "[Scenario 2] Cross-process Add race"
$null = Invoke-SampleCommand "A" "clear" "S2" "clear"

$jobA = Start-Job -ScriptBlock {
    param($repoRoot, $sampleProject, $channel, $redisConnection)
    $env:REDIS_CONNECTION = $redisConnection
    dotnet run --project $sampleProject -- --backplane --instance=A --channel=$channel --cmd "add race-key fromA"
} -ArgumentList $repoRoot, $sampleProject, $channel, $RedisConnection

$jobB = Start-Job -ScriptBlock {
    param($repoRoot, $sampleProject, $channel, $redisConnection)
    $env:REDIS_CONNECTION = $redisConnection
    dotnet run --project $sampleProject -- --backplane --instance=B --channel=$channel --cmd "add race-key fromB"
} -ArgumentList $repoRoot, $sampleProject, $channel, $RedisConnection

$null = Wait-Job -Job $jobA, $jobB -Timeout 60
$outA = (Receive-Job -Job $jobA | Out-String)
$outB = (Receive-Job -Job $jobB | Out-String)
Remove-Job -Job $jobA, $jobB -Force
Set-Content -Path $raceAPath -Value $outA
Set-Content -Path $raceBPath -Value $outB

$aOk = $outA -match "ADD race-key=fromA => OK"
$bOk = $outB -match "ADD race-key=fromB => OK"
if (($aOk -and $bOk) -or (-not $aOk -and -not $bOk)) {
    Write-Step -scenario "S2" -step "race-assert" -status "FAIL" -message ("A_OK=" + $aOk + ", B_OK=" + $bOk)
    throw "Race assertion failed: expected exactly one winner. A_OK=$aOk, B_OK=$bOk`nA:`n$outA`nB:`n$outB"
}
Write-Step -scenario "S2" -step "race-assert" -status "PASS" -message ("Exactly one winner. A_OK=" + $aOk + ", B_OK=" + $bOk)

Write-Summary "PASS: Add race has exactly one winner."

Write-Summary "[Scenario 3] Degraded mode (ReadOnlyStale) integration check"
$old = $env:REDIS_CONNECTION
$env:REDIS_CONNECTION = $RedisConnection
try {
    $testOut = & dotnet test $testProject -c Release --filter "ReadOnlyStale_Mode_ShouldKeepServingCachedValue_WhenRedisIsDown" 2>&1 | Out-String
}
finally {
    $env:REDIS_CONNECTION = $old
}
Set-Content -Path $scenario3Path -Value $testOut

if ($testOut -notmatch "Passed!\s+-\s+Failed:\s+0,\s+Passed:\s+1") {
    Write-Step -scenario "S3" -step "dotnet-test" -status "FAIL" -message "ReadOnlyStale test did not pass."
    throw "ReadOnlyStale scenario failed.`nOutput:`n$testOut"
}
Write-Step -scenario "S3" -step "dotnet-test" -status "PASS" -message "ReadOnlyStale test passed."

Write-Summary "PASS: ReadOnlyStale degraded behavior validated."

Write-Summary "[Scenario 4] FailFast mode integration check"
$old2 = $env:REDIS_CONNECTION
$env:REDIS_CONNECTION = $RedisConnection
try {
    $testOut2 = & dotnet test $testProject -c Release --filter "RedisFailure_ShouldOpenCircuit_AndExposeHealthSnapshot" 2>&1 | Out-String
}
finally {
    $env:REDIS_CONNECTION = $old2
}
Set-Content -Path $scenario4Path -Value $testOut2

if ($testOut2 -notmatch "Passed!\s+-\s+Failed:\s+0,\s+Passed:\s+1") {
    Write-Step -scenario "S4" -step "dotnet-test" -status "FAIL" -message "FailFast test did not pass."
    throw "FailFast scenario failed.`nOutput:`n$testOut2"
}
Write-Step -scenario "S4" -step "dotnet-test" -status "PASS" -message "FailFast test passed."

Write-Summary "PASS: FailFast behavior validated."
Write-Summary "ALL E2E SCENARIOS PASSED."
Write-Summary "Artifacts:"
Write-Summary "  - $summaryPath"
Write-Summary "  - $stepsPath"
Write-Summary "  - $watchRawPath"
Write-Summary "  - $raceAPath"
Write-Summary "  - $raceBPath"
Write-Summary "  - $scenario3Path"
Write-Summary "  - $scenario4Path"
}
catch {
    Write-Step -scenario "GLOBAL" -step "run" -status "FAIL" -message $_.Exception.Message
    Write-Summary "FAILED: $($_.Exception.Message)"
    Write-Summary "See artifacts in: $resultsDir"
    throw
}
