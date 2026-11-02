using System.Reflection;
using Recto.Pdf;
using Recto.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Recto;

internal static class Dialogs
{
    private static Style AppStyle(string key) => (Style)Application.Current.Resources[key];

    /// <summary>
    /// A dialog for the window <paramref name="root"/> belongs to. Dialogs are shown above the window's
    /// content rather than inside it, so they don't pick up the theme chosen in Settings by themselves.
    /// </summary>
    public static ContentDialog Create(XamlRoot root)
    {
        var dialog = new ContentDialog { XamlRoot = root };
        if (root.Content is FrameworkElement content) dialog.RequestedTheme = content.RequestedTheme;
        return dialog;
    }

    // Only touched on the UI thread.
    /// <summary>Completes when the last dialog shown or waiting to be shown has closed.</summary>
    private static Task _lastDialog = Task.CompletedTask;
    /// <summary>The dialog on screen, in whichever window.</summary>
    private static ContentDialog? _openDialog;
    /// <summary>Dialogs waiting for their turn.</summary>
    private static readonly List<ContentDialog> Waiting = [];
    /// <summary>Windows that have closed, whose dialogs still waiting are skipped.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<XamlRoot, object> ClosedRoots = [];
    /// <summary>
    /// Windows out of sight until they close, whose dialogs are skipped and closed like a closed window's, except
    /// the one each maps to (or none, when it maps to itself).
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<XamlRoot, object> HiddenRoots = [];

    /// <summary>Raised when a dialog opens or closes, or starts or stops waiting for its turn.</summary>
    public static event EventHandler? QueueChanged;

    /// <summary>The window of the dialog on screen, if there is one.</summary>
    public static XamlRoot? OpenRoot => _openDialog?.XamlRoot;

    /// <summary>
    /// Whether the window <paramref name="root"/> belongs to has a dialog waiting for one open in another window,
    /// which the window says it's waiting for.
    /// </summary>
    public static bool IsWaitingForOtherWindow(XamlRoot root) =>
        OpenRoot is { } open && open != root && Waiting.Any(d => d.XamlRoot == root);

    /// <summary>
    /// Closes the dialog of a window that's closing, so the dialogs of other windows waiting behind it get
    /// their turn, and skips the ones it still has waiting.
    /// </summary>
    public static void WindowClosed(XamlRoot root)
    {
        ClosedRoots.AddOrUpdate(root, root);
        if (_openDialog?.XamlRoot == root) _openDialog.Hide();
    }

    /// <summary>
    /// Treats the dialogs of a window that's going out of sight before it closes as <see cref="WindowClosed"/> does,
    /// except <paramref name="keep"/>, which stays open until it closes itself. Otherwise one waiting would open
    /// on the hidden window, where nobody could close it, and every dialog after it would wait forever.
    /// </summary>
    public static void WindowHidden(XamlRoot root, ContentDialog? keep)
    {
        HiddenRoots.AddOrUpdate(root, keep ?? (object)root);
        if (_openDialog is { } open && open.XamlRoot == root && open != keep) open.Hide();
    }

    private static bool IsGone(XamlRoot root, ContentDialog dialog) =>
        ClosedRoots.TryGetValue(root, out _) || HiddenRoots.TryGetValue(root, out var kept) && !ReferenceEquals(kept, dialog);

