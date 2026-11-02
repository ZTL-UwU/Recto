using Windows.Foundation;
using Windows.UI;

namespace Recto.Pdf;

/// <summary>Affine transform from PDF user space to "display points" (top-left origin, page rotation applied).</summary>
public readonly record struct PageTransform(double A, double B, double C, double D, double E, double F)
{
    public Point Apply(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);

    public Point Invert(double x, double y)
    {
        double det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) return new Point(x, y);
        double dx = x - E, dy = y - F;
        return new Point((D * dx - C * dy) / det, (-B * dx + A * dy) / det);
    }

    /// <summary>Maps a user-space rectangle (left, top, right, bottom) to an axis-aligned display rect.</summary>
    public Rect ApplyRect(double left, double top, double right, double bottom)
    {
        var p1 = Apply(left, top);
        var p2 = Apply(right, bottom);
        return new Rect(new Point(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y)), new Point(Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y)));
    }

    /// <summary>Display points per user space unit, for lengths such as a line's width.</summary>
    public double Scale => Math.Sqrt(Math.Abs(A * D - B * C));
}

public sealed class PdfDestination
{
    public int PageIndex { get; init; } = -1;
    /// <summary>Target location in PDF user space, when the destination specifies one.</summary>
    public double? UserX { get; init; }
    public double? UserY { get; init; }
    public string? Uri { get; init; }

    public bool IsInternal => PageIndex >= 0;
}

public sealed class PdfLink
{
    public required Rect Bounds { get; init; }
    public required PdfDestination Destination { get; init; }
}

public enum AnnotationKind
{
    Note,
    Highlight,
    Underline,
    StrikeOut,
    Squiggly,
    FreeText,
    Shape,
    Ink,
    Stamp,
    Attachment,
    Other,
}

public sealed class PdfAnnotation
{
    public required int PageIndex { get; init; }
    /// <summary>Index into the page's /Annots array when this was read. Edits re-check it against the fields below.</summary>
    public required int Index { get; init; }
    /// <summary>PDFium subtype, /Rect in user space and /NM, used to find the annotation again after the array shifted.</summary>
    internal int Subtype { get; init; }
    internal Rect UserRect { get; init; }
    internal string Name { get; init; } = "";
    public required AnnotationKind Kind { get; init; }
    public required Rect Bounds { get; init; }
    public string Contents { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTimeOffset? Modified { get; init; }
    public Color Color { get; init; } = Color.FromArgb(255, 255, 214, 10);
    /// <summary>What a drawing Recto made is drawn from. Null for other programs' drawings, whose look only their appearance stream has.</summary>
    public InkDrawing? Ink { get; init; }
    /// <summary>What a text box Recto made is drawn from. Null for other programs' text boxes.</summary>
    public FreeText? FreeText { get; init; }
}

/// <summary>Freehand strokes, in display points. Each stroke has at least one point.</summary>
public sealed record InkDrawing(Point[][] Strokes, Color Color, double Width, byte Opacity = 255)
{
    /// <summary>The strokes whose line comes within <paramref name="reach"/> of <paramref name="point"/>.</summary>
    public IEnumerable<int> StrokesAt(Point point, double reach)
    {
        double limit = Width / 2 + reach;
        for (int s = 0; s < Strokes.Length; s++)
        {
            var stroke = Strokes[s];
            for (int i = 0; i < stroke.Length; i++)
            {
                if (Distance(point, stroke[i], stroke[Math.Min(i + 1, stroke.Length - 1)]) > limit) continue;
                yield return s;
                break;
            }
        }
    }

    /// <summary>The same drawing <paramref name="dx"/>, <paramref name="dy"/> away.</summary>
    public InkDrawing Moved(double dx, double dy) =>
        this with { Strokes = [.. Strokes.Select(stroke => stroke.Select(p => new Point(p.X + dx, p.Y + dy)).ToArray())] };

