using System.Globalization;
using System.Text;
using Windows.Foundation;
using Windows.UI;

namespace Recto.Pdf;

// Drawings and text boxes. They're regular /Ink and /FreeText annotations, so other viewers show and list them, with
// appearances Recto writes itself: PDFium's joins strokes with spikes, and drops text its fallback font doesn't have.
// Their appearance streams hide their color from PDFium, so it's kept in /DA, read back by ParseDefaultAppearance.
public sealed unsafe partial class PdfDocument
{
    private static readonly byte[] KeyDefaultAppearance = "DA\0"u8.ToArray();
    private static readonly byte[] KeyOpacity = "CA\0"u8.ToArray();

    /// <summary>Space between a text box's edge and its text, in points.</summary>
    public const double TextBoxInset = 2;
    /// <summary>Narrowest a text box's text can be laid out, in points.</summary>
    public const double MinTextBoxWidth = 24;

    private static bool IsOwn(string name) => name.StartsWith(RectoNamePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether a number read from a file can be drawn with. The name alone doesn't make an annotation one of Recto's: a
    /// file can carry any, and what can't be written back is left alone like another program's.
    /// </summary>
    private static bool IsUsable(double value) => Math.Abs(value) < 1e6;

    /// <summary>Adds a drawing.</summary>
    public Task<PdfAnnotation> AddInkAsync(int index, InkDrawing ink) =>
        AddDrawingAsync(index, Native.FPDF_ANNOT_INK, annot => WriteInk(annot, ink, GetTransform(index)));

    /// <summary>Adds a text box.</summary>
    public Task<PdfAnnotation> AddFreeTextAsync(int index, FreeText text) =>
        AddDrawingAsync(index, Native.FPDF_ANNOT_FREETEXT, annot => WriteFreeText(annot, text, GetTransform(index), PageSizes[index].Height));

    private Task<PdfAnnotation> AddDrawingAsync(int index, int subtype, Action<IntPtr> write)
    {
        return PdfWorker.Run(() =>
        {
            IntPtr page = GetPage(index);
            IntPtr annot = Native.FPDFPage_CreateAnnot(page, subtype);
            if (annot == IntPtr.Zero) throw new InvalidOperationException("The annotation could not be created.");
            try
            {
                write(annot);
                Native.FPDFAnnot_SetFlags(annot, 4 /* print */);
                string contents = GetAnnotString(annot, KeyContents, exact: true);
                StampAnnotation(annot, contents);
                MarkEdited();
                return ReadCreated(index, page, annot);
            }
            finally
            {
                Native.FPDFPage_CloseAnnot(annot);
            }
        }, WorkPriority.Visible);
    }

    /// <summary>Changes a drawing in place. Returns what it was, or null if it's gone or isn't one Recto made.</summary>
    public Task<InkDrawing?> SetInkAsync(PdfAnnotation annotation, InkDrawing ink) => EditDrawingAsync(annotation, (annot, t) =>
    {
        if (ReadInk(annot, t) is not { } previous) return null;
        WriteInk(annot, ink, t);
        return previous;
    });

    /// <summary>Changes a text box in place. Returns what it was, or null if it's gone or isn't one Recto made.</summary>
    public Task<FreeText?> SetFreeTextAsync(PdfAnnotation annotation, FreeText text) => EditDrawingAsync(annotation, (annot, t) =>
    {
        if (ReadFreeText(annot, t) is not { } previous) return null;
        WriteFreeText(annot, text, t, PageSizes[annotation.PageIndex].Height);
        return previous;
    });

    private Task<T?> EditDrawingAsync<T>(PdfAnnotation annotation, Func<IntPtr, PageTransform, T?> edit) where T : class
    {
        return PdfWorker.Run(() =>
        {
            IntPtr page = GetPage(annotation.PageIndex);
            int index = FindAnnotation(page, annotation);
            if (index < 0) return null;
            IntPtr annot = Native.FPDFPage_GetAnnot(page, index);
            if (annot == IntPtr.Zero) return null;
            try
            {
                if (!IsOwn(GetAnnotString(annot, KeyName))) return null;
                var previous = edit(annot, GetTransform(annotation.PageIndex));
                if (previous is null) return null;
                SetAnnotString(annot, KeyModified, FormatPdfDate(DateTimeOffset.Now));
                MarkEdited();
                return previous;
            }
            finally
            {
                Native.FPDFPage_CloseAnnot(annot);
            }
        }, WorkPriority.Visible);
    }

    private static void WriteInk(IntPtr annot, InkDrawing ink, PageTransform t)
    {
        // PDFium won't set these on an annotation that has an appearance.
        Native.FPDFAnnot_SetAP(annot, Native.FPDF_ANNOT_APPEARANCEMODE_NORMAL, null);
        Native.FPDFAnnot_SetColor(annot, Native.FPDFANNOT_COLORTYPE_Color, ink.Color.R, ink.Color.G, ink.Color.B, ink.Opacity);
        double scale = Math.Max(t.Scale, 1e-6);
        float width = (float)(ink.Width / scale);
        Native.FPDFAnnot_SetBorder(annot, 0, 0, width);
        Native.FPDFAnnot_RemoveInkList(annot);

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var strokes = new List<FS_POINTF[]>(ink.Strokes.Length);
        foreach (var stroke in ink.Strokes)
        {
            var points = new FS_POINTF[stroke.Length];
            for (int i = 0; i < stroke.Length; i++)
            {
                var p = t.Invert(stroke[i].X, stroke[i].Y);
                points[i] = new FS_POINTF { X = (float)p.X, Y = (float)p.Y };
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            strokes.Add(points);
            fixed (FS_POINTF* p = points) Native.FPDFAnnot_AddInkStroke(annot, p, (nuint)points.Length);
        }
        // Past the caps at the ends of the strokes, which the appearance would otherwise be cut off at.
        float margin = width / 2 + 1;
        var bounds = new FS_RECTF { Left = (float)minX - margin, Bottom = (float)minY - margin, Right = (float)maxX + margin, Top = (float)maxY + margin };
        Native.FPDFAnnot_SetRect(annot, &bounds);
        SetAnnotString(annot, KeyDefaultAppearance, $"{ColorOperators(ink.Color)} rg");
        SetAppearance(annot, InkAppearance(strokes, ink.Color, width, ink.Opacity < 255));
    }

    /// <summary>Round joins and caps, and each point a curve towards the next, so handwriting looks like it was written.</summary>
    internal static string InkAppearance(IReadOnlyList<FS_POINTF[]> strokes, Color color, float width, bool translucent)
    {
        // PDFium defines /GS in the appearance's resources for an annotation that has /CA.
        var sb = new StringBuilder(translucent ? "/GS gs\n" : "");
        sb.Append(ColorOperators(color)).Append(" RG\n").Append(TextOutline.Number(width)).Append(" w 1 J 1 j\n");
        void Add(float x, float y) => sb.Append(TextOutline.Number(x)).Append(' ').Append(TextOutline.Number(y)).Append(' ');
        foreach (var s in strokes)
        {
            if (s.Length == 0) continue;
            Add(s[0].X, s[0].Y);
            sb.Append("m\n");
            float cx = s[0].X, cy = s[0].Y;
            for (int i = 1; i < s.Length - 1; i++)
            {
                // A quadratic curve through the middle of each pair of points, written as a cubic one.
                float mx = (s[i].X + s[i + 1].X) / 2, my = (s[i].Y + s[i + 1].Y) / 2;
                Add(cx + (s[i].X - cx) * 2 / 3, cy + (s[i].Y - cy) * 2 / 3);
                Add(mx + (s[i].X - mx) * 2 / 3, my + (s[i].Y - my) * 2 / 3);
                Add(mx, my);
                sb.Append("c\n");
                (cx, cy) = (mx, my);
            }
            // A single point is a dot: a line of no length, with its round caps.
            Add(s[^1].X, s[^1].Y);
            sb.Append("l\n");
        }
        return sb.Append("S\n").ToString();
    }

    private static InkDrawing? ReadInk(IntPtr annot, PageTransform t)
    {
        if (ParseDefaultAppearance(GetAnnotString(annot, KeyDefaultAppearance)) is not { Color: { } color }) return null;
        float rx, ry, width;
        if (Native.FPDFAnnot_GetBorder(annot, &rx, &ry, &width) == 0) return null;
        float opacity = 1;
        fixed (byte* key = KeyOpacity)
        {
            if (Native.FPDFAnnot_GetNumberValue(annot, key, &opacity) == 0) opacity = 1;
        }
        uint count = Native.FPDFAnnot_GetInkListCount(annot);
        var strokes = new List<Point[]>((int)count);
        for (uint s = 0; s < count; s++)
        {
            uint length = Native.FPDFAnnot_GetInkListPath(annot, s, null, 0);
            if (length == 0) continue;
            var points = new FS_POINTF[length];
            fixed (FS_POINTF* p = points) Native.FPDFAnnot_GetInkListPath(annot, s, p, length);
            if (!points.All(p => IsUsable(p.X) && IsUsable(p.Y))) return null;
            strokes.Add([.. points.Select(p => t.Apply(p.X, p.Y))]);
        }
        if (strokes.Count == 0 || !IsUsable(width) || width < 0) return null;
        return new InkDrawing([.. strokes], color, width * t.Scale, (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255));
    }

    private static void WriteFreeText(IntPtr annot, FreeText text, PageTransform t, double pageHeight)
    {
        var (operators, height) = DrawFreeText(text, pageHeight, p => t.Invert(p.X, p.Y));
        var box = new Rect(text.Bounds.Left, TextBoxTop(text.Bounds.Top, height + 2 * TextBoxInset, pageHeight), text.Bounds.Width, height + 2 * TextBoxInset);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var corner in new[] { new Point(box.Left, box.Top), new Point(box.Right, box.Top), new Point(box.Left, box.Bottom), new Point(box.Right, box.Bottom) })
        {
            var p = t.Invert(corner.X, corner.Y);
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        Native.FPDFAnnot_SetAP(annot, Native.FPDF_ANNOT_APPEARANCEMODE_NORMAL, null);
        var bounds = new FS_RECTF { Left = (float)minX, Bottom = (float)minY, Right = (float)maxX, Top = (float)maxY };
        Native.FPDFAnnot_SetRect(annot, &bounds);
        // No border, for viewers that draw the box anew from these entries.
        Native.FPDFAnnot_SetBorder(annot, 0, 0, 0);
        SetAnnotString(annot, KeyDefaultAppearance, $"/Helv {TextOutline.Number(text.FontSize / Math.Max(t.Scale, 1e-6))} Tf {ColorOperators(text.Color)} rg");
        SetAnnotString(annot, KeyContents, text.Text);
        SetAppearance(annot, $"{ColorOperators(text.Color)} rg\n{operators}f\n");
    }

    /// <summary>Lays a text box's text out. Its operators and height are as <see cref="TextOutline.Draw"/> gives them, placed by <paramref name="place"/> from display points.</summary>
    private static (string Operators, double Height) DrawFreeText(FreeText text, double pageHeight, Func<Point, Point> place)
    {
        double left = text.Bounds.Left + TextBoxInset;
        return TextOutline.Draw(text.Text, Math.Max(MinTextBoxWidth, text.Bounds.Width - 2 * TextBoxInset), text.FontSize,
            (lines, x, y) => place(new Point(left + x, TextBoxTop(text.Bounds.Top, lines + 2 * TextBoxInset, pageHeight) + TextBoxInset + y)));
    }

    /// <summary>
    /// A text box's text as XAML path markup, the same shapes the document draws, placed by <paramref name="place"/> from
    /// display points. Empty if it has nothing to show.
    /// </summary>
    public static string TextBoxMarkup(FreeText text, double pageHeight, Func<Point, Point> place) =>
        TextOutline.ToMarkup(DrawFreeText(text, pageHeight, place).Operators);

    /// <summary>Where a text box's top goes for all of it to be on the page, which shows nothing past its end. One taller than the page starts at the top.</summary>
    public static double TextBoxTop(double top, double height, double pageHeight) => Math.Max(0, Math.Min(top, pageHeight - height));

    private static FreeText? ReadFreeText(IntPtr annot, PageTransform t)
    {
        if (ParseDefaultAppearance(GetAnnotString(annot, KeyDefaultAppearance)) is not { Color: { } color, FontSize: { } size }) return null;
        FS_RECTF r;
        if (Native.FPDFAnnot_GetRect(annot, &r) == 0) return null;
        if (!IsUsable(size) || !IsUsable(r.Left) || !IsUsable(r.Top) || !IsUsable(r.Right) || !IsUsable(r.Bottom)) return null;
        return new FreeText(t.ApplyRect(r.Left, r.Top, r.Right, r.Bottom), GetAnnotString(annot, KeyContents, exact: true), color, size * t.Scale);
    }

    private static void SetAppearance(IntPtr annot, string operators)
    {
        fixed (char* p = operators) Native.FPDFAnnot_SetAP(annot, Native.FPDF_ANNOT_APPEARANCEMODE_NORMAL, p);
    }

    /// <summary>A color's components as PDF operands. Four decimals bring back the same 8-bit values.</summary>
    private static string ColorOperators(Color c) => string.Create(CultureInfo.InvariantCulture, $"{c.R / 255.0:0.####} {c.G / 255.0:0.####} {c.B / 255.0:0.####}");

    /// <summary>The font size and fill color of a default appearance string, such as <c>/Helv 12 Tf 1 0 0 rg</c>.</summary>
    internal static (double? FontSize, Color? Color)? ParseDefaultAppearance(string appearance)
    {
        double? size = null;
        Color? color = null;
        var tokens = appearance.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        static bool Number(string s, out double value) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        static byte Component(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "Tf" && i >= 1 && Number(tokens[i - 1], out double s) && s > 0) size = s;
            else if (tokens[i] == "rg" && i >= 3 && Number(tokens[i - 3], out double r) && Number(tokens[i - 2], out double g) && Number(tokens[i - 1], out double b))
                color = Color.FromArgb(255, Component(r), Component(g), Component(b));
        }
        return size is null && color is null ? null : (size, color);
    }
}
