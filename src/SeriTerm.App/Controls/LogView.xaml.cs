using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using SeriTerm.App.ViewModels;
using SeriTerm.Core.Documents;
using SeriTerm.Core.Pipeline;
using SeriTerm.Core.Search;

namespace SeriTerm.App.Controls;

public partial class LogView : UserControl
{
    private const double BottomTolerance = 8;
    private const int TextBatchSize = 128 * 1024;

    // A serial record can contain multiple physical text lines.
    private readonly List<int> _recordStarts = [];
    private MainViewModel? _viewModel;
    private bool _followQueued;
    private bool _programmaticScroll;
    private ScrollBar? _dragBar;
    private bool _followOnRelease;

    public LogView()
    {
        InitializeComponent();
        LogText.Options.EnableHyperlinks = false;
        LogText.Options.EnableEmailHyperlinks = false;
        LogText.Options.EnableTextDragDrop = false;
        LogText.Options.HighlightCurrentLine = false;
        LogText.Document.UndoStack.SizeLimit = 0;
        LogText.TextArea.SetResourceReference(TextArea.SelectionBrushProperty, "SelectionBrush");
        LogText.TextArea.TextView.LineTransformers.Add(new LogColorizer(this));
        LogText.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        LogText.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler(OnScrollbarScroll), handledEventsToo: true);
        LogText.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnThumbDragStarted));
        LogText.PreviewMouseWheel += (_, e) =>
        {
            if (e.Delta > 0 && LogText.VerticalOffset > 0)
                _viewModel?.PauseAutoScroll();
        };
        LogText.TextArea.SelectionChanged += (_, _) =>
        {
            if (LogText.SelectionLength > 0)
                _viewModel?.PauseAutoScroll();
        };

        DataContextChanged += (_, e) => Attach(e.NewValue as MainViewModel);
        Loaded += (_, _) => Attach(DataContext as MainViewModel);
        Unloaded += (_, _) => Attach(null);
    }

    public bool IsLogTextFocused => LogText.IsKeyboardFocusWithin;

    public void FocusSearch()
    {
        if (_viewModel?.SearchVisible == true)
            QueueSearchFocus();
        else
            _viewModel?.OpenSearchCommand.Execute(null);
    }

    public void CloseSearch()
    {
        var restoreFocus = SearchOverlay.IsKeyboardFocusWithin;
        _viewModel?.CloseSearchCommand.Execute(null);
        if (restoreFocus)
            LogText.TextArea.Focus();
    }

    private void Attach(MainViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
            return;

        if (_viewModel is { } previous)
        {
            previous.Log.ContentChanged -= OnContentChanged;
            previous.Log.SearchChanged -= OnSearchChanged;
            previous.PropertyChanged -= OnViewModelPropertyChanged;
            previous.ScrollToLineRequested -= OnScrollToLineRequested;
            previous.ScrollToEndRequested -= OnScrollToEndRequested;
        }

        _viewModel = viewModel;
        if (viewModel is not null)
        {
            viewModel.Log.ContentChanged += OnContentChanged;
            viewModel.Log.SearchChanged += OnSearchChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.ScrollToLineRequested += OnScrollToLineRequested;
            viewModel.ScrollToEndRequested += OnScrollToEndRequested;
        }

        UpdateText(new(0, IsReset: true));
    }

    private void OnContentChanged(object? sender, LogContentChange change) => UpdateText(change);

    private void UpdateText(LogContentChange change)
    {
        var document = LogText.Document;
        var removeCount = change.IsReset ? _recordStarts.Count : Math.Min(change.RemovedLines, _recordStarts.Count);
        var removeLength = removeCount == _recordStarts.Count ? document.TextLength : _recordStarts[removeCount];
        var textView = LogText.TextArea.TextView;
        var anchor = removeLength > 0 && !change.IsReset && _viewModel?.AutoScroll == false && textView.VisualLinesValid
            ? textView.GetDocumentLineByVisualTop(LogText.VerticalOffset)
            : null;
        var anchorOffset = anchor?.Offset ?? 0;
        var offsetInLine = anchor is null ? 0 : LogText.VerticalOffset - textView.GetVisualTopByDocumentLine(anchor.LineNumber);

        using (document.RunUpdate())
        {
            if (removeCount > 0)
            {
                _recordStarts.RemoveRange(0, removeCount);
                for (var i = 0; i < _recordStarts.Count; i++)
                    _recordStarts[i] -= removeLength;

                document.Remove(0, removeLength);
            }

            if (_viewModel is { } viewModel)
                AppendText(document, viewModel.Log.Lines, viewModel.ShowTimestamp);
        }

        if (_viewModel?.AutoScroll == true)
        {
            QueueFollow();
        }
        else if (anchor is not null)
        {
            var offset = Math.Clamp(anchorOffset - removeLength, 0, document.TextLength);
            var line = document.GetLineByOffset(offset);
            var y = offset == 0 ? 0 : textView.GetVisualTopByDocumentLine(line.LineNumber) + offsetInLine;
            ScrollProgrammatically(() => LogText.ScrollToVerticalOffset(y));
        }
    }

    private void AppendText(TextDocument document, IReadOnlyList<DisplayLine> records, bool showTimestamp)
    {
        if (_recordStarts.Count == records.Count)
            return;

        var batch = new StringBuilder();
        for (var i = _recordStarts.Count; i < records.Count; i++)
        {
            _recordStarts.Add(document.TextLength + batch.Length);
            batch.Append(records[i].ToDisplayText(showTimestamp)).AppendLine();
            if (batch.Length >= TextBatchSize)
                FlushBatch();
        }
        FlushBatch();

        void FlushBatch()
        {
            if (batch.Length == 0)
                return;

            document.Insert(document.TextLength, batch.ToString(), AnchorMovementType.BeforeInsertion);
            batch.Clear();
        }
    }

    private void OnSearchChanged(object? sender, EventArgs e) => LogText.TextArea.TextView.Redraw();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ShowTimestamp):
                UpdateText(new(0, IsReset: true));
                break;
            case nameof(MainViewModel.SearchVisible) when _viewModel?.SearchVisible == true:
                QueueSearchFocus();
                break;
        }
    }

    private void QueueSearchFocus() => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
    {
        if (_viewModel?.SearchVisible != true || !SearchBox.IsVisible)
            return;

        SearchBox.Focus();
        SearchBox.SelectAll();
    }));

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
            return;

        switch (e.Key)
        {
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                _viewModel.SearchPreviousCommand.Execute(null);
                break;
            case Key.Enter:
                _viewModel.SearchNextCommand.Execute(null);
                break;
            case Key.Escape:
                CloseSearch();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnCloseSearchClick(object sender, RoutedEventArgs e) => CloseSearch();

    private void OnScrollToLineRequested(object? sender, DisplayLine? record)
    {
        if (record is null || _viewModel is not { } viewModel)
            return;

        var index = viewModel.Log.Lines.IndexOf(record);
        if (index < 0 || index >= _recordStarts.Count)
            return;

        var hit = record.Text.IndexOf(viewModel.SearchText, SearchMatchFinder.Comparison(viewModel.SearchCaseSensitive));
        var offset = _recordStarts[index] + PrefixLength(record, viewModel.ShowTimestamp) + Math.Max(0, hit);
        var location = LogText.Document.GetLocation(offset);
        ScrollProgrammatically(() => LogText.ScrollTo(location.Line, location.Column));
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e) => QueueFollow();

    private void OnThumbDragStarted(object sender, DragStartedEventArgs e)
    {
        if (e.OriginalSource is not Thumb { TemplatedParent: ScrollBar { Orientation: Orientation.Vertical } bar } thumb)
            return;

        _dragBar = bar;
        _followOnRelease = _viewModel?.AutoScroll == true;
        _viewModel?.PauseAutoScroll();
        thumb.DragDelta += OnVerticalDrag;
        thumb.DragCompleted += OnVerticalDragCompleted;
    }

    private void OnVerticalDrag(object sender, DragDeltaEventArgs e)
    {
        if (_dragBar is not { } bar)
            return;

        // Handle this on the captured thumb, before ScrollBar can snap a sideways drag back to its origin.
        e.Handled = true;
        if (e.VerticalChange == 0)
            return;

        var distance = bar.Track.ValueFromDistance(0, e.VerticalChange);
        if (!double.IsFinite(distance))
            return;

        var target = Math.Clamp(bar.Value + distance, bar.Minimum, bar.Maximum);
        _followOnRelease = target >= bar.Maximum - BottomTolerance;
        LogText.ScrollToVerticalOffset(target);
    }

    private void OnVerticalDragCompleted(object sender, DragCompletedEventArgs e)
    {
        var thumb = (Thumb)sender;
        thumb.DragDelta -= OnVerticalDrag;
        thumb.DragCompleted -= OnVerticalDragCompleted;
        _dragBar = null;
        if (!e.Canceled && _followOnRelease)
            _viewModel?.ResumeAutoScroll();
    }

    private void OnScrollbarScroll(object sender, ScrollEventArgs e)
    {
        if (e.OriginalSource is ScrollBar { Orientation: Orientation.Vertical } bar
            && e.NewValue < bar.Maximum - BottomTolerance)
        {
            _viewModel?.PauseAutoScroll();
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_dragBar is not null || _viewModel is not { } viewModel || e.OriginalSource is not ScrollViewer viewer)
            return;

        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            if (viewModel.AutoScroll && viewer.VerticalOffset < viewer.ScrollableHeight)
                ScrollProgrammatically(viewer.ScrollToBottom);
            return;
        }

        if (_programmaticScroll || e.VerticalChange == 0 || viewer.ScrollableHeight <= 0)
            return;

        if (viewer.VerticalOffset >= viewer.ScrollableHeight - BottomTolerance)
        {
            if (!viewModel.AutoScroll)
                viewModel.ResumeAutoScroll();
        }
        else if (viewModel.AutoScroll)
        {
            viewModel.PauseAutoScroll();
        }
    }

    private void QueueFollow()
    {
        if (_followQueued)
            return;

        _followQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _followQueued = false;
            if (_dragBar is null && _viewModel?.AutoScroll == true && LogText.ExtentHeight > LogText.ViewportHeight)
                ScrollProgrammatically(() => LogText.ScrollToVerticalOffset(double.PositiveInfinity));
        }));
    }

    private void ScrollProgrammatically(Action scroll)
    {
        _programmaticScroll = true;
        scroll();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => _programmaticScroll = false));
    }

    private static int PrefixLength(DisplayLine record, bool showTimestamp) => showTimestamp ? record.TimeText.Length + 4 : 3;

    private sealed class LogColorizer(LogView view) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (view._viewModel is not { } model || line.Length == 0 || view._recordStarts.Count == 0)
                return;

            var index = view._recordStarts.BinarySearch(line.Offset);
            index = index < 0 ? ~index - 1 : index;
            var record = model.Log.Lines[index];
            var textStart = view._recordStarts[index] + PrefixLength(record, model.ShowTimestamp);
            var contentStart = Math.Max(textStart, line.Offset);

            ColorForeground(line.Offset, Math.Min(line.EndOffset, contentStart), "SubtleForegroundBrush");
            if (record.Direction != LineDirection.Rx)
            {
                var brushKey = record.Direction == LineDirection.Tx ? "TxForegroundBrush" : "SubtleForegroundBrush";
                ColorForeground(contentStart, line.EndOffset, brushKey);
            }

            if (!record.IsMatch || model.SearchText.Length == 0 || contentStart >= line.EndOffset)
                return;

            var brush = view.TryFindResource(record.IsCurrentMatch ? "CurrentMatchHighlightBrush" : "MatchHighlightBrush") as Brush;
            if (brush is null)
                return;

            var query = model.SearchText;
            var comparison = SearchMatchFinder.Comparison(model.SearchCaseSensitive);
            var start = Math.Max(0, contentStart - textStart - query.Length + 1);
            var end = Math.Min(record.Text.Length, line.EndOffset - textStart + query.Length - 1);
            while (start <= end - query.Length)
            {
                var hit = record.Text.IndexOf(query, start, end - start, comparison);
                if (hit < 0)
                    break;

                start = hit + query.Length;
                var from = Math.Max(contentStart, textStart + hit);
                var to = Math.Min(line.EndOffset, textStart + start);
                if (from < to)
                    ChangeLinePart(from, to, e => e.TextRunProperties.SetBackgroundBrush(brush));
            }
        }

        private void ColorForeground(int start, int end, string brushKey)
        {
            if (start < end && view.TryFindResource(brushKey) is Brush brush)
                ChangeLinePart(start, end, e => e.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}
