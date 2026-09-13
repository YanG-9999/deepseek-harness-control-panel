# Builds the per-user installer for the control panel.
#
# The version is read from the C# source rather than passed in, so the executable's
# version resource and the installer can never drift apart.
#
# Requires Inno Setup 6 (ISCC.exe). Run:
#   powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1
param(
    [string]$SourceFile = ''
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourceFile)) {
    $SourceFile = Join-Path $projectRoot 'src\DeepSeekHarnessControlPanel.cs'
}
$iss = Join-Path $projectRoot 'installer\DeepSeekHarnessControlPanel.iss'
$appExe = Join-Path $projectRoot 'bin\DeepSeekHarnessControlPanel.exe'

if (-not (Test-Path -LiteralPath $SourceFile)) {
    throw "Source file not found: $SourceFile"
}
if (-not (Test-Path -LiteralPath $iss)) {
    throw "Installer script not found: $iss"
}

# Read the version from the single place it is declared.
$source = [System.IO.File]::ReadAllText($SourceFile, [System.Text.Encoding]::UTF8)
$match = [regex]::Match($source, 'public const string Version = "([^"]+)"')
if (-not $match.Success) {
    throw "Could not read PanelVersionPolicy.Version from $SourceFile"
}
$version = $match.Groups[1].Value
Write-Host "Panel version: $version"

# The installer packages an already-built executable, so make sure it exists.
if (-not (Test-Path -LiteralPath $appExe)) {
    Write-Host "Executable missing; building it first."
    & (Join-Path $PSScriptRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Locate ISCC. Missing tooling is reported as a clear instruction, not a stack trace.
$candidates = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe',
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$iscc = $null
foreach ($candidate in $candidates) {
    if (Test-Path -LiteralPath $candidate) { $iscc = $candidate; break }
}
if (-not $iscc) {
    $onPath = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($onPath) { $iscc = $onPath.Source }
}

if (-not $iscc) {
    Write-Host ''
    Write-Host 'Inno Setup 6 was not found, so no installer was produced.' -ForegroundColor Yellow
    Write-Host 'Everything else is ready: the installer script and the built executable.'
    Write-Host ''
    Write-Host 'Install it once (free), then re-run this script:'
    Write-Host '  https://jrsoftware.org/isdl.php'
    Write-Host ''
    Write-Host 'The script also looks in these locations:'
    foreach ($candidate in $candidates) { Write-Host "  $candidate" }
    exit 2
}

Write-Host "Using compiler: $iscc"
& $iscc "/DAppVersion=$version" $iss
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$expected = Join-Path $projectRoot ("bin\installer\DeepSeekHarnessControlPanel-$version-setup.exe")
if (-not (Test-Path -LiteralPath $expected)) {
    throw "The compiler reported success but no package was found at $expected"
}
$package = Get-Item -LiteralPath $expected
Write-Host ("Built: {0} ({1:N0} bytes)" -f $package.FullName, $package.Length)
