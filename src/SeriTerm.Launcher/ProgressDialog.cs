using System.Runtime.InteropServices;

namespace SeriTerm.Launcher;

/// <summary>
/// Windows 自带的"进度对话框"（<c>IProgressDialog</c>，就是复制文件时那个带进度条、显示剩余时间、
/// 还能点取消的小窗）。
///
/// 为什么用它：启动器是 NativeAOT 原生程序，不能带 WPF/WinForms；自己 CreateWindowEx 画一个进度条
/// 要几百行，而这个系统对话框本身就是系统主题、有取消按钮、还自带剩余时间估算。
///
/// 为什么走裸 vtable 调 COM，而不是老老实实写 <c>[ComImport]</c> 接口：
/// NativeAOT 下"内置 COM 互操作"默认关闭，实测 <c>Activator.CreateInstance(Type.GetTypeFromCLSID(...))</c>
/// 直接抛 <c>PlatformNotSupportedException: PlatformNotSupported_ComInterop</c>；
/// 打开该开关又会让体积变大、还带着一坨运行时反射元数据。裸 vtable 只需要 CoCreateInstance +
/// 按序号取函数指针，编译期就能定下来，体积和稳定性都更好。
///
/// 用法约束：必须在 STA 线程上创建，创建之后要持续泵消息（<see cref="NativeMethods.PumpMessages"/>），
/// 否则窗口不刷新、取消按钮也点不动。
/// </summary>
internal sealed unsafe class ProgressDialog : IDisposable
{
    // IProgressDialog 的 vtable 布局（IUnknown 占前 3 个）：
    //   0 QueryInterface 1 AddRef 2 Release
    //   3 StartProgressDialog 4 StopProgressDialog 5 SetTitle 6 SetAnimation
    //   7 HasUserCancelled 8 SetProgress 9 SetProgress64 10 SetLine 11 SetCancelMsg 12 Timer
    private const int SlotStartProgressDialog = 3;
    private const int SlotStopProgressDialog = 4;
    private const int SlotSetTitle = 5;
    private const int SlotHasUserCancelled = 7;
    private const int SlotSetProgress64 = 9;
    private const int SlotSetLine = 10;
    private const int SlotRelease = 2;

    private const uint ClsctxInprocServer = 0x1;

    private const uint ProgdlgNormal = 0x00000000;
    private const uint ProgdlgAutoTime = 0x00000002;
    private const uint ProgdlgNoTime = 0x00000004;
    private const uint ProgdlgMarqueeProgress = 0x00000020;

    private static readonly Guid ClsidProgressDialog = new("F8383852-FCD3-11D1-A6B9-006097DF5BD4");
    private static readonly Guid IidIProgressDialog = new("EBBC7C04-315E-11D2-B62F-006097DF5BD4");

    private IntPtr _instance;

    private ProgressDialog(IntPtr instance) => _instance = instance;

    /// <summary>
    /// 创建并显示进度对话框。系统不支持时返回 null——
    /// 此时下载照常进行，只是没有进度窗口；失败原因由 <paramref name="failure"/> 带出，供诊断记录。
    /// </summary>
    public static ProgressDialog? TryCreate(string title, string line1, bool marquee, out string? failure)
    {
        failure = null;
        var instance = IntPtr.Zero;

        try
        {
            var clsid = ClsidProgressDialog;
            var iid = IidIProgressDialog;

            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out instance);

            if (hr != 0 || instance == IntPtr.Zero)
            {
                failure = $"CoCreateInstance(CLSID_ProgressDialog) 返回 0x{hr:X8}";

                if (instance != IntPtr.Zero)
                {
                    Release(instance);
                }

                return null;
            }

            var dialog = new ProgressDialog(instance);

            dialog.SetTitle(title);
            dialog.SetLine(1, line1);
            dialog.SetLine(2, "正在连接…");
            dialog.Start(marquee);

            return dialog;
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";

            if (instance != IntPtr.Zero)
            {
                Release(instance);
            }

            return null;
        }
    }

    /// <summary>用户是否按了取消。每收一块数据问一次。</summary>
    public bool Cancelled
    {
        get
        {
            if (_instance == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var method = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_instance, SlotHasUserCancelled);

                return method(_instance) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public void SetText(string text, ulong completed, ulong total)
    {
        if (_instance == IntPtr.Zero)
        {
            return;
        }

        try
        {
            SetLine(2, text);

            if (total > 0)
            {
                var method = (delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, int>)Slot(_instance, SlotSetProgress64);

                method(_instance, completed, total);
            }
        }
        catch (Exception)
        {
            // 进度显示失败不值得打断下载
        }
    }

    public void Dispose()
    {
        var instance = _instance;
        _instance = IntPtr.Zero;

        if (instance == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var stop = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(instance, SlotStopProgressDialog);

            stop(instance);
        }
        catch (Exception)
        {
        }

        Release(instance);
    }

    private void Start(bool marquee)
    {
        var flags = marquee
            ? ProgdlgNormal | ProgdlgMarqueeProgress | ProgdlgNoTime
            : ProgdlgNormal | ProgdlgAutoTime;

        var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, IntPtr, int>)Slot(_instance, SlotStartProgressDialog);

        method(_instance, IntPtr.Zero, IntPtr.Zero, flags, IntPtr.Zero);
    }

    private void SetTitle(string title) => CallWithString(SlotSetTitle, title);

    private void SetLine(uint line, string text)
    {
        var method = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int, IntPtr, int>)Slot(_instance, SlotSetLine);
        var textPtr = Marshal.StringToCoTaskMemUni(text);

        try
        {
            method(_instance, line, textPtr, 0, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeCoTaskMem(textPtr);
        }
    }

    private void CallWithString(int slot, string text)
    {
        var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(_instance, slot);
        var textPtr = Marshal.StringToCoTaskMemUni(text);

        try
        {
            method(_instance, textPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(textPtr);
        }
    }

    private static IntPtr Slot(IntPtr instance, int index) => (*(IntPtr**)instance)[index];

    private static void Release(IntPtr instance)
    {
        try
        {
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(instance, SlotRelease);

            release(instance);
        }
        catch (Exception)
        {
        }
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);
}
