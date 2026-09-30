using System.Collections.Specialized;
using System.ComponentModel;
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

    private MainViewModel? _viewModel;
    private bool _suppressScrollClassification;
    private bool _scrollToEndQueued;

    public LogView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();

        // ScrollChanged 会从 ListBox 模板内部的 ScrollViewer 冒泡上来
        LogList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

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
