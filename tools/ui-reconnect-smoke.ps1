# 真机拔插冒烟：验证 USB-TTL 被拔出时的故障诊断与自动重连。
#
# 背景（真机反馈的 bug）：拔线时 .NET 抛的是 UnauthorizedAccessException(ERROR_ACCESS_DENIED)，
#   与"端口被别的程序占用"同码不同因。旧实现把它报成"设备可能被其它程序抢占"，并弹模态框挡住界面。
#
# 本脚本：打开串口后等人手动拔线（脚本会提示），全程轮询界面日志，判定三件事：
#   1) 提示文案说的是"设备已被拔出"而不是"其它程序独占"；
#   2) 开启自动重连时不得弹出"串口连接中断"模态框；
#   3) 设备插回后自动重连成功。
#
# 需要 Windows PowerShell 5.1 运行；本文件必须保存为"带 BOM 的 UTF-8"。

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Port = 'COM5',
    [int]$WaitSeconds = 120,
    [int]$BaudRate = 1000000
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

if (-not ('ReconnectCap' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ReconnectCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
}

[void][ReconnectCap]::SetProcessDPIAware()

if (-not (Test-Path $ExePath)) { throw "找不到可执行文件：$ExePath" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Get-Process -Name 'SeriTerm' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$portNames = [System.IO.Ports.SerialPort]::GetPortNames()
if ($portNames -notcontains $Port) {
    Write-Output "SKIP: 未检测到 $Port，跳过拔插冒烟。"
    exit 0
}

# 预置配置：自动重连开启、自动滚动开启、显示时间戳，其余保持默认
$settingsDir = Join-Path $env:APPDATA 'SeriTerm'
New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null

$settings = [ordered]@{
    Theme                    = 'Dark'
    LastSerial               = [ordered]@{
        PortName = $Port; BaudRate = $BaudRate; DataBits = 8
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
    Presets                  = @()
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

function Invoke-Element {
    param($Element, [string]$What)
    if ($null -eq $Element) { throw "界面上找不到控件：$What" }
    ($Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Save-WindowShot {
    param([int]$ProcessId, [string]$Path)

    $handle = (Get-Process -Id $ProcessId).MainWindowHandle
    if ($handle -eq [IntPtr]::Zero) { return }

    $rect = New-Object ReconnectCap+RECT
    [void][ReconnectCap]::GetWindowRect($handle, [ref]$rect)
    $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try { [void][ReconnectCap]::PrintWindow($handle, $hdc, 2) }
    finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# 返回界面上的全部文本（日志行的 TextBlock 会以完整行内容出现）
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

# 是否存在模态对话框窗口（这是本次要验的关键点之一）
function Get-DialogWindows {
    param([int]$ProcessId)

    $windows = @()
    foreach ($element in @(Get-ProcessElements -ProcessId $ProcessId)) {
        try {
            if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -and $element.Current.Name) {
                $windows += $element.Current.Name
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
    }
    return $windows
}

$process = Start-Process -FilePath $ExePath -PassThru
$processId = $process.Id
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

$faultLine = ''
$waitingLine = ''
$recoveredLine = ''
$dialogSeen = ''
$dialogClosed = $false

try {
    Write-Output '== 等待界面就绪'
    $null = Wait-ForControl -ProcessId $processId -Text '打开' -TimeoutSeconds 30

    Write-Output "== 打开串口 $Port @ $BaudRate"
    Invoke-Element (Wait-ForControl -ProcessId $processId -Text '打开') '打开'

    # 等"串口已打开"这行日志出现，确认真的打开了
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-AllTexts -ProcessId $processId) | Where-Object { $_ -match '串口已打开' }) { break }
        Start-Sleep -Milliseconds 300
    }

    Write-Output ''
    Write-Output '========================================================'
    Write-Output "READY|请现在拔掉 USB-TTL（$Port），等 5 秒以上再插回。"
    Write-Output "READY|脚本会持续观察 $WaitSeconds 秒，插回后请不要动，等它自己恢复。"
    Write-Output '========================================================'
    Write-Output ''

    $deadline = (Get-Date).AddSeconds($WaitSeconds)

    while ((Get-Date) -lt $deadline) {
        $texts = @(Get-AllTexts -ProcessId $processId)

        # 1) 故障提示文案
        if (-not $faultLine) {
            $hit = $texts | Where-Object { $_ -match '链路中断' } | Select-Object -First 1
            if ($hit) {
                $faultLine = $hit
                Write-Output ("FAULT|t={0:N1}s|{1}" -f $stopwatch.Elapsed.TotalSeconds, $hit)
            }
        }

        if (-not $waitingLine) {
            $hit = $texts | Where-Object { $_ -match '等待设备插入|已断开' } | Select-Object -First 1
            if ($hit) {
                $waitingLine = $hit
                Write-Output ("WAITMSG|t={0:N1}s|{1}" -f $stopwatch.Elapsed.TotalSeconds, $hit)
            }
        }

        # 2) 模态框
        $dialogs = @(Get-DialogWindows -ProcessId $processId | Where-Object { $_ -match '串口连接中断|自动重连已停止' })
        if ($dialogs.Count -gt 0) {
            if (-not $dialogSeen) {
                $dialogSeen = ($dialogs -join ' + ')
                Write-Output ("DIALOG|t={0:N1}s|{1}" -f $stopwatch.Elapsed.TotalSeconds, $dialogSeen)
                Save-WindowShot -ProcessId $processId -Path (Join-Path $OutDir 'ui-reconnect-dialog.png')
            }

            # 关掉它，避免挡住后续观察
            if (-not $dialogClosed) {
                try {
                    Invoke-Element (Wait-ForControl -ProcessId $processId -Text '确定' -TimeoutSeconds 3) '确定'
                    $dialogClosed = $true
                }
                catch {
                }
            }
        }

        # 3) 恢复
        if ($faultLine -and -not $recoveredLine) {
            $hit = $texts | Where-Object { $_ -match '已重连|链路已恢复' } | Select-Object -First 1
            if ($hit) {
                $recoveredLine = $hit
                Write-Output ("RECOVERED|t={0:N1}s|{1}" -f $stopwatch.Elapsed.TotalSeconds, $hit)
                break
            }
        }

        Start-Sleep -Milliseconds 400
    }

    Save-WindowShot -ProcessId $processId -Path (Join-Path $OutDir 'ui-reconnect-final.png')

    Write-Output ''
    Write-Output '== 与拔插相关的界面文本 =='
    foreach ($text in @(Get-AllTexts -ProcessId $processId)) {
        if ($text -match '链路|重连|设备|拔出|COM5|已打开|故障') {
            Write-Output "TEXT|$text"
        }
    }

    $classify = if ($faultLine -match '拔出') { '设备拔出' }
                elseif ($faultLine -match '其它程序') { '被占用(误判)' }
                elseif ($faultLine) { '其它' }
                else { '无故障' }

    $modal = if ($dialogSeen) { "出现($dialogSeen)" } else { '未出现' }

    $recover = if ($recoveredLine) { '成功' } else { '未恢复' }

    Write-Output ''
    Write-Output "CHECK|故障判定=$classify 模态框=$modal 自动重连=$recover"
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
}

