using System.Runtime.InteropServices;

namespace SeriTerm.Launcher;

/// <summary>
/// 下载运行时安装包。走 Windows 自带的 WinHTTP（<c>winhttp.dll</c>），不用托管的 <c>HttpClient</c>。
///
/// 为什么不用 HttpClient：启动器是 NativeAOT 原生程序，托管的 HTTPS 会连根拔起
/// <c>System.Net.Security</c>／Sockets／整套托管加密实现。实测同一份启动器，
/// 去掉 HTTP 下载那段后从 5.88 MB 掉到 2.63 MB —— 也就是说托管 HTTPS 一家就占了 3.25 MB。
/// 换成 WinHTTP 后这段开销归零：TLS 走系统 SChannel，证书验证、代理设置、PAC 脚本都用系统的，
/// 顺带还解决了"公司内网必须走代理"这类场景（WinHTTP 会自动用系统代理）。
///
/// 只支持 https：我们只信任 aka.ms／builds.dotnet.microsoft.com 这种官方来源，
/// 接受明文 http 会让"下载并执行"这件事变得不可接受。
/// </summary>
internal static class HttpDownloader
{
    // WinHttpOpen 的访问类型：自动探测代理（Win8.1+）；老系统退回"系统默认代理"
    private const uint AccessTypeDefaultProxy = 0;
    private const uint AccessTypeAutomaticProxy = 4;

    private const uint FlagSecure = 0x00800000;

    private const uint OptionRedirectPolicy = 88;
    private const uint RedirectPolicyAlways = 2;

    private const uint QueryStatusCode = 19;
    private const uint QueryContentLength = 5;
    private const uint QueryFlagNumber = 0x20000000;

    private const int HttpStatusOk = 200;

    internal const string Cancelled = "__cancelled__";

    /// <summary>
    /// 把 <paramref name="url"/> 下载到 <paramref name="target"/>。返回 false 时 <paramref name="error"/>
    /// 是给用户看的原因（用户点取消时等于 <see cref="Cancelled"/>）。
    /// </summary>
    public static bool Download(string url, string target, string dialogTitle, string dialogLine, out string error)
    {
        error = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"只接受 https 地址：{url}";
            return false;
        }

        var session = IntPtr.Zero;
        var connection = IntPtr.Zero;
        var request = IntPtr.Zero;

        try
        {
            session = WinHttpOpen("SeriTerm", AccessTypeAutomaticProxy, null, null, 0);

            if (session == IntPtr.Zero)
            {
                // Win8.1 之前不支持 AUTOMATIC_PROXY
                session = WinHttpOpen("SeriTerm", AccessTypeDefaultProxy, null, null, 0);
            }

            if (session is 0)
            {
                error = Win32("WinHttpOpen");
                return false;
            }

            // 别让断网/半死不活的服务器把启动器吊死在这里
            WinHttpSetTimeouts(session, 15000, 15000, 30000, 30000);

            connection = WinHttpConnect(session, uri.Host, (ushort)uri.Port, 0);

            if (connection == IntPtr.Zero)
            {
                error = Win32("WinHttpConnect");
                return false;
            }

            request = WinHttpOpenRequest(
                connection,
                "GET",
                uri.PathAndQuery,
                null,
                null,
                IntPtr.Zero,
                FlagSecure);

            if (request == IntPtr.Zero)
            {
                error = Win32("WinHttpOpenRequest");
                return false;
            }

            // aka.ms 是短链，必须允许跳转（WinHTTP 默认策略是拒绝）
            var policy = RedirectPolicyAlways;
            WinHttpSetOption(request, OptionRedirectPolicy, ref policy, sizeof(uint));

            if (!WinHttpSendRequest(request, null, 0, IntPtr.Zero, 0, 0, IntPtr.Zero))
            {
                error = Win32("WinHttpSendRequest");
                return false;
            }

            if (!WinHttpReceiveResponse(request, IntPtr.Zero))
            {
                error = Win32("WinHttpReceiveResponse");
                return false;
            }

            var status = QueryNumber(request, QueryStatusCode);

            if (status != HttpStatusOk)
            {
                error = $"服务器返回 HTTP {status}";
                return false;
            }

            var total = (ulong)QueryNumber(request, QueryContentLength);

            using var dialog = ProgressDialog.TryCreate(dialogTitle, dialogLine, marquee: total == 0, out var failure);

            if (failure is not null)
            {
                Diagnostics.Note($"进度对话框不可用（下载照常进行）：{failure}");
            }

            using var destination = File.Create(target);

            ulong done = 0;

            while (true)
            {
                var available = QueryAvailable(request);

                if (available == 0)
                {
                    break;
                }

                var buffer = new byte[Math.Min(available, 64 * 1024)];
                uint read;

                if (!WinHttpReadData(request, buffer, (uint)buffer.Length, out read))
                {
                    error = Win32("WinHttpReadData");
                    return false;
                }

                if (read == 0)
                {
                    break;
                }

                destination.Write(buffer, 0, (int)read);
                done += read;

                if (total > 0)
                {
                    var percent = (int)(done * 100 / total);
                    dialog?.SetText($"已下载 {FormatBytes(done)} / {FormatBytes(total)}（{percent}%）", done, total);
                }

                NativeMethods.PumpMessages();

                if (dialog?.Cancelled == true)
                {
                    error = Cancelled;
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (request != IntPtr.Zero)
            {
                WinHttpCloseHandle(request);
            }

            if (connection != IntPtr.Zero)
            {
                WinHttpCloseHandle(connection);
            }

            if (session != IntPtr.Zero)
            {
                WinHttpCloseHandle(session);
            }
        }
    }

    private static uint QueryAvailable(IntPtr request)
    {
        uint available;
        return WinHttpQueryDataAvailable(request, out available) ? available : 0;
    }

    private static uint QueryNumber(IntPtr request, uint query)
    {
        uint value = 0;
        uint size = sizeof(uint);

        WinHttpQueryHeaders(request, query | QueryFlagNumber, null, ref value, ref size, IntPtr.Zero);

        return value;
    }

    private static string Win32(string api)
    {
        var code = Marshal.GetLastPInvokeError();

        return $"{api} 失败，Win32 错误码 {code}";
    }

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024UL * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024UL * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };

