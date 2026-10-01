# 一次性诊断：日志区到底是"点一下不选中"还是"拖动不连选"。
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
    Invoke-El (Find-Name -Root $root -Name '打开串口' -ControlType $CT::Button) '打开串口'
    Start-Sleep -Milliseconds 1500
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
    $r5 = $items[6].Current.BoundingRectangle
    Write-Output ("RECT|item1={0:0},{1:0},{2:0},{3:0}" -f $r1.Left, $r1.Top, $r1.Right, $r1.Bottom)

    # --- 1) 单击第 3 行：应当选中 1 行 ---
    [void][DragProbe]::SetCursorPos([int](($r2.Left + $r2.Right) / 2), [int](($r2.Top + $r2.Bottom) / 2))
    Start-Sleep -Milliseconds 200
    [DragProbe]::LeftDown()
    Start-Sleep -Milliseconds 120
    [DragProbe]::LeftUp()
    Start-Sleep -Milliseconds 600
    Write-Output "CLICK|选中行数=$(Get-SelectedCount $list)"

    # --- 2) 从第 1 行拖到第 6 行：应当选中 6 行 ---
    [void][DragProbe]::SetCursorPos([int](($r1.Left + $r1.Right) / 2), [int](($r1.Top + $r1.Bottom) / 2))
    Start-Sleep -Milliseconds 250
    [DragProbe]::LeftDown()
    Start-Sleep -Milliseconds 150
    for ($step = 1; $step -le 12; $step++) {
        $y = $r1.Top + (($r5.Top - $r1.Top) * $step / 12)
        [void][DragProbe]::SetCursorPos([int](($r1.Left + $r1.Right) / 2), [int]$y)
        Start-Sleep -Milliseconds 60
    }
    Start-Sleep -Milliseconds 250
    [DragProbe]::LeftUp()
    Start-Sleep -Milliseconds 700
    Write-Output "DRAG|选中行数=$(Get-SelectedCount $list)"

    # --- 3) 复制命令能不能用（Ctrl+C 走的是选中项） ---
    $list.SetFocus()
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('^c')
    Start-Sleep -Milliseconds 800
    $clip = Get-Clipboard -Raw -ErrorAction SilentlyContinue
    if ($null -eq $clip) { $clip = '' }
    Write-Output ("COPY|字符数={0}|首行={1}" -f $clip.Length, ($clip -split "`r`n")[0])
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
}
