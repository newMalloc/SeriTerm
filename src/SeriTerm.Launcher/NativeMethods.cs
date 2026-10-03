using System.Runtime.InteropServices;

namespace SeriTerm.Launcher;

/// <summary>
/// 启动器用到的 Win32 / COM 原生接口。全部靠 P/Invoke 手写，不引入任何托管 UI 框架
/// （WinForms/WPF 在 NativeAOT 下不可用或体积代价过高）。
/// </summary>
internal static class NativeMethods
{
    // ---- 消息框 ----
    internal const uint MB_OK = 0x00000000;
    internal const uint MB_YESNO = 0x00000004;
    internal const uint MB_ICONERROR = 0x00000010;
    internal const uint MB_ICONQUESTION = 0x00000020;
    internal const uint MB_ICONINFORMATION = 0x00000040;
    internal const uint MB_SETFOREGROUND = 0x00010000;
    internal const uint MB_TOPMOST = 0x00040000;

    internal const int IDYES = 6;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>弹一个置顶、强制到前台的消息框，返回用户点了哪个按钮。</summary>
    internal static int ShowMessage(string text, string caption, uint type) =>
        MessageBox(IntPtr.Zero, text, caption, type | MB_SETFOREGROUND | MB_TOPMOST);

    // ---- 消息泵（进度对话框要它才能刷新、才能响应"取消"）----
    private const uint PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static extern int PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern int TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    /// <summary>把当前线程消息队列里排队的消息处理掉。下载过程中每收一块数据调一次。</summary>
    internal static void PumpMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE) != 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    // ---- COM 初始化（IProgressDialog 要求 STA）----
    private const uint COINIT_APARTMENTTHREADED = 0x2;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    /// <summary>初始化当前线程的 COM；返回是否需要配对调用 <see cref="ShutdownCom"/>。</summary>
    internal static bool InitializeCom() => CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED) >= 0;

    internal static void ShutdownCom() => CoUninitialize();

    // ---- Authenticode 数字签名校验（下载回来的运行时安装包必须过这一关才允许执行）----
    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnion;          // WINTRUST_FILE_INFO*
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2：校验 Authenticode 签名的标准动作 GUID。</summary>
    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;
    private const uint WTD_SAFER_FLAG = 0x00000100;

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    /// <summary>
    /// 校验文件带有效的 Authenticode 签名（信任链到受信任根，且签名未被篡改）。
    /// 刻意不联网查吊销状态（<c>WTD_CACHE_ONLY_URL_RETRIEVAL</c>），否则断网或慢网会卡住。
    /// </summary>
    internal static bool HasValidAuthenticodeSignature(string filePath) => VerifySignature(filePath) == 0;

    /// <summary>同上，但返回原始 HRESULT（0 表示通过），便于诊断时把失败原因写进报告。</summary>
    internal static int VerifySignature(string filePath)
    {
        var filePathPtr = Marshal.StringToCoTaskMemUni(filePath);

        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePathPtr,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            var fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            try
            {
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pUnion = fileInfoPtr,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_SAFER_FLAG,
                };

                var dataPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_DATA>());
                Marshal.StructureToPtr(data, dataPtr, false);

                try
                {
                    var action = WinTrustActionGenericVerifyV2;
                    var status = WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);
                    // 无论结果如何都要再调一次把状态数据释放掉，否则会泄漏（官方文档明确要求）
                    var close = Marshal.PtrToStructure<WINTRUST_DATA>(dataPtr);
                    close.dwStateAction = WTD_STATEACTION_CLOSE;
                    Marshal.StructureToPtr(close, dataPtr, false);
                    WinVerifyTrust(IntPtr.Zero, ref action, dataPtr);

                    return status;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(dataPtr);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(fileInfoPtr);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(filePathPtr);
        }
    }
}
