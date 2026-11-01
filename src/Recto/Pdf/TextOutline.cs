using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Foundation;

namespace Recto.Pdf;

/// <summary>
/// Lays text out with GDI and returns the outlines of its glyphs as PDF path operators. An appearance stream can't
/// embed a font through PDFium, and the fonts PDFium falls back on leave out whatever they don't cover, such as
/// Chinese in Helvetica. Outlines need no font and look the same in every viewer, and GDI picks fallback fonts
/// and shapes complex scripts for whatever is typed.
/// </summary>
internal static unsafe partial class TextOutline
{
    public const string FontFamily = "Segoe UI";

    /// <summary>GDI lays text out in whole units, so a font this much larger gives text that's placed and wrapped as if it weren't hinted.</summary>
    private const double UnitsPerPoint = 32;

    private const int DEFAULT_CHARSET = 1;
    private const int OUT_OUTLINE_PRECIS = 8;
    private const int TRANSPARENT = 1;
    private const uint DT_WORDBREAK = 0x10, DT_NOCLIP = 0x100, DT_CALCRECT = 0x400, DT_NOPREFIX = 0x800, DT_EDITCONTROL = 0x2000;
    private const uint LayoutFlags = DT_WORDBREAK | DT_NOPREFIX | DT_EDITCONTROL;
    private const byte PT_CLOSEFIGURE = 1, PT_LINETO = 2, PT_BEZIERTO = 4, PT_MOVETO = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateCompatibleDC(IntPtr dc);
    [LibraryImport("gdi32.dll")] private static partial int DeleteDC(IntPtr dc);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [LibraryImport("gdi32.dll")] private static partial IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [LibraryImport("gdi32.dll")] private static partial int DeleteObject(IntPtr obj);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(IntPtr dc, int mode);
    [LibraryImport("gdi32.dll")] private static partial int BeginPath(IntPtr dc);
    [LibraryImport("gdi32.dll")] private static partial int EndPath(IntPtr dc);
    [LibraryImport("gdi32.dll")] private static partial int GetPath(IntPtr dc, POINT* points, byte* types, int count);
    [LibraryImport("user32.dll")] private static partial int DrawTextW(IntPtr dc, char* text, int length, RECT* rect, uint format);

    /// <summary>
    /// Wraps <paramref name="text"/> to <paramref name="width"/> points. Returns the operators drawing it, its top-left
    /// corner at the origin that <paramref name="place"/> takes offsets from, and its height in points, which
    /// <paramref name="place"/> is also given first.
    /// </summary>
    public static (string Operators, double Height) Draw(string text, double width, double fontSize, Func<double, double, double, Point> place)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new InvalidOperationException("The text could not be laid out.");
        IntPtr font = CreateFontW(-(int)Math.Round(fontSize * UnitsPerPoint), 0, 0, 0, 400, 0, 0, 0, DEFAULT_CHARSET, OUT_OUTLINE_PRECIS, 0, 0, 0, FontFamily);
        if (font == IntPtr.Zero)
        {
            DeleteDC(dc);
            throw new InvalidOperationException("The text could not be laid out.");
        }
        IntPtr previous = SelectObject(dc, font);
        try
        {
            SetBkMode(dc, TRANSPARENT);
            var rect = new RECT { Right = Math.Max(1, (int)Math.Round(width * UnitsPerPoint)) };
            // A box with no text is one empty line high.
            string measured = text.Length == 0 ? " " : text;
            fixed (char* p = measured) DrawTextW(dc, p, measured.Length, &rect, LayoutFlags | DT_CALCRECT);
            rect.Right = Math.Max(1, (int)Math.Round(width * UnitsPerPoint));
            double height = rect.Bottom / UnitsPerPoint;
            if (text.Length == 0) return ("", height);

            BeginPath(dc);
            fixed (char* p = text) DrawTextW(dc, p, text.Length, &rect, LayoutFlags | DT_NOCLIP);
            EndPath(dc);
            int count = GetPath(dc, null, null, 0);
            // Only spaces make no path.
            if (count <= 0) return ("", height);
            var points = new POINT[count];
            var types = new byte[count];
            fixed (POINT* pp = points)
            fixed (byte* tp = types)
            {
                count = GetPath(dc, pp, tp, count);
            }
            return (ToOperators(points, types, count, (x, y) => place(height, x, y)), height);
        }
        finally
        {
            SelectObject(dc, previous);
            DeleteObject(font);
            DeleteDC(dc);
        }
    }

    private static string ToOperators(POINT[] points, byte[] types, int count, Func<double, double, Point> place)
    {
        var sb = new StringBuilder(count * 16);
        void Add(POINT p)
        {
            var at = place(p.X / UnitsPerPoint, p.Y / UnitsPerPoint);
            sb.Append(Number(at.X)).Append(' ').Append(Number(at.Y)).Append(' ');
        }
        for (int i = 0; i < count; i++)
        {
            switch (types[i] & ~PT_CLOSEFIGURE)
            {
                case PT_MOVETO:
                    Add(points[i]);
                    sb.Append("m\n");
                    break;
                case PT_LINETO:
                    Add(points[i]);
                    sb.Append("l\n");
                    break;
                case PT_BEZIERTO when i + 2 < count:
                    Add(points[i]);
                    Add(points[++i]);
                    Add(points[++i]);
                    sb.Append("c\n");
                    break;
            }
            if ((types[i] & PT_CLOSEFIGURE) != 0) sb.Append("h\n");
        }
        return sb.ToString();
    }

    /// <summary>Operators from <see cref="Draw"/> as XAML path markup, with the fill rule of PDF's <c>f</c>. Empty if there are none.</summary>
    public static string ToMarkup(string operators)
    {
        if (operators.Length == 0) return "";
        var sb = new StringBuilder("F1", operators.Length + 2);
        foreach (string line in operators.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Operands come first in PDF and last in markup, where h is a horizontal line.
            sb.Append(' ').Append(line == "h" ? 'Z' : char.ToUpperInvariant(line[^1])).Append(line.AsSpan(0, line.Length - 1));
        }
        return sb.ToString();
    }

    /// <summary>A number as PDF content writes it.</summary>
    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