    /// <summary>
    /// Where a line that has reached <paramref name="line"/> goes as the pointer gets to <paramref name="pointer"/>: it trails
    /// <paramref name="lag"/> behind, as if on a string. A mouse reports whole pixels, so its slow strokes are staircases,
    /// which a line going straight to each point would show all along its edges.
    /// </summary>
    public static Point Follow(Point line, Point pointer, double lag)
    {
        double dx = pointer.X - line.X, dy = pointer.Y - line.Y, distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance <= lag) return line;
        double pulled = 1 - lag / distance;
        return new Point(line.X + dx * pulled, line.Y + dy * pulled);
    }

    /// <summary>The area the strokes cover, with the width of their lines.</summary>
    public Rect Bounds()
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var p in Strokes.SelectMany(s => s))
        {
            left = Math.Min(left, p.X); right = Math.Max(right, p.X);
            top = Math.Min(top, p.Y); bottom = Math.Max(bottom, p.Y);
        }
        return left > right ? Rect.Empty : new Rect(left - Width / 2, top - Width / 2, right - left + Width, bottom - top + Width);
    }

    /// <summary>Distance from a point to the segment from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static double Distance(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
        double t = length == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1);
        return Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2));
    }
}

/// <summary>
/// A box of text on a page. <paramref name="Bounds"/> is in display points; its height follows the text
/// when it's written to the document.
/// </summary>
public sealed record FreeText(Rect Bounds, string Text, Color Color, double FontSize);

/// <summary>A removed annotation, with what <see cref="PdfDocument.RestoreAnnotationAsync"/> needs to put it back.</summary>
public sealed class AnnotationSnapshot
{
    public required int PageIndex { get; init; }
    internal int Subtype { get; init; }
    internal FS_RECTF Rect { get; init; }
    internal (uint R, uint G, uint B, uint A)? Color { get; init; }
    internal FS_QUADPOINTSF[] Quads { get; init; } = [];
    internal int Flags { get; init; }
    /// <summary>Text entries such as /Contents and /NM, by their key with its terminating zero.</summary>
    internal (byte[] Key, string Value)[] Strings { get; init; } = [];
    internal InkDrawing? Ink { get; init; }
    internal FreeText? FreeText { get; init; }
}

public sealed class PageText
{
    public required int PageIndex { get; init; }
    public required int[] CodePoints { get; init; }
    /// <summary>Per character bounds in display points. <see cref="Rect.Empty"/> for generated characters.</summary>
    public required Rect[] Boxes { get; init; }
    public required PdfLink[] Links { get; init; }
    public required PdfAnnotation[] Annotations { get; init; }
    public required PageTransform Transform { get; init; }

    /// <summary>
    /// For each character index PDFium uses (as in search results), its index here, where characters
    /// are in reading order. Null when the orders are the same.
    /// </summary>
    public int[]? SourceIndexMap { get; init; }

    public int Count => CodePoints.Length;

    private TextLayout? _layout;
    /// <summary>The characters grouped into lines, for hit testing and highlighting.</summary>
    public TextLayout Layout => _layout ??= new TextLayout(this);

    public string GetText(int start, int end)
    {
        start = Math.Clamp(start, 0, Count);
        end = Math.Clamp(end, 0, Count);
        // A range that starts inside a surrogate pair takes the whole character.
        if (start > 0 && start < Count && IsLow(start) && IsHigh(start - 1)) start--;
        var sb = new System.Text.StringBuilder(Math.Max(0, end - start));
        for (int i = start; i < end; i++)
        {
            int cp = CodePoints[i];
            if (cp == 0xFFFE || cp == 0x02 || cp == 0) continue; // soft hyphen markers
            if (cp == '\r') continue;
            // PDFium reports UTF-16 code units, so characters outside the BMP (math italic letters, emoji)
            // arrive as two entries. Keep pairs together, even when the range ends between them.
            if (IsHigh(i))
            {
                if (i + 1 < Count && IsLow(i + 1)) sb.Append((char)cp).Append((char)CodePoints[++i]);
                continue;
            }
            if (cp is > 0 and <= 0xFFFF && !IsLow(i)) sb.Append((char)cp);
        }
        return sb.ToString();
    }

