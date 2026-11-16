using System.ComponentModel;
using Recto.Controls;
using Recto.Pdf;
using Recto.Printing;
using Recto.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Foundation;
using Windows.Graphics;

namespace Recto;

/// <summary>
/// Recto's own print dialog: a live preview of the sheets that will come out of the printer, next
/// to the settings that matter for documents. Jobs go straight to the printer driver through GDI
/// (see <see cref="PrintJob"/>), so pages print as vector output.
/// </summary>
public sealed partial class PrintDialog : ContentDialog
{
    private readonly Window _window;
    private readonly PdfDocument _document;
    private readonly string _title;
    private readonly int _currentPage;
    /// <summary>
    /// The current sheet and those either side, by index, rendering or rendered for <see cref="_renderedFor"/>;
    /// any change to that renders them again.
    /// </summary>
    private readonly Dictionary<int, SheetRender> _rendered = [];
    private (List<PrintSheet> Sheets, double Pixels, bool Gray, bool Annotations) _renderedFor;
    /// <summary>The render on the sheet, kept until another replaces it even once it's no longer in <see cref="_rendered"/>.</summary>
    private SheetRender? _shown;
    private int _previewVersion;
    private string? _defaultPrinter;
    private PrinterDetails? _printer;
    /// <summary>The printer's settings for the job, including ones only its own dialog can change.</summary>
    private DevMode? _settings;
    private PaperMetrics _paper = PaperMetrics.Letter;
    /// <summary>
    /// Whether <see cref="_paper"/> is what the printer reported, or there's no printer to ask. Until
    /// then the preview stays empty, so it doesn't show Letter and then jump to the printer's paper.
    /// </summary>
    private bool _paperKnown;
    private List<PrintSheet> _sheets = [];
    private int _sheetIndex;
    private int _printerVersion, _paperVersion;
    /// <summary>Set while controls are filled in from code, so their change events don't each lay out again.</summary>
    private bool _updating;
    private CancellationTokenSource? _printCts;
    private bool _printing, _closeRequested, _closed;
    /// <summary>Why there's nothing to print, shown in place of the preview.</summary>
    private string? _problem;

