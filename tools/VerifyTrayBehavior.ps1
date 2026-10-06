# End-to-end behaviour check for the tray popup, driven through window messages so
# it exercises the real process: show, close-to-tray, reopen, open the settings
# dialog, and exit. Development tool only.
param(
    [string]$OutputDirectory = 'bin-tray-balance'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace TrayProbe -Name Api -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, int message, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr param);
[DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr param);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int count);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
[DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr window);
public delegate bool EnumProc(IntPtr window, IntPtr param);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

function Get-Text([IntPtr]$Window) {
    $buffer = New-Object System.Text.StringBuilder 512
    [TrayProbe.Api]::GetWindowText($Window, $buffer, 512) | Out-Null
    return $buffer.ToString()
}

function Get-Class([IntPtr]$Window) {
    $buffer = New-Object System.Text.StringBuilder 256
    [TrayProbe.Api]::GetClassName($Window, $buffer, 256) | Out-Null
    return $buffer.ToString()
}

function Find-PanelWindow {
    $script:panel = [IntPtr]::Zero
    $callback = [TrayProbe.Api+EnumProc]{
        param($window, $param)
        if ((Get-Class $window).StartsWith('WindowsForms10.Window') -and (Get-Text $window) -eq 'DeepSeek Harness 控制面板') {
            $script:panel = $window
            return $false
        }
        return $true
    }
    [TrayProbe.Api]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    if ($script:panel -eq [IntPtr]::Zero) { throw 'The panel window was not found.' }
    return $script:panel
}

function Get-Buttons([IntPtr]$Parent) {
    $script:buttons = @()
    $callback = [TrayProbe.Api+EnumProc]{
        param($window, $param)
        if ((Get-Class $window) -like 'WindowsForms10.BUTTON*') {
            $rect = New-Object TrayProbe.Api+RECT
            [TrayProbe.Api]::GetWindowRect($window, [ref]$rect) | Out-Null
            $script:buttons += [pscustomobject]@{
                Handle = $window
                Text = (Get-Text $window)
                Enabled = [TrayProbe.Api]::IsWindowEnabled($window)
                Left = $rect.Left
                Top = $rect.Top
                Width = $rect.Right - $rect.Left
            }
        }
        return $true
    }
    [TrayProbe.Api]::EnumChildWindows($Parent, $callback, [IntPtr]::Zero) | Out-Null
    return $script:buttons
}

function Save-Rect([IntPtr]$Window, [string]$Path) {
    $rect = New-Object TrayProbe.Api+RECT
    [TrayProbe.Api]::GetWindowRect($Window, [ref]$rect) | Out-Null
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

$panel = Find-PanelWindow
$message = [TrayProbe.Api]::RegisterWindowMessage('DeepSeekHarnessControlPanel.ShowTrayPanel')
[TrayProbe.Api]::PostMessage($panel, $message, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 1200
Write-Host ("show -> visible=" + [TrayProbe.Api]::IsWindowVisible($panel))
Write-Host ("popup " + (Save-Rect $panel (Join-Path $OutputDirectory 'behaviour-popup.png')))

$buttons = Get-Buttons $panel
foreach ($button in $buttons) { Write-Host ("button '" + $button.Text + "' enabled=" + $button.Enabled) }

$panelRect = New-Object TrayProbe.Api+RECT
[TrayProbe.Api]::GetWindowRect($panel, [ref]$panelRect) | Out-Null
# The gear and the close mark are painted icon buttons, so they carry no window
# text: the gear is the empty-text button in the top-right of the header row.
$settings = $buttons | Where-Object {
    $_.Text -eq '' -and $_.Top -lt ($panelRect.Top + 90) -and $_.Left -gt ($panelRect.Left + ($panelRect.Right - $panelRect.Left) * 0.6)
} | Sort-Object Left | Select-Object -First 1
if ($settings) {
    [TrayProbe.Api]::PostMessage($settings.Handle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 1200
    $script:dialog = [IntPtr]::Zero
    $dialogCallback = [TrayProbe.Api+EnumProc]{
        param($window, $param)
        if ((Get-Text $window) -eq 'DeepSeek 余额设置') { $script:dialog = $window; return $false }
        return $true
    }
    [TrayProbe.Api]::EnumWindows($dialogCallback, [IntPtr]::Zero) | Out-Null
    Write-Host ("settings dialog shown=" + ($script:dialog -ne [IntPtr]::Zero))
    if ($script:dialog -ne [IntPtr]::Zero) {
        Write-Host ("dialog " + (Save-Rect $script:dialog (Join-Path $OutputDirectory 'behaviour-settings.png')))
        Write-Host ("popup still visible behind dialog=" + [TrayProbe.Api]::IsWindowVisible($panel))
        [TrayProbe.Api]::PostMessage($script:dialog, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 700
    }
}

# The title-bar X and Alt+F4 both arrive as WM_SYSCOMMAND/SC_CLOSE; a bare
# WM_CLOSE is what the task manager sends and is expected to exit instead.
[TrayProbe.Api]::PostMessage($panel, 0x0112, [IntPtr]0xF060, [IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 900
$process = Get-Process DeepSeekHarnessControlPanel -ErrorAction SilentlyContinue
Write-Host ("after close -> visible=" + [TrayProbe.Api]::IsWindowVisible($panel) + " processAlive=" + ($null -ne $process))

[TrayProbe.Api]::PostMessage($panel, $message, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 1200
Write-Host ("reopen -> visible=" + [TrayProbe.Api]::IsWindowVisible($panel))

$buttons = Get-Buttons $panel
$exit = $buttons | Where-Object { $_.Text -eq '退出' } | Select-Object -First 1
if ($exit) {
    [TrayProbe.Api]::PostMessage($exit.Handle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 1200
    Write-Host ("after exit click -> processAlive=" + ($null -ne (Get-Process DeepSeekHarnessControlPanel -ErrorAction SilentlyContinue)))
}