    /// <summary>
    /// Shows <paramref name="dialog"/> once the dialogs before it have closed. WinUI shows one dialog at a time
    /// per UI thread, not per window, and every Recto window shares the one thread: ShowAsync throws for a second
    /// dialog in any window, which one asked for from code (a file opened from Explorer that needs a password, a
    /// shortcut) could otherwise hit. A dialog whose window closes, before or while it's shown, comes back as
    /// cancelled, and so does one asked for while its window already has one waiting for another window: one
    /// waiting is enough, rather than a dialog for every key pressed meanwhile popping up one after another.
    /// </summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (dialog.XamlRoot is { } owner && IsWaitingForOtherWindow(owner)) return ContentDialogResult.None;
        var previous = _lastDialog;
        var done = new TaskCompletionSource();
        _lastDialog = done.Task;
        try
        {
            if (!previous.IsCompleted)
            {
                Waiting.Add(dialog);
                QueueChanged?.Invoke(null, EventArgs.Empty);
                await previous;
                Waiting.Remove(dialog);
            }
            if (dialog.XamlRoot is not { } root || IsGone(root, dialog)) return ContentDialogResult.None;
            _openDialog = dialog;
            QueueChanged?.Invoke(null, EventArgs.Empty);
            // Subscribed after the dialog's own handlers, so it has the last word; see OnClosing.
            dialog.Closing += OnClosing;
            return await dialog.ShowAsync();
        }
        finally
        {
            dialog.Closing -= OnClosing;
            if (_openDialog == dialog) _openDialog = null;
            done.SetResult();
            QueueChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// A dialog can refuse to close while it's busy (the password prompt does while it checks a password), but
    /// not once its window has closed: nothing would close it after that, and every dialog after it, in any
    /// window, would wait forever. WinUI raises Closing to its handlers in the order they were added.
    /// </summary>
    private static void OnClosing(ContentDialog dialog, ContentDialogClosingEventArgs args)
    {
        if (dialog.XamlRoot is { } root && IsGone(root, dialog)) args.Cancel = false;
    }

    public static async Task ShowPropertiesAsync(XamlRoot root, PdfDocument document)
    {
        var info = document.Info;
        var file = new FileInfo(document.FilePath);
        var rows = new List<(string, string)>
        {
            ("Title", info.Title),
            ("Author", info.Author),
            ("Subject", info.Subject),
            ("Keywords", info.Keywords),
            ("Creator", info.Creator),
            ("Producer", info.Producer),
            ("Created", info.Created?.LocalDateTime.ToString("f") ?? ""),
            ("Modified", info.Modified?.LocalDateTime.ToString("f") ?? ""),
        };
        var general = new List<(string, string)>
        {
            ("Location", file.FullName),
            ("Size", AttachmentItem.FormatSize(file.Exists ? file.Length : document.FileSize)),
            ("Format", string.IsNullOrEmpty(info.Version) ? "PDF" : $"PDF {info.Version}"),
            ("Pages", document.PageCount.ToString("N0")),
            ("Page size", PaperSize(document.PageSizes[0])),
            ("Security", info.IsEncrypted ? "Encrypted" : "None"),
        };

        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(Section("Document", rows.Where(r => !string.IsNullOrWhiteSpace(r.Item2)).ToList()));
        panel.Children.Add(Section("File", general));

        var dialog = Create(root);
        dialog.Title = "Properties";
        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 520, Padding = new Thickness(0, 0, 16, 0) };
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Close;
        await ShowAsync(dialog);
    }

