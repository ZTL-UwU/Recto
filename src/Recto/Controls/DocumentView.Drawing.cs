using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Recto.Pdf;
using Recto.Services;
using Windows.Foundation;
using Windows.UI;

namespace Recto.Controls;

/// <summary>What pressing on a page does, besides selecting text and following links.</summary>
public enum AnnotationTool
{
    None,
    Pen,
    Highlighter,
    Eraser,
    Text,
}

// Drawing, erasing and text boxes. Strokes are drawn over the page as the pointer moves and stay there until the page
// has been drawn again with them on it. A finger scrolls whatever the tool, so a hand resting on a screen leaves no marks.
public sealed partial class DocumentView
{
    /// <summary>How much of the page shows through highlighter strokes: 0 is none of the ink, 255 solid ink.</summary>
    public const byte HighlighterOpacity = 110;
    /// <summary>Strokes finished this soon after the last, in the same style, join its drawing, so a word or a signature is one annotation.</summary>
    private const long JoinStrokesMilliseconds = 1500;
    /// <summary>How far a line being drawn trails the pointer, in device pixels: enough to even out a mouse's whole pixels, too little to see.</summary>
    private const double StrokeLagPixels = 3;
    private const double DefaultTextBoxWidth = 240;

    private static readonly Color ErasedColor = Color.FromArgb(255, 128, 128, 128);

