# 针对性探针：查找框 / 收藏栏是不是真的"叠在日志右上角"。
#
# 要证明的三件事：
#   1) 打开搜索不再把日志区压下去（日志列表顶边前后位移必须为 0）；
#   2) 收藏栏是"每项一行"的垂直列表，关掉搜索后仍然在（随时可以点回去）；
#   3) 点收藏行能填回关键字，行尾 × 能删掉这一条、删空后浮层整体消失。
#
# 用法：powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/probe-search-overlay.ps1 -ExePath <exe> -OutDir <dir>
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Port = 'COM5',
    [string]$Keyword = 'loopba',
    [string]$SendText = 'SeriTerm loopback test'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

if (-not ('OverlayProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class OverlayProbe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int awareness);
}
'@
}

[void][OverlayProbe]::SetProcessDPIAware()
try { [void][OverlayProbe]::SetProcessDpiAwareness(2) } catch { }

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

function Find-HelpText { param($Root, [string]$Text)
    return $Root.FindFirst($TS::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::HelpTextProperty, $Text)))
}

# 收藏行就是一个按钮，AutomationProperties.Name 直接绑成了关键字
function Find-FavoriteRow { param($Root, [string]$Text)
    return Find-Name -Root $Root -Name $Text -ControlType $CT::Button
}

