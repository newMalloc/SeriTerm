# M8 界面冒烟：验证配置预设（★）——从下拉框选择预设后，连接与显示配置应随之切换。
#
# 做法：先在配置里塞两个预设（第二个刻意用不同的波特率/断帧/HEX 显示），
#       启动后在"预设"下拉框里选中它，再读波特率输入框与"十六进制显示"复选框来判定。
#
# 需要 Windows PowerShell 5.1 运行；本文件必须保存为"带 BOM 的 UTF-8"。

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Port = 'COM5'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

if (-not ('M8Cap' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class M8Cap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
}

[void][M8Cap]::SetProcessDPIAware()

if (-not (Test-Path $ExePath)) { throw "找不到可执行文件：$ExePath" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Get-Process -Name 'SeriTerm' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$portNames = [System.IO.Ports.SerialPort]::GetPortNames()
if ($portNames -notcontains $Port) {
    Write-Output "SKIP: 未检测到 $Port，跳过 M8 界面冒烟。"
    exit 0
}

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
    WindowHeight             = 900
    WindowMaximized          = $false
    AutoScroll               = $true
    HexDisplay               = $false
    EncodingName             = 'UTF-8'
    Framing                  = 'Gap'
    AutoFrameGapMilliseconds = 20
    DelimiterText            = '\r\n'
    ShowTimestamp            = $true
    LineWrap                 = $true
    LogFontSize              = 13
    SendHex                  = $false
    SendLineEnding           = 'CrLf'
    TimedSendIntervalSeconds = 1.0
    SaveLogToFile            = $false
    SaveRawLog               = $false
    AutoReconnect            = $true
    AutoOpenOnStartup        = $false
    TerminalLocalEcho        = $false
    TerminalBackspaceSendsDel = $true
    Presets                  = @(
        [ordered]@{
            Name = '慢速调试 9600'
            Serial = [ordered]@{
                PortName = $Port; BaudRate = 9600; DataBits = 8
                Parity = 'None'; StopBits = 'One'; Handshake = 'None'
                DtrEnable = $false; RtsEnable = $false
            }
            Framing = 'Gap'; AutoFrameGapMilliseconds = 20; DelimiterText = '\r\n'
            EncodingName = 'UTF-8'; HexDisplay = $false
            SendLineEnding = 'CrLf'; SendHex = $false
        },
        [ordered]@{
            Name = '高速十六进制'
            Serial = [ordered]@{
                PortName = $Port; BaudRate = 921600; DataBits = 8
                Parity = 'None'; StopBits = 'One'; Handshake = 'None'
                DtrEnable = $false; RtsEnable = $false
            }
            Framing = 'Delimiter'; AutoFrameGapMilliseconds = 5; DelimiterText = 'hex:0D 0A'
            EncodingName = 'GB2312'; HexDisplay = $true
            SendLineEnding = 'None'; SendHex = $true
        }
    )
}

$json = $settings | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText((Join-Path $settingsDir 'settings.json'), $json, (New-Object System.Text.UTF8Encoding($false)))

function Get-ProcessElements {
    param([Parameter(Mandatory = $true)][int]$ProcessId)

    try {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
        return [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants, $condition)
    }
    catch [System.Windows.Automation.ElementNotAvailableException] {
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

    while ((Get-Date) -lt $deadline) {
        foreach ($element in @(Get-ProcessElements -ProcessId $ProcessId)) {
            try {
                if ($AutomationId) {
                    if ($element.Current.AutomationId -eq $AutomationId) { return $element }
                }
                elseif ($Text -and $element.Current.Name -eq $Text) {
                    return $element
                }
            }
            catch [System.Windows.Automation.ElementNotAvailableException] {
            }
        }

        Start-Sleep -Milliseconds 400
    }

    $what = if ($AutomationId) { $AutomationId } else { $Text }
    throw "等待控件超时：$what"
}

function Save-WindowShot {
    param([int]$ProcessId, [string]$Path)

    $handle = (Get-Process -Id $ProcessId).MainWindowHandle
    $rect = New-Object M8Cap+RECT
    [void][M8Cap]::GetWindowRect($handle, [ref]$rect)
    $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try { [void][M8Cap]::PrintWindow($handle, $hdc, 2) }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

$process = Start-Process -FilePath $ExePath -PassThru
$processId = $process.Id

try {
    Write-Output '== 等待界面就绪'
    $null = Wait-ForControl -ProcessId $processId -Text '打开串口' -TimeoutSeconds 30

    $presetCombo = Wait-ForControl -ProcessId $processId -AutomationId 'PresetCombo'

    Write-Output '== 展开预设下拉框'
    $expand = $presetCombo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 800

    Write-Output '== 选择预设「高速十六进制」'
    # 下拉项在自动化树里可能同时出现 ListItem 与内部 Text，只有 ListItem 支持 SelectionItem；
    # 因此逐个尝试而不是找一个同名的元素就用。
    $selected = $false
    $deadline = (Get-Date).AddSeconds(10)

    while (-not $selected -and (Get-Date) -lt $deadline) {
        foreach ($element in @(Get-ProcessElements -ProcessId $processId)) {
            try {
                if ($element.Current.Name -ne '高速十六进制') { continue }

                $pattern = $null
                if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
                    $pattern.Select()
                    $selected = $true
                    break
                }
            }
            catch [System.Windows.Automation.ElementNotAvailableException] {
            }
        }

        if (-not $selected) { Start-Sleep -Milliseconds 300 }
    }

    if (-not $selected) { throw '下拉项里没有找到支持选择模式的「高速十六进制」' }
    Start-Sleep -Milliseconds 900

    # 判定依据：预设里的波特率 921600 与"十六进制显示"应当已生效
    $baud = ''
    foreach ($element in @(Get-ProcessElements -ProcessId $processId)) {
        try {
            if ($element.Current.AutomationId -ne 'PART_EditableTextBox') { continue }

            $pattern = $null
            if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
                $baud = $pattern.Current.Value
                break
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
    }

    $hexCheck = Wait-ForControl -ProcessId $processId -AutomationId 'HexDisplayCheck' -TimeoutSeconds 10
    $hexState = ($hexCheck.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState

    Save-WindowShot -ProcessId $processId -Path (Join-Path $OutDir 'ui-m8-presets.png')

    Write-Output "CHECK|波特率=$baud 十六进制显示=$hexState"
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
}