    /// <summary>The characters from <paramref name="start"/> to <paramref name="end"/> as search reads them, hyphen markers and line breaks kept.</summary>
    public string GetSearchText(int start, int end)
    {
        start = Math.Clamp(start, 0, Count);
        end = Math.Clamp(end, start, Count);
        if (start > 0 && start < Count && IsLow(start) && IsHigh(start - 1)) start--;
        return string.Create(end - start, (CodePoints, start), static (chars, state) =>
        {
            for (int i = 0; i < chars.Length; i++) chars[i] = state.CodePoints[state.start + i] is var cp and <= 0xFFFF ? (char)cp : '�';
        });
    }

    private bool IsHigh(int i) => CodePoints[i] is >= 0xD800 and <= 0xDBFF;
    private bool IsLow(int i) => CodePoints[i] is >= 0xDC00 and <= 0xDFFF;

    public bool IsWordChar(int i) => i >= 0 && i < Count && IsWordCodePoint(CodePoints[i]);

    /// <summary>Whether a character belongs to a word, for double-click selection and whole-word search.</summary>
    public static bool IsWordCodePoint(int cp) => cp > 32 && !char.IsWhiteSpace((char)Math.Min(cp, 0xFFFF)) && !IsPunctuation(cp);

    private static bool IsPunctuation(int cp) => cp < 0x10000 && char.IsPunctuation((char)cp) && cp != '\'' && cp != '-' && cp != '_';
}

public sealed class OutlineItem
{
    public required string Title { get; init; }
    public PdfDestination? Destination { get; init; }
    public List<OutlineItem> Children { get; } = [];
}

public sealed class PdfAttachment
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public long Size { get; init; }
}

public sealed class SearchHit
{
    public required int PageIndex { get; init; }
    public required int CharIndex { get; init; }
    public required int Length { get; init; }
    /// <summary>The page's search text, which the context below is read from.</summary>
    public required string PageText { get; init; }
    /// <summary>Whether hyphen markers are left out of the context, as they were out of the match.</summary>
    public bool JoinHyphenated { get; init; }

    // Read when a result is shown rather than for every hit: a short query has hundreds of thousands of them,
    // and making their context took most of the search's time and memory.
    public string Before => TextSearch.Context(PageText, Math.Max(0, CharIndex - 40), CharIndex, true, JoinHyphenated);
    public string Match => TextSearch.Context(PageText, CharIndex, CharIndex + Length, false, JoinHyphenated);
    public string After => TextSearch.Context(PageText, CharIndex + Length, Math.Min(PageText.Length, CharIndex + Length + 60), false, JoinHyphenated);
}

public sealed class DocumentInfo
{
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Keywords { get; init; } = "";
    public string Creator { get; init; } = "";
    public string Producer { get; init; } = "";
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string Version { get; init; } = "";
    public bool IsEncrypted { get; init; }
}

/// <summary>
/// BGRA pixels in native memory. Page bitmaps run to tens of megabytes; as managed arrays they'd
/// land on the large object heap, or stay parked in a shared pool long after a render.
/// </summary>
public sealed unsafe partial class PixelBuffer : IDisposable
{
    private byte* _data;

    public PixelBuffer(int width, int height)
    {
        Width = width;
        Height = height;
        _data = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)width * (nuint)height * 4);
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public int Length => Stride * Height;

    public byte* Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_data is null, this);
            return _data;
        }
    }

    public ReadOnlySpan<byte> Span => new(Pointer, Length);

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~PixelBuffer() => Free();

    private void Free()
    {
        System.Runtime.InteropServices.NativeMemory.Free(_data);
        _data = null;
    }
}

public sealed class PdfPasswordException(bool wrongPassword) : Exception(wrongPassword ? "Incorrect password" : "Password required")
{
    public bool WrongPassword { get; } = wrongPassword;
}
