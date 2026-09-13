# The running panel locks bin\DeepSeekHarnessControlPanel.exe, so a rebuild while it
# is open fails with CS1567. Pass -OutputDirectory to stage the binary elsewhere.
param(
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $projectRoot 'src\DeepSeekHarnessControlPanel.cs'
$icon = Join-Path $projectRoot 'assets\DeepSeekHarness.ico'
$manifest = Join-Path $projectRoot 'assets\DeepSeekHarness.manifest'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'bin'
}
$output = Join-Path $OutputDirectory 'DeepSeekHarnessControlPanel.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "The .NET Framework C# compiler was not found: $compiler"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
# The icon is embedded twice on purpose: /win32icon gives the executable its shell icon,
# and /resource gives the panel the multi-frame copy it draws the brand mark from. The
# associated-icon API only ever hands back the 32px frame, which is why the mark was soft.
& $compiler /nologo /target:winexe /out:$output /win32icon:$icon /win32manifest:$manifest `
    /resource:$icon,DeepSeekHarness.ico `
    /r:System.Windows.Forms.dll `
    /r:System.Drawing.dll `
    /r:System.Net.Http.dll `
    /r:System.Security.dll `
    /r:System.Web.Extensions.dll `
    /r:System.Management.dll `
    /r:System.IO.Compression.dll `
    /r:System.IO.Compression.FileSystem.dll `
    $source

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Built: $output"
