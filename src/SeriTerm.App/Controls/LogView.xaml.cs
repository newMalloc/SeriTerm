using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SeriTerm.App.Common;
using SeriTerm.App.ViewModels;
using SeriTerm.Core.Documents;
using SeriTerm.Core.Pipeline;
using SeriTerm.Core.Search;

namespace SeriTerm.App.Controls;

/// <summary>
/// 日志视图：虚拟化列表 + 任意一段字符的自由选择 + 自动滚动智能开关 + Ctrl+F 实时搜索。
///
/// 自动滚动的判定要点（这类功能最容易出错的地方）：
/// <list type="bullet">
/// <item>"内容高度变化"（ExtentHeightChange ≠ 0）只可能是追加/淘汰数据造成的，不能当成用户滚动；</item>
/// <item>只有"内容高度不变、偏移量变化"才是用户拖动滚动条或滚轮；</item>
/// <item>程序自己调用的 ScrollIntoView 用抑制标志排除，否则会被误判为用户操作而把开关关掉。</item>
/// </list>
///
/// 选择的粒度是"字符"：每一行的时间列 / 方向列 / 内容列各是一个只读文本框，
/// 行内拖动交给文本框原生处理；一旦拖到别的行，就切换成自己画的跨行字符选择
/// （<see cref="RepaintFreeSelection"/>）——这样"第 1 行后半段 + 第 2 行前半段"也能选中。
/// </summary>
public partial class LogView : UserControl
{
    /// <summary>距底部多少像素以内算"在最新位置"。</summary>
    private const double BottomTolerance = 8.0;

    /// <summary>按下后移动超过这么多像素才算"拖动选择"（否则算一次点击）。</summary>
    private const double DragThreshold = 4.0;

    /// <summary>行模板里"命中高亮"画布的名字。</summary>
    private const string MatchLayerName = "MatchLayer";

    /// <summary>行模板里"选择底色"画布的名字。</summary>
    private const string SelectionLayerName = "SelectionLayer";

    /// <summary>行模板里"内容列文本"的名字（整表重画高亮时按名字找）。</summary>
    private const string LineTextName = "LineText";

    private MainViewModel? _viewModel;
    private bool _suppressScrollClassification;
    private bool _scrollToEndQueued;

    /// <summary>按下左键时的位置，用来区分"点一下"和"拖动"。</summary>
    private Point _mouseDownPosition;

    /// <summary>这次按下之后是否已经拖动超过阈值。</summary>
    private bool _dragMoved;

    /// <summary>按下的那一行（-1 表示按下点不在任何行上），鼠标抬起时用它决定要不要选中整行。</summary>
    private int _pressedRowIndex = -1;

    /// <summary>按下时那一行本来就已选中：抬起时不再重设，免得把已有的多选打散。</summary>
    private bool _pressedRowWasSelected;

    /// <summary>按下时落在哪一段文字里；拖动时用来判断"还在不在同一个格子里"。</summary>
    private FrameworkElement? _anchorText;

    /// <summary>自由选择的锚点（按下时的行与行内字符下标），-1 表示当前没有自由选择。</summary>
    private int _selAnchorRow = -1;
    private int _selAnchorChar;

    /// <summary>自由选择的另一端（光标所在的行与行内字符下标）。</summary>
    private int _selEndRow = -1;
    private int _selEndChar;

    /// <summary>这次拖动是否已经切换成"自由选择"。</summary>
    private bool _freeSelectionActive;

    /// <summary>是否已经排了一次选择底色的重画。</summary>
    private bool _selectionRepaintQueued;

    /// <summary>等待重画高亮的行内文本（同一批合并成一次）。</summary>
    private readonly HashSet<FrameworkElement> _pendingHighlights = [];

    /// <summary>是否已经排了一次"整表重画高亮"。</summary>
    private bool _highlightAllQueued;

    /// <summary>是否已经往派发队列里排了一次高亮刷新。</summary>
    private bool _highlightFlushQueued;

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
    /// 用户点了日志区再按 Ctrl+C，意思显然是"复制选中的内容"，而不是往串口发 0x03。
    /// </summary>
    public bool IsLogListFocused => LogList.IsKeyboardFocusWithin;

    /// <summary>选中了多少行（供外部判断，例如自动化脚本）。</summary>
    public int SelectedLineCount => LogList.SelectedItems.Count;

    /// <summary>
    /// 自由选择覆盖了多少行（0 表示没有自由选择）。
    /// "整行选择"与"自由选择"是两件事：前者是点一下整行、供整行复制，后者是拖出来的字符区间。
    /// </summary>
    public int SelectedTextLineCount
    {
        get
        {
            if (!HasFreeSelection())
            {
                return 0;
            }

            var (start, end) = NormalizedSelection();
            return end.Row - start.Row + 1;
        }
    }

    /// <summary>自由选择会复制出来的文本（没有自由选择时是空串），供自动化脚本核对。</summary>
    public string SelectedText => ExtractSelectionText();

    /// <summary>聚焦搜索框（Ctrl+F 调用）。</summary>
    public void FocusSearch()
    {
        _viewModel?.OpenSearchCommand.Execute(null);
        FocusSearchBoxDeferred();
    }

