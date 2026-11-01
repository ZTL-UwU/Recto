using Recto.Pdf;
using Recto.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using ZoomMode = Recto.Services.ZoomMode;

namespace Recto.Controls;

/// <summary>
/// The scrolling, zoomable document surface. Pages are laid out at 100% (1pt = 96/72 DIP) and
/// the ScrollViewer's zoom factor is the user-visible zoom level; pages are re-rendered at the
/// effective resolution whenever zooming settles.
/// </summary>
public sealed partial class DocumentView : UserControl
{
    public const double BaseScale = 96.0 / 72.0;
    public const double MinZoom = 0.1;
    public const double MaxZoom = 10;

    private sealed record Row(int[] Pages, double Top, double Bottom);

    private readonly record struct TextPosition(int Page, int Caret) : IComparable<TextPosition>
    {
        public int CompareTo(TextPosition other) => Page != other.Page ? Page.CompareTo(other.Page) : Caret.CompareTo(other.Caret);
    }

    private readonly Grid _root = new();
    private readonly ScrollViewer _scroller;
    private readonly Canvas _host = new() { Background = new SolidColorBrush(Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _statusPill;
    private readonly TextBlock _statusText = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 };
    private readonly Dictionary<int, PageView> _views = [];
    private readonly Dictionary<int, PageText> _texts = [];
    /// <summary>Text loads in progress, cancelled when their page leaves the view.</summary>
    private readonly Dictionary<int, (CancellationTokenSource Cts, WorkPriority Priority)> _textLoading = [];
    private readonly Dictionary<int, List<SearchHit>> _searchHits = [];
    private List<Row> _rows = [];
    private Rect[] _pageRects = [];
    private PdfDocument? _document;

    private int _rotation;
    private bool _continuous = true;
    private bool _dual;
    private bool _oddPagesLeft;
    private bool _inverted;
    private bool _presentation;
    private (bool Continuous, bool Dual, ZoomMode Mode, double Zoom) _beforePresentation;
    private ZoomMode _zoomMode = ZoomMode.FitWidth;
    private int _currentPage;
    private int _currentRow;
    private double _lastZoom = 1;
    private float? _pendingZoom;
    /// <summary>Position (in unzoomed content coordinates) of a scroll request that hasn't been applied yet.</summary>
    private double? _pendingContentX, _pendingContentY;
    private SearchHit? _currentHit;
    /// <summary>The content in view when pages were last realized, to tell a scroll from a jump.</summary>
    private Rect _lastVisible = Rect.Empty;
    /// <summary>Realizes the pages around the view once it has stopped jumping.</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _prefetchTimer;

    private TextPosition? _selectionAnchor;
    private TextPosition? _selectionFocus;
    private bool _pointerDown;
    private bool _dragging;
    private Point _pressPoint;
    private PdfLink? _pressedLink;
    /// <summary>Where a selection starts if the press on <see cref="_pressedLink"/> turns into a drag.</summary>
    private TextPosition? _linkDragAnchor;
    private InputSystemCursorShape _cursor = InputSystemCursorShape.Arrow;

    // Middle-click scrolling: the view scrolls towards the pointer, faster the further it is from
    // where the button was pressed. A click leaves it on until the next click or key press;
    // holding the button and moving scrolls until it's released.
    private const double MiddleScrollDeadZone = 12;
    private const double MiddleScrollMarkerSize = 32;
    private readonly Canvas _middleScrollMarker = new() { Width = MiddleScrollMarkerSize, Height = MiddleScrollMarkerSize, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private bool _middleScroll;
    private bool _middleScrollHeld;
    private bool _swallowRightTap;
    private Point _middleScrollOrigin, _middleScrollPointer;
    private double _middleScrollX, _middleScrollY;
    private long _middleScrollTime;

    private readonly Brush _selectionBrush;
    private readonly Brush _searchBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xD0, 0x00));
    /// <summary>Drawn over the current hit's search highlight; the two together come to #90FF8000 on any page.</summary>
    private readonly Brush _currentSearchBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0x3A, 0x00));

    public DocumentView()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var a) && a is Color c ? c : Color.FromArgb(255, 0, 103, 192);
        _selectionBrush = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));
        _frame = new SelectionFrame(accent);

        _scroller = new ScrollViewer
        {
            ZoomMode = Microsoft.UI.Xaml.Controls.ZoomMode.Enabled,
            MinZoomFactor = (float)MinZoom,
            MaxZoomFactor = (float)MaxZoom,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            IsTabStop = false,
            Content = _host,
        };
        _scroller.ViewChanged += OnViewChanged;
        _prefetchTimer = DispatcherQueue.CreateTimer();
        _prefetchTimer.Interval = TimeSpan.FromMilliseconds(100);
        _prefetchTimer.IsRepeating = false;
        _scroller.SizeChanged += (_, _) => OnViewportSizeChanged();
        ScrollBarHideFix.Attach(_scroller);
        _root.Children.Add(_scroller);
        _root.Children.Add(_middleScrollMarker);

        _statusPill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12),
            Padding = new Thickness(10, 5, 10, 6),
            CornerRadius = new CornerRadius(6),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _statusText,
            Style = (Style)Application.Current.Resources["FloatingBorderStyle"],
        };
        _statusText.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _root.Children.Add(_statusPill);
        Content = _root;

        _host.PointerPressed += OnPointerPressed;
        _host.PointerMoved += OnPointerMoved;
        _host.PointerReleased += OnPointerReleased;
        _host.PointerCaptureLost += (_, e) =>
        {
            if (e.Pointer.PointerId == _gesturePointer) FinishToolGesture();
            _pointerDown = false;
            _dragging = false;
            StopAutoScroll();
        };
        _host.PointerExited += (_, _) => { if (!_pointerDown && !_middleScroll) { SetStatus(null); SetCursor(InputSystemCursorShape.Arrow); } };
        _host.RightTapped += OnRightTapped;
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        AddHandler(KeyUpEvent, new KeyEventHandler(OnKeyUp), true);
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), true);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnMiddleScrollPointerPressed), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnMiddleScrollPointerMoved), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnMiddleScrollPointerReleased), true);
        PointerCaptureLost += (_, _) => { if (_middleScrollHeld) StopMiddleScroll(); };
        LostFocus += (_, _) => { StopMiddleScroll(); StopAutoScroll(); StopKeyScroll(); };
        // Neither XamlRoot nor the timer is a XAML element, so the garbage collector can't trace a cycle
        // through their handlers. Detach so they can't keep the view, and the window it was in, alive after closing.
        Loaded += (_, _) =>
        {
            if (_xamlRoot is not null || XamlRoot is null) return;
            _xamlRoot = XamlRoot;
            _xamlRoot.Changed += OnXamlRootChanged;
            _prefetchTimer.Tick += OnPrefetchTimerTick;
        };
        Unloaded += (_, _) =>
        {
            StopMiddleScroll();
            StopAutoScroll();
            StopKeyScroll();
            Detach();
        };
    }

    private XamlRoot? _xamlRoot;

    /// <summary>Stops listening to the window's XamlRoot and timers. Closing a window doesn't unload its content, so it calls this.</summary>
    public void Detach()
    {
        if (_xamlRoot is null) return;
        _xamlRoot.Changed -= OnXamlRootChanged;
        _xamlRoot = null;
        _prefetchTimer.Stop();
        _prefetchTimer.Tick -= OnPrefetchTimerTick;
    }

    private void OnPrefetchTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => UpdateRealization(true);

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => RefreshRendering();

    // ---------------------------------------------------------------- public surface

    public event EventHandler? CurrentPageChanged;
    public event EventHandler? ZoomChanged;
    public event EventHandler? ViewSettingsChanged;
    public event EventHandler? SelectionChanged;
    /// <summary>Raised when the user edited annotations.</summary>
    public event EventHandler<int>? AnnotationsChanged;
    /// <summary>Raised when the user asked to search for the selected text.</summary>
    public event EventHandler? SearchRequested;
    /// <summary>Raised with a message when an annotation edit couldn't be made.</summary>
    public event EventHandler<string>? EditFailed;

    public PdfDocument? Document => _document;
    public int CurrentPage => _currentPage;
    public double Zoom => _pendingZoom ?? _scroller.ZoomFactor;
    public ZoomMode ZoomMode => _zoomMode;
    public int PageRotation => _rotation;
    public bool IsContinuous => _continuous;
    public bool IsDual => _dual;
    public bool OddPagesLeft => _oddPagesLeft;
    public bool IsInverted => _inverted;
    public bool IsPresenting => _presentation;
    public bool HasSelection => GetSelection() is { } s && s.Start.CompareTo(s.End) < 0;
    /// <summary>Whether an open note or text box has text that isn't in the document yet.</summary>
    public bool HasDrafts => _noteEdits.Any(e => e.IsChanged) || _textEdit is { IsChanged: true };
    /// <summary>Whether a draft, or a drawing still being written, isn't in the document yet.</summary>
    public bool HasPendingEdits => HasDrafts || !_drawingEdits.IsCompleted;
    /// <summary>Changes whenever an open note's or text box's text does or a drawing edit starts, to tell whether anything was done since it was last read.</summary>
    public int DraftVersion { get; private set; }

    public void SetDocument(PdfDocument? document, RecentDocument? restore = null)
    {
        StopMiddleScroll();
        StopAutoScroll();
        StopKeyScroll();
        ResetDrawing();
        _noteEdits.Clear();
        _undo.Clear();
        _redo.Clear();
        foreach (var view in _views.Values) view.Release();
        _views.Clear();
        _host.Children.Clear();
        _texts.Clear();
        foreach (var loading in _textLoading.Values) loading.Cts.Cancel();
        _textLoading.Clear();
        _lastVisible = Rect.Empty;
        _searchHits.Clear();
        _currentHit = null;
        ClearSelection();
        _document = document;
        _currentPage = 0;
        _currentRow = 0;
        _rotation = 0;
        UpdateAutomationName();
        if (document is null)
        {
            Relayout();
            return;
        }

        var prefs = AppState.Preferences;
        _continuous = restore?.Continuous ?? prefs.Continuous;
        _dual = restore?.Dual ?? prefs.Dual;
        _oddPagesLeft = prefs.OddPagesLeft;
        _inverted = prefs.Inverted;
        _zoomMode = restore?.ZoomMode ?? ZoomMode.FitWidth;
        _rotation = restore?.Rotation ?? 0;
        UpdateHostAlignment();
        int page = Math.Clamp(restore?.Page ?? 0, 0, Math.Max(0, document.PageCount - 1));
        _currentPage = page;
        UpdateAutomationName();
        Relayout();
        UpdateLayout();
        if (_zoomMode == ZoomMode.Custom) SetZoom(restore?.Zoom ?? 1, keepMode: true);
        else ApplyZoomMode();
        GoToPage(page);
        UpdateRealization(true);
    }

    public void SetContinuous(bool value)
    {
        if (_continuous == value) return;
        _continuous = value;
        UpdateHostAlignment();
        int page = _currentPage;
        _currentRow = RowIndexOf(page);
        Relayout();
        GoToPage(page);
        ViewSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetDual(bool value)
    {
        if (_dual == value) return;
        _dual = value;
        RelayoutKeepingPage();
    }

    public void SetOddPagesLeft(bool value)
    {
        if (_oddPagesLeft == value) return;
        _oddPagesLeft = value;
        RelayoutKeepingPage();
    }

    public void SetInverted(bool value)
    {
        if (_inverted == value) return;
        _inverted = value;
        foreach (var view in _views.Values) view.SetPaperColor(value ? Color.FromArgb(255, 30, 30, 30) : Colors.White);
        RestyleDrafts();
        RefreshRendering();
        ViewSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies the page corner preference to the pages on screen.</summary>
    public void RefreshPageFrames()
    {
        foreach (var view in _views.Values) view.RefreshFrame();
    }

    public void Rotate(int delta)
    {
        _rotation = ((_rotation + delta) % 360 + 360) % 360;
        RelayoutKeepingPage();
    }

    private void RelayoutKeepingPage()
    {
        AbandonGestures();
        int page = _currentPage;
        Relayout();
        UpdateLayout();
        ApplyZoomMode();
        GoToPage(page);
        ViewSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetZoomMode(ZoomMode mode)
    {
        _zoomMode = mode;
        ApplyZoomMode();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ZoomIn() => SetZoom(NextZoomStep(Zoom, +1));
    public void ZoomOut() => SetZoom(NextZoomStep(Zoom, -1));

    private static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6, 8, 10];

    private static double NextZoomStep(double current, int direction)
    {
        if (direction > 0) return ZoomSteps.FirstOrDefault(s => s > current * 1.01, MaxZoom);
        return ZoomSteps.LastOrDefault(s => s < current * 0.99, MinZoom);
    }

    public void SetZoom(double zoom, Point? anchor = null, bool keepMode = false)
    {
        if (!keepMode) _zoomMode = ZoomMode.Custom;
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        double vw = _scroller.ViewportWidth, vh = _scroller.ViewportHeight;
        var a = anchor ?? new Point(vw / 2, vh / 2);
        double z = _scroller.ZoomFactor;
        // Keep the content under the anchor in place, allowing for the gap around content that's
        // centred because it's smaller than the view, before and after.
        var gap = CenteringGap(z);
        double cx = (_scroller.HorizontalOffset + a.X - gap.X) / z;
        double cy = (_scroller.VerticalOffset + a.Y - gap.Y) / z;
        var newGap = CenteringGap(zoom);
        _pendingZoom = (float)zoom;
        ScrollTo(Math.Max(0, cx * zoom - a.X + newGap.X), Math.Max(0, cy * zoom - a.Y + newGap.Y), (float)zoom, true);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where the content starts in the view at a zoom when it's smaller than the view and centred.</summary>
    private Point CenteringGap(double zoom)
    {
        // The host has no size until the first layout.
        double w = double.IsNaN(_host.Width) ? 0 : _host.Width, h = double.IsNaN(_host.Height) ? 0 : _host.Height;
        return new(Math.Max(0, (_scroller.ViewportWidth - w * zoom) / 2),
            _host.VerticalAlignment == VerticalAlignment.Center ? Math.Max(0, (_scroller.ViewportHeight - h * zoom) / 2) : 0);
    }

    public void GoToPage(int index, Point? location = null)
    {
        if (_document is null) return;
        index = Math.Clamp(index, 0, _document.PageCount - 1);
        if (!_continuous || _presentation)
        {
            int row = RowIndexOf(index);
            if (row != _currentRow || _pageRects[index].IsEmpty)
            {
                _currentRow = row;
                Relayout();
                UpdateLayout();
                if (_zoomMode != ZoomMode.Custom) ApplyZoomMode();
            }
        }
        var rect = _pageRects[index];
        if (rect.IsEmpty) return;
        double z = Zoom;
        double? h = null;
        double v;
        if (location is { } loc)
        {
            var p = RotatedLayoutPoint(index, loc);
            v = (rect.Top + p.Y) * z - 12;
            if (rect.Width * z > _scroller.ViewportWidth)
                h = Math.Max(0, (rect.Left + p.X) * z - 24);
        }
        else
        {
            // First row: scroll to the very top so the full top margin shows.
            v = (rect.Top <= TopMargin + 1 ? 0 : rect.Top - (_presentation ? 0 : Spacing)) * z;
        }
        ScrollTo(h, Math.Max(0, v), null, true);
        SetCurrentPage(index);
    }

    public async void GoToDestination(PdfDestination destination)
    {
        if (_document is null) return;
        if (!destination.IsInternal)
        {
            if (destination.Uri is { } uri) await LaunchUriAsync(uri);
            return;
        }
        if (destination.UserY is null && destination.UserX is null)
        {
            GoToPage(destination.PageIndex);
            return;
        }
        var document = _document;
        try
        {
            var t = await document.GetTransformAsync(destination.PageIndex);
            if (document != _document) return;
            var p = t.Apply(destination.UserX ?? 0, destination.UserY ?? 0);
            var size = document.PageSizes[destination.PageIndex];
            GoToPage(destination.PageIndex, new Point(
                destination.UserX is null ? 0 : Math.Clamp(p.X, 0, size.Width),
                destination.UserY is null ? 0 : Math.Clamp(p.Y, 0, size.Height)));
        }
        // Closed meanwhile, or the target page couldn't be loaded.
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException)
        {
        }
    }

    public void NextPage()
    {
        if (_document is null) return;
        if (_dual && (!_continuous || _presentation))
        {
            if (_currentRow + 1 < _rows.Count) GoToPage(_rows[_currentRow + 1].Pages[0]);
            return;
        }
        int next = _dual ? _rows[Math.Min(_rows.Count - 1, RowIndexOf(_currentPage) + 1)].Pages[0] : _currentPage + 1;
        if (next < _document.PageCount) GoToPage(next);
    }

    public void PreviousPage()
    {
        if (_document is null) return;
        if (_dual)
        {
            int row = RowIndexOf(_currentPage);
            if (row > 0) GoToPage(_rows[row - 1].Pages[0]);
            else GoToPage(0);
            return;
        }
        if (_currentPage > 0) GoToPage(_currentPage - 1);
        else GoToPage(0);
    }

    public void SetPresentation(bool value)
    {
        if (_presentation == value || _document is null) return;
        StopMiddleScroll();
        StopAutoScroll();
        StopKeyScroll();
        int page = _currentPage;
        if (value)
        {
            SetTool(AnnotationTool.None, _toolColor, _toolSize);
            _ = CommitTextEditAsync();
            Deselect();
            HideFrame();
            _beforePresentation = (_continuous, _dual, _zoomMode, Zoom);
            _presentation = true;
            _zoomMode = ZoomMode.FitPage;
            _dual = false;
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            _root.Background = new SolidColorBrush(Colors.Black);
            ClearSelection();
        }
        else
        {
            _presentation = false;
            (_continuous, _dual, _zoomMode, double zoom) = _beforePresentation;
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _root.Background = null;
            _lastZoom = zoom;
        }
        UpdateHostAlignment();
        foreach (var view in _views.Values) view.Release();
        _views.Clear();
        _host.Children.Clear();
        // A text box that couldn't be saved stays open until it can be.
        if (_textEdit is { } edit) _host.Children.Add(edit.Box);
        _currentRow = RowIndexOf(page);
        Relayout();
        UpdateLayout();
        if (_zoomMode == ZoomMode.Custom) SetZoom(_lastZoom, keepMode: true);
        else ApplyZoomMode();
        GoToPage(page);
        Focus(FocusState.Programmatic);
    }

    /// <summary>Called after the document was saved/reloaded or annotations changed on a page.</summary>
    public void InvalidatePage(int index)
    {
        _texts.Remove(index);
        if (_views.TryGetValue(index, out var view))
        {
            view.Invalidate();
            EnsureText(index);
        }
        RefreshRendering();
    }

    // ---------------------------------------------------------------- search

    public void AddSearchResults(IEnumerable<SearchHit> hits)
    {
        var touched = new HashSet<int>();
        foreach (var hit in hits)
        {
            if (!_searchHits.TryGetValue(hit.PageIndex, out var list)) _searchHits[hit.PageIndex] = list = [];
            list.Add(hit);
            touched.Add(hit.PageIndex);
        }
        foreach (var page in touched) UpdateOverlay(page, Overlays.Search);
    }

    public void ClearSearch()
    {
        _searchHits.Clear();
        _currentHit = null;
        UpdateAllOverlays(Overlays.Search | Overlays.CurrentHit);
    }

    public async void ShowSearchHit(SearchHit hit)
    {
        var previous = _currentHit;
        _currentHit = hit;
        if (previous is not null && previous.PageIndex != hit.PageIndex) UpdateOverlay(previous.PageIndex, Overlays.CurrentHit);
        var text = await GetTextAsync(hit.PageIndex);
        if (text is null || _currentHit != hit) return;
        var bounds = Union(text.Layout.SourceRangeRects(hit.CharIndex, hit.CharIndex + hit.Length));
        EnsurePageVisible(hit.PageIndex, bounds);
        UpdateOverlay(hit.PageIndex, Overlays.CurrentHit);
    }

    private void EnsurePageVisible(int index, Rect displayBounds)
    {
        if (!_continuous || _presentation) GoToPage(index);
        var target = LayoutRect(index, displayBounds);
        if (target.IsEmpty) { GoToPage(index); return; }
        double z = Zoom;
        double vw = _scroller.ViewportWidth / z, vh = _scroller.ViewportHeight / z;
        double left = _scroller.HorizontalOffset / z, top = _scroller.VerticalOffset / z;
        double? h = null, v = null;
        if (target.Top < top + 20 || target.Bottom > top + vh - 20) v = (target.Top - vh / 3) * z;
        if (target.Left < left || target.Right > left + vw) h = (target.Left - vw / 3) * z;
        if (h is not null || v is not null) ScrollTo(h is null ? null : Math.Max(0, h.Value), v is null ? null : Math.Max(0, v.Value), null, true);
        SetCurrentPage(index);
    }

    /// <summary>Where <paramref name="displayBounds"/> on page <paramref name="index"/> is laid out, or empty if the page isn't.</summary>
    private Rect LayoutRect(int index, Rect displayBounds)
    {
        var rect = _pageRects[index];
        if (rect.IsEmpty || displayBounds.IsEmpty) return Rect.Empty;
        var tl = RotatedLayoutPoint(index, new Point(displayBounds.Left, displayBounds.Top));
        var br = RotatedLayoutPoint(index, new Point(displayBounds.Right, displayBounds.Bottom));
        return new Rect(new Point(rect.Left + tl.X, rect.Top + tl.Y), new Point(rect.Left + br.X, rect.Top + br.Y));
    }

    // ---------------------------------------------------------------- layout

    // Space around the pages (in 100% layout DIPs, so it scales with zoom).
    private double SideMargin => _presentation ? 0 : 48;
    private double TopMargin => _presentation ? 0 : 28;
    private double Spacing => _presentation ? 0 : 14;

    /// <summary>A page shown on its own sits in the middle of the view when it's smaller than it.</summary>
    private void UpdateHostAlignment() =>
        _host.VerticalAlignment = !_continuous || _presentation ? VerticalAlignment.Center : VerticalAlignment.Top;

    private Size LayoutSize(int index)
    {
        var s = _document!.PageSizes[index];
        return _rotation is 90 or 270 ? new Size(s.Height * BaseScale, s.Width * BaseScale) : new Size(s.Width * BaseScale, s.Height * BaseScale);
    }

    private Point RotatedLayoutPoint(int index, Point display)
    {
        var s = _document!.PageSizes[index];
        var q = _rotation switch
        {
            90 => new Point(s.Height - display.Y, display.X),
            180 => new Point(s.Width - display.X, s.Height - display.Y),
            270 => new Point(display.Y, s.Width - display.X),
            _ => display,
        };
        return new Point(q.X * BaseScale, q.Y * BaseScale);
    }

    private List<int[]> BuildRowPages()
    {
        var rows = new List<int[]>();
        int n = _document?.PageCount ?? 0;
        if (!_dual)
        {
            for (int i = 0; i < n; i++) rows.Add([i]);
            return rows;
        }
        int start = 0;
        if (!_oddPagesLeft && n > 0)
        {
            rows.Add([0]);
            start = 1;
        }
        for (int i = start; i < n; i += 2)
            rows.Add(i + 1 < n ? [i, i + 1] : [i]);
        return rows;
    }

    private bool IsLeftPage(int index) => _oddPagesLeft ? index % 2 == 0 : index % 2 == 1;

    private int RowIndexOf(int page)
    {
        if (!_dual) return page;
        if (_oddPagesLeft) return page / 2;
        return page == 0 ? 0 : (page + 1) / 2;
    }

    private void Relayout()
    {
        if (_document is null)
        {
            _rows = [];
            _pageRects = [];
            _host.Width = 0;
            _host.Height = 0;
            return;
        }
        int n = _document.PageCount;
        var rowPages = BuildRowPages();
        _pageRects = new Rect[n];
        for (int i = 0; i < n; i++) _pageRects[i] = Rect.Empty;

        // Widths of the left and right columns so that spreads share a common spine.
        double maxLeft = 0, maxRight = 0, maxSingle = 0;
        foreach (var pages in rowPages)
        {
            foreach (var p in pages)
            {
                var s = LayoutSize(p);
                if (!_dual) maxSingle = Math.Max(maxSingle, s.Width);
                else if (IsLeftPage(p)) maxLeft = Math.Max(maxLeft, s.Width);
                else maxRight = Math.Max(maxRight, s.Width);
            }
        }
        double contentWidth = _dual ? SideMargin * 2 + maxLeft + Spacing + maxRight : SideMargin * 2 + maxSingle;
        double spine = SideMargin + maxLeft + Spacing / 2;

        _currentRow = Math.Clamp(_currentRow, 0, Math.Max(0, rowPages.Count - 1));
        var rows = new List<Row>(rowPages.Count);
        double y = TopMargin;
        for (int r = 0; r < rowPages.Count; r++)
        {
            var pages = rowPages[r];
            bool laidOut = (_continuous && !_presentation) || r == _currentRow;
            double height = pages.Max(p => LayoutSize(p).Height);
            if (!laidOut)
            {
                rows.Add(new Row(pages, double.NaN, double.NaN));
                continue;
            }
            foreach (var p in pages)
            {
                var s = LayoutSize(p);
                double x = !_dual ? (contentWidth - s.Width) / 2 : IsLeftPage(p) ? spine - Spacing / 2 - s.Width : spine + Spacing / 2;
                _pageRects[p] = new Rect(Math.Round(x), Math.Round(y + (height - s.Height) / 2), s.Width, s.Height);
            }
            rows.Add(new Row(pages, y, y + height));
            y += height + Spacing;
        }
        _rows = rows;
        _host.Width = Math.Ceiling(contentWidth);
        _host.Height = Math.Ceiling(y - Spacing + TopMargin);

        foreach (var (index, view) in _views.ToList())
        {
            if (_pageRects[index].IsEmpty)
            {
                RemoveView(index);
                continue;
            }
            view.Arrange(_pageRects[index], BaseScale, _rotation);
            UpdateOverlay(index);
        }
        // In paged modes the scroll offset often stays the same, so no ViewChanged will follow.
        UpdateRealization(true);
        PlaceOverlays();
    }

    private double FitWidthZoom()
    {
        if (_document is null || _host.Width <= 0) return 1;
        double vw = _scroller.ViewportWidth > 0 ? _scroller.ViewportWidth : ActualWidth;
        return Math.Clamp(vw / _host.Width, MinZoom, MaxZoom);
    }

    private double FitPageZoom()
    {
        if (_document is null || _rows.Count == 0) return 1;
        double vh = _scroller.ViewportHeight > 0 ? _scroller.ViewportHeight : ActualHeight;
        var row = _rows[Math.Clamp(RowIndexOf(_currentPage), 0, _rows.Count - 1)];
        double height = row.Pages.Max(p => LayoutSize(p).Height) + TopMargin * 2;
        return Math.Clamp(Math.Min(FitWidthZoom(), vh / height), MinZoom, MaxZoom);
    }

    private void ApplyZoomMode()
    {
        if (_document is null || _zoomMode == ZoomMode.Custom || _scroller.ViewportWidth <= 0) return;
        double target = _zoomMode == ZoomMode.FitWidth ? FitWidthZoom() : FitPageZoom();
        if (Math.Abs(target - Zoom) < 0.0005) return;
        // Keep the top of the current viewport stable.
        double cy = _pendingContentY ?? _scroller.VerticalOffset / _scroller.ZoomFactor;
        _pendingZoom = (float)target;
        ScrollTo(0, cy * target, (float)target, true);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewportSizeChanged()
    {
        StopMiddleScroll();
        StopAutoScroll();
        StopKeyScroll();
        if (_zoomMode != ZoomMode.Custom) ApplyZoomMode();
        UpdateRealization(true);
    }

    // ---------------------------------------------------------------- realization

    private void ScrollTo(double? h, double? v, float? zoom, bool disableAnimation)
    {
        // A later ChangeView without a zoom factor would cancel a pending zoom change.
        zoom ??= _pendingZoom;
        float factor = _scroller.ZoomFactor;
        bool sameZoom = Math.Abs((zoom ?? factor) - factor) < 1e-4;
        bool sameH = h is null || Math.Abs(Math.Clamp(h.Value, 0, _scroller.ScrollableWidth) - _scroller.HorizontalOffset) < 0.5;
        bool sameV = v is null || Math.Abs(Math.Clamp(v.Value, 0, _scroller.ScrollableHeight) - _scroller.VerticalOffset) < 0.5;
        if (sameZoom && sameH && sameV)
        {
            // Nothing changes, so no ViewChanged would arrive to clear the pending view.
            _pendingZoom = null;
            _pendingContentX = _pendingContentY = null;
        }
        else
        {
            if (h is { } x) _pendingContentX = x / (zoom ?? factor);
            if (v is { } y) _pendingContentY = y / (zoom ?? factor);
        }
        _scroller.ChangeView(h, v, zoom, disableAnimation);
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        float zoom = _scroller.ZoomFactor;
        // The user zoomed with a gesture or Ctrl+wheel. Both animate, so check every step: by the
        // final view, _lastZoom has caught up.
        if (_pendingZoom is null && Math.Abs(zoom - _lastZoom) > 0.0001) _zoomMode = ZoomMode.Custom;
        if (!e.IsIntermediate)
        {
            _pendingContentX = _pendingContentY = null;
            _pendingZoom = null;
        }
        if (Math.Abs(zoom - _lastZoom) > 0.0001)
        {
            // All three track scroll offsets in pixels of the old zoom. A selection drag goes on; its next
            // move starts auto-scrolling again from the new offsets.
            StopMiddleScroll();
            StopAutoScroll();
            StopKeyScroll();
            _lastZoom = zoom;
            PlaceOverlays();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
        UpdateRealization(!e.IsIntermediate);
        UpdateCurrentPage();
    }

    private void RefreshRendering() => UpdateRealization(true);

    /// <summary>
    /// The part of the content in view, or that will be once a pending scroll or zoom lands, so
    /// pages aren't rendered for a view that's about to be replaced (such as the first one on open).
    /// </summary>
    private Rect VisibleContentRect()
    {
        double factor = Math.Max(0.01, _scroller.ZoomFactor);
        double z = Math.Max(0.01, Zoom);
        double vw = (_scroller.ViewportWidth > 0 ? _scroller.ViewportWidth : ActualWidth) / z;
        double vh = (_scroller.ViewportHeight > 0 ? _scroller.ViewportHeight : ActualHeight) / z;
        // When the content is narrower than the viewport it's centred, offsets are 0 then.
        double x = Math.Clamp(_pendingContentX ?? _scroller.HorizontalOffset / factor, 0, Math.Max(0, _host.Width - vw));
        double y = Math.Clamp(_pendingContentY ?? _scroller.VerticalOffset / factor, 0, Math.Max(0, _host.Height - vh));
        return new Rect(x, y, vw, vh);
    }

    private IEnumerable<int> PagesIn(Rect area)
    {
        if (_document is null) yield break;
        // Rows are sorted by Top; binary search the first candidate.
        int lo = 0, hi = _rows.Count - 1, first = _rows.Count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var row = _rows[mid];
            if (double.IsNaN(row.Bottom) || row.Bottom >= area.Top) { first = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        if (!_continuous || _presentation) first = 0;
        for (int r = first; r < _rows.Count; r++)
        {
            var row = _rows[r];
            if (double.IsNaN(row.Top)) continue;
            if (row.Top > area.Bottom) break;
            foreach (var p in row.Pages)
            {
                var rect = _pageRects[p];
                if (!rect.IsEmpty && rect.Right >= area.Left && rect.Left <= area.Right) yield return p;
            }
        }
    }

    private void UpdateRealization(bool allowRescale)
    {
        if (_document is null || XamlRoot is null) return;
        var visible = VisibleContentRect();
        // A view that jumped past the last one (dragging the scroll bar of a long document moves a
        // screenful or more a frame) will likely jump again before neighbouring pages could be used,
        // so it gets only the pages in view, and the neighbours once it has stayed put for a moment.
        bool jumped = !_lastVisible.IsEmpty && (visible.Top >= _lastVisible.Bottom || visible.Bottom <= _lastVisible.Top);
        _lastVisible = visible;
        if (jumped)
        {
            _prefetchTimer.Stop();
            _prefetchTimer.Start();
        }
        var prefetch = jumped ? visible : new Rect(visible.X, visible.Y - visible.Height * 0.75, visible.Width, visible.Height * 2.5);
        var keep = new Rect(visible.X - visible.Width, visible.Y - visible.Height * 1.5, visible.Width * 3, visible.Height * 4);
        var wanted = PagesIn(prefetch).Take(80).ToHashSet();
        var keepSet = PagesIn(keep).ToHashSet();

        foreach (var index in _views.Keys.ToList())
        {
            if (!keepSet.Contains(index)) RemoveView(index);
        }

        double pixelScale = Zoom * XamlRoot.RasterizationScale;
        foreach (var index in wanted)
        {
            if (!_views.TryGetValue(index, out var view))
            {
                view = new PageView(_document, index, framed: !_presentation);
                if (_inverted) view.SetPaperColor(Color.FromArgb(255, 30, 30, 30));
                view.Arrange(_pageRects[index], BaseScale, _rotation);
                _views[index] = view;
                _host.Children.Add(view);
                // Text comes after the bitmap: while the scroll bar is dragged, most pages are gone
                // before PDFium gets to them, and their text would only hold up the pages that stay.
                int page = index;
                view.Rendered += (_, _) => EnsureText(page);
                UpdateOverlay(index);
            }
            var rect = _pageRects[index];
            var inter = RectHelper.Intersect(rect, visible);
            bool isVisible = !inter.IsEmpty && inter.Width > 0 && inter.Height > 0;
            var local = isVisible ? new Rect(inter.X - rect.X, inter.Y - rect.Y, inter.Width, inter.Height) : Rect.Empty;
            if (allowRescale || !HasBitmap(view)) view.Render(pixelScale, local, _inverted, isVisible);
            // A page drawn while off screen queued its text behind the pages around it; now it's in view, that can't wait.
            if (isVisible && !_texts.ContainsKey(index) && HasBitmap(view)) EnsureText(index);
        }
        if (allowRescale) SnapPages();
    }

    private void SnapPages()
    {
        if (XamlRoot is null || _views.Count == 0) return;
        double raster = XamlRoot.RasterizationScale;
        double pixelScale = _scroller.ZoomFactor * raster;
        try
        {
            // TransformToVisual on the content ignores the scroller's zoom and offsets, so place the
            // pages by hand. The compositor rounds both the centring of content smaller than the
            // viewport and the scroll offsets to whole pixels.
            var origin = _scroller.TransformToVisual(null).TransformPoint(new Point(0, 0));
            // The centring comes from the size Relayout set, not ActualHeight: turning to a page of
            // another height snaps before the next layout pass, and often nothing snaps after it.
            double z = _scroller.ZoomFactor;
            var gap = CenteringGap(z);
            double ox = Math.Floor(gap.X * raster + 0.5) - Math.Round(_scroller.HorizontalOffset * raster);
            double oy = Math.Floor(gap.Y * raster + 0.5) - Math.Round(_scroller.VerticalOffset * raster);
            foreach (var (index, view) in _views)
            {
                var r = _pageRects[index];
                double px = (origin.X + r.X * z) * raster + ox;
                double py = (origin.Y + r.Y * z) * raster + oy;
                view.SnapToPixels(new Point(px, py), pixelScale);
            }
        }
        catch (ArgumentException)
        {
        }
    }

    private static bool HasBitmap(PageView view) => view.Children.OfType<Image>().Any(i => i.Source is not null);

    private void RemoveView(int index)
    {
        if (!_views.Remove(index, out var view)) return;
        if (_stroke?.Page == index || _move?.Annotation.PageIndex == index) AbandonGestures();
        view.Release();
        if (_textLoading.Remove(index, out var loading)) loading.Cts.Cancel();
        _host.Children.Remove(view);
    }

    private void UpdateCurrentPage()
    {
        if (_document is null) return;
        var visible = VisibleContentRect();
        int best = -1;
        double bestArea = 0;
        foreach (var p in PagesIn(visible))
        {
            var inter = RectHelper.Intersect(_pageRects[p], visible);
            if (inter.IsEmpty) continue;
            // Prefer the page that fills most of the viewport height; ties go to the earlier page.
            double score = inter.Height * Math.Min(1, inter.Width / Math.Max(1, _pageRects[p].Width));
            if (score > bestArea + 1) { bestArea = score; best = p; }
        }
        if (best >= 0) SetCurrentPage(best);
    }

    private void SetCurrentPage(int index)
    {
        if (_currentPage == index) return;
        _currentPage = index;
        UpdateAutomationName();
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Gives screen readers at least the position in the document. Page text isn't exposed yet.</summary>
    private void UpdateAutomationName()
    {
        AutomationProperties.SetName(this, _document is null
            ? "Document"
            : $"Document, page {_document.GetPageLabel(_currentPage)}, {_currentPage + 1} of {_document.PageCount}");
    }

    // ---------------------------------------------------------------- text data

    /// <summary>
    /// Loads a page's text. A page in view goes ahead of the bitmaps of the pages around it, as
    /// presses on it (selecting, following links) need its text.
    /// </summary>
    private async void EnsureText(int index)
    {
        if (_document is null || _texts.ContainsKey(index)) return;
        var priority = IsInView(index) ? WorkPriority.Visible : WorkPriority.Normal;
        if (_textLoading.TryGetValue(index, out var loading))
        {
            if (loading.Priority <= priority) return;
            loading.Cts.Cancel();
        }
        var document = _document;
        var cts = new CancellationTokenSource();
        _textLoading[index] = (cts, priority);
        try
        {
            var text = await document.GetPageTextAsync(index, priority, cts.Token);
            if (document != _document) return;
            _texts[index] = text;
            if (_texts.Count > 64)
            {
                foreach (var key in _texts.Keys.Where(k => !_views.ContainsKey(k)).Take(_texts.Count - 48).ToList()) _texts.Remove(key);
            }
            UpdateOverlay(index);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
        }
        finally
        {
            if (_textLoading.TryGetValue(index, out var current) && current.Cts == cts) _textLoading.Remove(index);
        }
    }

    private bool IsInView(int index)
    {
        if (index >= _pageRects.Length || _pageRects[index].IsEmpty) return false;
        var inter = RectHelper.Intersect(_pageRects[index], VisibleContentRect());
        return !inter.IsEmpty && inter.Width > 0 && inter.Height > 0;
    }

    private async Task<PageText?> GetTextAsync(int index)
    {
        if (_document is null) return null;
        if (_texts.TryGetValue(index, out var text)) return text;
        var document = _document;
        try
        {
            text = await document.GetPageTextAsync(index, WorkPriority.Visible);
            if (document != _document) return null;
            _texts[index] = text;
            return text;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- overlays

    [Flags]
    private enum Overlays
    {
        Search = 1,
        CurrentHit = 2,
        Selection = 4,
        All = Search | CurrentHit | Selection,
    }

    private void UpdateAllOverlays(Overlays layers = Overlays.All)
    {
        foreach (var index in _views.Keys) UpdateOverlay(index, layers);
    }

    /// <summary>
    /// Redraws a page's highlights in <paramref name="layers"/>. Each layer is its own, so moving the current hit
    /// or the selection doesn't redraw the page's other hits, which can be hundreds.
    /// </summary>
    private void UpdateOverlay(int index, Overlays layers = Overlays.All)
    {
        if (!_views.TryGetValue(index, out var view)) return;
        _texts.TryGetValue(index, out var text);
        if (layers.HasFlag(Overlays.Search))
        {
            var shapes = new List<(Rect, Brush)>();
            if (text is not null && _searchHits.TryGetValue(index, out var hits))
            {
                foreach (var hit in hits)
                    foreach (var r in text.Layout.SourceRangeRects(hit.CharIndex, hit.CharIndex + hit.Length)) shapes.Add((Inflate(r, 1), _searchBrush));
            }
            view.SetOverlay(OverlayLayer.Search, shapes);
        }
        if (layers.HasFlag(Overlays.CurrentHit))
        {
            var shapes = new List<(Rect, Brush)>();
            if (text is not null && _currentHit is { } hit && hit.PageIndex == index)
            {
                foreach (var r in text.Layout.SourceRangeRects(hit.CharIndex, hit.CharIndex + hit.Length)) shapes.Add((Inflate(r, 1), _currentSearchBrush));
            }
            view.SetOverlay(OverlayLayer.CurrentHit, shapes);
        }
        if (layers.HasFlag(Overlays.Selection))
        {
            var shapes = new List<(Rect, Brush)>();
            if (text is not null && GetSelection() is { } sel && index >= sel.Start.Page && index <= sel.End.Page)
            {
                int start = index == sel.Start.Page ? sel.Start.Caret : 0;
                int end = index == sel.End.Page ? sel.End.Caret : text.Count;
                foreach (var r in HitRects(text, start, end)) shapes.Add((r, _selectionBrush));
            }
            view.SetOverlay(OverlayLayer.Selection, shapes);
        }
    }

    private static Rect Inflate(Rect r, double d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    private static Rect Union(IEnumerable<Rect> rects)
    {
        var result = Rect.Empty;
        foreach (var r in rects) result = result.IsEmpty ? r : RectHelper.Union(result, r);
        return result;
    }

    /// <summary>Merges character boxes in [start, end) into one rectangle per line.</summary>
    private static List<Rect> HitRects(PageText text, int start, int end) => text.Layout.RangeRects(start, end);

    // ---------------------------------------------------------------- selection & pointer

    private (TextPosition Start, TextPosition End)? GetSelection()
    {
        if (_selectionAnchor is not { } a || _selectionFocus is not { } f) return null;
        return a.CompareTo(f) <= 0 ? (a, f) : (f, a);
    }

    public void ClearSelection()
    {
        _selectionFlyout?.Hide();
        _selectionFlyout = null;
        bool had = _selectionAnchor is not null;
        var old = GetSelection();
        _selectionAnchor = null;
        _selectionFocus = null;
        if (old is { } s)
        {
            for (int p = s.Start.Page; p <= s.End.Page; p++) UpdateOverlay(p, Overlays.Selection);
        }
        if (had) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll()
    {
        if (_document is null) return;
        _selectionAnchor = new TextPosition(0, 0);
        _selectionFocus = new TextPosition(_document.PageCount - 1, int.MaxValue);
        UpdateAllOverlays(Overlays.Selection);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string> GetSelectedTextAsync()
    {
        if (GetSelection() is not { } sel) return "";
        var sb = new System.Text.StringBuilder();
        for (int p = sel.Start.Page; p <= sel.End.Page; p++)
        {
            var text = await GetTextAsync(p);
            if (text is null) continue;
            int start = p == sel.Start.Page ? sel.Start.Caret : 0;
            int end = p == sel.End.Page ? sel.End.Caret : text.Count;
            sb.Append(text.GetText(start, end));
            if (p != sel.End.Page && sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// The selected text as a query, see <see cref="TextSearch.QueryFor"/>. Search finds text within a page, so this is
    /// the text on the selection's first page, or on the next when the selection starts at the end of a page.
    /// Empty if the document changes meanwhile.
    /// </summary>
    public async Task<string> GetSelectedQueryAsync(SearchOptions options)
    {
        if (GetSelection() is not { } sel) return "";
        var document = _document;
        for (int p = sel.Start.Page; p <= Math.Min(sel.End.Page, sel.Start.Page + 1); p++)
        {
            var text = await GetTextAsync(p);
            if (document != _document) return "";
            if (text is null) continue;
            int start = p == sel.Start.Page ? sel.Start.Caret : 0;
            int end = p == sel.End.Page ? sel.End.Caret : text.Count;
            var query = TextSearch.QueryFor(text.GetSearchText(start, end), options);
            if (query.Length > 0) return query;
        }
        return "";
    }

    public async void CopySelection()
    {
        string text = await GetSelectedTextAsync();
        if (!string.IsNullOrEmpty(text)) CopyText(text);
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        try
        {
            Clipboard.SetContent(package);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another app has the clipboard open (CLIPBRD_E_CANT_OPEN). Nothing is copied, and copying again works once it's done.
        }
    }

    private (int Page, Point Display)? HitPage(Point content)
    {
        foreach (var (index, view) in _views)
        {
            var rect = _pageRects[index];
            if (!rect.IsEmpty && rect.Contains(content))
                return (index, view.FromView(new Point(content.X - rect.X, content.Y - rect.Y)));
        }
        return null;
    }

    /// <summary>Finds the page nearest to a point, clamping the point into the page.</summary>
    private (int Page, Point Display)? HitPageNearest(Point content)
    {
        if (HitPage(content) is { } hit) return hit;
        int best = -1;
        double bestDistance = double.MaxValue;
        foreach (var (index, _) in _views)
        {
            var r = _pageRects[index];
            double dx = Math.Max(0, Math.Max(r.Left - content.X, content.X - r.Right));
            double dy = Math.Max(0, Math.Max(r.Top - content.Y, content.Y - r.Bottom));
            double d = dx * dx + dy * dy;
            if (d < bestDistance) { bestDistance = d; best = index; }
        }
        if (best < 0) return null;
        var rect = _pageRects[best];
        var clamped = new Point(Math.Clamp(content.X, rect.Left, rect.Right), Math.Clamp(content.Y, rect.Top, rect.Bottom));
        return (best, _views[best].FromView(new Point(clamped.X - rect.X, clamped.Y - rect.Y)));
    }

    /// <summary>
    /// Where a selection drag at a content point ends. Past the top or bottom of a page that's the
    /// start or end of the page, so dragging into the gap between pages takes the rest of the page.
    /// <c>Display</c> is null then.
    /// </summary>
    private (TextPosition Position, PageText Text, Point? Display)? DragTarget(Point content)
    {
        if (HitPageNearest(content) is not { } hit || !_texts.TryGetValue(hit.Page, out var text)) return null;
        var rect = _pageRects[hit.Page];
        if (content.Y < rect.Top) return (new TextPosition(hit.Page, 0), text, null);
        if (content.Y > rect.Bottom) return (new TextPosition(hit.Page, text.Count), text, null);
        return (new TextPosition(hit.Page, text.Layout.CaretAt(hit.Display)), text, hit.Display);
    }

    /// <summary>
    /// Whether a press at <paramref name="display"/> can start a selection: one on text always can
    /// (wherever the I-beam shows, even on a line cut off by the edge of the view); one in empty
    /// space only when the caret it places is in view with at least half its line showing, so it
    /// doesn't start selecting text off screen.
    /// </summary>
    private bool CanStartSelection(int page, PageText text, Point display)
    {
        if (text.Layout.CharAt(display, 1) >= 0) return true;
        if (text.Layout.CaretPoint(display) is not { } caret) return false;
        var rect = _pageRects[page];
        var view = _views[page].ToView(caret);
        return VisibleContentRect().Contains(new Point(rect.X + view.X, rect.Y + view.Y));
    }

    /// <summary>The link a click follows. A drawing or text box over one takes the click, to be selected.</summary>
    private PdfLink? ClickableLinkAt(int page, Point display) =>
        AnnotationAt(page, display) is { Ink: not null } or { FreeText: not null } ? null : LinkAt(page, display);

    private PdfLink? LinkAt(int page, Point display)
    {
        if (!_texts.TryGetValue(page, out var text)) return null;
        foreach (var link in text.Links)
        {
            if (link.Bounds.Contains(display)) return link;
        }
        return null;
    }

    private PdfAnnotation? AnnotationAt(int page, Point display)
    {
        if (!_texts.TryGetValue(page, out var text)) return null;
        // Topmost first.
        for (int i = text.Annotations.Length - 1; i >= 0; i--)
        {
            var a = text.Annotations[i];
            // A drawing is only where its lines are: the rest of its bounds are for the text under it.
            if (a.Ink is { } ink && ink.StrokesAt(display, 4 / Zoom).Any()) return a;
            if (a.Kind is AnnotationKind.Note or AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut or AnnotationKind.Squiggly or AnnotationKind.FreeText
                && a.Bounds.Contains(display)) return a;
        }
        return null;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        bool typing = _textEdit is not null;
        Focus(FocusState.Pointer);
        // OnMiddleScrollPointerPressed ends middle-click scrolling instead.
        if (_middleScroll) return;
        var point = e.GetCurrentPoint(_host);
        if (point.PointerDeviceType == PointerDeviceType.Touch) return;
        if (_presentation)
        {
            if (point.Properties.IsLeftButtonPressed) NextPage();
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsEraser) return;
        if (_gesturePointer is { } gesture)
        {
            if (gesture != e.Pointer.PointerId)
            {
                e.Handled = true;
                return;
            }
            // The same pointer can only press again if its release was never seen.
            FinishToolGesture();
        }
        // Pressing away from a text box being typed only closes it.
        if (typing)
        {
            _ = CommitTextEditAsync();
            e.Handled = true;
            return;
        }
        _selectUnit = 0;
        int clicks = CountClick(point.Position);
        if (StartToolGesture(point, e.Pointer))
        {
            e.Handled = true;
            return;
        }
        if (HitPage(point.Position) is { } onDrawing)
        {
            if (clicks >= 2 && AnnotationAt(onDrawing.Page, onDrawing.Display) is { FreeText: not null } box)
            {
                BeginTextEdit(box);
                e.Handled = true;
                return;
            }
            if (StartMove(onDrawing.Page, onDrawing.Display, point.Position, e.Pointer))
            {
                e.Handled = true;
                return;
            }
        }
        Deselect();
        if (clicks >= 2 && (e.KeyModifiers & VirtualKeyModifiers.Shift) == 0 && HitPage(point.Position) is { } unit
            && LinkAt(unit.Page, unit.Display) is null && SelectUnit(unit.Page, unit.Display, clicks))
        {
            // Double click selects a word, triple click a line; dragging then extends by that unit.
            _pressPoint = point.Position;
            _pointerDown = true;
            _dragging = false;
            _pressedLink = null;
            _host.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
        _pressPoint = point.Position;
        _pointerDown = true;
        _dragging = false;
        _pressedLink = null;
        _linkDragAnchor = null;
        // A press starts a new selection (or extends the current one with Shift), so a drag never
        // carries on from an old selection; one in empty space whose nearest text is out of view
        // only clears it. On a link that waits until the press turns into a drag, which selects the
        // text under it; a click follows the link and leaves the selection alone.
        bool extend = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0 && _selectionAnchor is not null;
        var hit = HitPage(point.Position);
        if (hit is { } h) _pressedLink = ClickableLinkAt(h.Page, h.Display);
        var target = hit is null ? null : DragTarget(point.Position);
        if (extend)
        {
            if (target is { } t && _pressedLink is null) SetSelectionFocus(t.Position);
        }
        else if (_pressedLink is not null)
        {
            _linkDragAnchor = target?.Position;
        }
        else
        {
            ClearSelection();
            if (target is { } t && t.Display is { } d && CanStartSelection(t.Position.Page, t.Text, d)) _selectionAnchor = t.Position;
        }
        _host.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_middleScroll) return;
        var point = e.GetCurrentPoint(_host);
        if (point.PointerDeviceType == PointerDeviceType.Touch || _presentation) return;
        if (ContinueToolGesture(e, point.Position)) return;
        if (Tool != AnnotationTool.None)
        {
            UpdateToolCursor();
            SetStatus(null);
            return;
        }

        if (_pointerDown)
        {
            var delta = new Point(point.Position.X - _pressPoint.X, point.Position.Y - _pressPoint.Y);
            if (!_dragging && Math.Abs(delta.X) + Math.Abs(delta.Y) > 4 / Zoom)
            {
                _dragging = true;
                if (_linkDragAnchor is { } anchor)
                {
                    ClearSelection();
                    _selectionAnchor = anchor;
                    _linkDragAnchor = null;
                }
                // The I-beam's hot spot is in its middle, which makes it clear which line is aimed at.
                if (_selectionAnchor is not null) SetCursor(InputSystemCursorShape.IBeam);
            }
            if (_dragging && _selectionAnchor is not null)
            {
                _dragContentPoint = point.Position;
                _dragViewportPoint = e.GetCurrentPoint(_scroller).Position;
                _dragOffsetX = _scroller.HorizontalOffset;
                _dragOffsetY = _scroller.VerticalOffset;
                UpdateDragSelection(point.Position);
                UpdateAutoScroll();
            }
            return;
        }

        var cursor = InputSystemCursorShape.Arrow;
        string? status = null;
        if (HitPage(point.Position) is { } h)
        {
            if (ClickableLinkAt(h.Page, h.Display) is { } link)
            {
                cursor = InputSystemCursorShape.Hand;
                status = link.Destination.Uri is { } uri
                    ? LaunchPolicy.ParseExternalLink(uri) is { } parsed ? LaunchPolicy.DescribeLink(parsed) : "This link can't be opened"
                    : $"Go to page {_document!.GetPageLabel(link.Destination.PageIndex)}";
            }
            else if (_selected is { } selected && selected.PageIndex == h.Page && Inflate(selected.Bounds, SelectionFrame.Gap / Zoom).Contains(h.Display))
            {
                cursor = selected.FreeText is not null && IsOnGrip(h.Page, selected.Bounds, point.Position)
                    ? InputSystemCursorShape.SizeNorthwestSoutheast : InputSystemCursorShape.SizeAll;
            }
            else if (AnnotationAt(h.Page, h.Display) is { } annotation)
            {
                cursor = InputSystemCursorShape.Hand;
                status = string.IsNullOrWhiteSpace(annotation.Contents) ? null : annotation.Contents.ReplaceLineEndings(" ");
            }
            else if (_texts.TryGetValue(h.Page, out var text) && text.Layout.CharAt(h.Display, 1) >= 0)
            {
                cursor = InputSystemCursorShape.IBeam;
            }
        }
        SetCursor(cursor);
        SetStatus(status);
    }

    private void UpdateDragSelection(Point content)
    {
        if (DragTarget(content) is not { } target) return;
        if (_selectUnit > 0) ExtendUnitSelection(target.Position, target.Text, target.Display);
        else SetSelectionFocus(target.Position);
    }

    // While a selection drag is past the edge of the view, the view keeps scrolling that way, faster
    // the further out the pointer is, and the selection follows the content passing under it.
    private const double AutoScrollMargin = 16;
    private Point _dragContentPoint, _dragViewportPoint;
    private double _dragOffsetX, _dragOffsetY;
    private double _autoScrollX, _autoScrollY;
    private long _autoScrollTime;
    private bool _autoScrolling;

    /// <summary>How far (DIP) past the edge of the view, with a small margin inside it, the pointer is on each axis.</summary>
    private (double X, double Y) AutoScrollExcess()
    {
        static double Excess(double v, double size) =>
            v < AutoScrollMargin ? v - AutoScrollMargin : v > size - AutoScrollMargin ? v - (size - AutoScrollMargin) : 0;
        var p = _dragViewportPoint;
        return (Excess(p.X, _scroller.ViewportWidth), Excess(p.Y, _scroller.ViewportHeight));
    }

    private void UpdateAutoScroll()
    {
        var (x, y) = AutoScrollExcess();
        bool wanted = _dragging && (x != 0 || y != 0);
        if (wanted == _autoScrolling) return;
        _autoScrolling = wanted;
        if (wanted)
        {
            _autoScrollX = _scroller.HorizontalOffset;
            _autoScrollY = _scroller.VerticalOffset;
            _autoScrollTime = System.Diagnostics.Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnAutoScrollFrame;
        }
        else
        {
            CompositionTarget.Rendering -= OnAutoScrollFrame;
        }
    }

    private void StopAutoScroll()
    {
        if (!_autoScrolling) return;
        _autoScrolling = false;
        CompositionTarget.Rendering -= OnAutoScrollFrame;
    }

    private void OnAutoScrollFrame(object? sender, object e)
    {
        if (!_dragging || _selectionAnchor is null)
        {
            StopAutoScroll();
            return;
        }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = Math.Min(0.1, (now - _autoScrollTime) / (double)System.Diagnostics.Stopwatch.Frequency);
        _autoScrollTime = now;
        var (ex, ey) = AutoScrollExcess();
        static double Speed(double excess) => Math.Sign(excess) * Math.Min(3000, 60 + Math.Pow(Math.Abs(excess), 1.3) * 5);
        double x = ex == 0 ? _autoScrollX : Math.Clamp(_autoScrollX + Speed(ex) * seconds, 0, _scroller.ScrollableWidth);
        double y = ey == 0 ? _autoScrollY : Math.Clamp(_autoScrollY + Speed(ey) * seconds, 0, _scroller.ScrollableHeight);
        if (x == _autoScrollX && y == _autoScrollY) return;
        // Offsets are tracked here, since the scroller's lag behind ChangeView.
        _autoScrollX = x;
        _autoScrollY = y;
        ScrollTo(x, y, null, true);
        double z = _scroller.ZoomFactor;
        UpdateDragSelection(new Point(_dragContentPoint.X + (x - _dragOffsetX) / z, _dragContentPoint.Y + (y - _dragOffsetY) / z));
    }

    // ---------------------------------------------------------------- middle-click scrolling

    private void OnMiddleScrollPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_root);
        _swallowRightTap = false;
        if (_middleScroll)
        {
            // Any click ends middle-click scrolling and does nothing else.
            _swallowRightTap = point.Properties.IsRightButtonPressed;
            StopMiddleScroll();
            e.Handled = true;
            return;
        }
        // A press inside a text box being typed is for its text.
        if (_textEdit is { } edit && IsInside(e.OriginalSource as DependencyObject, edit.Box)) return;
        // Presses on the pages focus the view already; the margins and scroll bars around them do too, so keys scroll it.
        if (_document is not null) Focus(FocusState.Pointer);
        if (point.PointerDeviceType != PointerDeviceType.Mouse || !point.Properties.IsMiddleButtonPressed || _document is null || _presentation) return;
        if (!StartMiddleScroll(point.Position)) return;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMiddleScrollPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_middleScroll) return;
        var point = e.GetCurrentPoint(_root);
        _middleScrollPointer = point.Position;
        double distance = Math.Max(Math.Abs(point.Position.X - _middleScrollOrigin.X), Math.Abs(point.Position.Y - _middleScrollOrigin.Y));
        if (point.Properties.IsMiddleButtonPressed && distance > MiddleScrollDeadZone) _middleScrollHeld = true;
    }

    private void OnMiddleScrollPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // A plain click leaves scrolling on; releasing after scrolling with the button held ends it.
        if (_middleScroll && _middleScrollHeld && e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased)
            StopMiddleScroll();
    }

    private bool StartMiddleScroll(Point origin)
    {
        bool vertical = _scroller.ScrollableHeight > 0, horizontal = _scroller.ScrollableWidth > 0;
        if (!vertical && !horizontal) return false;
        _middleScroll = true;
        _middleScrollHeld = false;
        _middleScrollOrigin = _middleScrollPointer = origin;
        _middleScrollX = _pendingContentX is { } x ? x * Zoom : _scroller.HorizontalOffset;
        _middleScrollY = _pendingContentY is { } y ? y * Zoom : _scroller.VerticalOffset;
        _middleScrollTime = System.Diagnostics.Stopwatch.GetTimestamp();
        DrawMiddleScrollMarker(vertical, horizontal);
        _middleScrollMarker.Margin = new Thickness(origin.X - MiddleScrollMarkerSize / 2, origin.Y - MiddleScrollMarkerSize / 2, 0, 0);
        _middleScrollMarker.Visibility = Visibility.Visible;
        SetStatus(null);
        SetCursor(vertical && horizontal ? InputSystemCursorShape.SizeAll : vertical ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
        CompositionTarget.Rendering += OnMiddleScrollFrame;
        return true;
    }

    /// <summary>Ends middle-click scrolling, if it's on.</summary>
    public void StopMiddleScroll()
    {
        if (!_middleScroll) return;
        _middleScroll = false;
        _middleScrollHeld = false;
        CompositionTarget.Rendering -= OnMiddleScrollFrame;
        _middleScrollMarker.Visibility = Visibility.Collapsed;
        ReleasePointerCaptures();
        SetCursor(InputSystemCursorShape.Arrow);
    }

    private void OnMiddleScrollFrame(object? sender, object e)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = Math.Min(0.1, (now - _middleScrollTime) / (double)System.Diagnostics.Stopwatch.Frequency);
        _middleScrollTime = now;
        double x = Math.Clamp(_middleScrollX + MiddleScrollSpeed(_middleScrollPointer.X - _middleScrollOrigin.X) * seconds, 0, _scroller.ScrollableWidth);
        double y = Math.Clamp(_middleScrollY + MiddleScrollSpeed(_middleScrollPointer.Y - _middleScrollOrigin.Y) * seconds, 0, _scroller.ScrollableHeight);
        if (x == _middleScrollX && y == _middleScrollY) return;
        // Offsets are tracked here, since the scroller's lag behind ChangeView.
        _middleScrollX = x;
        _middleScrollY = y;
        ScrollTo(x, y, null, true);
    }

    /// <summary>Scroll speed (DIP per second) for a pointer <paramref name="distance"/> DIP from the origin.</summary>
    private static double MiddleScrollSpeed(double distance)
    {
        double excess = Math.Abs(distance) - MiddleScrollDeadZone;
        return excess <= 0 ? 0 : Math.Sign(distance) * Math.Pow(excess, 1.5) * 1.2;
    }

    /// <summary>Draws the origin marker, with arrows for the directions that can scroll.</summary>
    private void DrawMiddleScrollMarker(bool vertical, bool horizontal)
    {
        var resources = Application.Current.Resources;
        var glyphStyle = (Style)resources["MiddleScrollGlyphStyle"];
        const double s = MiddleScrollMarkerSize, c = s / 2;
        _middleScrollMarker.Children.Clear();
        _middleScrollMarker.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = s,
            Height = s,
            Style = (Style)resources["MiddleScrollMarkerStyle"],
        });
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 4, Height = 4, Style = glyphStyle };
        Canvas.SetLeft(dot, c - 2);
        Canvas.SetTop(dot, c - 2);
        _middleScrollMarker.Children.Add(dot);

        // (dx, dy) is the unit direction the arrow points in.
        void Arrow(double tipX, double tipY, double dx, double dy)
        {
            var arrow = new Microsoft.UI.Xaml.Shapes.Polygon { Style = glyphStyle };
            arrow.Points.Add(new Point(tipX, tipY));
            arrow.Points.Add(new Point(tipX - dx * 4 - dy * 4, tipY - dy * 4 - dx * 4));
            arrow.Points.Add(new Point(tipX - dx * 4 + dy * 4, tipY - dy * 4 + dx * 4));
            _middleScrollMarker.Children.Add(arrow);
        }
        if (vertical)
        {
            Arrow(c, 5, 0, -1);
            Arrow(c, s - 5, 0, 1);
        }
        if (horizontal)
        {
            Arrow(5, c, -1, 0);
            Arrow(s - 5, c, 1, 0);
        }
    }

    private static bool IsInside(DependencyObject? element, DependencyObject ancestor)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element == ancestor) return true;
        }
        return false;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_gesturePointer is { } gesture && gesture != e.Pointer.PointerId) return;
        if (FinishToolGesture())
        {
            _host.ReleasePointerCaptures();
            e.Handled = true;
            return;
        }
        if (!_pointerDown) return;
        // Releasing capture raises PointerCaptureLost synchronously, which resets the drag state.
        bool dragging = _dragging;
        _pointerDown = false;
        _dragging = false;
        StopAutoScroll();
        _host.ReleasePointerCaptures();
        if (_selectUnit > 0)
        {
            // Keep the word/line selection made by the double/triple click.
            _selectUnit = 0;
            e.Handled = true;
            return;
        }
        var point = e.GetCurrentPoint(_host);
        _linkDragAnchor = null;
        if (!dragging)
        {
            if (_pressedLink is { } link)
            {
                // A click on a link leaves the selection as it was; one released off the link is cancelled.
                if (HitPage(point.Position) is { } h && LinkAt(h.Page, h.Display) == link) GoToDestination(link.Destination);
            }
            else if (HitPage(point.Position) is { } hit && AnnotationAt(hit.Page, hit.Display) is { } annotation && (e.KeyModifiers & VirtualKeyModifiers.Shift) == 0)
            {
                ClearSelection();
                if (annotation.Ink is not null || annotation.FreeText is not null) Select(annotation);
                else ShowAnnotationFlyout(annotation, point.Position);
            }
            else if ((e.KeyModifiers & VirtualKeyModifiers.Shift) == 0)
            {
                ClearSelection();
            }
        }
        _dragging = false;
        e.Handled = true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private static void ShowArrowWhenOpened(FlyoutBase flyout) => flyout.Opened += (_, _) => ArrowCursor.Show();

    private long _lastClickTime;
    private Point _lastClickPoint;
    private int _clickCount;

    /// <summary>Counts consecutive clicks at roughly the same spot (1 = single, 2 = double, 3 = triple).</summary>
    private int CountClick(Point position)
    {
        long now = Environment.TickCount64;
        double distance = Math.Abs(position.X - _lastClickPoint.X) + Math.Abs(position.Y - _lastClickPoint.Y);
        bool continues = now - _lastClickTime <= GetDoubleClickTime() && distance * Zoom <= 6;
        _clickCount = continues ? Math.Min(_clickCount + 1, 3) : 1;
        _lastClickTime = now;
        _lastClickPoint = position;
        return _clickCount;
    }

    // After a double/triple click, dragging extends the selection by whole words/lines
    // around the unit that was clicked first.
    private int _selectUnit;
    private TextPosition _unitStart;
    private TextPosition _unitEnd;

    /// <summary>Bounds [start, end) of the word (unit 2) or line (unit 3) containing character <paramref name="i"/>.</summary>
    private static (int Start, int End) UnitBounds(PageText text, int i, int unit)
    {
        i = Math.Clamp(i, 0, Math.Max(0, text.Count - 1));
        int start = i, end = Math.Min(i + 1, text.Count);
        if (unit >= 3)
        {
            int line = text.Layout.LineOf(i);
            if (line >= 0) (start, end) = (text.Layout.Lines[line].Start, text.Layout.Lines[line].End);
        }
        else if (text.IsWordChar(i))
        {
            while (start > 0 && text.IsWordChar(start - 1)) start--;
            while (end < text.Count && text.IsWordChar(end)) end++;
        }
        return (start, end);
    }

    /// <summary>Selects the word (clicks == 2) or line (clicks == 3) under a point.</summary>
    private bool SelectUnit(int page, Point display, int clicks)
    {
        if (!_texts.TryGetValue(page, out var text)) return false;
        int i = text.Layout.CharAt(display, 1);
        if (i < 0) return false;
        var (start, end) = UnitBounds(text, i, clicks);
        ClearSelection();
        _selectUnit = clicks;
        _unitStart = new TextPosition(page, start);
        _unitEnd = new TextPosition(page, end);
        _selectionAnchor = _unitStart;
        SetSelectionFocus(_unitEnd);
        return true;
    }

    /// <summary>
    /// Extends a unit-wise selection to the word/line at a caret position. <paramref name="display"/>
    /// is the point it was found at, or null past the end of the page, where no unit is extended to.
    /// </summary>
    private void ExtendUnitSelection(TextPosition position, PageText text, Point? display)
    {
        bool forward = position.CompareTo(_unitStart) > 0;
        int start = position.Caret, end = position.Caret;
        if (display is { } p && text.Layout.Lines.Count > 0)
        {
            // A line is taken whole wherever on it the pointer is; a word when the caret is inside
            // it, not when it's just past the end of the one before.
            if (_selectUnit >= 3)
            {
                var line = text.Layout.Lines[text.Layout.LineAt(p)];
                (start, end) = (line.Start, line.End);
            }
            else
            {
                (start, end) = UnitBounds(text, forward ? position.Caret - 1 : position.Caret, _selectUnit);
            }
        }
        if (forward)
        {
            _selectionAnchor = _unitStart;
            var focus = position with { Caret = end };
            SetSelectionFocus(focus.CompareTo(_unitEnd) < 0 ? _unitEnd : focus);
        }
        else
        {
            _selectionAnchor = _unitEnd;
            SetSelectionFocus(position with { Caret = start });
        }
    }

    private void SetSelectionFocus(TextPosition focus)
    {
        var old = GetSelection();
        _selectionFocus = focus;
        var now = GetSelection();
        int from = Math.Min(old?.Start.Page ?? focus.Page, now?.Start.Page ?? focus.Page);
        int to = Math.Max(old?.End.Page ?? focus.Page, now?.End.Page ?? focus.Page);
        for (int p = from; p <= to; p++) UpdateOverlay(p, Overlays.Selection);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetCursor(InputSystemCursorShape shape)
    {
        if (_cursor == shape) return;
        _cursor = shape;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    private void SetStatus(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _statusPill.Visibility = Visibility.Collapsed;
            return;
        }
        _statusText.Text = text.Length > 300 ? text[..300] + "…" : text;
        _statusPill.Visibility = Visibility.Visible;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        StopMiddleScroll();
        // Otherwise their next frame would scroll back to the offset they track.
        StopAutoScroll();
        StopKeyScroll();
        if (_document is null || (_continuous && !_presentation)) return;
        if ((e.KeyModifiers & VirtualKeyModifiers.Control) != 0) return;
        // Single page mode: flip pages when scrolling past the edges.
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        bool atBottom = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 1;
        bool atTop = _scroller.VerticalOffset <= 1;
        if (delta < 0 && atBottom) NextPage();
        else if (delta > 0 && atTop && _currentPage > 0)
        {
            PreviousPage();
            ScrollTo(null, BottomOffset(), null, true);
        }
    }

    // Keys scroll by the same steps as Edge: a line is 40 DIP, a screen is 7/8 of the view so a
    // little of the last one stays in sight.
    private const double LineStep = 40;
    private const double ScreenStep = 0.875;

    private static bool IsKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Where the view is headed: a scroll that's still animating counts as done, so repeated keys add up.</summary>
    private (double X, double Y) ScrollTarget()
    {
        double z = Zoom;
        return (_pendingContentX is { } x ? Math.Clamp(x * z, 0, _scroller.ScrollableWidth) : _scroller.HorizontalOffset,
            _pendingContentY is { } y ? Math.Clamp(y * z, 0, _scroller.ScrollableHeight) : _scroller.VerticalOffset);
    }

    private void ScrollBy(double dx, double dy)
    {
        // Both axes, so a scroll still under way on the other one keeps its target instead of
        // stopping where it has got to.
        var (x, y) = ScrollTarget();
        ScrollTo(Math.Clamp(x + dx, 0, _scroller.ScrollableWidth), Math.Clamp(y + dy, 0, _scroller.ScrollableHeight), null, false);
    }

    /// <summary>The offset that shows the bottom of the content, at the zoom it's headed to.</summary>
    /// <remarks>Turning a page can change the fit zoom, which the scroller's extent doesn't reflect yet.</remarks>
    private double BottomOffset() => Math.Max(0, _host.Height * Zoom - _scroller.ViewportHeight);

    /// <summary>Scrolls a line; in single page mode, scrolling past the edge of the page turns it, as the wheel does.</summary>
    private void ScrollLine(int direction)
    {
        if (_continuous)
        {
            ScrollBy(0, direction * LineStep);
            return;
        }
        double y = ScrollTarget().Y;
        if (direction > 0 && y >= _scroller.ScrollableHeight - 1) NextPage();
        else if (direction < 0 && y <= 1 && _currentPage > 0)
        {
            PreviousPage();
            ScrollTo(null, BottomOffset(), null, true);
        }
        else ScrollBy(0, direction * LineStep);
    }

    // Holding an arrow key scrolls at a steady speed, like Firefox, rather than a line per key
    // repeat. The first press still scrolls a line.
    private const double KeyScrollSpeed = 1800;
    private const double KeyScrollRampSeconds = 0.15;
    private VirtualKey? _keyScrollKey;
    private int _keyScrollDX, _keyScrollDY;
    private double _keyScrollX, _keyScrollY;
    private long _keyScrollStart, _keyScrollTime;

    /// <summary>Whether the view can scroll further in a direction (-1, 0 or 1 on each axis).</summary>
    private bool CanScroll(int dx, int dy)
    {
        var (x, y) = ScrollTarget();
        return dx < 0 && x > 1 || dx > 0 && x < _scroller.ScrollableWidth - 1 || dy < 0 && y > 1 || dy > 0 && y < _scroller.ScrollableHeight - 1;
    }

    /// <summary>Scrolls for an arrow key; once the key repeats, scrolls continuously until it's released.</summary>
    private void OnScrollKey(KeyRoutedEventArgs e, int dx, int dy)
    {
        // At an edge a repeat steps instead, which turns the page in single page mode.
        if (!e.KeyStatus.WasKeyDown || !CanScroll(dx, dy))
        {
            StopKeyScroll();
            if (dy != 0) ScrollLine(dy);
            else ScrollBy(dx * LineStep, 0);
            return;
        }
        if (_keyScrollKey == e.Key) return;
        StopKeyScroll();
        _keyScrollKey = e.Key;
        (_keyScrollDX, _keyScrollDY) = (dx, dy);
        // Carry on from wherever the first press's step has got to.
        _keyScrollX = _scroller.HorizontalOffset;
        _keyScrollY = _scroller.VerticalOffset;
        _keyScrollStart = _keyScrollTime = System.Diagnostics.Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnKeyScrollFrame;
    }

    /// <summary>Ends scrolling with a held arrow key, if it's on.</summary>
    public void StopKeyScroll()
    {
        if (_keyScrollKey is null) return;
        _keyScrollKey = null;
        CompositionTarget.Rendering -= OnKeyScrollFrame;
    }

    private void OnKeyScrollFrame(object? sender, object e)
    {
        // The key could have been released while another window had focus.
        if (_keyScrollKey is not { } key || !IsKeyDown(key))
        {
            StopKeyScroll();
            return;
        }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double frequency = System.Diagnostics.Stopwatch.Frequency;
        double seconds = Math.Min(0.1, (now - _keyScrollTime) / frequency);
        _keyScrollTime = now;
        // Ease in, as the first press's step eased out to a stop.
        double speed = KeyScrollSpeed * Math.Min(1, (now - _keyScrollStart) / frequency / KeyScrollRampSeconds);
        double x = Math.Clamp(_keyScrollX + _keyScrollDX * speed * seconds, 0, _scroller.ScrollableWidth);
        double y = Math.Clamp(_keyScrollY + _keyScrollDY * speed * seconds, 0, _scroller.ScrollableHeight);
        if (x != _keyScrollX || y != _keyScrollY)
        {
            // Offsets are tracked here, since the scroller's lag behind ChangeView.
            _keyScrollX = x;
            _keyScrollY = y;
            ScrollTo(_keyScrollDX == 0 ? null : x, _keyScrollDY == 0 ? null : y, null, true);
        }
        bool atEdge = _keyScrollDX < 0 && x <= 0 || _keyScrollDX > 0 && x >= _scroller.ScrollableWidth
            || _keyScrollDY < 0 && y <= 0 || _keyScrollDY > 0 && y >= _scroller.ScrollableHeight;
        if (atEdge) StopKeyScroll();
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == _keyScrollKey) StopKeyScroll();
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Keys can scroll the view, which auto-scrolling would undo on its next frame.
        if (e.Key is not (VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows))
        {
            StopAutoScroll();
            if (e.Key != _keyScrollKey) StopKeyScroll();
        }
        if (_middleScroll)
        {
            StopMiddleScroll();
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                return;
            }
        }
        if (_document is null || e.OriginalSource is TextBox) return;
        bool ctrl = IsKeyDown(VirtualKey.Control);
        bool shift = IsKeyDown(VirtualKey.Shift);
        bool alt = IsKeyDown(VirtualKey.Menu);
        bool paged = !_continuous || _presentation;
        // Like Edge, page keys turn pages when a whole page fits in the view.
        bool pageKeysTurn = paged || _zoomMode == ZoomMode.FitPage;
        bool fitsWidth = _scroller.ScrollableWidth <= 1;
        bool plain = !ctrl && !alt;
        switch (e.Key)
        {
            case VirtualKey.C when ctrl:
                CopySelection();
                e.Handled = true;
                break;
            case VirtualKey.A when ctrl:
                SelectAll();
                e.Handled = true;
                break;
            case VirtualKey.Escape when HasSelection:
                ClearSelection();
                e.Handled = true;
                break;
            case VirtualKey.Escape when _stroke is not null || _erase is not null || _move is not null:
                AbandonGestures();
                e.Handled = true;
                break;
            case VirtualKey.Escape when _selected is not null:
                Deselect();
                e.Handled = true;
                break;
            case VirtualKey.Escape when Tool != AnnotationTool.None:
                SetTool(AnnotationTool.None, _toolColor, _toolSize);
                e.Handled = true;
                break;
            case VirtualKey.Delete when _selected is { } selected:
                DeleteAnnotation(selected);
                e.Handled = true;
                break;
            case VirtualKey.PageDown when plain && pageKeysTurn:
            case VirtualKey.Space when plain && !shift && pageKeysTurn:
            case VirtualKey.Enter when plain && paged:
            case VirtualKey.Right or VirtualKey.Down when _presentation:
            case VirtualKey.Right when plain && fitsWidth:
                NextPage();
                e.Handled = true;
                break;
            case VirtualKey.PageUp when plain && pageKeysTurn:
            case VirtualKey.Space when plain && shift && pageKeysTurn:
            case VirtualKey.Back when plain && paged:
            case VirtualKey.Left or VirtualKey.Up when _presentation:
            case VirtualKey.Left when plain && fitsWidth:
                PreviousPage();
                e.Handled = true;
                break;
            case VirtualKey.PageDown or VirtualKey.PageUp or VirtualKey.Space when plain:
                ScrollBy(0, (e.Key == VirtualKey.PageUp || e.Key == VirtualKey.Space && shift ? -1 : 1) * _scroller.ViewportHeight * ScreenStep);
                e.Handled = true;
                break;
            case VirtualKey.Down or VirtualKey.Up when plain:
                OnScrollKey(e, 0, e.Key == VirtualKey.Down ? 1 : -1);
                e.Handled = true;
                break;
            case VirtualKey.Right or VirtualKey.Left when plain:
                OnScrollKey(e, e.Key == VirtualKey.Right ? 1 : -1, 0);
                e.Handled = true;
                break;
            case VirtualKey.Home when !alt && paged:
                GoToPage(0);
                e.Handled = true;
                break;
            case VirtualKey.End when !alt && paged:
                GoToPage(_document.PageCount - 1);
                e.Handled = true;
                break;
            case VirtualKey.Home or VirtualKey.End when !alt:
                ScrollTo(null, e.Key == VirtualKey.Home ? 0 : BottomOffset(), null, true);
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- flyouts & annotations

    private static readonly (string Name, Color Color)[] HighlightColors =
    [
        ("Yellow", Color.FromArgb(255, 255, 221, 51)),
        ("Green", Color.FromArgb(255, 125, 214, 115)),
        ("Blue", Color.FromArgb(255, 110, 180, 255)),
        ("Pink", Color.FromArgb(255, 255, 128, 190)),
        ("Purple", Color.FromArgb(255, 190, 140, 255)),
    ];

    private CommandBarFlyout? _selectionFlyout;

    /// <summary>Whether a point (display points on <paramref name="page"/>) lies on the selected text.</summary>
    private bool IsInSelection(int page, Point display)
    {
        if (GetSelection() is not { } sel || page < sel.Start.Page || page > sel.End.Page || !_texts.TryGetValue(page, out var text)) return false;
        int start = page == sel.Start.Page ? sel.Start.Caret : 0;
        int end = page == sel.End.Page ? sel.End.Caret : text.Count;
        return HitRects(text, start, end).Any(r => Inflate(r, 2).Contains(display));
    }

    private void ShowSelectionFlyout(Point at)
    {
        _selectionFlyout?.Hide();
        var flyout = _selectionFlyout = new CommandBarFlyout { AlwaysExpanded = true };
        var copy = new AppBarButton { Label = "Copy", Icon = new SymbolIcon(Symbol.Copy) };
        ToolTipService.SetToolTip(copy, "Copy (Ctrl+C)");
        copy.Click += (_, _) => { CopySelection(); flyout.Hide(); };
        var highlight = new AppBarButton { Label = "Highlight", Icon = new FontIcon { Glyph = "\uE7E6" } };
        ToolTipService.SetToolTip(highlight, "Highlight (Ctrl+H)");
        highlight.Click += (_, _) => { HighlightSelection(HighlightColors[0].Color); flyout.Hide(); };
        var note = new AppBarButton { Label = "Add note", Icon = new FontIcon { Glyph = "\uE70B" } };
        ToolTipService.SetToolTip(note, "Highlight and add a note");
        note.Click += (_, _) => { HighlightSelection(HighlightColors[0].Color, openNote: true); flyout.Hide(); };
        flyout.PrimaryCommands.Add(copy);
        flyout.PrimaryCommands.Add(highlight);
        flyout.PrimaryCommands.Add(note);

        foreach (var (name, color) in HighlightColors)
        {
            var item = new AppBarButton
            {
                Label = $"Highlight {name.ToLowerInvariant()}",
                Icon = new FontIcon { Glyph = "\uE91F", Foreground = new SolidColorBrush(color) },
            };
            item.Click += (_, _) => { HighlightSelection(color); flyout.Hide(); };
            flyout.SecondaryCommands.Add(item);
        }
        flyout.SecondaryCommands.Add(new AppBarSeparator());
        var search = new AppBarButton { Label = "Search for selection", Icon = new SymbolIcon(Symbol.Find) };
        search.Click += (_, _) =>
        {
            flyout.Hide();
            SearchRequested?.Invoke(this, EventArgs.Empty);
        };
        flyout.SecondaryCommands.Add(search);
        ShowArrowWhenOpened(flyout);
        // Shown as the context menu for selected text, like Papers.
        flyout.ShowAt(_host, new FlyoutShowOptions
        {
            Position = at,
            ShowMode = FlyoutShowMode.Standard,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
        });
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_swallowRightTap)
        {
            // The right click ended middle-click scrolling.
            _swallowRightTap = false;
            e.Handled = true;
            return;
        }
        if (_document is null) return;
        if (_presentation)
        {
            PreviousPage();
            e.Handled = true;
            return;
        }
        var position = e.GetPosition(_host);
        var hit = HitPage(position);
        if (hit is { } onText && IsInSelection(onText.Page, onText.Display))
        {
            ShowSelectionFlyout(position);
            e.Handled = true;
            return;
        }
        var menu = new MenuFlyout();
        if (hit is { } h)
        {
            if (LinkAt(h.Page, h.Display) is { Destination.Uri: { } uri })
            {
                if (LaunchPolicy.ParseExternalLink(uri) is not null) AddItem(menu, "Open link", "\uE8A7", () => _ = LaunchUriAsync(uri));
                AddItem(menu, "Copy link address", "\uE71B", () => CopyText(uri));
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            if (AnnotationAt(h.Page, h.Display) is { } annotation)
            {
                if (annotation.FreeText is not null) AddItem(menu, "Edit text", "\uE70F", () => BeginTextEdit(annotation));
                else if (!IsForeignText(annotation)) AddItem(menu, "Edit note", "\uE70F", () => ShowAnnotationFlyout(annotation, position));
                AddItem(menu, "Delete annotation", "\uE74D", () => DeleteAnnotation(annotation));
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            AddItem(menu, "Add note here", "\uE70B", () => AddNote(h.Page, h.Display, position));
            AddItem(menu, "Add text here", "\uE8D2", () => BeginTextEdit(h.Page, h.Display));
        }
        AddItem(menu, "Select all", "\uE8B3", SelectAll, VirtualKey.A);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(menu, "Rotate left", "\uE7AD", () => Rotate(-90));
        ((MenuFlyoutItem)menu.Items[^1]).Icon = new FontIcon { Glyph = "\uE7AD", RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform { ScaleX = -1 } };
        AddItem(menu, "Rotate right", "\uE7AD", () => Rotate(90));
        ShowArrowWhenOpened(menu);
        menu.ShowAt(_host, position);
        e.Handled = true;
    }

    private static void AddItem(MenuFlyout menu, string text, string glyph, Action action, VirtualKey? key = null)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        if (key is { } k)
        {
            item.KeyboardAcceleratorTextOverride = $"Ctrl+{k}";
        }
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    public async void HighlightSelection(Color? color = null, bool openNote = false)
    {
        if (_document is null || GetSelection() is not { } sel) return;
        var document = _document;
        var pages = new List<int>();
        var changes = new List<AnnotationChange>();
        try
        {
            for (int p = sel.Start.Page; p <= sel.End.Page; p++)
            {
                var text = await GetTextAsync(p);
                if (document != _document) return;
                if (text is null) continue;
                int start = p == sel.Start.Page ? sel.Start.Caret : 0;
                int end = p == sel.End.Page ? sel.End.Caret : text.Count;
                var rects = HitRects(text, start, end);
                if (rects.Count == 0) continue;
                changes.Add(new PresenceChange(await document.AddHighlightAsync(p, rects, color ?? HighlightColors[0].Color), added: true));
                pages.Add(p);
            }
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            // Pages highlighted before the failure still get refreshed below.
            ReportEditFailure(document, ex, "The text couldn't be highlighted.");
        }
        if (document != _document) return;
        Record([.. changes]);
        ClearSelection();
        foreach (var p in pages)
        {
            InvalidatePage(p);
            AnnotationsChanged?.Invoke(this, p);
        }
        if (!openNote) return;
        // The note goes on the first highlight in view: a selection can start pages earlier (Ctrl+A), on pages that
        // aren't laid out in single-page view.
        var visible = VisibleContentRect();
        int page = pages.FirstOrDefault(p =>
        {
            var area = _pageRects[p];
            area.Intersect(visible);
            return !area.IsEmpty;
        }, -1);
        if (page >= 0)
        {
            var text = await GetTextAsync(page);
            // Closed or replaced meanwhile: the page may not exist any more.
            if (document != _document) return;
            var annotation = text?.Annotations.LastOrDefault(a => a.Kind == AnnotationKind.Highlight);
            var r = _pageRects[page];
            // Visible part of the page: the highlight's corner can be out of view, below the screen for a whole page's.
            var area = r;
            area.Intersect(VisibleContentRect());
            if (annotation is not null && !area.IsEmpty)
            {
                var p = RotatedLayoutPoint(page, new Point(annotation.Bounds.Left, annotation.Bounds.Bottom));
                var at = new Point(Math.Clamp(r.X + p.X, area.Left, area.Right), Math.Clamp(r.Y + p.Y, area.Top, area.Bottom));
                ShowAnnotationFlyout(annotation, at);
            }
        }
    }

    private async void AddNote(int page, Point display, Point hostPosition)
    {
        if (_document is null) return;
        var document = _document;
        PdfAnnotation created;
        try
        {
            created = await document.AddNoteAsync(page, display, HighlightColors[0].Color, "");
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            ReportEditFailure(document, ex, "The note couldn't be added.");
            return;
        }
        if (document != _document) return;
        Record(new PresenceChange(created, added: true));
        InvalidatePage(page);
        AnnotationsChanged?.Invoke(this, page);
        var text = await GetTextAsync(page);
        var note = text?.Annotations.LastOrDefault(a => a.Kind == AnnotationKind.Note);
        if (note is not null) ShowAnnotationFlyout(note, hostPosition);
    }

    public void DeleteAnnotation(PdfAnnotation annotation)
    {
        // Right away, so that a key held down doesn't delete it again.
        if (_selected?.Name == annotation.Name && annotation.Name.Length > 0) Deselect();
        _ = QueueDrawingEdit(document => DeleteAnnotationAsync(document, annotation));
    }

    private async Task DeleteAnnotationAsync(PdfDocument document, PdfAnnotation annotation)
    {
        _lastInk = null;
        int edits = document.EditCount;
        AnnotationSnapshot? removed;
        try
        {
            removed = await document.RemoveAnnotationAsync(annotation);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            ReportEditFailure(document, ex, "The annotation couldn't be deleted.");
            return;
        }
        if (document != _document) return;
        if (removed is not null) Record(new PresenceChange(annotation, added: false, removed));
        else if (document.EditCount != edits)
        {
            // A kind that can't be put back, such as a stamp: its deletion can't be undone, so Ctrl+Z doesn't take
            // back an older change instead, as if it were this one.
            _undo.Clear();
            _redo.Clear();
            _historyVersion++;
        }
        InvalidatePage(annotation.PageIndex);
        AnnotationsChanged?.Invoke(this, annotation.PageIndex);
    }

    /// <summary>What a PDFium edit throws when the document closed meanwhile or the page couldn't be changed.</summary>
    private static bool IsEditFailure(Exception ex) => ex is ObjectDisposedException or InvalidOperationException or InvalidDataException;

    private void ReportEditFailure(PdfDocument document, Exception ex, string message)
    {
        // A document that was closed or replaced meanwhile has nobody left to tell.
        if (ex is ObjectDisposedException || document != _document) return;
        EditFailed?.Invoke(this, message);
    }

    public async void RevealAnnotation(PdfAnnotation annotation)
    {
        var text = await GetTextAsync(annotation.PageIndex);
        EnsurePageVisible(annotation.PageIndex, annotation.Bounds);
        await Task.Delay(50);
        var current = text?.Annotations.FirstOrDefault(a => a.Index == annotation.Index) ?? annotation;
        var r = _pageRects[annotation.PageIndex];
        if (r.IsEmpty) return;
        if (current.Ink is not null || current.FreeText is not null)
        {
            ClearSelection();
            Select(current);
            return;
        }
        var p = RotatedLayoutPoint(annotation.PageIndex, new Point(current.Bounds.Left, current.Bounds.Bottom));
        ShowAnnotationFlyout(current, new Point(r.X + p.X, r.Y + p.Y));
    }

    /// <summary>Whether this is another program's text box, whose text is drawn from its appearance, which writing /Contents wouldn't change.</summary>
    private static bool IsForeignText(PdfAnnotation annotation) => annotation is { Kind: AnnotationKind.FreeText, FreeText: null };

    private void ShowAnnotationFlyout(PdfAnnotation annotation, Point at)
    {
        if (_document is null) return;
        var document = _document;
        var header = new TextBlock
        {
            Text = string.Join(" · ", new[] { string.IsNullOrEmpty(annotation.Author) ? null : annotation.Author, annotation.Modified?.LocalDateTime.ToString("g") }.Where(s => s is not null)),
            Style = (Style)Application.Current.Resources["SecondaryCaptionTextBlockStyle"],
        };
        var box = new TextBox
        {
            Text = annotation.Contents,
            PlaceholderText = IsForeignText(annotation) ? "" : "Add a note",
            IsReadOnly = IsForeignText(annotation),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 96,
            MaxHeight = 280,
            Width = 300,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var delete = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new FontIcon { Glyph = "\uE74D", FontSize = 14 }, new TextBlock { Text = "Delete" } } },
        };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new Grid { Children = { new TextBlock { Text = KindName(annotation.Kind), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] } } },
                    header,
                    box,
                    delete,
                },
            },
        };
        if (string.IsNullOrEmpty(header.Text)) header.Visibility = Visibility.Collapsed;
        var edit = new NoteEdit(document, annotation, box, flyout);
        box.TextChanged += (_, _) => DraftVersion++;
        _noteEdits.Add(edit);
        delete.Click += async (_, _) =>
        {
            _noteEdits.Remove(edit);
            flyout.Hide();
            // The text typed into the note is written first, so undoing the deletion puts it back with the note.
            await CommitNoteAsync(edit);
            if (edit.Document == _document) DeleteAnnotation(annotation);
        };
        flyout.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        flyout.Closed += async (_, _) =>
        {
            if (!_noteEdits.Remove(edit)) return;
            await CommitNoteAsync(edit);
        };
        flyout.ShowAt(_host, new FlyoutShowOptions { Position = at, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    /// <summary>The text box of an open note flyout, whose text is written to the document when the flyout closes.</summary>
    private sealed class NoteEdit(PdfDocument document, PdfAnnotation annotation, TextBox box, Flyout flyout)
    {
        public PdfDocument Document { get; } = document;
        public PdfAnnotation Annotation { get; } = annotation;
        public Flyout Flyout { get; } = flyout;
        public string Text => box.Text;
        /// <summary>The contents last written to the document.</summary>
        public string Saved { get; set; } = annotation.Contents;
        public bool IsChanged => box.Text != Saved;
    }

    private readonly List<NoteEdit> _noteEdits = [];

    /// <summary>
    /// Writes the text of open note flyouts to the document. Closing the window, the document or
    /// reloading doesn't close the flyout first, so without this the text typed into it is lost.
    /// </summary>
    public async Task CommitPendingEditsAsync()
    {
        await CommitTextEditAsync();
        foreach (var edit in _noteEdits.ToList()) await CommitNoteAsync(edit);
    }

    private async Task CommitNoteAsync(NoteEdit edit)
    {
        if (!edit.IsChanged || edit.Document != _document) return;
        // Typing can go on while the flyout stays open; that's written when it closes.
        string text = edit.Text, saved = edit.Saved;
        // Set first, so a commit that starts meanwhile doesn't write the same text again.
        edit.Saved = text;
        int page = edit.Annotation.PageIndex;
        string? replaced;
        try
        {
            replaced = await edit.Document.SetAnnotationContentsAsync(edit.Annotation, text);
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            // Still unsaved, so closing the window doesn't drop the text without asking.
            if (edit.Saved == text) edit.Saved = saved;
            ReportEditFailure(edit.Document, ex, "The note couldn't be changed.");
            return;
        }
        if (edit.Document != _document) return;
        if (replaced is not null) RecordContents(edit.Annotation, replaced, text);
        _texts.Remove(page);
        EnsureText(page);
        AnnotationsChanged?.Invoke(this, page);
    }

    // ---------------------------------------------------------------- annotation history

    /// <summary>A change to one annotation, which can be taken back and made again.</summary>
    private abstract class AnnotationChange(PdfAnnotation annotation)
    {
        /// <summary>The annotation as last read, to find it in the document by.</summary>
        public PdfAnnotation Annotation { get; protected set; } = annotation;
        public abstract Task UndoAsync(PdfDocument document);
        public abstract Task RedoAsync(PdfDocument document);
    }

    /// <summary>An annotation added or deleted. Taking back either is doing the other.</summary>
    private sealed class PresenceChange(PdfAnnotation annotation, bool added, AnnotationSnapshot? removed = null) : AnnotationChange(annotation)
    {
        private AnnotationSnapshot? _removed = removed;
        public bool Added => added;

        public override Task UndoAsync(PdfDocument document) => added ? RemoveAsync(document) : RestoreAsync(document);
        public override Task RedoAsync(PdfDocument document) => added ? RestoreAsync(document) : RemoveAsync(document);

        /// <summary>Whether this adds <paramref name="other"/>, which Recto named when it added it.</summary>
        public bool Adds(PdfAnnotation other) =>
            added && other.Name.Length > 0 && other.Name == Annotation.Name && other.PageIndex == Annotation.PageIndex && other.Subtype == Annotation.Subtype;

        private async Task RemoveAsync(PdfDocument document) => _removed = await document.RemoveAnnotationAsync(Annotation);

        private async Task RestoreAsync(PdfDocument document)
        {
            // Gone already when it was to be removed, so there's nothing to put back.
            if (_removed is null) return;
            Annotation = await document.RestoreAnnotationAsync(_removed);
            _removed = null;
        }
    }

    /// <summary>A note's text changed from <paramref name="before"/> to <paramref name="after"/>.</summary>
    private sealed class ContentsChange(PdfAnnotation annotation, string before, string after) : AnnotationChange(annotation)
    {
        public override Task UndoAsync(PdfDocument document) => document.SetAnnotationContentsAsync(Annotation, before);
        public override Task RedoAsync(PdfDocument document) => document.SetAnnotationContentsAsync(Annotation, after);
    }

    /// <summary>A drawing or text box changed in place.</summary>
    private sealed class DrawingChange(PdfAnnotation annotation, Func<PdfDocument, Task> undo, Func<PdfDocument, Task> redo) : AnnotationChange(annotation)
    {
        public override Task UndoAsync(PdfDocument document) => undo(document);
        public override Task RedoAsync(PdfDocument document) => redo(document);
    }

    /// <summary>Steps of annotation changes, each taken back or made again as a whole.</summary>
    private readonly Stack<AnnotationChange[]> _undo = new(), _redo = new();
    /// <summary>Changes whenever a step is recorded, to tell whether one was while an undo was being made.</summary>
    private int _historyVersion;
    /// <summary>One undo or redo at a time, so a held-down key takes back steps in order.</summary>
    private readonly SemaphoreSlim _historyTurn = new(1, 1);

    private void Record(params AnnotationChange[] step)
    {
        if (step.Length == 0) return;
        _undo.Push(step);
        _redo.Clear();
        _historyVersion++;
    }

    private void RecordContents(PdfAnnotation annotation, string before, string after)
    {
        // The first text typed into an annotation just added goes with it: undo takes back both, and redo puts back the text too.
        if (before.Length == 0 && _undo.TryPeek(out var last) && last.Any(c => c is PresenceChange p && p.Adds(annotation)))
        {
            _redo.Clear();
            _historyVersion++;
            return;
        }
        Record(new ContentsChange(annotation, before, after));
    }

    /// <summary>Takes back the last highlight, note or other annotation change.</summary>
    public async void Undo() => await StepHistoryAsync(undo: true);

    /// <summary>Makes again the last annotation change that was undone.</summary>
    public async void Redo() => await StepHistoryAsync(undo: false);

    private async Task StepHistoryAsync(bool undo)
    {
        var document = _document;
        // The menu of sizes for a text box is open: undoing would close the box, which it's kept open not to.
        if (document is null || _textEditHeld) return;
        await _historyTurn.WaitAsync();
        try
        {
            // A stroke still being written is the latest edit, and what's taken back.
            await _drawingEdits;
            // Text typed into a note that's still open is the latest edit. Undo writes it first, so it's what's
            // taken back. Redo has nothing to make again after it, just as writing it would drop the steps that
            // could be redone; the note stays open with its text.
            bool drafting = HasPendingEdits;
            if (undo ? _undo.Count == 0 && !drafting : _redo.Count == 0 || drafting) return;
            await CommitPendingEditsAsync();
            // A draft that couldn't be written stays in its flyout or box, to be written when that closes.
            if (HasDrafts) return;
            // What was selected may be gone or elsewhere after this, and the next stroke starts a drawing of its own.
            Deselect();
            _lastInk = null;
            // The flyouts close, as they would show text the document no longer has.
            foreach (var edit in _noteEdits.ToList())
            {
                _noteEdits.Remove(edit);
                edit.Flyout.Hide();
            }
            var (from, to) = undo ? (_undo, _redo) : (_redo, _undo);
            if (document != _document || !from.TryPop(out var step)) return;
            int version = _historyVersion;
            var pages = new SortedSet<int>();
            try
            {
                foreach (var change in undo ? Enumerable.Reverse(step) : step)
                {
                    pages.Add(change.Annotation.PageIndex);
                    await (undo ? change.UndoAsync(document) : change.RedoAsync(document));
                }
                // The document closed or reloaded meanwhile: its history is gone. An edit made meanwhile drops
                // what could be redone, this too.
                if (document == _document && (!undo || version == _historyVersion)) to.Push(step);
            }
            catch (Exception ex) when (IsEditFailure(ex))
            {
                // Left out of the history: partly made, it can't be taken back or made again as a whole.
                ReportEditFailure(document, ex, undo ? "The change couldn't be undone." : "The change couldn't be redone.");
            }
            if (document != _document) return;
            foreach (int page in pages)
            {
                InvalidatePage(page);
                AnnotationsChanged?.Invoke(this, page);
            }
            RevealAnnotationArea(step[0].Annotation);
        }
        finally
        {
            _historyTurn.Release();
        }
    }

    /// <summary>Scrolls to an annotation that undo or redo changed, unless it's in view already.</summary>
    private void RevealAnnotationArea(PdfAnnotation annotation)
    {
        var target = LayoutRect(annotation.PageIndex, annotation.Bounds);
        target.Intersect(VisibleContentRect());
        if (target.IsEmpty) EnsurePageVisible(annotation.PageIndex, annotation.Bounds);
    }

    public static string KindName(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Note => "Note",
        AnnotationKind.Highlight => "Highlight",
        AnnotationKind.Underline => "Underline",
        AnnotationKind.StrikeOut => "Strikethrough",
        AnnotationKind.Squiggly => "Squiggly underline",
        AnnotationKind.FreeText => "Text box",
        AnnotationKind.Shape => "Shape",
        AnnotationKind.Ink => "Drawing",
        AnnotationKind.Stamp => "Stamp",
        AnnotationKind.Attachment => "Attachment",
        _ => "Annotation",
    };

    private static async Task LaunchUriAsync(string uri)
    {
        if (LaunchPolicy.ParseExternalLink(uri) is not { } parsed) return;
        try
        {
            await Launcher.LaunchUriAsync(parsed);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or UnauthorizedAccessException)
        {
            // Nothing to open it with, or Windows refused: the link just doesn't open.
        }
    }
}