    [DllImport("winhttp.dll", EntryPoint = "WinHttpOpen", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WinHttpOpen(
        string? userAgent,
        uint accessType,
        string? proxyName,
        string? proxyBypass,
        uint flags);

    [DllImport("winhttp.dll", EntryPoint = "WinHttpConnect", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WinHttpConnect(IntPtr session, string serverName, ushort port, uint reserved);

    [DllImport("winhttp.dll", EntryPoint = "WinHttpOpenRequest", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WinHttpOpenRequest(
        IntPtr connection,
        string verb,
        string? objectName,
        string? version,
        string? referrer,
        IntPtr acceptTypes,
        uint flags);

    [DllImport("winhttp.dll", EntryPoint = "WinHttpSetOption", SetLastError = true)]
    private static extern bool WinHttpSetOption(IntPtr handle, uint option, ref uint buffer, uint bufferLength);

    [DllImport("winhttp.dll", EntryPoint = "WinHttpSetTimeouts", SetLastError = true)]
    private static extern bool WinHttpSetTimeouts(
        IntPtr handle,
        int resolveTimeout,
        int connectTimeout,
        int sendTimeout,
        int receiveTimeout);

    [DllImport("winhttp.dll", EntryPoint = "WinHttpSendRequest", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WinHttpSendRequest(
        IntPtr request,
        string? headers,
        uint headersLength,
        IntPtr optional,
        uint optionalLength,
        uint totalLength,
        IntPtr context);

    [DllImport("winhttp.dll", EntryPoint = "WinHttpReceiveResponse", SetLastError = true)]
    private static extern bool WinHttpReceiveResponse(IntPtr request, IntPtr reserved);

    [DllImport("winhttp.dll", EntryPoint = "WinHttpQueryHeaders", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WinHttpQueryHeaders(
        IntPtr request,
        uint infoLevel,
        string? name,
        ref uint buffer,
        ref uint bufferLength,
        IntPtr index);

    [DllImport("winhttp.dll", EntryPoint = "WinHttpQueryDataAvailable", SetLastError = true)]
    private static extern bool WinHttpQueryDataAvailable(IntPtr request, out uint available);

    [DllImport("winhttp.dll", EntryPoint = "WinHttpReadData", SetLastError = true)]
    private static extern bool WinHttpReadData(IntPtr request, byte[] buffer, uint toRead, out uint read);

    [DllImport("winhttp.dll", EntryPoint = "WinHttpCloseHandle", SetLastError = true)]
    private static extern bool WinHttpCloseHandle(IntPtr handle);
}
