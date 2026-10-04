param(
    [string]$BuildDirectory = (Join-Path $PSScriptRoot '../artifacts/MDV-portable-win-x64'),
    [string]$Report = (Join-Path $PSScriptRoot '../artifacts/native-smoke.json')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = (Resolve-Path $BuildDirectory).Path
$reportPath = [System.IO.Path]::GetFullPath($Report)
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('mdv-portable-smoke-' + [guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $staging 'app with spaces'
$logs = Join-Path $staging 'reports'
$working = Join-Path $staging 'different working directory'
New-Item -ItemType Directory -Path $appDirectory, $logs, $working | Out-Null
function Invoke-Portable([string]$Exe, [string]$Output, [switch]$Restart, [switch]$ExpectFailure) {
    $arguments = @('--smoke-test', ('"' + $Output + '"'))
    if ($Restart) { $arguments += '--smoke-restart' }
    $stderr = "$Output.stderr.log"
    $process = Start-Process -FilePath $Exe -ArgumentList $arguments -WorkingDirectory $working -RedirectStandardError $stderr -PassThru
    $processHandle = $process.Handle
    if (-not $process.WaitForExit(90000)) { $process.Kill(); throw 'Portable test timed out.' }
    if (-not (Test-Path $Output)) { Get-Content $stderr; throw "No report produced. Exit code: $($process.ExitCode)" }
    $result = Get-Content -LiteralPath $Output -Raw | ConvertFrom-Json
    if ($ExpectFailure) {
        if ($process.ExitCode -eq 0 -or $result.success) { throw 'Read-only INI should have been reported as an error.' }
    }
    else {
        if ($process.ExitCode -ne 0 -or -not $result.success) { Get-Content $Output; Get-Content $stderr; throw 'Portable test failed.' }
        if (Test-Path $result.userData) { throw 'Temporary WebView2 user data was not removed after exit.' }
    }
    return $result
}
try {
    $files = @(Get-ChildItem -LiteralPath $source -Recurse -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'MDV.exe') { throw 'Distribution must contain exactly one MDV.exe.' }
    # Copy only the executable. No DLLs, assets, INI, or runtime are supplied.
    $exe = Join-Path $appDirectory 'MDV.exe'
    Copy-Item -LiteralPath (Join-Path $source 'MDV.exe') -Destination $exe
    $legacyIni = Join-Path $env:LOCALAPPDATA 'MDV/settings.json'
    $legacyBefore = if (Test-Path $legacyIni) { (Get-FileHash $legacyIni).Hash } else { '' }
    $localReport = Join-Path $logs 'native-smoke.json'
    $first = Invoke-Portable $exe $localReport
    $ini = Join-Path $appDirectory 'MDV.ini'
    if (-not (Test-Path $ini) -or (Get-Content $ini -Raw) -notmatch '\[Viewer\]') { throw 'INI was not generated.' }
    if (@(Get-ChildItem $appDirectory -File).Count -ne 2 -or @(Get-ChildItem $appDirectory -Directory).Count -ne 0) { throw 'App directory must contain only EXE and INI.' }
    if (Test-Path (Join-Path $working 'MDV.ini')) { throw 'INI was incorrectly written to the working directory.' }
    $moved = Join-Path $staging 'relocated app'
    Move-Item -LiteralPath $appDirectory -Destination $moved
    Rename-Item -LiteralPath (Join-Path $moved 'MDV.exe') -NewName 'Reader.exe'
    Rename-Item -LiteralPath (Join-Path $moved 'MDV.ini') -NewName 'Reader.ini'
    $restartReport = Join-Path $logs 'portable-restart.json'
    $restart = Invoke-Portable (Join-Path $moved 'Reader.exe') $restartReport -Restart
    if ($first.assets -ne $restart.assets) { throw 'The payload cache was not reused after relocation.' }
    $readonlyIni = Get-Item (Join-Path $moved 'Reader.ini')
    $readonlyIni.IsReadOnly = $true
    $readonlyReport = Join-Path $logs 'portable-readonly.json'
    try { $readonly = Invoke-Portable (Join-Path $moved 'Reader.exe') $readonlyReport -Restart -ExpectFailure }
    finally { $readonlyIni.IsReadOnly = $false }
    $legacyAfter = if (Test-Path $legacyIni) { (Get-FileHash $legacyIni).Hash } else { '' }
    if ($legacyBefore -ne $legacyAfter) { throw 'Legacy AppData settings were changed.' }
    Write-Host 'PASS: single EXE, fixed runtime, INI, relocation, rename, restart, profile cleanup, read-only errors, legacy settings unchanged.'
}
finally {
    New-Item -ItemType Directory -Force -Path (Split-Path $reportPath -Parent) | Out-Null
    foreach ($file in Get-ChildItem -Path $logs -File -ErrorAction SilentlyContinue) {
        $destination = if ($file.Name.StartsWith('native-smoke.')) { [System.IO.Path]::ChangeExtension($reportPath, $file.Extension) } else { Join-Path (Split-Path $reportPath -Parent) $file.Name }
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}
