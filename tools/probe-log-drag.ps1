# 日志区"选择与复制"回归探针：真实鼠标事件拖一遍，读回 UIA 选中行数、行内选中字符与剪贴板内容。
#
# 期望（2026-10 改为"跨行按字符自由选择"之后）：
#   1) 单击某行            → 选中行数=1（整行复制这个粒度仍在）
#   2) 从第 1 行内容中间拖到第 3 行内容中间
#                         → 选中行数=0，复制出来的是"那一段字符"（含 CRLF，首行不是完整一行）
#   3) 在同一行内拖一小段   → 选中行数=0，文本框原生选中那一段，Ctrl+C 只复制那几个字
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

function Get-SelectedCount { param($List)
    $pattern = $List.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
    return $pattern.Current.GetSelection().Count
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

    $items = $list.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)))
    Write-Output "ITEMS|$($items.Count)"
    if ($items.Count -lt 6) { throw "日志行太少（$($items.Count)），无法做拖动测试" }

    $r1 = $items[1].Current.BoundingRectangle
    $r2 = $items[3].Current.BoundingRectangle
    $r3 = $items[6].Current.BoundingRectangle
    Write-Output ("RECT|item1={0:0},{1:0},{2:0},{3:0}" -f $r1.Left, $r1.Top, $r1.Right, $r1.Bottom)

    # --- 1) 单击第 3 行：应当选中 1 行 ---
    [void][DragProbe]::SetCursorPos([int](($r2.Left + $r2.Right) / 2), [int](($r2.Top + $r2.Bottom) / 2))
    Start-Sleep -Milliseconds 200
    [DragProbe]::LeftDown()
    Start-Sleep -Milliseconds 120
    [DragProbe]::LeftUp()
    Start-Sleep -Milliseconds 600
    Write-Output "CLICK|选中行数=$(Get-SelectedCount $list)"

    # --- 2) 从第 1 行内容中间拖到第 3 行内容中间：应当是"这一段字符"的选择，不该再整行连选 ---
    #     内容列在时间列（104）+ 方向列（30）之后，所以从行左边缘 +260 起算落在内容文字上
    $startX = $r1.Left + 260
    $endX = $r2.Left + 300
    [System.Windows.Forms.Clipboard]::Clear()
    [void][DragProbe]::SetCursorPos([int]$startX, [int](($r1.Top + $r1.Bottom) / 2))
    Start-Sleep -Milliseconds 250
    [DragProbe]::LeftDown()
    Start-Sleep -Milliseconds 150
    for ($step = 1; $step -le 12; $step++) {
        $y = $r1.Top + (($r2.Top - $r1.Top) * $step / 12)
        $x = $startX + (($endX - $startX) * $step / 12)
        [void][DragProbe]::SetCursorPos([int]$x, [int]$y)
        Start-Sleep -Milliseconds 60
    }
    Start-Sleep -Milliseconds 250
    [DragProbe]::LeftUp()
    Start-Sleep -Milliseconds 700
    Write-Output "DRAG|选中行数=$(Get-SelectedCount $list)（跨行部分选择应当=0）"

    $list.SetFocus()
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('^c')
    Start-Sleep -Milliseconds 800
    $clip = Get-ClipboardText
    $firstLine = ($clip -split "`r`n")[0]
    Write-Output ("DRAGCOPY|字符数={0}|含CRLF={1}|首行=[{2}]" -f $clip.Length, $clip.Contains("`r`n"), $firstLine)
    Write-Output ("DRAGCOPY-判定|首行不是完整一行（不以时间戳开头）= {0}" -f (-not ($firstLine -match '^\d\d:\d\d:\d\d\.\d\d\d ')))

    # --- 3) 行内自由选择：在第 4 行文字上拖一小段，Ctrl+C 应当只复制那一段字符 ---
    [System.Windows.Forms.Clipboard]::Clear()
    $r4 = $items[4].Current.BoundingRectangle
    $startX = $r4.Left + 300      # 跳过时间列/方向列，落在内容文本上
    $endX = $r4.Left + 420
    $midY = [int](($r4.Top + $r4.Bottom) / 2)
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

    # 行文本框是只读 Edit，读一下它当前选中的文本
    $edits = $list.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Edit)))
    $picked = ''
    foreach ($edit in $edits) {
        $pattern = $null
        if ($edit.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$pattern)) {
            $ranges = $pattern.GetSelection()
            if ($ranges.Count -gt 0) {
                $t = $ranges[0].GetText(-1)
                if ($t) { $picked = $t; break }
            }
        }
    }
    Write-Output ("TEXTSEL|行内拖选得到的字符=[{0}]" -f $picked)

    [System.Windows.Forms.SendKeys]::SendWait('^c')
    Start-Sleep -Milliseconds 800
    Write-Output ("TEXTCOPY|剪贴板=[{0}]" -f (Get-ClipboardText))
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
}
