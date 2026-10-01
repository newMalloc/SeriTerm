# M5 界面冒烟：验证终端模式——键盘输入直接进串口，并通过 COM5 回环看到设备回显。
#
# 做法：打开串口 → 勾选"终端模式" → 把焦点移到日志区（避开输入框）→ 用 SendKeys 敲 "AT" + 回车
#       → 日志里应出现一条 Tx（本地回显，整行一条）与一条 Rx（回环回来的 AT）。
#
# 需要 Windows PowerShell 5.1 运行；本文件必须保存为"带 BOM 的 UTF-8"。

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Port = 'COM5',
    [string]$Typed = 'AT'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not ('M5Cap' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class M5Cap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    // SendKeys 用 VK_PACKET 注入字符，WPF 会把它报成 ImeProcessed，连回车都会丢；
    // 特殊键改用真实的虚拟键注入，才能模拟真人按键。
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
'@
}

[void][M5Cap]::SetProcessDPIAware()

function Send-VirtualKey {
    param([byte]$VirtualKey)
    $keyUp = 0x0002
    [M5Cap]::keybd_event($VirtualKey, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 30
    [M5Cap]::keybd_event($VirtualKey, 0, $keyUp, [UIntPtr]::Zero)
}

if (-not (Test-Path $ExePath)) { throw "找不到可执行文件：$ExePath" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Get-Process -Name 'SeriTerm' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$portNames = [System.IO.Ports.SerialPort]::GetPortNames()
if ($portNames -notcontains $Port) {
    Write-Output "SKIP: 未检测到 $Port，跳过 M5 界面冒烟。"
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
    TerminalLocalEcho        = $true
    TerminalBackspaceSendsDel = $true
}

$json = $settings | ConvertTo-Json -Depth 5
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

function Invoke-Element {
    param($Element, [string]$What)
    if ($null -eq $Element) { throw "界面上找不到控件：$What" }
    ($Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Toggle-Element {
    param($Element, [string]$What)
    if ($null -eq $Element) { throw "界面上找不到控件：$What" }
    ($Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
}

function Save-WindowShot {
    param([int]$ProcessId, [string]$Path)

    $handle = (Get-Process -Id $ProcessId).MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) { throw '主窗口句柄无效' }

    $rect = New-Object M5Cap+RECT
    [void][M5Cap]::GetWindowRect($handle, [ref]$rect)
    $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try { [void][M5Cap]::PrintWindow($handle, $hdc, 2) }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function Get-AllTexts {
    param([int]$ProcessId)
    $texts = @()
    foreach ($element in @(Get-ProcessElements -ProcessId $ProcessId)) {
        try {
            if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and $element.Current.Name) {
                $texts += $element.Current.Name
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
    }
    return $texts
}

$process = Start-Process -FilePath $ExePath -PassThru
$processId = $process.Id

try {
    Write-Output '== 等待界面就绪'
    $null = Wait-ForControl -ProcessId $processId -Text '打开串口' -TimeoutSeconds 30

    Write-Output "== 打开串口 $Port"
    Invoke-Element (Wait-ForControl -ProcessId $processId -Text '打开串口') '打开'
    Start-Sleep -Seconds 2

    Write-Output '== 勾选终端模式'
    Toggle-Element (Wait-ForControl -ProcessId $processId -AutomationId 'TerminalModeCheck') '终端模式'
    Start-Sleep -Milliseconds 600

    Write-Output '== 把焦点移到日志区并敲入内容'
    $logList = Wait-ForControl -ProcessId $processId -AutomationId 'LogList'
    $handle = (Get-Process -Id $processId).MainWindowHandle
    [void][M5Cap]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 400

    $foreground = [M5Cap]::GetForegroundWindow()
    Write-Output ("KEYS|前台窗口=0x{0:X} 应用窗口=0x{1:X}" -f [int64]$foreground, [int64]$handle)

    $logList.SetFocus()
    Start-Sleep -Milliseconds 500

    [System.Windows.Forms.SendKeys]::SendWait($Typed)
    Start-Sleep -Milliseconds 500
    Send-VirtualKey -VirtualKey 0x0D          # VK_RETURN：模拟真实回车键
    Start-Sleep -Seconds 1

    Save-WindowShot -ProcessId $processId -Path (Join-Path $OutDir 'ui-m5-terminal.png')

    Write-Output '== 界面文本清单 =='
    $texts = Get-AllTexts -ProcessId $processId
    $texts | ForEach-Object { Write-Output "TEXT|$_" }

    # 判定依据：
    #   Tx 行 "AT"      —— 本地回显按整行记录（回车时才落一条）
    #   Rx 行 "A"/"T"   —— 回环回来的字符（逐键间隔超过断帧阈值，因此被切成两帧）
    #   "4 B" 出现两次  —— Rx/Tx 计数各 4 字节 = "AT" + CRLF
    $txLine = @($texts | Where-Object { $_ -eq 'AT' }).Count
    $rxA = @($texts | Where-Object { $_ -eq 'A' }).Count
    $rxT = @($texts | Where-Object { $_ -eq 'T' }).Count
    $fourBytes = @($texts | Where-Object { $_ -eq '4 B' }).Count

    Write-Output "CHECK|Tx整行AT=$txLine Rx字符A=$rxA Rx字符T=$rxT 4B计数元素=$fourBytes"
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
}
