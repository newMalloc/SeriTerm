# AI 接入（MCP）

SeriTerm 内置 [MCP](https://modelcontextprotocol.io)（Model Context Protocol）服务端。
接上之后，Claude Desktop / Cursor / VS Code 这类 AI 客户端里的模型**能直接读到你设备的输出**，
不必再"把日志复制粘贴给它"。

它解决的不是"少打几个字"，而是两件粘贴做不到的事：

- **能等**：`serial_wait_for_pattern` 会一直等到设备打印出指定内容才返回——可以等重启后的第一行、等 `OK`、等 `panic`；
- **是帧不是字节**：AI 看到的是按你的断帧设置切好、带到达时间与方向的数据，还带 `nextCursor` 游标，
  按游标续读既不重复也不漏。

## 怎么接

1. 打开 SeriTerm，**保持窗口开着**（串口在它手里）；
2. 左栏「AI 接入」→ 点「复制配置」；
3. 把这段 JSON 粘进 AI 客户端的 MCP 设置（Claude Desktop 是 `claude_desktop_config.json`，
   Cursor / VS Code 是各自的 `mcp.json`），然后重启客户端。

```json
{
  "mcpServers": {
    "seriterm": {
      "command": "C:\\Users\\<你>\\AppData\\Local\\SeriTerm\\app\\1.1.0\\SeriTerm.exe",
      "args": ["--mcp-stdio"]
    }
  }
}
```

路径就是「复制配置」给你的那一条（当前正在运行的 exe）。**升级换版本后路径会变，重新复制一次即可**；
如果启动器版本装在别处，也可以直接把 `command` 指向你双击的那个 exe——它同样认识 `--mcp-stdio`。

界面里那一栏同时会显示：服务状态、当前权限档位、已接入的客户端数量。

## 权限只有两档

| 档位 | 能力 | 说明 |
|---|---|---|
| **只读**（默认） | 列端口、看状态、读帧、等关键词 | 写入类工具**不会出现在工具清单里**，不会被模型反复尝试 |
| **完全权限** | 上面全部 + 打开/关闭串口、改波特率、发送数据 | 必须由用户在界面上勾选；状态栏会常驻显示 `MCP: 完全权限` |

只读是默认档，理由很直接：**读错只是答复没用，写错可能把设备写进 bootloader、擦掉 Flash 或触发执行器**。
状态栏那三个字是刻意留的——切到完全权限是"要一直记得"的事，不能只藏在左栏卡片里。

完全权限下仍然有四条护栏：

| 护栏 | 取值 | 为什么 |
|---|---|---|
| 单次长度上限 | 4096 字节 | 够发一帧协议命令；防止一口气灌几十 KB |
| 发送速率 | 3 次/秒，突发 6 次 | 够"看日志 → 发一条 → 再看日志"，又不至于让跑偏的模型把设备刷死 |
| 审计行 | 每次发送都在日志里留一行 `[AI] 发送 N 字节：…` | 事后能分清"哪条数据是我发的、哪条是它发的" |
| 一键收回 | 取消勾选「允许 AI 发送数据」 | 立即生效，无需重启 |

另外，通信只走**本机命名管道，且仅当前用户可连**（`PipeOptions.CurrentUserOnly`），
同机器上的其它用户连不上；也不需要开任何网络端口。

## 工具清单

| 工具 | 需要完全权限 | 干什么 |
|---|---|---|
| `serial_list_ports` | | 列出可用串口（含设备友好名）与当前选中项 |
| `serial_get_status` | | 会话快照：是否已打开、端口与参数、收发字节数、编码与断帧方式、自动重连状态、权限档位、帧缓冲游标范围 |
| `serial_read_frames` | | 按游标读帧（方向过滤、HEX/文本、字符上限），返回 `nextCursor` / `hasMore` / `evictedFrames` |
| `serial_wait_for_pattern` | | 阻塞等到出现指定内容（文本 / 正则 / 十六进制字节），可带上下文帧，默认从现在开始等 |
| `serial_open` | ✔ | 打开串口，参数省略时沿用界面当前设置（注意：会按设置拉 DTR/RTS，很多板子因此复位） |
| `serial_close` | ✔ | 关闭串口 |
| `serial_set_baud_rate` | ✔ | 保持打开的情况下改波特率（收发缓冲不清空） |
| `serial_write` | ✔ | 发送文本 / 十六进制，可带行尾（none/cr/lf/crlf） |

工具失败时按 MCP 约定返回 `isError: true`，文案前缀是机器可读的原因，
例如 `[permission_denied]`、`[rate_limited]`、`[payload_too_large]`、`[not_connected]`、`[serial_fault]`。

## 工作原理

```
AI 客户端 ──stdio──▶ SeriTerm.exe --mcp-stdio ──命名管道──▶ SeriTerm.exe（界面进程，持有串口）
```

为什么是两个进程：MCP 客户端要求 server 用 **stdio** 说话，而 stdio 只属于它自己拉起的那个子进程。
界面进程同时拿着串口、还要给人看日志，不能走 stdio；所以对外只暴露一个"桥"：

- `--mcp-stdio` 模式下的 exe **不建窗口**，只把每条 MCP 请求转成一次管道往返；
- 界面进程监听命名管道 `SeriTerm.Mcp.<用户标识短哈希>`，一次连接处理一个请求；
- 桥在启动时先探一次；界面没开就**把界面拉起来**（最多等 20 秒），不用你手动先去点图标。

界面上那份日志和 AI 读到的**不是同一份数据**：AI 读的是一份独立的帧缓冲（默认 2 万帧 / 16 MB）。
这样"暂停显示"不会骗到 AI，日志区淘汰旧行也不会悄悄让 AI 漏数据——
它想知道中间丢没丢，看 `evictedFrames` 就行。

## 排障

| 现象 | 原因与处理 |
|---|---|
| 工具调用返回 `[not_connected]` | 界面进程没在运行（或被别的用户账户运行着）。启动 SeriTerm 后重试 |
| 工具清单里没有 `serial_write` | 当前是只读档。需要发送时在界面上勾「允许 AI 发送数据（完全权限）」 |
| 返回 `[permission_denied]` | 客户端缓存了旧工具清单，或权限刚被收回。让用户确认权限档位后重试 |
| 界面里显示「MCP 服务启动失败」 | 管道名被别的进程占用（同一账户跑了第二个实例）。关掉多余的实例 |
| 想手工看协议 | `SeriTerm.exe --mcp-stdio`，然后按行喂 JSON-RPC（如 `{"jsonrpc":"2.0","id":1,"method":"tools/list"}`） |

发版前建议跑一次冒烟脚本（它会真的拉起桥、走完 initialize → tools/list → 打开 → 发送 → 等关键词）：

```powershell
pwsh -File tools/mcp-smoke.ps1 -Exe src/SeriTerm.App/bin/Debug/net8.0-windows/SeriTerm.exe
pwsh -File tools/mcp-smoke.ps1 -Exe <同一个 exe> -ExpectFullPermission   # 完全权限档下
```
