using System.Security.Cryptography;
using System.Text;

namespace Recto.Tests;

/// <summary>Writes tiny PDFs for tests, optionally encrypted with the standard security handler (RC4, 40 bit).</summary>
internal static class TestPdf
{
    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private static readonly byte[] FileId = [.. Enumerable.Range(1, 16).Select(i => (byte)(i * 7))];

    /// <param name="pages">Number of empty pages.</param>
    /// <param name="mediaBox">The /MediaBox of every page.</param>
    /// <param name="userPassword">Encrypts the file so it needs this password to open.</param>
    /// <param name="marker">A comment written into the body, to check that saving keeps the original bytes.</param>
    /// <param name="text">Text written on every page in Helvetica, a line per <c>\n</c>, from the top left. Not with <paramref name="userPassword"/>.</param>
    /// <param name="annots">
    /// Annotation dictionaries on the first page, as another program would have written them. <c>{appearance}</c> in one
    /// stands for a reference to an appearance stream, a filled square.
    /// </param>
    /// <param name="rotate">The /Rotate of every page.</param>
    public static byte[] Create(int pages = 1, string mediaBox = "0 0 200 300", string? userPassword = null, string marker = "", string? text = null, string[]? annots = null, int rotate = 0)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{i + 3} 0 R"))}] /Count {pages} >>",
        };
        int font = pages + 3, content = pages + 4, appearance = pages + (text is null ? 3 : 5);
        string resources = text is null ? "" : $" /Resources << /Font << /F1 {font} 0 R >> >> /Contents {content} 0 R";
        for (int i = 0; i < pages; i++)
        {
            string annotations = i == 0 && annots is not null ? $" /Annots [{string.Join(" ", annots).Replace("{appearance}", $"{appearance} 0 R")}]" : "";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [{mediaBox}] /Rotate {rotate}{resources}{annotations} >>");
        }
        if (text is not null)
        {
            if (userPassword is not null) throw new ArgumentException("Text isn't encrypted, so it can't go in an encrypted file.");
            string top = mediaBox.Split(' ')[3];
            string stream = $"BT /F1 12 Tf 14 TL 10 {top} Td T* {string.Concat(text.Split('\n').Select(line => $"({line}) Tj T* "))}ET";
            objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        }
        if (annots is not null)
        {
            const string Square = "0 0 1 rg 0 0 10 10 re f";
            objects.Add($"<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length {Square.Length} >>\nstream\n{Square}\nendstream");
        }

        string trailerExtra = "";
        if (userPassword is not null)
        {
            const int Permissions = -4;
            byte[] owner = Rc4(Md5(Pad("owner"))[..5], Pad(userPassword));
            byte[] key = Md5([.. Pad(userPassword), .. owner, .. BitConverter.GetBytes(Permissions), .. FileId])[..5];
            byte[] user = Rc4(key, Padding);
            objects.Add($"<< /Filter /Standard /V 1 /R 2 /Length 40 /O <{Convert.ToHexString(owner)}> /U <{Convert.ToHexString(user)}> /P {Permissions} >>");
            trailerExtra = $" /Encrypt {objects.Count} 0 R /ID [<{Convert.ToHexString(FileId)}> <{Convert.ToHexString(FileId)}>]";
        }

        var pdf = new StringBuilder("%PDF-1.4\n");
        if (marker.Length > 0) pdf.Append('%').Append(marker).Append('\n');
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static byte[] Pad(string password) => [.. Encoding.Latin1.GetBytes(password).Concat(Padding).Take(32)];

    private static byte[] Md5(byte[] data) => MD5.HashData(data);

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var result = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            result[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return result;
    }
}

/// <summary>A scratch folder that's deleted after the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RectoTests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string Write(string name, byte[] contents)
    {
        string path = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
