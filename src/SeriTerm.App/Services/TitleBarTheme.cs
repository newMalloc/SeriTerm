using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SeriTerm.App.Services;

/// <summary>
/// 让系统标题栏跟随深/浅主题。Windows 10 1809 起支持，属性号在 20H1 前后不同，
/// 因此两个号都试一次；不支持时静默忽略（标题栏保持系统默认色）。
/// </summary>
internal static class TitleBarTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var value = dark ? 1 : 0;
        var result = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        if (result != 0)
        {
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
        }
    }
}
