# SeriTerm MCP 冒烟脚本
#
# 干什么：像 AI 客户端那样，用 stdio 把 `SeriTerm.exe --mcp-stdio` 拉起来，
# 依次调用 initialize / tools/list / serial_get_status / serial_open / serial_write /
# serial_read_frames / serial_wait_for_pattern，并把每一步的结果打出来。
#
# 为什么要它：MCP 这条链路跨了三个进程（AI 客户端 → 桥 → 界面进程），
# 单测只能覆盖到"桥"和"派发层"，真正跑起来还得有人从外面说一遍话。
# 发版前跑一次，比事后听用户说"AI 连不上"要便宜得多。
#
# 用法：
#   pwsh -File tools/mcp-smoke.ps1 -Exe src/SeriTerm.App/bin/Debug/net8.0-windows/SeriTerm.exe
#   pwsh -File tools/mcp-smoke.ps1 -Exe <exe> -ExpectFullPermission     # 完全权限档下应能发送
#
# 注意：界面进程需要已经在运行（脚本不会替你去点窗口）；回环验证要求 TX–RX 短接。

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Pattern = 'seriterm-mcp-smoke',
    [switch]$ExpectFullPermission,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Exe)) {
    throw "找不到 $Exe（先 dotnet build）"
}

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = (Resolve-Path -LiteralPath $Exe).Path
$psi.ArgumentList.Add('--mcp-stdio')
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$psi.StandardInputEncoding = [System.Text.Encoding]::UTF8
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

$bridge = [System.Diagnostics.Process]::Start($psi)
$nextId = 0

function Send-Message {
    param([hashtable]$Message)

    $json = $Message | ConvertTo-Json -Compress -Depth 24
    $bridge.StandardInput.WriteLine($json)
    $bridge.StandardInput.Flush()
}

function Receive-Result {
    param([int]$Id)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        $line = $bridge.StandardOutput.ReadLine()

        if ($null -eq $line) {
            throw "桥进程的 stdout 已关闭（退出码 $($bridge.ExitCode)）"
        }

        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $message = $line | ConvertFrom-Json

        if ($message.id -eq $Id) {
            return $message
        }
    }

    throw "等 id=$Id 的响应超时（$TimeoutSeconds 秒）"
}

function Invoke-Tool {
    param([string]$Name, [hashtable]$Arguments = @{})

    $script:nextId++
    $id = $script:nextId

    Send-Message @{
        jsonrpc = '2.0'
        id      = $id
        method  = 'tools/call'
        params  = @{ name = $Name; arguments = $Arguments }
    }

    $response = Receive-Result -Id $id

    if ($response.error) {
        throw "$Name 返回协议错误：$($response.error.message)"
    }

    $isError = [bool]$response.result.isError
    $text = $response.result.content[0].text

    [pscustomobject]@{
        Tool    = $Name
        IsError = $isError
        Text    = $text
    }
}

function Show {
    param($Step, $Value)

    Write-Host ("--- {0}" -f $Step) -ForegroundColor Cyan

    if ($Value -is [string]) {
        Write-Host $Value
    }
    else {
        $Value | ConvertTo-Json -Depth 12
    }
}

try {
    # 1) initialize：这一步不依赖界面进程，必须成功
    $nextId++
    Send-Message @{
        jsonrpc = '2.0'
        id      = $nextId
        method  = 'initialize'
        params  = @{
            protocolVersion = '2025-06-18'
            capabilities    = @{}
            clientInfo      = @{ name = 'seriterm-smoke'; version = '1.0' }
        }
    }

    $init = Receive-Result -Id $nextId
    Show 'initialize' ([pscustomobject]@{
        protocolVersion = $init.result.protocolVersion
        serverName      = $init.result.serverInfo.name
        serverVersion   = $init.result.serverInfo.version
        toolsCapability = $init.result.capabilities.tools
        instructions    = ($init.result.instructions -split "`n")[0]
    })

    # 通知：按 MCP 约定不该有响应，这里只发不等
    Send-Message @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    # 2) tools/list
    $nextId++
    Send-Message @{ jsonrpc = '2.0'; id = $nextId; method = 'tools/list' }
    $tools = Receive-Result -Id $nextId
    $names = @($tools.result.tools | ForEach-Object { $_.name })
    Show 'tools/list' ([pscustomobject]@{ count = $names.Count; tools = $names })

    # 3) 状态
    $status = Invoke-Tool -Name 'serial_get_status'
    Show 'serial_get_status' $status.Text

    # 4) 打开串口（只读档下这一步应当被拒绝——那也是一种正确结果）
    $open = Invoke-Tool -Name 'serial_open'
    Show 'serial_open' ([pscustomobject]@{ isError = $open.IsError; text = $open.Text })

    if ($ExpectFullPermission -and $open.IsError) {
        throw "期望完全权限下能打开串口，但被拒绝了：$($open.Text)"
    }

    # 5) 发送（只读档下应被拒绝）
    $write = Invoke-Tool -Name 'serial_write' -Arguments @{ data = $Pattern; lineEnding = 'crlf' }
    Show 'serial_write' ([pscustomobject]@{ isError = $write.IsError; text = $write.Text })

    if ($ExpectFullPermission -and $write.IsError) {
        throw "期望完全权限下能发送，但被拒绝了：$($write.Text)"
    }

    if (-not $ExpectFullPermission -and -not $write.IsError) {
        throw '只读档下竟然发送成功了——权限护栏失效'
    }

    if (-not $write.IsError) {
        # 6) 回环：TX–RX 短接时刚发出去的内容应当作为 Rx 帧回来
        $wait = Invoke-Tool -Name 'serial_wait_for_pattern' -Arguments @{
            pattern   = $Pattern
            since     = 0
            timeoutMs = 3000
            context   = 1
        }

        Show 'serial_wait_for_pattern' $wait.Text

        if ($wait.IsError) {
            throw "等关键词失败：$($wait.Text)"
        }

        if ($wait.Text -notmatch '"matched": true') {
            Write-Warning '没有等到发出去的内容：确认 COM5 的 TX–RX 已短接、且波特率与界面一致'
        }

        # 7) 读帧
        $frames = Invoke-Tool -Name 'serial_read_frames' -Arguments @{ since = 0; limit = 10; direction = 'rx' }
        Show 'serial_read_frames' $frames.Text
    }

    Write-Host ''
    Write-Host '冒烟通过。' -ForegroundColor Green
    exit 0
}
finally {
    if (-not $bridge.HasExited) {
        $bridge.StandardInput.Close()

        if (-not $bridge.WaitForExit(5000)) {
            $bridge.Kill($true)
        }
    }

    $stderr = $bridge.StandardError.ReadToEnd()

    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Host '--- 桥进程的 stderr' -ForegroundColor Yellow
        Write-Host $stderr
    }
}
