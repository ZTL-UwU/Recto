using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Recto.Pdf;
using Recto.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using WinRT;

namespace Recto.Controls;

/// <summary>Highlights over a page, bottom to top. Each is replaced on its own, so one changing leaves the others be.</summary>
internal enum OverlayLayer
{
    Search,
    CurrentHit,
    Selection,
    Strokes,
}

/// <summary>
/// Displays a single page: a full-page bitmap, optional high resolution tiles for the visible
/// region when zoomed in, and an overlay for selection / search highlights.
/// </summary>
internal sealed partial class PageView : Canvas
{
    /// <summary>In device pixels.</summary>
    public const int TileSize = 512;
    private const double MaxFullPagePixels = 6_000_000;
    private const double LowResPixels = 1_500_000;
    private const double MaxBitmapSide = 8192;
    /// <summary>Most tiles rendered by one PDFium pass: 64 tiles of 512 px is 16 MP, about two 4K screens.</summary>
    private const int MaxTilesPerBatch = 64;

    private readonly PdfDocument _document;
    private readonly Image _base = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Canvas _tiles = new() { IsHitTestVisible = false };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Canvas[] _overlayLayers = [.. Enum.GetValues<OverlayLayer>().Select(_ => new Canvas())];
    private readonly Border _frameHost = new() { IsHitTestVisible = false };
    private readonly Border _paper = new() { IsHitTestVisible = false };
    private readonly Dictionary<(int Col, int Row), Image> _tileImages = [];
    private readonly Dictionary<(int Col, int Row), TileBatch> _pendingTiles = [];
    /// <summary>Tiles from before the last <see cref="Invalidate"/>. They stay up until replaced: the bitmap under them is blurry.</summary>
    private readonly HashSet<(int Col, int Row)> _staleTiles = [];
    /// <summary>
    /// Tiles from an earlier zoom, oldest first, in <see cref="_tiles"/> under the current ones. Stretched, they look much
    /// better than the bitmap under them, so they stay up until the current ones cover the view.
    /// </summary>
    private readonly List<(Image Image, double Pixels)> _previousTiles = [];
    /// <summary>Whether some of <see cref="_previousTiles"/> show the page from before the last <see cref="Invalidate"/>.</summary>
    private bool _previousStale;
    /// <summary>The tiles in view at the last <see cref="Render"/>, or null if none are.</summary>
    private (int C0, int R0, int C1, int R1)? _visibleTiles;
    private bool _tiled;
    private CancellationTokenSource? _baseCts;
    private double _baseScale;
    private double _tileScale;
    private int _renderedRotation = -1;
    private bool _renderedInverted;
    private int _version;
    /// <summary>The <see cref="_version"/> that <see cref="_base"/> shows.</summary>
    private int _baseVersion;
    /// <summary>The scale <see cref="_base"/> shows the page at.</summary>
    private double _shownBaseScale;
    /// <summary>A bitmap of the page lower in resolution than <see cref="_base"/>'s, which replaces it once tiles cover the view.</summary>
    private (SoftwareBitmapSource Source, double Width, double Height, int Version, double Scale)? _heldBase;
    /// <summary>An annotation being moved, which is drawn over the page meanwhile.</summary>
    private PdfAnnotation? _leftOut;

    public PageView(PdfDocument document, int pageIndex, bool framed)
    {
        _document = document;
        PageIndex = pageIndex;
        _paper.Background = new SolidColorBrush(Colors.White);
        if (framed) Children.Add(_frameHost);
        Children.Add(_paper);
        Children.Add(_base);
        Children.Add(_tiles);
        Children.Add(_overlay);
        foreach (var layer in _overlayLayers) _overlay.Children.Add(layer);
        if (framed) AttachFrame();
    }

    public int PageIndex { get; }
    /// <summary>Page size in display points, before user rotation.</summary>
    public Size PagePoints => _document.PageSizes[PageIndex];
    public int PageRotation { get; private set; }
    /// <summary>Layout DIPs per point.</summary>
    public double LayoutScale { get; private set; } = 1;
    /// <summary>Whether the page needs <see cref="Render"/> while scrolling at <paramref name="pixelScale"/>.</summary>
    public bool NeedsScrollRender(double pixelScale, bool inverted)
    {
        if (_tiled || _base.Source is null || _baseScale <= 0 || inverted != _renderedInverted || PageRotation != _renderedRotation) return true;
        double pxPerPoint = LayoutScale * pixelScale;
        return pxPerPoint > 0 && Math.Abs(pxPerPoint - _baseScale) / pxPerPoint > 0.02;
    }
    /// <summary>Raised when PDFium has drawn the page, so work for it that can wait (its text) can start.</summary>
    public event EventHandler? Rendered;

    // Windows 11 card look: rounded corners and an outside hairline in CardStrokeColorDefault, no
    // shadow. Sizes are in screen DIPs and are rescaled to cancel out zoom.
    private const double FrameRadius = 4;
    private static readonly Windows.UI.Color StrokeLight = Windows.UI.Color.FromArgb(0x0F, 0, 0, 0);
    private static readonly Windows.UI.Color StrokeDark = Windows.UI.Color.FromArgb(0x19, 0, 0, 0);

    private ShapeVisual? _outline;
    private CompositionRoundedRectangleGeometry? _outlineGeometry, _clipGeometry;
    private CompositionColorBrush? _outlineBrush;
    /// <summary>Device pixels per layout DIP the frame was last sized for; 0 before the first render.</summary>
    private double _framePixelScale;

