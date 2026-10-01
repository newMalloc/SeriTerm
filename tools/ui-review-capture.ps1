# 界面评审用：启动 SeriTerm，批量截取「浅色/深色 × 默认尺寸/加高 × 顶部/底部」，
# 并可选地打开 COM5 回环发几行数据，让日志区不是空白。
# 用法：powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ui-review-capture.ps1 -ExePath <exe> -OutDir <dir> [-WithData] [-Port COM5] [-Baud 115200]
#
# 两个坑：
#   1) 本脚本 DPI 感知，SetWindowPos 收的是物理像素；本机 150% 缩放，
#      「1280 DIP 宽」要写 1920 物理像素。
#   2) SeriTerm 会在退出时把当前窗口尺寸写回 settings.json，
#      所以脚本结束前必须把尺寸恢复原样，否则会把用户的窗口大小改掉。
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [switch]$WithData,
    [string]$Port = 'COM5',
    [int]$Baud = 115200
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) { throw "找不到可执行文件：$ExePath" }
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

if (-not ('UiShot' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class UiShot
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int awareness);
}
'@
}

[void][UiShot]::SetProcessDPIAware()
try { [void][UiShot]::SetProcessDpiAwareness(2) } catch { }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Save-Shot {
    param([IntPtr]$Handle, [string]$Name)
    $rect = New-Object UiShot+RECT
    $ok = [UiShot]::GetWindowRect($Handle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    Write-Output "RECT|$Name|ok=$ok|$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom)|exited=$($process.HasExited)"
    if ($width -le 0 -or $height -le 0) { throw "截图失败：窗口矩形无效 ($width x $height)" }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try { [void][UiShot]::PrintWindow($Handle, $hdc, 2) }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    $bitmap.Save((Join-Path $OutDir "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Output "SHOT|$Name|${width}x${height}"
}

function Find-ById {
    param($Root, [string]$Id)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $Id)
    return $Root.FindFirst($TS::Descendants, $cond)
}

function Find-ByType {
    param($Root, $ControlType)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ControlType)
    return $Root.FindAll($TS::Descendants, $cond)
}

function Find-Button {
    # MinWidth 用来区分同名按钮：标题栏的「关闭」只有 44 宽，
    # 而「打开/关闭串口」这个主按钮是横向拉伸的（>100）。
    param($Root, [string]$Pattern, [double]$MinWidth = 0)
    foreach ($b in (Find-ByType -Root $Root -ControlType ([System.Windows.Automation.ControlType]::Button))) {
        if ($b.Current.Name -match $Pattern -and $b.Current.BoundingRectangle.Width -ge $MinWidth) { return $b }
    }
    return $null
}

function Invoke-Element {
    param($Element)
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Scroll-IntoView {
    param($Element)
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.ScrollIntoView()
    } else {
        $Element.SetFocus()
    }
}

$process = Start-Process -FilePath $ExePath -PassThru
$originalSize = $null
$handle = [IntPtr]::Zero
try {
    Start-Sleep -Seconds 6
    $process.Refresh()
    $handle = $process.MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw '主窗口尚未创建' }

    $root = $AE::FromHandle($handle)
    [void][UiShot]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 500

    $rect = New-Object UiShot+RECT
    [void][UiShot]::GetWindowRect($handle, [ref]$rect)
    $originalSize = @{ W = $rect.Right - $rect.Left; H = $rect.Bottom - $rect.Top }
    Write-Output ("ORIGINAL|{0}x{1}" -f $originalSize.W, $originalSize.H)

    # --- 主题按钮文案就是当前状态的镜像："切换到深色" 表示现在是浅色 ---
    $themeButton = Find-Button -Root $root -Pattern '深色|浅色|主题'
    if (-not $themeButton) { throw '找不到主题切换按钮' }
    $themeNow = if ($themeButton.Current.Name -match '深色') { 'Light' } else { 'Dark' }
    Write-Output "THEME|启动时=$themeNow"
    if ($themeNow -ne 'Light') {
        Invoke-Element $themeButton
        Start-Sleep -Milliseconds 1200
        $themeButton = Find-Button -Root $root -Pattern '深色|浅色|主题'
    }

    # --- 可选：打开串口并回环发几行，让日志区有内容 ---
    if ($WithData) {
        $openButton = Find-Button -Root $root -Pattern '^(打开|关闭)串口$' -MinWidth 100
        if ($openButton) {
            Invoke-Element $openButton
            Start-Sleep -Milliseconds 1500
            Write-Output 'DATA|已点击打开串口'
        } else {
            Write-Output 'WARN|找不到打开串口按钮'
        }

        $sendTextBox = Find-ById -Root $root -Id 'SendTextBox'
        $lines = @(
            "AT+VERSION$([char]13)$([char]10)",
            "boot ok, baud $Baud$([char]13)$([char]10)",
            "temp=26.5C hum=48%$([char]13)$([char]10)",
            "ERR: checksum mismatch at 0x1F40$([char]13)$([char]10)"
        )
        foreach ($line in $lines) {
            if ($sendTextBox) {
                $sendTextBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($line)
            }
            $sendButton = Find-Button -Root $root -Pattern '^发送$'
            if ($sendButton) { Invoke-Element $sendButton }
            Start-Sleep -Milliseconds 600
        }
        Start-Sleep -Milliseconds 800
        Write-Output 'DATA|已回环发送 4 行'
    }

    # ================= 浅色 =================
    [void][UiShot]::SetWindowPos($handle, [IntPtr]::Zero, 10, 10, 1920, 1200, 0x0004)
    Start-Sleep -Milliseconds 900
    Save-Shot -Handle $handle -Name '01-light-default'

    [void][UiShot]::SetWindowPos($handle, [IntPtr]::Zero, 10, 10, 1920, 1500, 0x0004)
    Start-Sleep -Milliseconds 900
    Save-Shot -Handle $handle -Name '02-light-tall-top'

    $scrollTarget = Find-ById -Root $root -Id 'SendTextBox'
    if ($scrollTarget) { Scroll-IntoView $scrollTarget; Start-Sleep -Milliseconds 800 }
    Save-Shot -Handle $handle -Name '03-light-tall-bottom'

    # ================= 深色 =================
    Invoke-Element $themeButton
    Start-Sleep -Milliseconds 1500
    Save-Shot -Handle $handle -Name '04-dark-tall-bottom'

    $scrollTarget = Find-ById -Root $root -Id 'PresetCombo'
    if ($scrollTarget) { Scroll-IntoView $scrollTarget; Start-Sleep -Milliseconds 800 }
    Save-Shot -Handle $handle -Name '05-dark-tall-top'

    [void][UiShot]::SetWindowPos($handle, [IntPtr]::Zero, 10, 10, 1920, 1200, 0x0004)
    Start-Sleep -Milliseconds 900
    Save-Shot -Handle $handle -Name '06-dark-default'

    # --- 还原主题（保持用户原来的设置） ---
    if ($themeNow -ne 'Dark') {
        $themeButton = Find-Button -Root $root -Pattern '深色|浅色|主题'
        Invoke-Element $themeButton
        Start-Sleep -Milliseconds 1200
        Write-Output 'THEME|已还原为浅色'
    }
}
finally {
    # 关键：退出前把窗口尺寸恢复原样，否则 SeriTerm 会把评审用的尺寸写回 settings.json
    if ((-not $process.HasExited) -and $originalSize -and $handle -ne [IntPtr]::Zero) {
        [void][UiShot]::SetWindowPos($handle, [IntPtr]::Zero, 10, 10, $originalSize.W, $originalSize.H, 0x0004)
        Start-Sleep -Milliseconds 600
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    } elseif (-not $process.HasExited) {
        $process.Kill()
    }
}