    /// <summary>
    /// 延后到输入优先级再聚焦：搜索框平时是折叠的（浮层只在需要时出现），
    /// 对 Collapsed 元素调用 Focus() 会静默失败，必须等绑定生效、控件真正可见之后。
    /// </summary>
    private void FocusSearchBoxDeferred()
        => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!SearchBox.IsVisible)
            {
                return;
            }

            SearchBox.Focus();
            SearchBox.SelectAll();
        }));

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        if (e.NewValue is not MainViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;
        viewModel.Log.Lines.CollectionChanged += OnLinesChanged;
        viewModel.Log.SearchChanged += OnSearchChanged;
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
        _viewModel.Log.SearchChanged -= OnSearchChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ScrollToLineRequested -= OnScrollToLineRequested;
        _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
        _pendingHighlights.Clear();
        _highlightAllQueued = false;
        _selAnchorRow = -1;
        _selEndRow = -1;
        _anchorText = null;
        _viewModel = null;
    }

    private void OnSearchChanged(object? sender, EventArgs e) => QueueAllHighlights();

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reset = 整表换过内容（切 HEX/编码、淘汰旧数据、清空）：行号已经对不上原来的选择了
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ClearFreeSelection();
        }

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
        // 关键字、大小写、字号、换行方式变了：已生成的那些行要重画命中高亮
        if (e.PropertyName is nameof(MainViewModel.SearchText)
            or nameof(MainViewModel.SearchCaseSensitive)
            or nameof(MainViewModel.LogFontSize)
            or nameof(MainViewModel.LineWrap))
        {
            QueueAllHighlights();
            return;
        }

        if (e.PropertyName != nameof(MainViewModel.SearchVisible) || _viewModel?.SearchVisible != true)
        {
            return;
        }

        // 浮层刚变可见，同样要等绑定和布局走完再聚焦（见 FocusSearchBoxDeferred）
        FocusSearchBoxDeferred();
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
    /// 按下左键：记住起点，登记"拖动之后要从哪个字符开始选"；双击则直接选中一个词。
    ///
    /// 两件事刻意分开：
    /// <list type="bullet">
    /// <item>"点一下选中整行"推迟到鼠标抬起且没拖动时才做 —— 否则刚按下去整行就变蓝，
    ///       紧接着拖出来的字符选择看上去像是"又要选整行"（使用者反馈的就是这个）；</item>
    /// <item>按下时先把上一次的自由选择清掉，并记下按在哪一段文字上（行内拖动也由自己接管，
    ///       因为行里已经没有文本框了，见 <see cref="OnLogListPreviewMouseMove"/>）。</item>
    /// </list>
    /// 按住 Ctrl/Shift 时不动选择（让位给 WPF 自己的加减选）。
    /// </summary>
    private void OnLogListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownPosition = e.GetPosition(LogList);
        _dragMoved = false;
        _freeSelectionActive = false;
        _pressedRowIndex = -1;
        _pressedRowWasSelected = false;
        _anchorText = null;
        ClearFreeSelection();

        var source = e.OriginalSource as DependencyObject;
        var withModifier = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        var rowIndex = withModifier ? -1 : IndexOfItemAt(source);

        if (rowIndex < 0)
        {
            return;
        }

        _pressedRowIndex = rowIndex;

        if (TryResolveCaret(_mouseDownPosition, allowExtend: false, out _, out var charIndex, out var part))
        {
            _anchorText = part?.Element;
        }

        // 双击：选中光标底下的那个词（行里没有文本框，原生双击选词得自己补）
        if (e.ClickCount == 2 && _anchorText is not null)
        {
            SelectWordAt(rowIndex, charIndex);

            // 双击不再走"点一下选中整行"（否则松开时会把它替换成整行选中）
            _pressedRowIndex = -1;
            e.Handled = true;
            return;
        }

        if (LogList.ItemContainerGenerator.ContainerFromIndex(rowIndex) is not ListBoxItem item)
        {
            return;
        }

        _pressedRowWasSelected = item.IsSelected;

        // 按在没被选中的行上：先把整行选择清掉。拖动选字符时不会同时留着一行整行高亮；
        // 真的是"点一下"的话，鼠标抬起时会把这行选上（见 OnLogListPreviewMouseLeftButtonUp）。
        if (!item.IsSelected)
        {
            LogList.SelectedItems.Clear();
        }

        // 锚点字符：拖动时从这里开始选
        if (_anchorText is not null)
        {
            SetFreeSelectionAnchor(rowIndex, charIndex);
        }
    }

    /// <summary>双击选中"第 <paramref name="row"/> 行第 <paramref name="charIndex"/> 个字符"所在的词。</summary>
    private void SelectWordAt(int row, int charIndex)
    {
        if (row < 0 || row >= LogList.Items.Count || LogList.Items[row] is not DisplayLine line)
        {
            return;
        }

        var text = line.ToDisplayText(_viewModel?.ShowTimestamp ?? true);
        var (start, end) = LogTextSelection.WordRange(text, charIndex);

        if (end <= start)
        {
            return;
        }

        SetFreeSelectionAnchor(row, start);
        _selEndRow = row;
        _selEndChar = end;
        QueueSelectionRepaint();
    }

    /// <summary>
    /// 松开左键：把拖动期间借走的鼠标捕获还回去，并处理"点一下选中整行"。
    /// 拖动过（<see cref="_dragMoved"/>）或正在自由选择时不动整行选择。
    /// </summary>
    private void OnLogListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var clickedRow = _pressedRowIndex;
        var wasSelected = _pressedRowWasSelected;
        var moved = _dragMoved;
        var selecting = _freeSelectionActive;

        _freeSelectionActive = false;
        _pressedRowIndex = -1;
        _pressedRowWasSelected = false;
        _anchorText = null;
        _dragMoved = false;

        if (ReferenceEquals(Mouse.Captured, LogList))
        {
            Mouse.Capture(null);
        }

        if (moved || selecting || clickedRow < 0 || wasSelected)
        {
            return;
        }

        SelectSingleRow(clickedRow);
    }

    /// <summary>选中整行（清掉其它行）。</summary>
    private void SelectSingleRow(int rowIndex)
    {
        if (LogList.ItemContainerGenerator.ContainerFromIndex(rowIndex) is not ListBoxItem item)
        {
            return;
        }

        LogList.SelectedItems.Clear();
        item.IsSelected = true;
    }

    /// <summary>
    /// 右键：点在当前的自由选择里就完全不动（这样右键「复制」复制的就是选中的那段字符）；
    /// 点在别处才先选中这一行——否则"复制"复制的还是上一次选中的内容。
    /// （WPF 的 ListBox 默认不会因为右键而改变选中项，这里补上这个习惯行为。）
    /// </summary>
    private void OnLogListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (IsInsideFreeSelection(e.GetPosition(LogList)))
        {
            return;
        }

        ClearFreeSelection();

        if (ItemsControl.ContainerFromElement(LogList, source) is not ListBoxItem item || item.IsSelected)
        {
            return;
        }

        LogList.SelectedItems.Clear();
        item.IsSelected = true;
        item.Focus();
    }

    /// <summary>
    /// Ctrl+C：焦点在日志区且有"选中内容"时自己复制（自由选择 → 选中的整行）；
    /// 什么都没有时返回，让终端模式去发 0x03。
    /// 放在 Preview 上是故意的：字符选择根本不是原生选择，只有自己接管才确定。
    /// </summary>
    private void OnLogListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        if (!HasFreeSelection() && LogList.SelectedItems.Count == 0)
        {
            return;
        }

        CopySelection();
        e.Handled = true;
    }

    /// <summary>
    /// 拖动：同一行内交给文本框原生选字符；一旦拖到别的行上，就切换成"自由选择"。
    ///
    /// 自由选择是自己画的（<see cref="RepaintFreeSelection"/>），所以端点可以落在任意字符上——
    /// 这就是"第 1 行后半段 + 第 2 行前半段"这种选择的实现方式：不是按整行扩选。
    ///
    /// 切换时把锚点行文本框自己选的那一段清掉、并把鼠标捕获转到列表上：
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

        if (!_dragMoved)
        {
            if (Math.Abs(position.X - _mouseDownPosition.X) < DragThreshold
                && Math.Abs(position.Y - _mouseDownPosition.Y) < DragThreshold)
            {
                return;
            }

            _dragMoved = true;
        }

        if (_selAnchorRow < 0 || !TryResolveCaret(position, allowExtend: true, out var row, out var charIndex, out var part))
        {
            return;
        }

        if (!_freeSelectionActive)
        {
            // 行里已经没有文本框了，行内拖动也由自己画；只要拖动超过阈值就接管
            _freeSelectionActive = true;

            // 自由选择和"整行选中"不能同时显示：否则拖出来的字符区间会被整行底色盖住
            LogList.SelectedItems.Clear();
            Mouse.Capture(LogList);
        }

        if (row != _selEndRow || charIndex != _selEndChar)
        {
            _selEndRow = row;
            _selEndChar = charIndex;
            QueueSelectionRepaint();
        }

        // 拖动选择时停止跟随最新数据：否则新行一到就把视野拉到底，刚选中的行立刻被冲走。
        if (_viewModel is { AutoScroll: true })
        {
            _viewModel.PauseAutoScroll();
        }
    }

    // ---------- 自由选择（跨行按字符） ----------

    /// <summary>登记自由选择的锚点（按下时的行与字符下标）。</summary>
    private void SetFreeSelectionAnchor(int row, int charIndex)
    {
        _selAnchorRow = row;
        _selAnchorChar = charIndex;
        _selEndRow = row;
        _selEndChar = charIndex;
    }

    /// <summary>当前有没有"选中的一段字符"（哪怕只有一个字符，或者只在一行内）。</summary>
    private bool HasFreeSelection()
    {
        var (start, end) = NormalizedSelection();
        return start.Row >= 0 && (start.Row != end.Row || start.Char != end.Char);
    }

    /// <summary>把两个端点归一化成"上 → 下、左 → 右"，并裁剪到实际行数与行内长度。</summary>
    private (RowCaret Start, RowCaret End) NormalizedSelection()
    {
        if (_selAnchorRow < 0 || _selEndRow < 0 || LogList.Items.Count == 0)
        {
            return (new RowCaret(-1, 0), new RowCaret(-1, 0));
        }

        var (start, end) = LogTextSelection.Normalize(
            new RowCaret(_selAnchorRow, _selAnchorChar),
            new RowCaret(_selEndRow, _selEndChar));

        start = new RowCaret(Math.Clamp(start.Row, 0, LogList.Items.Count - 1), Math.Max(0, start.Char));
        end = new RowCaret(Math.Clamp(end.Row, 0, LogList.Items.Count - 1), Math.Max(0, end.Char));
        return (start, end);
    }

    /// <summary>清掉自由选择（并把已经画上的底色擦掉）。</summary>
    private void ClearFreeSelection()
    {
        if (_selAnchorRow < 0 && _selEndRow < 0)
        {
            return;
        }

        _selAnchorRow = -1;
        _selEndRow = -1;
        _selAnchorChar = 0;
        _selEndChar = 0;
        _freeSelectionActive = false;
        QueueSelectionRepaint();
    }

    /// <summary>这个位置（列表坐标）是不是落在当前自由选择的范围里。</summary>
    private bool IsInsideFreeSelection(Point position)
    {
        if (!HasFreeSelection() || !TryResolveCaret(position, allowExtend: false, out var row, out var charIndex))
        {
            return false;
        }

        var (start, end) = NormalizedSelection();
        var caret = new RowCaret(row, charIndex);
        return Compare(caret, start) >= 0 && Compare(caret, end) <= 0;
    }

    private static int Compare(RowCaret left, RowCaret right)
        => left.Row != right.Row ? left.Row.CompareTo(right.Row) : left.Char.CompareTo(right.Char);

    /// <summary>把自由选择拼成剪贴板文本（没有自由选择时是空串）。</summary>
    private string ExtractSelectionText()
    {
        var (start, end) = NormalizedSelection();

        if (start.Row < 0 || (start.Row == end.Row && start.Char >= end.Char))
        {
            return string.Empty;
        }

        var withTimestamp = _viewModel?.ShowTimestamp ?? true;
        var rows = new List<string>(end.Row - start.Row + 1);

        for (var row = start.Row; row <= end.Row && row < LogList.Items.Count; row++)
        {
            rows.Add(LogList.Items[row] is DisplayLine line ? line.ToDisplayText(withTimestamp) : string.Empty);
        }

        return LogTextSelection.Extract(rows, start.Char, end.Char);
    }

    /// <summary>
    /// 排队重画选择底色。
    ///
    /// 必须用 Loaded 优先级延后：拖动过程中容器会被虚拟化回收、重新生成，
    /// 而 <c>DataContextChanged</c> 正好发生在"内容生成中"，这时调
    /// <see cref="TextBox.GetRectFromCharacterIndex(int, bool)"/> 会强制一次布局，
    /// 撞上 WPF 的保护直接抛 <c>InvalidOperationException：无法在正在进行内容生成时调用 StartAt</c>。
    /// 同一个原因，命中高亮也走这条队列（见 <see cref="QueueHighlight"/>）。
    /// 顺带还起到合并的作用：一次拖动里的多次 MouseMove 只会重画一帧。
    /// </summary>
    private void QueueSelectionRepaint()
    {
        if (_selectionRepaintQueued || !IsLoaded)
        {
            return;
        }

        _selectionRepaintQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _selectionRepaintQueued = false;
            RepaintFreeSelection();
        }));
    }

    /// <summary>
    /// 按当前选择给每一行（已经生成出来的那些）重画底色。
    ///
    /// 只画可视行：行是虚拟化的，没生成的行等它生成时会被重新画一遍
    /// （<see cref="OnLineTextDataContextChanged"/> 里排的那次重画）。
    /// 整行都在选择范围内时直接铺一块，不逐个字符去量——一次拖选几千行也不会卡。
    /// </summary>
    private void RepaintFreeSelection()
    {
        var hasSelection = HasFreeSelection();
        var (start, end) = NormalizedSelection();
        var brush = (Brush)FindResource("SelectionBrush");

        foreach (var item in FindDescendants<ListBoxItem>(LogList))
        {
            var layer = FindDescendants<Canvas>(item).FirstOrDefault(c => c.Name == SelectionLayerName);

            if (layer is null)
            {
                continue;
            }

            layer.Children.Clear();

            var rowIndex = LogList.ItemContainerGenerator.IndexFromContainer(item);

            if (!hasSelection || rowIndex < start.Row || rowIndex > end.Row || !TryGetRowLayout(item, out var layout))
            {
                continue;
            }

            var from = Math.Clamp(rowIndex == start.Row ? start.Char : 0, 0, layout.Length);
            var to = Math.Clamp(rowIndex == end.Row ? end.Char : layout.Length, 0, layout.Length);

            if (to <= from)
            {
                continue;
            }

            if (from == 0 && to == layout.Length)
            {
                // 整行都在范围里：铺一块到底，不去逐字量（自动换行时它就是整段的高度）。
                // 这一块是**整行**（底色层和行一样高，见 LogView.xaml 的行模板），
                // 所以连续整行选中时相邻两块严丝合缝，中间不会露出背景色的横线。
                AddSelectionRect(layer, brush, new Rect(0, 0, layer.ActualWidth, layer.ActualHeight));
                continue;
            }

            // 一行只有一个视觉行时（不换行，这是默认样子），底色铺满整行而不是只铺"一行文字"那么高：
            // 行高里还有行距和上下各 1px 内边距，只按字符矩形铺会在行与行之间留下一道缝。
            var fillRow = IsSingleVisualLine(layout);

            foreach (var part in layout.Parts)
            {
                var partFrom = Math.Max(from, part.Start);
                var partTo = Math.Min(to, part.Start + part.Length);

                if (partTo > partFrom)
                {
                    DrawPartSelection(layer, brush, part, partFrom - part.Start, partTo - partFrom, fillRow);
                }
            }
        }
    }

    /// <summary>
    /// 这一行的文字是不是只占一个视觉行（自动换行关掉时永远是这样）。
    ///
    /// 比的是量出来的值：最高的那一段有多高、一个字符盒有多高。换行开着且这一行真的折了行时，
    /// 内容列会高出一截，比值就露馅了——用实际布局判定，不猜字体行距。
    /// </summary>
    private static bool IsSingleVisualLine(RowLayout layout)
    {
        var lineHeight = 0d;
        var tallest = 0d;

        foreach (var part in layout.Parts)
        {
            var glyph = PartCharRect(part, 0);

            if (!glyph.IsEmpty)
            {
                lineHeight = Math.Max(lineHeight, glyph.Height);
            }

            tallest = Math.Max(tallest, part.Element.ActualHeight);
        }

        return lineHeight <= 0 || tallest <= lineHeight + 0.5;
    }

    /// <summary>
    /// 量出一段字符占的矩形并画上去。
    ///
    /// 逐字符量、同一视觉行上的合并成一块（否则一段命中会画出上百个小方块）；
    /// 自动换行时同一段会横跨几个视觉行，靠"矩形 Top 变了就另起一块"自然处理
    /// （字符矩形的高度就是视觉行的高度，所以相邻两个视觉行的方块本来就是贴着的）。
    ///
    /// <paramref name="fillRow"/> 为真表示这一行只有一个视觉行：纵向铺满**整行**，
    /// 而不是只铺字符矩形那么高——字符盒不含行距、更不含 Border 的上下内边距，
    /// 只按它铺，连续选中时行与行之间就会留下一道背景色横线（"选区一行一行分开"的样子）。
    /// </summary>
    private static void DrawPartSelection(
        Canvas layer, Brush brush, RowPart part, int localStart, int localLength, bool fillRow)
    {
        var origin = part.Element.TranslatePoint(new Point(0, 0), layer);
        var rowHeight = layer.ActualHeight;
        Rectangle? current = null;

        for (var i = localStart; i < localStart + localLength; i++)
        {
            var rectInPart = PartCharRect(part, i);

            if (rectInPart.IsEmpty)
            {
                continue;
            }

            var rect = new Rect(
                rectInPart.Left + origin.X,
                fillRow ? 0 : rectInPart.Top + origin.Y,
                rectInPart.Width,
                fillRow ? rowHeight : rectInPart.Height);

            // 同一个视觉行上、且接在前一块右边的字符：把方块拉宽就行（不然会画成上百个小方块）
            if (current is not null && Math.Abs(Canvas.GetTop(current) - rect.Top) < 0.5)
            {
                var left = Canvas.GetLeft(current);
                current.Width = Math.Max(left + current.Width, rect.Right) - left;
                continue;
            }

            current = new Rectangle { Width = rect.Width, Height = rect.Height, Fill = brush };
            Canvas.SetLeft(current, rect.Left);
            Canvas.SetTop(current, rect.Top);
            layer.Children.Add(current);
        }
    }

    /// <summary>
    /// 某个字符占用它所在控件的哪块矩形（相对该控件左上角；量不到时返回 <see cref="Rect.Empty"/>）。
    ///
    /// TextBlock 没有 GetRectFromCharacterIndex，但它的内容是一个 TextContainer，
    /// 可以用 <see cref="TextPointer"/> 拿到字符矩形——那正是文本框那个 API 内部用的东西，
    /// 所以自动换行、任意字体（中文/等宽混排）都对：
    /// "第 i 个字符之后"的位置往**回**看，拿到的就是第 i 个字符自己的矩形（带宽度、且落在它所在的那一视觉行上）。
    /// 反向（往前看）拿到的是零宽的前边缘矩形——那个只能用来定位，不能用来算宽度。
    /// </summary>
    private static Rect PartCharRect(RowPart part, int index)
    {
        if (part.Box is { } box)
        {
            var leading = box.GetRectFromCharacterIndex(index);

            if (leading.IsEmpty)
            {
                return Rect.Empty;
            }

            // 换行处"字符尾边"会落到下一行的行首，这种就只取这个字符自己的宽度
            var trailing = box.GetRectFromCharacterIndex(index, true);

            var right = trailing.IsEmpty || Math.Abs(trailing.Top - leading.Top) > 0.5
                ? leading.Right
                : trailing.Right;

            return new Rect(leading.Left, leading.Top, Math.Max(0.5, right - leading.Left), leading.Height);
        }

        if (part.Block is not { } block || index < 0 || index >= part.Length)
        {
            return Rect.Empty;
        }

        // 与文本框那套同构：GetCharacterRect(Forward) 给的是**零宽的前边缘**（文本框的
        // GetRectFromCharacterIndex(i) 实测也是零宽，两者是同一套 TextPointer 机制），
        // 所以宽度要用"下一个字符的前边缘"减出来；换行处后边缘会落到下一行行首，那就只取前边缘。
        var glyph = TextBlockGlyph(block, index);

        if (glyph.IsEmpty)
        {
            return Rect.Empty;
        }

        // 字形盒的宽度不等于"到下一个字符的步进"（末字符还常常带上行尾的富余），
        // 所以右边取"下一个字符的左边缘"；段内最后一个字符则沿用前一个字符的步进。
        var width = glyph.Width;

        if (index + 1 < part.Length)
        {
            var next = TextBlockGlyph(block, index + 1);

            if (!next.IsEmpty && Math.Abs(next.Top - glyph.Top) < 0.5 && next.Left > glyph.Left)
            {
                width = next.Left - glyph.Left;
            }
        }
        else if (index > 0)
        {
            var previous = TextBlockGlyph(block, index - 1);

            if (!previous.IsEmpty && Math.Abs(previous.Top - glyph.Top) < 0.5 && glyph.Left > previous.Left)
            {
                width = glyph.Left - previous.Left;
            }
        }

        return new Rect(glyph.Left, glyph.Top, Math.Max(0.5, width), glyph.Height);
    }

    /// <summary>
    /// 第 <paramref name="index"/> 个字符在 TextBlock 里的"字形盒"（相对控件左上角）。
    /// 取法：从"该字符之后"的位置往**回**看——这是 TextBlock 上唯一稳定可靠的字符矩形
    /// （往前看的前边缘矩形在 TextBlock 上会返回空）。
    /// </summary>
    private static Rect TextBlockGlyph(TextBlock block, int index)
    {
        if (index < 0)
        {
            return Rect.Empty;
        }

        var after = block.ContentStart.GetPositionAtOffset(index + 1, LogicalDirection.Forward);

        return after?.GetCharacterRect(LogicalDirection.Backward) ?? Rect.Empty;
    }

    private static void AddSelectionRect(Canvas layer, Brush brush, Rect rect)
    {
        var mark = new Rectangle { Width = Math.Max(0, rect.Width), Height = Math.Max(0, rect.Height), Fill = brush };
        Canvas.SetLeft(mark, rect.Left);
        Canvas.SetTop(mark, rect.Top);
        layer.Children.Add(mark);
    }

    /// <summary>
    /// 一行的文本由哪几段拼出来：把这一行里可见的文字控件按左右顺序排好，
    /// 记下每一段在"整行文本"里的起点与长度（列与列之间那个空格算作分隔）。
    /// 只按顺序取、不认控件名字，所以改列布局不会让坐标偷偷错位。
    /// 现在整行都是 TextBlock（内容列以前是只读 TextBox，为了滚动流畅已经换掉）。
    /// </summary>
    private static bool TryGetRowLayout(FrameworkElement container, out RowLayout layout)
    {
        var parts = new List<RowPart>();
        var length = 0;

        var elements = FindDescendants<FrameworkElement>(container)
            .Where(element => element is (TextBox or TextBlock) && element.Visibility == Visibility.Visible)
            .OrderBy(element => element.TranslatePoint(new Point(0, 0), container).X);

        foreach (var element in elements)
        {
            if (parts.Count > 0)
            {
                length++;   // 列间那个空格
            }

            var part = new RowPart(element, length);
            parts.Add(part);
            length += part.Length;
        }

        layout = new RowLayout(parts, length);
        return parts.Count > 0;
    }

    private static int MapPartChar(RowLayout layout, FrameworkElement element, int localIndex)
    {
        foreach (var part in layout.Parts)
        {
            if (ReferenceEquals(part.Element, element))
            {
                return part.Start + Math.Clamp(localIndex, 0, part.Length);
            }
        }

        return 0;
    }

    /// <summary>命中到的这个视觉元素属于这一行里的哪一段（往上找到某一段的宿主为止）。</summary>
    private static RowPart? FindPart(RowLayout layout, DependencyObject? source)
    {
        while (source is not null)
        {
            foreach (var part in layout.Parts)
            {
                if (ReferenceEquals(part.Element, source))
                {
                    return part;
                }
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    /// <summary>
    /// 把"列表坐标"解析成"第几行的第几个字符"。
    ///
    /// 必须自己做命中测试（而不是看 <see cref="Mouse.DirectlyOver"/>）：
    /// 拖动期间鼠标被行文本框捕获，直接命中结果永远指向捕获元素，跨行就检测不出来。
    /// 点落在列表上/下方（拖出列表外）时按方向延伸到相邻行并滚进视野，端点取该行的行首/行尾。
    /// </summary>
    private bool TryResolveCaret(Point position, bool allowExtend, out int row, out int charIndex)
        => TryResolveCaret(position, allowExtend, out row, out charIndex, out _);

    /// <summary>同上，并给出坐标落在哪一段里（判断"有没有跨出按下时那一个格子"要用）。</summary>
    private bool TryResolveCaret(Point position, bool allowExtend, out int row, out int charIndex, out RowPart? part)
    {
        row = -1;
        charIndex = 0;
        part = null;

        var hit = VisualTreeHelper.HitTest(LogList, position);
        var container = hit is null ? null : ItemsControl.ContainerFromElement(LogList, hit.VisualHit) as ListBoxItem;

        if (container is not null)
        {
            row = LogList.ItemContainerGenerator.IndexFromContainer(container);

            if (row < 0)
            {
                return false;
            }

            if (!TryGetRowLayout(container, out var layout))
            {
                return true;
            }

            var hitPart = hit is null ? null : FindPart(layout, hit.VisualHit);

            if (hitPart is null)
            {
                hitPart = NearestPart(layout, position);
            }

            if (hitPart is null)
            {
                return true;
            }

            var element = hitPart.Value.Element;
            part = hitPart;
            var pointInPart = LogList.TranslatePoint(position, element);
            charIndex = MapPartChar(layout, element, ToCaret(hitPart.Value, pointInPart));
            return true;
        }

        if (!allowExtend)
        {
            return false;
        }

        var step = position.Y < 0 ? -1 : position.Y > LogList.ActualHeight ? 1 : 0;
        var (first, last) = RealizedRowRange();

        if (step == 0 || first < 0)
        {
            return false;
        }

        if (step > 0)
        {
            row = Math.Min(LogList.Items.Count - 1, last + 1);
            charIndex = ModelRowLength(row);
        }
        else
        {
            row = Math.Max(0, first - 1);
            charIndex = 0;
        }

        LogList.ScrollIntoView(LogList.Items[row]);
        return true;
    }

    /// <summary>
    /// 点在某一 <paramref name="part"/> 的哪个字符间隙上（坐标已换算成"相对该控件"）。
    ///
    /// 两种控件都按"逐个字符比矩形"来做：左边半格算"插在这个字符之前"、右半格算"算上这个字符"，
    /// 这就是所有文本编辑器的规矩（少这一步的话，把鼠标拖到某个字中间，那个字反而选不上）。
    /// TextBlock 用 <see cref="PartCharRect"/> 拿字符矩形，所以自动换行时也能按视觉行判断。
    /// </summary>
    private static int ToCaret(RowPart part, Point pointInPart)
    {
        if (part.Box is { } box)
        {
            return ToCaret(box, box.GetCharacterIndexFromPoint(pointInPart, true), pointInPart);
        }

        var length = part.Length;

        if (length == 0)
        {
            return 0;
        }

        var caret = 0;

        for (var i = 0; i < length; i++)
        {
            var rect = PartCharRect(part, i);

            if (rect.IsEmpty)
            {
                continue;
            }

            // 点在这一行的上方：光标停在上一行末尾（就是当前 caret）
            if (pointInPart.Y < rect.Top - 0.5)
            {
                break;
            }

            // 点在这一行的下方：这一行整行都在点之前
            if (pointInPart.Y > rect.Bottom + 0.5)
            {
                caret = i + 1;
                continue;
            }

            // 同一视觉行：落在左半边就插在它前面，右半边则算上它
            if (pointInPart.X <= rect.Left + (rect.Width / 2))
            {
                return i;
            }

            caret = i + 1;
        }

        return Math.Clamp(caret, 0, length);
    }

    /// <summary>
    /// 把"点落在哪个字符上"换成"光标在哪两个字符之间"。
    ///
    /// <see cref="TextBox.GetCharacterIndexFromPoint(Point, bool)"/> 给的是**光标底下那个字符**
    /// （点在第 4 个字中间，返回的就是 4），而选择的端点要的是字符之间的位置：点在字符右半边时，
    /// 这个字符本身也算被选中（左半边就不算）——这就是所有文本编辑器的规矩。
    /// 少这一步的后果很直观：把鼠标拖到某个字中间，那个字却没被选上。
    ///
    /// ⚠️ 判断左右半边不能用 <c>GetRectFromCharacterIndex(i)</c>：它给的是这个字符**左边缘**的
    /// 零宽矩形（实测宽度恒为 0），拿它当中线会让"点在字符左边一丁点"也被算成右半边。
    /// 字符真正的右边缘要用 <c>trailingEdge: true</c> 那次调用去拿。
    /// </summary>
    private static int ToCaret(TextBox box, int index, Point pointInBox)
    {
        var length = box.Text?.Length ?? 0;

        if (length == 0)
        {
            return 0;
        }

        if (index >= length)
        {
            return length;
        }

        index = Math.Max(0, index);
        var leading = box.GetRectFromCharacterIndex(index);
        var trailing = box.GetRectFromCharacterIndex(index, true);
        var left = leading.IsEmpty ? trailing.Left : leading.Left;

        // 换行处"字符右边缘"会落到下一行的行首，这种拿不到宽度，就按"算上这个字符"处理
        var right = trailing.IsEmpty || Math.Abs(trailing.Top - leading.Top) > 0.5 ? leading.Right : trailing.Right;

        if (right <= left)
        {
            return index + 1;
        }

        return pointInBox.X > left + ((right - left) / 2) ? index + 1 : index;
    }

    /// <summary>一段文字占的横向范围（相对列表；落在列间的空隙或列外时取最近的那段）。</summary>
    private RowPart? NearestPart(RowLayout layout, Point positionInList)
    {
        RowPart? nearest = null;
        var nearestDistance = double.MaxValue;

        foreach (var part in layout.Parts)
        {
            var left = part.Element.TranslatePoint(new Point(0, 0), LogList).X;
            var right = left + part.Element.ActualWidth;

            if (positionInList.X >= left && positionInList.X <= right)
            {
                return part;
            }

            var distance = positionInList.X < left ? left - positionInList.X : positionInList.X - right;

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = part;
            }
        }

        return nearest;
    }

    /// <summary>已经生成出来的行里，最小与最大的行号（都没有时是 -1）。</summary>
    private (int First, int Last) RealizedRowRange()
    {
        var first = -1;
        var last = -1;

        foreach (var item in FindDescendants<ListBoxItem>(LogList))
        {
            var index = LogList.ItemContainerGenerator.IndexFromContainer(item);

            if (index < 0)
            {
                continue;
            }

            first = first < 0 ? index : Math.Min(first, index);
            last = Math.Max(last, index);
        }

        return (first, last);
    }

    /// <summary>某一行在屏幕上的文本长度（按数据模型算，用于拖到列表外时取行首/行尾）。</summary>
    private int ModelRowLength(int rowIndex)
        => rowIndex >= 0 && rowIndex < LogList.Items.Count && LogList.Items[rowIndex] is DisplayLine line
            ? line.ToDisplayText(_viewModel?.ShowTimestamp ?? true).Length
            : 0;

    /// <summary>
    /// 一行里的一段文字：现在是三个 TextBlock（内容列、时间列、方向列）。
    /// 文本框那条分支留着（<see cref="PartCharRect"/> 里两套量法都实现了），
    /// 免得以后哪一列换回文本框时坐标悄悄错位。占用 [Start, Start + Length) 这一段整行文本。
    /// </summary>
    private readonly struct RowPart
    {
        public RowPart(FrameworkElement element, int start)
        {
            Element = element;
            Start = start;
            Length = element switch
            {
                TextBox box => box.Text?.Length ?? 0,
                TextBlock block => block.Text?.Length ?? 0,
                _ => 0,
            };
        }

        public FrameworkElement Element { get; }

        public TextBox? Box => Element as TextBox;

        public TextBlock? Block => Element as TextBlock;

        public int Start { get; }

        public int Length { get; }

        /// <summary>这一段承载的文本。</summary>
        public string Text => Element switch
        {
            TextBox box => box.Text ?? string.Empty,
            TextBlock block => block.Text ?? string.Empty,
            _ => string.Empty,
        };
    }

    /// <summary>一行的文本布局：由几段拼成、一共多少字符。</summary>
    private sealed record RowLayout(List<RowPart> Parts, int Length);

    private int IndexOfItemAt(DependencyObject? source)
        => source is not null && ItemsControl.ContainerFromElement(LogList, source) is ListBoxItem container
            ? LogList.ItemContainerGenerator.IndexFromContainer(container)
            : -1;

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

    // ---------- 命中关键字的高亮 ----------

    private void OnLineTextLoaded(object sender, RoutedEventArgs e)
    {
        QueueHighlight(sender as FrameworkElement);
        QueueSelectionRepaint();
    }

    private void OnLineTextDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        QueueHighlight(sender as FrameworkElement);

        // 容器被回收去显示别的行了：这一行的选择底色要按当前选择重画（或擦掉）
        QueueSelectionRepaint();
    }

    private void OnLineTextSizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueHighlight(sender as FrameworkElement);
        QueueSelectionRepaint();
    }

    /// <summary>
    /// 排队重画一行的命中高亮。
    ///
    /// 为什么不能就地量：量字符矩形（<see cref="PartCharRect"/>）会强制一次文本布局
    /// （TextBlock 的字符矩形同样要 TextView 校验过才有值）。而 <c>DataContextChanged</c> 恰恰是在
    /// 虚拟化面板生成行容器的**过程中**触发的，这时再进布局就会撞上 WPF 的保护：
    /// <c>InvalidOperationException：无法在正在进行内容生成时调用 StartAt</c>——程序直接崩。
    /// （日志一边刷、查找框一边开着时必现。）改成用 Loaded 优先级排队，等这一轮布局走完再量。
    /// </summary>
    private void QueueHighlight(FrameworkElement? element)
    {
        // 时间列/方向列也在这条的触发路径上（它们同样是行内文字），但它们背后没有高亮层：
        // 不挡掉的话每次都会白量一遍字符坐标
        if (element is null || MatchLayerOf(element) is null)
        {
            return;
        }

        _pendingHighlights.Add(element);
        QueueHighlightFlush();
    }

    /// <summary>
    /// 整表重画：只用在"搜索条件/字号/换行方式变了"这种全局变化上（见 <see cref="OnSearchChanged"/>
    /// 与 <see cref="OnViewModelPropertyChanged"/>）。不要拿它给滚动/追加兜底——
    /// 那会让每追加一行都变成把屏幕上所有行重新量一遍，开着查找时界面 CPU 直接跑满。
    /// </summary>
    private void QueueAllHighlights()
    {
        _highlightAllQueued = true;
        QueueHighlightFlush();
    }

    private void QueueHighlightFlush()
    {
        if (_highlightFlushQueued || !IsLoaded)
        {
            return;
        }

        _highlightFlushQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FlushHighlights));
    }

    private void FlushHighlights()
    {
        _highlightFlushQueued = false;

        var all = _highlightAllQueued;
        _highlightAllQueued = false;

        FrameworkElement[] texts = [.. _pendingHighlights];
        _pendingHighlights.Clear();

        if (all)
        {
            RefreshMatchHighlights();
        }

        foreach (var text in texts)
        {
            UpdateMatchHighlight(text);
        }
    }

    /// <summary>
    /// 把当前关键字在**这一行里出现的位置**画成高亮方块。
    ///
    /// 做法：内容列是一个 Grid，底层 Canvas 画方块、上层是那行文本，
    /// 方块位置由该行文本自己的字符矩形量出来（见 <see cref="PartCharRect"/>），
    /// 所以自动换行时也会跟着文字走。文本本身的颜色一律不动（原来把当前命中整行刷成
    /// 强调色 + 白字，反而看不出命中在哪一段）。
    /// </summary>
    private void UpdateMatchHighlight(FrameworkElement? element)
    {
        if (element is null || MatchLayerOf(element) is not { } layer)
        {
            return;
        }

        layer.Children.Clear();

        var query = _viewModel?.SearchText;

        // 没有关键字（没搜索，或刚把搜索条关掉）时也要走到这里：
        // 上面那句 Clear 才是把上一次画的方块擦掉的地方，提前 return 会把高亮留在屏幕上
        if (element.DataContext is not DisplayLine line
            || string.IsNullOrEmpty(query)
            || !TryGetRowLayoutFor(element, out var layout)
            || FindPart(layout, element) is not { } part
            || string.IsNullOrEmpty(part.Text))
        {
            return;
        }

        var ranges = SearchMatchFinder.FindRanges(part.Text, query, _viewModel?.SearchCaseSensitive ?? false);

        if (ranges.Count == 0)
        {
            return;
        }

        var brush = (Brush)FindResource(
            line.IsCurrentMatch ? "CurrentMatchHighlightBrush" : "MatchHighlightBrush");

        foreach (var (start, length) in ranges)
        {
            foreach (var rect in MeasureRanges(part, start, length))
            {
                var mark = new Rectangle
                {
                    Width = rect.Width,
                    Height = rect.Height,
                    Fill = brush,
                    RadiusX = 2,
                    RadiusY = 2,
                };

                Canvas.SetLeft(mark, rect.Left);
                Canvas.SetTop(mark, rect.Top);
                layer.Children.Add(mark);
            }
        }
    }

    /// <summary>这一行（某个行内元素所在的行容器）的整行文本布局；拿不到容器时返回 false。</summary>
    private bool TryGetRowLayoutFor(DependencyObject element, out RowLayout layout)
    {
        if (ItemsControl.ContainerFromElement(LogList, element) is ListBoxItem container)
        {
            return TryGetRowLayout(container, out layout);
        }

        layout = default!;
        return false;
    }

    /// <summary>
    /// 内容行"背后那层高亮画布"。
    ///
    /// 必须按名字取，不能取"父面板里的第一个 Canvas"：行模板里还有一个给自由选择用的
    /// <c>SelectionLayer</c>（它是行根 Grid 的第一个子元素），按顺序取会把它当成高亮层清掉，
    /// 于是"选中底色"和"命中高亮"互相擦除。
    /// </summary>
    private static Canvas? MatchLayerOf(FrameworkElement element)
        => element.Parent is Panel host
            ? host.Children.OfType<Canvas>().FirstOrDefault(canvas => canvas.Name == MatchLayerName)
            : null;

    /// <summary>
    /// 量出一段字符占的矩形。自动换行时一段命中可能横跨两三个视觉行，
    /// 所以逐个字符量、同一视觉行上的合并成一个矩形（否则一次命中会画出上百个小方块）。
    /// </summary>
    private static List<Rect> MeasureRanges(RowPart part, int start, int length)
    {
        var rects = new List<Rect>();

        for (var i = start; i < start + length && i < part.Length; i++)
        {
            var rect = PartCharRect(part, i);

            if (rect.IsEmpty)
            {
                continue;
            }

            if (rects.Count > 0)
            {
                var last = rects[^1];

                if (Math.Abs(last.Top - rect.Top) < 0.5 && rect.Left >= last.Left)
                {
                    rects[^1] = new Rect(last.Left, last.Top, Math.Max(last.Right, rect.Right) - last.Left, last.Height);
                    continue;
                }
            }

            rects.Add(rect);
        }

        return rects;
    }

    /// <summary>
    /// 搜索条件或字号/换行方式变了：把所有已经生成出来的行重画一遍。
    ///
    /// 注意这里**不能**因为"当前没有关键字"就跳过：关掉搜索条也是一个需要重画的事件，
    /// 得让它把上一次画的方块擦掉（曾经因为提前 return，关掉搜索框后高亮一直留在屏幕上）。
    /// </summary>
    private void RefreshMatchHighlights()
    {
        foreach (var item in FindDescendants<ListBoxItem>(LogList))
        {
            if (FindDescendants<FrameworkElement>(item).FirstOrDefault(e => e.Name == LineTextName) is { } text)
            {
                UpdateMatchHighlight(text);
            }
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// 当前有没有"选中的字符"。文本框被虚拟化回收后会重新绑定到别的行，
    /// 那时选中内容已被清空，这里的长度判断顺便把这种失效引用挡掉。
    /// </summary>
    private void OnCopyCanExecute(object sender, CanExecuteRoutedEventArgs e)
        => e.CanExecute = HasFreeSelection() || LogList.SelectedItems.Count > 0;

    private void OnCopyExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        CopySelection();
        e.Handled = true;
    }

    /// <summary>
    /// 复制：优先复制选出来的那段字符（可以跨行、端点落在字符上），否则复制选中的整行。
    /// </summary>
    private void CopySelection()
    {
        if (HasFreeSelection())
        {
            var selected = ExtractSelectionText();
            var lines = SelectedTextLineCount;
            var what = lines > 1
                ? $"选中的文本（{selected.Length} 字 / {lines} 行）"
                : $"选中的文本（{selected.Length} 字）";

            _viewModel?.ReportCopyResult(what, ClipboardText.TrySet(selected));
            return;
        }

        var text = BuildSelectedText();

        if (text.Length == 0)
        {
            return;
        }

        var copied = ClipboardText.TrySet(text);
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

            builder.Append(line.ToDisplayText(withTimestamp)).Append(LogTextSelection.LineSeparator);
        }

        return builder.ToString();
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
