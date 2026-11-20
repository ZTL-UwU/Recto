using System.ComponentModel;
using System.Runtime.InteropServices;
using Recto.Pdf;
using Windows.Foundation;
using Windows.Graphics;

namespace Recto.Printing;

/// <summary>
/// Everything a print job needs; <see cref="Pages"/> are zero-based, in print order. <see cref="Copies"/>
/// are the copies sent; any the printer makes itself are in <see cref="Settings"/>.
/// </summary>
internal sealed record PrintRequest(
    string Printer,
    DevMode Settings,
    string Title,
    string? OutputFile,
    IReadOnlyList<int> Pages,
    LayoutOptions Layout,
    int Copies,
    bool Collate,
    bool TwoSided,
    bool Annotations,
    bool Grayscale,
    bool AsImage);

/// <summary>Prints through GDI, with PDFium drawing each page straight onto the printer.</summary>
internal static partial class PrintJob
{
    private const int ERROR_CANCELLED = 1223;

    /// <summary>
    /// Prints <paramref name="request"/>, reporting the sheets of paper sent so far and in all (counting
    /// both sides of each when printing two-sided, and the blank backs between copies). Returns the
    /// number of pages that couldn't be drawn and were left blank. Cancelling deletes the job.
    /// </summary>
    public static async Task<int> RunAsync(PdfDocument document, PrintRequest request, IProgress<(int Done, int Total)> progress, CancellationToken token)
    {
        IntPtr dc = await Task.Run(() => CreateDC(request), token).ConfigureAwait(false);

        bool started = false, finished = false;
        try
        {
            var (paper, dpiX, dpiY, offsetX, offsetY) = Printers.ReadMetrics(dc);
            // Laid out again for the paper the printer actually reports, which the preview could only ask about.
            var sheets = PrintLayout.Layout(request.Pages, document.PageSizes, paper, request.Layout);
            var sequence = PrintLayout.Sequence(sheets.Count, request.Copies, request.Collate, request.TwoSided);
            int paperTotal = PrintLayout.PaperCount(sheets.Count, request.Copies, request.TwoSided);

            await Task.Run(() => StartDoc(dc, request), token).ConfigureAwait(false);
            started = true;

            // Counted once, not once per copy.
            var failed = new HashSet<int>();
            for (int n = 0; n < sequence.Count; n++)
            {
                token.ThrowIfCancellationRequested();
                // Starting and ending a page can wait on the spooler, so they stay off the PDFium thread,
                // which only draws, a page at a time, letting the viewer's own renders in between.
                await Task.Run(() =>
                {
                    if (StartPage(dc) <= 0) throw new InvalidOperationException(Strings.Get("PrinterStoppedAccepting"));
                }, token).ConfigureAwait(false);
                if (sequence[n] is { } i)
                {
                    var sheet = sheets[i];
                    foreach (var placement in sheet.Pages)
                    {
                        bool drawn = await PdfWorker.Run(() =>
                        {
                            try
                            {
                                Draw(document, dc, sheet, placement, paper, dpiX, dpiY, offsetX, offsetY, request, token);
                                return true;
                            }
                            catch (InvalidDataException)
                            {
                                return false;
                            }
                            finally
                            {
                                // GDI batches calls per thread, and EndPage comes from another.
                                GdiFlush();
                            }
                        }, WorkPriority.Normal, token).ConfigureAwait(false);
                        if (!drawn) failed.Add(placement.Page);
                    }
                }
                await Task.Run(() =>
                {
                    if (EndPage(dc) <= 0) throw new InvalidOperationException(Strings.Get("PrinterStoppedAccepting"));
                }, token).ConfigureAwait(false);
                progress.Report((request.TwoSided ? (n + 2) / 2 : n + 1, paperTotal));
            }

            await Task.Run(() =>
            {
                if (EndDoc(dc) <= 0) throw new InvalidOperationException(Strings.Get("PrinterCouldntFinish"));
            }).ConfigureAwait(false);
            finished = true;
            return failed.Count;
        }
        finally
        {
            IntPtr handle = dc;
            bool abort = started && !finished;
            // AbortDoc and DeleteDC can wait on the spooler, so they stay off the caller's thread.
            await Task.Run(() =>
            {
                if (abort) AbortDoc(handle);
                Printers.DeleteDC(handle);
            }).ConfigureAwait(false);
        }
    }

    private static unsafe IntPtr CreateDC(PrintRequest request)
    {
        fixed (byte* settings = request.Settings.Data)
        {
            IntPtr dc = Printers.CreateDCW("WINSPOOL", request.Printer, null, settings);
            if (dc == IntPtr.Zero) throw new InvalidOperationException(Strings.Format("PrinterOpenFailed", request.Printer, new Win32Exception(Marshal.GetLastPInvokeError()).Message));
            return dc;
        }
    }

    private static unsafe void StartDoc(IntPtr dc, PrintRequest request)
    {
        fixed (char* name = request.Title)
        fixed (char* output = request.OutputFile)
        {
            var info = new DOCINFOW { Size = sizeof(DOCINFOW), DocName = name, Output = output };
            if (StartDocW(dc, &info) <= 0)
            {
                int error = Marshal.GetLastPInvokeError();
                // Cancelled in a driver's own prompt, such as for a file name.
                if (error == ERROR_CANCELLED) throw new OperationCanceledException();
                throw new InvalidOperationException(Strings.Format("PrinterRejectedDocument", new Win32Exception(error).Message));
            }
        }
    }

    private static void Draw(PdfDocument document, IntPtr dc, PrintSheet sheet, PagePlacement placement, PaperMetrics paper,
        int dpiX, int dpiY, int offsetX, int offsetY, PrintRequest request, CancellationToken token)
    {
        var (bounds, clip, quarter) = PrintLayout.OnPaper(sheet, placement, paper);
        RectInt32 ToDevice(Rect r)
        {
            int left = (int)Math.Round(r.X * dpiX / 72), top = (int)Math.Round(r.Y * dpiY / 72);
            int right = (int)Math.Round(r.Right * dpiX / 72), bottom = (int)Math.Round(r.Bottom * dpiY / 72);
            return new RectInt32(left - offsetX, top - offsetY, Math.Max(1, right - left), Math.Max(1, bottom - top));
        }
        document.DrawToDevice(dc, placement.Page, ToDevice(bounds), quarter, ToDevice(clip), Math.Max(dpiX, dpiY),
            request.Annotations, request.Grayscale, request.AsImage, token);
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DOCINFOW
    {
        public int Size;
        public char* DocName;
        public char* Output;
        public char* Datatype;
        public uint Type;
    }

    [LibraryImport("gdi32", SetLastError = true)] private static unsafe partial int StartDocW(IntPtr dc, DOCINFOW* info);
    [LibraryImport("gdi32")] private static partial int StartPage(IntPtr dc);
    [LibraryImport("gdi32")] private static partial int EndPage(IntPtr dc);
    [LibraryImport("gdi32")] private static partial int EndDoc(IntPtr dc);
    [LibraryImport("gdi32")] private static partial int AbortDoc(IntPtr dc);
    [LibraryImport("gdi32")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GdiFlush();
}
