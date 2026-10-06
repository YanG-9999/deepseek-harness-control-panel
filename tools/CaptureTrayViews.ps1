# Captures the real tray popup and the balance dialog from the running panel.
# Run in-process (.\tools\CaptureTrayViews.ps1) so the calling host keeps its
# DPI awareness and screen coordinates stay physical. Development tool only.
param(
    [string]$OutputDirectory = 'bin-tray-balance'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace TrayShot -Name Api -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, int message, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr param);
[DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr param);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int count);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
public delegate bool EnumProc(IntPtr window, IntPtr param);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

function Get-Text([IntPtr]$Window) {
    $buffer = New-Object System.Text.StringBuilder 512
    [TrayShot.Api]::GetWindowText($Window, $buffer, 512) | Out-Null
    return $buffer.ToString()
}

function Get-Class([IntPtr]$Window) {
    $buffer = New-Object System.Text.StringBuilder 256
    [TrayShot.Api]::GetClassName($Window, $buffer, 256) | Out-Null
    return $buffer.ToString()
}

function Find-Window([string]$Title) {
    $script:hit = [IntPtr]::Zero
    $callback = [TrayShot.Api+EnumProc]{
        param($window, $param)
        if ((Get-Class $window).StartsWith('WindowsForms10.Window') -and (Get-Text $window) -eq $Title) {
            $script:hit = $window
            return $false
        }
        return $true
    }
    [TrayShot.Api]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    return $script:hit
}

function Save-Window([IntPtr]$Window, [string]$Path) {
    $rect = New-Object TrayShot.Api+RECT
    [TrayShot.Api]::GetWindowRect($Window, [ref]$rect) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $image = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($image)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $image.Size)
    $graphics.Dispose()
    $image.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $image.Dispose()
    return "$width x $height at $($rect.Left),$($rect.Top)"
}

$panel = Find-Window 'DeepSeek Harness 控制面板'
if ($panel -eq [IntPtr]::Zero) { throw 'The panel window was not found.' }

$message = [TrayShot.Api]::RegisterWindowMessage('DeepSeekHarnessControlPanel.ShowTrayPanel')
[TrayShot.Api]::PostMessage($panel, $message, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 1500
Write-Host ("popup visible=" + [TrayShot.Api]::IsWindowVisible($panel) + " " +
    (Save-Window $panel (Join-Path $OutputDirectory 'popup-live.png')))

$panelRect = New-Object TrayShot.Api+RECT
[TrayShot.Api]::GetWindowRect($panel, [ref]$panelRect) | Out-Null
$script:settings = [IntPtr]::Zero
$buttonCallback = [TrayShot.Api+EnumProc]{
    param($window, $param)
    if ((Get-Class $window) -like 'WindowsForms10.BUTTON*' -and (Get-Text $window) -eq '') {
        $rect = New-Object TrayShot.Api+RECT
        [TrayShot.Api]::GetWindowRect($window, [ref]$rect) | Out-Null
        if ($rect.Top -lt ($panelRect.Top + 120) -and $rect.Left -gt ($panelRect.Left + 600)) {
            $script:settings = $window
            return $false
        }
    }
    return $true
}
[TrayShot.Api]::EnumChildWindows($panel, $buttonCallback, [IntPtr]::Zero) | Out-Null
if ($script:settings -eq [IntPtr]::Zero) { throw 'The settings button was not found.' }

[TrayShot.Api]::PostMessage($script:settings, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 1500
$dialog = Find-Window 'DeepSeek 余额设置'
if ($dialog -eq [IntPtr]::Zero) { throw 'The balance dialog did not open.' }
Write-Host ("dialog visible=" + [TrayShot.Api]::IsWindowVisible($dialog) + " " +
    (Save-Window $dialog (Join-Path $OutputDirectory 'settings-live.png')))
[TrayShot.Api]::PostMessage($dialog, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 400
Write-Host ("popup still visible after dialog=" + [TrayShot.Api]::IsWindowVisible($panel))
