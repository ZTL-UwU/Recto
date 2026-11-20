using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Recto.Printing;

/// <summary>A paper size a printer offers, in points.</summary>
public sealed record PaperSize(short Id, string Name, double Width, double Height);

/// <summary>
/// A printer's settings for a job (a DEVMODE). The driver keeps its own settings after the
/// standard fields, so the whole block is carried around and only the standard fields are changed.
/// </summary>
internal sealed class DevMode
{
    // Offsets into DEVMODEW.
    private const int FieldsOffset = 72, OrientationOffset = 76, PaperSizeOffset = 78, CopiesOffset = 86;
    private const int ColorOffset = 92, DuplexOffset = 94, CollateOffset = 100;
    private const int MinSize = 104;

    private const uint DM_ORIENTATION = 0x1, DM_PAPERSIZE = 0x2, DM_PAPERLENGTH = 0x4, DM_PAPERWIDTH = 0x8;
    private const uint DM_COPIES = 0x100, DM_COLOR = 0x800, DM_DUPLEX = 0x1000, DM_COLLATE = 0x8000, DM_FORMNAME = 0x10000;

    private readonly byte[] _data;

    public DevMode(byte[] data)
    {
        if (data.Length < MinSize) throw new ArgumentException(Strings.Get("PrinterSettingsIncomplete"), nameof(data));
        _data = data;
    }

    public byte[] Data => _data;
    public DevMode Clone() => new((byte[])_data.Clone());

    private uint Fields
    {
        get => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(FieldsOffset));
        set => BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(FieldsOffset), value);
    }

    private short? Get(int offset, uint field) => (Fields & field) != 0 ? BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(offset)) : null;

    private void Set(int offset, uint field, short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(_data.AsSpan(offset), value);
        Fields |= field;
    }

    public bool? Landscape
    {
        get => Get(OrientationOffset, DM_ORIENTATION) is { } o ? o == 2 : null;
        set { if (value is { } v) Set(OrientationOffset, DM_ORIENTATION, (short)(v ? 2 : 1)); }
    }

    public short? PaperSize
    {
        get => Get(PaperSizeOffset, DM_PAPERSIZE);
        set
        {
            if (value is not { } v) return;
            Set(PaperSizeOffset, DM_PAPERSIZE, v);
            // A form name or explicit dimensions would take precedence over the size picked.
            Fields &= ~(DM_FORMNAME | DM_PAPERLENGTH | DM_PAPERWIDTH);
        }
    }

    public short? Copies
    {
        get => Get(CopiesOffset, DM_COPIES);
        set { if (value is { } v) Set(CopiesOffset, DM_COPIES, v); }
    }

    public bool? Collate
    {
        get => Get(CollateOffset, DM_COLLATE) is { } c ? c == 1 : null;
        set { if (value is { } v) Set(CollateOffset, DM_COLLATE, (short)(v ? 1 : 0)); }
    }

    public bool? Grayscale
    {
        get => Get(ColorOffset, DM_COLOR) is { } c ? c == 1 : null;
        set { if (value is { } v) Set(ColorOffset, DM_COLOR, (short)(v ? 1 : 2)); }
    }

    public TwoSided? Duplex
    {
        get => Get(DuplexOffset, DM_DUPLEX) switch { 2 => TwoSided.LongEdge, 3 => TwoSided.ShortEdge, null => null, _ => TwoSided.Off };
        set { if (value is { } v) Set(DuplexOffset, DM_DUPLEX, v switch { TwoSided.LongEdge => 2, TwoSided.ShortEdge => 3, _ => 1 }); }
    }
}

/// <summary>What a printer is and can do. Reading it can take a moment for network printers, so do it off the UI thread.</summary>
internal sealed class PrinterDetails
{
    public required string Name { get; init; }
    public string? Port { get; init; }
    public string? Driver { get; init; }
    public string Location { get; init; } = "";
    public string Status { get; init; } = "";
    /// <summary>Whether the status is something that will keep the job from printing right away.</summary>
    public bool HasProblem { get; init; }
    public IReadOnlyList<PaperSize> Papers { get; init; } = [];
    public bool CanDuplex { get; init; }
    public bool IsColor { get; init; }
    /// <summary>Most copies the driver makes itself; 1 if it can't.</summary>
    public int MaxCopies { get; init; } = 1;
    /// <summary>Whether the driver can collate the copies it makes.</summary>
    public bool CanCollate { get; init; }
    public required DevMode Defaults { get; init; }

