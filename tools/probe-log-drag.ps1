# 日志区选择与复制回归探针：真实鼠标拖选，并比较 UIA 选中文本和剪贴板。
#
# 单击只移动光标。跨行和行内拖动都按字符连续选择。
#
# 需要真实串口（用发送按钮灌数据），所以要求 COM5 空闲：跑之前先关掉正在运行的 SeriTerm。
# 不需要串口的同类验证是 C# 探针（直接往 LogDocument 灌行 + 真实鼠标），见开发规格 11.30。
#
# 用法：powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/probe-log-drag.ps1 -ExePath <exe> [-Port COM5]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$Port = 'COM5'
)

$ErrorActionPreference = 'Stop'

if (-not ('DragProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class DragProbe
{
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int awareness);

    public static void LeftDown() { mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); }
    public static void LeftUp() { mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); }
}
'@
}

[void][DragProbe]::SetProcessDPIAware()
try { [void][DragProbe]::SetProcessDpiAwareness(2) } catch { }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Find-Id { param($Root, [string]$Id)
    return $Root.FindFirst($TS::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $Id)))
}

function Find-Name { param($Root, [string]$Name, $ControlType)
    $conds = @((New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)))
    if ($ControlType) {
        $conds += (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ControlType))
    }
    if ($conds.Count -eq 1) { return $Root.FindFirst($TS::Descendants, $conds[0]) }
    return $Root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition($conds)))
}

function Invoke-El { param($Element, [string]$What)
    if ($null -eq $Element) { throw "找不到控件：$What" }
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Get-SelectedText { param($Pattern)
    $ranges = $Pattern.GetSelection()
    if ($ranges.Count -eq 0) { return '' }
    return $ranges[0].GetText(-1)
}

function Get-TextRect { param($Range)
    $bounds = $Range.GetBoundingRectangles()
    if ($bounds.Count -lt 4) { throw '测试文字当前不可见' }
    return New-Object System.Windows.Rect($bounds[0], $bounds[1], $bounds[2], $bounds[3])
}

function Get-ClipboardText {
    $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
    if ($null -eq $text) { return '' }
    return $text
}

Get-Process SeriTerm -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(3000) }
Start-Sleep -Milliseconds 800