    public PrintDialog(Window window, XamlRoot root, PdfDocument document, string title, int currentPage)
    {
        _window = window;
        _document = document;
        _title = title;
        _currentPage = Math.Clamp(currentPage, 0, document.PageCount - 1);
        XamlRoot = root;
        // Dialogs sit above the window's content, so they don't pick up the theme chosen in Settings by themselves.
        if (root.Content is FrameworkElement content) RequestedTheme = content.RequestedTheme;
        // Controls report their initial selections while they're created, before every field is set.
        _updating = true;
        InitializeComponent();

        // Each option carries the setting it stands for (read with Choice, picked with Choose), so the
        // order they're listed in is only how they look. The first of each list is the default.
        int count = document.PageCount;
        PagesBox.ItemsSource = new PrintChoice[]
        {
            new(PageRange.All, "\uE8C8", Strings.Get("PrintAllPages"), Strings.Count("PageCount", count)),
            new(PageRange.Current, "\uE7C3", Strings.Get("PrintCurrentPage"), Strings.Format("PageLabel", (_currentPage + 1).ToString("N0"))),
            new(PageRange.Custom, "\uE9D5", Strings.Get("PrintCustomRange"), Strings.Get("PrintCustomRangeDetail")),
        };
        OrientationBox.ItemsSource = new PrintChoice[]
        {
            new(SheetOrientation.Automatic, "\uE8B4", Strings.Get("OrientationAutomatic")),
            new(SheetOrientation.Portrait, "\uE7C3", Strings.Get("OrientationPortrait")),
            new(SheetOrientation.Landscape, "\uE7C3", Strings.Get("OrientationLandscape"), angle: 90),
        };
        // The value is whether to print in grey.
        ColorBox.ItemsSource = new PrintChoice[] { new(false, "\uE790", Strings.Get("PrintColor")), new(true, "\uE7A1", Strings.Get("PrintBlackAndWhite")) };
        DuplexBox.ItemsSource = new PrintChoice[]
        {
            new(TwoSided.Off, "\uE7C3", Strings.Get("DuplexOff")),
            new(TwoSided.LongEdge, "\uE8C8", Strings.Get("DuplexLongEdge")),
            new(TwoSided.ShortEdge, "\uE8C8", Strings.Get("DuplexShortEdge"), angle: 90),
        };
        // Without an icon: these lists show the option's text.
        PerSheetBox.ItemsSource = PrintLayout.PagesPerSheetChoices.Select(n => new PrintChoice(n, "", n.ToString())).ToArray();
        ScaleBox.ItemsSource = new PrintChoice[]
        {
            new(PrintScaling.Fit, "", Strings.Get("ScaleFit")),
            new(PrintScaling.ShrinkOversized, "", Strings.Get("ScaleShrink")),
            new(PrintScaling.ActualSize, "", Strings.Get("ScaleActual")),
            new(PrintScaling.Custom, "", Strings.Get("ScaleCustom")),
        };
        SubsetBox.ItemsSource = new PrintChoice[]
        {
            new(PageSubset.All, "", Strings.Get("SubsetAll")),
            new(PageSubset.Odd, "", Strings.Get("SubsetOdd")),
            new(PageSubset.Even, "", Strings.Get("SubsetEven")),
        };
        foreach (var box in new[] { PagesBox, OrientationBox, ColorBox, DuplexBox, PerSheetBox, ScaleBox, SubsetBox }) box.SelectedIndex = 0;
        _updating = false;
        foreach (var box in new[] { PrinterBox, PaperBox, PagesBox, OrientationBox, ColorBox, DuplexBox, PerSheetBox, ScaleBox, SubsetBox })
        {
            // An open list empties the box, which would shrink one showing two lines and pull the
            // settings below it (and the list) up, so it keeps its height until the list closes.
            box.DropDownOpened += (_, _) =>
            {
                ArrowCursor.Show();
                box.MinHeight = box.ActualHeight;
            };
            box.DropDownClosed += (_, _) => box.ClearValue(MinHeightProperty);
        }

        // The sheet fades in when it first shows, rather than popping in once its pages are drawn.
        var fadeIn = ElementCompositionPreview.GetElementVisual(Sheet).Compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Target = "Opacity";
        fadeIn.InsertKeyFrame(0, 0);
        fadeIn.InsertKeyFrame(1, 1);
        fadeIn.Duration = TimeSpan.FromMilliseconds(200);
        ElementCompositionPreview.SetImplicitShowAnimation(Sheet, fadeIn);

        Closing += OnClosing;
        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closed += (_, _) =>
        {
            _closed = true;
            root.Changed -= OnRootChanged;
            ReleaseImages();
        };
        SizeToWindow();
        Relayout();
    }

    private static Style AppStyle(string key) => (Style)Application.Current.Resources[key];

    /// <summary>Which pages the Pages list stands for.</summary>
    private enum PageRange
    {
        All,
        Current,
        Custom,
    }

    /// <summary>The setting the option picked in <paramref name="box"/> stands for, or <paramref name="fallback"/> before one is.</summary>
    private static T Choice<T>(ComboBox box, T fallback) => box.SelectedItem is PrintChoice { Value: T value } ? value : fallback;

    /// <summary>Picks the option of <paramref name="box"/> that stands for <paramref name="value"/>, if it has one.</summary>
    private static void Choose<T>(ComboBox box, T value)
    {
        if ((box.ItemsSource as IEnumerable<PrintChoice>)?.FirstOrDefault(c => Equals(c.Value, value)) is { } choice) box.SelectedItem = choice;
    }

    private PageRange PagesChoice => Choice(PagesBox, PageRange.All);

    /// <summary>Whether the user asked for the Windows print dialog instead.</summary>
    public bool UseSystemDialog { get; private set; }

