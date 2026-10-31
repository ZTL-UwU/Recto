using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Foundation;
using Windows.UI;

namespace Recto.Pdf;

/// <summary>
/// A PDF document backed by PDFium. All public members are thread safe; the actual work is
/// marshalled onto the <see cref="PdfWorker"/> thread.
/// </summary>
public sealed unsafe partial class PdfDocument : IDisposable
{
    private const int PageCacheSize = 8;

    /// <summary>
    /// Pages are shown at their true size up to this many points per side (200 inches, the
    /// limit in the PDF spec). Larger pages are scaled down, so a hostile media box can't
    /// blow up the layout or the bitmaps that are allocated from it.
    /// </summary>
    public const double MaxPageSide = 14400;
    private const double MinPageSide = 1;
    /// <summary>Last line of defence for <see cref="RenderAsync"/>: callers size their bitmaps well below this.</summary>
    private const long MaxRenderPixels = 40_000_000;
    /// <summary>
    /// The whole file is read into memory, so a huge (or sparse) file is refused before anything is
    /// allocated. Files under this that still don't fit in memory fail cleanly in <see cref="ReadFile"/>.
    /// </summary>
    public const long MaxFileBytes = 8L << 30;
    /// <summary>
    /// Pages laid out at most. The page count comes from the file; without a cap, a document that
    /// claims billions of pages would allocate per-page arrays of that size.
    /// </summary>
    public const int MaxPages = 1_000_000;
    /// <summary>Largest embedded file that's extracted: the data is copied into one managed array, so this is the array limit (just under 2 GB).</summary>
    public static long MaxAttachmentBytes => Array.MaxLength;
    /// <summary>Longest link target read, in characters; anything longer isn't a usable address, so the link is left out.</summary>
    private const int MaxLinkLength = 32 * 1024;

    private IntPtr _handle;
    private void* _buffer;
    /// <summary>Form-fill environment; without it PDFium doesn't draw form fields (widget annotations).</summary>
    private IntPtr _form;
    private void* _formInfo;
    private readonly LinkedList<(int Index, IntPtr Page)> _pages = new();
    private readonly Dictionary<int, PageTransform> _transforms = [];
    /// <summary>Written on the worker thread, read from any; see <see cref="GetSearchTextAsync"/>.</summary>
    private readonly string?[] _searchTexts;
    private int _searchTextPages;
    private long _searchTextChars;
    private bool _disposed;

    public string FilePath { get; private set; }
    public long FileSize { get; }
    public int PageCount { get; }
    /// <summary>Page sizes in points, with the page's own /Rotate applied and clamped by <see cref="ClampPageSize"/>.</summary>
    public Size[] PageSizes { get; }
    public string?[] PageLabels { get; }
    public DocumentInfo Info { get; }
    public bool IsModified { get; private set; }
    /// <summary>Edits made so far, saved or not, so callers can tell whether the document changed since they last looked.</summary>
    public int EditCount => Volatile.Read(ref _editCount);
    private int _editCount;

    private PdfDocument(string path, IntPtr handle, void* buffer, long size)
    {
        FilePath = path;
        _handle = handle;
        _buffer = buffer;
        FileSize = size;
        PageCount = Math.Min(Native.FPDF_GetPageCount(handle), MaxPages);
        PageSizes = new Size[PageCount];
        PageLabels = new string?[PageCount];
        _searchTexts = new string?[PageCount];
        for (int i = 0; i < PageCount; i++)
        {
            FS_SIZEF s;
            if (Native.FPDF_GetPageSizeByIndexF(handle, i, &s) != 0 && float.IsFinite(s.Width) && float.IsFinite(s.Height) && s.Width > 0 && s.Height > 0)
                PageSizes[i] = ClampPageSize(s.Width, s.Height).Display;
            else
                PageSizes[i] = new Size(612, 792);

            int index = i;
            var label = Native.ReadUtf16((b, l) => Native.FPDF_GetPageLabel(handle, index, (void*)b, l));
            PageLabels[i] = string.IsNullOrEmpty(label) ? null : label;
        }
        Info = ReadInfo();

        _formInfo = NativeMemory.AllocZeroed(Native.FormFillInfoSize);
        *(int*)_formInfo = 1; // FPDF_FORMFILLINFO.version
        _form = Native.FPDFDOC_InitFormFillEnvironment(handle, _formInfo);
    }

    /// <summary>Scales oversized pages down uniformly and pads degenerate sides up to a minimum.</summary>
    internal static (Size Display, (double X, double Y) Scale) ClampPageSize(double width, double height)
    {
        double k = Math.Min(1, MaxPageSide / Math.Max(width, height));
        double w = Math.Max(MinPageSide, width * k), h = Math.Max(MinPageSide, height * k);
        return (new Size(w, h), (w / width, h / height));
    }

