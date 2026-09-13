# Validates the installer script without needing Inno Setup installed.
#
# The point is the uninstall boundary. Removing the panel must never remove Harness or
# the user's data, and the only previously-existing safeguard was a comment. A comment
# cannot fail a build, so these checks fail instead.
#
# Run:
#   powershell -ExecutionPolicy Bypass -File .\scripts\verify-installer.ps1
param(
    [string]$IssFile = '',
    [string]$SourceFile = ''
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($IssFile)) {
    $IssFile = Join-Path $projectRoot 'installer\DeepSeekHarnessControlPanel.iss'
}
if ([string]::IsNullOrWhiteSpace($SourceFile)) {
    $SourceFile = Join-Path $projectRoot 'src\DeepSeekHarnessControlPanel.cs'
}

if (-not (Test-Path -LiteralPath $IssFile)) { throw "Installer script not found: $IssFile" }
if (-not (Test-Path -LiteralPath $SourceFile)) { throw "Source file not found: $SourceFile" }

$iss = [System.IO.File]::ReadAllText($IssFile, [System.Text.Encoding]::UTF8)
$source = [System.IO.File]::ReadAllText($SourceFile, [System.Text.Encoding]::UTF8)

# Comment stripping must drop whole comment lines only, and must cover both comment
# styles: ';' in the directive sections and '//' inside [Code]. Truncating at the first
# ';' anywhere on a line is wrong because values legitimately contain semicolons, and
# cutting there also removed the Flags that follow them.
#
# Splitting on LF leaves a trailing CR on CRLF files, which defeats line anchors. Split
# on both so the anchors behave the same on either line ending.
$issLines = $iss -split "\r?\n"
$issDirectives = ($issLines |
    Where-Object { $_.TrimStart() -notmatch '^(;|//)' } |
    Where-Object { $_.Trim().Length -gt 0 }) -join "`n"

# Bodies of the sections that decide what happens on the user's filesystem.
function Get-IssSection([string]$name) {
    $match = [regex]::Match($script:issDirectives, "(?s)\[" + [regex]::Escape($name) + "\](.*?)(\r?\n\[|\z)")
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}

$failures = New-Object System.Collections.Generic.List[string]
$checks = 0

function Assert-Iss([bool]$condition, [string]$message) {
    $script:checks++
    if (-not $condition) { $script:failures.Add($message) }
}

# --- per-user, no elevation -------------------------------------------------
Assert-Iss ($issDirectives -match '(?m)^\s*PrivilegesRequired\s*=\s*lowest\s*$') `
    'PrivilegesRequired must be lowest, or the installer asks for administrator rights.'
Assert-Iss ($issDirectives -match '(?m)^\s*DefaultDirName\s*=\s*\{localappdata\}') `
    'DefaultDirName must be under {localappdata} for a per-user install.'
Assert-Iss ($issDirectives -notmatch '(?m)^\s*DefaultDirName\s*=\s*\{pf\}') `
    'DefaultDirName must not be under Program Files: that would require elevation.'

# --- upgrade identity -------------------------------------------------------
# Inno treats a literal { at the start of AppId as a constant, so the value must be a
# GUID inside escaped braces. Checking for the GUID text itself is what matters:
# without it, an upgrade installs a second copy beside the first.
Assert-Iss ($issDirectives -match '(?m)^\s*AppId\s*=\s*\{') `
    'AppId must be set, otherwise an upgrade installs side by side.'
Assert-Iss ($issDirectives -match '#define\s+AppId\s+"\{\{[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}\}\}"') `
    'AppId must be an explicit GUID in escaped braces.'

# --- mutual exclusion with the running panel --------------------------------
# The name has to match the mutex the panel creates, or setup will overwrite a
# running executable instead of asking the user to close it.
Assert-Iss ($issDirectives -match 'AppMutex\s*=\s*Local\\DeepSeekHarnessControlPanel\.SingleInstance') `
    'AppMutex must match the panel''s single-instance mutex.'