    /// <summary>Printers that write a file (Microsoft Print to PDF, XPS) and the extension of that file.</summary>
    public string? OutputExtension => !string.Equals(Port, "PORTPROMPT:", StringComparison.OrdinalIgnoreCase) ? null
        : Driver?.Contains("PDF", StringComparison.OrdinalIgnoreCase) == true ? ".pdf"
        : Driver?.Contains("XPS", StringComparison.OrdinalIgnoreCase) == true ? ".oxps"
        : null;
}

internal static unsafe partial class Printers
{
    /// <summary>Installed printers by name, and the default one.</summary>
    public static (List<string> Names, string? Default) Enumerate()
    {
        const uint PRINTER_ENUM_LOCAL = 2, PRINTER_ENUM_CONNECTIONS = 4;
        var names = new List<string>();
        uint flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
        EnumPrintersW(flags, null, 4, null, 0, out uint needed, out _);
        if (needed > 0)
        {
            var buffer = new byte[needed];
            fixed (byte* p = buffer)
            {
                if (EnumPrintersW(flags, null, 4, p, needed, out _, out uint count))
                {
                    // PRINTER_INFO_4: name, server name (pointers), attributes.
                    int stride = IntPtr.Size * 2 + 4;
                    stride = (stride + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
                    for (int i = 0; i < count; i++)
                    {
                        var name = Marshal.PtrToStringUni(*(IntPtr*)(p + i * stride));
                        if (!string.IsNullOrEmpty(name)) names.Add(name);
                    }
                }
            }
        }
        names.Sort(StringComparer.CurrentCultureIgnoreCase);

        string? defaultName = null;
        uint size = 0;
        GetDefaultPrinterW(null, ref size);
        if (size > 0)
        {
            var chars = new char[size];
            fixed (char* c = chars)
            {
                if (GetDefaultPrinterW(c, ref size)) defaultName = new string(c);
            }
        }
        return (names, defaultName);
    }

    public static PrinterDetails Load(string name)
    {
        using var printer = Open(name);
        string? port = null, driver = null, location = "";
        uint status = 0, attributes = 0, jobs = 0;
        GetPrinterW(printer.Handle, 2, null, 0, out uint needed);
        if (needed > 0)
        {
            var buffer = new byte[needed];
            fixed (byte* p = buffer)
            {
                if (GetPrinterW(printer.Handle, 2, p, needed, out _))
                {
                    // PRINTER_INFO_2: 13 pointers, then Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs.
                    IntPtr* s = (IntPtr*)p;
                    port = Marshal.PtrToStringUni(s[3]);
                    driver = Marshal.PtrToStringUni(s[4]);
                    location = Marshal.PtrToStringUni(s[6]) ?? "";
                    uint* d = (uint*)(p + 13 * IntPtr.Size);
                    attributes = d[0];
                    status = d[5];
                    jobs = d[6];
                }
            }
        }

        var defaults = new DevMode(ReadDevMode(printer.Handle, name, null, IntPtr.Zero, 0));
        var (text, problem) = DescribeStatus(status, attributes, jobs);
        return new PrinterDetails
        {
            Name = name,
            Port = port,
            Driver = driver,
            Location = location.Trim(),
            Status = text,
            HasProblem = problem,
            Papers = ReadPapers(name, port),
            CanDuplex = DeviceCapabilitiesW(name, port, DC_DUPLEX, null, null) == 1,
            IsColor = DeviceCapabilitiesW(name, port, DC_COLORDEVICE, null, null) == 1,
            MaxCopies = Math.Max(1, DeviceCapabilitiesW(name, port, DC_COPIES, null, null)),
            CanCollate = DeviceCapabilitiesW(name, port, DC_COLLATE, null, null) == 1,
            Defaults = defaults,
        };
    }

    /// <summary>Lets the driver check and complete <paramref name="settings"/>, as it expects before a job starts.</summary>
    public static DevMode Merge(string name, DevMode settings)
    {
        using var printer = Open(name);
        try
        {
            return new DevMode(ReadDevMode(printer.Handle, name, settings, IntPtr.Zero, DM_IN_BUFFER));
        }
        catch (Win32Exception)
        {
            return settings;
        }
    }

    /// <summary>
    /// Shows the driver's own settings dialog. Returns the changed settings, or null if it was
    /// cancelled. Call on the UI thread: the dialog is modal to <paramref name="owner"/>.
    /// </summary>
    public static DevMode? ShowProperties(IntPtr owner, string name, DevMode settings)
    {
        using var printer = Open(name);
        int size = DocumentPropertiesW(owner, printer.Handle, name, null, null, 0);
        if (size <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var output = new byte[Math.Max(size, settings.Data.Length)];
        int result;
        fixed (byte* o = output, i = settings.Data)
        {
            result = DocumentPropertiesW(owner, printer.Handle, name, o, i, DM_IN_BUFFER | DM_OUT_BUFFER | DM_IN_PROMPT);
        }
        if (result < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return result == IDOK ? new DevMode(output) : null;
    }

    /// <summary>The paper the printer feeds with <paramref name="settings"/>, and its printable area.</summary>
    public static PaperMetrics? QueryPaper(string name, DevMode settings)
    {
        fixed (byte* d = settings.Data)
        {
            IntPtr ic = CreateICW("WINSPOOL", name, null, d);
            if (ic == IntPtr.Zero) return null;
            try
            {
                return ReadMetrics(ic).Paper;
            }
            finally
            {
                DeleteDC(ic);
            }
        }
    }

    /// <summary>The paper of a printer's device context in points, and its resolution.</summary>
    public static (PaperMetrics Paper, int DpiX, int DpiY, int OffsetX, int OffsetY) ReadMetrics(IntPtr dc)
    {
        int dpiX = Math.Max(1, GetDeviceCaps(dc, LOGPIXELSX)), dpiY = Math.Max(1, GetDeviceCaps(dc, LOGPIXELSY));
        int width = GetDeviceCaps(dc, PHYSICALWIDTH), height = GetDeviceCaps(dc, PHYSICALHEIGHT);
        int offsetX = GetDeviceCaps(dc, PHYSICALOFFSETX), offsetY = GetDeviceCaps(dc, PHYSICALOFFSETY);
        int printableWidth = GetDeviceCaps(dc, HORZRES), printableHeight = GetDeviceCaps(dc, VERTRES);
        if (width <= 0 || height <= 0)
        {
            // Some virtual printers report no paper; treat the printable area as the paper.
            width = printableWidth;
            height = printableHeight;
            offsetX = offsetY = 0;
        }
        double kx = 72.0 / dpiX, ky = 72.0 / dpiY;
        var paper = new PaperMetrics(Math.Max(1, width * kx), Math.Max(1, height * ky),
            new Windows.Foundation.Rect(offsetX * kx, offsetY * ky, Math.Max(1, printableWidth * kx), Math.Max(1, printableHeight * ky)));
        return (paper, dpiX, dpiY, offsetX, offsetY);
    }

    private static List<PaperSize> ReadPapers(string name, string? port)
    {
        var papers = new List<PaperSize>();
        int count = DeviceCapabilitiesW(name, port, DC_PAPERS, null, null);
        if (count <= 0 || count > 4096) return papers;
        var ids = new short[count];
        var sizes = new int[count * 2];
        var names = new char[count * 64];
        fixed (short* i = ids)
        fixed (int* s = sizes)
        fixed (char* n = names)
        {
            if (DeviceCapabilitiesW(name, port, DC_PAPERS, i, null) != count) return papers;
            if (DeviceCapabilitiesW(name, port, DC_PAPERSIZE, s, null) != count) return papers;
            bool named = DeviceCapabilitiesW(name, port, DC_PAPERNAMES, n, null) == count;
            for (int k = 0; k < count; k++)
            {
                // Tenths of a millimetre.
                double w = sizes[2 * k] * 72 / 254.0, h = sizes[2 * k + 1] * 72 / 254.0;
                if (w <= 0 || h <= 0) continue;
                string label = named ? new string(n + k * 64, 0, 64).TrimEnd('\0').Trim() : "";
                if (label.Length == 0) label = $"{sizes[2 * k] / 10.0:0.#} × {sizes[2 * k + 1] / 10.0:0.#} mm";
                papers.Add(new PaperSize(ids[k], label, w, h));
            }
        }
        return papers;
    }

    private static (string Text, bool Problem) DescribeStatus(uint status, uint attributes, uint jobs)
    {
        const uint PRINTER_ATTRIBUTE_WORK_OFFLINE = 0x400;
        (uint Flag, string Text)[] problems =
        [
            (0x80, Strings.Get("PrinterOffline")), (0x1000, Strings.Get("PrinterNotAvailable")), (0x1, Strings.Get("PrinterPaused")),
            (0x8, Strings.Get("PrinterPaperJam")), (0x10, Strings.Get("PrinterOutOfPaper")), (0x40, Strings.Get("PrinterPaperProblem")),
            (0x40000, Strings.Get("PrinterNoToner")), (0x400000, Strings.Get("PrinterDoorOpen")), (0x100000, Strings.Get("PrinterNeedsAttention")),
            (0x2, Strings.Get("PrinterError")),
        ];
        string? problem = (attributes & PRINTER_ATTRIBUTE_WORK_OFFLINE) != 0 ? Strings.Get("PrinterOffline") : null;
        problem ??= problems.FirstOrDefault(p => (status & p.Flag) != 0).Text;
        string text = problem ?? Strings.Get((status & 0x20000) != 0 ? "PrinterTonerLow" : "PrinterReady");
        if (jobs > 0) text += " · " + Strings.Count("DocumentsWaiting", jobs);
        return (text, problem is not null);
    }

    private static byte[] ReadDevMode(IntPtr printer, string name, DevMode? input, IntPtr owner, uint mode)
    {
        int size = DocumentPropertiesW(owner, printer, name, null, null, 0);
        if (size <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var output = new byte[Math.Max(size, input?.Data.Length ?? 0)];
        fixed (byte* o = output, i = input?.Data)
        {
            if (DocumentPropertiesW(owner, printer, name, o, i, mode | DM_OUT_BUFFER) < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return output;
    }

    private static PrinterHandle Open(string name)
    {
        if (!OpenPrinterW(name, out IntPtr handle, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new PrinterHandle(handle);
    }

    private readonly struct PrinterHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle => handle;
        public void Dispose() => ClosePrinter(handle);
    }

    private const uint DM_OUT_BUFFER = 2, DM_IN_PROMPT = 4, DM_IN_BUFFER = 8;
    private const int IDOK = 1;
    private const ushort DC_PAPERS = 2, DC_PAPERSIZE = 3, DC_DUPLEX = 7, DC_PAPERNAMES = 16, DC_COPIES = 18, DC_COLLATE = 22, DC_COLORDEVICE = 32;
    private const int HORZRES = 8, VERTRES = 10, LOGPIXELSX = 88, LOGPIXELSY = 90;
    private const int PHYSICALWIDTH = 110, PHYSICALHEIGHT = 111, PHYSICALOFFSETX = 112, PHYSICALOFFSETY = 113;

    [LibraryImport("winspool.drv", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumPrintersW(uint flags, string? name, uint level, byte* buffer, uint size, out uint needed, out uint returned);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDefaultPrinterW(char* buffer, ref uint size);

    [LibraryImport("winspool.drv", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenPrinterW(string name, out IntPtr printer, IntPtr defaults);

    [LibraryImport("winspool.drv")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(IntPtr printer);

    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPrinterW(IntPtr printer, uint level, byte* buffer, uint size, out uint needed);

    [LibraryImport("winspool.drv", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DocumentPropertiesW(IntPtr owner, IntPtr printer, string name, byte* output, byte* input, uint mode);

    [LibraryImport("winspool.drv", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeviceCapabilitiesW(string device, string? port, ushort capability, void* output, byte* devMode);

    [LibraryImport("gdi32", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateICW(string driver, string device, string? output, byte* devMode);

    [LibraryImport("gdi32", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateDCW(string driver, string device, string? output, byte* devMode);

    [LibraryImport("gdi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(IntPtr dc);

    [LibraryImport("gdi32")]
    private static partial int GetDeviceCaps(IntPtr dc, int index);
}