$process = Start-Process -FilePath $ExePath -PassThru
try {
    $deadline = (Get-Date).AddSeconds(90)
    $handle = [IntPtr]::Zero
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        $handle = $process.MainWindowHandle
        if ($handle -ne [IntPtr]::Zero) {
            $ready = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
            if ($null -ne (Find-Id -Root $ready -Id 'LogViewControl')) { break }
        }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Milliseconds 1200
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)

    # 灌点数据：打开串口 + 发几行
    # （使用者的配置里可能开着"启动时自动打开串口"，那启动完按钮就已经是"关闭串口"了）
    $openButton = Find-Name -Root $root -Name '打开串口' -ControlType $CT::Button
    if ($null -ne $openButton) {
        Invoke-El $openButton '打开串口'
        Start-Sleep -Milliseconds 1500
    }
    else {
        Write-Output "OPEN|没有“打开串口”按钮：配置里开了启动自动打开，端口应已打开"
    }
    $sendBox = Find-Id -Root $root -Id 'SendTextBox'
    $sendBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('DRAG-PROBE-LINE')
    for ($i = 0; $i -lt 18; $i++) {
        Invoke-El (Find-Name -Root $root -Name '发送' -ControlType $CT::Button) '发送'
        Start-Sleep -Milliseconds 100
    }
    Start-Sleep -Milliseconds 1200

    $list = Find-Id -Root $root -Id 'LogList'
    if ($null -eq $list) { throw '找不到 LogList' }

    $pattern = $list.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $available = $pattern.DocumentRange
    $r2 = $null
    foreach ($i in 1..3) {
        $marker = $available.FindText('DRAG-PROBE-LINE', $true, $false)
        if ($null -eq $marker) { throw '找不到足够的测试日志' }
        if ($i -eq 1) { $r2 = Get-TextRect $marker }
        $available.MoveEndpointByRange([System.Windows.Automation.TextPatternRangeEndpoint]::End,
            $marker, [System.Windows.Automation.TextPatternRangeEndpoint]::Start)
    }
    $r1 = Get-TextRect $marker

    # --- 1) 单击只移动光标 ---
    [void][DragProbe]::SetCursorPos([int](($r2.Left + $r2.Right) / 2), [int](($r2.Top + $r2.Bottom) / 2))
    Start-Sleep -Milliseconds 200
    [DragProbe]::LeftDown()
    Start-Sleep -Milliseconds 120
    [DragProbe]::LeftUp()
    Start-Sleep -Milliseconds 600
    if (Get-SelectedText $pattern) { throw '单击后不应选中文字' }

    # --- 2) 跨行拖选 ---
    $startX = $r1.Left + $r1.Width / 4
    $endX = $r2.Left + $r2.Width / 2
    $startY = ($r1.Top + $r1.Bottom) / 2
    $endY = ($r2.Top + $r2.Bottom) / 2
    [System.Windows.Forms.Clipboard]::Clear()
    [void][DragProbe]::SetCursorPos([int]$startX, [int]$startY)
    Start-Sleep -Milliseconds 250
    [DragProbe]::LeftDown()
    Start-Sleep -Milliseconds 150
    for ($step = 1; $step -le 12; $step++) {
        $y = $startY + (($endY - $startY) * $step / 12)
        $x = $startX + (($endX - $startX) * $step / 12)
        [void][DragProbe]::SetCursorPos([int]$x, [int]$y)
        Start-Sleep -Milliseconds 60
    }
    Start-Sleep -Milliseconds 250
    [DragProbe]::LeftUp()
    Start-Sleep -Milliseconds 700
    $picked = Get-SelectedText $pattern

    [System.Windows.Forms.SendKeys]::SendWait('^c')
    Start-Sleep -Milliseconds 800
    $clip = Get-ClipboardText
    $firstLine = ($clip -split "`r`n")[0]
    Write-Output ("DRAGCOPY|字符数={0}|含CRLF={1}|首行=[{2}]" -f $clip.Length, $clip.Contains("`r`n"), $firstLine)
    if ($clip -cne $picked -or -not $clip.Contains("`r`n") -or $firstLine -match '^\d\d:\d\d:\d\d\.\d\d\d ') {
        throw '跨行复制应与选中的字符一致'
    }

    # --- 3) 行内拖选 ---
    [System.Windows.Forms.Clipboard]::Clear()
    $startX = $r2.Left + $r2.Width / 4
    $endX = $r2.Left + $r2.Width * 3 / 4
    $midY = [int]$endY
    [void][DragProbe]::SetCursorPos([int]$startX, $midY)
    Start-Sleep -Milliseconds 250
    [DragProbe]::LeftDown()
    Start-Sleep -Milliseconds 120
    for ($step = 1; $step -le 8; $step++) {
        [void][DragProbe]::SetCursorPos([int]($startX + (($endX - $startX) * $step / 8)), $midY)
        Start-Sleep -Milliseconds 60
    }
    Start-Sleep -Milliseconds 200
    [DragProbe]::LeftUp()
    Start-Sleep -Milliseconds 600

    $picked = Get-SelectedText $pattern
    Write-Output ("TEXTSEL|行内拖选得到的字符=[{0}]" -f $picked)

    [System.Windows.Forms.SendKeys]::SendWait('^c')
    Start-Sleep -Milliseconds 800
    $clip = Get-ClipboardText
    Write-Output ("TEXTCOPY|剪贴板=[{0}]" -f $clip)
    if (-not $picked -or $picked.Contains("`n") -or $clip -cne $picked) {
        throw '行内复制应与选中的字符一致'
    }
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
}
