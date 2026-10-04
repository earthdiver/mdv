param([string]$CacheDirectory = (Join-Path $PSScriptRoot '../artifacts/cache'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$manifest = Get-Content (Join-Path $PSScriptRoot 'webview2-runtime.json') -Raw | ConvertFrom-Json
$cache = [System.IO.Path]::GetFullPath($CacheDirectory)
$cab = Join-Path $cache 'webview2-x64.cab'
$unpacked = Join-Path $cache 'webview2'
$runtime = Join-Path $unpacked "Microsoft.WebView2.FixedVersionRuntime.$($manifest.version).x64"
$stamp = Join-Path $runtime '.mdv-package-sha256'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
if (-not (Test-Path $cab)) {
    Write-Host "Downloading Microsoft WebView2 Fixed Version $($manifest.version)..."
    $download = "$cab.download"
    try {
        Invoke-WebRequest -Uri $manifest.url -OutFile $download -UseBasicParsing
        Move-Item -LiteralPath $download -Destination $cab -Force
    }
    finally { if (Test-Path $download) { Remove-Item -LiteralPath $download -Force } }
}
if ((Get-FileHash -LiteralPath $cab -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.sha256) {
    throw "WebView2 archive checksum mismatch. Delete $cab and retry."
}
if (-not (Test-Path $stamp) -or (Get-Content $stamp -Raw).Trim() -ne $manifest.sha256) {
    if (Test-Path $runtime) { Remove-Item -LiteralPath $runtime -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $unpacked | Out-Null
    & expand.exe '-F:*' $cab $unpacked | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "expand.exe failed: $LASTEXITCODE" }
    if (-not (Test-Path (Join-Path $runtime 'msedgewebview2.exe'))) { throw 'Fixed Version runtime was not extracted.' }
    Set-Content -Path $stamp -Value $manifest.sha256 -Encoding ASCII
}
Write-Output $runtime
