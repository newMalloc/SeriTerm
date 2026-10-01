# 界面改版后的结构与交互检查：把"改掉了什么、还在不在、点了有没有反应"变成可复现的输出。
# 用法：powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ui-layout-check.ps1 -ExePath <exe> -OutDir <dir> [-Port COM5]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Port = 'COM5'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
if (Test-Path "$env:APPDATA\SeriTerm\ui-errors.log") { Remove-Item "$env:APPDATA\SeriTerm\ui-errors.log" -Force }
Get-Process SeriTerm -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(3000) }
Start-Sleep -Milliseconds 800

$portNames = [System.IO.Ports.SerialPort]::GetPortNames()
if ($portNames -notcontains $Port) {
    Write-Output "SKIP: 未检测到 $Port，跳过界面改版检查。"
    exit 0
}

# 自己写一份确定的配置：浅色主题、不自动打开串口、带两个预设。
# （M4-M8 四个冒烟脚本都会把 settings.json 覆盖成深色主题并留着不管，
#   所以这里不能依赖"跑完烟测后的配置"；脚本跑完我再单独还原用户配置。）
$settingsDir = Join-Path $env:APPDATA 'SeriTerm'
New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
$seed = [ordered]@{
    Theme                    = 'Light'
    LastSerial               = [ordered]@{
        PortName = $Port; BaudRate = 115200; DataBits = 8
        Parity = 'None'; StopBits = 'One'; Handshake = 'None'
        DtrEnable = $false; RtsEnable = $false
    }
    WindowWidth              = 1280
    WindowHeight             = 800
    WindowMaximized          = $false
    AutoScroll               = $true
    HexDisplay               = $false
    EncodingName             = 'UTF-8'
    Framing                  = 'Delimiter'
    AutoFrameGapMilliseconds = 20
    DelimiterText            = '\r\n'
    ShowTimestamp            = $true
    LineWrap                 = $true
    LogFontSize              = 13
    SendHex                  = $false
    SendLineEnding           = 'CrLf'
    TimedSendIntervalSeconds = 0.5
    SaveLogToFile            = $false
    SaveRawLog               = $false
    AutoReconnect            = $true
    AutoOpenOnStartup        = $false
    BlurBackground           = $true
    Presets                  = @(
        [ordered]@{
            Name    = "$Port 1000000 8N1"
            Serial  = [ordered]@{
                PortName = $Port; BaudRate = 1000000; DataBits = 8
                Parity = 'None'; StopBits = 'One'; Handshake = 'None'
                DtrEnable = $false; RtsEnable = $false
            }
            Framing = 'Delimiter'; AutoFrameGapMilliseconds = 20; DelimiterText = '\r\n'
            EncodingName = 'UTF-8'; HexDisplay = $true; SendLineEnding = 'CrLf'; SendHex = $false
        },
        [ordered]@{
            Name    = "$Port 115200 8N1"
            Serial  = [ordered]@{
                PortName = $Port; BaudRate = 115200; DataBits = 8
                Parity = 'None'; StopBits = 'One'; Handshake = 'None'
                DtrEnable = $false; RtsEnable = $false
            }
            Framing = 'Delimiter'; AutoFrameGapMilliseconds = 20; DelimiterText = '\r\n'
            EncodingName = 'UTF-8'; HexDisplay = $false; SendLineEnding = 'CrLf'; SendHex = $false
        }
    )
}
[System.IO.File]::WriteAllText(
    (Join-Path $settingsDir 'settings.json'),
    ($seed | ConvertTo-Json -Depth 6),
    (New-Object System.Text.UTF8Encoding($false)))

if (-not ('LayoutCheck' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LayoutCheck
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int awareness);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
'@
}
[void][LayoutCheck]::SetProcessDPIAware()
try { [void][LayoutCheck]::SetProcessDpiAwareness(2) } catch { }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]

function Get-Root { param([IntPtr]$Handle) return $AE::FromHandle($Handle) }

