using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SeriTerm.App.ViewModels;
using SeriTerm.Core.Pipeline;

namespace SeriTerm.App.Controls;

/// <summary>
/// 日志视图：虚拟化列表 + 自动滚动智能开关 + Ctrl+F 实时搜索。
///
/// 自动滚动的判定要点（这类功能最容易出错的地方）：
/// <list type="bullet">
/// <item>"内容高度变化"（ExtentHeightChange ≠ 0）只可能是追加/淘汰数据造成的，不能当成用户滚动；</item>
/// <item>只有"内容高度不变、偏移量变化"才是用户拖动滚动条或滚轮；</item>
/// <item>程序自己调用的 ScrollIntoView 用抑制标志排除，否则会被误判为用户操作而把开关关掉。</item>
/// </list>
/// </summary>
public partial class LogView : UserControl
{
    /// <summary>距底部多少像素以内算"在最新位置"。</summary>
    private const double BottomTolerance = 8.0;

    /// <summary>按下后移动超过这么多像素才算"拖动选择"。</summary>
    private const double DragThreshold = 4.0;

    private MainViewModel? _viewModel;
    private bool _suppressScrollClassification;
    private bool _scrollToEndQueued;

    /// <summary>按下左键时的位置，用来区分"点一下选中一行"和"拖动选择多行"。</summary>
    private Point _mouseDownPosition;

    /// <summary>拖动连选的锚点行索引，-1 表示这次按下不参与拖动连选。</summary>
    private int _dragAnchorIndex = -1;

    /// <summary>拖动过程中已经扩选到的行索引。</summary>
    private int _dragCurrentIndex = -1;

    public LogView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();

        // ScrollChanged 会从 ListBox 模板内部的 ScrollViewer 冒泡上来
        LogList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    /// <summary>
    /// 键盘焦点是否在日志列表里。终端模式要据此让出 Ctrl+C：
    /// 用户点了日志区再按 Ctrl+C，意思显然是"复制选中的行"，而不是往串口发 0x03。
    /// </summary>
    public bool IsLogListFocused => LogList.IsKeyboardFocusWithin;

    /// <summary>选中了多少行（供外部判断，例如自动化脚本）。</summary>
    public int SelectedLineCount => LogList.SelectedItems.Count;