    private static FrameworkElement Section(string title, List<(string Label, string Value)> rows)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = title, Style = AppStyle("BodyStrongTextBlockStyle") });
        var card = new Border { Style = AppStyle("CardBorderStyle"), Padding = new Thickness(16, 12, 16, 12) };
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (rows.Count == 0) rows.Add(("", "No information available"));
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, Style = AppStyle("SecondaryTextBlockStyle") };
            var value = new TextBlock { Text = rows[i].Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
        card.Child = grid;
        stack.Children.Add(card);
        return stack;
    }

    private static string PaperSize(Windows.Foundation.Size points)
    {
        double w = points.Width * 25.4 / 72, h = points.Height * 25.4 / 72;
        double a = Math.Min(w, h), b = Math.Max(w, h);
        (string Name, double A, double B)[] known =
        [
            ("A3", 297, 420), ("A4", 210, 297), ("A5", 148, 210), ("B5", 176, 250),
            ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6), ("Tabloid", 279.4, 431.8),
        ];
        var match = known.FirstOrDefault(k => Math.Abs(k.A - a) < 2 && Math.Abs(k.B - b) < 2);
        string orientation = w > h ? ", landscape" : "";
        string size = $"{w:0} × {h:0} mm";
        return match.Name is null ? size : $"{match.Name}{orientation} ({size})";
    }

    public static async Task ShowShortcutsAsync(XamlRoot root)
    {
        (string Group, (string Keys, string Action)[] Items)[] groups =
        [
            ("Documents", [
                ("Ctrl+O", "Open a document"), ("Ctrl+N", "New window"), ("Ctrl+S", "Save a copy"),
                ("Ctrl+P", "Print"), ("Ctrl+Shift+P", "Print using the system dialog"), ("Ctrl+R", "Reload"),
                ("Alt+Enter", "Properties"), ("Ctrl+W", AppState.Preferences.RememberRecent ? "Close document" : "Close window"),
                ("Ctrl+,", "Settings"),
            ]),
            ("Navigation", [
                ("Up / Down", "Scroll"),
                ("PgUp / PgDn", "Previous / next screen, or page in single page and fit page"),
                ("Shift+Space / Space", "Previous / next screen, or page in single page and fit page"),
                ("Left / Right", "Previous / next page, or scroll when the page is wider than the window"),
                ("Home / End", "Beginning / end of document"),
                ("Ctrl+B / F9", "Show or hide the sidebar"),
            ]),
            ("Search and selection", [
                ("Ctrl+F", "Search"), ("F3 / Enter", "Next result"), ("Shift+F3 / Shift+Enter", "Previous result"),
                ("Ctrl+C", "Copy selected text"), ("Ctrl+A", "Select all"),
            ]),
            ("Annotations", [
                ("Ctrl+H", "Highlight selection"), ("Ctrl+Shift+A", "Show or hide the drawing and text tools"),
                ("Esc", "Leave the current tool"), ("Delete", "Delete the selected drawing or text box"),
                ("Ctrl+Z", "Undo"), ("Ctrl+Y / Ctrl+Shift+Z", "Redo"),
            ]),
            ("View", [
                ("Ctrl+Plus / Ctrl+Minus", "Zoom in / out"), ("Ctrl+0", "Actual size"), ("Ctrl+Wheel", "Zoom"),
                ("Ctrl+Left / Ctrl+Right", "Rotate"), ("Ctrl+I", "Invert colors"), ("F11", "Full screen"), ("F5", "Present"),
            ]),
        ];

        var panel = new StackPanel { Spacing = 20 };
        foreach (var (group, items) in groups)
        {
            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock { Text = group, Style = AppStyle("BodyStrongTextBlockStyle") });
            var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < items.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var action = new TextBlock { Text = items[i].Action, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
                var alternatives = items[i].Keys.Split(" / ");
                for (int alt = 0; alt < alternatives.Length; alt++)
                {
                    if (alt > 0)
                        keys.Children.Add(new TextBlock { Text = "/", VerticalAlignment = VerticalAlignment.Center, Style = AppStyle("SecondaryTextBlockStyle") });
                    foreach (var key in alternatives[alt].Split('+'))
                    {
                        keys.Children.Add(new Border
                        {
                            Style = AppStyle("KeyCapBorderStyle"),
                            Child = new TextBlock { Text = key, Style = AppStyle("CaptionTextBlockStyle") },
                        });
                    }
                }
                Grid.SetRow(action, i);
                Grid.SetRow(keys, i);
                Grid.SetColumn(keys, 1);
                grid.Children.Add(action);
                grid.Children.Add(keys);
            }
            section.Children.Add(grid);
            panel.Children.Add(section);
        }

        var dialog = Create(root);
        dialog.Title = "Keyboard shortcuts";
        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 520, Padding = new Thickness(0, 0, 16, 0) };
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Close;
        await ShowAsync(dialog);
    }

    public static async Task ShowAboutAsync(XamlRoot root)
    {
        var version = typeof(Dialogs).Assembly.GetName().Version;
        var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Width = 320 };
        panel.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png")),
            Width = 96,
            Height = 96,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock { Text = "Recto", Style = AppStyle("SubtitleTextBlockStyle"), HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = $"Version {version?.ToString(3) ?? "1.0.0"}",
            Style = AppStyle("SecondaryTextBlockStyle"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        // The commit and checkout (or CI branch) it was built from; see AddBuildInfo in Recto.csproj.
        var buildInfo = typeof(Dialogs).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildInfo")?.Value;
        if (!string.IsNullOrEmpty(buildInfo))
        {
            panel.Children.Add(new TextBlock
            {
                Text = buildInfo,
                Style = AppStyle("SecondaryTextBlockStyle"),
                HorizontalAlignment = HorizontalAlignment.Center,
                IsTextSelectionEnabled = true,
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = "A simple document viewer for Windows, inspired by GNOME Papers. PDF rendering by PDFium.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        });
        var dialog = Create(root);
        dialog.Content = panel;
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Close;
        await ShowAsync(dialog);
    }
}
