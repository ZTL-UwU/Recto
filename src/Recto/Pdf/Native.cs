using System.Runtime.InteropServices;

namespace Recto.Pdf;

[StructLayout(LayoutKind.Sequential)]
internal struct FS_RECTF
{
    public float Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_POINTF
{
    public float X, Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_SIZEF
{
    public float Width, Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FS_QUADPOINTSF
{
    public float X1, Y1, X2, Y2, X3, Y3, X4, Y4;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FPDF_LIBRARY_CONFIG
{
    public int Version;
    public IntPtr UserFontPaths;
    public IntPtr Isolate;
    public uint V8EmbedderSlot;
    public IntPtr Platform;
    public int RendererType;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FPDF_FILEWRITE
{
    public int Version;
    public delegate* unmanaged<FPDF_FILEWRITE*, void*, uint, int> WriteBlock;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct IFSDK_PAUSE
{
    public int Version;
    public delegate* unmanaged<IFSDK_PAUSE*, int> NeedToPauseNow;
    public void* User;
}

/// <summary>Raw PDFium entry points. Every call must happen on the <see cref="PdfWorker"/> thread.</summary>
internal static unsafe partial class Native
{
    private const string Lib = "pdfium";

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_GRAYSCALE = 0x08;
    public const int FPDF_PRINTING = 0x800;

    public const int FPDFBitmap_BGRA = 4;

    public const int FPDF_ERR_PASSWORD = 4;

    public const uint FPDF_INCREMENTAL = 1;

    public const uint PDFACTION_GOTO = 1;
    public const uint PDFACTION_URI = 3;

    public const int FPDF_ANNOT_TEXT = 1;
    public const int FPDF_ANNOT_FREETEXT = 3;
    public const int FPDF_ANNOT_SQUARE = 5;
    public const int FPDF_ANNOT_CIRCLE = 6;
    public const int FPDF_ANNOT_HIGHLIGHT = 9;
    public const int FPDF_ANNOT_UNDERLINE = 10;
    public const int FPDF_ANNOT_SQUIGGLY = 11;
    public const int FPDF_ANNOT_STRIKEOUT = 12;
    public const int FPDF_ANNOT_STAMP = 13;
    public const int FPDF_ANNOT_INK = 15;
    public const int FPDF_ANNOT_FILEATTACHMENT = 17;
    public const int FPDF_ANNOT_WIDGET = 20;

    public const int FPDFANNOT_COLORTYPE_Color = 0;
    public const int FPDF_ANNOT_APPEARANCEMODE_NORMAL = 0;

    [LibraryImport(Lib)] public static partial void FPDF_InitLibraryWithConfig(FPDF_LIBRARY_CONFIG* config);
    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();

    [LibraryImport(Lib)] public static partial IntPtr FPDF_LoadMemDocument64(void* data, nuint size, byte* password);
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(IntPtr document);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(IntPtr document);
    [LibraryImport(Lib)] public static partial int FPDF_GetPageSizeByIndexF(IntPtr document, int index, FS_SIZEF* size);
    [LibraryImport(Lib)] public static partial uint FPDF_GetMetaText(IntPtr document, byte* tag, void* buffer, uint buflen);
    [LibraryImport(Lib)] public static partial int FPDF_GetFileVersion(IntPtr document, int* version);
    [LibraryImport(Lib)] public static partial uint FPDF_GetPageLabel(IntPtr document, int index, void* buffer, uint buflen);
    [LibraryImport(Lib)] public static partial int FPDF_GetSecurityHandlerRevision(IntPtr document);
    [LibraryImport(Lib)] public static partial int FPDF_SaveAsCopy(IntPtr document, FPDF_FILEWRITE* fileWrite, uint flags);

    [LibraryImport(Lib)] public static partial IntPtr FPDF_LoadPage(IntPtr document, int index);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(IntPtr page);
    [LibraryImport(Lib)] public static partial int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, int* deviceX, int* deviceY);

    [LibraryImport(Lib)] public static partial IntPtr FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [LibraryImport(Lib)] public static partial int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(IntPtr bitmap);

    public const int FPDF_RENDER_TOBECONTINUED = 1;
    /// <summary>Starts a render that asks <paramref name="pause"/> whether to stop between steps. Finish with <see cref="FPDF_RenderPage_Close"/>.</summary>
    [LibraryImport(Lib)] public static partial int FPDF_RenderPageBitmap_Start(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags, IFSDK_PAUSE* pause);
    [LibraryImport(Lib)] public static partial void FPDF_RenderPage_Close(IntPtr page);
    /// <summary>Draws a page onto a GDI device context, such as a printer's, as vector output where it can.</summary>
    [LibraryImport(Lib)] public static partial void FPDF_RenderPage(IntPtr dc, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    /// <summary>
    /// Size of the zeroed block passed as FPDF_FORMFILLINFO. Only <c>version</c> is set; every
    /// callback stays null, which PDFium checks before calling. Generously larger than the struct.
    /// </summary>
    public const int FormFillInfoSize = 512;
    [LibraryImport(Lib)] public static partial IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, void* formInfo);
    [LibraryImport(Lib)] public static partial void FPDFDOC_ExitFormFillEnvironment(IntPtr form);
    [LibraryImport(Lib)] public static partial void FORM_OnAfterLoadPage(IntPtr page, IntPtr form);
    [LibraryImport(Lib)] public static partial void FORM_OnBeforeClosePage(IntPtr page, IntPtr form);
    [LibraryImport(Lib)] public static partial void FPDF_FFLDraw(IntPtr form, IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Lib)] public static partial IntPtr FPDFText_LoadPage(IntPtr page);
    [LibraryImport(Lib)] public static partial void FPDFText_ClosePage(IntPtr textPage);
    [LibraryImport(Lib)] public static partial int FPDFText_CountChars(IntPtr textPage);
    [LibraryImport(Lib)] public static partial uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFText_GetLooseCharBox(IntPtr textPage, int index, FS_RECTF* rect);

    [LibraryImport(Lib)] public static partial IntPtr FPDFLink_LoadWebLinks(IntPtr textPage);
    [LibraryImport(Lib)] public static partial int FPDFLink_CountWebLinks(IntPtr linkPage);
    [LibraryImport(Lib)] public static partial int FPDFLink_GetURL(IntPtr linkPage, int index, char* buffer, int buflen);
    [LibraryImport(Lib)] public static partial int FPDFLink_CountRects(IntPtr linkPage, int index);
    [LibraryImport(Lib)] public static partial int FPDFLink_GetRect(IntPtr linkPage, int index, int rectIndex, double* left, double* top, double* right, double* bottom);
    [LibraryImport(Lib)] public static partial void FPDFLink_CloseWebLinks(IntPtr linkPage);

    [LibraryImport(Lib)] public static partial int FPDFLink_Enumerate(IntPtr page, int* startPos, IntPtr* link);
    [LibraryImport(Lib)] public static partial int FPDFLink_GetAnnotRect(IntPtr link, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);
    [LibraryImport(Lib)] public static partial IntPtr FPDFLink_GetAction(IntPtr link);
    [LibraryImport(Lib)] public static partial uint FPDFAction_GetType(IntPtr action);
    [LibraryImport(Lib)] public static partial IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);
    [LibraryImport(Lib)] public static partial uint FPDFAction_GetURIPath(IntPtr document, IntPtr action, void* buffer, uint buflen);
    [LibraryImport(Lib)] public static partial int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);
    [LibraryImport(Lib)] public static partial int FPDFDest_GetLocationInPage(IntPtr dest, int* hasX, int* hasY, int* hasZoom, float* x, float* y, float* zoom);

    [LibraryImport(Lib)] public static partial IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);
    [LibraryImport(Lib)] public static partial IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);
    [LibraryImport(Lib)] public static partial uint FPDFBookmark_GetTitle(IntPtr bookmark, void* buffer, uint buflen);
    [LibraryImport(Lib)] public static partial IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);
    [LibraryImport(Lib)] public static partial IntPtr FPDFBookmark_GetAction(IntPtr bookmark);

    [LibraryImport(Lib)] public static partial int FPDFDoc_GetAttachmentCount(IntPtr document);
    [LibraryImport(Lib)] public static partial IntPtr FPDFDoc_GetAttachment(IntPtr document, int index);
    [LibraryImport(Lib)] public static partial uint FPDFAttachment_GetName(IntPtr attachment, void* buffer, uint buflen);
    [LibraryImport(Lib)] public static partial int FPDFAttachment_GetFile(IntPtr attachment, void* buffer, uint buflen, uint* outBuflen);

    [LibraryImport(Lib)] public static partial int FPDFPage_GetAnnotCount(IntPtr page);
    [LibraryImport(Lib)] public static partial IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [LibraryImport(Lib)] public static partial IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [LibraryImport(Lib)] public static partial int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [LibraryImport(Lib)] public static partial void FPDFPage_CloseAnnot(IntPtr annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetSubtype(IntPtr annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetRect(IntPtr annot, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetRect(IntPtr annot, FS_RECTF* rect);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetColor(IntPtr annot, int type, uint* r, uint* g, uint* b, uint* a);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetColor(IntPtr annot, int type, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial uint FPDFAnnot_GetStringValue(IntPtr annot, byte* key, void* buffer, uint buflen);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetStringValue(IntPtr annot, byte* key, char* value);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_AppendAttachmentPoints(IntPtr annot, FS_QUADPOINTSF* quad);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetFlags(IntPtr annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetFlags(IntPtr annot, int flags);
    [LibraryImport(Lib)] public static partial nuint FPDFAnnot_CountAttachmentPoints(IntPtr annot);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetAttachmentPoints(IntPtr annot, nuint index, FS_QUADPOINTSF* quad);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetAP(IntPtr annot, int appearanceMode, char* value);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_AddInkStroke(IntPtr annot, FS_POINTF* points, nuint count);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_RemoveInkList(IntPtr annot);
    [LibraryImport(Lib)] public static partial uint FPDFAnnot_GetInkListCount(IntPtr annot);
    [LibraryImport(Lib)] public static partial uint FPDFAnnot_GetInkListPath(IntPtr annot, uint path, FS_POINTF* buffer, uint length);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_SetBorder(IntPtr annot, float horizontalRadius, float verticalRadius, float width);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetBorder(IntPtr annot, float* horizontalRadius, float* verticalRadius, float* width);
    [LibraryImport(Lib)] public static partial int FPDFAnnot_GetNumberValue(IntPtr annot, byte* key, float* value);

    /// <summary>Longest document string <see cref="ReadUtf16"/> reads; anything longer comes back empty.</summary>
    private const uint MaxStringBytes = 16 << 20;

    /// <summary>
    /// Reads a UTF-16 string from a two-call PDFium API that reports its size in bytes. These are
    /// strings from the document that end up in the UI, so bidi controls are removed unless
    /// <paramref name="exact"/>, for strings that go back into the document.
    /// </summary>
    public static string ReadUtf16(Func<IntPtr, uint, uint> call, bool exact = false)
    {
        uint size = call(IntPtr.Zero, 0);
        if (size <= 2 || size > MaxStringBytes) return string.Empty;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            call((IntPtr)p, size);
        }
        string text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size - 2);
        return exact ? text : Services.LaunchPolicy.StripBidiControls(text);
    }
}
