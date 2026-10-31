using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Recto.Controls;
using Recto.Pdf;
using Recto.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.System;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using ZoomMode = Recto.Services.ZoomMode;
using COMException = System.Runtime.InteropServices.COMException;

namespace Recto;

public sealed record OutlineEntry(string Title, string Page, PdfDestination? Destination);

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueueTimer _searchDebounce;
    private readonly DispatcherQueueTimer _reloadDebounce;
    private readonly DispatcherQueueTimer _hintTimer;
    private readonly DispatcherQueueTimer _thumbnailSyncThrottle;
    private bool _thumbnailSyncPending;
    private readonly TaskCompletionSource _loaded = new();
    private readonly SearchItemCollection _searchItems = new();
    private readonly ObservableCollection<AnnotationItem> _annotationItems = [];
    private readonly Dictionary<int, PdfAnnotation[]> _annotations = [];
    private List<ThumbnailItem> _thumbnails = [];
    private List<RecentItem> _recentItems = [];
    private readonly ObservableCollection<RecentItem> _recentMatches = [];
    private readonly EventHandler _recentChanged;
    private readonly EventHandler _dialogQueueChanged;
    /// <summary>Folders under %TEMP%\Recto holding attachments this window extracted; deleted when it closes.</summary>
    private readonly List<string> _attachmentFolders = [];
    private PdfDocument? _document;
    /// <summary>The password that unlocked <see cref="_document"/>, kept for reloads during this session.</summary>
    private string? _password;
    private int _opening;
    /// <summary>Numbers requests to show a file (see <see cref="OpenAsync"/>) in the order they're made.</summary>
    private int _openRequests;
    /// <summary>The latest request that showed its file. An open that finishes after a later one did is dropped.</summary>
    private int _openShown;
    /// <summary>Files being opened, until their open finishes.</summary>
    private readonly List<string> _openingPaths = [];
    /// <summary>Files loading; the progress ring spins while there are any.</summary>
    private int _loading;
    /// <summary>The window was opened for a file and stays empty until it loads, instead of flashing the start page.</summary>
    private bool _hideStartPage;
    private RecentDocument? _entry;
    private CancellationTokenSource? _searchCts;
    /// <summary>Reading the pages' text while the search panel is open; see <see cref="ReadSearchText"/>.</summary>
    private CancellationTokenSource? _searchTextCts;
    private CancellationTokenSource? _annotationScanCts;
    private FileSystemWatcher? _watcher;
    private DateTime _ignoreWatcherUntil;
    private int _searchIndex = -1;
    /// <summary>
    /// The search picked the current result, and keeps it at the top of the list while it adds the ones after it,
    /// until another is picked.
    /// </summary>
    private bool _searchResultAtTop;
    /// <summary>
    /// The results listed are the last search's: they stay until the new one lists its own, so the list and the
    /// highlights don't flash empty while it starts.
    /// </summary>
    private bool _searchResultsStale;
    private string _searchQuery = "";
    /// <summary>The options <see cref="_searchQuery"/> was searched with.</summary>
    private SearchOptions _searchOptions;
    private bool _syncingThumbnails;
    private bool _sidebarOpen;
    private bool _fullscreen;
    private bool _presenting;
    private bool _sidebarBeforeFullscreen;
    private bool _annotationsScanned;
    private bool _forceClose;
    /// <summary>The window has closed. Opens still under way, such as one waiting on a password check, show nothing.</summary>
    private bool _closed;
    private bool _hasOutline;
    private bool _hasAttachments;
    private bool _settingsOpen;
    /// <summary>Print dialogs (Recto's or Windows') that are open or still sending the document to the printer.</summary>
    private int _printing;
    /// <summary>Recto's print dialog, while it's open or waiting for its turn; there's one at a time.</summary>
    private PrintDialog? _printDialog;
    /// <summary>The window was closed while printing: it's out of sight, and closes once the job has been sent.</summary>
    private bool _closeWhenPrinted;
    /// <summary>Documents closed or replaced while printing, which the job may still be reading; released once it's done.</summary>
    private readonly List<PdfDocument> _releaseWhenPrinted = [];
    /// <summary>The file changed on disk while Settings or a print dialog was open; checked again when they close.</summary>
    private bool _reloadPending;
    /// <summary>Automatic reloads put off because another app still had the file open, likely still writing it.</summary>
    private int _reloadRetries;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        Title = "Recto";

        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        var prefs = AppState.Preferences;
        double scale = GetDpiScale();
        AppWindow.Resize(new SizeInt32((int)(Math.Max(640, prefs.WindowWidth) * scale), (int)(Math.Max(480, prefs.WindowHeight) * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(560 * scale);
            presenter.PreferredMinimumHeight = (int)(420 * scale);
            if (prefs.WindowMaximized) presenter.Maximize();
        }
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;
        Activated += OnActivated;
        App.OpenWindows.Add(this);

        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += OnSearchDebounceTick;

        _reloadDebounce = DispatcherQueue.CreateTimer();
        _reloadDebounce.Interval = TimeSpan.FromMilliseconds(600);
        _reloadDebounce.IsRepeating = false;
        _reloadDebounce.Tick += OnReloadDebounceTick;

        _hintTimer = DispatcherQueue.CreateTimer();
        _hintTimer.Interval = TimeSpan.FromSeconds(2.5);
        _hintTimer.IsRepeating = false;
        _hintTimer.Tick += OnHintTimerTick;

        _thumbnailSyncThrottle = DispatcherQueue.CreateTimer();
        _thumbnailSyncThrottle.Interval = TimeSpan.FromMilliseconds(150);
        _thumbnailSyncThrottle.IsRepeating = false;
        _thumbnailSyncThrottle.Tick += OnThumbnailSyncThrottleTick;

        SearchResultList.ItemsSource = _searchItems;
        AnnotationList.ItemsSource = _annotationItems;
        RecentGrid.ItemsSource = _recentMatches;
        SidebarGrip.Target = SidebarColumn;
        foreach (var list in new Control[] { ThumbnailList, OutlineTree, AnnotationList, AttachmentList, SearchResultList })
            ScrollBarHideFix.Attach(list);
        SidebarGrip.Resized += (_, width) => AppState.Preferences.SidebarWidthOverride = width;
        SidebarGrip.DoubleTapped += (_, _) =>
        {
            // Back to the automatic width.
            AppState.Preferences.SidebarWidthOverride = null;
            if (_sidebarOpen) SidebarColumn.Width = new GridLength(SidebarWidth());
        };

        Viewer.CurrentPageChanged += (_, _) => OnCurrentPageChanged();
        Viewer.ZoomChanged += (_, _) => UpdateZoomText();
        Viewer.ViewSettingsChanged += (_, _) => OnViewSettingsChanged();
        Viewer.AnnotationsChanged += (_, page) => OnAnnotationsChanged(page);
        Viewer.EditFailed += (_, message) => ShowMessage("Unable to change annotations", message, InfoBarSeverity.Error);
        Viewer.SearchRequested += async (_, _) => await SearchForSelectionAsync();
        SetupAnnotateBar();

        // The mouse back button leaves Settings, as in other apps.
        SettingsView.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(SettingsView).Properties.IsXButton1Pressed) return;
            CloseSettings();
            e.Handled = true;
        };

        Root.Loaded += (_, _) => _loaded.TrySetResult();
        SearchBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnSearchKeyDown), true);
        RecentSearchBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRecentSearchKeyDown), true);
        // Once the results list is scrolled, results listed after that leave it where it is.
        SearchResultList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => _searchResultAtTop = false), true);
        SearchResultList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _searchResultAtTop = false), true);
        SearchResultList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => _searchResultAtTop = false), true);
        SetupAccelerators();
        // AppState is static: unsubscribed in OnWindowClosed, or it would keep every closed window alive.
        _recentChanged = (_, _) => DispatcherQueue.TryEnqueue(RefreshRecent);
        AppState.RecentChanged += _recentChanged;
        // Dialogs is static too: unsubscribed in OnWindowClosed as well.
        _dialogQueueChanged = (_, _) => UpdateDialogWait();
        Dialogs.QueueChanged += _dialogQueueChanged;
        RefreshRecent();
        UpdateChrome();
        ApplyTheme();
    }

    /// <summary>Applies the theme chosen in Settings.</summary>
    internal void ApplyTheme()
    {
        var theme = AppState.Preferences.Theme;
        Root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        // The caption buttons are drawn by the system, outside the XAML content.
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    /// <summary>Applies the page corner style chosen in Settings.</summary>
    internal void ApplyPageCorners() => Viewer.RefreshPageFrames();

    private double GetDpiScale()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return GetDpiForWindow(hwnd) / 96.0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ================================================================ opening & closing

    /// <summary>
    /// Shows the file at <paramref name="path"/>. Opens are numbered as they're asked for, and the last one asked
    /// for wins: one that finishes loading after a later one has shown its file is dropped. One that's cancelled
    /// or fails leaves the others be, and asking for the file already shown counts as showing it.
    /// </summary>
    public async Task OpenAsync(string path)
    {
        // Already on its way: from a second launch, or a file listed twice.
        if (IsOpening(path)) return;
        int request = ++_openRequests;
        _openingPaths.Add(path);
        _opening++;
        // Not loaded yet means nothing has been drawn: a new window opened for this file.
        if (!_loaded.Task.IsCompleted && _document is null)
        {
            _hideStartPage = true;
            ShowCurrentView();
        }
        try
        {
            await _loaded.Task;
            if (!File.Exists(path))
            {
                ShowMessage("File not found", $"“{Path.GetFileName(path)}” could not be found. It may have been moved or deleted.", InfoBarSeverity.Warning);
                return;
            }
            if (IsShowing(path))
            {
                _openShown = Math.Max(_openShown, request);
                return;
            }
            if (!await ConfirmDiscardChangesAsync()) return;
            // Edits up to here the user has been asked about; ones made while the file loads are asked about again.
            var stamp = TakeEditStamp();

            (PdfDocument? Document, string? Password) loaded;
            _loading++;
            LoadingRing.IsActive = true;
            try
            {
                loaded = await LoadAsync(path, null);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                if (request > _openShown) ShowMessage("Unable to open document", ex.Message, InfoBarSeverity.Error);
                return;
            }
            finally
            {
                if (--_loading == 0) LoadingRing.IsActive = false;
            }
            if (loaded.Document is not { } document) return;
            bool show = false;
            try
            {
                show = await MayShowAsync(request, stamp);
            }
            finally
            {
                // Also when asking about the edits threw, so the loaded file isn't left open.
                if (!show) document.Dispose();
            }
            if (!show) return;
            _openShown = request;
            ShowDocument(document, loaded.Password);
        }
        finally
        {
            _openingPaths.Remove(path);
            _opening--;
            // Missing, unreadable or cancelled: fall back to the start page.
            if (_opening == 0 && _hideStartPage)
            {
                _hideStartPage = false;
                ShowCurrentView();
            }
        }
    }

    /// <summary>How far the document on screen, and what's being typed or drawn on it, had been edited at some point.</summary>
    private readonly record struct EditStamp(PdfDocument? Document, int Edits, int Drafts);

    private EditStamp TakeEditStamp() => new(_document, _document?.EditCount ?? 0, Viewer.DraftVersion);

    /// <summary>Whether the document on screen has unsaved edits made since <paramref name="stamp"/>, which the user hasn't been asked about.</summary>
    private bool EditedSince(EditStamp stamp) =>
        _document is not null && (_document.IsModified || Viewer.HasPendingEdits) && stamp != TakeEditStamp();

    /// <summary>
    /// Whether open <paramref name="request"/> may show its file now: no later one has shown its own, and the user
    /// agreed to lose edits made since <paramref name="stamp"/>, if there are any.
    /// </summary>
    private async Task<bool> MayShowAsync(int request, EditStamp stamp)
    {
        // A closed window has already released its document, and nothing would release this one.
        if (_closed || request < _openShown) return false;
        if (EditedSince(stamp) && !await ConfirmDiscardChangesAsync()) return false;
        // Again, as another may have been shown, or the window closed, while the user was asked.
        return !_closed && request > _openShown;
    }

    /// <summary>
    /// Opens <paramref name="path"/>, asking for a password until it's right. Returns no document
    /// if the user cancelled the password prompt.
    /// </summary>
    private async Task<(PdfDocument? Document, string? Password)> LoadAsync(string path, string? password)
    {
        try
        {
            return (await PdfDocument.OpenAsync(path, password), password);
        }
        catch (PdfPasswordException ex)
        {
            return await UnlockAsync(path, ex.WrongPassword);
        }
    }

    /// <summary>Whether this window shows the file at <paramref name="path"/> (a full path).</summary>
    internal bool IsShowing(string path) => !_closeWhenPrinted && _document is not null && SamePath(path, _document.FilePath);

    /// <summary>Whether this window is opening the file at <paramref name="path"/>, so it'll show it soon.</summary>
    internal bool IsOpening(string path) => !_closeWhenPrinted && _openingPaths.Any(p => SamePath(path, p));

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>On the start page and not about to open anything, so it can take a document opened from outside.</summary>
    internal bool IsIdle => !_closeWhenPrinted && _document is null && _opening == 0;

    internal void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        // Activate doesn't raise a window that's already open behind others. The launch that
        // forwarded the file allowed us to take the foreground, so do it explicitly.
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    internal void ReportUnexpectedError()
    {
        if (Root.XamlRoot is null) return;
        ShowMessage("Something went wrong",
            $"Recto ran into an unexpected problem. If something stops working, save your annotations and restart Recto. Details are in {Path.Combine(AppState.Folder, "errors.log")}.",
            InfoBarSeverity.Error);
    }

    private void ShowDocument(PdfDocument document, string? password, bool reloading = false)
    {
        var previous = _document;
        PersistPosition();
        CancelSearch(clearText: !reloading);
        _annotationScanCts?.Cancel();
        // Its Save as… button is for an attachment of the document being replaced.
        if (Notification.IsOpen && Notification.Title == BlockedAttachmentTitle) Notification.IsOpen = false;

        _document = document;
        _password = password;
        // Attachments opened from another PDF are temporary copies: don't list them as recent.
        bool remember = AppState.Preferences.RememberRecent && !AttachmentFiles.Contains(document.FilePath);
        var existing = remember ? AppState.Find(document.FilePath) : reloading ? _entry : null;
        _entry = existing ?? new RecentDocument { Path = document.FilePath };
        // The start page shows recent documents without asking for passwords, so nothing from
        // inside an encrypted one (its title or a picture of its first page) is kept there.
        bool encrypted = document.Info.IsEncrypted;
        _entry.Title = encrypted || string.IsNullOrWhiteSpace(document.Info.Title) ? "" : document.Info.Title;
        _entry.LastOpened = DateTimeOffset.Now;
        if (remember && encrypted) AppState.DeleteThumbnail(document.FilePath);
        if (remember) AppState.Touch(_entry);

        _settingsOpen = false;
        StopReloading();
        ShowCurrentView();
        // Size the sidebar for this document's thumbnails before the viewer lays out, so its
        // viewport (and the fit-width zoom the first pages render at) doesn't change afterwards.
        _thumbnailRotation = existing?.Rotation ?? 0;
        SetSidebarOpen(AppState.Preferences.SidebarOpen, persist: false);
        Root.UpdateLayout();

        Viewer.SetDocument(document, existing);
        if (previous is not null) ReleaseDocument(previous);

        UpdateChrome();
        UpdateTitle();
        UpdateZoomText();
        BuildThumbnails();
        LoadOutline();
        LoadAttachments();
        _annotations.Clear();
        _annotationItems.Clear();
        _annotationsScanned = false;
        SelectSidebarTab(AppState.Preferences.SidebarPage);
        WatchFile(document);
        if (remember && !encrypted) _ = SaveRecentThumbnailAsync(document);
        Viewer.Focus(FocusState.Programmatic);
    }

    public async void CloseDocument()
    {
        // Without recent documents the start page has nothing to offer, so the window goes as well.
        if (!AppState.Preferences.RememberRecent)
        {
            await CloseWindowAsync();
            return;
        }
        if (_document is null) return;
        if (!await ConfirmDiscardChangesAsync()) return;
        if (_presenting) SetPresenting(false);
        if (_fullscreen) SetFullscreen(false);
        PersistPosition();
        CancelSearch(clearText: true);
        _annotationScanCts?.Cancel();
        _watcher?.Dispose();
        _watcher = null;
        StopReloading();
        var document = _document;
        _document = null;
        _password = null;
        _entry = null;
        Viewer.SetDocument(null);
        ReleaseDocument(document);
        ClearThumbnails();
        OutlineTree.RootNodes.Clear();
        AttachmentList.ItemsSource = null;
        if (Notification.IsOpen && Notification.Title == BlockedAttachmentTitle) Notification.IsOpen = false;
        _annotationItems.Clear();
        ShowCurrentView();
        UpdateChrome();
        UpdateTitle();
        RefreshRecent();
    }

    private const string ReloadFailedTitle = "Unable to reload document";
    /// <summary>About 30 seconds of retries, at the reload delay.</summary>
    private const int MaxReloadRetries = 50;

    /// <summary>
    /// Opens the file again in place of the document. <paramref name="automatic"/>: because it changed on disk,
    /// so while another app still has it open (most likely still writing it), this tries again shortly instead.
    /// </summary>
    private async Task ReloadAsync(bool automatic = false)
    {
        if (_document is null) return;
        var current = _document;
        string path = current.FilePath;
        // Edits up to here the user agreed to lose (an automatic reload only starts without any); ones made while
        // the file loads they haven't.
        var stamp = TakeEditStamp();
        PersistPosition();
        PdfDocument? document;
        string? password;
        try
        {
            // Encrypted files reopen with the password from this session; if that no longer works, ask again.
            (document, password) = await LoadAsync(path, _password);
        }
        catch (IOException ex) when (automatic && IsInUse(ex) && _reloadRetries < MaxReloadRetries)
        {
            // Unless another document has taken this one's place meanwhile.
            if (_document != current) return;
            _reloadRetries++;
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (_document != current) return;
            _reloadRetries = 0;
            ShowMessage(ReloadFailedTitle, ex.Message, InfoBarSeverity.Error);
            return;
        }
        if (document is null) return;
        bool replace = _document == current;
        if (replace && EditedSince(stamp))
        {
            // An automatic reload offers itself with a warning instead; one the user asked for asks about the edits.
            try
            {
                replace = !automatic && await ConfirmDiscardChangesAsync() && _document == current;
            }
            catch
            {
                document.Dispose();
                throw;
            }
            if (automatic) OnFileChangedOnDisk();
        }
        if (replace && IsDocumentInUse)
        {
            // Settings or a print dialog opened while it loaded: done again once they close.
            replace = false;
            _reloadPending = true;
        }
        if (!replace)
        {
            document.Dispose();
            return;
        }
        if (Notification.IsOpen && Notification.Title == ReloadFailedTitle) Notification.IsOpen = false;
        // Showing the document cancels the search, query included.
        string query = _searchQuery;
        ShowDocument(document, password, reloading: true);
        if (!string.IsNullOrEmpty(query)) StartSearch(query);
    }

    /// <summary>
    /// Whether Settings or a print dialog has the document: the viewer is hidden behind Settings and has no size to
    /// lay out a reloaded document in, and reloading would close the document a print dialog is previewing or printing.
    /// </summary>
    private bool IsDocumentInUse => _settingsOpen || _printing > 0;

    /// <summary>Drops reloads waiting for the document on screen, when it's replaced or closed.</summary>
    private void StopReloading()
    {
        _reloadDebounce.Stop();
        _reloadRetries = 0;
        _reloadPending = false;
    }

    /// <summary>Whether a file couldn't be opened because another process has it open (ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION).</summary>
    private static bool IsInUse(IOException ex) => ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);

    /// <summary>Checks the file again if it changed while Settings or a print dialog was open.</summary>
    private void CheckPendingReload()
    {
        if (!_reloadPending) return;
        _reloadPending = false;
        OnFileChangedOnDisk();
    }

    private void PersistPosition()
    {
        if (_entry is null || _document is null) return;
        _entry.Page = Viewer.CurrentPage;
        _entry.ZoomMode = Viewer.ZoomMode;
        _entry.Zoom = Viewer.Zoom;
        _entry.Rotation = Viewer.PageRotation;
        if (!Viewer.IsPresenting)
        {
            _entry.Continuous = Viewer.IsContinuous;
            _entry.Dual = Viewer.IsDual;
        }
        AppState.Save();
    }

    private void WatchFile(PdfDocument document)
    {
        _watcher?.Dispose();
        _watcher = null;
        string path = document.FilePath;
        // Events already queued when the watcher goes are for that document, not the next.
        void Changed()
        {
            if (_document != document) return;
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        }
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is null) return;
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => DispatcherQueue.TryEnqueue(Changed);
            _watcher.Renamed += (_, _) => DispatcherQueue.TryEnqueue(Changed);
            // A file deleted and replaced by one moved in from another folder raises only Created.
            _watcher.Created += (_, _) => DispatcherQueue.TryEnqueue(Changed);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
    }

    private void OnFileChangedOnDisk()
    {
        if (_document is null || DateTime.UtcNow < _ignoreWatcherUntil || !File.Exists(_document.FilePath)) return;
        if (IsDocumentInUse)
        {
            _reloadPending = true;
            return;
        }
        if (!_document.IsModified && !Viewer.HasPendingEdits)
        {
            _ = ReloadAsync(automatic: true);
            return;
        }
        var document = _document;
        var reload = new Button { Content = "Reload" };
        reload.Click += async (_, _) =>
        {
            Notification.IsOpen = false;
            if (_document == document) await ReloadAsync();
        };
        ShowMessage("The document changed on disk", "Reloading will discard your unsaved annotations.", InfoBarSeverity.Warning, reload);
    }

    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        // A note still being typed counts as an unsaved change.
        await Viewer.CommitPendingEditsAsync();
        // A note whose text couldn't be written is still pending.
        if (_document is null || !_document.IsModified && !Viewer.HasPendingEdits) return true;
        var dialog = Dialogs.Create(Root.XamlRoot);
        dialog.Title = "Save changes to a copy?";
        dialog.Content = $"“{Path.GetFileName(_document.FilePath)}” has unsaved annotations. If you don't save a copy, they will be lost.";
        dialog.PrimaryButtonText = "Save a copy";
        dialog.SecondaryButtonText = "Don't save";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        var result = await Dialogs.ShowAsync(dialog);
        if (result == ContentDialogResult.None) return false;
        if (result == ContentDialogResult.Primary) return await SaveCopyAsync();
        return true;
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose) return;
        if (_document is { IsModified: true } || Viewer.HasPendingEdits)
        {
            args.Cancel = true;
            await CloseWindowAsync();
            return;
        }
        SaveWindowState();
        PersistPosition();
        if (_printing > 0)
        {
            args.Cancel = true;
            CloseWhenPrinted();
        }
    }

    /// <summary>Closes the window, offering to save unsaved annotations first.</summary>
    private async Task CloseWindowAsync()
    {
        if (!await ConfirmDiscardChangesAsync()) return;
        // Close() skips OnAppWindowClosing, so remember the window and position now.
        SaveWindowState();
        PersistPosition();
        if (_printing > 0)
        {
            CloseWhenPrinted();
            return;
        }
        _forceClose = true;
        Close();
    }

    /// <summary>
    /// Closing the window while a job is being sent would cut it off: the document goes, and with the last window
    /// the process. So the window goes out of sight and closes once the job has been sent (see <see cref="EndPrinting"/>).
    /// Its dialogs close or are dropped now, except Recto's print dialog while it's sending, which closes when that's done.
    /// </summary>
    private void CloseWhenPrinted()
    {
        _closeWhenPrinted = true;
        if (App.LastActiveWindow == this) App.LastActiveWindow = null;
        var sending = _printDialog is { IsSending: true } dialog ? dialog : null;
        sending?.CloseWhenDone();
        if (Root.XamlRoot is { } root) Dialogs.WindowHidden(root, sending);
        AppWindow.Hide();
    }

    /// <summary>Closes a document that's no longer shown, once no print job can be reading it.</summary>
    private void ReleaseDocument(PdfDocument document)
    {
        if (_printing > 0) _releaseWhenPrinted.Add(document);
        else document.Dispose();
    }

    /// <summary>Ends a print counted in <see cref="_printing"/>. False if the window was waiting for it to close, and now has.</summary>
    private bool EndPrinting()
    {
        if (--_printing > 0) return true;
        foreach (var document in _releaseWhenPrinted) document.Dispose();
        _releaseWhenPrinted.Clear();
        if (!_closeWhenPrinted) return true;
        _forceClose = true;
        Close();
        return false;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != WindowActivationState.Deactivated) App.LastActiveWindow = this;
        else
        {
            Viewer.StopMiddleScroll();
            Viewer.StopKeyScroll();
        }
    }

    /// <summary>Releases everything that would otherwise outlive the window.</summary>
    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        _closed = true;
        // A dialog left open here would hold up those of every other window.
        if (Root.XamlRoot is { } root) Dialogs.WindowClosed(root);
        AppState.RecentChanged -= _recentChanged;
        Dialogs.QueueChanged -= _dialogQueueChanged;
        App.OpenWindows.Remove(this);
        if (App.LastActiveWindow == this) App.LastActiveWindow = null;
        // The window, its AppWindow and the timers aren't XAML elements, so the garbage collector can't trace
        // a cycle through their handlers. Unsubscribe so they can't keep the closed window alive.
        Closed -= OnWindowClosed;
        Activated -= OnActivated;
        AppWindow.Closing -= OnAppWindowClosing;
        _searchDebounce.Stop();
        _searchDebounce.Tick -= OnSearchDebounceTick;
        _reloadDebounce.Stop();
        _reloadDebounce.Tick -= OnReloadDebounceTick;
        _hintTimer.Stop();
        _hintTimer.Tick -= OnHintTimerTick;
        _thumbnailSyncThrottle.Stop();
        _thumbnailSyncThrottle.Tick -= OnThumbnailSyncThrottleTick;
        _searchCts?.Cancel();
        _searchTextCts?.Cancel();
        _annotationScanCts?.Cancel();
        _watcher?.Dispose();
        _watcher = null;
        // Page and thumbnail bitmaps are only freed when released, not when the window is collected.
        Viewer.SetDocument(null);
        Viewer.Detach();
        ClearThumbnails();
        _document?.Dispose();
        _document = null;
        _password = null;
        foreach (var document in _releaseWhenPrinted) document.Dispose();
        _releaseWhenPrinted.Clear();
        PrintService.Release(this);
        foreach (var dir in _attachmentFolders) AttachmentFiles.Delete(dir);
        _attachmentFolders.Clear();
        // The process may exit with this window; finish writing the position saved while closing.
        AppState.Flush();
        // WinUI keeps the title bar's header content alive after the window closes, and the Click handlers
        // in it lead back here, so the whole window would never be collected. Detaching the header frees it.
        AppTitleBar.RightHeader = null;
    }

    private void OnSearchDebounceTick(DispatcherQueueTimer sender, object args)
    {
        // A space typed after the query leaves it as it was: searching again would move back to the page's first result.
        if (_searchQuery.Length > 0 && SearchBox.Text.Trim() == _searchQuery && CurrentSearchOptions() == _searchOptions) return;
        StartSearch(SearchBox.Text);
    }
    private void OnReloadDebounceTick(DispatcherQueueTimer sender, object args) => OnFileChangedOnDisk();
    private void OnHintTimerTick(DispatcherQueueTimer sender, object args) => FullscreenHint.Opacity = 0;

    /// <summary>Takes over a folder of extracted attachments, deleting it when this window closes.</summary>
    internal void AdoptAttachmentFolder(string dir) => _attachmentFolders.Add(dir);

    private void SaveWindowState()
    {
        var prefs = AppState.Preferences;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
        {
            prefs.WindowMaximized = true;
        }
        else if (!_fullscreen)
        {
            prefs.WindowMaximized = false;
            double scale = GetDpiScale();
            prefs.WindowWidth = (int)(AppWindow.Size.Width / scale);
            prefs.WindowHeight = (int)(AppWindow.Size.Height / scale);
        }
        AppState.Save();
    }

    /// <summary>
    /// Asks for the password of the protected file at <paramref name="path"/>, keeping the prompt open
    /// until the password is right. Returns no document if the user cancelled.
    /// </summary>
    private async Task<(PdfDocument? Document, string? Password)> UnlockAsync(string path, bool wrongPassword)
    {
        var box = new PasswordBox { PlaceholderText = "Password" };
        var wrong = new TextBlock
        {
            Text = "The password is incorrect. Try again.",
            Style = (Style)Application.Current.Resources["CriticalTextBlockStyle"],
            Visibility = wrongPassword ? Visibility.Visible : Visibility.Collapsed,
        };
        // Fixed width so a long file name wraps instead of widening the dialog past the password box.
        var panel = new StackPanel { Spacing = 12, Width = 360 };
        panel.Children.Add(new TextBlock { Text = $"“{Path.GetFileName(path)}” is protected. Enter the password to open it.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(wrong);
        var dialog = Dialogs.Create(Root.XamlRoot);
        dialog.Title = "Password required";
        dialog.Content = panel;
        dialog.PrimaryButtonText = "Unlock";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;

        PdfDocument? document = null;
        string? password = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        bool checking = false, dismissed = false;
        // Whether the dialog can close: unlocked, or failed for a reason other than the password.
        async Task<bool> TryUnlockAsync()
        {
            checking = true;
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                password = box.Password;
                var opened = await PdfDocument.OpenAsync(path, password);
                // The window closed while it opened, taking the dialog with it: nothing will show it.
                if (dismissed) opened.Dispose();
                else document = opened;
                return true;
            }
            catch (PdfPasswordException)
            {
                wrong.Visibility = Visibility.Visible;
                box.SelectAll();
                box.Focus(FocusState.Programmatic);
                return false;
            }
            catch (Exception ex)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                return true;
            }
            finally
            {
                checking = false;
                dialog.IsPrimaryButtonEnabled = true;
            }
        }

        dialog.PrimaryButtonClick += async (_, e) =>
        {
            if (checking)
            {
                e.Cancel = true;
                return;
            }
            var deferral = e.GetDeferral();
            e.Cancel = !await TryUnlockAsync();
            deferral.Complete();
        };
        box.KeyDown += async (_, e) =>
        {
            if (e.Key != VirtualKey.Enter || checking) return;
            e.Handled = true;
            if (await TryUnlockAsync()) dialog.Hide();
        };
        // Don't let Cancel or Esc drop a document that's still opening, unless the window has closed: the dialog
        // would stay open for good, holding up every other window's dialogs. The open then shows nothing.
        dialog.Closing += (_, e) => e.Cancel |= checking && !_closed;
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        await Dialogs.ShowAsync(dialog);
        dismissed = true;
        failure?.Throw();
        return document is null ? (null, null) : (document, password);
    }

    // ================================================================ chrome

    private void UpdateChrome()
    {
        bool showingDocument = _document is not null && !_settingsOpen;
        bool searching = SearchPanel.Visibility == Visibility.Visible;
        var visibility = showingDocument ? Visibility.Visible : Visibility.Collapsed;
        ZoomControls.Visibility = visibility;
        HeaderSeparator.Visibility = visibility;
        AnnotateToggle.Visibility = visibility;
        if (!showingDocument) SetAnnotateOpen(false);
        AppTitleBar.IsPaneToggleButtonVisible = showingDocument && !searching;
        AppTitleBar.IsBackButtonVisible = _settingsOpen || showingDocument && searching;
        foreach (var item in new MenuFlyoutItemBase[]
                 {
                     DocumentSeparator, SaveCopyItem, PrintItem, ViewSeparator, ContinuousItem, DualItem, OddLeftItem,
                     InvertItem, RotateItem, ModeSeparator, FullscreenItem, PresentItem, InfoSeparator, ReloadItem,
                     ShowInFolderItem, PropertiesItem, CloseSeparator, CloseItem,
                 })
        {
            item.Visibility = visibility;
        }
    }

    private void UpdateTitle()
    {
        if (_document is null)
        {
            AppTitleBar.Title = "Recto";
            AppTitleBar.Subtitle = "";
            Title = "Recto";
            return;
        }
        string name = DisplayName(_document);
        AppTitleBar.Title = name;
        AppTitleBar.Subtitle = _document.IsModified ? "Edited" : "";
        AppTitleBar.InvalidateMeasure();
        Title = $"{(_document.IsModified ? "• " : "")}{name} – Recto";
    }

    private static string DisplayName(PdfDocument document)
    {
        var title = document.Info.Title;
        // Many generators write junk titles such as "untitled" or "Microsoft Word - foo.docx".
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || title.Equals("untitled", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("Microsoft Word", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(document.FilePath);
        return title;
    }

    private void OnCurrentPageChanged()
    {
        if (_document is null) return;
        ThrottledSyncThumbnailSelection();
    }

    private void UpdateZoomText()
    {
        ZoomButton.Content = $"{Math.Round(Viewer.Zoom * 100):0}%";
        FitPageItem.IsChecked = Viewer.ZoomMode == ZoomMode.FitPage;
        FitWidthItem.IsChecked = Viewer.ZoomMode == ZoomMode.FitWidth;
    }

    private void OnViewSettingsChanged()
    {
        var prefs = AppState.Preferences;
        if (!Viewer.IsPresenting)
        {
            prefs.Continuous = Viewer.IsContinuous;
            prefs.Dual = Viewer.IsDual;
        }
        prefs.OddPagesLeft = Viewer.OddPagesLeft;
        prefs.Inverted = Viewer.IsInverted;
        if (_thumbnails.Count > 0 && (_thumbnailRotation != Viewer.PageRotation || _thumbnailInverted != Viewer.IsInverted)) BuildThumbnails();
        UpdateZoomText();
    }

    private void OnMainMenuOpening(object? sender, object e)
    {
        ContinuousItem.IsChecked = Viewer.IsContinuous;
        DualItem.IsChecked = Viewer.IsDual;
        OddLeftItem.IsChecked = Viewer.OddPagesLeft;
        OddLeftItem.IsEnabled = Viewer.IsDual;
        InvertItem.IsChecked = Viewer.IsInverted;
        CloseItem.Text = AppState.Preferences.RememberRecent ? "Close document" : "Close window";
        FullscreenItem.Text = _fullscreen ? "Exit full screen" : "Full screen";
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity, ButtonBase? action = null)
    {
        Notification.Title = title;
        Notification.Message = message;
        Notification.Severity = severity;
        Notification.ActionButton = action;
        Notification.IsOpen = true;
    }

    /// <summary>
    /// Says so while a dialog of this window waits for one in another window, which could be out of sight
    /// (behind this one, or on another screen): Windows shows one dialog at a time for all of Recto's windows.
    /// </summary>
    private void UpdateDialogWait()
    {
        var root = Root.XamlRoot;
        DialogWaitBar.IsOpen = root is not null && Dialogs.IsWaitingForOtherWindow(root);
    }

    /// <summary>Brings forward the window with the dialog this one's is waiting for.</summary>
    private void OnShowWaitingDialog(object sender, RoutedEventArgs e)
    {
        var open = Dialogs.OpenRoot;
        App.OpenWindows.FirstOrDefault(w => w != this && w.Root.XamlRoot == open)?.BringToFront();
    }

    private void ShowHint(string text)
    {
        FullscreenHintText.Text = text;
        FullscreenHint.Opacity = 1;
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    // ================================================================ sidebar

    private void OnPaneToggleRequested(TitleBar sender, object args) => SetSidebarOpen(!_sidebarOpen);

    // In Settings, and while searching, the pane toggle becomes a back button that leaves them.
    private void OnTitleBarBackRequested(TitleBar sender, object args)
    {
        if (_settingsOpen)
        {
            CloseSettings();
            return;
        }
        CancelSearch(clearText: true);
        Viewer.Focus(FocusState.Programmatic);
    }

    private void SetSidebarOpen(bool open, bool persist = true)
    {
        _sidebarOpen = open;
        Sidebar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidebarGrip.Visibility = Sidebar.Visibility;
        SidebarColumn.Width = open ? new GridLength(SidebarWidth()) : new GridLength(0);
        DocumentCard.CornerRadius = open ? new CornerRadius(8, 0, 0, 0) : new CornerRadius(0);
        DocumentCard.BorderThickness = open ? new Thickness(1, 1, 0, 0) : new Thickness(0, 1, 0, 0);
        if (persist) AppState.Preferences.SidebarOpen = open;
        if (open) SyncThumbnailSelection();
    }

    /// <summary>The user's chosen width, or by default just wide enough for the page thumbnails.</summary>
    private double SidebarWidth()
    {
        if (AppState.Preferences.SidebarWidthOverride is { } width) return Math.Clamp(width, 150, 520);
        double thumbnail = _document is { PageCount: > 0 } document ? ThumbnailSizes(document.PageSizes, _thumbnailRotation).Max(s => s.Width) : ThumbnailWidth * 0.82;
        // Item padding (10 on each side) plus a roomy gutter on each side of the list.
        return Math.Clamp(thumbnail + 20 + 2 * 28, 150, 520);
    }

    private void SelectSidebarTab(int index)
    {
        var tabs = new[] { ThumbnailsTab, OutlineTab, AnnotationsTab, AttachmentsTab };
        var tab = tabs[Math.Clamp(index, 0, tabs.Length - 1)];
        if (tab.Visibility != Visibility.Visible) tab = ThumbnailsTab;
        SidebarSelector.SelectedItem = tab;
        ShowSidebarPage(tab);
    }

    private void OnSidebarSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is { } item) ShowSidebarPage(item);
    }

    private void ShowSidebarPage(SelectorBarItem tab)
    {
        ThumbnailList.Visibility = tab == ThumbnailsTab ? Visibility.Visible : Visibility.Collapsed;
        OutlineTree.Visibility = tab == OutlineTab ? Visibility.Visible : Visibility.Collapsed;
        AnnotationsPanel.Visibility = tab == AnnotationsTab ? Visibility.Visible : Visibility.Collapsed;
        AttachmentList.Visibility = tab == AttachmentsTab ? Visibility.Visible : Visibility.Collapsed;
        AppState.Preferences.SidebarPage = tab == OutlineTab ? 1 : tab == AnnotationsTab ? 2 : tab == AttachmentsTab ? 3 : 0;
        if (tab == AnnotationsTab) ScanAnnotations();
        if (tab == ThumbnailsTab) SyncThumbnailSelection();
    }

    // ---------------------------------------------------------------- thumbnails

    private const double ThumbnailWidth = 128;
    private int _thumbnailRotation;
    private bool _thumbnailInverted;

    private void BuildThumbnails()
    {
        if (_document is null) return;
        ClearThumbnails();
        _thumbnailRotation = Viewer.PageRotation;
        _thumbnailInverted = Viewer.IsInverted;
        var paper = new SolidColorBrush(_thumbnailInverted ? Windows.UI.Color.FromArgb(255, 30, 30, 30) : Colors.White);
        var sizes = ThumbnailSizes(_document.PageSizes, _thumbnailRotation);
        _thumbnails = Enumerable.Range(0, _document.PageCount).Select(i =>
        {
            var (width, height) = sizes[i];
            return new ThumbnailItem { Index = i, Label = _document.GetPageLabel(i), Width = width, Height = height, Paper = paper };
        }).ToList();
        ThumbnailList.ItemsSource = _thumbnails;
        if (_sidebarOpen) SidebarColumn.Width = new GridLength(SidebarWidth());
        SyncThumbnailSelection();
    }

    /// <summary>Empties the thumbnail list and releases the previews in it.</summary>
    private void ClearThumbnails()
    {
        var old = _thumbnails;
        _thumbnails = [];
        ThumbnailList.ItemsSource = null;
        foreach (var t in old) ReleaseThumbnail(t);
    }

    private static void ReleaseThumbnail(ThumbnailItem item)
    {
        item.Pending?.Cancel();
        SetThumbnailImage(item, null);
    }

    /// <summary>Shows <paramref name="image"/> as the preview and releases the one it replaces.</summary>
    private static void SetThumbnailImage(ThumbnailItem item, ImageSource? image)
    {
        var old = item.Image;
        item.Image = image;
        (old as IDisposable)?.Dispose();
    }

    /// <summary>
    /// One scale for every page, so mixed page sizes keep their proportions: portrait pages would
    /// be <see cref="ThumbnailWidth"/> * 0.82 wide and landscape ones <see cref="ThumbnailWidth"/>,
    /// and whichever page needs the smallest scale for that sets it for all.
    /// </summary>
    private static (double Width, double Height)[] ThumbnailSizes(IReadOnlyList<Windows.Foundation.Size> sizes, int rotation)
    {
        if (sizes.Count == 0) return [];
        bool swap = rotation is 90 or 270;
        var pages = sizes.Select(s => swap ? (W: s.Height, H: s.Width) : (W: s.Width, H: s.Height)).ToArray();
        double scale = pages.Min(p => (p.W >= p.H ? ThumbnailWidth : ThumbnailWidth * 0.82) / p.W);
        return pages.Select(p =>
        {
            // One giant page shouldn't shrink the rest to specks.
            double k = Math.Max(scale, ThumbnailWidth * 0.25 / Math.Max(p.W, p.H));
            // Extremely long or thin pages get a cropped thumbnail rather than a giant bitmap.
            return (Math.Max(4, Math.Round(p.W * k)), Math.Clamp(Math.Round(p.H * k), 4, ThumbnailWidth * 3));
        }).ToArray();
    }

    private void OnThumbnailContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ThumbnailItem item) return;
        if (args.InRecycleQueue)
        {
            ReleaseThumbnail(item);
            return;
        }
        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(1, (_, a) => RenderThumbnail((ThumbnailItem)a.Item));
        }
    }

    /// <summary>
    /// Draws the page at the screen's resolution first, then again at twice that, which the image
    /// samples down 2:1 for a crisper result. The second pass is only picked up when nothing else is
    /// queued, but renders aren't preempted: one that has started still finishes before a page or a
    /// newly scrolled-in preview gets the PDFium thread.
    /// </summary>
    private async void RenderThumbnail(ThumbnailItem item)
    {
        if (_document is null || item.Image is not null) return;
        var document = _document;
        item.Pending?.Cancel();
        var cts = item.Pending = new CancellationTokenSource();
        double raster = Root.XamlRoot?.RasterizationScale ?? 1;
        int rotation = _thumbnailRotation;
        bool inverted = _thumbnailInverted;
        var size = document.PageSizes[item.Index];
        bool swap = rotation is 90 or 270;
        double w = swap ? size.Height : size.Width, h = swap ? size.Width : size.Height;
        // The image sits inside the 1-DIP hairline; a bitmap sized for the outer box gets squeezed into it and blurs.
        double width = item.Width - 2, height = item.Height - 2;
        // Cover the slot rather than fit its width: rounding can leave the page a fraction short of
        // the slot's height, and Stretch="Fill" would stretch the white row that leaves.
        double fit = Math.Max(width / w, height / h);
        try
        {
            foreach (var (supersample, priority) in new[] { (1, WorkPriority.Background), (2, WorkPriority.Idle) })
            {
                double px = raster * supersample;
                var region = new RectInt32(0, 0, Math.Max(1, (int)Math.Round(width * px)), Math.Max(1, (int)Math.Round(height * px)));
                using var buffer = await document.RenderAsync(item.Index, fit * px, rotation, region, inverted, priority, cts.Token);
                if (buffer is null || cts.IsCancellationRequested || document != _document) return;
                var image = await PageView.ToSourceAsync(buffer);
                if (cts.IsCancellationRequested || document != _document)
                {
                    image.Dispose();
                    return;
                }
                SetThumbnailImage(item, image);
            }
        }
        // COMException: the upload failed, so the slot keeps its blank page (or the first pass).
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidDataException or COMException)
        {
        }
    }

    /// <summary>
    /// Follows the current page in the thumbnails at most every 150 ms: scrolling them realizes a
    /// screenful of previews, which dragging the document's scroll bar used to do on every frame.
    /// </summary>
    private void ThrottledSyncThumbnailSelection()
    {
        if (_thumbnailSyncThrottle.IsRunning)
        {
            _thumbnailSyncPending = true;
            return;
        }
        SyncThumbnailSelection();
        _thumbnailSyncThrottle.Start();
    }

    private void OnThumbnailSyncThrottleTick(DispatcherQueueTimer sender, object args)
    {
        if (!_thumbnailSyncPending) return;
        _thumbnailSyncPending = false;
        SyncThumbnailSelection();
        _thumbnailSyncThrottle.Start();
    }

    private void SyncThumbnailSelection()
    {
        if (_document is null || _thumbnails.Count == 0) return;
        int page = Viewer.CurrentPage;
        if (page >= _thumbnails.Count) return;
        _syncingThumbnails = true;
        ThumbnailList.SelectedIndex = page;
        _syncingThumbnails = false;
        foreach (var t in _thumbnails) t.IsCurrent = t.Index == page;
        if (_sidebarOpen && ThumbnailList.Visibility == Visibility.Visible)
            ThumbnailList.ScrollIntoView(_thumbnails[page]);
    }

    private void OnThumbnailClick(object sender, ItemClickEventArgs e)
    {
        if (_syncingThumbnails || e.ClickedItem is not ThumbnailItem item) return;
        Viewer.GoToPage(item.Index);
        Viewer.Focus(FocusState.Programmatic);
    }

    // ---------------------------------------------------------------- outline

    private async void LoadOutline()
    {
        OutlineTree.RootNodes.Clear();
        _hasOutline = false;
        OutlineTab.Visibility = Visibility.Collapsed;
        if (_document is null) return;
        var document = _document;
        try
        {
            var outline = await document.GetOutlineAsync();
            if (document != _document) return;
            int total = 0;
            TreeViewNode Build(OutlineItem item)
            {
                total++;
                string page = item.Destination is { IsInternal: true } d ? document.GetPageLabel(d.PageIndex) : "";
                var node = new TreeViewNode { Content = new OutlineEntry(item.Title, page, item.Destination) };
                foreach (var child in item.Children) node.Children.Add(Build(child));
                return node;
            }
            foreach (var item in outline) OutlineTree.RootNodes.Add(Build(item));
            if (total <= 40)
            {
                foreach (var node in OutlineTree.RootNodes) node.IsExpanded = true;
            }
            _hasOutline = outline.Count > 0;
            OutlineTab.Visibility = _hasOutline ? Visibility.Visible : Visibility.Collapsed;
            if (_hasOutline && AppState.Preferences.SidebarPage == 1) SelectSidebarTab(1);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public static string OutlineTitle(object content) => (content as OutlineEntry)?.Title ?? "";
    public static string OutlinePage(object content) => (content as OutlineEntry)?.Page ?? "";

    private void OnOutlineInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode { Content: OutlineEntry { Destination: { } destination } })
        {
            Viewer.GoToDestination(destination);
        }
    }

    // ---------------------------------------------------------------- annotations

    private async void ScanAnnotations()
    {
        if (_document is null || _annotationsScanned) return;
        _annotationsScanned = true;
        _annotationScanCts?.Cancel();
        var cts = _annotationScanCts = new CancellationTokenSource();
        var document = _document;
        try
        {
            var all = await document.GetAllAnnotationsAsync(cts.Token);
            if (document != _document || cts.IsCancellationRequested) return;
            _annotations.Clear();
            foreach (var group in all.GroupBy(a => a.PageIndex)) _annotations[group.Key] = [.. group];
            RefreshAnnotationList();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async void OnAnnotationsChanged(int page)
    {
        UpdateTitle();
        if (_document is null) return;
        var document = _document;
        try
        {
            var text = await document.GetPageTextAsync(page, WorkPriority.Visible);
            if (document != _document) return;
            _annotations[page] = text.Annotations;
            if (_annotationsScanned) RefreshAnnotationList();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RefreshAnnotationList()
    {
        if (_document is null) return;
        _annotationItems.Clear();
        foreach (var page in _annotations.Keys.Order())
        {
            foreach (var a in _annotations[page])
            {
                if (a.Kind == AnnotationKind.Attachment) continue;
                _annotationItems.Add(new AnnotationItem(a, _document.GetPageLabel(page)));
            }
        }
        AnnotationsEmpty.Visibility = _annotationItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAnnotationClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AnnotationItem item) Viewer.RevealAnnotation(item.Annotation);
    }

    private void OnAnnotationRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AnnotationItem item } element) return;
        var menu = new MenuFlyout();
        var show = new MenuFlyoutItem { Text = "Show", Icon = new FontIcon { Glyph = "\uE8A7" } };
        show.Click += (_, _) => Viewer.RevealAnnotation(item.Annotation);
        var delete = new MenuFlyoutItem { Text = "Delete", Icon = new FontIcon { Glyph = "\uE74D" } };
        delete.Click += (_, _) => Viewer.DeleteAnnotation(item.Annotation);
        menu.Items.Add(show);
        menu.Items.Add(delete);
        menu.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    // ---------------------------------------------------------------- attachments

    private async void LoadAttachments()
    {
        AttachmentList.ItemsSource = null;
        _hasAttachments = false;
        AttachmentsTab.Visibility = Visibility.Collapsed;
        if (_document is null) return;
        var document = _document;
        try
        {
            var attachments = await document.GetAttachmentsAsync();
            if (document != _document) return;
            AttachmentList.ItemsSource = attachments.Select(a => new AttachmentItem(document, a)).ToList();
            _hasAttachments = attachments.Count > 0;
            AttachmentsTab.Visibility = _hasAttachments ? Visibility.Visible : Visibility.Collapsed;
            if (_hasAttachments && AppState.Preferences.SidebarPage == 3) SelectSidebarTab(3);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnAttachmentClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AttachmentItem item) OpenAttachment(item);
    }

    private void OnAttachmentRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AttachmentItem item } element) return;
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open", Icon = new FontIcon { Glyph = "\uE8E5" } };
        open.Click += (_, _) => OpenAttachment(item);
        var save = new MenuFlyoutItem { Text = "Save as…", Icon = new FontIcon { Glyph = "\uE792" } };
        save.Click += (_, _) => SaveAttachment(item);
        menu.Items.Add(open);
        menu.Items.Add(save);
        menu.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private const string BlockedAttachmentTitle = "Recto won't open this attachment";
    private const string AttachmentDocumentChanged = "The document was reloaded or closed meanwhile. Try again from its attachments.";

    private async void OpenAttachment(AttachmentItem item)
    {
        if (item.Document != _document) return;
        var document = item.Document;
        string name = LaunchPolicy.SafeFileName(item.Name);
        var risk = LaunchPolicy.Classify(name);
        if (risk == AttachmentRisk.Blocked)
        {
            var save = new Button { Content = "Save as…" };
            save.Click += (_, _) =>
            {
                Notification.IsOpen = false;
                SaveAttachment(item);
            };
            string saveName = LaunchPolicy.SaveFileName(name);
            ShowMessage(BlockedAttachmentTitle,
                saveName == name
                    ? $"“{name}” is a program, script or shortcut that could harm your computer. You can save it and check it before opening it."
                    : $"“{name}” is a shortcut that could harm your computer, even unopened. It can be saved as “{saveName}” so Windows ignores it.",
                InfoBarSeverity.Warning, save);
            return;
        }
        if (risk == AttachmentRisk.Ask && !await ConfirmOpenAttachmentAsync(name)) return;
        if (document != _document)
        {
            ShowMessage("Unable to open attachment", AttachmentDocumentChanged, InfoBarSeverity.Warning);
            return;
        }
        try
        {
            var data = await document.GetAttachmentDataAsync(item.Attachment);
            string dir = AttachmentFiles.CreateFolder();
            // Under the name it would be saved as, so a desktop.ini doesn't customize the folder it's in.
            string path = Path.Combine(dir, LaunchPolicy.SaveFileName(name));
            await File.WriteAllBytesAsync(path, data);
            // Without the mark, the app that opens it can't tell it came from a document off the internet.
            if (!LaunchPolicy.MarkAsUntrusted(path) && risk != AttachmentRisk.Pdf)
            {
                AttachmentFiles.Delete(dir);
                ShowMessage("Unable to open attachment",
                    $"Windows couldn't mark “{name}” as coming from the internet, so Recto won't open it. Try saving it instead.",
                    InfoBarSeverity.Warning);
                return;
            }
            if (risk == AttachmentRisk.Pdf)
            {
                var window = new MainWindow();
                window.AdoptAttachmentFolder(dir);
                window.Activate();
                await window.OpenAsync(path);
                return;
            }
            _attachmentFolders.Add(dir);
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            if (!await Launcher.LaunchFileAsync(file, new LauncherOptions { DisplayApplicationPicker = false }))
                ShowMessage("Unable to open attachment", $"Windows couldn't open “{name}”. Try saving it instead.", InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or COMException)
        {
            ShowMessage("Unable to open attachment", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task<bool> ConfirmOpenAttachmentAsync(string name)
    {
        var dialog = Dialogs.Create(Root.XamlRoot);
        dialog.Title = "Open attachment?";
        dialog.Content = new TextBlock
        {
            Text = $"“{name}” will open in the app Windows uses for this type of file. Attachments can contain harmful content, so only open it if you trust where this document came from.",
            TextWrapping = TextWrapping.Wrap,
            Width = 360,
        };
        dialog.PrimaryButtonText = "Open";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Close;
        return await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    private async void SaveAttachment(AttachmentItem item)
    {
        // From a document no longer shown (a reload or another file replaced it): its index could name another attachment.
        if (item.Document != _document) return;
        var document = item.Document;
        string name = LaunchPolicy.SaveFileName(LaunchPolicy.SafeFileName(item.Name));
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(name),
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        string ext = Path.GetExtension(name);
        picker.FileTypeChoices.Add(string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant() + " file", [string.IsNullOrEmpty(ext) ? "." : ext]);
        var result = await picker.PickSaveFileAsync();
        if (result is null) return;
        if (document != _document)
        {
            ShowMessage("Unable to save attachment", AttachmentDocumentChanged, InfoBarSeverity.Warning);
            return;
        }
        try
        {
            await File.WriteAllBytesAsync(result.Path, await document.GetAttachmentDataAsync(item.Attachment));
            if (!LaunchPolicy.MarkAsUntrusted(result.Path))
                ShowMessage("Attachment saved without a security mark",
                    $"This location can't record that “{Path.GetFileName(result.Path)}” came from the internet, so Windows won't warn you when it's opened.",
                    InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            ShowMessage("Unable to save attachment", ex.Message, InfoBarSeverity.Error);
        }
    }

    // ================================================================ search

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _searchDebounce.Stop();
        if (string.IsNullOrEmpty(sender.Text)) StartSearch("");
        else
        {
            _searchDebounce.Interval = TimeSpan.FromMilliseconds(SearchDelay(sender.Text));
            _searchDebounce.Start();
        }
    }

    /// <summary>How long to wait after a key is typed before searching for <paramref name="text"/>, in milliseconds.</summary>
    private double SearchDelay(string text)
    {
        // Searching takes milliseconds once the pages are read, so it can keep up with typing. The shorter the
        // query, the more it matches and the likelier it's a word still being typed, so the longer it waits.
        int length = text.Trim().Length;
        var (delay, max) = length < 3 ? (200.0, 225.0) : length < 5 ? (75.0, 90.0) : (25.0, 40.0);
        // Those suit about two million characters of text (a 1,000-page document). The results, and the time to
        // list them, grow with the text, so scale the wait with it, up to a limit.
        if (_document?.EstimatedTextLength is { } chars) delay = Math.Min(delay * Math.Max(chars / 2e6, 0.25), max);
        return delay;
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchDebounce.Stop();
        // Enter submits the query whether or not Shift is down, so Shift+Enter is told apart here.
        // _searchQuery is trimmed, so a space typed after the query doesn't make Enter search again instead of moving on.
        if (args.QueryText.Trim() == _searchQuery && _searchItems.Count > 0 && !_searchResultsStale) MoveSearch(IsShiftDown() ? -1 : +1);
        else StartSearch(args.QueryText);
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            CancelSearch(clearText: true);
            Viewer.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private static bool IsShiftDown() => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnSearchOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_searchQuery)) StartSearch(_searchQuery);
    }

    private async void StartSearch(string query)
    {
        _searchCts?.Cancel();
        _searchQuery = query.Trim();
        if (_document is null || _searchQuery.Length == 0) ClearSearchResults();
        if (_document is null) return;
        ShowSearchPanel();
        if (string.IsNullOrEmpty(_searchQuery))
        {
            SearchStatus.Text = "";
            return;
        }
        var cts = _searchCts = new CancellationTokenSource();
        var document = _document;
        SearchStatus.Text = "Searching…";
        var options = _searchOptions = CurrentSearchOptions();
        _searchResultsStale = true;
        int start = Viewer.CurrentPage;
        // Results wait in pending to be listed in batches.
        int found = 0, fromStart = 0;
        // Rows a full list shows: a list item is at least 40 high.
        int screenful = (int)(SearchResultList.ActualHeight / 40) + 1;
        var pending = new List<SearchItem>();
        long started = Environment.TickCount64, yielded = started, listed = started;
        try
        {
            // Pages are searched on another thread; those it has searched are taken here without waiting.
            await foreach (var (i, hits) in document.Search(_searchQuery, options, cts.Token).ReadAllAsync(cts.Token))
            {
                if (cts.IsCancellationRequested) return;
                if (hits.Count > 0)
                {
                    string label = document.GetPageLabel(i);
                    foreach (var hit in hits) pending.Add(new SearchItem(hit, label));
                    found += hits.Count;
                    if (i >= start) fromStart += hits.Count;
                }
                // Each change to the list has it lay out its rows again, which takes about as long as searching pages
                // already read, so results are listed once, when the search is done. While pages are still being
                // read, they're listed every so often, the first time once there are enough from the current page on
                // to fill the list below the one shown: listed alone, it would show at the bottom, then jump to the top.
                // Failing that, they're listed a moment after the current page is searched, not before: those from
                // the start of the document would show with none selected. Until then the last search's stay.
                long now = Environment.TickCount64;
                bool listing = pending.Count > 0 && (_searchResultsStale || _searchIndex < 0
                    ? i >= start && now - started > 100 && (fromStart >= screenful || now - listed > 300)
                    : now - listed > 250);
                if (listing)
                {
                    ListSearchResults(pending, start);
                    listed = Environment.TickCount64;
                }
                SearchStatus.Text = ResultText(found) + "…";
                // Pages read by an earlier search are searched faster than they're listed; let the window draw and take
                // input now and then, and after updating the list, which takes it a while.
                if (listing || Environment.TickCount64 - yielded > 30)
                {
                    await YieldToWindowAsync();
                    if (cts.IsCancellationRequested) return;
                    yielded = Environment.TickCount64;
                }
            }
            ListSearchResults(pending, start);
            // Nothing from the current page on: wrap round to the first.
            if (_searchIndex < 0 && _searchItems.Count > 0) SelectSearchResult(0);
            SearchStatus.Text = found == 0 ? "No results found" : ResultText(found);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Lists waiting results, and if none is selected yet, selects the first from page <paramref name="start"/>
    /// on: the next one from where the reader is.
    /// </summary>
    private void ListSearchResults(List<SearchItem> pending, int start)
    {
        if (_searchResultsStale) ClearSearchResults();
        if (pending.Count == 0) return;
        int first = _searchItems.Count;
        _searchItems.AddRange(pending);
        Viewer.AddSearchResults(pending.Select(item => item.Hit));
        pending.Clear();
        if (_searchIndex >= 0)
        {
            // The list forgets its selection when reset.
            SearchResultList.SelectedIndex = _searchIndex;
            // Results listed after the current one let it scroll further up.
            if (_searchResultAtTop) SearchResultList.ScrollIntoView(_searchItems[_searchIndex], ScrollIntoViewAlignment.Leading);
            return;
        }
        for (int i = first; i < _searchItems.Count; i++)
        {
            if (_searchItems[i].Hit.PageIndex < start) continue;
            SelectSearchResult(i);
            // At the top, with the results after it below, rather than at the bottom as the last one listed so far.
            SearchResultList.ScrollIntoView(_searchItems[i], ScrollIntoViewAlignment.Leading);
            _searchResultAtTop = true;
            break;
        }
    }

    /// <summary>
    /// Lets the window take input and draw before carrying on. Task.Yield isn't enough: it queues the rest at normal
    /// priority, ahead of input and rendering, so a loop that keeps yielding still freezes the window.
    /// </summary>
    private Task YieldToWindowAsync()
    {
        var done = new TaskCompletionSource();
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, done.SetResult)) done.SetResult();
        return done.Task;
    }

    private SearchOptions CurrentSearchOptions()
    {
        var prefs = AppState.Preferences;
        return (MatchCaseToggle.IsChecked == true ? SearchOptions.MatchCase : 0) | (WholeWordToggle.IsChecked == true ? SearchOptions.WholeWords : 0)
            | (prefs.SearchAnyQuote ? SearchOptions.AnyQuote : 0) | (prefs.SearchAnyDash ? SearchOptions.AnyDash : 0)
            | (prefs.SearchJoinHyphenated ? SearchOptions.JoinHyphenated : 0);
    }

    private const double SearchSidebarWidth = 280;

    /// <summary>Shows the search field and results in place of the sidebar pages.</summary>
    private void ShowSearchPanel()
    {
        if (!_sidebarOpen) SetSidebarOpen(true, persist: false);
        // Results need more room than the thumbnails.
        SidebarColumn.Width = new GridLength(Math.Max(SidebarWidth(), SearchSidebarWidth));
        SearchPanel.Visibility = Visibility.Visible;
        SidebarSelector.Visibility = Visibility.Collapsed;
        AppTitleBar.IsPaneToggleButtonVisible = false;
        AppTitleBar.IsBackButtonVisible = true;
        ThumbnailList.Visibility = OutlineTree.Visibility = AnnotationsPanel.Visibility = AttachmentList.Visibility = Visibility.Collapsed;
        ReadSearchText();
    }

    /// <summary>
    /// Starts reading the pages' text when search opens rather than when the document does, so it costs nothing
    /// for documents that aren't searched, and is mostly done by the time the query is typed.
    /// </summary>
    private async void ReadSearchText()
    {
        if (_document is null || _searchTextCts is not null) return;
        var cts = _searchTextCts = new CancellationTokenSource();
        try
        {
            await _document.ReadSearchTextAsync(cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static string ResultText(int count) => count == 1 ? "1 result" : $"{count:N0} results";

    private void CancelSearch(bool clearText)
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _searchTextCts?.Cancel();
        _searchTextCts = null;
        _searchDebounce.Stop();
        ClearSearchResults();
        _searchQuery = "";
        if (clearText) SearchBox.Text = "";
        SearchStatus.Text = "";
        SearchPanel.Visibility = Visibility.Collapsed;
        SidebarSelector.Visibility = Visibility.Visible;
        AppTitleBar.IsBackButtonVisible = false;
        AppTitleBar.IsPaneToggleButtonVisible = _document is not null;
        if (_sidebarOpen) SidebarColumn.Width = new GridLength(SidebarWidth());
        if (SidebarSelector.SelectedItem is { } tab) ShowSidebarPage(tab);
        if (_document is not null && !AppState.Preferences.SidebarOpen && _sidebarOpen) SetSidebarOpen(false, persist: false);
    }

    private void ClearSearchResults()
    {
        _searchItems.Clear();
        _searchIndex = -1;
        _searchResultAtTop = false;
        _searchResultsStale = false;
        Viewer.ClearSearch();
    }

    private void SelectSearchResult(int index)
    {
        if (index < 0 || index >= _searchItems.Count) return;
        _searchResultAtTop = false;
        if (_searchIndex >= 0 && _searchIndex < _searchItems.Count) _searchItems[_searchIndex].IsCurrent = false;
        _searchIndex = index;
        _searchItems[index].IsCurrent = true;
        SearchResultList.SelectedIndex = index;
        SearchResultList.ScrollIntoView(_searchItems[index]);
        Viewer.ShowSearchHit(_searchItems[index].Hit);
    }

    private void MoveSearch(int direction)
    {
        // The results listed are the last search's until this one lists its own.
        if (_searchItems.Count == 0 || _searchResultsStale) return;
        int next = _searchIndex < 0 ? 0 : (_searchIndex + direction + _searchItems.Count) % _searchItems.Count;
        SelectSearchResult(next);
    }

    private void OnSearchResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchItem item) SelectSearchResult(_searchItems.IndexOf(item));
    }

    private void OnNextResult(object sender, RoutedEventArgs e) => MoveSearch(+1);
    private void OnPreviousResult(object sender, RoutedEventArgs e) => MoveSearch(-1);

    private void OnSearchContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not SearchItem item) return;
        if (args.ItemContainer.ContentTemplateRoot is not Grid grid || grid.Children[0] is not TextBlock text) return;
        text.Inlines.Clear();
        var hit = item.Hit;
        string before = hit.Before;
        text.Inlines.Add(new Run { Text = (before.Length > 0 ? "…" : "") + before });
        text.Inlines.Add(new Run { Text = hit.Match, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Inlines.Add(new Run { Text = hit.After });
    }

    // ================================================================ zoom

    private void OnZoomIn(object sender, RoutedEventArgs e) => Viewer.ZoomIn();
    private void OnZoomOut(object sender, RoutedEventArgs e) => Viewer.ZoomOut();
    private void OnFitPage(object sender, RoutedEventArgs e) => Viewer.SetZoomMode(ZoomMode.FitPage);
    private void OnFitWidth(object sender, RoutedEventArgs e) => Viewer.SetZoomMode(ZoomMode.FitWidth);

    private void OnZoomPreset(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var zoom))
            Viewer.SetZoom(zoom);
    }

    // ================================================================ menu commands

    private async void OnOpen(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".pdf");
        var result = await picker.PickSingleFileAsync();
        if (result is not null) await OpenAsync(result.Path);
    }

    private void OnNewWindow(object sender, RoutedEventArgs e) => new MainWindow().Activate();

    private async void OnSaveCopy(object sender, RoutedEventArgs e) => await SaveCopyAsync();

    /// <summary>
    /// Saves the document under a new name, leaving the original untouched unless it's picked on
    /// purpose. Like GNOME Papers, the window stays on the original, which counts as saved afterwards.
    /// </summary>
    private async Task<bool> SaveCopyAsync()
    {
        if (_document is null) return false;
        await Viewer.CommitPendingEditsAsync();
        if (_document is null) return false;
        if (Viewer.HasDrafts)
        {
            ShowMessage("Unable to save", "A note or text box couldn't be written to the document. Nothing was saved.", InfoBarSeverity.Warning);
            return false;
        }
        var document = _document;
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(document.FilePath) + (document.IsModified ? " (annotated)" : " (copy)"),
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        // Next to the original, unless that's a temporary folder (such as an opened attachment's).
        string? folder = Path.GetDirectoryName(document.FilePath);
        if (folder is not null && Directory.Exists(folder) && !IsTemporaryFolder(folder)) picker.SuggestedFolder = folder;
        picker.FileTypeChoices.Add("PDF document", [".pdf"]);
        var result = await picker.PickSaveFileAsync();
        if (result is null) return false;
        if (_document != document)
        {
            // Reloaded or replaced while the picker was open: the edits that were to be saved are gone.
            ShowMessage("Unable to save", "The document changed while you chose where to save it. Nothing was saved.", InfoBarSeverity.Warning);
            return false;
        }
        bool replacing = string.Equals(Path.GetFullPath(result.Path), Path.GetFullPath(document.FilePath), StringComparison.OrdinalIgnoreCase);
        try
        {
            // Our own write shouldn't trigger a reload. The watcher event can arrive after a long
            // save finishes, so the quiet period runs from the end of the save as well.
            if (replacing) _ignoreWatcherUntil = DateTime.MaxValue;
            try
            {
                await document.SaveAsync(result.Path);
            }
            finally
            {
                if (replacing) _ignoreWatcherUntil = DateTime.UtcNow.AddSeconds(3);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage("Unable to save", ex.Message, InfoBarSeverity.Error);
            return false;
        }
        UpdateTitle();
        if (!replacing) ShowMessage("Copy saved", $"Saved to “{Path.GetFileName(result.Path)}”.", InfoBarSeverity.Success);
        return true;
    }

    private static bool IsTemporaryFolder(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return new[] { Path.GetTempPath(), AppPackage.TempFolder }.Any(temp =>
            full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(temp)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        // One at a time: a second, asked for while the first waits for another window's dialog, would be dropped anyway.
        if (_document is null || _printDialog is not null) return;
        var dialog = new PrintDialog(this, Root.XamlRoot, _document, DisplayName(_document), Viewer.CurrentPage);
        // The document stays as it is until the dialog closes, and through the Windows dialog it can hand over to.
        _printing++;
        _printDialog = dialog;
        bool open;
        try
        {
            await Dialogs.ShowAsync(dialog);
        }
        finally
        {
            _printDialog = null;
            open = EndPrinting();
        }
        if (!open) return;
        if (dialog.UseSystemDialog)
        {
            await PrintWithSystemDialogAsync();
            return;
        }
        if (dialog.FailedPages > 0)
            ShowMessage("Printing problem", dialog.FailedPages == 1 ? "One page couldn't be printed and was left blank." : $"{dialog.FailedPages} pages couldn't be printed and were left blank.", InfoBarSeverity.Warning);
        CheckPendingReload();
    }

    private async Task PrintWithSystemDialogAsync()
    {
        if (_document is null) return;
        // The document stays as it is until the job has been sent: the dialog renders its pages until then.
        _printing++;
        bool open;
        try
        {
            await PrintService.PrintAsync(this, _document, DisplayName(_document),
                message => DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closed) ShowMessage("Printing problem", message, InfoBarSeverity.Warning);
                }));
        }
        catch (Exception ex)
        {
            ShowMessage("Unable to print", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            open = EndPrinting();
        }
        if (!open) return;
        CheckPendingReload();
    }

    private void OnToggleContinuous(object sender, RoutedEventArgs e) => Viewer.SetContinuous(ContinuousItem.IsChecked);
    private void OnToggleDual(object sender, RoutedEventArgs e) => Viewer.SetDual(DualItem.IsChecked);
    private void OnToggleOddLeft(object sender, RoutedEventArgs e) => Viewer.SetOddPagesLeft(OddLeftItem.IsChecked);
    private void OnToggleInvert(object sender, RoutedEventArgs e) => Viewer.SetInverted(InvertItem.IsChecked);
    private void OnRotateLeft(object sender, RoutedEventArgs e) => Viewer.Rotate(-90);
    private void OnRotateRight(object sender, RoutedEventArgs e) => Viewer.Rotate(90);
    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);
    private void OnPresent(object sender, RoutedEventArgs e) => SetPresenting(true);
    private async void OnReload(object sender, RoutedEventArgs e) => await ReloadIfAllowedAsync();

    private async Task ReloadIfAllowedAsync()
    {
        if (_document is null || !await ConfirmDiscardChangesAsync()) return;
        await ReloadAsync();
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (_document is not null) ShowInFolder(_document.FilePath);
    }

    private static void ShowInFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private void OnCloseDocument(object sender, RoutedEventArgs e) => CloseDocument();

    private async void OnProperties(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        await Dialogs.ShowPropertiesAsync(Root.XamlRoot, _document);
    }

    private void OnSettings(object sender, RoutedEventArgs e) => ShowSettings();
    private async void OnShortcuts(object sender, RoutedEventArgs e) => await Dialogs.ShowShortcutsAsync(Root.XamlRoot);
    private async void OnAbout(object sender, RoutedEventArgs e) => await Dialogs.ShowAboutAsync(Root.XamlRoot);

    // ================================================================ settings

    private void ShowSettings()
    {
        if (_settingsOpen) return;
        if (_presenting) SetPresenting(false);
        if (_fullscreen) SetFullscreen(false);
        Viewer.StopMiddleScroll();
        _settingsOpen = true;
        SettingsView.Refresh();
        ShowCurrentView();
        UpdateChrome();
        SettingsView.UpdateLayout();
        if (FocusManager.FindFirstFocusableElement(SettingsView) is Control first) first.Focus(FocusState.Programmatic);
    }

    private void CloseSettings()
    {
        if (!_settingsOpen) return;
        _settingsOpen = false;
        ShowCurrentView();
        UpdateChrome();
        if (_document is not null) Viewer.Focus(FocusState.Programmatic);
        // Settings has options for how search matches.
        if (_searchQuery.Length > 0 && CurrentSearchOptions() != _searchOptions) StartSearch(_searchQuery);
        CheckPendingReload();
    }

    /// <summary>Shows Settings if it's open, otherwise the document or the start page.</summary>
    private void ShowCurrentView()
    {
        SettingsView.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
        StartView.Visibility = !_settingsOpen && _document is null && !_hideStartPage ? Visibility.Visible : Visibility.Collapsed;
        DocumentLayout.Visibility = !_settingsOpen && _document is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================ full screen & presentation

    private void SetFullscreen(bool value)
    {
        if (_fullscreen == value) return;
        _fullscreen = value;
        // An extended title bar keeps the content 1px below the top for the resize border, which would show
        // as a light line along the top of the screen.
        ExtendsContentIntoTitleBar = !value;
        if (!value) SetTitleBar(AppTitleBar);
        AppWindow.SetPresenter(value ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        AppTitleBar.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        if (value)
        {
            _sidebarBeforeFullscreen = _sidebarOpen;
            SetSidebarOpen(false, persist: false);
            DocumentCard.BorderThickness = new Thickness(0);
            if (!_presenting) ShowHint("Press Esc or F11 to exit full screen");
        }
        else
        {
            SetSidebarOpen(_sidebarBeforeFullscreen, persist: false);
            FullscreenHint.Opacity = 0;
        }
    }

    private void SetPresenting(bool value)
    {
        if (_document is null || _presenting == value) return;
        _presenting = value;
        if (value)
        {
            SetFullscreen(true);
            SetAnnotateOpen(false);
            Viewer.SetPresentation(true);
            DocumentSurface.Background = new SolidColorBrush(Colors.Black);
            ShowHint("Press Esc to end the presentation");
        }
        else
        {
            Viewer.SetPresentation(false);
            // Uncovers the card's own background, which follows the theme.
            DocumentSurface.Background = null;
            SetFullscreen(false);
        }
    }

    // ================================================================ recent documents

    private void RefreshRecent()
    {
        var recent = AppState.Preferences.RememberRecent ? AppState.Recent : [];
        _recentItems = recent.Select(r => new RecentItem(r)).ToList();
        bool any = _recentItems.Count > 0;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        RecentHeader.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        FilterRecent();
        foreach (var item in _recentItems) LoadRecentThumbnail(item);
    }

    /// <summary>Shows the recent documents whose name or path contains every word typed in the search box.</summary>
    private void FilterRecent()
    {
        var terms = RecentSearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = terms.Length == 0
            ? _recentItems
            : _recentItems.Where(item => terms.All(t =>
                item.Name.Contains(t, StringComparison.CurrentCultureIgnoreCase) || item.Path.Contains(t, StringComparison.CurrentCultureIgnoreCase))).ToList();
        // Sync in place so only cards entering or leaving the filter animate; replacing ItemsSource replays every card's entrance on each keystroke.
        for (int i = _recentMatches.Count - 1; i >= 0; i--)
            if (!matches.Contains(_recentMatches[i])) _recentMatches.RemoveAt(i);
        for (int i = 0; i < matches.Count; i++)
            if (i == _recentMatches.Count || _recentMatches[i] != matches[i]) _recentMatches.Insert(i, matches[i]);
        RecentGrid.Visibility = matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentNoMatches.Visibility = _recentItems.Count > 0 && matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Smallest card slot, margin included; spare row width is shared out between the columns.</summary>
    private const double RecentCardMinWidth = 176;

    private void OnRecentGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RecentGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        double available = e.NewSize.Width - RecentGrid.Padding.Left - RecentGrid.Padding.Right;
        if (available <= 0) return;
        int columns = Math.Max(1, (int)(available / RecentCardMinWidth));
        // Rounded down so sub-pixel layout rounding can't push the last column onto the next row.
        panel.ItemWidth = Math.Floor(available / columns);
    }

    private void OnRecentSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => FilterRecent();

    // Enter opens the first match.
    private async void OnRecentSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_recentMatches.Count > 0) await OpenAsync(_recentMatches[0].Path);
    }

    private void OnRecentSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || string.IsNullOrEmpty(RecentSearchBox.Text)) return;
        RecentSearchBox.Text = "";
        e.Handled = true;
    }

    private static async void LoadRecentThumbnail(RecentItem item)
    {
        try
        {
            string path = item.Entry.ThumbnailPath;
            if (!File.Exists(path)) return;
            var bytes = await File.ReadAllBytesAsync(path);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            item.Thumbnail = bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
        }
    }

    private async Task SaveRecentThumbnailAsync(PdfDocument document)
    {
        try
        {
            string path = AppState.ThumbnailPathFor(document.FilePath);
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= File.GetLastWriteTimeUtc(document.FilePath)) return;
            var size = document.PageSizes[0];
            const int width = 300;
            double scale = width / size.Width;
            // Cropped for very tall pages; the card only shows the top anyway.
            var region = new RectInt32(0, 0, width, (int)Math.Clamp(Math.Round(size.Height * scale), 1, 2 * width));
            using var buffer = await document.RenderAsync(0, scale, 0, region, false, WorkPriority.Background, CancellationToken.None);
            // The list may have been cleared while the page rendered.
            if (buffer is null || AppState.Find(document.FilePath) is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)buffer.Width, (uint)buffer.Height, 96, 96,
                buffer.Span.ToArray());
            await encoder.FlushAsync();
            var bytes = new byte[file.Size];
            file.Seek(0);
            await file.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, Windows.Storage.Streams.InputStreamOptions.None);
            await File.WriteAllBytesAsync(path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or COMException)
        {
        }
    }

    private async void OnRecentClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentItem item) await OpenAsync(item.Path);
    }

    private void OnRecentRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecentItem item } element) return;
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open", Icon = new FontIcon { Glyph = "\uE8E5" } };
        open.Click += async (_, _) => await OpenAsync(item.Path);
        var newWindow = new MenuFlyoutItem { Text = "Open in new window", Icon = new FontIcon { Glyph = "\uE8A7" } };
        newWindow.Click += async (_, _) =>
        {
            var window = new MainWindow();
            window.Activate();
            await window.OpenAsync(item.Path);
        };
        var folder = new MenuFlyoutItem { Text = "Show in folder", Icon = new FontIcon { Glyph = "\uE838" } };
        folder.Click += (_, _) => ShowInFolder(item.Path);
        var remove = new MenuFlyoutItem { Text = "Remove from list", Icon = new FontIcon { Glyph = "\uE711" } };
        remove.Click += (_, _) => AppState.Remove(item.Path);
        menu.Items.Add(open);
        menu.Items.Add(newWindow);
        menu.Items.Add(folder);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);
        menu.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private async void OnClearRecent(object sender, RoutedEventArgs e)
    {
        var dialog = Dialogs.Create(Root.XamlRoot);
        dialog.Title = "Clear recent documents?";
        dialog.Content = new TextBlock
        {
            Text = "All documents will be removed from the list. The files themselves won't be deleted.",
            TextWrapping = TextWrapping.Wrap,
            Width = 360,
        };
        dialog.PrimaryButtonText = "Clear";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary) AppState.ClearRecent();
    }

    // ================================================================ drag and drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Open";
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var files = items.OfType<Windows.Storage.StorageFile>().Where(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0)
        {
            ShowMessage("Unsupported file", "Only PDF documents can be opened.", InfoBarSeverity.Warning);
            return;
        }
        for (int i = 1; i < files.Count; i++)
        {
            var window = new MainWindow();
            window.Activate();
            _ = window.OpenAsync(files[i].Path);
        }
        await OpenAsync(files[0].Path);
    }

    // ================================================================ keyboard

    private void SetupAccelerators()
    {
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        const VirtualKeyModifiers Ctrl = VirtualKeyModifiers.Control;
        const VirtualKeyModifiers Shift = VirtualKeyModifiers.Shift;
        const VirtualKeyModifiers None = VirtualKeyModifiers.None;

        Add(VirtualKey.O, Ctrl, () => _ = PickAndOpenAsync(), true);
        Add(VirtualKey.N, Ctrl, () => new MainWindow().Activate(), true);
        Add(VirtualKey.W, Ctrl, CloseDocument, true);
        Add(VirtualKey.S, Ctrl, () => _ = SaveCopyAsync(), true);
        Add(VirtualKey.S, Ctrl | Shift, () => _ = SaveCopyAsync(), true);
        Add(VirtualKey.P, Ctrl, () => OnPrint(this, new RoutedEventArgs()), true);
        Add(VirtualKey.P, Ctrl | Shift, () => _ = PrintWithSystemDialogAsync(), true);
        Add(VirtualKey.F, Ctrl, FocusSearch, true);
        Add(VirtualKey.F3, None, () => MoveSearch(+1), true);
        Add(VirtualKey.F3, Shift, () => MoveSearch(-1), true);
        Add(VirtualKey.G, Ctrl, () => MoveSearch(+1), true);
        Add(VirtualKey.G, Ctrl | Shift, () => MoveSearch(-1), true);
        Add(VirtualKey.Add, Ctrl, Viewer.ZoomIn, true);
        Add((VirtualKey)187, Ctrl, Viewer.ZoomIn, true);
        Add((VirtualKey)187, Ctrl | Shift, Viewer.ZoomIn, true);
        Add(VirtualKey.Subtract, Ctrl, Viewer.ZoomOut, true);
        Add((VirtualKey)189, Ctrl, Viewer.ZoomOut, true);
        Add(VirtualKey.Number0, Ctrl, () => Viewer.SetZoom(1), true);
        Add(VirtualKey.NumberPad0, Ctrl, () => Viewer.SetZoom(1), true);
        Add(VirtualKey.F9, None, () => { if (_document is not null) SetSidebarOpen(!_sidebarOpen); }, true);
        Add(VirtualKey.B, Ctrl, () => { if (_document is not null) SetSidebarOpen(!_sidebarOpen); }, true);
        Add(VirtualKey.F11, None, () => { if (_presenting) SetPresenting(false); else SetFullscreen(!_fullscreen); }, true);
        Add(VirtualKey.F5, None, () => SetPresenting(!_presenting), true);
        Add(VirtualKey.Left, Ctrl, () => Viewer.Rotate(-90), false);
        Add(VirtualKey.Right, Ctrl, () => Viewer.Rotate(90), false);
        Add(VirtualKey.I, Ctrl, () => Viewer.SetInverted(!Viewer.IsInverted), false);
        Add(VirtualKey.R, Ctrl, () => _ = ReloadIfAllowedAsync(), true);
        Add(VirtualKey.Enter, VirtualKeyModifiers.Menu, () => OnProperties(this, new RoutedEventArgs()), true);
        Add(VirtualKey.H, Ctrl, () => Viewer.HighlightSelection(), false);
        Add(VirtualKey.A, Ctrl | Shift, () => { if (!_presenting) SetAnnotateOpen(AnnotateBar.Visibility != Visibility.Visible); }, false);
        // Text boxes keep these for their own text.
        Add(VirtualKey.Z, Ctrl, Viewer.Undo, false);
        Add(VirtualKey.Y, Ctrl, Viewer.Redo, false);
        Add(VirtualKey.Z, Ctrl | Shift, Viewer.Redo, false);
        Add((VirtualKey)191, Ctrl | Shift, () => OnShortcuts(this, new RoutedEventArgs()), true);
        Add(VirtualKey.F1, None, () => OnShortcuts(this, new RoutedEventArgs()), true);
        Add((VirtualKey)188, Ctrl, () => OnSettings(this, new RoutedEventArgs()), true);
        Add(VirtualKey.Escape, None, () =>
        {
            if (_settingsOpen) CloseSettings();
            else if (_presenting) SetPresenting(false);
            else if (_fullscreen) SetFullscreen(false);
        }, false, () => _settingsOpen || _presenting || _fullscreen);
        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, CloseSettings, true, () => _settingsOpen);

        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action, bool inTextBoxes, Func<bool>? canExecute = null)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                if (canExecute?.Invoke() == false) return;
                if (!inTextBoxes && Root.XamlRoot is not null && FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or PasswordBox or AutoSuggestBox) return;
                // In Settings the document is out of sight, so only what works without one applies; an
                // accelerator with its own condition decides for itself.
                if ((_document is null || _settingsOpen) && canExecute is null && !WorksWithoutDocument(key)) return;
                args.Handled = true;
                action();
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }

        // Ctrl+W closes the window when there's no start page to go back to.
        static bool WorksWithoutDocument(VirtualKey key) =>
            key is VirtualKey.O or VirtualKey.N or VirtualKey.F or VirtualKey.F1 or (VirtualKey)191 or (VirtualKey)188
            || key == VirtualKey.W && !AppState.Preferences.RememberRecent;
    }

    private async void FocusSearch()
    {
        if (_settingsOpen) return;
        if (_document is null)
        {
            // On the start page, search the recent documents instead.
            if (RecentHeader.Visibility != Visibility.Visible) return;
            RecentSearchBox.Focus(FocusState.Keyboard);
            if (RecentSearchBox.FindDescendant<TextBox>() is { } recentBox) recentBox.SelectAll();
            return;
        }
        // Selected text becomes the query, as in browsers.
        if (!await SearchForSelectionAsync()) return;
        ShowSearchPanel();
        SearchBox.UpdateLayout();
        SearchBox.Focus(FocusState.Keyboard);
        if (SearchBox.FindDescendant<TextBox>() is { } box) box.SelectAll();
    }

    /// <summary>
    /// Searches for the selected text, if any. False when the document closed or changed, or Settings opened, while
    /// its text was read, so search shouldn't open.
    /// </summary>
    private async Task<bool> SearchForSelectionAsync()
    {
        var document = _document;
        if (document is null || !Viewer.HasSelection) return document is not null;
        var options = CurrentSearchOptions();
        string query = await Viewer.GetSelectedQueryAsync(options);
        if (_document != document || _settingsOpen) return false;
        if (query.Length == 0) return true;
        SearchBox.Text = query;
        // Searching again for the same query would move back to the page's first result.
        if (query != _searchQuery || CurrentSearchOptions() != _searchOptions || _searchResultsStale) StartSearch(query);
        return true;
    }
}

internal static class VisualTreeExtensions
{
    public static T? FindDescendant<T>(this DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (child.FindDescendant<T>() is { } found) return found;
        }
        return null;
    }

    public static T? FindDescendant<T>(this DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            if (child.FindDescendant<T>(name) is { } found) return found;
        }
        return null;
    }
}
