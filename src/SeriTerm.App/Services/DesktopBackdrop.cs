using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using Microsoft.Win32;

namespace SeriTerm.App.Services;

/// <summary>在主窗口的客户区启用 DWM 实时背景。成功后 WPF 仍需透明的合成底色。</summary>
internal static class DesktopBackdrop
{
    // DWMWA_SYSTEMBACKDROP_TYPE 于 Windows 11 22H2 引入。
    public static bool UsesSystemAcrylic => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    public static bool IsAllowed()
    {
        if (SystemParameters.HighContrast || DwmIsCompositionEnabled(out var enabled) < 0 || !enabled)
            return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or System.IO.IOException)
        {
            return false;
        }
    }

    public static bool SetMaterial(IntPtr hwnd, int? blurLevel)
    {
        if (blurLevel != 0)
        {
            var off = new BlurBehind { Flags = 1 };
            DwmEnableBlurBehindWindow(hwnd, ref off);
        }
        // 22H2 起系统 Acrylic 用全客户区玻璃。旧版 BlurBehind 中间档只在仍支持它的系统上提供。
        if (UsesSystemAcrylic)
        {
            const int dwmwaSystemBackdropType = 38;
            int backdrop = blurLevel is > 0 ? 3 : 1; // DWMSBT_TRANSIENTWINDOW / DWMSBT_NONE
            if (DwmSetWindowAttribute(hwnd, dwmwaSystemBackdropType, ref backdrop, sizeof(int)) < 0)
                return false;
            if (blurLevel is not 0) return true;
        }

        return blurLevel switch
        {
            null => SetAccentPolicy(hwnd, default),
            0 => EnableClearBackground(hwnd),
            1 => SetAccentBlur(hwnd, true),
            _ => SetStrongAccent(hwnd),
        };
    }

    private static bool SetStrongAccent(IntPtr hwnd)
    {
        // Win10 直接从 BlurBehind 切到 Acrylic 可能继续显示旧材质，先解除再在当前位置合成。
        SetAccentPolicy(hwnd, default);
        return SetAccentPolicy(hwnd, new AccentPolicy { State = 4, GradientColor = 0x01000000 }) || SetAccentBlur(hwnd, true);
    }

    private static bool EnableClearBackground(IntPtr hwnd)
    {
        SetAccentPolicy(hwnd, default);
        var region = CreateRectRgn(0, 0, -1, -1);
        if (region == 0) return false;
        try
        {
            // 透明客户区参与 DWM 合成，将模糊区域放在客户区之外即可保留背景边缘。
            var clear = new BlurBehind { Flags = 3, Enable = 1, Region = region };
            return DwmEnableBlurBehindWindow(hwnd, ref clear) >= 0;
        }
        finally { DeleteObject(region); }
    }

    // 分层 Popup 使用 BlurBehind，确保同一应用内的文字和色块边缘也会被模糊。
    public static bool SetAccentBlur(IntPtr hwnd, bool enabled) =>
        SetAccentPolicy(hwnd, new AccentPolicy { State = enabled ? 3 : 0 });

    private static bool SetAccentPolicy(IntPtr hwnd, AccentPolicy policy)
    {
        const int wcaAccentPolicy = 19;
        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new AttributeData { Attribute = wcaAccentPolicy, Data = pointer, Size = size };
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int State, Flags, GradientColor, AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind
    {
        public uint Flags;
        public int Enable;
        public IntPtr Region;
        public int TransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public nint Size;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref BlurBehind blur);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref AttributeData data);
}
