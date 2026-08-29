$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $projectRoot 'src\DeepSeekHarnessControlPanel.cs'
$icon = Join-Path $projectRoot 'assets\DeepSeekHarness.ico'
$outputDirectory = Join-Path $projectRoot 'bin'
$output = Join-Path $outputDirectory 'DeepSeekHarnessControlPanel.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "The .NET Framework C# compiler was not found: $compiler"
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
& $compiler /nologo /target:winexe /out:$output /win32icon:$icon `
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
