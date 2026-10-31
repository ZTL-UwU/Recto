using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Recto.Controls;
using Recto.Services;
using Windows.UI;

namespace Recto;

// The bar of drawing and text tools over the document.
public sealed partial class MainWindow
{
    private static readonly (string Name, int Rgb)[] InkColors =
    [
        ("Black", 0x000000),
        ("Red", 0xD92D20),
        ("Orange", 0xF27A1A),
        ("Green", 0x1E8E3E),
        ("Blue", 0x1A66D9),
        ("Purple", 0x8E3FD1),
    ];

    /// <summary>The same as highlights of text.</summary>
    private static readonly (string Name, int Rgb)[] HighlighterColors =
    [
        ("Yellow", 0xFFDD33),
        ("Green", 0x7DD673),
        ("Blue", 0x6EB4FF),
        ("Pink", 0xFF80BE),
        ("Purple", 0xBE8CFF),
    ];

    private static readonly double[] PenWidths = [1, 2, 3, 5, 8];
    private static readonly double[] HighlighterWidths = [8, 14, 20, 28];
    private static readonly double[] TextSizes = [8, 9, 10, 11, 12, 14, 16, 18, 24, 36, 48];

    /// <summary>The tool whose color and size the bar shows: the one in use, or the last that had any.</summary>
    private AnnotationTool _styledTool = AnnotationTool.Pen;
    /// <summary>The tool the color swatches were last made for.</summary>
    private AnnotationTool? _swatchesTool;

    private void SetupAnnotateBar()
    {
        Viewer.ToolChanged += (_, _) => UpdateAnnotateBar();
        Viewer.SelectedDrawingChanged += (_, _) => UpdateAnnotateBar();
        // The menu takes the focus, which would close a text box being typed before a size could be picked for it.
        AnnotateSizeMenu.Opening += (_, _) => Viewer.HoldTextEdit(true);
        AnnotateSizeMenu.Closed += (_, _) => Viewer.HoldTextEdit(false);
    }

