# 界面冒烟测试：用 UI 自动化真实点击"打开 / 发送"，验证 COM5 回环与自动滚动/搜索在界面上跑通，
# 并截图 + 导出界面文本，便于人工与脚本双重核对。
#
# 需要 Windows PowerShell 5.1（powershell.exe）运行：UIAutomationClient 只在 .NET Framework 里提供。
# 注意：本文件必须保存为"带 BOM 的 UTF-8"，否则 PowerShell 5.1 会按 ANSI 读取，中文按钮名会乱码。
#
# 用法：powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ui-smoke-test.ps1 -ExePath <exe> -OutDir artifacts

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Port = 'COM5',
    [int]$SendClicks = 3,
    [string]$SearchText = 'loopback',
    # UIA：用 ValuePattern 直接写值；Keys：模拟真实键盘输入（验证键盘路径也能触发绑定）
    [ValidateSet('UIA', 'Keys')][string]$SearchInputMode = 'UIA',
    [switch]$SkipAutoScrollCheck
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not ('WinCap' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WinCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
}
'@
}

[void][WinCap]::SetProcessDPIAware()

if (-not (Test-Path $ExePath)) { throw "找不到可执行文件：$ExePath" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# 上一次异常退出可能留下占用串口与 exe 的僵尸进程，先清掉，保证脚本可重复运行
$leftovers = Get-Process -Name 'SeriTerm' -ErrorAction SilentlyContinue
if ($leftovers) {
    Write-Output "== 清理残留的 SeriTerm 进程：$($leftovers.Count) 个"
    $leftovers | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

$portNames = [System.IO.Ports.SerialPort]::GetPortNames()
if ($portNames -notcontains $Port) {
    Write-Output "SKIP: 未检测到 $Port（当前：$($portNames -join ', ')），跳过界面冒烟测试。"
    exit 0
}

function Find-ByName {
    param($Parent, [string]$Name)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ByAutomationId {
    param($Parent, [string]$AutomationId)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    return $Parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-Element {
    param($Element, [string]$What)
    if ($null -eq $Element) { throw "界面上找不到控件：$What" }
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Get-ToggleState {
    param($Element, [string]$What)
    if ($null -eq $Element) { throw "界面上找不到控件：$What" }
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return $pattern.Current.ToggleState
}

function Save-WindowShot {
    param($Window, [string]$Path)
    $rect = New-Object WinCap+RECT
    [void][WinCap]::GetWindowRect($Window.Current.NativeWindowHandle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try { [void][WinCap]::PrintWindow($Window.Current.NativeWindowHandle, $hdc, 2) }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function Get-AllTexts {
    param($Window)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $elements = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $texts = @()
    foreach ($element in $elements) {
        $name = $element.Current.Name
        if ($name) { $texts += $name }
    }
    return $texts
}

$process = Start-Process -FilePath $ExePath -PassThru
try {
    Start-Sleep -Seconds 5

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $processCondition)
    if ($null -eq $window) { throw '找不到应用主窗口' }

    Write-Output "== 打开串口 $Port"
    Invoke-Element (Find-ByName $window '打开串口') '打开'
    Start-Sleep -Seconds 2

    Write-Output "== 点击发送 $SendClicks 次"
    for ($i = 0; $i -lt $SendClicks; $i++) {
        Invoke-Element (Find-ByName $window '发送') '发送'
        Start-Sleep -Milliseconds 400
    }
    Start-Sleep -Seconds 1

    Save-WindowShot $window (Join-Path $OutDir 'ui-loopback-dark.png')

    if (-not $SkipAutoScrollCheck) {
        Write-Output '== 自动滚动检查 =='
        try {
            $autoScrollToggle = Find-ByAutomationId $window 'AutoScrollToggle'
            $logList = Find-ByAutomationId $window 'LogList'
            if ($null -eq $autoScrollToggle -or $null -eq $logList) { throw '找不到自动滚动开关或日志列表' }

            # 先灌足够多的数据，让内容超出可视区
            for ($i = 0; $i -lt 20; $i++) {
                Invoke-Element (Find-ByName $window '发送') '发送'
                Start-Sleep -Milliseconds 120
            }
            Start-Sleep -Seconds 1

            Write-Output "AUTO_SCROLL|初始跟随状态=$(Get-ToggleState $autoScrollToggle '自动滚动')"

            $scrollPattern = $logList.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            Write-Output ("AUTO_SCROLL|滚动位置={0:N1}%" -f $scrollPattern.Current.VerticalScrollPercent)

            # 用户向上滚动 => 应自动停止跟随
            $scrollPattern.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount,
                                  [System.Windows.Automation.ScrollAmount]::LargeDecrement)
            Start-Sleep -Milliseconds 700
            Write-Output "AUTO_SCROLL|向上滚动后跟随状态=$(Get-ToggleState $autoScrollToggle '自动滚动')"

            # 不跟随状态下再来新数据 => 日志区底部应出现"新数据"提示条
            Invoke-Element (Find-ByName $window '发送') '发送'
            Start-Sleep -Milliseconds 900
            Save-WindowShot $window (Join-Path $OutDir 'ui-autoscroll-paused.png')

            # 拉回底部 => 应自动恢复跟随
            for ($i = 0; $i -lt 12; $i++) {
                $scrollPattern.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount,
                                      [System.Windows.Automation.ScrollAmount]::LargeIncrement)
            }
            Start-Sleep -Milliseconds 900
            Write-Output "AUTO_SCROLL|拉回底部后跟随状态=$(Get-ToggleState $autoScrollToggle '自动滚动')"
        }
        catch {
            Write-Output "AUTO_SCROLL|检查失败：$($_.Exception.Message)"
        }
    }

    Write-Output "== Ctrl+F 打开搜索并填入 '$SearchText'"
    [void][WinCap]::SetForegroundWindow($window.Current.NativeWindowHandle)
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait('^f')
    Start-Sleep -Milliseconds 800

    $searchBox = Find-ByAutomationId $window 'SearchBox'
    if ($null -eq $searchBox) {
        Write-Output 'WARN: 找不到搜索框（AutomationId=SearchBox），改用 SendKeys'
        [System.Windows.Forms.SendKeys]::SendWait($SearchText)
    }
    elseif ($SearchInputMode -eq 'Keys') {
        # 验证"真实键盘输入"这条路径：只有窗口确实在前台，SendKeys 才会打到它身上
        $foreground = [WinCap]::GetForegroundWindow()
        Write-Output ("KEYS|前台窗口=0x{0:X} 应用窗口=0x{1:X}" -f [int64]$foreground, [int64]$window.Current.NativeWindowHandle)
        $searchBox.SetFocus()
        Start-Sleep -Milliseconds 400
        [System.Windows.Forms.SendKeys]::SendWait($SearchText)
    }
    else {
        $valuePattern = $searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $valuePattern.SetValue($SearchText)
    }

    Start-Sleep -Seconds 1
    Invoke-Element (Find-ByName $window '▼') '下一个命中'
    Start-Sleep -Milliseconds 800

    Save-WindowShot $window (Join-Path $OutDir 'ui-search-dark.png')

    Write-Output '== 界面文本清单 =='
    Get-AllTexts $window | ForEach-Object { Write-Output "TEXT|$_" }
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
}