    /// <summary>Reads a file straight into native memory, so the buffer PDFium parses is the only copy.</summary>
    private static (IntPtr Buffer, long Size) ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        long size = stream.Length;
        if (size > MaxFileBytes) throw new InvalidDataException($"The file is too large to open. Recto opens documents up to {MaxFileBytes >> 30} GB.");
        byte* buffer;
        try
        {
            buffer = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, size));
        }
        catch (OutOfMemoryException)
        {
            throw new InvalidDataException("There isn't enough memory to open the file.");
        }
        try
        {
            long done = 0;
            while (done < size)
            {
                int read = stream.Read(new Span<byte>(buffer + done, (int)Math.Min(size - done, 1 << 30)));
                if (read == 0) throw new IOException("The file changed while it was being read.");
                done += read;
            }
            return ((IntPtr)buffer, size);
        }
        catch
        {
            NativeMemory.Free(buffer);
            throw;
        }
    }

    /// <summary>Opens a buffer returned by <see cref="ReadFile"/>. Takes ownership of it, also on failure.</summary>
    private static PdfDocument Load(string path, IntPtr data, long size, string? password)
    {
        void* buffer = (void*)data;
        IntPtr handle = IntPtr.Zero;
        try
        {
            byte[]? pw = password is null ? null : Encoding.UTF8.GetBytes(password + "\0");
            fixed (byte* p = pw)
            {
                handle = Native.FPDF_LoadMemDocument64(buffer, (nuint)size, p);
            }
            if (handle == IntPtr.Zero)
            {
                uint error = Native.FPDF_GetLastError();
                if (error == Native.FPDF_ERR_PASSWORD) throw new PdfPasswordException(password is not null);
                throw new InvalidDataException(error switch
                {
                    2 => "The file could not be found or opened.",
                    3 => "The file is not a PDF document or it is damaged.",
                    5 => "The document uses an unsupported security scheme.",
                    _ => "The document could not be loaded.",
                });
            }
            if (Native.FPDF_GetPageCount(handle) <= 0) throw new InvalidDataException("The document has no pages.");
            return new PdfDocument(path, handle, buffer, size);
        }
        catch
        {
            if (handle != IntPtr.Zero) Native.FPDF_CloseDocument(handle);
            NativeMemory.Free(buffer);
            throw;
        }
    }

    public string GetPageLabel(int index)
    {
        var label = index >= 0 && index < PageCount ? PageLabels[index] : null;
        return label ?? (index + 1).ToString(CultureInfo.CurrentCulture);
    }

    // ---------------------------------------------------------------- page handles

    private IntPtr GetPage(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var node = _pages.First; node is not null; node = node.Next)
        {
            if (node.Value.Index == index)
            {
                _pages.Remove(node);
                _pages.AddFirst(node);
                return node.Value.Page;
            }
        }
        IntPtr page = Native.FPDF_LoadPage(_handle, index);
        if (page == IntPtr.Zero) throw new InvalidDataException($"Page {index + 1} could not be loaded.");
        if (_form != IntPtr.Zero) Native.FORM_OnAfterLoadPage(page, _form);
        _pages.AddFirst((index, page));
        while (_pages.Count > PageCacheSize)
        {
            ClosePage(_pages.Last!.Value.Page);
            _pages.RemoveLast();
        }
        return page;
    }

    private void ClosePage(IntPtr page)
    {
        if (_form != IntPtr.Zero) Native.FORM_OnBeforeClosePage(page, _form);
        Native.FPDF_ClosePage(page);
    }

    private void EvictPage(int index)
    {
        for (var node = _pages.First; node is not null; node = node.Next)
        {
            if (node.Value.Index == index)
            {
                ClosePage(node.Value.Page);
                _pages.Remove(node);
                return;
            }
        }
    }

    private PageTransform GetTransform(int index)
    {
        if (_transforms.TryGetValue(index, out var t)) return t;
        IntPtr page = GetPage(index);
        const int K = 64;
        var size = PageSizes[index];
        int sx = (int)Math.Round(size.Width * K), sy = (int)Math.Round(size.Height * K);
        int x0, y0, x1, y1, x2, y2;
        Native.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, 0, 0, &x0, &y0);
        Native.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, 1000, 0, &x1, &y1);
        Native.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, 0, 1000, &x2, &y2);
        const double U = 1000.0 * K;
        t = new PageTransform((x1 - x0) / U, (y1 - y0) / U, (x2 - x0) / U, (y2 - y0) / U, x0 / (double)K, y0 / (double)K);
        _transforms[index] = t;
        return t;
    }

    public Task<PageTransform> GetTransformAsync(int index) => PdfWorker.Run(() => GetTransform(index), WorkPriority.Visible);

    // ---------------------------------------------------------------- rendering

    /// <summary>
    /// Renders part of a page. <paramref name="scale"/> is in device pixels per point and
    /// <paramref name="region"/> is expressed in device pixels of the rotated page. Cancelling
    /// <paramref name="token"/> also stops a render that has already started. <paramref name="printing"/>
    /// renders the page as it prints, which can hide or show some annotations; <paramref name="annotations"/>
    /// off leaves out annotations and form fields; <paramref name="grayscale"/> renders it grey the way
    /// it's printed in black and white.
    /// </summary>
    public Task<PixelBuffer?> RenderAsync(int index, double scale, int rotation, Windows.Graphics.RectInt32 region, bool invert,
        WorkPriority priority, CancellationToken token, bool printing = false, bool annotations = true, bool grayscale = false)
    {
        return PdfWorker.Run<PixelBuffer?>(() =>
        {
            if (_disposed || region.Width <= 0 || region.Height <= 0 || (long)region.Width * region.Height > MaxRenderPixels) return null;
            IntPtr page = GetPage(index);
            var size = PageSizes[index];
            // PDFium places the whole page, rotated clockwise by quarter turns, at its pixel size;
            // offsetting it by the region origin leaves just the region in the bitmap. Mapping the
            // page onto the clamped display size also applies ClampPageSize's scaling.
            int quarter = ((rotation % 360) + 360) % 360 / 90;
            double pw = quarter % 2 == 0 ? size.Width : size.Height, ph = quarter % 2 == 0 ? size.Height : size.Width;
            int sizeX = (int)Math.Round(pw * scale), sizeY = (int)Math.Round(ph * scale);

            var buffer = new PixelBuffer(region.Width, region.Height);
            bool done = false;
            try
            {
                byte* p = buffer.Pointer;
                IntPtr bitmap = Native.FPDFBitmap_CreateEx(region.Width, region.Height, Native.FPDFBitmap_BGRA, p, buffer.Stride);
                if (bitmap == IntPtr.Zero) return null;
                try
                {
                    int flags = (annotations ? Native.FPDF_ANNOT : 0) | (printing ? Native.FPDF_PRINTING : 0) | (grayscale ? Native.FPDF_GRAYSCALE : 0);
                    RenderOnto(bitmap, p, region.Width, region.Height, page, -region.X, -region.Y, sizeX, sizeY, quarter, flags, token);
                }
                finally
                {
                    Native.FPDFBitmap_Destroy(bitmap);
                }
                if (invert) Invert(p, buffer.Length);
                done = true;
                return buffer;
            }
            finally
            {
                if (!done) buffer.Dispose();
            }
        }, priority, token);
    }

    /// <summary>
    /// Renders a page onto a white BGRA bitmap of <paramref name="width"/> x <paramref name="height"/>
    /// at <paramref name="pixels"/>: its content, then form fields (only FFLDraw draws those), then grey
    /// for <see cref="Native.FPDF_GRAYSCALE"/>. The viewer, the print preview and pages printed as
    /// images all go through here, so the preview shows what those print.
    /// </summary>
    private void RenderOnto(IntPtr bitmap, byte* pixels, int width, int height, IntPtr page, int x, int y, int sizeX, int sizeY, int quarter, int flags, CancellationToken token)
    {
        Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
        RenderProgressively(bitmap, page, x, y, sizeX, sizeY, quarter, flags, token);
        if (_form != IntPtr.Zero && (flags & Native.FPDF_ANNOT) != 0) Native.FPDF_FFLDraw(_form, bitmap, page, x, y, sizeX, sizeY, quarter, flags);
        // Greying the pixels too makes the result grey whatever PDFium's own grey mode covers.
        if ((flags & Native.FPDF_GRAYSCALE) != 0) ToGray(pixels, width * height * 4);
    }

    /// <summary>The token of the render in progress on the worker thread, polled by <see cref="NeedToPauseNow"/>.</summary>
    [ThreadStatic] private static CancellationToken _renderToken;

    [UnmanagedCallersOnly]
    private static int NeedToPauseNow(IFSDK_PAUSE* self) => _renderToken.IsCancellationRequested ? 1 : 0;

    /// <summary>
    /// Renders through PDFium's progressive API, which checks for cancellation between steps, so a
    /// heavy page that scrolled away doesn't hold up the worker until it's finished.
    /// </summary>
    private static void RenderProgressively(IntPtr bitmap, IntPtr page, int x, int y, int sizeX, int sizeY, int quarter, int flags, CancellationToken token)
    {
        var pause = new IFSDK_PAUSE { Version = 1, NeedToPauseNow = &NeedToPauseNow };
        _renderToken = token;
        try
        {
            int status = Native.FPDF_RenderPageBitmap_Start(bitmap, page, x, y, sizeX, sizeY, quarter, flags, &pause);
            // PDFium only pauses when asked to, so a pause here means the render was cancelled.
            if (status == Native.FPDF_RENDER_TOBECONTINUED) token.ThrowIfCancellationRequested();
        }
        finally
        {
            Native.FPDF_RenderPage_Close(page);
            _renderToken = default;
        }
    }

    private static void Invert(byte* p, int length)
    {
        // Map white to a dark grey and black to a light grey; easier on the eyes than a pure inversion.
        const int Dark = 30, Light = 225;
        var lut = stackalloc byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)(Light - i * (Light - Dark) / 255);
        for (int i = 0; i < length; i += 4)
        {
            p[i] = lut[p[i]];
            p[i + 1] = lut[p[i + 1]];
            p[i + 2] = lut[p[i + 2]];
        }
    }

    // ---------------------------------------------------------------- text, links, annotations

    public Task<PageText> GetPageTextAsync(int index, WorkPriority priority = WorkPriority.Normal, CancellationToken token = default)
        => PdfWorker.Run(() => LoadPageText(index), priority, token);

    private PageText LoadPageText(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IntPtr page = GetPage(index);
        var transform = GetTransform(index);
        IntPtr text = Native.FPDFText_LoadPage(page);
        try
        {
            int count = text == IntPtr.Zero ? 0 : Math.Max(0, Native.FPDFText_CountChars(text));
            var codePoints = new int[count];
            var boxes = new Rect[count];
            for (int i = 0; i < count; i++)
            {
                codePoints[i] = (int)Native.FPDFText_GetUnicode(text, i);
                FS_RECTF r;
                if (Native.FPDFText_GetLooseCharBox(text, i, &r) != 0 && (r.Right - r.Left) > 0.01f && Math.Abs(r.Top - r.Bottom) > 0.01f)
                    boxes[i] = transform.ApplyRect(r.Left, r.Top, r.Right, r.Bottom);
                else
                    boxes[i] = Rect.Empty;
            }

            var links = new List<PdfLink>();
            int pos = 0;
            IntPtr link;
            while (Native.FPDFLink_Enumerate(page, &pos, &link) != 0)
            {
                FS_RECTF r;
                if (Native.FPDFLink_GetAnnotRect(link, &r) == 0) continue;
                var destination = ResolveLink(link);
                if (destination is null) continue;
                links.Add(new PdfLink { Bounds = transform.ApplyRect(r.Left, r.Top, r.Right, r.Bottom), Destination = destination });
            }

            if (text != IntPtr.Zero)
            {
                IntPtr web = Native.FPDFLink_LoadWebLinks(text);
                if (web != IntPtr.Zero)
                {
                    int n = Native.FPDFLink_CountWebLinks(web);
                    for (int i = 0; i < n; i++)
                    {
                        int len = Native.FPDFLink_GetURL(web, i, null, 0);
                        if (len <= 1 || len > MaxLinkLength) continue;
                        var chars = new char[len];
                        fixed (char* c = chars) Native.FPDFLink_GetURL(web, i, c, len);
                        string url = new string(chars, 0, len - 1);
                        int rects = Native.FPDFLink_CountRects(web, i);
                        for (int j = 0; j < rects; j++)
                        {
                            double l, t, rr, bb;
                            if (Native.FPDFLink_GetRect(web, i, j, &l, &t, &rr, &bb) == 0) continue;
                            var bounds = transform.ApplyRect(l, t, rr, bb);
                            if (links.Any(x => x.Bounds.Contains(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)))) continue;
                            links.Add(new PdfLink { Bounds = bounds, Destination = new PdfDestination { Uri = url } });
                        }
                    }
                    Native.FPDFLink_CloseWebLinks(web);
                }
            }

            // The viewer reads the text of the pages it shows, so keep it for search too.
            if (_searchTexts[index] is null)
                KeepSearchText(index, string.Create(count, codePoints, static (chars, cps) =>
                {
                    for (int i = 0; i < chars.Length; i++) chars[i] = SearchChar((uint)cps[i]);
                }));
            var (ordered, orderedBoxes, map) = TextLayout.ToReadingOrder(codePoints, boxes);
            return new PageText
            {
                PageIndex = index,
                CodePoints = ordered,
                Boxes = orderedBoxes,
                SourceIndexMap = map,
                Links = [.. links],
                Annotations = ReadAnnotations(index, page, transform),
                Transform = transform,
            };
        }
        finally
        {
            if (text != IntPtr.Zero) Native.FPDFText_ClosePage(text);
        }
    }

    private PdfDestination? ResolveLink(IntPtr link)
    {
        IntPtr dest = Native.FPDFLink_GetDest(_handle, link);
        if (dest != IntPtr.Zero) return ReadDest(dest);
        IntPtr action = Native.FPDFLink_GetAction(link);
        return action == IntPtr.Zero ? null : ReadAction(action);
    }

    private PdfDestination? ReadAction(IntPtr action)
    {
        uint type = Native.FPDFAction_GetType(action);
        if (type == Native.PDFACTION_GOTO)
        {
            IntPtr dest = Native.FPDFAction_GetDest(_handle, action);
            return dest == IntPtr.Zero ? null : ReadDest(dest);
        }
        if (type == Native.PDFACTION_URI)
        {
            uint size = Native.FPDFAction_GetURIPath(_handle, action, null, 0);
            if (size <= 1 || size > MaxLinkLength) return null;
            var buffer = new byte[size];
            fixed (byte* b = buffer) Native.FPDFAction_GetURIPath(_handle, action, b, size);
            return new PdfDestination { Uri = Encoding.UTF8.GetString(buffer, 0, (int)size - 1) };
        }
        return null;
    }

    private PdfDestination? ReadDest(IntPtr dest)
    {
        int page = Native.FPDFDest_GetDestPageIndex(_handle, dest);
        if (page < 0 || page >= PageCount) return null;
        int hasX, hasY, hasZoom;
        float x, y, zoom;
        if (Native.FPDFDest_GetLocationInPage(dest, &hasX, &hasY, &hasZoom, &x, &y, &zoom) != 0)
        {
            return new PdfDestination
            {
                PageIndex = page,
                UserX = hasX != 0 ? x : null,
                UserY = hasY != 0 ? y : null,
            };
        }
        return new PdfDestination { PageIndex = page };
    }

    private static readonly byte[] KeyContents = "Contents\0"u8.ToArray();
    private static readonly byte[] KeyAuthor = "T\0"u8.ToArray();
    private static readonly byte[] KeyModified = "M\0"u8.ToArray();
    private static readonly byte[] KeyName = "NM\0"u8.ToArray();
    private static readonly byte[] KeySubject = "Subj\0"u8.ToArray();
    private static readonly byte[] KeyCreated = "CreationDate\0"u8.ToArray();
    /// <summary>The text entries a removed annotation is put back with.</summary>
    private static readonly byte[][] RestoredKeys = [KeyName, KeyContents, KeyAuthor, KeySubject, KeyCreated, KeyModified];

    private static string GetAnnotString(IntPtr annot, byte[] key, bool exact = false)
    {
        fixed (byte* k = key)
        {
            byte* kp = k;
            return Native.ReadUtf16((b, l) => Native.FPDFAnnot_GetStringValue(annot, kp, (void*)b, l), exact);
        }
    }

    private static void SetAnnotString(IntPtr annot, byte[] key, string value)
    {
        fixed (byte* k = key)
        fixed (char* v = value + "\0")
        {
            Native.FPDFAnnot_SetStringValue(annot, k, v);
        }
    }

    private static PdfAnnotation[] ReadAnnotations(int index, IntPtr page, PageTransform transform)
    {
        var result = new List<PdfAnnotation>();
        int count = Native.FPDFPage_GetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            IntPtr annot = Native.FPDFPage_GetAnnot(page, i);
            if (annot == IntPtr.Zero) continue;
            try
            {
                if (ReadAnnotation(index, i, annot, transform) is { } annotation) result.Add(annotation);
            }
            finally
            {
                Native.FPDFPage_CloseAnnot(annot);
            }
        }
        return [.. result];
    }

    /// <summary>Annotation <paramref name="i"/> on page <paramref name="index"/>, or null if it's of a kind Recto doesn't show.</summary>
    private static PdfAnnotation? ReadAnnotation(int index, int i, IntPtr annot, PageTransform transform)
    {
        int subtype = Native.FPDFAnnot_GetSubtype(annot);
        AnnotationKind? kind = subtype switch
        {
            Native.FPDF_ANNOT_TEXT => AnnotationKind.Note,
            Native.FPDF_ANNOT_HIGHLIGHT => AnnotationKind.Highlight,
            Native.FPDF_ANNOT_UNDERLINE => AnnotationKind.Underline,
            Native.FPDF_ANNOT_STRIKEOUT => AnnotationKind.StrikeOut,
            Native.FPDF_ANNOT_SQUIGGLY => AnnotationKind.Squiggly,
            Native.FPDF_ANNOT_FREETEXT => AnnotationKind.FreeText,
            Native.FPDF_ANNOT_SQUARE or Native.FPDF_ANNOT_CIRCLE => AnnotationKind.Shape,
            Native.FPDF_ANNOT_INK => AnnotationKind.Ink,
            Native.FPDF_ANNOT_STAMP => AnnotationKind.Stamp,
            Native.FPDF_ANNOT_FILEATTACHMENT => AnnotationKind.Attachment,
            _ => null,
        };
        if (kind is null) return null;
        FS_RECTF r;
        if (Native.FPDFAnnot_GetRect(annot, &r) == 0) return null;
        uint cr = 255, cg = 214, cb = 10, ca = 255;
        Native.FPDFAnnot_GetColor(annot, Native.FPDFANNOT_COLORTYPE_Color, &cr, &cg, &cb, &ca);
        string name = GetAnnotString(annot, KeyName);
        var ink = subtype == Native.FPDF_ANNOT_INK && IsOwn(name) ? ReadInk(annot, transform) : null;
        var freeText = subtype == Native.FPDF_ANNOT_FREETEXT && IsOwn(name) ? ReadFreeText(annot, transform) : null;
        var drawn = ink?.Color ?? freeText?.Color;
        return new PdfAnnotation
        {
            PageIndex = index,
            Index = i,
            Subtype = subtype,
            UserRect = UserRect(r),
            Name = name,
            Kind = kind.Value,
            Bounds = transform.ApplyRect(r.Left, r.Top, r.Right, r.Bottom),
            Contents = GetAnnotString(annot, KeyContents),
            Author = GetAnnotString(annot, KeyAuthor),
            Modified = ParsePdfDate(GetAnnotString(annot, KeyModified)),
            Color = drawn ?? Color.FromArgb(255, (byte)cr, (byte)cg, (byte)cb),
            Ink = ink,
            FreeText = freeText,
        };
    }

    /// <summary>Reads the annotation just added to the end of <paramref name="page"/>'s /Annots.</summary>
    private PdfAnnotation ReadCreated(int index, IntPtr page, IntPtr annot) =>
        ReadAnnotation(index, Native.FPDFPage_GetAnnotCount(page) - 1, annot, GetTransform(index))
        ?? throw new InvalidOperationException("The annotation could not be read back.");

    private PdfAnnotation[] ReadAnnotations(int index) => _disposed ? [] : ReadAnnotations(index, GetPage(index), GetTransform(index));

    /// <summary>Adds a highlight covering <paramref name="rects"/> (display points).</summary>
    public Task<PdfAnnotation> AddHighlightAsync(int index, IReadOnlyList<Rect> rects, Color color)
    {
        return PdfWorker.Run(() =>
        {
            IntPtr page = GetPage(index);
            var t = GetTransform(index);
            IntPtr annot = Native.FPDFPage_CreateAnnot(page, Native.FPDF_ANNOT_HIGHLIGHT);
            if (annot == IntPtr.Zero) throw new InvalidOperationException("The highlight could not be created.");
            try
            {
                Native.FPDFAnnot_SetColor(annot, Native.FPDFANNOT_COLORTYPE_Color, color.R, color.G, color.B, 255);
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var rect in rects)
                {
                    var tl = t.Invert(rect.Left, rect.Top);
                    var tr = t.Invert(rect.Right, rect.Top);
                    var bl = t.Invert(rect.Left, rect.Bottom);
                    var br = t.Invert(rect.Right, rect.Bottom);
                    var quad = new FS_QUADPOINTSF
                    {
                        X1 = (float)tl.X, Y1 = (float)tl.Y, X2 = (float)tr.X, Y2 = (float)tr.Y,
                        X3 = (float)bl.X, Y3 = (float)bl.Y, X4 = (float)br.X, Y4 = (float)br.Y,
                    };
                    Native.FPDFAnnot_AppendAttachmentPoints(annot, &quad);
                    foreach (var p in new[] { tl, tr, bl, br })
                    {
                        minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                        minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                    }
                }
                var bounds = new FS_RECTF { Left = (float)minX, Bottom = (float)minY, Right = (float)maxX, Top = (float)maxY };
                Native.FPDFAnnot_SetRect(annot, &bounds);
                Native.FPDFAnnot_SetFlags(annot, 4 /* print */);
                StampAnnotation(annot, "");
                MarkEdited();
                return ReadCreated(index, page, annot);
            }
            finally
            {
                Native.FPDFPage_CloseAnnot(annot);
            }
        }, WorkPriority.Visible);
    }

    /// <summary>Adds a sticky note whose top-left corner is at <paramref name="at"/> (display points).</summary>
    public Task<PdfAnnotation> AddNoteAsync(int index, Point at, Color color, string contents)
    {
        return PdfWorker.Run(() =>
        {
            IntPtr page = GetPage(index);
            var t = GetTransform(index);
            IntPtr annot = Native.FPDFPage_CreateAnnot(page, Native.FPDF_ANNOT_TEXT);
            if (annot == IntPtr.Zero) throw new InvalidOperationException("The note could not be created.");
            try
            {
                var p1 = t.Invert(at.X, at.Y);
                var p2 = t.Invert(at.X + 20, at.Y + 20);
                var bounds = new FS_RECTF
                {
                    Left = (float)Math.Min(p1.X, p2.X), Right = (float)Math.Max(p1.X, p2.X),
                    Bottom = (float)Math.Min(p1.Y, p2.Y), Top = (float)Math.Max(p1.Y, p2.Y),
                };
                Native.FPDFAnnot_SetRect(annot, &bounds);
                Native.FPDFAnnot_SetColor(annot, Native.FPDFANNOT_COLORTYPE_Color, color.R, color.G, color.B, 255);
                Native.FPDFAnnot_SetFlags(annot, 4 | 8 | 16 /* print, no zoom, no rotate */);
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

    private const string RectoNamePrefix = "recto-";

    private static void StampAnnotation(IntPtr annot, string contents)
    {
        SetAnnotString(annot, KeyName, $"{RectoNamePrefix}{Guid.NewGuid():N}");
        SetAnnotString(annot, KeyContents, contents);
        SetAnnotString(annot, KeyAuthor, Environment.UserName);
        SetAnnotString(annot, KeyModified, FormatPdfDate(DateTimeOffset.Now));
    }

    private static Rect UserRect(FS_RECTF r) => new(new Point(r.Left, r.Bottom), new Point(r.Right, r.Top));

    private static bool IsSameAnnotation(IntPtr annot, PdfAnnotation annotation)
    {
        if (Native.FPDFAnnot_GetSubtype(annot) != annotation.Subtype) return false;
        // Recto's own names are unique, so they tell the annotation apart even if PDFium moved its /Rect
        // while drawing it, which it does to notes.
        if (annotation.Name.StartsWith(RectoNamePrefix, StringComparison.Ordinal)) return GetAnnotString(annot, KeyName) == annotation.Name;
        FS_RECTF r;
        if (Native.FPDFAnnot_GetRect(annot, &r) == 0) return false;
        Rect current = UserRect(r), recorded = annotation.UserRect;
        const double Tolerance = 0.01;
        // PDFium resizes a note when it draws its appearance, as it does for one put back by undo, and keeps its
        // bottom-left corner.
        bool sized = annotation.Subtype == Native.FPDF_ANNOT_TEXT
            || Math.Abs(current.Width - recorded.Width) < Tolerance && Math.Abs(current.Height - recorded.Height) < Tolerance;
        return Math.Abs(current.X - recorded.X) < Tolerance && Math.Abs(current.Y - recorded.Y) < Tolerance && sized
            && GetAnnotString(annot, KeyName) == annotation.Name;
    }

    /// <summary>
    /// Current index of <paramref name="annotation"/> in its page's /Annots array, or -1 if it's gone.
    /// The recorded index is only a hint: earlier deletions shift the array.
    /// </summary>
    private int FindAnnotation(IntPtr page, PdfAnnotation annotation)
    {
        int count = Native.FPDFPage_GetAnnotCount(page);
        int fallback = -1;
        // Start at the recorded index, then try the rest of the page.
        for (int n = 0; n < count; n++)
        {
            int i = (annotation.Index + n) % count;
            if (i < 0) i += count;
            IntPtr annot = Native.FPDFPage_GetAnnot(page, i);
            if (annot == IntPtr.Zero) continue;
            try
            {
                if (!IsSameAnnotation(annot, annotation)) continue;
                // Several identical annotations without /NM: prefer the one with the same text.
                if (n == 0 || GetAnnotString(annot, KeyContents) == annotation.Contents) return i;
                if (fallback < 0) fallback = i;
            }
            finally
            {
                Native.FPDFPage_CloseAnnot(annot);
            }
        }
        return fallback;
    }

    /// <summary>
    /// Sets the text of <paramref name="annotation"/>. Returns the text it replaced, or null if the annotation is gone
    /// or already had this text.
    /// </summary>
    public Task<string?> SetAnnotationContentsAsync(PdfAnnotation annotation, string contents)
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
                string previous = GetAnnotString(annot, KeyContents, exact: true);
                if (previous == contents) return null;
                // A text box of Recto's draws its contents, so they change on the page too.
                if (Native.FPDFAnnot_GetSubtype(annot) == Native.FPDF_ANNOT_FREETEXT && IsOwn(GetAnnotString(annot, KeyName))
                    && ReadFreeText(annot, GetTransform(annotation.PageIndex)) is { } text)
                {
                    WriteFreeText(annot, text with { Text = contents }, GetTransform(annotation.PageIndex), PageSizes[annotation.PageIndex].Height);
                }
                else
                {
                    SetAnnotString(annot, KeyContents, contents);
                }
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

    /// <summary>
    /// Removes <paramref name="annotation"/>. Returns what <see cref="RestoreAnnotationAsync"/> needs to put it back, or
    /// null if it was gone already or is of a kind that can't be put back.
    /// </summary>
    public Task<AnnotationSnapshot?> RemoveAnnotationAsync(PdfAnnotation annotation)
    {
        return PdfWorker.Run(() =>
        {
            IntPtr page = GetPage(annotation.PageIndex);
            int index = FindAnnotation(page, annotation);
            if (index < 0) return null;
            var snapshot = TakeSnapshot(annotation.PageIndex, page, index);
            if (Native.FPDFPage_RemoveAnnot(page, index) == 0) return null;
            MarkEdited();
            // Force PDFium to rebuild the page's cached annotation list.
            EvictPage(annotation.PageIndex);
            return snapshot;
        }, WorkPriority.Visible);
    }

    /// <summary>
    /// Kinds that can be put back once removed: PDFium can create them, and draw them from what a snapshot keeps.
    /// Others, such as stamps and other programs' drawings, depend on their appearance stream and its resources.
    /// </summary>
    private static bool IsRestorable(int subtype) => subtype is Native.FPDF_ANNOT_TEXT or Native.FPDF_ANNOT_HIGHLIGHT
        or Native.FPDF_ANNOT_UNDERLINE or Native.FPDF_ANNOT_SQUIGGLY or Native.FPDF_ANNOT_STRIKEOUT;

    private AnnotationSnapshot? TakeSnapshot(int index, IntPtr page, int i)
    {
        IntPtr annot = Native.FPDFPage_GetAnnot(page, i);
        if (annot == IntPtr.Zero) return null;
        try
        {
            int subtype = Native.FPDFAnnot_GetSubtype(annot);
            FS_RECTF rect;
            // Recto can draw its own drawings and text boxes again.
            bool own = IsOwn(GetAnnotString(annot, KeyName));
            var t = GetTransform(index);
            var ink = own && subtype == Native.FPDF_ANNOT_INK ? ReadInk(annot, t) : null;
            var freeText = own && subtype == Native.FPDF_ANNOT_FREETEXT ? ReadFreeText(annot, t) : null;
            if (!IsRestorable(subtype) && ink is null && freeText is null || Native.FPDFAnnot_GetRect(annot, &rect) == 0) return null;
            // PDFium only reads the color of an annotation without an appearance stream. This one is about to be
            // removed, and the one put back gets its appearance drawn anew from the color.
            Native.FPDFAnnot_SetAP(annot, Native.FPDF_ANNOT_APPEARANCEMODE_NORMAL, null);
            uint r, g, b, a;
            bool hasColor = Native.FPDFAnnot_GetColor(annot, Native.FPDFANNOT_COLORTYPE_Color, &r, &g, &b, &a) != 0;
            var quads = new List<FS_QUADPOINTSF>();
            nuint count = Native.FPDFAnnot_CountAttachmentPoints(annot);
            for (nuint q = 0; q < count; q++)
            {
                FS_QUADPOINTSF quad;
                if (Native.FPDFAnnot_GetAttachmentPoints(annot, q, &quad) != 0) quads.Add(quad);
            }
            return new AnnotationSnapshot
            {
                PageIndex = index,
                Subtype = subtype,
                Rect = rect,
                Color = hasColor ? (r, g, b, a) : null,
                Quads = [.. quads],
                Flags = Native.FPDFAnnot_GetFlags(annot),
                Strings = [.. RestoredKeys.Select(key => (key, GetAnnotString(annot, key, exact: true))).Where(s => s.Item2.Length > 0)],
                Ink = ink,
                FreeText = freeText,
            };
        }
        finally
        {
            Native.FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>
    /// Puts back an annotation removed by <see cref="RemoveAnnotationAsync"/>, on top of the others on its page:
    /// PDFium only appends to /Annots, and has no way to move an annotation back to where it was.
    /// </summary>
    public Task<PdfAnnotation> RestoreAnnotationAsync(AnnotationSnapshot snapshot)
    {
        return PdfWorker.Run(() =>
        {
            IntPtr page = GetPage(snapshot.PageIndex);
            IntPtr annot = Native.FPDFPage_CreateAnnot(page, snapshot.Subtype);
            if (annot == IntPtr.Zero) throw new InvalidOperationException("The annotation could not be put back.");
            try
            {
                var rect = snapshot.Rect;
                Native.FPDFAnnot_SetRect(annot, &rect);
                if (snapshot.Ink is { } ink) WriteInk(annot, ink, GetTransform(snapshot.PageIndex));
                else if (snapshot.FreeText is { } text) WriteFreeText(annot, text, GetTransform(snapshot.PageIndex), PageSizes[snapshot.PageIndex].Height);
                else if (snapshot.Color is { } c) Native.FPDFAnnot_SetColor(annot, Native.FPDFANNOT_COLORTYPE_Color, c.R, c.G, c.B, c.A);
                foreach (var q in snapshot.Quads)
                {
                    var quad = q;
                    Native.FPDFAnnot_AppendAttachmentPoints(annot, &quad);
                }
                Native.FPDFAnnot_SetFlags(annot, snapshot.Flags);
                foreach (var (key, value) in snapshot.Strings) SetAnnotString(annot, key, value);
                MarkEdited();
                return ReadCreated(snapshot.PageIndex, page, annot);
            }
            finally
            {
                Native.FPDFPage_CloseAnnot(annot);
            }
        }, WorkPriority.Visible);
    }

    private void MarkEdited()
    {
        IsModified = true;
        Interlocked.Increment(ref _editCount);
    }

    // ---------------------------------------------------------------- search

    /// <summary>
    /// A page's characters for <see cref="TextSearch"/>, one per PDFium character index. Read on the first
    /// search and kept, as loading a page and its text is nearly all of what searching it costs.
    /// </summary>
    public Task<string> GetSearchTextAsync(int index, CancellationToken token, WorkPriority priority = WorkPriority.Background)
    {
        if (Volatile.Read(ref _searchTexts[index]) is { } cached) return Task.FromResult(cached);
        return PdfWorker.Run(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_searchTexts[index] is { } text) return text;
            // Reuse the page if it's open, but don't add it to the cache: a search would push out the pages on screen.
            IntPtr page = IntPtr.Zero;
            foreach (var (i, p) in _pages)
            {
                if (i == index) { page = p; break; }
            }
            bool owned = page == IntPtr.Zero;
            if (owned) page = Native.FPDF_LoadPage(_handle, index);
            try
            {
                text = page == IntPtr.Zero ? "" : ReadSearchText(page);
            }
            finally
            {
                if (owned && page != IntPtr.Zero) Native.FPDF_ClosePage(page);
            }
            KeepSearchText(index, text);
            return text;
        }, priority, token);
    }

    /// <summary>Characters in the whole document, estimated from the pages read for search so far; null before any are.</summary>
    public long? EstimatedTextLength
    {
        get
        {
            int pages = Volatile.Read(ref _searchTextPages);
            return pages == 0 ? null : Interlocked.Read(ref _searchTextChars) * PageCount / pages;
        }
    }

    private void KeepSearchText(int index, string text)
    {
        Volatile.Write(ref _searchTexts[index], text);
        Interlocked.Add(ref _searchTextChars, text.Length);
        Interlocked.Increment(ref _searchTextPages);
    }

    private static string ReadSearchText(IntPtr page)
    {
        IntPtr text = Native.FPDFText_LoadPage(page);
        if (text == IntPtr.Zero) return "";
        try
        {
            // FPDFText_GetText would be one call, but it leaves out some characters, so its indexes drift from PDFium's.
            return string.Create(Math.Max(0, Native.FPDFText_CountChars(text)), text, static (chars, handle) =>
            {
                for (int i = 0; i < chars.Length; i++) chars[i] = SearchChar(Native.FPDFText_GetUnicode(handle, i));
            });
        }
        finally
        {
            Native.FPDFText_ClosePage(text);
        }
    }

    private static char SearchChar(uint cp) => cp <= 0xFFFF ? (char)cp : '\uFFFD';

    // ---------------------------------------------------------------- outline, attachments, metadata

    public Task<List<OutlineItem>> GetOutlineAsync()
    {
        return PdfWorker.Run(() =>
        {
            var root = new List<OutlineItem>();
            var visited = new HashSet<IntPtr>();
            int budget = 20000;
            ReadOutline(IntPtr.Zero, root, visited, ref budget, 0);
            return root;
        }, WorkPriority.Normal);
    }

    private void ReadOutline(IntPtr parent, List<OutlineItem> into, HashSet<IntPtr> visited, ref int budget, int depth)
    {
        if (depth > 32) return;
        for (IntPtr bm = Native.FPDFBookmark_GetFirstChild(_handle, parent); bm != IntPtr.Zero && budget > 0;
             bm = Native.FPDFBookmark_GetNextSibling(_handle, bm))
        {
            if (!visited.Add(bm)) break;
            budget--;
            IntPtr b = bm;
            string title = Native.ReadUtf16((buf, len) => Native.FPDFBookmark_GetTitle(b, (void*)buf, len));
            PdfDestination? destination = null;
            IntPtr dest = Native.FPDFBookmark_GetDest(_handle, bm);
            if (dest != IntPtr.Zero)
            {
                destination = ReadDest(dest);
            }
            else
            {
                IntPtr action = Native.FPDFBookmark_GetAction(bm);
                if (action != IntPtr.Zero) destination = ReadAction(action);
            }
            var item = new OutlineItem { Title = title.Replace('\r', ' ').Replace('\n', ' ').Trim(), Destination = destination };
            ReadOutline(bm, item.Children, visited, ref budget, depth + 1);
            into.Add(item);
        }
    }

    public Task<List<PdfAttachment>> GetAttachmentsAsync()
    {
        return PdfWorker.Run(() =>
        {
            var list = new List<PdfAttachment>();
            int count = Native.FPDFDoc_GetAttachmentCount(_handle);
            for (int i = 0; i < count; i++)
            {
                IntPtr a = Native.FPDFDoc_GetAttachment(_handle, i);
                if (a == IntPtr.Zero) continue;
                string name = Native.ReadUtf16((b, l) => Native.FPDFAttachment_GetName(a, (void*)b, l));
                uint size = 0;
                Native.FPDFAttachment_GetFile(a, null, 0, &size);
                list.Add(new PdfAttachment { Index = i, Name = string.IsNullOrEmpty(name) ? $"Attachment {i + 1}" : name, Size = size });
            }
            return list;
        }, WorkPriority.Normal);
    }

    public Task<byte[]> GetAttachmentDataAsync(PdfAttachment attachment)
    {
        return PdfWorker.Run(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IntPtr a = Native.FPDFDoc_GetAttachment(_handle, attachment.Index);
            // Not an empty file: the caller would write it out as the attachment.
            if (a == IntPtr.Zero) throw new IOException("The attachment couldn't be found in the document.");
            uint size = 0;
            // Fails, leaving the size alone, when the embedded file's data can't be read.
            if (Native.FPDFAttachment_GetFile(a, null, 0, &size) == 0) throw new IOException("The attachment couldn't be read from the document.");
            if (size > MaxAttachmentBytes) throw new IOException("The attachment is too large to extract. Recto extracts files up to 2 GB.");
            byte[] data;
            try
            {
                data = new byte[size];
            }
            catch (OutOfMemoryException)
            {
                throw new IOException("There isn't enough memory to extract the attachment.");
            }
            uint read = 0;
            fixed (byte* p = data)
            {
                if (Native.FPDFAttachment_GetFile(a, p, size, &read) == 0 || read != size) throw new IOException("The attachment couldn't be read from the document.");
            }
            return data;
        }, WorkPriority.Visible);
    }

    private DocumentInfo ReadInfo()
    {
        string Meta(string tag)
        {
            var key = Encoding.ASCII.GetBytes(tag + "\0");
            fixed (byte* k = key)
            {
                byte* kp = k;
                return Native.ReadUtf16((b, l) => Native.FPDF_GetMetaText(_handle, kp, (void*)b, l)).Trim();
            }
        }
        int version = 0;
        Native.FPDF_GetFileVersion(_handle, &version);
        return new DocumentInfo
        {
            Title = Meta("Title"),
            Author = Meta("Author"),
            Subject = Meta("Subject"),
            Keywords = Meta("Keywords"),
            Creator = Meta("Creator"),
            Producer = Meta("Producer"),
            Created = ParsePdfDate(Meta("CreationDate")),
            Modified = ParsePdfDate(Meta("ModDate")),
            Version = version > 0 ? $"{version / 10}.{version % 10}" : "",
            // -1 means there's no security handler. The permission flags can't tell: a document
            // opened with its owner password reports all of them, just like an unencrypted one.
            IsEncrypted = Native.FPDF_GetSecurityHandlerRevision(_handle) >= 0,
        };
    }

    public static DateTimeOffset? ParsePdfDate(string value)
    {
        // D:YYYYMMDDHHmmSSOHH'mm'
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        int Part(int start, int length, int fallback) =>
            s.Length >= start + length && int.TryParse(s.AsSpan(start, length), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        int year = Part(0, 4, -1);
        if (year < 1) return null;
        int month = Math.Clamp(Part(4, 2, 1), 1, 12), day = Math.Clamp(Part(6, 2, 1), 1, 31);
        int hour = Math.Clamp(Part(8, 2, 0), 0, 23), minute = Math.Clamp(Part(10, 2, 0), 0, 59), second = Math.Clamp(Part(12, 2, 0), 0, 59);
        var offset = TimeSpan.Zero;
        if (s.Length > 14 && (s[14] == '+' || s[14] == '-'))
        {
            int oh = Part(15, 2, 0), om = s.Length >= 20 ? Part(18, 2, 0) : 0;
            offset = new TimeSpan(oh, om, 0);
            if (s[14] == '-') offset = -offset;
        }
        try
        {
            day = Math.Min(day, DateTime.DaysInMonth(year, month));
            return new DateTimeOffset(year, month, day, hour, minute, second, offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string FormatPdfDate(DateTimeOffset date)
    {
        var o = date.Offset;
        return $"D:{date:yyyyMMddHHmmss}{(o < TimeSpan.Zero ? '-' : '+')}{Math.Abs(o.Hours):00}'{Math.Abs(o.Minutes):00}'";
    }

    // ---------------------------------------------------------------- saving

    [ThreadStatic] private static Stream? _saveStream;

    [UnmanagedCallersOnly]
    private static int WriteBlock(FPDF_FILEWRITE* self, void* data, uint size)
    {
        try
        {
            _saveStream!.Write(new ReadOnlySpan<byte>(data, (int)size));
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Writes the document to <paramref name="path"/> as an incremental update: the original bytes
    /// are copied unchanged and the edited objects are appended, so signatures, form data and
    /// anything else PDFium didn't load survive.
    /// </summary>
    public Task SaveAsync(string path)
    {
        return PdfWorker.Run(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string temp = path + ".recto-tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    _saveStream = stream;
                    try
                    {
                        var writer = new FPDF_FILEWRITE { Version = 1, WriteBlock = &WriteBlock };
                        if (Native.FPDF_SaveAsCopy(_handle, &writer, Native.FPDF_INCREMENTAL) == 0)
                            throw new IOException("The document could not be saved.");
                    }
                    finally
                    {
                        _saveStream = null;
                    }
                }
                CopyZoneIdentifier(FilePath, temp);
                MoveIntoPlace(temp, path);
            }
            catch
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
                throw;
            }
            // Like GNOME Papers, the edits count as saved once they're in a copy too.
            IsModified = false;
        }, WorkPriority.Visible);
    }

    /// <summary>
    /// Puts the finished file at <paramref name="path"/>. An existing file is replaced in place, which
    /// keeps what belongs to it rather than to its contents: permissions, attributes, creation time
    /// and alternate data streams. A plain move would give the file the temporary file's instead.
    /// </summary>
    private static void MoveIntoPlace(string temp, string path)
    {
        if (!File.Exists(path))
        {
            File.Move(temp, path);
            return;
        }
        try
        {
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (IOException) when (File.Exists(temp) && File.Exists(path))
        {
            // Some file systems and network shares can't replace in place. Both files are still
            // there, so nothing was lost; fall back to the plain move.
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>
    /// Carries the source file's Mark of the Web over to the saved copy, so a document from the
    /// internet doesn't turn local once annotated.
    /// </summary>
    private static void CopyZoneIdentifier(string from, string to)
    {
        try
        {
            File.WriteAllText(to + ":Zone.Identifier", File.ReadAllText(from + ":Zone.Identifier"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // No mark, or a file system without alternate data streams (where a copy in Explorer loses it too).
        }
    }

    // ---------------------------------------------------------------- lifetime

    public void Dispose()
    {
        if (_disposed) return;
        PdfWorker.Run(() =>
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var (_, page) in _pages) ClosePage(page);
            _pages.Clear();
            if (_form != IntPtr.Zero) Native.FPDFDOC_ExitFormFillEnvironment(_form);
            _form = IntPtr.Zero;
            NativeMemory.Free(_formInfo);
            _formInfo = null;
            Native.FPDF_CloseDocument(_handle);
            _handle = IntPtr.Zero;
            NativeMemory.Free(_buffer);
            _buffer = null;
        }, WorkPriority.Visible);
    }
}
