using System.Numerics;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace SeriTerm.App.Services;

/// <summary>在 WPF 窗口正下方合成实时背景，保留前景文字和控件的清晰度。</summary>
internal sealed class CompositionGlassLayer : IDisposable
{
    [ThreadStatic] private static DispatcherQueueController? _queue;
    private readonly nint _foreground;
    private readonly WinEventProc _cloakChanged;
    private nint _cloakHook;
    private nint _surface;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private SpriteVisual? _sprite;
    private CompositionBackdropBrush? _brush;
    private CompositionEffectBrush? _material;
    private CompositionEffectFactory? _factory;
    private bool _positioning;
    private bool _cloaked;

    public bool IsDark { get; }
    public bool ToneActive { get; private set; } = true;

    private CompositionGlassLayer(nint foreground, bool dark)
    {
        _foreground = foreground;
        IsDark = dark;
        _cloakChanged = OnCloakChanged;
    }

    public static CompositionGlassLayer? TryCreate(nint hwnd, bool dark, Action onToneFallback)
    {
        var layer = new CompositionGlassLayer(hwnd, dark);
        try
        {
            layer.Initialize(onToneFallback);
            return layer;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException
            or EntryPointNotFoundException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            layer.Dispose();
            System.Diagnostics.Debug.WriteLine($"Background composition unavailable: {ex.Message}");
            return null;
        }
    }

    private void Initialize(Action onToneFallback)
    {
        if (DispatcherQueue.GetForCurrentThread() is null)
        {
            var options = new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, Apartment = 2 };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var controller));
            try { _queue = DispatcherQueueController.FromAbi(controller); }
            finally { Marshal.Release(controller); }
        }
        // WS_EX_NOACTIVATE | NOREDIRECTIONBITMAP | TOOLWINDOW | TRANSPARENT
        const int exStyle = 0x08000000 | 0x00200000 | 0x80 | 0x20;
        _surface = CreateWindowEx(exStyle, "STATIC", null, 0x88000000, 0, 0, 1, 1, 0, 0, 0, 0); // POPUP | DISABLED
        if (_surface == 0 || !DesktopBackdrop.SetAccentBlur(_surface, true))
            throw new NotSupportedException("Host backdrop is unavailable.");
        _compositor = new Compositor();
        var interop = _compositor.As<ICompositorDesktopInterop>();
        Marshal.ThrowExceptionForHR(interop.CreateDesktopWindowTarget(_surface, false, out var target));
        try { _target = DesktopWindowTarget.FromAbi(target); }
        finally { Marshal.Release(target); }
        _brush = _compositor.CreateBackdropBrush();
        _sprite = _compositor.CreateSpriteVisual();
        var parameter = new CompositionEffectSourceParameter("Background");
        var tone = new BackdropTone(new BackdropGaussianBlur(parameter), IsDark);
        _factory = _compositor.CreateEffectFactory(tone);
        _material = _factory.CreateBrush();
        _material.SetSourceParameter("Background", _brush);
        _sprite.Brush = _material;
        _sprite.RelativeSizeAdjustment = Vector2.One;
        _target.Root = _sprite;
        // 虚拟桌面会 cloak 窗口，此时 WS_VISIBLE 仍然为真。
        _cloaked = DwmGetWindowAttribute(_foreground, 14, out var cloak, sizeof(int)) >= 0 && cloak != 0;
        _cloakHook = SetWinEventHook(0x8017, 0x8018, 0, _cloakChanged, (uint)Environment.ProcessId, 0, 0);
        SyncPosition();
        _ = CheckEffectAsync(onToneFallback);
    }

    private async Task CheckEffectAsync(Action onToneFallback)
    {
        try
        {
            await _compositor!.RequestCommitAsync();
            if (_surface != 0 && _factory!.LoadStatus == CompositionEffectFactoryLoadStatus.Pending)
                await _compositor.RequestCommitAsync();
            if (_surface == 0 || _factory!.LoadStatus is CompositionEffectFactoryLoadStatus.Success or CompositionEffectFactoryLoadStatus.Pending)
                return;
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or OperationCanceledException)
        {
            if (_surface == 0) return;
        }
        // 设备拒绝效果时仍可透过主窗口看到系统材质。
        if (_surface != 0)
        {
            _sprite!.Brush = null;
            DesktopBackdrop.SetMaterial(_surface, 2);
            ToneActive = false;
            onToneFallback();
        }
    }

    private void OnCloakChanged(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_surface == 0 || hwnd != _foreground || objectId != 0 || childId != 0) return;
        _cloaked = eventId == 0x8017;
        if (!_cloaked)
        {
            try
            {
                // 在任务视图中移动主窗口后，将配套的工具窗口移到同一虚拟桌面。
                var desktops = (IVirtualDesktopManager)(object)new VirtualDesktopManager();
                if (desktops.GetWindowDesktopId(_foreground, out var desktop) >= 0 &&
                    desktops.GetWindowDesktopId(_surface, out var previous) >= 0 && previous != desktop)
                    desktops.MoveWindowToDesktop(_surface, in desktop);
            }
            catch (COMException) { /* Explorer 可能正在重启。 */ }
        }
        SyncPosition();
    }

    public void SyncPosition()
    {
        if (_surface == 0 || _positioning) return;
        _positioning = true;
        try
        {
            if (_cloaked || !IsWindowVisible(_foreground) || IsIconic(_foreground))
            {
                ShowWindow(_surface, 0);
                return;
            }
            if (GetWindowRect(_foreground, out var r))
                SetWindowPos(_surface, _foreground, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                    0x0010 | 0x0040 | 0x0200); // NOACTIVATE | SHOWWINDOW | NOOWNERZORDER
        }
        finally { _positioning = false; }
    }

    public void Dispose()
    {
        if (_cloakHook != 0) UnhookWinEvent(_cloakHook);
        _cloakHook = 0;
        var surface = _surface;
        _surface = 0;
        try
        {
            if (surface != 0) ShowWindow(surface, 0);
            _target?.Dispose();
            _sprite?.Dispose();
            _material?.Dispose();
            _factory?.Dispose();
            _brush?.Dispose();
            _compositor?.Dispose();
        }
        finally { if (surface != 0) DestroyWindow(surface); }
    }

    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        [PreserveSig] int CreateDesktopWindowTarget(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out nint target);
    }

    [ComImport, Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")] private class VirtualDesktopManager { }
    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint hwnd, out int onCurrent);
        [PreserveSig] int GetWindowDesktopId(nint hwnd, out Guid desktop);
        [PreserveSig] int MoveWindowToDesktop(nint hwnd, in Guid desktop);
    }

    private delegate void WinEventProc(nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size, ThreadType, Apartment; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, out nint controller);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(int exStyle, string name, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
}
