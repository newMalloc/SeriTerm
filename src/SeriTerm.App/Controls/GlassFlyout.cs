using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SeriTerm.App.Services;

namespace SeriTerm.App.Controls;

/// <summary>独立菜单使用自己的 DWM 模糊层，窗口内浮层透出主窗口的玻璃。</summary>
public sealed class GlassFlyout : Border
{
    public static readonly DependencyProperty UnderlayProperty = DependencyProperty.Register(
        nameof(Underlay), typeof(FrameworkElement), typeof(GlassFlyout),
        new PropertyMetadata(null, (d, _) => ((GlassFlyout)d).UpdateMaterial()));
    public static readonly DependencyProperty UseGlassProperty = DependencyProperty.Register(
        nameof(UseGlass), typeof(bool), typeof(GlassFlyout),
        new PropertyMetadata(false, (d, _) => ((GlassFlyout)d).UpdateMaterial()));

    public FrameworkElement? Underlay
    {
        get => (FrameworkElement?)GetValue(UnderlayProperty);
        set => SetValue(UnderlayProperty, value);
    }
    public bool UseGlass
    {
        get => (bool)GetValue(UseGlassProperty);
        set => SetValue(UseGlassProperty, value);
    }

    private HwndSource? _nativeSource;
    private Color? _previousClearColor;
    private FrameworkElement? _clipped;
    private Rect _cutout = Rect.Empty;
    private Size _underlaySize;

    public GlassFlyout()
    {
        SetResourceReference(BackgroundProperty, "FlyoutBackgroundBrush");
        SetResourceReference(UseGlassProperty, "UseDesktopGlass");
        Loaded += (_, _) => OnVisibilityChanged();
        Unloaded += (_, _) => ResetMaterial();
        IsVisibleChanged += (_, _) => OnVisibilityChanged();
        SizeChanged += (_, _) => UpdatePopupCorners();
    }

    private void OnVisibilityChanged()
    {
        UpdateMaterial();
        if (IsLoaded && IsVisible && (Underlay is not null || PresentationSource.FromVisual(this)?.RootVisual is not Window))
            Reveal(this);
    }

    internal static void Reveal(FrameworkElement element)
    {
        if (!SystemParameters.ClientAreaAnimation) return;

        // 固定弹层边界，让原生模糊区域和选项的点击位置始终对齐。
        var fade = new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        element.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateMaterial()
    {
        ResetMaterial();
        if (!UseGlass || !IsLoaded || !IsVisible) return;

        if (Underlay is { } underlay)
        {
            _clipped = underlay;
            LayoutUpdated += OnLayoutUpdated;
            OnLayoutUpdated(this, EventArgs.Empty);
            SetResourceReference(BackgroundProperty, "GlassFlyoutBrush");
        }
        else EnableNativeBlur();
    }

    private void EnableNativeBlur()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        // 内嵌浮层在 Underlay 绑定完成前可能短暂为空，不能对主窗口的 HWND 改材质。
        if (source.RootVisual is Window window && window.Content != this) return;
        if (!DesktopBackdrop.SetAccentBlur(source.Handle, true)) return;

        _nativeSource = source;
        if (source.RootVisual is Window)
        {
            _previousClearColor = source.CompositionTarget.BackgroundColor;
            source.CompositionTarget.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
            SetResourceReference(BackgroundProperty, "GlassFlyoutBrush");
        }
        else
        {
            UpdatePopupCorners();
            SetResourceReference(BackgroundProperty, "GlassPopupBrush");
            var anchor = TemplatedParent is ContextMenu menu ? menu.PlacementTarget : TemplatedParent;
            if (anchor is not null && Window.GetWindow(anchor) is MainWindow main)
                main.SyncBackdropAfterPopupOpens();
        }
    }

    private void ResetMaterial()
    {
        if (_clipped is not null)
        {
            LayoutUpdated -= OnLayoutUpdated;
            _clipped.ClearValue(ClipProperty);
            _clipped = null;
            _cutout = Rect.Empty;
        }
        if (_nativeSource is { IsDisposed: false } source)
        {
            DesktopBackdrop.SetAccentBlur(source.Handle, false);
            if (_previousClearColor is { } previous) source.CompositionTarget.BackgroundColor = previous;
            else SetWindowRgn(source.Handle, 0, true);
        }
        _nativeSource = null;
        _previousClearColor = null;
        SetResourceReference(BackgroundProperty, "FlyoutBackgroundBrush");
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_clipped is not { } target || !IsArrangeValid || !target.IsArrangeValid ||
            PresentationSource.FromVisual(target) is not { } source || source != PresentationSource.FromVisual(this)) return;

        var area = TransformToVisual(target).TransformBounds(new Rect(RenderSize));
        if (area == _cutout && target.RenderSize == _underlaySize) return;
        _cutout = area;
        _underlaySize = target.RenderSize;
        // 只挖掉浮层实际覆盖的区域，避免日志原文透在菜单文字下面。
        var radius = CornerRadius.TopLeft;
        var clip = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(target.RenderSize)), new RectangleGeometry(area, radius, radius));
        clip.Freeze();
        target.Clip = clip;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdatePopupCorners();
    }

    private void UpdatePopupCorners()
    {
        if (_nativeSource is not { IsDisposed: false, RootVisual: not Window } popup || ActualWidth <= 0 || ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var diameter = 2 * CornerRadius.TopLeft;
        var region = CreateRoundRectRgn(0, 0, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX) + 1,
            (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY) + 1,
            (int)Math.Round(diameter * dpi.DpiScaleX), (int)Math.Round(diameter * dpi.DpiScaleY));
        // SetWindowRgn 成功后，区域句柄的所有权归系统。
        if (region != 0 && SetWindowRgn(popup.Handle, region, true) == 0) DeleteObject(region);
    }

    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
}
