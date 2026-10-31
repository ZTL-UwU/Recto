using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recto.Services;

public enum ZoomMode
{
    Custom,
    FitWidth,
    FitPage,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class RecentDocument
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset LastOpened { get; set; }
    public int Page { get; set; }
    public ZoomMode ZoomMode { get; set; } = ZoomMode.FitWidth;
    public double Zoom { get; set; } = 1;
    public int Rotation { get; set; }
    public bool? Continuous { get; set; }
    public bool? Dual { get; set; }

    [JsonIgnore] public string FileName => System.IO.Path.GetFileName(Path);
    [JsonIgnore] public string ThumbnailPath => AppState.ThumbnailPathFor(Path);
}

public sealed class Preferences
{
    public AppTheme Theme { get; set; }
    /// <summary>Round the corners of pages in the viewer, like Windows 11 cards; off gives square paper.</summary>
    public bool RoundedPageCorners { get; set; } = true;
    /// <summary>Keep a list of opened documents, with the page each was left on, for the start page.</summary>
    public bool RememberRecent { get; set; } = true;
    /// <summary>Search matches straight and curly quotes to each other, see <see cref="Recto.Pdf.SearchOptions.AnyQuote"/>.</summary>
    public bool SearchAnyQuote { get; set; } = true;
    /// <summary>Search matches hyphens, dashes and the minus sign to each other, see <see cref="Recto.Pdf.SearchOptions.AnyDash"/>.</summary>
    public bool SearchAnyDash { get; set; } = true;
    /// <summary>Search finds words hyphenated at the end of a line, see <see cref="Recto.Pdf.SearchOptions.JoinHyphenated"/>.</summary>
    public bool SearchJoinHyphenated { get; set; } = true;
    public bool Continuous { get; set; } = true;
    public bool Dual { get; set; }
    public bool OddPagesLeft { get; set; }
    public bool Inverted { get; set; }
    public bool SidebarOpen { get; set; } = true;
    /// <summary>Width chosen by dragging the sidebar edge; null means "fit the page thumbnails".</summary>
    public double? SidebarWidthOverride { get; set; }
    public int SidebarPage { get; set; }
    public int WindowWidth { get; set; } = 1200;
    public int WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }
    public PrintPreferences Print { get; set; } = new();
    public AnnotatePreferences Annotate { get; set; } = new();
}

/// <summary>The color (as 0xRRGGBB) and size, in points, each annotation tool was last used with.</summary>
public sealed class AnnotatePreferences
{
    public int PenColor { get; set; }
    public double PenWidth { get; set; } = 2;
    public int HighlighterColor { get; set; } = 0xFFDD33;
    public double HighlighterWidth { get; set; } = 14;
    public int TextColor { get; set; }
    public double TextSize { get; set; } = 12;
}

/// <summary>Choices from the last print job, offered again for the same printer.</summary>
public sealed class PrintPreferences
{
    public string? Printer { get; set; }
    public short? PaperSize { get; set; }
    public bool Grayscale { get; set; }
    public Recto.Printing.TwoSided TwoSided { get; set; }
}

[JsonSerializable(typeof(StateFile))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class StateJsonContext : JsonSerializerContext;

internal sealed class StateFile
{
    public Preferences Preferences { get; set; } = new();
    public List<RecentDocument> Recent { get; set; } = [];
}

/// <summary>Per-user state persisted as JSON in %LOCALAPPDATA%\Recto (or the package's folder, see <see cref="AppPackage"/>).</summary>
public static class AppState
{
    private const int MaxRecent = 40;

    public static readonly string Folder = AppPackage.DataFolder;
    private static readonly string StatePath = System.IO.Path.Combine(Folder, "state.json");
    private static readonly string ThumbnailFolder = System.IO.Path.Combine(Folder, "Thumbnails");
    private static readonly object Gate = new();
    private static readonly object WriteGate = new();
    private static string? _pendingJson;
    private static bool _writing;
    private static Task _writer = Task.CompletedTask;
    private static StateFile _state = Load();

    public static event EventHandler? RecentChanged;

    public static Preferences Preferences => _state.Preferences;

    public static IReadOnlyList<RecentDocument> Recent
    {
        get { lock (Gate) return _state.Recent.ToArray(); }
    }

    public static RecentDocument? Find(string path)
    {
        lock (Gate) return _state.Recent.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    public static void Touch(RecentDocument entry)
    {
        lock (Gate)
        {
            _state.Recent.RemoveAll(r => string.Equals(r.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            _state.Recent.Insert(0, entry);
            if (_state.Recent.Count > MaxRecent)
            {
                foreach (var old in _state.Recent.Skip(MaxRecent)) TryDelete(old.ThumbnailPath);
                _state.Recent.RemoveRange(MaxRecent, _state.Recent.Count - MaxRecent);
            }
        }
        Save();
        RecentChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Remove(string path)
    {
        lock (Gate) _state.Recent.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        TryDelete(ThumbnailPathFor(path));
        Save();
        RecentChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Turns the recent documents list on or off. While it's off the list is kept, but hidden and not added to.</summary>
    public static void SetRememberRecent(bool remember)
    {
        Preferences.RememberRecent = remember;
        Save();
        RecentChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void ClearRecent()
    {
        lock (Gate)
        {
            foreach (var r in _state.Recent) TryDelete(r.ThumbnailPath);
            _state.Recent.Clear();
        }
        Save();
        RecentChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Removes the start page thumbnail of a document, if there is one.</summary>
    public static void DeleteThumbnail(string documentPath) => TryDelete(ThumbnailPathFor(documentPath));

    public static string ThumbnailPathFor(string documentPath)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(documentPath.ToLowerInvariant())));
        return System.IO.Path.Combine(ThumbnailFolder, hash[..16] + ".png");
    }

    /// <summary>
    /// Snapshots the state and writes it in the background: the flush to disk can take tens of
    /// milliseconds, which used to land on the UI thread every time a document opened.
    /// </summary>
    public static void Save()
    {
        string json;
        lock (Gate) json = JsonSerializer.Serialize(_state, StateJsonContext.Default.StateFile);
        lock (WriteGate)
        {
            // Only the newest snapshot matters; one writer at a time keeps an older one from landing last.
            _pendingJson = json;
            if (!_writing)
            {
                _writing = true;
                _writer = Task.Run(WritePending);
            }
        }
    }

    /// <summary>Waits for <see cref="Save"/>s still being written, so they aren't lost when the process exits.</summary>
    public static void Flush()
    {
        Task writer;
        lock (WriteGate) writer = _writer;
        writer.Wait();
    }

    private static void WritePending()
    {
        while (true)
        {
            string json;
            lock (WriteGate)
            {
                if (_pendingJson is null)
                {
                    _writing = false;
                    return;
                }
                json = _pendingJson;
                _pendingJson = null;
            }
            Write(json);
        }
    }

    private static void Write(string json)
    {
        string temp = StatePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Folder);
            // Write the whole file next to the old one, then swap it in, so a crash halfway
            // through never leaves a truncated state.json behind.
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, StatePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
        }
    }

    private static StateFile Load()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                var state = JsonSerializer.Deserialize(File.ReadAllText(StatePath), StateJsonContext.Default.StateFile) ?? new();
                // An explicit null (a file edited by hand) skips the defaults the properties start with.
                state.Preferences ??= new();
                state.Preferences.Print ??= new();
                state.Preferences.Annotate ??= new();
                state.Recent = state.Recent?.Where(r => r is not null && !string.IsNullOrEmpty(r.Path)).ToList() ?? [];
                foreach (var r in state.Recent) r.Title ??= "";
                return state;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