    private void AttachFrame()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(_frameHost).Compositor;
        // A rounded rect a hairline larger than the page, behind it.
        _outlineGeometry = compositor.CreateRoundedRectangleGeometry();
        _outlineBrush = compositor.CreateColorBrush();
        var shape = compositor.CreateSpriteShape(_outlineGeometry);
        shape.FillBrush = _outlineBrush;
        _outline = compositor.CreateShapeVisual();
        _outline.Shapes.Add(shape);
        ElementCompositionPreview.SetElementChildVisual(_frameHost, _outline);

        _clipGeometry = compositor.CreateRoundedRectangleGeometry();
        foreach (var element in new UIElement[] { _paper, _base, _tiles, _overlay })
            ElementCompositionPreview.GetElementVisual(element).Clip = compositor.CreateGeometricClip(_clipGeometry);
        ActualThemeChanged += (_, _) => UpdateFrame();
    }

    private void UpdateFrame()
    {
        if (_outline is null || _outlineGeometry is null || _outlineBrush is null || _clipGeometry is null) return;
        if (double.IsNaN(Width) || double.IsNaN(Height)) return;
        double raster = XamlRoot?.RasterizationScale ?? 1;
        double pixel = _framePixelScale > 0 ? _framePixelScale : raster;
        // Whole device pixels, so the clip lines up with the pixel-snapped bitmap instead of cutting into its last row.
        var size = new Vector2((float)(Math.Round(Width * pixel) / pixel), (float)(Math.Round(Height * pixel) / pixel));
        float hairline = (float)(Math.Max(1, Math.Round(raster)) / pixel);
        bool rounded = AppState.Preferences.RoundedPageCorners;
        float radius = rounded ? (float)(FrameRadius * raster / pixel) : 0;
        _clipGeometry.Size = size;
        _clipGeometry.CornerRadius = new Vector2(radius);
        _outline.Offset = new Vector3(-hairline, -hairline, 0);
        _outline.Size = size + new Vector2(2 * hairline);
        _outlineGeometry.Size = _outline.Size;
        _outlineGeometry.CornerRadius = new Vector2(rounded ? radius + hairline : 0);
        _outlineBrush.Color = ActualTheme == ElementTheme.Dark ? StrokeDark : StrokeLight;
    }

    /// <summary>Reapplies the frame after the page corner preference changes.</summary>
    public void RefreshFrame() => UpdateFrame();

    public void SetPaperColor(Windows.UI.Color color) => ((SolidColorBrush)_paper.Background).Color = color;

    public void Arrange(Rect rect, double layoutScale, int rotation)
    {
        SetLeft(this, rect.X);
        SetTop(this, rect.Y);
        Width = rect.Width;
        Height = rect.Height;
        _paper.Width = rect.Width;
        _paper.Height = rect.Height;
        UpdateFrame();
        if (rotation != PageRotation || _base.Source is null)
        {
            _base.Width = rect.Width;
            _base.Height = rect.Height;
        }
        if (rotation != PageRotation)
        {
            // Old bitmaps have the wrong orientation; drop them.
            Free(_base);
            DiscardHeldBase();
            ClearTiles();
            _baseScale = 0;
        }
        LayoutScale = layoutScale;
        PageRotation = rotation;
    }

    /// <summary>Converts a rect in display points to page-local layout DIPs, applying rotation.</summary>
    public Rect ToView(Rect r)
    {
        var p1 = ToView(new Point(r.Left, r.Top));
        var p2 = ToView(new Point(r.Right, r.Bottom));
        return new Rect(p1, p2);
    }

    public Point ToView(Point p)
    {
        double w = PagePoints.Width, h = PagePoints.Height;
        var q = PageRotation switch
        {
            90 => new Point(h - p.Y, p.X),
            180 => new Point(w - p.X, h - p.Y),
            270 => new Point(p.Y, w - p.X),
            _ => p,
        };
        return new Point(q.X * LayoutScale, q.Y * LayoutScale);
    }

    /// <summary>Converts a page-local layout DIP point to display points.</summary>
    public Point FromView(Point v)
    {
        double w = PagePoints.Width, h = PagePoints.Height;
        double x = v.X / LayoutScale, y = v.Y / LayoutScale;
        return PageRotation switch
        {
            90 => new Point(y, h - x),
            180 => new Point(w - x, h - y),
            270 => new Point(w - y, x),
            _ => new Point(x, y),
        };
    }

    /// <summary>A rectangle of tiles rendered in one pass: every pass walks the whole page, so fewer passes are much cheaper.</summary>
    private sealed class TileBatch(int c0, int r0, int c1, int r1)
    {
        public readonly CancellationTokenSource Cts = new();
        public int C0 { get; } = c0;
        public int R0 { get; } = r0;
        public int C1 { get; } = c1;
        public int R1 { get; } = r1;

        public IEnumerable<(int Col, int Row)> Keys
        {
            get
            {
                for (int r = R0; r <= R1; r++)
                    for (int c = C0; c <= C1; c++) yield return (c, r);
            }
        }

        public bool Overlaps(int c0, int r0, int c1, int r1) => C0 <= c1 && C1 >= c0 && R0 <= r1 && R1 >= r0;
    }

    public void Invalidate()
    {
        _version++;
        _baseScale = 0;
        CancelPendingTiles();
        _staleTiles.UnionWith(_tileImages.Keys);
        if (_previousTiles.Count > 0) _previousStale = true;
    }

    /// <summary>Draws the page without <paramref name="annotation"/> from now on, or with all of them again if it's null.</summary>
    public void LeaveOut(PdfAnnotation? annotation)
    {
        _leftOut = annotation;
        Invalidate();
    }

    private void CancelPendingTiles()
    {
        foreach (var batch in _pendingTiles.Values) batch.Cts.Cancel();
        _pendingTiles.Clear();
    }

    /// <summary>
    /// Ensures the bitmaps match the current zoom. <paramref name="pixelScale"/> is device pixels per
    /// layout DIP; <paramref name="visible"/> is the visible part of the page in page-local layout DIPs, and
    /// <paramref name="ahead"/> the part around the view to have tiles ready for, in case it scrolls into it.
    /// </summary>
    public void Render(double pixelScale, Rect visible, Rect ahead, bool inverted, bool isVisible)
    {
        if (Math.Abs(pixelScale - _framePixelScale) > 1e-6)
        {
            _framePixelScale = pixelScale;
            UpdateFrame();
        }
        double pxPerPoint = LayoutScale * pixelScale;
        double pw = Width * pixelScale, ph = Height * pixelScale;
        double area = pw * ph, side = Math.Max(pw, ph);
        // Very long, thin pages stay under the pixel budget but can exceed the GPU's texture size.
        bool tiled = area > MaxFullPagePixels || side > MaxBitmapSide;
        double baseScale = tiled ? pxPerPoint * Math.Min(Math.Sqrt(LowResPixels / area), MaxBitmapSide / side) : pxPerPoint;

        if (inverted != _renderedInverted || PageRotation != _renderedRotation)
        {
            Invalidate();
        }

        _tiled = tiled;
        if (_baseScale <= 0 || Math.Abs(baseScale - _baseScale) / baseScale > 0.02)
        {
            RenderBase(baseScale, inverted, isVisible);
        }

        if (!tiled)
        {
            _visibleTiles = null;
            // Until the bitmap at this zoom arrives, the one up is the low resolution one from under the tiles.
            if (BaseIsCurrent(baseScale)) ClearTiles();
            else
            {
                RetireTiles();
                if (isVisible && !visible.IsEmpty) PrunePreviousTiles(visible, pixelScale);
                else ClearPreviousTiles();
            }
            OnRedrawn();
            return;
        }
        if (Math.Abs(_tileScale - pxPerPoint) / pxPerPoint > 0.001)
        {
            RetireTiles();
            _tileScale = pxPerPoint;
        }
        bool inView = isVisible && !visible.IsEmpty;
        if (!inView) _visibleTiles = null;
        if (ahead.IsEmpty)
        {
            CancelPendingTiles();
            foreach (var key in _staleTiles.ToList()) DropTile(key);
            OnTilesChanged();
            OnRedrawn();
            return;
        }

        int maxW = (int)Math.Ceiling(pw), maxH = (int)Math.Ceiling(ph);
        (int C0, int R0, int C1, int R1) TilesIn(Rect area) => (
            Math.Max(0, (int)(area.Left * pixelScale) / TileSize),
            Math.Max(0, (int)(area.Top * pixelScale) / TileSize),
            Math.Min((maxW - 1) / TileSize, (int)(area.Right * pixelScale) / TileSize),
            Math.Min((maxH - 1) / TileSize, (int)(area.Bottom * pixelScale) / TileSize));
        var near = TilesIn(ahead);
        if (inView) _visibleTiles = TilesIn(visible);
        // Keep a 1-tile ring around the view so small back-and-forth scrolls don't evict and re-render tiles,
        // while only pre-rendering the narrower `near` margin.
        var keep = _visibleTiles is { } vis
            ? (C0: Math.Min(near.C0, vis.C0 - 1), R0: Math.Min(near.R0, vis.R0 - 1), C1: Math.Max(near.C1, vis.C1 + 1), R1: Math.Max(near.R1, vis.R1 + 1))
            : (C0: near.C0 - 1, R0: near.R0 - 1, C1: near.C1 + 1, R1: near.R1 + 1);

        bool InView((int Col, int Row) key) => _visibleTiles is { } v && key.Col >= v.C0 && key.Col <= v.C1 && key.Row >= v.R0 && key.Row <= v.R1;
        foreach (var key in _tileImages.Keys.ToList())
        {
            if (key.Col < keep.C0 || key.Col > keep.C1 || key.Row < keep.R0 || key.Row > keep.R1 || (!InView(key) && _staleTiles.Contains(key))) DropTile(key);
        }
        // Batches still partly near the view finish; ones that scrolled away entirely stop.
        foreach (var batch in _pendingTiles.Values.Distinct().ToList())
        {
            if (batch.Overlaps(near.C0, near.R0, near.C1, near.R1)) continue;
            batch.Cts.Cancel();
            foreach (var key in batch.Keys) _pendingTiles.Remove(key);
        }

        // Cover near in as few passes as possible (every PDFium pass walks the whole page), giving
        // batches that touch the view Visible priority and surrounding-only batches Normal priority.
        foreach (var (bc0, br0, bc1, br1) in MissingTileRects(near.C0, near.R0, near.C1, near.R1))
        {
            var batch = new TileBatch(bc0, br0, bc1, br1);
            foreach (var key in batch.Keys) _pendingTiles[key] = batch;
            var priority = _visibleTiles is { } v && batch.Overlaps(v.C0, v.R0, v.C1, v.R1)
                ? WorkPriority.Visible
                : WorkPriority.Normal;
            RenderTiles(batch, pxPerPoint, pixelScale, inverted, maxW, maxH, priority);
        }
        if (inView) PrunePreviousTiles(visible, pixelScale);
        OnTilesChanged();
        OnRedrawn();
    }

    private bool BaseIsCurrent(double scale) =>
        _base.Source is not null && _baseVersion == _version && Math.Abs(scale - _shownBaseScale) / scale <= 0.02;

    /// <summary>Makes the current tiles previous ones, for a new zoom or for the untiled bitmap to replace.</summary>
    private void RetireTiles()
    {
        CancelPendingTiles();
        if (_tileImages.Count == 0) return;
        if (_staleTiles.Count > 0) _previousStale = true;
        // Tiles are added to the canvas as they arrive, so these are over the earlier previous ones and under the new ones.
        double pixelScale = _tileScale / LayoutScale;
        foreach (var image in _tileImages.Values) _previousTiles.Add((image, image.Width * image.Height * pixelScale * pixelScale));
        _tileImages.Clear();
        _staleTiles.Clear();
    }

    /// <summary>
    /// Drops the previous tiles out of view, then the oldest ones while they add up to more than a couple of
    /// screens: zooming on and on before tiles arrive would pile them up.
    /// </summary>
    private void PrunePreviousTiles(Rect visible, double pixelScale)
    {
        for (int i = _previousTiles.Count - 1; i >= 0; i--)
        {
            var image = _previousTiles[i].Image;
            double x = GetLeft(image), y = GetTop(image);
            if (x < visible.Right && x + image.Width > visible.Left && y < visible.Bottom && y + image.Height > visible.Top) continue;
            RemovePreviousTile(i);
        }
        double budget = Math.Max(MaxFullPagePixels, 2 * visible.Width * visible.Height * pixelScale * pixelScale);
        double total = _previousTiles.Sum(t => t.Pixels);
        while (_previousTiles.Count > 0 && total > budget)
        {
            total -= _previousTiles[0].Pixels;
            RemovePreviousTile(0);
        }
    }

    private void RemovePreviousTile(int index)
    {
        var image = _previousTiles[index].Image;
        _previousTiles.RemoveAt(index);
        _tiles.Children.Remove(image);
        Free(image);
    }

    private void ClearPreviousTiles()
    {
        for (int i = _previousTiles.Count - 1; i >= 0; i--) RemovePreviousTile(i);
        _previousStale = false;
    }

    /// <summary>Whether tiles at the current zoom, of this version of the page or an older one, cover the view.</summary>
    private bool TilesCoverView()
    {
        if (_visibleTiles is not { } v) return true;
        for (int r = v.R0; r <= v.R1; r++)
            for (int c = v.C0; c <= v.C1; c++)
                if (!_tileImages.ContainsKey((c, r))) return false;
        return true;
    }

    /// <summary>Once the tiles cover the view, takes down what stood in for them.</summary>
    private void OnTilesChanged()
    {
        if (!TilesCoverView()) return;
        ClearPreviousTiles();
        if (_heldBase is { } held)
        {
            _heldBase = null;
            if (held.Version == _version) ShowBase(held.Source, held.Width, held.Height, held.Version, held.Scale);
            else held.Source.Dispose();
        }
    }

    private void DiscardHeldBase()
    {
        _heldBase?.Source.Dispose();
        _heldBase = null;
    }

    private bool IsMissing(int c, int r) => (!_tileImages.ContainsKey((c, r)) || _staleTiles.Contains((c, r))) && !_pendingTiles.ContainsKey((c, r));

    private void DropTile((int Col, int Row) key)
    {
        _staleTiles.Remove(key);
        if (!_tileImages.Remove(key, out var image)) return;
        _tiles.Children.Remove(image);
        Free(image);
    }

    /// <summary>
    /// Covers the tiles in [c0, c1] x [r0, r1] that are neither shown nor pending with few
    /// rectangles: runs of missing tiles in a row, extended down while the rows below have the same run.
    /// </summary>
    private List<(int C0, int R0, int C1, int R1)> MissingTileRects(int c0, int r0, int c1, int r1)
    {
        var done = new List<(int, int, int, int)>();
        // Runs still growing downwards: (first column, last column) -> top row.
        var open = new Dictionary<(int C0, int C1), int>();
        for (int r = r0; r <= r1 + 1; r++)
        {
            var next = new Dictionary<(int C0, int C1), int>();
            for (int c = c0; r <= r1 && c <= c1; c++)
            {
                if (!IsMissing(c, r)) continue;
                int start = c;
                while (c < c1 && IsMissing(c + 1, r)) c++;
                var run = (start, c);
                if (open.Remove(run, out int top))
                {
                    if ((r - top + 1) * (c - start + 1) <= MaxTilesPerBatch)
                    {
                        next[run] = top;
                        continue;
                    }
                    done.Add((start, top, c, r - 1));
                }
                next[run] = r;
            }
            foreach (var ((oc0, oc1), top) in open) done.Add((oc0, top, oc1, r - 1));
            open = next;
        }
        return done;
    }

    private async void RenderBase(double scale, bool inverted, bool isVisible)
    {
        _baseCts?.Cancel();
        DiscardHeldBase();
        var cts = _baseCts = new CancellationTokenSource();
        _baseScale = scale;
        _renderedInverted = inverted;
        _renderedRotation = PageRotation;
        int version = _version;
        var size = RotatedPoints();
        var region = new RectInt32(0, 0, Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));
        try
        {
            using var buffer = await _document.RenderAsync(PageIndex, scale, PageRotation, region, inverted,
                isVisible ? WorkPriority.Visible : WorkPriority.Normal, cts.Token, without: _leftOut);
            if (cts.IsCancellationRequested || version != _version) return;
            if (buffer is null)
            {
                if (_baseCts == cts && !_tiled) ClearTiles();
                return;
            }
            Rendered?.Invoke(this, EventArgs.Empty);
            var source = await ToSourceAsync(buffer);
            if (cts.IsCancellationRequested || version != _version)
            {
                source.Dispose();
                return;
            }
            // Size the image to its exact pixel dimensions so it maps 1:1 onto the screen.
            double width = buffer.Width * LayoutScale / scale, height = buffer.Height * LayoutScale / scale;
            // Zooming in past where the page gets tiled draws it at a much lower resolution, which would show
            // over the view until the tiles arrive, so the sharper bitmap stays up until then.
            if (_tiled && _base.Source is not null && _baseVersion == version && scale * 1.02 < _shownBaseScale && !TilesCoverView())
            {
                _heldBase = (source, width, height, version, scale);
                return;
            }
            ShowBase(source, width, height, version, scale);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidDataException)
        {
            if (_baseCts == cts && !_tiled) ClearTiles();
        }
        catch (COMException)
        {
            // The upload failed; the page keeps the bitmap it had, and the next change of scale tries again. Without
            // one it would stay blank until then, so the next Render (on scrolling) tries again at this scale. So it
            // does with retired tiles or drawing that stays over the page until the page shows it.
            if (_baseCts == cts && (_base.Source is null || _previousTiles.Count > 0 || _strokes.Count > 0 || _whenRedrawn.Count > 0)) _baseScale = 0;
        }
    }

    private void ShowBase(SoftwareBitmapSource source, double width, double height, int version, double scale)
    {
        _base.Width = width;
        _base.Height = height;
        var old = _base.Source;
        _base.Source = source;
        (old as IDisposable)?.Dispose();
        _baseVersion = version;
        _shownBaseScale = scale;
        // Tiles on an untiled page were only left up until this arrived.
        if (!_tiled) ClearTiles();
        OnRedrawn();
    }

    private async void RenderTiles(TileBatch batch, double pxPerPoint, double pixelScale, bool inverted, int maxW, int maxH, WorkPriority priority)
    {
        int version = _version;
        int x0 = batch.C0 * TileSize, y0 = batch.R0 * TileSize;
        var region = new RectInt32(x0, y0, Math.Min((batch.C1 + 1) * TileSize, maxW) - x0, Math.Min((batch.R1 + 1) * TileSize, maxH) - y0);
        try
        {
            using var buffer = await _document.RenderAsync(PageIndex, pxPerPoint, PageRotation, region, inverted, priority, batch.Cts.Token, without: _leftOut);
            bool IsStale() => batch.Cts.IsCancellationRequested || version != _version || Math.Abs(_tileScale - pxPerPoint) > 1e-9;
            if (buffer is null || IsStale())
            {
                ForgetBatch(batch);
                return;
            }
            // Cut the pass into tiles, which are what's kept and dropped as the view moves. The batch stays
            // pending while they upload, so Render still cancels it if the tiles go out of view or the page
            // stops being tiled, and doesn't start rendering the same tiles again meanwhile.
            var tiles = batch.Keys.Select(key => (Key: key, Rect: new RectInt32(key.Col * TileSize, key.Row * TileSize,
                Math.Min(TileSize, maxW - key.Col * TileSize), Math.Min(TileSize, maxH - key.Row * TileSize)))).ToList();
            var visibleGroup = new List<((int Col, int Row) Key, RectInt32 Rect)>();
            var surroundingGroup = new List<((int Col, int Row) Key, RectInt32 Rect)>();
            foreach (var tile in tiles)
            {
                if (_visibleTiles is { } v && tile.Key.Col >= v.C0 && tile.Key.Col <= v.C1 && tile.Key.Row >= v.R0 && tile.Key.Row <= v.R1)
                    visibleGroup.Add(tile);
                else
                    surroundingGroup.Add(tile);
            }
            bool CommitGroup(List<((int Col, int Row) Key, RectInt32 Rect)> group, Image[] images)
            {
                if (IsStale())
                {
                    foreach (var image in images) Free(image);
                    return false;
                }
                for (int i = 0; i < group.Count; i++)
                {
                    var (key, image) = (group[i].Key, images[i]);
                    if (_pendingTiles.TryGetValue(key, out var current) && current == batch) _pendingTiles.Remove(key);
                    DropTile(key);
                    _tileImages[key] = image;
                    _tiles.Children.Add(image);
                }
                OnTilesChanged();
                OnRedrawn();
                return true;
            }
            if (visibleGroup.Count > 0)
            {
                var images = await CutAsync(buffer, region, visibleGroup.Select(t => t.Rect).ToList(), pixelScale);
                if (!CommitGroup(visibleGroup, images))
                {
                    ForgetBatch(batch);
                    return;
                }
            }
            if (surroundingGroup.Count > 0)
            {
                if (visibleGroup.Count > 0)
                {
                    using var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(batch.Cts.Token);
                    frameTimeout.CancelAfter(60);
                    await WaitForFramesAsync(2, frameTimeout.Token);
                }
                if (IsStale())
                {
                    ForgetBatch(batch);
                    return;
                }
                // Upload off-screen surrounding tiles one at a time after the retired previous-zoom surfaces
                // have been released, so WinUI reuses its staging pool instead of expanding it.
                var uploaded = new List<Image>(surroundingGroup.Count);
                try
                {
                    foreach (var tile in surroundingGroup)
                    {
                        if (IsStale())
                        {
                            foreach (var image in uploaded) Free(image);
                            ForgetBatch(batch);
                            return;
                        }
                        var p = tile.Rect;
                        var source = await ToSourceAsync(buffer, p.X - region.X, p.Y - region.Y, p.Width, p.Height);
                        var img = new Image { Source = source, Stretch = Stretch.Fill, Width = p.Width / pixelScale, Height = p.Height / pixelScale };
                        SetLeft(img, p.X / pixelScale);
                        SetTop(img, p.Y / pixelScale);
                        uploaded.Add(img);
                    }
                }
                catch
                {
                    foreach (var image in uploaded) Free(image);
                    throw;
                }
                ForgetBatch(batch);
                if (!CommitGroup(surroundingGroup, uploaded.ToArray())) return;
            }
            else
            {
                ForgetBatch(batch);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidDataException)
        {
        }
        catch (COMException)
        {
            // The upload failed. The tiles stay pending, so they're tried again once they've scrolled out of view and back.
        }
    }

    /// <summary>Waits for <paramref name="count"/> composition frames so surfaces freed by <see cref="ClearPreviousTiles"/> are retired.</summary>
    private Task WaitForFramesAsync(int count, CancellationToken token)
    {
        if (count <= 0 || token.IsCancellationRequested) return Task.CompletedTask;
        var tcs = new TaskCompletionSource();
        var dispatcher = DispatcherQueue;
        int remaining = count;
        bool done = false;
        EventHandler<object>? onFrame = null;
        CancellationTokenRegistration reg = default;
        void Finish()
        {
            if (done) return;
            done = true;
            CompositionTarget.Rendering -= onFrame;
            reg.Dispose();
            tcs.TrySetResult();
        }
        onFrame = (_, _) =>
        {
            if (--remaining <= 0) Finish();
        };
        CompositionTarget.Rendering += onFrame;
        reg = token.Register(() =>
        {
            if (dispatcher is not null && !dispatcher.HasThreadAccess)
            {
                if (!dispatcher.TryEnqueue(Finish)) tcs.TrySetResult();
            }
            else Finish();
        });
        if (token.IsCancellationRequested) Finish();
        return tcs.Task;
    }

    /// <summary>Takes <paramref name="batch"/>'s tiles off the pending list, unless a newer batch has them.</summary>
    private void ForgetBatch(TileBatch batch)
    {
        foreach (var key in batch.Keys)
        {
            if (_pendingTiles.TryGetValue(key, out var current) && current == batch) _pendingTiles.Remove(key);
        }
    }

    /// <summary>Offsets the page by a sub-pixel amount so its bitmaps land on whole device pixels.</summary>
    public void SnapToPixels(Point devicePosition, double pixelScale)
    {
        double fx = devicePosition.X - Math.Round(devicePosition.X);
        double fy = devicePosition.Y - Math.Round(devicePosition.Y);
        if (RenderTransform is not TranslateTransform t) RenderTransform = t = new TranslateTransform();
        t.X = -fx / pixelScale;
        t.Y = -fy / pixelScale;
    }

    private Size RotatedPoints() => PageRotation is 90 or 270 ? new Size(PagePoints.Height, PagePoints.Width) : PagePoints;

    private void ClearTiles()
    {
        CancelPendingTiles();
        _tileImages.Clear();
        _staleTiles.Clear();
        _previousTiles.Clear();
        _previousStale = false;
        Clear(_tiles);
    }

    public void Release()
    {
        _baseCts?.Cancel();
        DiscardHeldBase();
        ClearTiles();
        Free(_base);
        foreach (var (action, _) in _whenRedrawn) action();
        _whenRedrawn.Clear();
    }

    internal static WriteableBitmap ToBitmap(PixelBuffer buffer)
    {
        var bitmap = new WriteableBitmap(buffer.Width, buffer.Height);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(buffer.Span);
        bitmap.Invalidate();
        return bitmap;
    }

    internal static Task<SoftwareBitmapSource> ToSourceAsync(PixelBuffer buffer) => ToSourceAsync(buffer, 0, 0, buffer.Width, buffer.Height);

    /// <summary>
    /// Copies the <paramref name="width"/> x <paramref name="height"/> pixels at (<paramref name="x"/>, <paramref name="y"/>)
    /// into a bitmap for an <see cref="Image"/>. Disposing it releases the pixels, in system memory and on the
    /// GPU, right away rather than whenever the garbage collector gets to its wrapper, which with a small
    /// managed heap can take a long time.
    /// <paramref name="buffer"/> has to stay alive until this completes. Call it on the UI thread.
    /// </summary>
    internal static async Task<SoftwareBitmapSource> ToSourceAsync(PixelBuffer buffer, int x, int y, int width, int height)
    {
        // Filling a new bitmap touches fresh memory, which took up to several milliseconds a page on
        // the UI thread; a background thread does that, and only the upload starts here.
        var bitmap = await Task.Run(() =>
        {
            var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            try
            {
                CopyPixels(buffer, x, y, bitmap);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        });
        var source = new SoftwareBitmapSource();
        try
        {
            // The source keeps the bitmap and copies it to the GPU again whenever it loses its copy there,
            // such as after the image leaves the tree or the device is lost, so the bitmap stays open until
            // the source is disposed, which closes it. Closing it before then crashes the next copy.
            await source.SetBitmapAsync(bitmap);
            return source;
        }
        catch
        {
            source.Dispose();
            bitmap.Dispose();
            throw;
        }
    }

    private static unsafe void CopyPixels(PixelBuffer buffer, int x, int y, SoftwareBitmap bitmap)
    {
        // The copy below reads raw memory, so a rectangle outside the buffer would read past its allocation.
        if (x < 0 || y < 0 || (long)x + bitmap.PixelWidth > buffer.Width || (long)y + bitmap.PixelHeight > buffer.Height)
            throw new ArgumentOutOfRangeException(nameof(x), "The rectangle isn't inside the buffer.");
        using var locked = bitmap.LockBuffer(BitmapBufferAccessMode.Write);
        var plane = locked.GetPlaneDescription(0);
        using var reference = locked.CreateReference();
        // IMemoryBufferByteAccess, called through its vtable: built-in COM interop is off in trimmed apps.
        var iid = new Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(((IWinRTObject)reference).NativeObject.ThisPtr, in iid, out IntPtr access));
        try
        {
            byte* target;
            uint capacity;
            var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)(*(void***)access)[3];
            Marshal.ThrowExceptionForHR(getBuffer(access, &target, &capacity));
            int rowBytes = bitmap.PixelWidth * 4;
            if ((long)plane.StartIndex + (long)plane.Stride * (bitmap.PixelHeight - 1) + rowBytes > capacity) throw new InvalidOperationException("The bitmap buffer is too small.");
            byte* from = buffer.Pointer + (long)y * buffer.Stride + x * 4;
            for (int row = 0; row < bitmap.PixelHeight; row++)
                Buffer.MemoryCopy(from + (long)row * buffer.Stride, target + plane.StartIndex + (long)row * plane.Stride, rowBytes, rowBytes);
        }
        finally
        {
            Marshal.Release(access);
        }
    }

    /// <summary>
    /// Cuts <paramref name="pieces"/> out of a render of <paramref name="region"/> (both in pixels) into images
    /// placed at <paramref name="pixelScale"/> pixels per layout DIP.
    /// </summary>
    private static async Task<Image[]> CutAsync(PixelBuffer buffer, RectInt32 region, IReadOnlyList<RectInt32> pieces, double pixelScale)
    {
        var uploads = pieces.Select(p => ToSourceAsync(buffer, p.X - region.X, p.Y - region.Y, p.Width, p.Height)).ToArray();
        try
        {
            await Task.WhenAll(uploads);
        }
        catch
        {
            foreach (var upload in uploads)
            {
                if (upload.IsCompletedSuccessfully) upload.Result.Dispose();
            }
            throw;
        }
        var images = new Image[pieces.Count];
        for (int i = 0; i < images.Length; i++)
        {
            var p = pieces[i];
            images[i] = new Image { Source = uploads[i].Result, Stretch = Stretch.Fill, Width = p.Width / pixelScale, Height = p.Height / pixelScale };
            SetLeft(images[i], p.X / pixelScale);
            SetTop(images[i], p.Y / pixelScale);
        }
        return images;
    }

    /// <summary>Takes the bitmap off <paramref name="image"/> and releases it.</summary>
    private static void Free(Image image)
    {
        var source = image.Source;
        image.Source = null;
        (source as IDisposable)?.Dispose();
    }

    /// <summary>Removes the images in <paramref name="canvas"/> and releases their bitmaps.</summary>
    private static void Clear(Canvas canvas)
    {
        foreach (var image in canvas.Children.OfType<Image>()) Free(image);
        canvas.Children.Clear();
    }

    // ---------------------------------------------------------------- overlay

    /// <summary>
    /// Shows highlight rectangles (display points). Each brush becomes one Path whose geometry is
    /// parsed from path markup in a single call, instead of a Rectangle element per highlight:
    /// pages with hundreds of search hits used to take tens of milliseconds to update.
    /// </summary>
    public void SetOverlay(OverlayLayer layer, IEnumerable<(Rect Rect, Brush Fill)> shapes)
    {
        var paths = new Dictionary<Brush, StringBuilder>();
        foreach (var (rect, fill) in shapes)
        {
            var view = ToView(rect);
            if (view.IsEmpty || view.Width <= 0 || view.Height <= 0) continue;
            // F1: nonzero fill, so overlapping rectangles don't cut holes in each other.
            if (!paths.TryGetValue(fill, out var markup)) paths[fill] = markup = new StringBuilder("F1");
            AppendRoundedRect(markup, view, 1.5);
        }
        var canvas = _overlayLayers[(int)layer];
        canvas.Children.Clear();
        foreach (var (fill, markup) in paths)
        {
            var geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), markup.ToString());
            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Fill = fill });
        }
    }

    // Strokes being drawn, drawings and text boxes being moved, and ones already in the document until the page has been drawn again
    // with them on it. The version is the last one of the page they aren't in, which only a finished stroke has.
    private readonly List<(Microsoft.UI.Xaml.Shapes.Shape Line, int? Version)> _strokes = [];
    private readonly List<(Action Action, int Version)> _whenRedrawn = [];

    /// <summary>Starts drawing a stroke over the page. <paramref name="width"/> is in points, its points in page-local layout DIPs.</summary>
    public Microsoft.UI.Xaml.Shapes.Polyline AddStroke(Windows.UI.Color color, double width, double opacity) =>
        AddLine(new Microsoft.UI.Xaml.Shapes.Polyline(), color, width, opacity);

    /// <summary>
    /// Draws a whole drawing over the page in <paramref name="color"/>, as what the document writes looks: curved, and as
    /// one line, so its strokes don't show through each other where a highlighter's cross.
    /// </summary>
    public Microsoft.UI.Xaml.Shapes.Path AddDrawing(InkDrawing ink, Windows.UI.Color color)
    {
        var markup = new StringBuilder();
        void Add(string command, Point p) => markup.Append(CultureInfo.InvariantCulture, $" {command}{p.X:0.###},{p.Y:0.###}");
        foreach (var stroke in ink.Strokes)
        {
            Point[] points = [.. stroke.Select(ToView)];
            Add("M", points[0]);
            for (int i = 1; i < points.Length - 1; i++)
            {
                Add("Q", points[i]);
                Add("", new Point((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2));
            }
            // A line needs two points, or a dot wouldn't show.
            Add("L", points.Length > 1 ? points[^1] : new Point(points[0].X + 0.01, points[0].Y));
        }
        var data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), markup.ToString());
        return AddLine(new Microsoft.UI.Xaml.Shapes.Path { Data = data }, color, ink.Width, ink.Opacity / 255.0);
    }

    /// <summary>Draws the outlines of a text box over the page, from <see cref="PdfDocument.TextBoxMarkup"/>.</summary>
    public Microsoft.UI.Xaml.Shapes.Path AddText(string markup, Windows.UI.Color color)
    {
        var text = new Microsoft.UI.Xaml.Shapes.Path { Fill = new SolidColorBrush(color) };
        SetText(text, markup);
        return AddShape(text);
    }

    /// <summary>Changes what <see cref="AddText"/> drew to other outlines of a text box.</summary>
    public static void SetText(Microsoft.UI.Xaml.Shapes.Path text, string markup) =>
        text.Data = markup.Length > 0 ? (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), markup) : null;

    private T AddLine<T>(T line, Windows.UI.Color color, double width, double opacity) where T : Microsoft.UI.Xaml.Shapes.Shape
    {
        line.Stroke = new SolidColorBrush(color);
        line.StrokeThickness = width * LayoutScale;
        line.Opacity = opacity;
        line.StrokeLineJoin = PenLineJoin.Round;
        line.StrokeStartLineCap = PenLineCap.Round;
        line.StrokeEndLineCap = PenLineCap.Round;
        return AddShape(line);
    }

    private T AddShape<T>(T shape) where T : Microsoft.UI.Xaml.Shapes.Shape
    {
        _overlayLayers[(int)OverlayLayer.Strokes].Children.Add(shape);
        _strokes.Add((shape, null));
        return shape;
    }

    /// <summary>Puts a line <paramref name="distance"/> away from where it was drawn, in display points.</summary>
    public void OffsetStroke(Microsoft.UI.Xaml.Shapes.Shape line, Point distance)
    {
        Point from = ToView(default(Point)), to = ToView(distance);
        SetLeft(line, to.X - from.X);
        SetTop(line, to.Y - from.Y);
    }

    public static void ExtendStroke(Microsoft.UI.Xaml.Shapes.Polyline stroke, Point view)
    {
        stroke.Points.Add(view);
        // A line needs two points, or a click would leave no dot.
        if (stroke.Points.Count == 1) stroke.Points.Add(new Point(view.X + 0.01, view.Y));
    }

    /// <summary>The stroke is in the document: it goes once the page has been drawn with it, which starts after this.</summary>
    public void FinishStroke(Microsoft.UI.Xaml.Shapes.Shape line)
    {
        int i = _strokes.FindIndex(s => s.Line == line);
        if (i >= 0) _strokes[i] = (line, _version);
    }

    public void RemoveStroke(Microsoft.UI.Xaml.Shapes.Shape line)
    {
        _strokes.RemoveAll(s => s.Line == line);
        _overlayLayers[(int)OverlayLayer.Strokes].Children.Remove(line);
    }

    /// <summary>Runs <paramref name="action"/> once the page has been drawn again after the next <see cref="Invalidate"/>.</summary>
    public void CallWhenRedrawn(Action action) => _whenRedrawn.Add((action, _version));

    /// <summary>Takes down what stood in for a change once the page shows it: on the bitmap, with no older tiles over it.</summary>
    private void OnRedrawn()
    {
        if (_staleTiles.Count > 0 || (_previousStale && _previousTiles.Count > 0)) return;
        foreach (var (line, _) in _strokes.Where(s => s.Version < _baseVersion).ToList()) RemoveStroke(line);
        foreach (var call in _whenRedrawn.Where(c => c.Version < _baseVersion).ToList())
        {
            _whenRedrawn.Remove(call);
            call.Action();
        }
    }

    private static void AppendRoundedRect(StringBuilder sb, Rect r, double radius)
    {
        double k = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        double l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
        sb.Append(CultureInfo.InvariantCulture,
            $" M{l + k:0.###},{t:0.###} H{rt - k:0.###} A{k:0.###},{k:0.###} 0 0 1 {rt:0.###},{t + k:0.###} V{b - k:0.###} A{k:0.###},{k:0.###} 0 0 1 {rt - k:0.###},{b:0.###} H{l + k:0.###} A{k:0.###},{k:0.###} 0 0 1 {l:0.###},{b - k:0.###} V{t + k:0.###} A{k:0.###},{k:0.###} 0 0 1 {l + k:0.###},{t:0.###} Z");
    }
}
