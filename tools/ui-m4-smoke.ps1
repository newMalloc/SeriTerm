# M3/M4 界面冒烟：验证分隔符断帧、HEX 发送、定时发送在真实界面上按预期工作。
#
# 前置：脚本会先把配置写成"分隔符断帧(CRLF) + HEX 发送 + 行尾无"，再启动应用。
# 发送内容 41 0D 0A 42 0D 0A 在分隔符断帧下应显示成两行 "A" 与 "B"；
# 若断帧失效则会显示成一行 "A B" —— 这就是本脚本的判定依据。
#
# 需要 Windows PowerShell 5.1（powershell.exe）运行；本文件必须保存为"带 BOM 的 UTF-8"。
#
# 踩坑记录（很重要）：
#   1) 不能缓存"刚出现"的窗口元素。WPF 内容建立之前拿到的往往是旧式 HWND 代理，
#      它的子树永远是空的（FindAll 返回 0 个元素）。必须每次从 RootElement 按进程号重新查找。
#   2) 也不要依赖 FindFirst(PropertyCondition) 找控件：某些 peer 的 Name/AutomationId 是延迟计算的。
#      统一用"枚举 + 手动比较"最稳。
#
# 用法：powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ui-m4-smoke.ps1 -ExePath <exe> -OutDir artifacts

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Port = 'COM5',
    [string]$HexPayload = '41 0D 0A 42 0D 0A',
    [switch]$SkipSettingsSeed
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

if (-not ('M4Cap' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class M4Cap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
}

[void][M4Cap]::SetProcessDPIAware()

if (-not (Test-Path $ExePath)) { throw "找不到可执行文件：$ExePath" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Get-Process -Name 'SeriTerm' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$portNames = [System.IO.Ports.SerialPort]::GetPortNames()
if ($portNames -notcontains $Port) {
    Write-Output "SKIP: 未检测到 $Port，跳过 M4 界面冒烟。"
    exit 0
}

if (-not $SkipSettingsSeed) {
    $settingsDir = Join-Path $env:APPDATA 'SeriTerm'
    New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null

    $settings = [ordered]@{
        Theme                    = 'Dark'
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
        SendHex                  = $true
        SendLineEnding           = 'None'
        TimedSendIntervalSeconds = 0.2
    }

    # 必须写"不带 BOM"的 UTF-8，否则 JSON 反序列化会因 BOM 失败而退回默认配置
    $json = $settings | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText(
        (Join-Path $settingsDir 'settings.json'),
        $json,
        (New-Object System.Text.UTF8Encoding($false)))
}

function Get-ProcessElements {
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    try {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
        return [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants, $condition)
    }
    catch [System.Windows.Automation.ElementNotAvailableException] {
        # UIA 偶发"目标元素的对应 UI 不再可用"（窗口正在重绘/关闭），当作空结果，下一轮轮询会重试
        return @()
    }
}

function Wait-ForControl {
    param(
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [string]$AutomationId,
        [string]$Text,
        [int]$TimeoutSeconds = 25
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    # 原子查询条件：一次服务端查找，比"枚举所有元素再逐个读属性"稳得多。
    # 日志区高频刷新时枚举很容易撞上失效元素而整体失败，这里先走原子查询。
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $targetCondition = if ($AutomationId) {
        New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    }
    else {
        New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Text)
    }
    $atomicCondition = New-Object System.Windows.Automation.AndCondition($processCondition, $targetCondition)

    while ((Get-Date) -lt $deadline) {
        try {
            $found = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants, $atomicCondition)

            if ($null -ne $found) { return $found }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }

        $elements = @(Get-ProcessElements -ProcessId $ProcessId)

        foreach ($element in $elements) {
            try {
                if ($AutomationId) {
                    if ($element.Current.AutomationId -eq $AutomationId) { return $element }
                }
                elseif ($Text -and $element.Current.Name -eq $Text) {
                    return $element
                }
            }
            catch [System.Windows.Automation.ElementNotAvailableException] {
                # 元素在枚举后失效，跳过即可
            }
        }

        Start-Sleep -Milliseconds 400
    }

    $what = if ($AutomationId) { $AutomationId } else { $Text }
    throw "等待控件超时：$what"
}

function Invoke-Element {
    param($Element, [string]$What)
    if ($null -eq $Element) { throw "界面上找不到控件：$What" }
    ($Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Set-ElementText {
    param($Element, [string]$Text, [string]$What)
    if ($null -eq $Element) { throw "界面上找不到控件：$What" }
    ($Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Text)
}

function Save-WindowShot {
    param([int]$ProcessId, [string]$Path)

    $handle = (Get-Process -Id $ProcessId).MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw '主窗口句柄无效' }

    $rect = New-Object M4Cap+RECT
    [void][M4Cap]::GetWindowRect($handle, [ref]$rect)
    $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try { [void][M4Cap]::PrintWindow($handle, $hdc, 2) }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function Get-AllTexts {
    param([int]$ProcessId)
    $texts = @()
    foreach ($element in Get-ProcessElements -ProcessId $ProcessId) {
        if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and $element.Current.Name) {
            $texts += $element.Current.Name
        }
    }
    return $texts
}

$process = Start-Process -FilePath $ExePath -PassThru
$processId = $process.Id

try {
    Write-Output '== 等待界面就绪'
    $null = Wait-ForControl -ProcessId $processId -Text '打开' -TimeoutSeconds 30

    Write-Output "== 填入 HEX 发送内容：$HexPayload"
    Set-ElementText (Wait-ForControl -ProcessId $processId -AutomationId 'SendTextBox') $HexPayload 'SendTextBox'

    Write-Output "== 打开串口 $Port"
    Invoke-Element (Wait-ForControl -ProcessId $processId -Text '打开') '打开'
    Start-Sleep -Seconds 2

    Write-Output '== 发送一次，验证分隔符断帧'
    Invoke-Element (Wait-ForControl -ProcessId $processId -Text '发送') '发送'
    Start-Sleep -Seconds 1
    Save-WindowShot -ProcessId $processId -Path (Join-Path $OutDir 'ui-m4-delimiter.png')

    Write-Output '== 定时发送（0.2 秒）'
    Invoke-Element (Wait-ForControl -ProcessId $processId -Text '定时发送') '定时发送'
    Start-Sleep -Milliseconds 1600
    Save-WindowShot -ProcessId $processId -Path (Join-Path $OutDir 'ui-m4-timed.png')
    Invoke-Element (Wait-ForControl -ProcessId $processId -Text '停止定时') '停止定时'
    Start-Sleep -Milliseconds 500

    Write-Output '== 界面文本清单 =='
    $texts = Get-AllTexts -ProcessId $processId
    $texts | ForEach-Object { Write-Output "TEXT|$_" }

    # 判定：分隔符断帧下 Rx 应出现独立的 "A" 与 "B" 两行；断帧失效会合成 "A B"
    $hasA = @($texts | Where-Object { $_ -eq 'A' }).Count
    $hasB = @($texts | Where-Object { $_ -eq 'B' }).Count
    $merged = @($texts | Where-Object { $_ -match '^A\s+B$' }).Count

    Write-Output "CHECK|独立Rx行A=$hasA 独立Rx行B=$hasB 合并行A_B=$merged"
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
}