function Find-All {
    param($Root, $ControlType)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ControlType)
    return $Root.FindAll($TS::Descendants, $cond)
}

function Find-One {
    param($Root, [string]$Name, $ControlType)
    # AndCondition 至少要两个条件；只有名称条件时直接用 PropertyCondition
    if (-not $ControlType) {
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
        return $Root.FindFirst($TS::Descendants, $cond)
    }
    $conds = @(
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ControlType))
    )
    $and = New-Object System.Windows.Automation.AndCondition($conds)
    return $Root.FindFirst($TS::Descendants, $and)
}

function Find-Id {
    param($Root, [string]$Id)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $Id)
    return $Root.FindFirst($TS::Descendants, $cond)
}

function Find-ById-Pattern {
    param($Root, [string]$IdPattern, $ControlType)
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ControlType)
    foreach ($e in $Root.FindAll($TS::Descendants, $cond)) {
        if ($e.Current.AutomationId -match $IdPattern) { return $e }
    }
    return $null
}

function Invoke-Element { param($Element) $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Toggle-Element { param($Element) $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
function Set-Text { param($Element, [string]$Value) $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value) }
function Get-RectOf { param($Element) return $Element.Current.BoundingRectangle }

# 下拉项在自动化树里可能同时出现 ListItem 与内部 Text，只有 ListItem 支持 SelectionItem，
# 所以逐个同名元素尝试，并给弹出动画留出时间（和 M8 冒烟脚本踩过的是同一个坑）。
function Select-ComboItem {
    param($Root, [string]$Name, [int]$TimeoutSeconds = 10)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
        foreach ($element in $Root.FindAll($TS::Descendants, $cond)) {
            try {
                $pattern = $null
                if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
                    $pattern.Select()
                    return $true
                }
            }
            catch [System.Windows.Automation.ElementNotAvailableException] {
            }
        }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Save-Shot {
    param([IntPtr]$Handle, [string]$Name)
    $rect = New-Object LayoutCheck+RECT
    [void][LayoutCheck]::GetWindowRect($Handle, [ref]$rect)
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    $bitmap = New-Object System.Drawing.Bitmap($w, $h)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try { [void][LayoutCheck]::PrintWindow($Handle, $hdc, 2) }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    $bitmap.Save((Join-Path $OutDir "$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Output "SHOT|$Name|${w}x${h}"
}

$process = Start-Process -FilePath $ExePath -PassThru
$originalSize = $null
$handle = [IntPtr]::Zero
$failures = 0
try {
    # 等窗口真正出现并建好可视树，而不是固定睡 6 秒。单文件包首次运行要先解包，
    # 窗口可能 8~10 秒才出现；固定睡眠会把"窗口刚创建、绑定与首次布局还没跑完"的
    # 中间态当成最终界面 —— 实测踩过一次：那一刻搜索条的 Visibility 绑定还没生效，
    # 占位文本被误判成"状态栏重复显示搜索命中数"，白报一个失败项。
    $deadline = (Get-Date).AddSeconds(90)
    $handle = [IntPtr]::Zero
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        $handle = $process.MainWindowHandle
        if ($handle -ne [IntPtr]::Zero) {
            $ready = (Get-Root -Handle $handle).FindFirst($TS::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'LogViewControl')))
            if ($null -ne $ready) { break }
        }
        Start-Sleep -Milliseconds 500
    }
    if ($handle -eq [IntPtr]::Zero) { throw '主窗口尚未创建' }
    Start-Sleep -Milliseconds 1500
    $root = Get-Root -Handle $handle

    $rect = New-Object LayoutCheck+RECT
    [void][LayoutCheck]::GetWindowRect($handle, [ref]$rect)
    $originalSize = @{ W = $rect.Right - $rect.Left; H = $rect.Bottom - $rect.Top }

    # ---------- 1) 该在的控件都在 ----------
    Write-Output '== 控件存在性'
    foreach ($name in @('查找', '暂停显示', '自动换行', '自动滚动', '字号:', '保存', '清空',
                        '终端模式', '十六进制发送', '行尾:', '定时:', '定时发送', '发送文件', '发送',
                        '打开串口', '刷新', '保存预设', '删除预设', '选择目录', '打开目录', '端口设置', '接收设置',
                        '日志显示', '日志保存',
                        '模糊背景', '切换到深色')) {
        $found = ($null -ne (Find-One -Root $root -Name $name))
        Write-Output ("EXISTS|{0}={1}" -f $name, $found)
        if (-not $found) { $failures++ }
    }

    # ---------- 2) 删掉的死按钮 / 重复项不该再出现 ----------
    Write-Output '== 冗余项检查（期望全为 0）'
    $dead = 0
    foreach ($b in (Find-All -Root $root -ControlType $CT::Button)) {
        if ($b.Current.Name -in @('⚙', '?', 'A-', 'A+', 'SeriTerm')) { $dead++ }
    }
    Write-Output "REDUNDANT|死按钮与冗余文案=$dead"
    if ($dead -ne 0) { $failures++ }

    # 自动滚动只能有一处：以前侧栏和日志工具条各有一个开关，点了会互相打脸
    $autoscrollChecks = 0
    foreach ($c in (Find-All -Root $root -ControlType $CT::CheckBox)) {
        if ($c.Current.Name -eq '自动滚动') { $autoscrollChecks++ }
    }
    $autoscrollButtons = 0
    foreach ($b in (Find-All -Root $root -ControlType $CT::Button)) {
        if ($b.Current.Name -eq '自动滚动') { $autoscrollButtons++ }
    }
    $autoscrollToggle = Find-Id -Root $root -Id 'AutoScrollToggle'
    Write-Output "REDUNDANT|自动滚动复选框=$autoscrollChecks|自动滚动按钮=$autoscrollButtons|AutoScrollToggle=$($null -ne $autoscrollToggle)"
    if ($autoscrollChecks -ne 1 -or $autoscrollButtons -ne 0 -or -not $autoscrollToggle) { $failures++ }

    # 显示类控件确实在左栏：右边界不超过日志区左边界（"挪到左侧"这件事的可复现证据）
    Write-Output '== 控件归属：显示类在左栏'
    $logView = Find-Id -Root $root -Id 'LogViewControl'
    if ($null -eq $logView) {
        Write-Output 'OWNER|找不到 LogViewControl'
        $failures++
    } else {
        $logLeft = [math]::Round((Get-RectOf $logView).Left, 0)
        foreach ($name in @('查找', '暂停显示', '自动换行', '自动滚动', '字号:', '保存', '清空', '查找收藏:')) {
            $element = Find-One -Root $root -Name $name
            $right = -1
            if ($element) { $right = [math]::Round((Get-RectOf $element).Right, 0) }
            $inLeft = $right -ge 0 -and $right -le $logLeft
            Write-Output ("OWNER|{0}|右边界={1} 日志区左边界={2} 在左栏={3}" -f $name, $right, $logLeft, $inLeft)
            if (-not $inLeft) { $failures++ }
        }
    }

    # 状态栏不再重复显示搜索命中数：搜索未打开时应当一个都找不到
    $searchStatus = 0
    foreach ($t in (Find-All -Root $root -ControlType $CT::Text)) {
        if ($t.Current.Name -eq '输入关键字开始查找') { $searchStatus++ }
    }
    Write-Output "REDUNDANT|未搜索时的搜索状态文本=$searchStatus"
    if ($searchStatus -ne 0) { $failures++ }

    # ---------- 3) 字号下拉框真的改字号 ----------
    Write-Output '== 字号下拉框'
    $fontCombo = Find-Id -Root $root -Id 'FontSizeCombo'
    $openButton = Find-One -Root $root -Name '打开串口' -ControlType $CT::Button
    Invoke-Element $openButton
    Start-Sleep -Milliseconds 1500
    $sendBox = Find-Id -Root $root -Id 'SendTextBox'
    Set-Text $sendBox 'LAYOUT-CHECK-LINE'
    Invoke-Element (Find-One -Root $root -Name '发送' -ControlType $CT::Button)
    Start-Sleep -Milliseconds 1200

    $logList = Find-Id -Root $root -Id 'LogList'
    $items = $logList.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)
    $heightBefore = if ($items.Count -gt 0) { [math]::Round($items[0].Current.BoundingRectangle.Height, 1) } else { 0 }
    Write-Output "FONT|行数=$($items.Count)|13号字行高=$heightBefore"

    $fontCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 500
    if (Select-ComboItem -Root $root -Name '20') {
        Write-Output 'FONT|已选择 20'
    } else {
        Write-Output 'FONT|下拉项里没找到 20'
        $failures++
    }
    Start-Sleep -Milliseconds 900
    $items = $logList.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)
    $heightAfter = if ($items.Count -gt 0) { [math]::Round($items[0].Current.BoundingRectangle.Height, 1) } else { 0 }
    Write-Output "FONT|20号字行高=$heightAfter"
    if (-not ($heightAfter -gt $heightBefore)) { $failures++; Write-Output 'FONT|字号没有生效' }
    Save-Shot -Handle $handle -Name '01-font20'

    # 还原成 13
    $fontCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 500
    $null = Select-ComboItem -Root $root -Name '13'
    Start-Sleep -Milliseconds 700

    # ---------- 4) 预设占位符与套用 ----------
    Write-Output '== 预设'
    $placeholder = Find-One -Root $root -Name '未选择预设' -ControlType $CT::Text
    Write-Output "PRESET|占位提示=$($null -ne $placeholder)"
    if (-not $placeholder) { $failures++ }

    $presetCombo = Find-Id -Root $root -Id 'PresetCombo'
    $presetCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 600
    $picked = Select-ComboItem -Root $root -Name "$Port 1000000 8N1"
    if ($picked) {
        Write-Output "PRESET|已套用=$Port 1000000 8N1"
        Start-Sleep -Milliseconds 900
    } else {
        Write-Output 'PRESET|下拉里没有该预设'
        $failures++
    }
    $placeholder = Find-One -Root $root -Name '未选择预设' -ControlType $CT::Text
    Write-Output "PRESET|套用后占位提示=$($null -ne $placeholder)"
    if ($placeholder) { $failures++ }

    # 套用预设要真的改到连接参数：预设里是 1000000，读波特率输入框验证
    # （可编辑 ComboBox 的值在 PART_EditableTextBox 这个子元素上）
    $baud = ''
    foreach ($element in $root.FindAll($TS::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'PART_EditableTextBox')))) {
        $pattern = $null
        if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
            $baud = $pattern.Current.Value
            break
        }
    }
    Write-Output "PRESET|套用后波特率=$baud"
    if ($baud -ne '1000000') { $failures++ }

    # ---------- 5) 终端模式的子选项与发送区不越界 ----------
    Write-Output '== 终端模式子选项'
    Toggle-Element (Find-Id -Root $root -Id 'TerminalModeCheck')
    Start-Sleep -Milliseconds 800
    $echo = Find-One -Root $root -Name '本地回显' -ControlType $CT::CheckBox
    $del = Find-One -Root $root -Name '退格发 0x7F' -ControlType $CT::CheckBox
    Write-Output "TERMINAL|本地回显=$($null -ne $echo)|退格发0x7F=$($null -ne $del)"
    if (-not $echo -or -not $del) { $failures++ }
    Save-Shot -Handle $handle -Name '02-terminal-mode'

    [void][LayoutCheck]::GetWindowRect($handle, [ref]$rect)
    $sendRect = Get-RectOf (Find-One -Root $root -Name '发送' -ControlType $CT::Button)
    $inside = $sendRect.Bottom -le $rect.Bottom
    Write-Output ("BOUNDS|发送按钮底边={0:0} 窗口底边={1:0} 在窗口内={2}" -f $sendRect.Bottom, $rect.Bottom, $inside)
    if (-not $inside) { $failures++ }

    Toggle-Element (Find-Id -Root $root -Id 'TerminalModeCheck')
    Start-Sleep -Milliseconds 600

    # ---------- 6) 暂停显示 ----------
    Write-Output '== 暂停显示'
    Invoke-Element (Find-One -Root $root -Name '暂停显示' -ControlType $CT::Button)
    Start-Sleep -Milliseconds 700
    $resume = Find-One -Root $root -Name '继续显示' -ControlType $CT::Button
    Write-Output "PAUSE|切换后出现继续显示=$($null -ne $resume)"
    if (-not $resume) { $failures++ }
    Invoke-Element $resume
    Start-Sleep -Milliseconds 600

    # ---------- 7) 清空 ----------
    Write-Output '== 清空'
    $before = $logList.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition).Count
    Invoke-Element (Find-One -Root $root -Name '清空' -ControlType $CT::Button)
    Start-Sleep -Milliseconds 900
    $after = $logList.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition).Count
    Write-Output "CLEAR|清空前=$before 清空后=$after"
    if ($after -ge $before) { $failures++ }

    Invoke-Element (Find-One -Root $root -Name '关闭串口' -ControlType $CT::Button)
    Start-Sleep -Milliseconds 900
    Save-Shot -Handle $handle -Name '03-final'

    # ---------- 7.5) 搜索条：命中数只显示一次（原来搜索条和状态栏各显示一份） ----------
    Write-Output '== 搜索'
    Invoke-Element (Find-One -Root $root -Name '查找' -ControlType $CT::Button)
    Start-Sleep -Milliseconds 700
    $searchBox = Find-Id -Root $root -Id 'SearchBox'
    if ($searchBox) {
        Set-Text $searchBox 'CHECK'
        Start-Sleep -Milliseconds 900
        $statusCount = 0
        foreach ($t in (Find-All -Root $root -ControlType $CT::Text)) {
            if ($t.Current.Name -match '命中|匹配|找不到') { $statusCount++ }
        }
        Write-Output "SEARCH|命中数文本出现次数=$statusCount（期望 1）"
        if ($statusCount -ne 1) { $failures++ }
        Save-Shot -Handle $handle -Name '05-search'
        Invoke-Element (Find-One -Root $root -Name '✕' -ControlType $CT::Button)
        Start-Sleep -Milliseconds 600
    } else {
        Write-Output 'SEARCH|找不到搜索输入框'
        $failures++
    }

    # ---------- 8) 窄窗口：发送区选项行应当折行而不是被裁掉 ----------
    Write-Output '== 窄窗口（最小宽度）'
    $restore = $originalSize
    [void][LayoutCheck]::SetWindowPos($handle, [IntPtr]::Zero, 10, 10, 1380, 1200, 0x0004)
    Start-Sleep -Milliseconds 1200
    [void][LayoutCheck]::GetWindowRect($handle, [ref]$rect)
    $fileButton = Find-One -Root $root -Name '发送文件' -ControlType $CT::Button
    $fileRect = Get-RectOf $fileButton
    $inside = ($fileRect.Right -le $rect.Right) -and ($fileRect.Bottom -le $rect.Bottom)
    Write-Output ("NARROW|发送文件右={0:0} 窗口右={1:0} 在窗口内={2}" -f $fileRect.Right, $rect.Right, $inside)
    if (-not $inside) { $failures++ }
    Save-Shot -Handle $handle -Name '04-narrow'
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        if ($originalSize -and $handle -ne [IntPtr]::Zero) {
            [void][LayoutCheck]::SetWindowPos($handle, [IntPtr]::Zero, 10, 10, $originalSize.W, $originalSize.H, 0x0004)
            Start-Sleep -Milliseconds 500
            $process.CloseMainWindow() | Out-Null
            if (-not $process.WaitForExit(5000)) { $process.Kill() }
        } else {
            $process.Kill()
        }
    }
    Write-Output "SUMMARY|失败项=$failures"
}