    /// <summary>聚焦搜索框（Ctrl+F 调用）。</summary>
    public void FocusSearch()
    {
        _viewModel?.OpenSearchCommand.Execute(null);

        // 延后到输入优先级：等搜索条的 Visibility 绑定生效、控件真正可见后再聚焦，
        // 否则对 Collapsed 元素调用 Focus() 会静默失败
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }));
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        if (e.NewValue is not MainViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;
        viewModel.Log.Lines.CollectionChanged += OnLinesChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ScrollToLineRequested += OnScrollToLineRequested;
        viewModel.ScrollToEndRequested += OnScrollToEndRequested;
    }

    private void Detach()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.Log.Lines.CollectionChanged -= OnLinesChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ScrollToLineRequested -= OnScrollToLineRequested;
        _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
        _viewModel = null;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel?.AutoScroll != true)
        {
            return;
        }

        // 集合刚变化时布局还没走完，直接滚会滚到旧的末尾位置
        QueueScrollToEnd();
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e) => QueueScrollToEnd();

    private void OnScrollToLineRequested(object? sender, DisplayLine? line)
    {
        if (line is null)
        {
            return;
        }

        SuppressScrollClassification();
        LogList.ScrollIntoView(line);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SearchVisible) || _viewModel?.SearchVisible != true)
        {
            return;
        }

        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                _viewModel.SearchPreviousCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Enter:
                _viewModel.SearchNextCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                _viewModel.CloseSearchCommand.Execute(null);
                LogList.Focus();
                e.Handled = true;
                break;
        }
    }

    // ---------- 选中与复制 ----------

    /// <summary>
    /// 按下左键：记住起点，并记下"拖动连选"的锚点行。
    ///
    /// WPF 的 ListBox **不会**因为按住拖动就自动连选（实测：从第 1 行拖到第 6 行，选中数仍是 1），
    /// 所以拖动连选要自己实现，见 <see cref="OnLogListMouseMove"/>。
    /// 按住 Ctrl/Shift 时把活交回 WPF 自己的加减选逻辑，不抢。
    /// </summary>
    private void OnLogListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownPosition = e.GetPosition(LogList);
        _dragCurrentIndex = -1;

        var withModifier = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        _dragAnchorIndex = withModifier || e.OriginalSource is not DependencyObject source
            ? -1
            : IndexOfItemAt(source);
    }

    /// <summary>
    /// 右键点在没被选中的行上时，先选中这一行——否则"复制"复制的还是上一次选中的内容。
    /// （WPF 的 ListBox 默认不会因为右键而改变选中项，这里补上这个习惯行为。）
    /// </summary>
    private void OnLogListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(LogList, source) is not ListBoxItem item || item.IsSelected)
        {
            return;
        }

        LogList.SelectedItems.Clear();
        item.IsSelected = true;
        item.Focus();
    }

    /// <summary>
    /// 拖动连选 + 顺手停掉自动滚动。
    ///
    /// 拖动时鼠标常常已经移到列表外面（上方/下方），此时拿不到行容器，
    /// 就按"上/下各走一行"继续扩选并把它滚进视野，这样拖到边缘也能一路选下去。
    /// </summary>
    private void OnLogListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(LogList);
        var moved = Math.Abs(position.X - _mouseDownPosition.X) >= DragThreshold
                    || Math.Abs(position.Y - _mouseDownPosition.Y) >= DragThreshold;

        if (!moved)
        {
            return;
        }

        if (_dragAnchorIndex >= 0)
        {
            ExtendSelectionTo(position);
        }

        // 拖动选择时停止跟随最新数据：否则新行一到就把视野拉到底，刚选中的行立刻被冲走。
        // （单纯点一下选中一行不会走到这里，所以不会顺手把自动滚动关掉。）
        if (_viewModel is { AutoScroll: true } && LogList.SelectedItems.Count > 0)
        {
            _viewModel.PauseAutoScroll();
        }
    }

    private void ExtendSelectionTo(Point position)
    {
        var index = IndexOfItemAt(Mouse.DirectlyOver as DependencyObject);

        if (index < 0)
        {
            // 拖到列表外面：按方向继续走一行，并把它滚进视野
            var step = position.Y < 0 ? -1 : position.Y > LogList.ActualHeight ? 1 : 0;

            if (step == 0 || LogList.Items.Count == 0)
            {
                return;
            }

            var last = _dragCurrentIndex >= 0 ? _dragCurrentIndex : _dragAnchorIndex;
            index = Math.Clamp(last + step, 0, LogList.Items.Count - 1);
            LogList.ScrollIntoView(LogList.Items[index]);
        }

        if (index == _dragCurrentIndex)
        {
            return;
        }

        _dragCurrentIndex = index;
        SelectRange(_dragAnchorIndex, index);
    }

    private void SelectRange(int anchor, int current)
    {
        var from = Math.Max(0, Math.Min(anchor, current));
        var to = Math.Min(LogList.Items.Count - 1, Math.Max(anchor, current));

        LogList.SelectedItems.Clear();
        for (var i = from; i <= to; i++)
        {
            LogList.SelectedItems.Add(LogList.Items[i]);
        }
    }

    private int IndexOfItemAt(DependencyObject? source)
        => source is not null && ItemsControl.ContainerFromElement(LogList, source) is ListBoxItem container
            ? LogList.ItemContainerGenerator.IndexFromContainer(container)
            : -1;

    private void OnCopyCanExecute(object sender, CanExecuteRoutedEventArgs e)
        => e.CanExecute = LogList.SelectedItems.Count > 0;

    private void OnCopyExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var text = BuildSelectedText();

        if (text.Length == 0)
        {
            return;
        }

        var copied = TrySetClipboard(text);
        _viewModel?.ReportCopyResult(LogList.SelectedItems.Count, copied);
        e.Handled = true;
    }

    /// <summary>按屏幕上的样子拼出选中行的文本（关掉时间戳时就不带时间）。</summary>
    private string BuildSelectedText()
    {
        var builder = new StringBuilder();
        var withTimestamp = _viewModel?.ShowTimestamp ?? true;

        foreach (var item in LogList.SelectedItems)
        {
            if (item is not DisplayLine line)
            {
                continue;
            }

            if (withTimestamp)
            {
                builder.Append(line.TimeText).Append(' ');
            }

            builder.Append(line.DirectionText).Append(' ').Append(line.Text).Append("\r\n");
        }

        return builder.ToString();
    }

    private static bool TrySetClipboard(string text)
    {
        // 剪贴板被别的进程占用时会抛 COMException（CLIPBRD_E_CANT_OPEN），重试几次再认输
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(60);
            }
        }

        return false;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var viewModel = _viewModel;

        if (viewModel is null || _suppressScrollClassification)
        {
            return;
        }

        if (e.OriginalSource is not ScrollViewer viewer || Math.Abs(e.VerticalChange) < 0.001)
        {
            return;
        }

        if (IsAtBottom(viewer))
        {
            // 用户拉回（或停在）最新位置 => 自动重新开启跟随
            if (!viewModel.AutoScroll)
            {
                viewModel.ResumeAutoScroll();
            }

            return;
        }

        // 内容高度没变却发生了偏移 => 只可能是用户拖动/滚轮，离开底部就停止跟随
        if (viewModel.AutoScroll && Math.Abs(e.ExtentHeightChange) < 0.001)
        {
            viewModel.PauseAutoScroll();
        }
    }

    private static bool IsAtBottom(ScrollViewer viewer)
        => viewer.ScrollableHeight <= 0
           || viewer.VerticalOffset >= viewer.ScrollableHeight - BottomTolerance;

    private void QueueScrollToEnd()
    {
        if (_scrollToEndQueued)
        {
            return;
        }

        _scrollToEndQueued = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _scrollToEndQueued = false;
            ScrollToEnd();
        }));
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count == 0 || _viewModel?.AutoScroll != true)
        {
            return;
        }

        SuppressScrollClassification();
        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    /// <summary>
    /// 程序发起的滚动在分类逻辑里必须被忽略；ScrollIntoView 引发的 ScrollChanged
    /// 可能在布局阶段才派发，所以用 Background 优先级延后解除抑制。
    /// </summary>
    private void SuppressScrollClassification()
    {
        _suppressScrollClassification = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _suppressScrollClassification = false;
        }));
    }
}