    private Color _toolColor = Colors.Black;
    private double _toolSize = 2;
    private readonly Brush _accentBrush;
    private readonly Rectangle _frame = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed, StrokeDashArray = [4, 3] };
    private readonly Rectangle _grip = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };

    private sealed class StrokeGesture(int page, Polyline preview, Color color, double width, byte opacity)
    {
        public int Page { get; } = page;
        public Polyline Preview { get; } = preview;
        public Color Color { get; } = color;
        public double Width { get; } = width;
        public byte Opacity { get; } = opacity;
        public List<Point> Points { get; } = [];
        /// <summary>Where the pointer is, which the line ends up at when it's let go.</summary>
        public Point Pointer { get; set; }
    }

    /// <summary>Where a press on the eraser has gone. What's under that is wiped from the document when it's released.</summary>
    private sealed class EraseGesture
    {
        public List<(int Page, Point At, double Reach)> Path { get; } = [];
        public HashSet<(string Annotation, int Stroke)> Shown { get; } = [];
        public List<(int Page, Polyline Preview)> Previews { get; } = [];
    }

    private sealed class MoveGesture(PdfAnnotation annotation, Point start, Rect bounds, bool resizing)
    {
        public PdfAnnotation Annotation { get; } = annotation;
        public Point Start { get; } = start;
        public Rect Original { get; } = bounds;
        public Rect Bounds { get; set; } = bounds;
        public bool Resizing { get; } = resizing;
    }

    /// <summary>The box a text box is typed into, over the page where it will be drawn.</summary>
    private sealed class TextEdit(PdfDocument document, int page, PdfAnnotation? annotation, TextBox box, Rect bounds, Color color, double fontSize)
    {
        public PdfDocument Document { get; } = document;
        public int Page { get; } = page;
        /// <summary>The text box being edited; null for a new one, which is only added once it has text.</summary>
        public PdfAnnotation? Annotation { get; } = annotation;
        public TextBox Box { get; } = box;
        public Rect Bounds { get; } = bounds;
        public Color Color { get; set; } = color;
        public double FontSize { get; set; } = fontSize;
        /// <summary>TextBox ends lines with \r.</summary>
        public string Text => Box.Text.ReplaceLineEndings("\n");
        public bool IsChanged => Annotation?.FreeText is { } text
            ? text.Text != Text || text.Color != Color || text.FontSize != FontSize
            : Text.Trim().Length > 0;
    }

    private StrokeGesture? _stroke;
    private EraseGesture? _erase;
    private MoveGesture? _move;
    /// <summary>The pointer of the gesture under way. Others leave it alone, so a hand resting on a screen doesn't end a pen's stroke.</summary>
    private uint? _gesturePointer;
    private TextEdit? _textEdit;
    /// <summary>Whether something else may have the focus without that ending <see cref="_textEdit"/>.</summary>
    private bool _textEditHeld;
    /// <summary>
    /// The last edit to a drawing or text box. They're made one at a time, so each starts from what the last one wrote,
    /// and what follows them, such as an undo or a save, can wait for them.
    /// </summary>
    private Task _drawingEdits = Task.CompletedTask;
    private PdfAnnotation? _selected;
    /// <summary>The drawing the last stroke went into, and when, for the next stroke to join.</summary>
    private (PdfAnnotation Annotation, InkDrawing Ink, long Time)? _lastInk;

    public event EventHandler? ToolChanged;
    /// <summary>Raised when another drawing or text box is selected, or none is.</summary>
    public event EventHandler? SelectedDrawingChanged;

    public AnnotationTool Tool { get; private set; }
    internal PdfAnnotation? SelectedDrawing => _selected;

    /// <summary>
    /// Picks what pressing on a page does, and the color and size it does that with: a line's width or a font size,
    /// both in points.
    /// </summary>
    public void SetTool(AnnotationTool tool, Color color, double size)
    {
        (_toolColor, _toolSize) = (color, size);
        if (tool == Tool) return;
        CancelGestures();
        _lastInk = null;
        Tool = tool;
        if (tool != AnnotationTool.None) ClearSelection();
        // What's picked on the bar then goes to the tool, unless the selection is what the tool would make.
        if (tool != AnnotationTool.None && (tool != AnnotationTool.Text || _selected?.FreeText is null)) Deselect();
        UpdateToolCursor();
        ToolChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateToolCursor() => SetCursor(Tool switch
    {
        AnnotationTool.None => InputSystemCursorShape.Arrow,
        AnnotationTool.Text => InputSystemCursorShape.IBeam,
        _ => InputSystemCursorShape.Cross,
    });

    /// <summary>Changes the text box being typed or the selected drawing to a <paramref name="color"/> or size.</summary>
    public void ApplyStyle(Color? color, double? size)
    {
        if (_textEdit is { } edit)
        {
            edit.Color = color ?? edit.Color;
            edit.FontSize = size ?? edit.FontSize;
            StyleEditor(edit);
            DraftVersion++;
            return;
        }
        if (_selected is not { } annotation) return;
        ChangeDrawing(annotation, ink => ink with { Color = color ?? ink.Color, Width = size ?? ink.Width }, text => text with { Color = color ?? text.Color, FontSize = size ?? text.FontSize });
    }

    /// <summary>Keeps the text box being typed open while something else has the focus, such as the menu of sizes for it.</summary>
    public void HoldTextEdit(bool hold)
    {
        _textEditHeld = hold;
        if (!hold) _textEdit?.Box.Focus(FocusState.Programmatic);
    }

    private void ResetDrawing()
    {
        CancelGestures();
        if (_textEdit is { } edit) _host.Children.Remove(edit.Box);
        _textEdit = null;
        _lastInk = null;
        _textEditHeld = false;
        _drawingEdits = Task.CompletedTask;
        Deselect();
    }

    /// <summary>The color a page shows <paramref name="color"/> in, which night mode changes the same way as what PDFium draws.</summary>
    private Color ShownColor(Color color)
    {
        if (!_inverted) return color;
        static byte Map(byte v) => (byte)(225 - v * 195 / 255);
        return Color.FromArgb(color.A, Map(color.R), Map(color.G), Map(color.B));
    }

    /// <summary>A point on a page, whether or not it's inside it, or null if the page isn't shown.</summary>
    private Point? DisplayPoint(int page, Point content)
    {
        if (!_views.TryGetValue(page, out var view)) return null;
        var rect = _pageRects[page];
        return view.FromView(new Point(content.X - rect.X, content.Y - rect.Y));
    }

    private Point ClampToPage(int page, Point display)
    {
        var size = _document!.PageSizes[page];
        return new Point(Math.Clamp(display.X, 0, size.Width), Math.Clamp(display.Y, 0, size.Height));
    }

    // ---------------------------------------------------------------- gestures

    /// <summary>Handles a press for the current tool. A pen's eraser end erases whichever tool is on. False if there's no tool to press with.</summary>
    private bool StartToolGesture(PointerPoint point, Pointer pointer)
    {
        var tool = point.Properties.IsEraser ? AnnotationTool.Eraser : Tool;
        if (tool == AnnotationTool.None) return false;
        ClearSelection();
        if (tool != AnnotationTool.Text) Deselect();
        if (HitPage(point.Position) is not { } hit) return true;
        switch (tool)
        {
            case AnnotationTool.Pen or AnnotationTool.Highlighter:
                byte opacity = tool == AnnotationTool.Highlighter ? HighlighterOpacity : (byte)255;
                if (!_views.TryGetValue(hit.Page, out var view)) return true;
                var preview = view.AddStroke(ShownColor(_toolColor), _toolSize, opacity / 255.0);
                _stroke = new StrokeGesture(hit.Page, preview, _toolColor, _toolSize, opacity);
                ExtendStroke(hit.Display);
                CaptureForGesture(pointer);
                break;
            case AnnotationTool.Eraser:
                _erase = new EraseGesture();
                EraseAt(hit.Page, hit.Display);
                CaptureForGesture(pointer);
                break;
            case AnnotationTool.Text:
                if (AnnotationAt(hit.Page, hit.Display) is { FreeText: not null } existing) BeginTextEdit(existing);
                else BeginTextEdit(hit.Page, hit.Display);
                break;
        }
        return true;
    }

    private void CaptureForGesture(Pointer pointer)
    {
        _gesturePointer = pointer.PointerId;
        _host.CapturePointer(pointer);
    }

    private void ExtendStroke(Point display)
    {
        if (_stroke is not { } stroke) return;
        display = ClampToPage(stroke.Page, display);
        stroke.Pointer = display;
        if (stroke.Points.Count > 0)
        {
            display = InkDrawing.Follow(stroke.Points[^1], display, StrokeLagPixels / (Zoom * BaseScale * (XamlRoot?.RasterizationScale ?? 1)));
            // Points closer than a device pixel only make the file larger.
            if (Math.Abs(stroke.Points[^1].X - display.X) + Math.Abs(stroke.Points[^1].Y - display.Y) < 0.5 / Zoom) return;
        }
        AddStrokePoint(stroke, display);
    }

    private void AddStrokePoint(StrokeGesture stroke, Point display)
    {
        stroke.Points.Add(display);
        if (_views.TryGetValue(stroke.Page, out var view)) PageView.ExtendStroke(stroke.Preview, view.ToView(display));
    }

    private void EraseAt(int page, Point display)
    {
        if (_erase is not { } erase) return;
        double reach = 4 / Zoom;
        erase.Path.Add((page, display, reach));
        if (!_texts.TryGetValue(page, out var text) || !_views.TryGetValue(page, out var view)) return;
        foreach (var annotation in text.Annotations)
        {
            if (annotation.Ink is not { } ink) continue;
            foreach (int s in ink.StrokesAt(display, reach))
            {
                if (!erase.Shown.Add((annotation.Name, s))) continue;
                var preview = view.AddStroke(ErasedColor, ink.Width + 2, 0.75);
                foreach (var p in ink.Strokes[s]) PageView.ExtendStroke(preview, view.ToView(p));
                erase.Previews.Add((page, preview));
            }
        }
    }

    /// <summary>Follows the pointer in a drawing, erasing or moving gesture. False if none is under way.</summary>
    private bool ContinueToolGesture(PointerRoutedEventArgs e, Point content)
    {
        if (_gesturePointer is { } gesture && gesture != e.Pointer.PointerId) return true;
        if (_stroke is { } stroke)
        {
            // Pointers report faster than frames arrive; the points in between keep fast strokes round.
            foreach (var p in e.GetIntermediatePoints(_host).OrderBy(p => p.Timestamp))
                if (DisplayPoint(stroke.Page, p.Position) is { } display) ExtendStroke(display);
            return true;
        }
        if (_erase is not null)
        {
            foreach (var p in e.GetIntermediatePoints(_host).OrderBy(p => p.Timestamp))
                if (HitPage(p.Position) is { } hit) EraseAt(hit.Page, hit.Display);
            return true;
        }
        if (_move is { } move)
        {
            if (DisplayPoint(move.Annotation.PageIndex, content) is { } at) UpdateMove(move, at);
            return true;
        }
        return false;
    }

    private void UpdateMove(MoveGesture move, Point at)
    {
        var size = _document!.PageSizes[move.Annotation.PageIndex];
        var r = move.Original;
        if (move.Resizing)
        {
            double width = Math.Clamp(at.X - r.Left, PdfDocument.MinTextBoxWidth + 2 * PdfDocument.TextBoxInset, Math.Max(r.Width, size.Width - r.Left));
            move.Bounds = new Rect(r.Left, r.Top, width, r.Height);
        }
        else
        {
            // A drawing larger than the page can't be kept inside it: it stays where it can move to.
            double x = Math.Clamp(r.Left + at.X - move.Start.X, Math.Min(0, r.Left), Math.Max(r.Left, size.Width - r.Width));
            double y = Math.Clamp(r.Top + at.Y - move.Start.Y, Math.Min(0, r.Top), Math.Max(r.Top, size.Height - r.Height));
            move.Bounds = new Rect(x, y, r.Width, r.Height);
        }
        ShowFrame(move.Annotation.PageIndex, move.Bounds, move.Annotation.FreeText is not null);
    }

    /// <summary>Ends a gesture when the pointer is released. False if none is under way.</summary>
    private bool FinishToolGesture()
    {
        _gesturePointer = null;
        if (_stroke is { } stroke)
        {
            _stroke = null;
            FinishStroke(stroke);
            return true;
        }
        if (_erase is { } erase)
        {
            _erase = null;
            FinishErase(erase);
            return true;
        }
        if (_move is { } move)
        {
            _move = null;
            if (move.Bounds == move.Original) return true;
            double dx = move.Bounds.Left - move.Original.Left, dy = move.Bounds.Top - move.Original.Top;
            ChangeDrawing(move.Annotation, ink => ink.Moved(dx, dy),
                text => text with { Bounds = new Rect(text.Bounds.Left + dx, text.Bounds.Top + dy, move.Bounds.Width, text.Bounds.Height) });
            return true;
        }
        return false;
    }

    /// <summary>Drops a gesture the page has moved out from under, while the pointer is still down.</summary>
    private void AbandonGestures()
    {
        if (_gesturePointer is null) return;
        CancelGestures();
        _host.ReleasePointerCaptures();
    }

    private void CancelGestures()
    {
        _gesturePointer = null;
        if (_stroke is { } stroke && _views.TryGetValue(stroke.Page, out var view)) view.RemoveStroke(stroke.Preview);
        if (_erase is { } erase) RemovePreviews(erase);
        _stroke = null;
        _erase = null;
        if (_move is { } move)
        {
            _move = null;
            ShowFrame(move.Annotation.PageIndex, move.Original, move.Annotation.FreeText is not null);
        }
    }

    private void RemovePreviews(EraseGesture erase)
    {
        foreach (var (page, preview) in erase.Previews)
        {
            if (_views.TryGetValue(page, out var view)) view.RemoveStroke(preview);
        }
    }

    /// <summary>Runs an edit once those before it are done, unless the document has closed or changed by then.</summary>
    private Task QueueDrawingEdit(Func<PdfDocument, Task> edit)
    {
        if (_document is not { } document) return Task.CompletedTask;
        DraftVersion++;
        return _drawingEdits = RunAsync(_drawingEdits);

        async Task RunAsync(Task previous)
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            if (document == _document) await edit(document);
        }
    }

    private void FinishStroke(StrokeGesture stroke)
    {
        if (stroke.Points[^1] != stroke.Pointer) AddStrokePoint(stroke, stroke.Pointer);
        long finished = Environment.TickCount64;
        _ = QueueDrawingEdit(document => WriteStrokeAsync(document, stroke, finished));
    }

    private async Task WriteStrokeAsync(PdfDocument document, StrokeGesture stroke, long finished)
    {
        Point[] points = [.. stroke.Points];
        AnnotationChange? change = null;
        try
        {
            if (_lastInk is { } last && last.Annotation.PageIndex == stroke.Page && finished - last.Time < JoinStrokesMilliseconds
                && last.Ink.Color == stroke.Color && last.Ink.Width == stroke.Width && last.Ink.Opacity == stroke.Opacity)
            {
                var joined = last.Ink with { Strokes = [.. last.Ink.Strokes, points] };
                // Gone already if it was deleted meanwhile; the stroke then starts a drawing of its own.
                if (await document.SetInkAsync(last.Annotation, joined) is not null)
                {
                    change = InkChange(last.Annotation, last.Ink, joined);
                    _lastInk = (last.Annotation, joined, finished);
                }
            }
            if (change is null)
            {
                var ink = new InkDrawing([points], stroke.Color, stroke.Width, stroke.Opacity);
                var created = await document.AddInkAsync(stroke.Page, ink);
                change = new PresenceChange(created, added: true);
                _lastInk = (created, ink, finished);
            }
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            if (_views.TryGetValue(stroke.Page, out var failed)) failed.RemoveStroke(stroke.Preview);
            ReportEditFailure(document, ex, "The drawing couldn't be added.");
            return;
        }
        if (document != _document) return;
        Record(change);
        if (_views.TryGetValue(stroke.Page, out var view)) view.FinishStroke(stroke.Preview);
        InvalidatePage(stroke.Page);
        AnnotationsChanged?.Invoke(this, stroke.Page);
    }

    private void FinishErase(EraseGesture erase) => _ = QueueDrawingEdit(document => EraseAsync(document, erase));

    private async Task EraseAsync(PdfDocument document, EraseGesture erase)
    {
        var changes = new List<AnnotationChange>();
        var pages = new SortedSet<int>();
        try
        {
            foreach (var path in erase.Path.GroupBy(p => p.Page))
            {
                // The drawings as they are now: the ones the pointer went over may still have been being written.
                var text = await GetTextAsync(path.Key);
                if (document != _document) return;
                foreach (var annotation in text?.Annotations ?? [])
                {
                    if (annotation.Ink is not { } ink) continue;
                    var strokes = path.SelectMany(p => ink.StrokesAt(p.At, p.Reach)).ToHashSet();
                    if (strokes.Count == 0) continue;
                    Point[][] left = [.. ink.Strokes.Where((_, i) => !strokes.Contains(i))];
                    pages.Add(annotation.PageIndex);
                    if (left.Length == 0)
                    {
                        if (await document.RemoveAnnotationAsync(annotation) is { } removed) changes.Add(new PresenceChange(annotation, added: false, removed));
                    }
                    else
                    {
                        var after = ink with { Strokes = left };
                        if (await document.SetInkAsync(annotation, after) is not null) changes.Add(InkChange(annotation, ink, after));
                    }
                }
            }
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            // Strokes erased before the failure stay in the history below.
            ReportEditFailure(document, ex, "The drawing couldn't be erased.");
        }
        if (document != _document) return;
        _lastInk = null;
        Record([.. changes]);
        foreach (var (page, preview) in erase.Previews)
        {
            if (!_views.TryGetValue(page, out var view)) continue;
            if (pages.Contains(page)) view.FinishStroke(preview);
            else view.RemoveStroke(preview);
        }
        foreach (int page in pages)
        {
            InvalidatePage(page);
            AnnotationsChanged?.Invoke(this, page);
        }
    }

    private static DrawingChange InkChange(PdfAnnotation annotation, InkDrawing before, InkDrawing after) =>
        new(annotation, document => document.SetInkAsync(annotation, before), document => document.SetInkAsync(annotation, after));

    /// <summary>Changes a drawing or text box, from how it is once the edits before this are done.</summary>
    private void ChangeDrawing(PdfAnnotation annotation, Func<InkDrawing, InkDrawing> ink, Func<FreeText, FreeText> text) =>
        _ = QueueDrawingEdit(document => ChangeDrawingAsync(document, annotation, ink, text));

    /// <summary>Writes a new form of a drawing or text box, as a step that can be undone, and keeps it selected if it was.</summary>
    private async Task ChangeDrawingAsync(PdfDocument document, PdfAnnotation annotation, Func<InkDrawing, InkDrawing> changeInk, Func<FreeText, FreeText> changeText)
    {
        var page = await GetTextAsync(annotation.PageIndex);
        if (document != _document) return;
        var current = page?.Annotations.FirstOrDefault(a => a.Name == annotation.Name) ?? annotation;
        AnnotationChange? change = null;
        try
        {
            if (current.Ink is { } ink)
            {
                var after = changeInk(ink);
                if (await document.SetInkAsync(current, after) is { } before) change = InkChange(current, before, after);
            }
            else if (current.FreeText is { } text)
            {
                var after = changeText(text);
                if (await document.SetFreeTextAsync(current, after) is { } before)
                    change = new DrawingChange(current, d => d.SetFreeTextAsync(current, before), d => d.SetFreeTextAsync(current, after));
            }
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            ReportEditFailure(document, ex, "The annotation couldn't be changed.");
        }
        if (document != _document) return;
        if (change is null)
        {
            if (_selected?.Name == current.Name) Select(current);
            return;
        }
        Record(change);
        if (_lastInk?.Annotation.Name == current.Name) _lastInk = null;
        InvalidatePage(current.PageIndex);
        AnnotationsChanged?.Invoke(this, current.PageIndex);
        if (_selected?.Name != current.Name) return;
        page = await GetTextAsync(current.PageIndex);
        if (document == _document && _selected?.Name == current.Name && page?.Annotations.FirstOrDefault(a => a.Name == current.Name) is { } updated) Select(updated);
    }

    // ---------------------------------------------------------------- selected drawing

    private void Select(PdfAnnotation annotation)
    {
        // An edit that was under way when a presentation started finishes during it.
        if (_presentation) return;
        bool changed = _selected?.Name != annotation.Name;
        _selected = annotation;
        ShowFrame(annotation.PageIndex, annotation.Bounds, annotation.FreeText is not null);
        if (changed) SelectedDrawingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Deselect()
    {
        if (_selected is null) return;
        _selected = null;
        if (_textEdit is null) HideFrame();
        SelectedDrawingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts moving the selected drawing, or resizing it, if a press is on its frame.</summary>
    private bool StartMove(int page, Point display, Point content, Pointer pointer)
    {
        if (_selected is not { } selected || selected.PageIndex != page) return false;
        var bounds = selected.Bounds;
        bool grip = selected.FreeText is not null && IsOnGrip(page, bounds, content);
        if (!grip && !Inflate(bounds, 3 / Zoom).Contains(display)) return false;
        _move = new MoveGesture(selected, display, bounds, grip);
        CaptureForGesture(pointer);
        return true;
    }

    private bool IsOnGrip(int page, Rect bounds, Point content)
    {
        var corner = GripPoint(page, bounds);
        double reach = 8 / Zoom;
        return Math.Abs(content.X - corner.X) <= reach && Math.Abs(content.Y - corner.Y) <= reach;
    }

    /// <summary>The corner of a box that resizes it, wherever the page's rotation has put it.</summary>
    private Point GripPoint(int page, Rect bounds)
    {
        var rect = _pageRects[page];
        var p = RotatedLayoutPoint(page, new Point(bounds.Right, bounds.Bottom));
        return new Point(rect.X + p.X, rect.Y + p.Y);
    }

    private void ShowFrame(int page, Rect bounds, bool grip)
    {
        var area = LayoutRect(page, bounds);
        if (area.IsEmpty)
        {
            HideFrame();
            return;
        }
        if (_frame.Parent is null)
        {
            Canvas.SetZIndex(_frame, 1000);
            Canvas.SetZIndex(_grip, 1001);
            _frame.Stroke = _accentBrush;
            _grip.Fill = _accentBrush;
            _host.Children.Add(_frame);
            _host.Children.Add(_grip);
        }
        double z = Math.Max(0.01, Zoom), gap = 3 / z;
        _frame.StrokeThickness = 1.5 / z;
        _frame.Width = area.Width + 2 * gap;
        _frame.Height = area.Height + 2 * gap;
        Canvas.SetLeft(_frame, area.Left - gap);
        Canvas.SetTop(_frame, area.Top - gap);
        _frame.Visibility = Visibility.Visible;
        _grip.Visibility = grip ? Visibility.Visible : Visibility.Collapsed;
        if (!grip) return;
        var corner = GripPoint(page, bounds);
        _grip.Width = _grip.Height = 9 / z;
        Canvas.SetLeft(_grip, corner.X - 4.5 / z);
        Canvas.SetTop(_grip, corner.Y - 4.5 / z);
    }

    private void HideFrame() => _frame.Visibility = _grip.Visibility = Visibility.Collapsed;

    /// <summary>Puts what floats over the pages back on them after they were laid out or the zoom changed.</summary>
    private void PlaceOverlays()
    {
        if (_textEdit is { } edit)
        {
            if (_pageRects[edit.Page].IsEmpty) _ = CommitTextEditAsync();
            else PlaceEditor(edit);
        }
        else if (_selected is { } selected)
        {
            if (_pageRects[selected.PageIndex].IsEmpty) Deselect();
            else ShowFrame(selected.PageIndex, selected.Bounds, selected.FreeText is not null);
        }
    }

    // ---------------------------------------------------------------- text boxes

    /// <summary>Opens a new text box at a point of a page. It's only added to the document once it has text.</summary>
    private void BeginTextEdit(int page, Point display)
    {
        if (_document is not { } document) return;
        var size = document.PageSizes[page];
        double width = Math.Clamp(size.Width - display.X - 8, 2 * PdfDocument.MinTextBoxWidth, DefaultTextBoxWidth);
        var prefs = AppState.Preferences.Annotate;
        double fontSize = Math.Clamp(prefs.TextSize, 4, 144);
        var color = Color.FromArgb(255, (byte)(prefs.TextColor >> 16), (byte)(prefs.TextColor >> 8), (byte)prefs.TextColor);
        // With the first line level with the point.
        var bounds = new Rect(Math.Min(display.X, Math.Max(0, size.Width - width)), Math.Max(0, display.Y - fontSize * 0.7), width, 0);
        OpenEditor(new TextEdit(document, page, null, CreateEditor(), bounds, color, fontSize));
    }

    /// <summary>Opens a text box of Recto's for editing.</summary>
    private void BeginTextEdit(PdfAnnotation annotation)
    {
        if (_document is not { } document || annotation.FreeText is not { } text) return;
        ClearSelection();
        var edit = new TextEdit(document, annotation.PageIndex, annotation, CreateEditor(), text.Bounds, text.Color, text.FontSize);
        edit.Box.Text = text.Text;
        OpenEditor(edit);
        edit.Box.SelectionStart = edit.Box.Text.Length;
    }

    private TextBox CreateEditor()
    {
        var box = new TextBox
        {
            Style = (Style)Application.Current.Resources["AnnotationTextBoxStyle"],
            FontFamily = new FontFamily(TextOutline.FontFamily),
            Padding = new Thickness(PdfDocument.TextBoxInset * BaseScale),
            MaxLength = 20000,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Text box");
        return box;
    }

    private void OpenEditor(TextEdit edit)
    {
        var box = edit.Box;
        Deselect();
        _textEdit = edit;
        Canvas.SetZIndex(box, 999);
        _host.Children.Add(box);
        box.TextChanged += (_, _) => DraftVersion++;
        box.SizeChanged += (_, _) => { if (_textEdit == edit) PlaceEditor(edit); };
        box.LostFocus += (_, _) => { if (_textEdit == edit && !_textEditHeld) _ = CommitTextEditAsync(); };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        box.AddHandler(PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Escape) return;
            // Keeps what was typed, as clicking away does: nothing here is lost by a slip of a key.
            e.Handled = true;
            _ = CommitTextEditAsync();
            Focus(FocusState.Programmatic);
        }), true);
        StyleEditor(edit);
        PlaceEditor(edit);
    }

    private void StyleEditor(TextEdit edit)
    {
        edit.Box.Foreground = new SolidColorBrush(ShownColor(edit.Color));
        edit.Box.FontSize = edit.FontSize * BaseScale;
        // Covers the page's own copy of the text, until it's drawn again.
        if (edit.Annotation is not null) edit.Box.Background = new SolidColorBrush(_inverted ? Color.FromArgb(255, 30, 30, 30) : Colors.White);
    }

    /// <summary>Gives what's being drawn or typed the colors of night mode, or takes them away.</summary>
    private void RestyleDrafts()
    {
        if (_stroke is { } stroke) stroke.Preview.Stroke = new SolidColorBrush(ShownColor(stroke.Color));
        if (_textEdit is { } edit) StyleEditor(edit);
    }

    private void PlaceEditor(TextEdit edit)
    {
        var box = edit.Box;
        var rect = _pageRects[edit.Page];
        double height = box.ActualHeight > 0 ? box.ActualHeight / BaseScale : edit.FontSize * 1.33 + 2 * PdfDocument.TextBoxInset;
        double top = PdfDocument.TextBoxTop(edit.Bounds.Top, height, edit.Document.PageSizes[edit.Page].Height);
        var corner = RotatedLayoutPoint(edit.Page, new Point(edit.Bounds.Left, top));
        // Turned with the page, on which it's drawn the right way up.
        box.Width = edit.Bounds.Width * BaseScale;
        box.RenderTransform = _rotation == 0 ? null : new RotateTransform { Angle = _rotation };
        Canvas.SetLeft(box, rect.X + corner.X);
        Canvas.SetTop(box, rect.Y + corner.Y);
        ShowFrame(edit.Page, new Rect(edit.Bounds.Left, top, edit.Bounds.Width, height), false);
    }

    /// <summary>Closes the text box being typed, writing it to the document. Done by the time the task is.</summary>
    private Task CommitTextEditAsync()
    {
        if (_textEdit is not { } edit) return _drawingEdits;
        _textEdit = null;
        edit.Box.IsHitTestVisible = false;
        return QueueDrawingEdit(_ => CommitAsync(edit));
    }

    private async Task CommitAsync(TextEdit edit)
    {
        string text = edit.Text;
        var written = new FreeText(edit.Bounds, text.TrimEnd(), edit.Color, edit.FontSize);
        PdfAnnotation? current = edit.Annotation;
        if (!edit.IsChanged)
        {
            _host.Children.Remove(edit.Box);
            HideFrame();
            if (current is not null) Select(current);
            return;
        }
        if (text.Trim().Length == 0)
        {
            // Clearing a text box deletes it, instead of leaving an invisible one.
            _host.Children.Remove(edit.Box);
            HideFrame();
            if (current is not null) await DeleteAnnotationAsync(edit.Document, current);
            return;
        }
        try
        {
            if (current is null)
            {
                current = await edit.Document.AddFreeTextAsync(edit.Page, written);
                if (edit.Document == _document) Record(new PresenceChange(current, added: true));
            }
            else if (await edit.Document.SetFreeTextAsync(current, written) is { } before)
            {
                var annotation = current;
                if (edit.Document == _document)
                    Record(new DrawingChange(annotation, d => d.SetFreeTextAsync(annotation, before), d => d.SetFreeTextAsync(annotation, written)));
            }
        }
        catch (Exception ex) when (IsEditFailure(ex))
        {
            // Still unsaved, so what was typed isn't lost: it's written again the next time the box closes.
            if (edit.Document == _document && _textEdit is null)
            {
                _textEdit = edit;
                edit.Box.IsHitTestVisible = true;
                if (edit.Box.Parent is null) _host.Children.Add(edit.Box);
                if (!_pageRects[edit.Page].IsEmpty) PlaceEditor(edit);
            }
            else
            {
                _host.Children.Remove(edit.Box);
            }
            ReportEditFailure(edit.Document, ex, "The text box couldn't be saved.");
            return;
        }
        if (edit.Document != _document) return;
        // The text box stays up until the page shows its text, so the text doesn't blink out meanwhile.
        if (_views.TryGetValue(edit.Page, out var view)) view.CallWhenRedrawn(() => _host.Children.Remove(edit.Box));
        else _host.Children.Remove(edit.Box);
        InvalidatePage(edit.Page);
        AnnotationsChanged?.Invoke(this, edit.Page);
        var page = await GetTextAsync(edit.Page);
        if (edit.Document != _document) return;
        string name = current.Name;
        if (page?.Annotations.FirstOrDefault(a => a.Name == name) is { } updated && _textEdit is null) Select(updated);
    }
}
