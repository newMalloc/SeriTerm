# 针对性探针：查找命中的"字符级高亮"与行选中的观感是否真的画出来了。
# 做法：开回环串口灌几行 → Ctrl+F 输入关键字 → 截图（人眼看高亮）→ 再用真实鼠标拖选两行 → 再截一张。
# 用法：powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/probe-log-search.ps1 -ExePath <exe> -OutDir <dir>
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Port = 'COM5',
    [string]$Keyword = 'loopba',
    [string]$SendText = 'SeriTerm loopback test'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

if (-not ('SearchProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class SearchProbe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int awareness);

    public static void LeftDown() { mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); }
    public static void LeftUp() { mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); }
}
'@
}

[void][SearchProbe]::SetProcessDPIAware()
try { [void][SearchProbe]::SetProcessDpiAwareness(2) } catch { }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

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

function Save-Shot { param([IntPtr]$Handle, [string]$Name)
    $rect = New-Object SearchProbe+RECT
    [void][SearchProbe]::GetWindowRect($Handle, [ref]$rect)
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    $bitmap = New-Object System.Drawing.Bitmap($w, $h)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try { [void][SearchProbe]::PrintWindow($Handle, $hdc, 2) }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    $bitmap.Save((Join-Path $OutDir "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Output "SHOT|$Name|${w}x${h}"
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
    Start-Sleep -Milliseconds 1500
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)

    Invoke-El (Find-Name -Root $root -Name '打开串口' -ControlType $CT::Button) '打开串口'
    Start-Sleep -Milliseconds 1500

    $sendBox = Find-Id -Root $root -Id 'SendTextBox'
    $sendBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($SendText)
    for ($i = 0; $i -lt 10; $i++) {
        Invoke-El (Find-Name -Root $root -Name '发送' -ControlType $CT::Button) '发送'
        Start-Sleep -Milliseconds 120
    }
    Start-Sleep -Milliseconds 1200

    # Ctrl+F 打开搜索条，填入关键字
    [void][SearchProbe]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait('^f')
    Start-Sleep -Milliseconds 900
    $searchBox = Find-Id -Root $root -Id 'SearchBox'
    if ($null -eq $searchBox) { throw '搜索条没打开（找不到 SearchBox）' }
    $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Keyword)
    Start-Sleep -Milliseconds 1200

    # 跳到第 2 处命中，好让"当前命中行"和普通命中行同时出现在画面上
    $nextButton = Find-Name -Root $root -Name '▼' -ControlType $CT::Button
    if ($nextButton) {
        Invoke-El $nextButton '下一个'
        Start-Sleep -Milliseconds 900
    }
    Save-Shot -Handle $handle -Name '01-search'

    # 命中数文本
    foreach ($t in $root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)))) {
        if ($t.Current.Name -match '第 .* 共') { Write-Output "SEARCH|$($t.Current.Name)" }
    }

    # 再拖选两行，看选中底色够不够明显
    $list = Find-Id -Root $root -Id 'LogList'
    $items = $list.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)))
    if ($items.Count -ge 4) {
        $a = $items[1].Current.BoundingRectangle
        $b = $items[3].Current.BoundingRectangle
        [void][SearchProbe]::SetCursorPos([int]($a.Left + 40), [int](($a.Top + $a.Bottom) / 2))
        Start-Sleep -Milliseconds 250
        [SearchProbe]::LeftDown()
        Start-Sleep -Milliseconds 150
        for ($step = 1; $step -le 10; $step++) {
            $y = $a.Top + (($b.Top - $a.Top) * $step / 10)
            [void][SearchProbe]::SetCursorPos([int]($a.Left + 40), [int]$y)
            Start-Sleep -Milliseconds 60
        }
        [SearchProbe]::LeftUp()
        Start-Sleep -Milliseconds 700

        $pattern = $list.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
        Write-Output "SELECTED|$($pattern.Current.GetSelection().Count)"
        Save-Shot -Handle $handle -Name '02-selection'
    }

    # --- 关掉搜索条：命中高亮（行底色 + 字符方块）必须一起消失 ---
    $closeButton = Find-Name -Root $root -Name '✕' -ControlType $CT::Button
    if ($null -eq $closeButton) { throw '找不到搜索条的关闭按钮' }
    Invoke-El $closeButton '关闭搜索'
    Start-Sleep -Milliseconds 1200
    Save-Shot -Handle $handle -Name '03-search-closed'

    # 在日志区里找"连续的琥珀色横条"：只有查找高亮才可能形成几十像素长的横条，
    # 橙色文本（Tx 行）的抗锯齿边缘只会留下一两个像素的碎点，所以按"最长连续长度"判定。
    # 坐标系：UIA 矩形是屏幕坐标，截图以窗口左上角为原点，要先减去窗口原点。
    $winRect = New-Object SearchProbe+RECT
    [void][SearchProbe]::GetWindowRect($handle, [ref]$winRect)
    $listRect = $list.Current.BoundingRectangle
    $shot = [System.Drawing.Bitmap]::FromFile((Join-Path $OutDir '03-search-closed.png'))
    $bandRows = 0
    for ($py = [int]($listRect.Top - $winRect.Top); $py -lt [int]($listRect.Bottom - $winRect.Top); $py += 2) {
        if ($py -lt 0 -or $py -ge $shot.Height) { continue }
        $run = 0
        $best = 0
        for ($px = [int]($listRect.Left - $winRect.Left); $px -lt [int]($listRect.Right - $winRect.Left); $px += 1) {
            if ($px -lt 0 -or $px -ge $shot.Width) { continue }
            $c = $shot.GetPixel($px, $py)
            $pale = [math]::Abs($c.R - 255) + [math]::Abs($c.G - 228) + [math]::Abs($c.B - 154)
            $strong = [math]::Abs($c.R - 255) + [math]::Abs($c.G - 190) + [math]::Abs($c.B - 61)
            if ($pale -lt 60 -or $strong -lt 60) {
                $run++
                if ($run -gt $best) { $best = $run }
            }
            else { $run = 0 }
        }
        if ($best -ge 8) { $bandRows++ }
    }
    $shot.Dispose()
    Write-Output "AFTER_CLOSE|仍有高亮横条的行数=$bandRows（期望 0）"
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
}