    /// <summary>Pages that couldn't be drawn and were left blank in the job that was sent.</summary>
    public int FailedPages { get; private set; }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        // Followed by Closed, which unsubscribes. A dialog that never opens, because its window closed or already
        // had one waiting, would otherwise stay subscribed and keep the window alive through _window.
        XamlRoot.Changed += OnRootChanged;
        // The window may have changed size while the dialog waited for its turn.
        SizeToWindow();
        // Start on the printer list. Otherwise focus goes to the first control in the dialog, which is
        // one of the preview's sheet buttons.
        PrinterBox.Focus(FocusState.Programmatic);
        List<string> names;
        try
        {
            (names, _defaultPrinter) = await Task.Run(Printers.Enumerate);
        }
        catch (Exception ex) when (ex is Win32Exception or System.Runtime.InteropServices.COMException)
        {
            names = [];
        }
        if (_closed) return;
        if (names.Count == 0)
        {
            PrinterBox.PlaceholderText = Strings.Get("NoPrinters");
            PrinterStatus.Text = Strings.Get("NoPrintersHint");
            PaperKnown();
            return;
        }
        string? remembered = AppState.Preferences.Print.Printer;
        PrinterBox.ItemsSource = names;
        PrinterBox.SelectedItem = names.Contains(remembered!) ? remembered : names.Contains(_defaultPrinter!) ? _defaultPrinter : names[0];
    }

    // ================================================================ sizing

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToWindow();

    /// <summary>Takes most of the window, leaving a margin around the dialog.</summary>
    private void SizeToWindow()
    {
        var size = XamlRoot?.Size ?? new Size(1200, 820);
        double width = Math.Clamp(size.Width - 96, 320, 1180);
        double height = Math.Clamp(size.Height - 96, 320, 860);
        Root.Width = width;
        Root.Height = height;
        // Too narrow for a useful preview: settings only.
        bool preview = width >= 680;
        PreviewPane.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.Width = preview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SettingsColumn.Width = preview ? new GridLength(348) : new GridLength(1, GridUnitType.Star);
    }

    // ================================================================ printer

    private async void OnPrinterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrinterBox.SelectedItem is not string name) return;
        int version = ++_printerVersion;
        _printer = null;
        PrintButton.IsEnabled = false;
        PropertiesButton.IsEnabled = false;
        PrinterStatus.Text = Strings.Get("PrinterConnecting");
        PrinterStatus.Style = AppStyle("SecondaryCaptionTextBlockStyle");
        PrinterDetails details;
        try
        {
            details = await Task.Run(() => Printers.Load(name));
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            if (version != _printerVersion) return;
            PrinterStatus.Text = Strings.Format("PrinterUnavailable", ex.Message);
            PaperKnown();
            return;
        }
        if (version != _printerVersion) return;

        _printer = details;
        _settings = details.Defaults.Clone();
        var prefs = AppState.Preferences.Print;
        bool remembered = prefs.Printer == name;

        _updating = true;
        PaperBox.ItemsSource = details.Papers.Select(p => new PrintChoice(p, "", p.Name)).ToArray();
        PaperBox.Visibility = details.Papers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        short? paper = remembered && details.Papers.Any(p => p.Id == prefs.PaperSize) ? prefs.PaperSize : details.Defaults.PaperSize;
        SelectPaper(paper);
        ColorBox.Visibility = details.IsColor ? Visibility.Visible : Visibility.Collapsed;
        Choose(ColorBox, remembered ? prefs.Grayscale : details.Defaults.Grayscale ?? false);
        DuplexBox.Visibility = details.CanDuplex ? Visibility.Visible : Visibility.Collapsed;
        Choose(DuplexBox, remembered ? prefs.TwoSided : details.Defaults.Duplex ?? TwoSided.Off);
        _updating = false;

        var status = new List<string>();
        if (name == _defaultPrinter) status.Add(Strings.Get("PrinterDefault"));
        status.Add(details.Status);
        if (details.Location.Length > 0) status.Add(details.Location);
        PrinterStatus.Text = string.Join(" · ", status);
        ToolTipService.SetToolTip(PrinterStatus, PrinterStatus.Text);
        if (details.HasProblem) PrinterStatus.Style = AppStyle("CriticalCaptionTextBlockStyle");
        PropertiesButton.IsEnabled = true;

        await RefreshPaperAsync();
        if (version == _printerVersion) UpdateCanPrint();
    }

    /// <summary>
    /// Picks the paper with <paramref name="id"/>. No id, or one the printer doesn't list, such as a
    /// custom size picked in its own dialog, leaves the list without a selection, so the job keeps the
    /// paper its settings name.
    /// </summary>
    private void SelectPaper(short? id)
    {
        if (_printer is null || _printer.Papers.Count == 0) return;
        if (_printer.Papers.FirstOrDefault(p => p.Id == id) is { } paper) Choose(PaperBox, paper);
        else PaperBox.SelectedItem = null;
    }

    private PaperSize? SelectedPaper => Choice<PaperSize?>(PaperBox, null);

    private bool Grayscale => _printer is { IsColor: false } || (ColorBox.Visibility == Visibility.Visible && Choice(ColorBox, false));

    private TwoSided TwoSidedChoice => _printer is { CanDuplex: true } ? Choice(DuplexBox, TwoSided.Off) : TwoSided.Off;

    private int Copies => double.IsNaN(CopiesBox.Value) ? 1 : (int)Math.Clamp(CopiesBox.Value, 1, 999);

    /// <summary>The printer's settings with the choices made here.</summary>
    private DevMode JobSettings(DevMode settings)
    {
        var job = settings.Clone();
        // Sheets are turned by the layout instead (see PrintLayout.ToPaper), so every printer gets the
        // same pages and the preview shows what comes out. Copies are added when the job is sent.
        job.Landscape = false;
        job.Copies = 1;
        job.Collate = false;
        // Only a different paper: setting it drops the form name the printer's settings may carry.
        if (SelectedPaper is { } paper && paper.Id != job.PaperSize) job.PaperSize = paper.Id;
        if (_printer is { IsColor: true }) job.Grayscale = Grayscale;
        if (_printer is { CanDuplex: true }) job.Duplex = TwoSidedChoice;
        return job;
    }

    /// <summary>Asks the printer for the paper it'll use and where it can print on it.</summary>
    private async Task RefreshPaperAsync()
    {
        if (_printer is not { } printer || _settings is not { } settings) return;
        int version = ++_paperVersion;
        var job = JobSettings(settings);
        PaperMetrics? metrics = null;
        try
        {
            metrics = await Task.Run(() => Printers.QueryPaper(printer.Name, job));
        }
        catch (Exception ex) when (ex is Win32Exception or System.Runtime.InteropServices.COMException)
        {
        }
        if (version != _paperVersion || _printer != printer) return;
        if (metrics is null && SelectedPaper is { } paper)
        {
            // Unknown margins: a quarter inch all round.
            metrics = new PaperMetrics(paper.Width, paper.Height, new Rect(18, 18, Math.Max(1, paper.Width - 36), Math.Max(1, paper.Height - 36)));
        }
        _paper = metrics ?? PaperMetrics.Letter;
        _paperKnown = true;
        Relayout();
    }

    /// <summary>Shows the preview on the paper there is, when the printer can't say what it'll use.</summary>
    private void PaperKnown()
    {
        if (_paperKnown) return;
        _paperKnown = true;
        RenderPreview();
    }

    private async void OnPaperChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) await RefreshPaperAsync();
    }

    private async void OnPrinterProperties(object sender, RoutedEventArgs e)
    {
        if (_printer is not { } printer || _settings is not { } settings) return;
        var job = JobSettings(settings);
        // The printer's dialog shows the choices made here, though the job itself is always sent portrait.
        bool landscape = Choice(OrientationBox, SheetOrientation.Automatic) == SheetOrientation.Landscape;
        job.Landscape = landscape;
        job.Copies = (short)Copies;
        job.Collate = CollateBox.IsChecked == true;
        DevMode? result;
        try
        {
            result = Printers.ShowProperties(WinRT.Interop.WindowNative.GetWindowHandle(_window), printer.Name, job);
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            ShowError(Strings.Format("PrinterPropertiesFailed", ex.Message));
            return;
        }
        if (result is null || _printer != printer) return;

        // Bring the choices here in line with what was picked there.
        _settings = result;
        _updating = true;
        SelectPaper(result.PaperSize);
        // Only a change: leaving it as shown keeps Automatic.
        if (result.Landscape is { } turned && turned != landscape) Choose(OrientationBox, turned ? SheetOrientation.Landscape : SheetOrientation.Portrait);
        if (printer.IsColor && result.Grayscale is { } gray) Choose(ColorBox, gray);
        if (printer.CanDuplex && result.Duplex is { } duplex) Choose(DuplexBox, duplex);
        if (result.Copies is { } copies && copies > 0) CopiesBox.Value = copies;
        if (result.Collate is { } collate) CollateBox.IsChecked = collate;
        CollateBox.IsEnabled = Copies > 1;
        _updating = false;
        await RefreshPaperAsync();
    }

    private void OnSystemDialog(object sender, RoutedEventArgs e)
    {
        if (_printing) return;
        UseSystemDialog = true;
        Hide();
    }

    // ================================================================ settings

    private void OnPagesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        bool custom = PagesChoice == PageRange.Custom;
        RangeBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        Relayout();
    }

    private void OnPagesDropDownClosed(object sender, object e)
    {
        // Picked "Custom" from the list: the range is typed next. Not on SelectionChanged, which also
        // fires for arrow keys on the closed box, and while the list is open it takes focus back.
        if (PagesChoice == PageRange.Custom) RangeBox.Focus(FocusState.Programmatic);
    }

    private void OnToggleMore(object sender, RoutedEventArgs e)
    {
        bool open = MoreContent.Visibility != Visibility.Visible;
        MoreContent.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        // Turns the chevron up or down, quicker than its own animation would.
        var turn = new DoubleAnimation
        {
            To = open ? 180 : 0,
            Duration = TimeSpan.FromMilliseconds(150),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(turn, MoreChevronRotation);
        Storyboard.SetTargetProperty(turn, nameof(RotateTransform.Angle));
        var storyboard = new Storyboard();
        storyboard.Children.Add(turn);
        storyboard.Begin();
    }

    private void OnRangeChanged(object sender, TextChangedEventArgs e) => Relayout();
    private void OnLayoutChanged(object sender, RoutedEventArgs e) => Relayout();
    private void OnAppearanceChanged(object sender, RoutedEventArgs e) => RenderPreview();
    private void OnCountChanged(object sender, RoutedEventArgs e) => UpdateSummary();

    private void OnCopiesChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updating) return;
        if (double.IsNaN(args.NewValue)) sender.Value = 1;
        CollateBox.IsEnabled = Copies > 1;
        UpdateSummary();
    }

    private void OnScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        ScalePercentBox.Visibility = Choice(ScaleBox, PrintScaling.Fit) == PrintScaling.Custom ? Visibility.Visible : Visibility.Collapsed;
        Relayout();
    }

    private void OnScalePercentChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updating) return;
        if (double.IsNaN(args.NewValue)) sender.Value = 100;
        else Relayout();
    }

    private LayoutOptions Layout => new(
        Choice(OrientationBox, SheetOrientation.Automatic),
        Choice(ScaleBox, PrintScaling.Fit),
        (double.IsNaN(ScalePercentBox.Value) ? 100 : ScalePercentBox.Value) / 100,
        Choice(PerSheetBox, 1));

    /// <summary>The pages to print, in order, or an explanation of what's wrong with the range.</summary>
    private bool TryGetPages(out List<int> pages, out string? error)
    {
        error = null;
        switch (PagesChoice)
        {
            case PageRange.Current:
                pages = [_currentPage];
                break;
            case PageRange.Custom:
                if (!PrintLayout.TryParseRanges(RangeBox.Text, _document.PageCount, out pages, out error)) return false;
                break;
            default:
                pages = [.. Enumerable.Range(0, _document.PageCount)];
                break;
        }
        var subset = Choice(SubsetBox, PageSubset.All);
        pages = PrintLayout.Filter(pages, subset, ReverseBox.IsChecked == true);
        if (pages.Count == 0)
        {
            error = Strings.Get(subset == PageSubset.Even ? "NoEvenPages" : "NoOddPages");
            return false;
        }
        return true;
    }

    private void Relayout()
    {
        if (_updating || RangeError is null) return;
        if (TryGetPages(out var pages, out string? error))
        {
            RangeError.Visibility = Visibility.Collapsed;
            _problem = null;
            _sheets = PrintLayout.Layout(pages, _document.PageSizes, _paper, Layout);
        }
        else
        {
            // An unfinished range while typing: no error until there's something to complain about.
            bool show = PagesChoice != PageRange.Custom || RangeBox.Text.Trim().Length > 0;
            RangeError.Text = error;
            RangeError.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _problem = show ? error : Strings.Get("EnterPages");
            _sheets = [];
        }
        _sheetIndex = Math.Clamp(_sheetIndex, 0, Math.Max(0, _sheets.Count - 1));
        UpdateSummary();
        UpdateCanPrint();
        RenderPreview();
    }

    private void UpdateSummary()
    {
        if (_updating || Summary is null) return;
        if (_sheets.Count == 0)
        {
            Summary.Text = Strings.Get("NothingToPrint");
            return;
        }
        bool twoSided = TwoSidedChoice != TwoSided.Off;
        int paper = PrintLayout.PaperCount(_sheets.Count, Copies, twoSided);
        Summary.Text = Strings.Count("SheetsOfPaper", paper);
    }

    private void UpdateCanPrint() => PrintButton.IsEnabled = !_printing && _printer is not null && _sheets.Count > 0;

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    // ================================================================ preview

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => RenderPreview();

    private void OnPreviousSheet(object sender, RoutedEventArgs e) => MoveSheet(-1);
    private void OnNextSheet(object sender, RoutedEventArgs e) => MoveSheet(+1);

    private void OnPreviewWheel(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(PreviewPane).Properties.MouseWheelDelta;
        if (delta != 0) MoveSheet(delta < 0 ? +1 : -1);
        e.Handled = true;
    }

    private void MoveSheet(int step)
    {
        int index = Math.Clamp(_sheetIndex + step, 0, Math.Max(0, _sheets.Count - 1));
        if (index == _sheetIndex) return;
        _sheetIndex = index;
        RenderPreview();
    }

    /// <summary>
    /// Shows the current sheet, rendering it if it isn't already, and starts on the sheets either side
    /// so stepping to them shows them straight away. The sheet on screen stays up until the new one is
    /// ready, so changing a setting doesn't flash an empty sheet.
    /// </summary>
    private async void RenderPreview()
    {
        // Closed: nothing would release the images.
        if (_updating || _closed || Sheet is null) return;
        int version = ++_previewVersion;

        PreviousSheetButton.IsEnabled = _sheetIndex > 0;
        NextSheetButton.IsEnabled = _sheetIndex < _sheets.Count - 1;
        SheetPosition.Text = _sheets.Count == 0 ? "" : Strings.Format("SheetPosition", _sheetIndex + 1, _sheets.Count);
        if (_sheets.Count == 0)
        {
            Sheet.Visibility = Visibility.Collapsed;
            PreviewMessage.Text = _problem ?? Strings.Get("NothingToPrintSentence");
            ReleaseImages();
            return;
        }
        // Leave room for the hairline around the paper, or it's clipped where the paper fills the space.
        // Layout rounds each side to whole device pixels (a 1 DIP side is 2 pixels at 150%), so leave that much.
        double scale = XamlRoot?.RasterizationScale ?? 1;
        double Laid(double side) => Math.Round(side * scale, MidpointRounding.AwayFromZero) / scale;
        var border = Sheet.BorderThickness;
        double availableWidth = PreviewHost.ActualWidth - Laid(border.Left) - Laid(border.Right);
        double availableHeight = PreviewHost.ActualHeight - Laid(border.Top) - Laid(border.Bottom);
        if (!_paperKnown || availableWidth <= 0 || availableHeight <= 0) return;

        int index = _sheetIndex;
        var sheet = _sheets[index];
        double k = Math.Min(availableWidth / sheet.Paper.Width, availableHeight / sheet.Paper.Height);
        double pixels = k * scale;
        bool gray = Grayscale, annotations = AnnotationsBox.IsChecked == true;

        var key = (_sheets, pixels, gray, annotations);
        if (_renderedFor != key)
        {
            ForgetRendered(_ => true);
            _renderedFor = key;
        }
        ForgetRendered(i => Math.Abs(i - index) > 1);
        SheetRender Render(int i, WorkPriority priority)
        {
            if (!_rendered.TryGetValue(i, out var render))
                _rendered[i] = render = new SheetRender(_document, _sheets[i], pixels, gray, annotations, priority);
            return render;
        }
        var current = Render(index, WorkPriority.Visible);
        if (index + 1 < _sheets.Count) Render(index + 1, WorkPriority.Normal);
        if (index > 0) Render(index - 1, WorkPriority.Normal);

        var pages = await current.Pages;
        if (pages is null)
        {
            // Failed, or stopped because it's no longer wanted: either way the next time it's shown starts again.
            if (_rendered.TryGetValue(index, out var render) && render == current) ForgetRendered(i => i == index);
            return;
        }
        if (version != _previewVersion || _closed) return;

        SheetCanvas.Children.Clear();
        if (_shown is { } old && old != current && !_rendered.ContainsValue(old)) old.Release();
        _shown = current;
        SheetCanvas.Width = sheet.Paper.Width * k;
        SheetCanvas.Height = sheet.Paper.Height * k;
        Sheet.Visibility = Visibility.Visible;
        PreviewMessage.Text = "";
        foreach (var (visible, source) in pages)
        {
            if (source is null) continue;
            var image = new Image { Source = source, Stretch = Stretch.Fill, Width = visible.Width * k, Height = visible.Height * k };
            Canvas.SetLeft(image, visible.X * k);
            Canvas.SetTop(image, visible.Y * k);
            SheetCanvas.Children.Add(image);
        }
    }

    /// <summary>Lets go of the rendered sheets <paramref name="match"/> picks; the one on screen is kept until it's replaced.</summary>
    private void ForgetRendered(Func<int, bool> match)
    {
        foreach (int i in _rendered.Keys.Where(match).ToList())
        {
            var render = _rendered[i];
            _rendered.Remove(i);
            if (render != _shown) render.Release();
        }
    }

    private void ReleaseImages()
    {
        SheetCanvas.Children.Clear();
        ForgetRendered(_ => true);
        _shown?.Release();
        _shown = null;
    }

    /// <summary>A sheet's pages rendering, or rendered, for the preview.</summary>
    private sealed class SheetRender
    {
        private readonly CancellationTokenSource _cts = new();
        private bool _released;

        public SheetRender(PdfDocument document, PrintSheet sheet, double pixels, bool gray, bool annotations, WorkPriority priority) =>
            Pages = RenderAsync(document, sheet, pixels, gray, annotations, priority, _cts.Token);

        /// <summary>Where each page shows on the sheet and its image, or null if the sheet couldn't be rendered.</summary>
        public Task<(Rect Visible, SoftwareBitmapSource? Source)[]?> Pages { get; }

        /// <summary>Stops the render if it's still going, and releases the images once it's done.</summary>
        public async void Release()
        {
            if (_released) return;
            _released = true;
            _cts.Cancel();
            if (await Pages is { } pages)
            {
                foreach (var (_, source) in pages) source?.Dispose();
            }
            _cts.Dispose();
        }

        private static async Task<(Rect Visible, SoftwareBitmapSource? Source)[]?> RenderAsync(PdfDocument document, PrintSheet sheet,
            double pixels, bool gray, bool annotations, WorkPriority priority, CancellationToken token)
        {
            async Task<(Rect Visible, SoftwareBitmapSource? Source)> RenderPageAsync(PagePlacement placement)
            {
                var visible = placement.Bounds;
                visible.Intersect(placement.Clip);
                if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return (visible, null);
                var size = document.PageSizes[placement.Page];
                double scale = pixels * placement.Bounds.Width / size.Width;
                var region = new RectInt32(
                    (int)Math.Round((visible.X - placement.Bounds.X) * pixels), (int)Math.Round((visible.Y - placement.Bounds.Y) * pixels),
                    Math.Max(1, (int)Math.Round(visible.Width * pixels)), Math.Max(1, (int)Math.Round(visible.Height * pixels)));
                try
                {
                    using var buffer = await document.RenderAsync(placement.Page, scale, 0, region, false, priority, token, printing: true, annotations, gray);
                    if (buffer is null || token.IsCancellationRequested) return (visible, null);
                    return (visible, await PageView.ToSourceAsync(buffer));
                }
                catch (InvalidDataException)
                {
                    return (visible, null);
                }
            }

            var renders = sheet.Pages.Select(RenderPageAsync).ToArray();
            try
            {
                await Task.WhenAll(renders);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
            {
                // The pages that were ready before one was stopped or failed to upload.
                foreach (var render in renders)
                {
                    if (render.IsCompletedSuccessfully) render.Result.Source?.Dispose();
                }
                return null;
            }
            return renders.Select(r => r.Result).ToArray();
        }
    }

    // ================================================================ printing

    /// <summary>Enter that no control used, such as after typing pages, prints: what the dialog's own default button did.</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || !PrintButton.IsEnabled) return;
        e.Handled = true;
        OnPrintClick(PrintButton, e);
    }

    /// <summary>Hides the dialog, or while a job is being sent, cancels it first (see <see cref="OnClosing"/>).</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e) => Hide();

    private async void OnPrintClick(object sender, RoutedEventArgs e)
    {
        // The dialog stays up while the job is sent, with its progress above the buttons.
        if (_printing || _printer is not { } printer || _settings is not { } settings) return;
        if (!TryGetPages(out var pages, out _))
        {
            RangeBox.Focus(FocusState.Programmatic);
            return;
        }
        ErrorBar.IsOpen = false;

        var job = JobSettings(settings);
        bool twoSided = TwoSidedChoice != TwoSided.Off;
        // Printing from here on, file picker included, so another Print does nothing and closing cancels.
        SetPrinting(true);
        ProgressText.Text = Strings.Get("PrintPreparing");
        PrintProgress.Value = 0;
        var cts = _printCts = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (!_printing) return;
            PrintProgress.Value = (double)p.Done / p.Total;
            ProgressText.Text = Strings.Format("PrintSending", p.Done, p.Total);
        });
        try
        {
            string? output = null;
            if (printer.OutputExtension is { } extension)
            {
                // Printers that make a file would ask for its name themselves, in a dialog that can open behind this window.
                output = await PickOutputAsync(extension) ?? throw new OperationCanceledException();
                cts.Token.ThrowIfCancellationRequested();
            }
            // The printer makes the copies when it can, so the document is only sent once.
            int copies = Copies;
            bool collate = CollateBox.IsChecked == true;
            bool printerCopies = PrintLayout.PrinterMakesCopies(copies, collate, twoSided, printer.MaxCopies, printer.CanCollate);
            if (printerCopies)
            {
                job.Copies = (short)copies;
                job.Collate = collate;
            }
            job = await Task.Run(() => Printers.Merge(printer.Name, job));
            if (printerCopies && (job.Copies != copies || (job.Collate ?? false) != collate))
            {
                // The driver didn't keep them after all: send the copies instead.
                job.Copies = 1;
                job.Collate = false;
                printerCopies = false;
            }
            var request = new PrintRequest(printer.Name, job, _title, output, pages, Layout, printerCopies ? 1 : copies, collate, twoSided,
                AnnotationsBox.IsChecked == true, Grayscale, ImageBox.IsChecked == true);
            FailedPages = await PrintJob.RunAsync(_document, request, progress, cts.Token);
            Remember(printer);
            SetPrinting(false);
            Hide();
        }
        catch (OperationCanceledException)
        {
            SetPrinting(false);
            if (_closeRequested) Hide();
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ObjectDisposedException or Win32Exception
            or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            SetPrinting(false);
            if (_closeRequested) Hide();
            else ShowError(ex is ObjectDisposedException ? Strings.Get("PrintDocumentClosed") : ex.Message);
        }
    }

    private async Task<string?> PickOutputAsync(string extension)
    {
        var picker = new FileSavePicker(_window.AppWindow.Id)
        {
            // The file's name rather than the title, which can hold characters a file name can't.
            SuggestedFileName = Path.GetFileNameWithoutExtension(_document.FilePath),
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add(Strings.Get(extension == ".pdf" ? "PdfDocumentType" : "XpsDocumentType"), [extension]);
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    private void SetPrinting(bool printing)
    {
        _printing = printing;
        SettingsScroller.IsEnabled = !printing;
        ProgressPanel.Visibility = printing ? Visibility.Visible : Visibility.Collapsed;
        UpdateCanPrint();
    }

    /// <summary>Whether a job is being sent, file picker included.</summary>
    public bool IsSending => _printing;

    /// <summary>
    /// For a window that's closing once its printing is done: closes the dialog once the job has been sent or has
    /// failed, rather than cancelling it.
    /// </summary>
    public void CloseWhenDone() => _closeRequested = true;

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_printing) return;
        // Cancel (or Esc) while the job is being sent stops it; the dialog closes once it's withdrawn.
        args.Cancel = true;
        _closeRequested = true;
        ProgressText.Text = Strings.Get("PrintCancelling");
        _printCts?.Cancel();
    }

    private void Remember(PrinterDetails printer)
    {
        var prefs = AppState.Preferences.Print;
        prefs.Printer = printer.Name;
        prefs.PaperSize = SelectedPaper?.Id;
        if (printer.IsColor) prefs.Grayscale = Grayscale;
        if (printer.CanDuplex) prefs.TwoSided = TwoSidedChoice;
        AppState.Save();
    }
}

/// <summary>
/// An option in the print dialog's lists: the setting it stands for, its name, and for the main
/// lists an icon and maybe a second line. Lists without the icon template show <see cref="ToString"/>.
/// </summary>
public sealed class PrintChoice(object value, string glyph, string text, string? detail = null, double angle = 0)
{
    /// <summary>The setting the option stands for: an enum value, a number, a paper size.</summary>
    public object Value => value;
    public string Glyph => glyph;
    /// <summary>Turns the icon, for the sideways version of one.</summary>
    public double Angle => angle;
    public string Text => text;
    public string Detail => detail ?? "";
    public bool HasDetail => detail is not null;

    /// <summary>What screen readers announce for the option.</summary>
    public override string ToString() => text;
}
