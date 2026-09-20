[CmdletBinding()]
param(
    [ValidateSet('Marketplace', 'All')][string]$Suite = 'Marketplace',
    [switch]$Background,
    [switch]$Worker,
    [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunId = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$resultsRoot = Join-Path $repoRoot 'artifacts\tests'
if (!$RunId) { $RunId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8) }
$runDirectory = Join-Path $resultsRoot $RunId
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$statusPath = Join-Path $runDirectory 'status.json'

if (!$Worker) {
    Set-Content -LiteralPath (Join-Path $resultsRoot 'latest.txt') -Value $runDirectory -Encoding UTF8
}
if ($Background) {
    if ($Worker) { throw 'Background and Worker cannot be combined.' }
    @{ state = 'starting'; suite = $Suite; runId = $RunId } | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
    $shell = (Get-Process -Id $PID).Path
    $arguments = @('-NoProfile', '-NonInteractive', '-File', ('"' + $PSCommandPath + '"'), '-Suite', $Suite, '-RunId', $RunId, '-Worker')
    $process = Start-Process -FilePath $shell -ArgumentList $arguments -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $runDirectory 'worker.log') -RedirectStandardError (Join-Path $runDirectory 'worker-error.log')
    Write-Output "Started test process $($process.Id). Results: $runDirectory"
    return
}

$status = [ordered]@{
    state = 'running'; suite = $Suite; runId = $RunId; processId = $PID
    startedUtc = [DateTime]::UtcNow.ToString('O'); finishedUtc = $null
    dotnetExitCode = $null; javascriptExitCode = $null; passed = $null; failed = $null; error = $null
}
function Save-Status {
    $temporary = Join-Path $runDirectory 'status.tmp'
    $status | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $statusPath -Force
}

$runLock = $null
$exitCode = 1
Push-Location $repoRoot
try {
    # Prevent concurrent builds/test runs started with this runner.
    $runLock = [System.IO.File]::Open((Join-Path $resultsRoot 'run.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    Save-Status
    $testArguments = @('test', 'tests/WDWAPP.Tests/WDWAPP.Tests.csproj', '-c', 'Release', '-p:UseSharedCompilation=false',
        '--logger', 'trx;LogFileName=dotnet.trx', '--results-directory', $runDirectory, '--verbosity', 'minimal')
    if ($Suite -eq 'Marketplace') { $testArguments += @('--filter', 'FullyQualifiedName~AdvertisementTests') }
    & dotnet @testArguments *> (Join-Path $runDirectory 'dotnet.log')
    $status.dotnetExitCode = $LASTEXITCODE
    $trxPath = Join-Path $runDirectory 'dotnet.trx'
    if (Test-Path -LiteralPath $trxPath) {
        [xml]$trx = Get-Content -Raw -LiteralPath $trxPath
        $counters = $trx.SelectSingleNode('//*[local-name()="Counters"]')
        if ($counters) { $status.passed = [int]$counters.passed; $status.failed = [int]$counters.failed }
    }
    Save-Status
    & node --test tests/marketplace.test.mjs *> (Join-Path $runDirectory 'javascript.log')
    $status.javascriptExitCode = $LASTEXITCODE
    $exitCode = if ($status.dotnetExitCode -eq 0 -and $status.javascriptExitCode -eq 0) { 0 } else { 1 }
    $status.state = if ($exitCode -eq 0) { 'passed' } else { 'failed' }
}
catch {
    $status.state = 'failed'
    $status.error = $_.Exception.Message
}
finally {
    $status.finishedUtc = [DateTime]::UtcNow.ToString('O')
    Save-Status
    if ($runLock) { $runLock.Dispose() }
    Pop-Location
}
Write-Output "$($status.state): .NET passed=$($status.passed), failed=$($status.failed); JavaScript exit=$($status.javascriptExitCode). Results: $runDirectory"
exit $exitCode