function Invoke-El { param($Element, [string]$What)
    if ($null -eq $Element) { throw "找不到控件：$What" }
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Get-Rect { param($Element)
    return $Element.Current.BoundingRectangle
}

function Save-Shot { param([IntPtr]$Handle, [string]$Name)
    $rect = New-Object OverlayProbe+RECT
    [void][OverlayProbe]::GetWindowRect($Handle, [ref]$rect)
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    $bitmap = New-Object System.Drawing.Bitmap($w, $h)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try { [void][OverlayProbe]::PrintWindow($Handle, $hdc, 2) }
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
            $ready = $AE::FromHandle($handle)
            if ($null -ne (Find-Id -Root $ready -Id 'LogViewControl')) { break }
        }
        Start-Sleep -Milliseconds 500
    }
    if ($handle -eq [IntPtr]::Zero) { throw '主窗口尚未创建' }
    Start-Sleep -Milliseconds 1500
    $root = $AE::FromHandle($handle)

    # 先灌几行日志，才有东西被浮层盖住 / 才能搜到命中
    Invoke-El (Find-Name -Root $root -Name '打开串口' -ControlType $CT::Button) '打开串口'
    Start-Sleep -Milliseconds 1500

    $sendBox = Find-Id -Root $root -Id 'SendTextBox'
    $sendBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($SendText)
    for ($i = 0; $i -lt 12; $i++) {
        Invoke-El (Find-Name -Root $root -Name '发送' -ControlType $CT::Button) '发送'
        Start-Sleep -Milliseconds 120
    }
    Start-Sleep -Milliseconds 1200

    $list = Find-Id -Root $root -Id 'LogList'
    $logView = Find-Id -Root $root -Id 'LogViewControl'
    $logTopClosed = [math]::Round((Get-Rect $list).Top, 1)
    $viewRect = Get-Rect $logView
    Write-Output ("LOGTOP|未搜索时日志列表顶边={0}" -f $logTopClosed)
    Write-Output ("LOGVIEW|左={0:0} 右={1:0} 顶={2:0}" -f $viewRect.Left, $viewRect.Right, $viewRect.Top)
    Save-Shot -Handle $handle -Name '01-closed'

    # 浮层此刻应当完全不存在：没有搜索框，也没有收藏
    $before = Find-Id -Root $root -Id 'SearchBox'
    Write-Output "OVERLAY|未搜索且无收藏时浮层存在=$($null -ne $before)（期望 False）"

    # --- 打开搜索：日志列表顶边必须一动不动 ---
    [void][OverlayProbe]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait('^f')
    Start-Sleep -Milliseconds 900
    $searchBox = Find-Id -Root $root -Id 'SearchBox'
    if ($null -eq $searchBox) { throw '搜索浮层没打开（找不到 SearchBox）' }

    $logTopOpen = [math]::Round((Get-Rect $list).Top, 1)
    Write-Output ("LOGTOP|搜索打开后日志列表顶边={0}|位移={1}（期望 0）" -f $logTopOpen, ($logTopOpen - $logTopClosed))
    if ([math]::Abs($logTopOpen - $logTopClosed) -gt 0.5) { Write-Output 'LOGTOP|日志区被搜索条压下去了' }

    # 浮层贴右上角：用"关闭搜索"那颗 ✕ 代表浮层右边界（它是浮层里最靠右的元素）
    $closeButton = Find-HelpText -Root $root -Text '关闭搜索（Esc）'
    $closeRect = Get-Rect $closeButton
    $boxRect = Get-Rect $searchBox
    $cardRightGap = [math]::Round($viewRect.Right - $closeRect.Right, 1)
    $cardTopGap = [math]::Round($closeRect.Top - $viewRect.Top, 1)
    Write-Output ("OVERLAY|浮层右边界距日志区右边={0} 顶边界距日志区上边={1}（都要小，说明叠在右上角）" -f $cardRightGap, $cardTopGap)
    Write-Output ("OVERLAY|查找框 左={0:0}（日志区左={1:0}，应当在右半边）" -f $boxRect.Left, $viewRect.Left)

    $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Keyword)
    Start-Sleep -Milliseconds 1200
    foreach ($t in $root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text)))) {
        if ($t.Current.Name -match '第 .* 共') { Write-Output "SEARCH|$($t.Current.Name)" }
    }
    Save-Shot -Handle $handle -Name '02-search'

    # --- 收藏两个关键字：证明收藏栏是"每项一行"的垂直列表，而不是横着排的标签 ---
    $keyword2 = 'SeriTerm'
    Invoke-El (Find-Name -Root $root -Name '收藏' -ControlType $CT::Button) '收藏'
    Start-Sleep -Milliseconds 800
    $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($keyword2)
    Start-Sleep -Milliseconds 700
    Invoke-El (Find-Name -Root $root -Name '收藏' -ControlType $CT::Button) '收藏第二个'
    Start-Sleep -Milliseconds 900

    $row1 = Find-FavoriteRow -Root $root -Text $Keyword
    $row2 = Find-FavoriteRow -Root $root -Text $keyword2
    Write-Output "FAV|收藏行出现=$($null -ne $row1) 和 $($null -ne $row2)"
    if ($null -ne $row1 -and $null -ne $row2) {
        $r1 = Get-Rect $row1
        $r2 = Get-Rect $row2
        $stacked = ([math]::Abs($r1.Left - $r2.Left) -le 2) -and ($r2.Top -ge $r1.Bottom - 1)
        Write-Output ("FAV|{0}: 距右={1:0} 顶={2:0} 底={3:0} 行高={4:0}" -f $Keyword, ($viewRect.Right - $r1.Right), $r1.Top, $r1.Bottom, $r1.Height)
        Write-Output ("FAV|{0}: 距右={1:0} 顶={2:0} 底={3:0} 行高={4:0}" -f $keyword2, ($viewRect.Right - $r2.Right), $r2.Top, $r2.Bottom, $r2.Height)
        Write-Output "FAV|两项左边界对齐且上下叠放=$stacked（期望 True）"
        Write-Output ("FAV|两项所在行高之和={0:0}（两行就是两倍行高，说明各占一行）" -f ($r1.Height + $r2.Height))
    }
    Save-Shot -Handle $handle -Name '03-search-fav'

    # 深色主题下再截一张：浮层用的是主题画刷，两套主题都得看得清
    Invoke-El (Find-Name -Root $root -Name '切换到深色' -ControlType $CT::Button) '切深色'
    Start-Sleep -Milliseconds 900
    Save-Shot -Handle $handle -Name '03b-dark'
    Invoke-El (Find-Name -Root $root -Name '切换到浅色' -ControlType $CT::Button) '切回浅色'
    Start-Sleep -Milliseconds 900

    # --- 关掉搜索：收藏栏留在原地，日志高亮同时清掉 ---
    Invoke-El (Find-HelpText -Root $root -Text '关闭搜索（Esc）') '关闭搜索'
    Start-Sleep -Milliseconds 1000
    $searchBox = Find-Id -Root $root -Id 'SearchBox'
    $row1 = Find-FavoriteRow -Root $root -Text $Keyword
    $row2 = Find-FavoriteRow -Root $root -Text $keyword2
    Write-Output "FAV|关掉搜索后：搜索框在=$($null -ne $searchBox)（期望 False）|两条收藏都在=$(($null -ne $row1) -and ($null -ne $row2))（期望 True）"
    if ($null -ne $row1 -and $null -ne $row2) {
        $r1 = Get-Rect $row1
        $r2 = Get-Rect $row2
        Write-Output ("LOGTOC|关掉搜索后收藏栏 距顶={0:0} 距右={1:0}（收藏栏顶上就是日志区上边+26，说明它压在最上面那几行日志上）" -f ($r1.Top - $viewRect.Top), ($viewRect.Right - $r1.Right))
        Write-Output ("LOGTOC|收藏栏底边={0:0}" -f $r2.Bottom)
    }
    $logTopClosedAgain = [math]::Round((Get-Rect $list).Top, 1)
    Write-Output ("LOGTOP|关掉搜索后日志列表顶边={0}" -f $logTopClosedAgain)
    Save-Shot -Handle $handle -Name '04-fav-only'

    # --- 点收藏行：搜索浮层回来，关键字填回输入框 ---
    Invoke-El (Find-FavoriteRow -Root $root -Text $Keyword) '收藏行'
    Start-Sleep -Milliseconds 1200
    $searchBox = Find-Id -Root $root -Id 'SearchBox'
    $value = ''
    if ($null -ne $searchBox) {
        $value = $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    }
    Write-Output "FAV|点收藏行后：搜索框值='$value'（期望 $Keyword）"
    Save-Shot -Handle $handle -Name '05-reapplied'

    # --- 逐条删掉：收藏删空；搜索还开着，所以浮层留下查找框、只剩收藏那一块消失 ---
    Invoke-El (Find-Name -Root $root -Name '删除收藏' -ControlType $CT::Button) '删除第一条'
    Start-Sleep -Milliseconds 900
    $row1 = Find-FavoriteRow -Root $root -Text $Keyword
    $row2 = Find-FavoriteRow -Root $root -Text $keyword2
    Write-Output "FAV|删掉一条后：$Keyword 在=$($null -ne $row1)（期望 False）|$keyword2 在=$($null -ne $row2)（期望 True）"

    Invoke-El (Find-Name -Root $root -Name '删除收藏' -ControlType $CT::Button) '删除第二条'
    Start-Sleep -Milliseconds 900
    $searchBox = Find-Id -Root $root -Id 'SearchBox'
    $row2 = Find-FavoriteRow -Root $root -Text $keyword2
    Write-Output "FAV|删空后：搜索框在=$($null -ne $searchBox)（期望 True，搜索还开着）|收藏行在=$($null -ne $row2)（期望 False）"
    Save-Shot -Handle $handle -Name '06-removed'
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
}
