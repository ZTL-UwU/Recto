using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Recto.Controls;

/// <summary>
/// The frame around a selected drawing or text box: the accent line and light inner line of a selected GridView item,
/// with a Slider's thumb for a grip. It's sized in screen pixels on a canvas that zooms with the pages.
/// Its colors follow the page rather than the app's theme: paper is white in a dark window too, and on it WinUI's
/// dark theme accent, a pale tint, would hardly show. Its lines fall on whole device pixels, or a 1 px line would be two faint ones.
/// </summary>
internal sealed class SelectionFrame
{
    /// <summary>How far the frame stands off what it's around, as a focus ring does.</summary>
    public const double Gap = 3;

    private const int FrameZIndex = 1000;

    private readonly Rectangle _outer = CreatePart(), _inner = CreatePart(), _grip = CreatePart(), _gripDot = CreatePart();
    private readonly Color _accentOnLight, _accentOnDark;
    private readonly SolidColorBrush _accent = new(), _surface = new(), _edge = new();

    public SelectionFrame(Color accent)
    {
        _accentOnLight = Shade("SystemAccentColorDark1", accent);
        _accentOnDark = Shade("SystemAccentColorLight2", accent);
        _outer.Stroke = _gripDot.Fill = _accent;
        _inner.Stroke = _grip.Fill = _surface;
        _grip.Stroke = _edge;
        SetPage(dark: false);
    }

    private static Color Shade(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var shade) && shade is Color color ? color : fallback;

    /// <summary>Takes WinUI's light or dark colors, whichever suit the page.</summary>
    public void SetPage(bool dark)
    {
        _accent.Color = dark ? _accentOnDark : _accentOnLight;
        _surface.Color = dark ? Color.FromArgb(0xFF, 0x45, 0x45, 0x45) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        _edge.Color = dark ? Color.FromArgb(0x8B, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x72, 0, 0, 0);
    }

    /// <summary>Shows the frame around an area of the canvas, with a grip at a point if there's one.</summary>
    /// <param name="zoom">How much the canvas is scaled.</param>
    /// <param name="raster">Device pixels to a screen pixel.</param>
    public void Show(Panel host, Rect area, Point? grip, double zoom, double raster)
    {
        if (_outer.Parent is null)
        {
            foreach (var part in (Rectangle[])[_outer, _inner])
            {
                Canvas.SetZIndex(part, FrameZIndex);
                host.Children.Add(part);
            }
            foreach (var part in (Rectangle[])[_grip, _gripDot])
            {
                Canvas.SetZIndex(part, FrameZIndex + 1);
                host.Children.Add(part);
            }
        }
        double device = zoom * raster;
        // In whole device pixels.
        double Line(double thickness) => Math.Max(1, Math.Round(thickness * raster)) / device;

        var box = Snap(Inset(area, -Gap / zoom), device);
        PlaceRing(_outer, box, Line(2), 7 / zoom);
        PlaceRing(_inner, Inset(box, Line(2)), Line(1), 5 / zoom);
        _grip.Visibility = _gripDot.Visibility = Visibility.Collapsed;
        if (grip is not { } at) return;
        _grip.StrokeThickness = Line(1);
        PlaceDisc(_grip, at, 16 / zoom, device);
        PlaceDisc(_gripDot, at, 8 / zoom, device);
    }

    public void Hide() => _outer.Visibility = _inner.Visibility = _grip.Visibility = _gripDot.Visibility = Visibility.Collapsed;

    private static void PlaceRing(Rectangle ring, Rect box, double thickness, double radius)
    {
        ring.StrokeThickness = thickness;
        ring.RadiusX = ring.RadiusY = radius;
        Place(ring, box);
    }

    private static void PlaceDisc(Rectangle disc, Point center, double size, double device)
    {
        disc.RadiusX = disc.RadiusY = size / 2;
        Place(disc, Snap(new Rect(center.X - size / 2, center.Y - size / 2, size, size), device));
    }

    private static void Place(Rectangle part, Rect box)
    {
        part.Width = box.Width;
        part.Height = box.Height;
        Canvas.SetLeft(part, box.Left);
        Canvas.SetTop(part, box.Top);
        part.Visibility = Visibility.Visible;
    }

    private static Rect Snap(Rect box, double device) => new(
        Math.Round(box.Left * device) / device,
        Math.Round(box.Top * device) / device,
        Math.Round(box.Width * device) / device,
        Math.Round(box.Height * device) / device);

    private static Rect Inset(Rect box, double by) =>
        new(box.Left + by, box.Top + by, Math.Max(0, box.Width - 2 * by), Math.Max(0, box.Height - 2 * by));

    private static Rectangle CreatePart() => new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
}
