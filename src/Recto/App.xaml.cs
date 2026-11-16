using System.Diagnostics;
using System.Globalization;
using Recto.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Recto;

public partial class App : Application
{
    private const long MaxLogSize = 1024 * 1024;

    /// <summary>Open windows, in creation order.</summary>
    internal static List<MainWindow> OpenWindows { get; } = [];
    internal static MainWindow? LastActiveWindow { get; set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogError(e.Exception);
            // Keep the process alive so unsaved annotations aren't lost, but say so: the window
            // that failed may not be fully working any more.
            e.Handled = true;
            (LastActiveWindow ?? OpenWindows.LastOrDefault())?.ReportUnexpectedError();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (Program.IsPrimary)
        {
            // No other Recto is running, so leftovers from earlier sessions are no longer in use by us.
            _ = Task.Run(() => AttachmentFiles.Purge(TimeSpan.FromDays(1)));
            SingleInstance.Listen(paths => dispatcher.TryEnqueue(() => OpenFiles(paths)));
        }
        OpenFiles(Program.StartupFiles, adoptAttachments: Program.IsRestart);
    }

    /// <summary>
    /// Opens each file in the window that already shows it, an idle start page, or a new window.
    /// With no files, opens a new window. <paramref name="adoptAttachments"/> is for a restart, whose
    /// windows take over the folders of the attachments they show from the windows that closed.
    /// </summary>
    internal static void OpenFiles(IReadOnlyList<string> files, bool adoptAttachments = false)
    {
        if (files.Count == 0)
        {
            var window = new MainWindow();
            window.Activate();
            return;
        }
        foreach (var path in files)
        {
            var window = OpenWindows.FirstOrDefault(w => w.IsShowing(path) || w.IsOpening(path)) ?? OpenWindows.FirstOrDefault(w => w.IsIdle) ?? new MainWindow();
            if (adoptAttachments && AttachmentFiles.Contains(path) && Path.GetDirectoryName(path) is { } dir) window.AdoptAttachmentFolder(dir);
            window.BringToFront();
            _ = window.OpenAsync(path);
        }
    }

    private static bool _restarting;

    /// <summary>
    /// Starts Recto again, for a language chosen in Settings, with the documents that are open now. Each window
    /// first offers to save its unsaved annotations, and cancelling there calls the restart off.
    /// </summary>
    internal static async Task RestartAsync()
    {
        if (_restarting) return;
        _restarting = true;
        try
        {
            // A prompt only blocks its own window, so the others can be edited, or new ones opened, while it's up.
            // So each window is asked again if it has been edited since, until none needs asking; the windows then
            // close with no await in between. Windows closing once a print job is sent have already been asked.
            var asked = new Dictionary<MainWindow, MainWindow.EditStamp>();
            while (OpenWindows.FirstOrDefault(w => !w.IsClosing && (!asked.TryGetValue(w, out var stamp) || w.EditedSince(stamp))) is { } next)
            {
                if (!await next.ConfirmDiscardChangesAsync()) return;
                asked[next] = next.TakeEditStamp();
            }
            // The window restarted from last, so that it ends up in front.
            var windows = OpenWindows.Where(w => !w.IsClosing).OrderBy(w => w == LastActiveWindow).ToList();
            var files = windows.Select(w => w.DocumentPath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add(Program.RestartAfter + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            foreach (var file in files) start.ArgumentList.Add(file);
            Process.Start(start)?.Dispose();
            // An attachment's copy would go with the window showing it, before the new process can open it.
            foreach (var window in windows)
            {
                if (window.DocumentPath is { } path && AttachmentFiles.Contains(path)) window.HandOverAttachmentFolder(path);
            }
            // The process ends with its last window: once a print job being sent has finished, if there is one.
            foreach (var window in windows) window.CloseNow();
        }
        finally
        {
            _restarting = false;
        }
    }

    /// <summary>Applies the theme chosen in Settings to every open window.</summary>
    internal static void ApplyTheme()
    {
        foreach (var window in OpenWindows) window.ApplyTheme();
    }

    /// <summary>Applies the page corner style chosen in Settings to every open window.</summary>
    internal static void ApplyPageCorners()
    {
        foreach (var window in OpenWindows) window.ApplyPageCorners();
    }

    private static void LogError(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppState.Folder);
            string log = Path.Combine(AppState.Folder, "errors.log");
            var info = new FileInfo(log);
            if (info.Exists && info.Length > MaxLogSize) File.Move(log, Path.Combine(AppState.Folder, "errors.old.log"), overwrite: true);
            File.AppendAllText(log, $"[{DateTimeOffset.Now:u}] {exception}\n\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