Assert-Iss ($source -match 'MutexName\s*=\s*@"Local\\DeepSeekHarnessControlPanel\.SingleInstance"') `
    'The panel''s mutex name changed; update AppMutex in the installer script to match.'

# --- the uninstall boundary -------------------------------------------------
# No directive may name a Harness path. A single [UninstallDelete] line pointing at the
# install root or the Harness home would destroy API keys and conversation history when
# someone removes a few-hundred-kilobyte panel.
$forbidden = @(
    @{ Pattern = '\.dsh(?![-\w])';            Why = 'the Harness home (%USERPROFILE%\.dsh) holds API keys, sessions, and attachments' },
    @{ Pattern = '\.dsh-runtime';             Why = 'the private Node runtime belongs to the Harness install' },
    @{ Pattern = '\.dsh-manager-state\.json'; Why = 'the manager state file belongs to the Harness install' },
    @{ Pattern = 'deepseek-harness';          Why = 'the Harness source tree must be left alone' }
)
foreach ($rule in $forbidden) {
    $hit = [regex]::Match($issDirectives, $rule.Pattern)
    Assert-Iss (-not $hit.Success) `
        ("An installer directive references '{0}' ({1}); removing the panel must not touch it." -f $hit.Value, $rule.Why)
}

# Deletion must be limited to the app directory, and only when empty.
$deleteBody = Get-IssSection 'UninstallDelete'
Assert-Iss ($null -ne $deleteBody) 'An [UninstallDelete] section is required to clean up an empty app directory.'
if ($null -ne $deleteBody) {
    $deleteLines = ($deleteBody -split "`n") | Where-Object { $_ -match '^\s*Type:' }
    Assert-Iss ($deleteLines.Count -gt 0) '[UninstallDelete] must actually delete the empty app directory.'
    foreach ($line in $deleteLines) {
        Assert-Iss ($line -match 'dirifempty') `
            ("Only dirifempty deletions are allowed, found: {0}" -f $line.Trim())
        Assert-Iss ($line -notmatch 'filesandordirs|files') `
            ("A recursive delete in [UninstallDelete] is refused: {0}" -f $line.Trim())
    }
}

# --- auto-start stays opt-in ------------------------------------------------
$tasksBody = Get-IssSection 'Tasks'
Assert-Iss ($null -ne $tasksBody) 'A [Tasks] section is required for the optional auto-start.'
if ($null -ne $tasksBody) {
    Assert-Iss ($tasksBody -match 'Name:\s*"autostart"') 'The installer must offer an auto-start task.'
    Assert-Iss ($tasksBody -match 'Flags:.*unchecked') 'The auto-start task must be unchecked by default.'
}

$registryBody = Get-IssSection 'Registry'
Assert-Iss ($null -ne $registryBody) 'A [Registry] section is required for the opt-in auto-start entry.'
if ($null -ne $registryBody) {
    Assert-Iss ($registryBody -match 'HKCU;.*CurrentVersion\\Run') `
        'Auto-start must be written under HKCU, not HKLM.'
    Assert-Iss ($registryBody -notmatch 'HKLM') `
        'Nothing may be written to HKLM from a per-user installer.'
}

# --- deliverables -----------------------------------------------------------
Assert-Iss ($issDirectives -match '(?m)^\s*OutputDir\s*=') 'OutputDir must be set so the package lands in a known place.'
Assert-Iss ($issDirectives -match '\{uninstallexe\}') 'A Start Menu uninstall entry makes removal discoverable.'

Write-Host ("Installer script checks run: {0}" -f $checks)
if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host 'FAILED:' -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host ("  - {0}" -f $failure) -ForegroundColor Red }
    exit 1
}

Write-Host 'Installer script verified: per-user, no elevation, and the uninstall boundary holds.' -ForegroundColor Green