    private static Color ToColor(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private static int ToRgb(Color color) => color.R << 16 | color.G << 8 | color.B;

    private static (int Rgb, double Size) StyleOf(AnnotationTool tool)
    {
        var prefs = AppState.Preferences.Annotate;
        return tool switch
        {
            AnnotationTool.Highlighter => (prefs.HighlighterColor, Math.Clamp(prefs.HighlighterWidth, 1, 96)),
            AnnotationTool.Text => (prefs.TextColor, Math.Clamp(prefs.TextSize, 4, 144)),
            _ => (prefs.PenColor, Math.Clamp(prefs.PenWidth, 0.25, 96)),
        };
    }

    private void SetAnnotateTool(AnnotationTool tool)
    {
        if (tool is not (AnnotationTool.None or AnnotationTool.Eraser)) _styledTool = tool;
        var (rgb, size) = StyleOf(tool);
        Viewer.SetTool(tool, ToColor(rgb), size);
        UpdateAnnotateBar();
    }

    private void SetAnnotateOpen(bool open)
    {
        bool isOpen = AnnotateBar.Visibility == Visibility.Visible;
        AnnotateToggle.IsChecked = open;
        if (open == isOpen) return;
        AnnotateBar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SetAnnotateTool(open ? _styledTool : AnnotationTool.None);
    }

    private void OnToggleAnnotate(object sender, RoutedEventArgs e) => SetAnnotateOpen(AnnotateToggle.IsChecked == true);

    private void OnAnnotateToolClick(object sender, RoutedEventArgs e)
    {
        var button = (ToggleButton)sender;
        var tool = button == PenTool ? AnnotationTool.Pen : button == HighlighterTool ? AnnotationTool.Highlighter : button == EraserTool ? AnnotationTool.Eraser : AnnotationTool.Text;
        // Off again leaves no tool, for selecting text and moving what's been drawn.
        SetAnnotateTool(Viewer.Tool == tool ? AnnotationTool.None : tool);
    }

    private void OnAnnotateUndo(object sender, RoutedEventArgs e) => Viewer.Undo();
    private void OnAnnotateRedo(object sender, RoutedEventArgs e) => Viewer.Redo();

    private void UpdateAnnotateBar()
    {
        var tool = Viewer.Tool;
        // The bar shows the selected drawing's style, and what's picked on it changes that.
        if (Viewer.SelectedDrawing is { } drawing)
            _styledTool = drawing.FreeText is not null ? AnnotationTool.Text : drawing.Ink is { Opacity: < 255 } ? AnnotationTool.Highlighter : AnnotationTool.Pen;
        else if (tool is not (AnnotationTool.None or AnnotationTool.Eraser)) _styledTool = tool;
        PenTool.IsChecked = tool == AnnotationTool.Pen;
        HighlighterTool.IsChecked = tool == AnnotationTool.Highlighter;
        EraserTool.IsChecked = tool == AnnotationTool.Eraser;
        TextTool.IsChecked = tool == AnnotationTool.Text;

        bool styled = tool != AnnotationTool.Eraser;
        var visibility = styled ? Visibility.Visible : Visibility.Collapsed;
        AnnotateStyleSeparator.Visibility = AnnotateColors.Visibility = AnnotateSize.Visibility = visibility;
        if (!styled) return;

        var (rgb, size) = StyleOf(_styledTool);
        if (Viewer.SelectedDrawing is { } selected)
        {
            rgb = ToRgb(selected.Color);
            size = selected.FreeText?.FontSize ?? selected.Ink?.Width ?? size;
        }
        if (_swatchesTool != _styledTool) BuildColors();
        _swatchesTool = _styledTool;
        foreach (var swatch in AnnotateColors.Children.OfType<ToggleButton>()) swatch.IsChecked = (int)swatch.Tag == rgb;
        AnnotateSize.Content = $"{size:0.##} pt";
        BuildSizes(size);
    }

    private void BuildColors()
    {
        AnnotateColors.Children.Clear();
        foreach (var (name, rgb) in _styledTool == AnnotationTool.Highlighter ? HighlighterColors : InkColors)
        {
            var swatch = new ToggleButton
            {
                Style = (Style)Application.Current.Resources["ToolbarToggleStyle"],
                AllowFocusOnInteraction = false,
                Tag = rgb,
                Content = new Ellipse
                {
                    Width = 16,
                    Height = 16,
                    Fill = new SolidColorBrush(ToColor(rgb)),
                    // Keeps black and white apart from the bar in either theme.
                    Stroke = new SolidColorBrush(Color.FromArgb(0x66, 128, 128, 128)),
                    StrokeThickness = 1,
                },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, name);
            ToolTipService.SetToolTip(swatch, name);
            swatch.Click += (_, _) => PickColor(rgb);
            AnnotateColors.Children.Add(swatch);
        }
    }

    private void BuildSizes(double current)
    {
        AnnotateSizeMenu.Items.Clear();
        foreach (double size in _styledTool switch
        {
            AnnotationTool.Text => TextSizes,
            AnnotationTool.Highlighter => HighlighterWidths,
            _ => PenWidths,
        })
        {
            var item = new RadioMenuFlyoutItem { Text = $"{size:0.##} pt", GroupName = "annotate-size", IsChecked = size == current };
            item.Click += (_, _) => PickSize(size);
            AnnotateSizeMenu.Items.Add(item);
        }
    }

    private void PickColor(int rgb)
    {
        var prefs = AppState.Preferences.Annotate;
        if (_styledTool == AnnotationTool.Highlighter) prefs.HighlighterColor = rgb;
        else if (_styledTool == AnnotationTool.Text) prefs.TextColor = rgb;
        else prefs.PenColor = rgb;
        AppState.Save();
        Viewer.ApplyStyle(ToColor(rgb), null);
        UpdateStyle();
    }

    private void PickSize(double size)
    {
        var prefs = AppState.Preferences.Annotate;
        if (_styledTool == AnnotationTool.Highlighter) prefs.HighlighterWidth = size;
        else if (_styledTool == AnnotationTool.Text) prefs.TextSize = size;
        else prefs.PenWidth = size;
        AppState.Save();
        Viewer.ApplyStyle(null, size);
        UpdateStyle();
    }

    /// <summary>Gives the tool in use the style just picked.</summary>
    private void UpdateStyle()
    {
        var (rgb, size) = StyleOf(Viewer.Tool);
        Viewer.SetTool(Viewer.Tool, ToColor(rgb), size);
        UpdateAnnotateBar();
    }
}
