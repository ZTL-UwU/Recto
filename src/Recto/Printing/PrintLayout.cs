using System.Globalization;
using Windows.Foundation;

namespace Recto.Printing;

public enum SheetOrientation
{
    /// <summary>
    /// Each sheet takes the orientation that suits its pages: with one page to a sheet, the page's own
    /// (landscape pages come out on sideways paper); with several, whichever holds them larger.
    /// </summary>
    Automatic,
    Portrait,
    Landscape,
}

public enum PrintScaling
{
    /// <summary>Scale pages up or down to fill the printable area, or their cell when several share a sheet.</summary>
    Fit,
    /// <summary>Scale down pages that don't fit the printable area (or their cell), print the others at their true size.</summary>
    ShrinkOversized,
    ActualSize,
    Custom,
}

public enum PageSubset
{
    All,
    Odd,
    Even,
}

public enum TwoSided
{
    Off,
    LongEdge,
    ShortEdge,
}

/// <summary>A sheet of paper in points: its size and the part of it the printer can print on.</summary>
public readonly record struct PaperMetrics(double Width, double Height, Rect Printable)
{
    public bool IsLandscape => Width > Height;

    /// <summary>US Letter with quarter-inch margins, shown until the printer reports its own.</summary>
    public static PaperMetrics Letter => new(612, 792, new Rect(18, 18, 576, 756));
}

public sealed record LayoutOptions(SheetOrientation Orientation, PrintScaling Scaling, double CustomScale, int PagesPerSheet);

/// <summary>A page on a sheet: where it goes and the part of that which is printed, in sheet points.</summary>
public readonly record struct PagePlacement(int Page, Rect Bounds, Rect Clip);

/// <summary>
/// One side of a sheet as it's read, which can be turned from how it goes through the printer
/// (see <see cref="PrintLayout.ToPaper"/>).
/// </summary>
public sealed record PrintSheet(PaperMetrics Paper, IReadOnlyList<PagePlacement> Pages);

/// <summary>Page selection and imposition for printing. Pure arithmetic, so it's unit tested.</summary>
public static class PrintLayout
{
    public static readonly int[] PagesPerSheetChoices = [1, 2, 4, 6, 9, 16];

    /// <summary>Space between pages printed several to a sheet, in points (about 3 mm).</summary>
    public const double Gutter = 8;

    /// <summary>Most pages a range can expand to, so "1-999999, 1-999999, …" can't allocate without bound.</summary>
    private const int MaxSelected = 1_000_000;

    /// <summary>
    /// Parses ranges such as "1-5, 8, 11-" (one-based, inclusive) into zero-based page indexes, in
    /// the order given. "-3" starts at the first page, "5-" runs to the last; "5-3" prints backwards.
    /// </summary>
    public static bool TryParseRanges(string text, int pageCount, out List<int> pages, out string? error)
    {
        pages = [];
        error = null;
        foreach (var raw in text.Split([',', ';', '，', '、']))
        {
            var part = raw.Trim().Replace('–', '-').Replace('—', '-');
            if (part.Length == 0) continue;
            int dash = part.IndexOf('-');
            int first, last;
            if (dash < 0)
            {
                if (!TryPage(part, pageCount, out first, ref error)) return false;
                last = first;
            }
            else
            {
                string from = part[..dash].Trim(), to = part[(dash + 1)..].Trim();
                if (from.Length == 0 && to.Length == 0) return Fail(Strings.Format("RangeNotRange", part), out error);
                first = 1;
                last = pageCount;
                if (from.Length > 0 && !TryPage(from, pageCount, out first, ref error)) return false;
                if (to.Length > 0 && !TryPage(to, pageCount, out last, ref error)) return false;
            }
            if (pages.Count + Math.Abs(last - first) + 1 > MaxSelected) return Fail(Strings.Get("RangeTooMany"), out error);
            int step = last >= first ? 1 : -1;
            for (int p = first; p != last + step; p += step) pages.Add(p - 1);
        }
        if (pages.Count == 0) return Fail(Strings.Get("RangeEmpty"), out error);
        return true;
    }

