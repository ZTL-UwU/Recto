using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Recto.Pdf;

// Drawing onto a printer. GDI is only used here, so its few entry points live here too.
public sealed unsafe partial class PdfDocument
{
    /// <summary>Resolution pages printed as images are rendered at, at most; printers interpolate the rest.</summary>
    private const double MaxImageDpi = 300;

    /// <summary>
    /// Draws a page onto a device context with a page in progress (a printer's): into <paramref name="bounds"/>,
    /// in device pixels, turned clockwise by <paramref name="quarter"/> quarter turns and cut off at
    /// <paramref name="clip"/>. Pages go to the printer as vector output, so text stays sharp at any
    /// resolution, except with <paramref name="asImage"/> and for pages with form fields, which PDFium
    /// only draws into bitmaps. Cancelling <paramref name="token"/> stops a page being drawn as an image.
    /// Call on the <see cref="PdfWorker"/> thread.
    /// </summary>
    internal void DrawToDevice(IntPtr dc, int index, RectInt32 bounds, int quarter, RectInt32 clip, int deviceDpi,
        bool annotations, bool grayscale, bool asImage, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= PageCount || bounds.Width <= 0 || bounds.Height <= 0) return;
        IntPtr page = GetPage(index);
        int flags = Native.FPDF_PRINTING | (annotations ? Native.FPDF_ANNOT : 0) | (grayscale ? Native.FPDF_GRAYSCALE : 0);
        // Whatever either path changes on the device context (clip, stretch mode, brush origin) is
        // undone before the next page on the sheet is drawn.
        int saved = SaveDC(dc);
        try
        {
            if (asImage || (annotations && HasFormFields(page)))
            {
                DrawAsImage(dc, page, bounds, quarter, clip, deviceDpi, flags, token);
                return;
            }
            IntersectClipRect(dc, clip.X, clip.Y, clip.X + clip.Width, clip.Y + clip.Height);
            Native.FPDF_RenderPage(dc, page, bounds.X, bounds.Y, bounds.Width, bounds.Height, quarter, flags);
        }
        finally
        {
            RestoreDC(dc, saved);
        }
    }

    private bool HasFormFields(IntPtr page)
    {
        if (_form == IntPtr.Zero) return false;
        int count = Native.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            IntPtr annot = Native.FPDFPage_GetAnnot(page, i);
            if (annot == IntPtr.Zero) continue;
            int subtype = Native.FPDFAnnot_GetSubtype(annot);
            Native.FPDFPage_CloseAnnot(annot);
            if (subtype == Native.FPDF_ANNOT_WIDGET) return true;
        }
        return false;
    }

    private void DrawAsImage(IntPtr dc, IntPtr page, RectInt32 bounds, int quarter, RectInt32 clip, int deviceDpi, int flags, CancellationToken token)
    {
        // Only the part of the page that's printed.
        int left = Math.Max(bounds.X, clip.X), top = Math.Max(bounds.Y, clip.Y);
        int right = Math.Min(bounds.X + bounds.Width, clip.X + clip.Width), bottom = Math.Min(bounds.Y + bounds.Height, clip.Y + clip.Height);
        if (right <= left || bottom <= top) return;
        double k = Math.Min(1, MaxImageDpi / Math.Max(1, deviceDpi));
        k = Math.Min(k, Math.Sqrt(MaxRenderPixels / ((double)(right - left) * (bottom - top))));
        int width = Math.Max(1, (int)Math.Ceiling((right - left) * k)), height = Math.Max(1, (int)Math.Ceiling((bottom - top) * k));
        int x = (int)Math.Round((bounds.X - left) * k), y = (int)Math.Round((bounds.Y - top) * k);
        int sizeX = Math.Max(1, (int)Math.Round(bounds.Width * k)), sizeY = Math.Max(1, (int)Math.Round(bounds.Height * k));

        using var buffer = new PixelBuffer(width, height);
        byte* p = buffer.Pointer;
        IntPtr bitmap = Native.FPDFBitmap_CreateEx(width, height, Native.FPDFBitmap_BGRA, p, buffer.Stride);
        if (bitmap == IntPtr.Zero) throw new InvalidDataException(Strings.Get("PrintPageNoMemory"));
        try
        {
            RenderOnto(bitmap, p, width, height, page, x, y, sizeX, sizeY, quarter, flags, token);
        }
        finally
        {
            Native.FPDFBitmap_Destroy(bitmap);
        }

        var info = new BITMAPINFOHEADER
        {
            Size = sizeof(BITMAPINFOHEADER),
            Width = width,
            Height = -height, // top-down rows
            Planes = 1,
            BitCount = 32,
        };
        SetStretchBltMode(dc, HALFTONE);
        SetBrushOrgEx(dc, 0, 0, IntPtr.Zero);
        if (StretchDIBits(dc, left, top, right - left, bottom - top, 0, 0, width, height, p, &info, DIB_RGB_COLORS, SRCCOPY) == 0)
            throw new InvalidDataException(Strings.Get("PrinterRejectedPage"));
    }

    /// <summary>Turns BGRA pixels grey, by perceived brightness.</summary>
    private static void ToGray(byte* p, int length)
    {
        for (int i = 0; i < length; i += 4)
        {
            byte g = (byte)((p[i] * 29 + p[i + 1] * 150 + p[i + 2] * 77) >> 8);
            p[i] = p[i + 1] = p[i + 2] = g;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size;
        public int Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    private const int HALFTONE = 4;
    private const uint DIB_RGB_COLORS = 0;
    private const uint SRCCOPY = 0x00CC0020;

    [LibraryImport("gdi32")] private static partial int SaveDC(IntPtr dc);
    [LibraryImport("gdi32")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool RestoreDC(IntPtr dc, int saved);
    [LibraryImport("gdi32")] private static partial int IntersectClipRect(IntPtr dc, int left, int top, int right, int bottom);
    [LibraryImport("gdi32")] private static partial int SetStretchBltMode(IntPtr dc, int mode);
    [LibraryImport("gdi32")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [LibraryImport("gdi32")] private static partial int StretchDIBits(IntPtr dc, int xDest, int yDest, int destWidth, int destHeight, int xSrc, int ySrc, int srcWidth, int srcHeight, void* bits, BITMAPINFOHEADER* info, uint usage, uint rop);
}
