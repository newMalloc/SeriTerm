# 量一下界面元素的真实矩形，用来判断"哪一块被挤出可视区"。
# 用法：powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/probe-layout.ps1 -ExePath <exe> [-Width 1920] [-Height 1200]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [int]$Width = 1920,
    [int]$Height = 1200
)

$ErrorActionPreference = 'Stop'

if (-not ('LayoutProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LayoutProbe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int awareness);
}
'@
}

[void][LayoutProbe]::SetProcessDPIAware()
try { [void][LayoutProbe]::SetProcessDpiAwareness(2) } catch { }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Get-Rect {
    param([string]$Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    $element = $AE::RootElement.FindFirst($TS::Descendants, $cond)
    if (-not $element) { return $null }
    return $element.Current.BoundingRectangle
}

$process = Start-Process -FilePath $ExePath -PassThru
$originalSize = $null
$handle = [IntPtr]::Zero
try {
    Start-Sleep -Seconds 6
    $process.Refresh()
    $handle = $process.MainWindowHandle

    $rect = New-Object LayoutProbe+RECT
    [void][LayoutProbe]::GetWindowRect($handle, [ref]$rect)
    $originalSize = @{ W = $rect.Right - $rect.Left; H = $rect.Bottom - $rect.Top }

    [void][LayoutProbe]::SetWindowPos($handle, [IntPtr]::Zero, 10, 10, $Width, $Height, 0x0004)
    Start-Sleep -Milliseconds 1200

    [void][LayoutProbe]::GetWindowRect($handle, [ref]$rect)
    Write-Output ("WINDOW|{0},{1},{2},{3}|{4}x{5}" -f $rect.Left, $rect.Top, $rect.Right, $rect.Bottom, ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))

    $names = @(
        '端口设置', '接收设置', '日志保存',
        '十六进制显示', '同时保存原始字节（可重放）',
        '关闭串口', '打开串口',
        '查找', '暂停显示', '自动换行', '自动滚动', '字号', '保存', '清空',
        '终端模式', '十六进制发送', '行尾:', '定时:', '定时发送', '发送文件', '发送',
        '已打开', 'Rx:'
    )
    foreach ($name in $names) {
        $r = Get-Rect -Name $name
        if ($r) {
            Write-Output ("EL|{0}|{1:0},{2:0},{3:0},{4:0}" -f $name, $r.Left, $r.Top, $r.Right, $r.Bottom)
        } else {
            Write-Output ("EL|{0}|NOT-FOUND" -f $name)
        }
    }

    # 日志目录那条：文本会被省略号截断，取一下实际宽度
    $pathCond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'SendTextBox')
    $sendBox = $AE::RootElement.FindFirst($TS::Descendants, $pathCond)
    if ($sendBox) {
        $r = $sendBox.Current.BoundingRectangle
        Write-Output ("EL|SendTextBox|{0:0},{1:0},{2:0},{3:0}" -f $r.Left, $r.Top, $r.Right, $r.Bottom)
    }
}
finally {
    if ((-not $process.HasExited) -and $originalSize) {
        [void][LayoutProbe]::SetWindowPos($handle, [IntPtr]::Zero, 10, 10, $originalSize.W, $originalSize.H, 0x0004)
        Start-Sleep -Milliseconds 500
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    } elseif (-not $process.HasExited) {
        $process.Kill()
    }
}
