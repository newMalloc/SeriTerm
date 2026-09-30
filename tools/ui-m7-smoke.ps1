# M7 界面冒烟：验证"将接收保存到文件"真的把数据写到磁盘，并顺带验证 M6 的"启动时自动打开串口"。
#
# 做法：
#   1) 先把配置写成"自动打开串口 + 保存文本日志 + 保存原始字节"，日志目录指向 artifacts 下的临时目录；
#   2) 启动应用（应当自动打开 COM5），发送两次；
#   3) 通过界面取消勾选"将接收保存到文件"来触发落盘 flush（直接杀进程会丢掉缓冲）；
#   4) 打印产物文件与内容，交由调用方核对。
#
# 需要 Windows PowerShell 5.1 运行；本文件必须保存为"带 BOM 的 UTF-8"。

param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Port = 'COM5',
    [string]$SendText = 'M7-LOG-CHECK'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not (Test-Path $ExePath)) { throw "找不到可执行文件：$ExePath" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$logDir = Join-Path $OutDir 'm7-logs'
if (Test-Path $logDir) { Remove-Item $logDir -Recurse -Force }
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

Get-Process -Name 'SeriTerm' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$portNames = [System.IO.Ports.SerialPort]::GetPortNames()
if ($portNames -notcontains $Port) {
    Write-Output "SKIP: 未检测到 $Port，跳过 M7 界面冒烟。"
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
    SaveLogToFile            = $true
    SaveRawLog               = $true
    LogDirectory             = $logDir
    AutoReconnect            = $true
    AutoOpenOnStartup        = $true
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

function Set-ElementText {
    param($Element, [string]$Text, [string]$What)
    if ($null -eq $Element) { throw "界面上找不到控件：$What" }
    ($Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Text)
}

function Toggle-Element {
    param($Element, [string]$What)
    if ($null -eq $Element) { throw "界面上找不到控件：$What" }
    ($Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
}

function Get-StatusTexts {
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
    Write-Output '== 等待界面就绪（并等待自动打开串口）'
    $null = Wait-ForControl -ProcessId $processId -Text '打开' -TimeoutSeconds 30

    # 已打开时按钮会变成"关闭"；等它出现即说明 AutoOpenOnStartup 生效
    $null = Wait-ForControl -ProcessId $processId -Text '关闭' -TimeoutSeconds 20

    $texts = Get-StatusTexts -ProcessId $processId
    $autoOpened = @($texts | Where-Object { $_ -eq '已打开' }).Count -gt 0
    Write-Output "CHECK|启动自动打开串口=$autoOpened"

    Write-Output "== 填入发送内容并发送两次：$SendText"
    Set-ElementText (Wait-ForControl -ProcessId $processId -AutomationId 'SendTextBox') $SendText 'SendTextBox'
    Invoke-Element (Wait-ForControl -ProcessId $processId -Text '发送') '发送'
    Start-Sleep -Milliseconds 400
    Invoke-Element (Wait-ForControl -ProcessId $processId -Text '发送') '发送'
    Start-Sleep -Milliseconds 600

    Write-Output '== 取消勾选"将接收保存到文件"以触发落盘'
    Toggle-Element (Wait-ForControl -ProcessId $processId -AutomationId 'SaveLogCheck') 'SaveLogCheck'
    Start-Sleep -Milliseconds 1200

    $texts = Get-StatusTexts -ProcessId $processId
    $logStatus = @($texts | Where-Object { $_ -like '日志 *' })
    Write-Output "CHECK|日志状态=$($logStatus -join ' | ')"
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
}

Write-Output '== 产物文件 =='
$files = @(Get-ChildItem -Path $logDir -File -ErrorAction SilentlyContinue)
foreach ($file in $files) {
    Write-Output "FILE|$($file.Name)|$($file.Length) 字节"
}

$textLog = $files | Where-Object { $_.Extension -eq '.log' } | Select-Object -First 1
if ($textLog) {
    Write-Output '== 文本日志内容 =='
    Get-Content $textLog.FullName | Select-Object -First 10 | ForEach-Object { Write-Output "LOG|$_" }
}

$rawLog = $files | Where-Object { $_.Extension -eq '.bin' } | Select-Object -First 1
if ($rawLog) {
    $bytes = [System.IO.File]::ReadAllBytes($rawLog.FullName)
    Write-Output "CHECK|原始日志字节数=$($bytes.Length)"
}