    private static bool TryPage(string text, int pageCount, out int page, ref string? error)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page))
            return Fail(Strings.Format("RangeNotNumber", text), out error);
        if (page < 1 || page > pageCount)
            return Fail(pageCount == 1 ? Strings.Get("RangeOnlyOnePage") : Strings.Format("RangeNoSuchPage", page, pageCount), out error);
        return true;
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    /// <summary>Keeps the odd or even page numbers of <paramref name="pages"/> and optionally reverses them.</summary>
    public static List<int> Filter(IEnumerable<int> pages, PageSubset subset, bool reverse)
    {
        var result = subset switch
        {
            // Page numbers, not indexes: page 1 (index 0) is odd.
            PageSubset.Odd => pages.Where(p => p % 2 == 0).ToList(),
            PageSubset.Even => pages.Where(p => p % 2 == 1).ToList(),
            _ => pages.ToList(),
        };
        if (reverse) result.Reverse();
        return result;
    }

    /// <summary>Columns and rows for several pages to a sheet, with more of them along the sheet's long side.</summary>
    public static (int Columns, int Rows) Grid(int pagesPerSheet, bool landscape)
    {
        var (a, b) = pagesPerSheet switch
        {
            <= 1 => (1, 1),
            2 => (2, 1),
            4 => (2, 2),
            6 => (3, 2),
            9 => (3, 3),
            _ => (4, 4),
        };
        return landscape ? (a, b) : (b, a);
    }

    /// <summary>The paper turned to the requested orientation, with its margins turned along with it.</summary>
    public static PaperMetrics Orient(PaperMetrics paper, bool landscape)
    {
        if (paper.IsLandscape == landscape || paper.Width == paper.Height) return paper;
        // The inverse of ToPaper: paper point (X, Y) is sheet point (W - Y, X).
        double width = paper.Height;
        var p = paper.Printable;
        return new PaperMetrics(width, paper.Width, new Rect(width - p.Y - p.Height, p.X, p.Height, p.Width));
    }

    /// <summary>
    /// Maps a rectangle on a sheet that's turned from the paper (see <see cref="Orient"/>) onto the
    /// paper: the sheet goes through the printer turned a quarter counterclockwise, as printers
    /// usually print landscape, so its top runs along the paper's left edge.
    /// </summary>
    public static Rect ToPaper(Rect rect, double sheetWidth) => new(rect.Y, sheetWidth - rect.X - rect.Width, rect.Height, rect.Width);

    /// <summary>
    /// Where a page goes on the paper as it goes through the printer, in paper points, and the
    /// clockwise quarter turns to draw it with. On a sheet turned from the paper (see <see cref="ToPaper"/>)
    /// the page turns with the sheet, a quarter counterclockwise: three quarters clockwise.
    /// </summary>
    public static (Rect Bounds, Rect Clip, int Quarter) OnPaper(PrintSheet sheet, PagePlacement placement, PaperMetrics paper)
    {
        if (sheet.Paper.IsLandscape == paper.IsLandscape) return (placement.Bounds, placement.Clip, 0);
        return (ToPaper(placement.Bounds, sheet.Paper.Width), ToPaper(placement.Clip, sheet.Paper.Width), 3);
    }

    /// <summary>Lays out <paramref name="pages"/> (zero-based indexes into <paramref name="pageSizes"/>, in points) on sheets.</summary>
    public static List<PrintSheet> Layout(IReadOnlyList<int> pages, IReadOnlyList<Size> pageSizes, PaperMetrics paper, LayoutOptions options)
    {
        int perSheet = Math.Max(1, options.PagesPerSheet);
        var sheets = new List<PrintSheet>();
        for (int start = 0; start < pages.Count; start += perSheet)
        {
            int count = Math.Min(perSheet, pages.Count - start);
            bool landscape = options.Orientation switch
            {
                SheetOrientation.Portrait => false,
                SheetOrientation.Landscape => true,
                _ => PrefersLandscape(pageSizes[pages[start]], paper, perSheet),
            };
            var sheet = Orient(paper, landscape);
            var (columns, rows) = Grid(perSheet, sheet.IsLandscape);
            var area = sheet.Printable;
            double gutter = perSheet > 1 ? Gutter : 0;
            double cellWidth = Math.Max(1, (area.Width - gutter * (columns - 1)) / columns);
            double cellHeight = Math.Max(1, (area.Height - gutter * (rows - 1)) / rows);

            var placements = new List<PagePlacement>(count);
            for (int i = 0; i < count; i++)
            {
                var cell = new Rect(area.X + i % columns * (cellWidth + gutter), area.Y + i / columns * (cellHeight + gutter), cellWidth, cellHeight);
                var size = pageSizes[pages[start + i]];
                double fit = Math.Min(cell.Width / size.Width, cell.Height / size.Height);
                double scale = options.Scaling switch
                {
                    PrintScaling.ShrinkOversized => Math.Min(1, fit),
                    PrintScaling.ActualSize => 1,
                    PrintScaling.Custom => Math.Clamp(options.CustomScale, 0.01, 10),
                    _ => fit,
                };
                // A page printed at a set size on its own sheet is centred on the paper, like the original
                // would be; anything that doesn't fit is cut off at the printable area.
                var frame = perSheet == 1 && options.Scaling is PrintScaling.ActualSize or PrintScaling.Custom
                    ? new Rect(0, 0, sheet.Width, sheet.Height)
                    : cell;
                double w = size.Width * scale, h = size.Height * scale;
                var bounds = new Rect(frame.X + (frame.Width - w) / 2, frame.Y + (frame.Height - h) / 2, w, h);
                placements.Add(new PagePlacement(pages[start + i], bounds, cell));
            }
            sheets.Add(new PrintSheet(sheet, placements));
        }
        return sheets;
    }

    /// <summary>Whether a sheet starting with a page of <paramref name="size"/> holds its pages larger on its side.</summary>
    private static bool PrefersLandscape(Size size, PaperMetrics paper, int perSheet)
    {
        if (perSheet == 1) return size.Width > size.Height;
        double Fit(bool landscape)
        {
            var sheet = Orient(paper, landscape);
            var (columns, rows) = Grid(perSheet, sheet.IsLandscape);
            double w = (sheet.Printable.Width - Gutter * (columns - 1)) / columns, h = (sheet.Printable.Height - Gutter * (rows - 1)) / rows;
            return Math.Min(w / size.Width, h / size.Height);
        }
        return Fit(true) > Fit(false) + 1e-9;
    }

    /// <summary>
    /// The order sheets go to the printer, for copies made by sending them again. Null is a blank
    /// side, so that with two-sided printing every copy starts on a new sheet of paper.
    /// </summary>
    public static List<int?> Sequence(int sheetCount, int copies, bool collate, bool twoSided)
    {
        var sequence = new List<int?>();
        copies = Math.Max(1, copies);
        if (collate || copies == 1)
        {
            for (int c = 0; c < copies; c++)
            {
                for (int i = 0; i < sheetCount; i++) sequence.Add(i);
                if (twoSided && sheetCount % 2 == 1 && c < copies - 1) sequence.Add(null);
            }
        }
        else
        {
            // Uncollated: each sheet of paper (both its sides when printing two-sided) repeated.
            int step = twoSided ? 2 : 1;
            for (int i = 0; i < sheetCount; i += step)
            {
                for (int c = 0; c < copies; c++)
                {
                    sequence.Add(i);
                    if (twoSided && (i + 1 < sheetCount || c < copies - 1)) sequence.Add(i + 1 < sheetCount ? i + 1 : null);
                }
            }
        }
        return sequence;
    }

    /// <summary>
    /// Sheets of paper the <see cref="Sequence"/> for these settings uses, without building it: every copy starts on
    /// a new sheet of paper, collated or not.
    /// </summary>
    public static int PaperCount(int sheetCount, int copies, bool twoSided) =>
        Math.Max(1, copies) * (twoSided ? (sheetCount + 1) / 2 : sheetCount);

    /// <summary>
    /// Whether the printer can make the copies itself, so the document is sent once rather than once
    /// per copy: when its driver takes that many and, for collated copies, can collate. Uncollated
    /// two-sided copies are always sent, as drivers differ in whether they repeat each side or each
    /// sheet of paper.
    /// </summary>
    public static bool PrinterMakesCopies(int copies, bool collate, bool twoSided, int maxCopies, bool canCollate) =>
        copies > 1 && copies <= maxCopies && (collate ? canCollate : !twoSided);
}
