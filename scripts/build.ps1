param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [string]$FixedRuntimeDirectory,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    function Invoke-Checked([string]$Command, [string[]]$Arguments) {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
    }
    Invoke-Checked npm @('ci')
    if (-not $SkipTests) {
        Invoke-Checked npm @('test')
        Invoke-Checked dotnet @('run', '--project', 'tests/Mdv.Core.Tests', '-c', 'Release')
    }
    Invoke-Checked npm @('run', 'build')
    if (-not $FixedRuntimeDirectory) {
        if ($Runtime -ne 'win-x64') { throw 'For ARM64, specify -FixedRuntimeDirectory with the official ARM64 Fixed Version runtime.' }
        $FixedRuntimeDirectory = & (Join-Path $PSScriptRoot 'fetch-webview2.ps1')
    }
    $fixedRuntime = (Resolve-Path $FixedRuntimeDirectory).Path
    $browser = Join-Path $fixedRuntime 'msedgewebview2.exe'
    if (-not (Test-Path $browser)) { throw "Missing $browser" }
    $stream = [System.IO.File]::OpenRead($browser)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        $stream.Position = 0x3c; $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset + 4; $machine = $reader.ReadUInt16()
        $expectedMachine = if ($Runtime -eq 'win-x64') { 0x8664 } else { 0xaa64 }
        if ($machine -ne $expectedMachine) { throw 'WebView2 runtime architecture does not match the application.' }
    }
    finally { $reader.Dispose(); $stream.Dispose() }
    Invoke-Checked dotnet @('restore', 'src/Mdv.App/Mdv.App.csproj', '-r', $Runtime, '-p:PortableBuild=true')
    $payload = Join-Path (Get-Location) "artifacts/payload-$Runtime"
    if (Test-Path $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
    New-Item -ItemType Directory -Path $payload | Out-Null
    Copy-Item src/Mdv.App/Renderer -Destination (Join-Path $payload 'Renderer') -Recurse
    Copy-Item samples -Destination (Join-Path $payload 'Samples') -Recurse
    Copy-Item $fixedRuntime -Destination (Join-Path $payload 'WebView2Runtime') -Recurse
    Copy-Item README.md, LICENSE -Destination $payload
    Copy-Item docs -Destination $payload -Recurse
    Invoke-Checked node @('scripts/collect-native-licenses.mjs', $payload, $Runtime)
    Invoke-Checked dotnet @('run', '--project', 'tools/Mdv.Pack', '-c', 'Release', '--', $payload, 'src/Mdv.App/PortablePayload/payload.br')
    $output = Join-Path (Get-Location) "artifacts/MDV-portable-$Runtime"
    if (Test-Path $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    Invoke-Checked dotnet @('publish', 'src/Mdv.App/Mdv.App.csproj', '-c', 'Release', '-r', $Runtime, '--self-contained', 'true', '-p:PortableBuild=true', '--no-restore', '-o', $output)
    # Debug symbols and SDK IntelliSense XML are build artifacts, not runtime dependencies.
    Get-ChildItem -LiteralPath $output -Filter '*.pdb' -File | Remove-Item -Force
    Get-ChildItem -LiteralPath $output -Filter 'Microsoft.Web.WebView2.*.xml' -File | Remove-Item -Force
    $files = @(Get-ChildItem -LiteralPath $output -Recurse -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'MDV.exe') { throw 'Portable output must contain exactly one MDV.exe.' }
    $checksum = (Get-FileHash (Join-Path $output 'MDV.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path (Join-Path (Split-Path $output -Parent) "MDV-portable-$Runtime.sha256") -Value "$checksum  MDV.exe" -Encoding ASCII
    Write-Host "Built: $output/MDV.exe"
    Write-Host 'Distribute MDV.exe alone. MDV.ini is created beside it on first launch.'
}
finally { Pop-Location }
