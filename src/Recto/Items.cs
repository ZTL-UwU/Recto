using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Recto.Controls;
using Recto.Pdf;
using Recto.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Recto;

public abstract partial class ObservableItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class RecentItem(RecentDocument entry) : ObservableItem
{
    private ImageSource? _thumbnail;

    public RecentDocument Entry { get; } = entry;
    public string Name => string.IsNullOrWhiteSpace(Entry.Title) ? Entry.FileName : Entry.Title;
    public string Path => Entry.Path;

    public string Details => Humanize(Entry.LastOpened);

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            Set(ref _thumbnail, value);
            Raise(nameof(PlaceholderVisibility));
        }
    }

    public Visibility PlaceholderVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    private static string Humanize(DateTimeOffset time)
    {
        var span = DateTimeOffset.Now - time;
        if (span.TotalMinutes < 1) return Strings.Get("JustNow");
        if (span.TotalHours < 1) return Strings.Format("MinutesAgo", (int)span.TotalMinutes);
        if (time.Date == DateTime.Today) return Strings.Format("TodayAt", time.LocalDateTime.ToString("t"));
        if (time.Date == DateTime.Today.AddDays(-1)) return Strings.Get("Yesterday");
        if (span.TotalDays < 7) return Strings.Format("DaysAgo", (int)Math.Ceiling(span.TotalDays));
        return time.LocalDateTime.ToString("d");
    }
}

public sealed partial class ThumbnailItem : ObservableItem
{
    private ImageSource? _image;
    private Brush _paper = new SolidColorBrush(Colors.White);
    private bool _isCurrent;

    public required int Index { get; init; }
    public required string Label { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public CancellationTokenSource? Pending { get; set; }

    public ImageSource? Image
    {
        get => _image;
        set => Set(ref _image, value);
    }

    public Brush Paper
    {
        get => _paper;
        set => Set(ref _paper, value);
    }

    /// <summary>Whether this is the page the viewer is on.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }

    public static Windows.UI.Text.FontWeight LabelWeight(bool current) => current ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
}

public sealed class AnnotationItem(PdfAnnotation annotation, string pageLabel)
{
    public PdfAnnotation Annotation { get; } = annotation;
    public string Kind => DocumentView.KindName(Annotation.Kind);
    public string Page => Strings.Format("PageLabel", pageLabel);
    public string Contents => Annotation.Contents.Trim();
    public Visibility ContentsVisibility => string.IsNullOrWhiteSpace(Annotation.Contents) ? Visibility.Collapsed : Visibility.Visible;
    public Brush ColorBrush => new SolidColorBrush(Annotation.Color);

    public string Details => string.Join(" · ", new[]
    {
        string.IsNullOrWhiteSpace(Annotation.Author) ? null : Annotation.Author,
        Annotation.Modified?.LocalDateTime.ToString("g"),
    }.Where(s => s is not null));
}

public sealed class AttachmentItem(PdfDocument document, PdfAttachment attachment)
{
    /// <summary>The document it's attached to: its index only means something there.</summary>
    public PdfDocument Document { get; } = document;
    public PdfAttachment Attachment { get; } = attachment;
    public string Name => Attachment.Name;
    public string Size => FormatSize(Attachment.Size);

    public static string FormatSize(long bytes)
    {
        double value = bytes;
        int unit = 0;
        // Up to GB.
        while (value >= 1024 && unit < 3)
        {
            value /= 1024;
            unit++;
        }
        return unit switch
        {
            0 => Strings.Count("SizeBytes", bytes),
            1 => Strings.Format("SizeKB", value),
            2 => Strings.Format("SizeMB", value),
            _ => Strings.Format("SizeGB", value),
        };
    }
}

public sealed partial class SearchItem(SearchHit hit, string pageLabel) : ObservableItem
{
    private bool _isCurrent;

    public SearchHit Hit { get; } = hit;
    public string Page { get; } = pageLabel;

    /// <summary>The result shown in the document, marked in the list.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }
}

/// <summary>
/// Search results, added in batches: a ListView handles each change to a collection on its own, at about 12 µs
/// each, so adding a search's results one by one took seconds. A batch goes in as one Reset.
/// </summary>
public sealed partial class SearchItemCollection : ObservableCollection<SearchItem>
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs Reset = new(NotifyCollectionChangedAction.Reset);

    /// <summary>Appends <paramref name="items"/> as one change.</summary>
    public void AddRange(List<SearchItem> items)
    {
        if (items.Count == 0) return;
        CheckReentrancy();
        // Collection<T> keeps its items in a List<T> unless given another list.
        ((List<SearchItem>)Items).AddRange(items);
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(Reset);
    }
}
