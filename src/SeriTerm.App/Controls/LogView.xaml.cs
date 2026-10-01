using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>按下时所在的那一行文本框（跨行拖动时要把它自己的字符选择清掉）。</summary>
    private TextBox? _dragSourceTextBox;

    /// <summary>这次拖动是否已经切换成"选多行"。</summary>
    private bool _dragRowMode;

    /// <summary>最后一次选中了字符的行文本框，复制时优先用它。</summary>
    private TextBox? _textSelectionBox;

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
    /// 两种选择粒度是共存的：
    /// <list type="bullet">
    /// <item>行内容是可以选字符的只读文本框 —— 在同一行里拖就是普通的"选中一段文字"；</item>
    /// <item>拖到别的行上就切换成"选多行"（WPF 的 ListBox 不实现拖动连选，见 <see cref="OnLogListMouseMove"/>）。</item>
    /// </list>
    /// 按住 Ctrl/Shift 时把活交回 WPF 自己的加减选逻辑，不抢。
    /// </summary>
    private void OnLogListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownPosition = e.GetPosition(LogList);
        _dragCurrentIndex = -1;
        _dragRowMode = false;

        var source = e.OriginalSource as DependencyObject;
        _dragSourceTextBox = FindAncestor<TextBox>(source);

        var withModifier = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        _dragAnchorIndex = withModifier ? -1 : IndexOfItemAt(source);

        // 行内容现在是只读文本框，它会吃掉这次点击（文本框要放光标/开始选字符），
        // 所以"点一下选中整行"得自己补；已经选中的行不动，免得把已有的多行选择清掉。
        if (_dragAnchorIndex >= 0
            && LogList.ItemContainerGenerator.ContainerFromIndex(_dragAnchorIndex) is ListBoxItem item
            && !item.IsSelected)
        {
            LogList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    /// <summary>松开左键：把拖动期间借走的鼠标捕获还回去。</summary>
    private void OnLogListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragRowMode = false;
        _dragAnchorIndex = -1;
        _dragCurrentIndex = -1;

        if (ReferenceEquals(Mouse.Captured, LogList))
        {
            Mouse.Capture(null);
        }
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
    /// Ctrl+C：焦点在行文本框里且真的选了字符时，复制那段字符（这就是"自由复制"）；
    /// 否则复制选中的整行。放在 Preview 上是故意的：只读文本框在没有选中内容时
    /// 对 Copy 的处理不可靠，自己接管才确定。
    /// </summary>
    private void OnLogListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        if (HasTextSelection() || LogList.SelectedItems.Count == 0)
        {
            return;
        }

        CopySelection();
        e.Handled = true;
    }

    /// <summary>
    /// 拖动：同一行内交给文本框选字符，跨行则切换成"选多行"。
    ///
    /// 跨行时把行文本框自己选的那一段清掉、并把鼠标捕获转到列表上：
    /// 前者避免同时出现两种高亮，后者让鼠标拖到列表外面还能继续扩选。
    /// 拖动时鼠标常常已经移到列表外面（上方/下方），此时拿不到行容器，
    /// 就按"上/下各走一行"继续扩选并把它滚进视野。
    ///
    /// 用 PreviewMouseMove（隧道路由）而不是 MouseMove：拖动期间鼠标被行文本框捕获，
    /// 冒泡事件会被文本框自己消化掉，隧道路由才能稳定收到。
    /// </summary>
    private void OnLogListPreviewMouseMove(object sender, MouseEventArgs e)
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
        var index = IndexOfItemAtPoint(position);

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

        if (index == _dragAnchorIndex && !_dragRowMode)
        {
            // 还在按下时那一行里：这是"选中一段文字"，交给文本框自己处理，别动整行选择
            return;
        }

        if (!_dragRowMode)
        {
            // 跨到别的行了 => 这次拖动是"选多行"：让文本框把字符选择让出来，
            // 鼠标捕获也转到列表上，拖到列表外面才能继续扩选
            _dragRowMode = true;
            _dragSourceTextBox?.Select(0, 0);
            _dragSourceTextBox = null;
            Mouse.Capture(LogList);
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

    /// <summary>
    /// 按坐标找行索引。必须自己做命中测试（而不是看 <see cref="Mouse.DirectlyOver"/>）：
    /// 拖动期间鼠标被行文本框捕获，直接命中结果永远指向捕获元素，跨行就检测不出来。
    /// </summary>
    private int IndexOfItemAtPoint(Point position)
    {
        var hit = VisualTreeHelper.HitTest(LogList, position);
        return hit is null ? -1 : IndexOfItemAt(hit.VisualHit);
    }

    /// <summary>往可视树上方找最近的某个祖先元素（行内容 → 只读文本框）。</summary>
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    /// <summary>记住最后一次"选了字符"的那个行文本框，复制时优先用它。</summary>
    private void OnLineTextSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (box.SelectionLength > 0)
        {
            _textSelectionBox = box;
        }
        else if (ReferenceEquals(_textSelectionBox, box))
        {
            _textSelectionBox = null;
        }
    }

    /// <summary>
    /// 当前有没有"选中的字符"。文本框被虚拟化回收后会重新绑定到别的行，
    /// 那时选中内容已被清空，这里的长度判断顺便把这种失效引用挡掉。
    /// </summary>
    private bool HasTextSelection() => _textSelectionBox is { SelectedText.Length: > 0 };

    private void OnCopyCanExecute(object sender, CanExecuteRoutedEventArgs e)
        => e.CanExecute = HasTextSelection() || LogList.SelectedItems.Count > 0;

    private void OnCopyExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        CopySelection();
        e.Handled = true;
    }

    /// <summary>复制：优先复制文本框里选中的那段字符，没有就复制选中的整行。</summary>
    private void CopySelection()
    {
        if (_textSelectionBox is { SelectedText.Length: > 0 } box)
        {
            var selected = box.SelectedText;
            var copiedText = TrySetClipboard(selected);
            _viewModel?.ReportCopyResult($"选中的文本（{selected.Length} 字）", copiedText);
            return;
        }

        var text = BuildSelectedText();

        if (text.Length == 0)
        {
            return;
        }

        var copied = TrySetClipboard(text);
        _viewModel?.ReportCopyResult($"{LogList.SelectedItems.Count} 行日志", copied);
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
