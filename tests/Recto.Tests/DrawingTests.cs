using Recto.Pdf;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace Recto.Tests;

public sealed class DrawingTests : IDisposable
{
    private static readonly Color Red = Color.FromArgb(255, 200, 30, 30);
    private static readonly Color Blue = Color.FromArgb(255, 20, 60, 220);
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static InkDrawing Line(double x1, double y1, double x2, double y2, double width = 6, byte opacity = 255, Color? color = null) =>
        new([[new Point(x1, y1), new Point(x2, y2)]], color ?? Red, width, opacity);

    /// <summary>The page drawn at one pixel per point, as (B, G, R) by position.</summary>
    private static async Task<Func<int, int, (byte B, byte G, byte R)>> RenderAsync(PdfDocument document, int page = 0)
    {
        var size = document.PageSizes[page];
        using var buffer = await document.RenderAsync(page, 1, 0, new RectInt32(0, 0, (int)size.Width, (int)size.Height), false, WorkPriority.Visible, CancellationToken.None);
        var pixels = buffer!.Span.ToArray();
        int stride = buffer.Stride;
        return (x, y) => (pixels[y * stride + x * 4], pixels[y * stride + x * 4 + 1], pixels[y * stride + x * 4 + 2]);
    }

    private static bool IsWhite((byte B, byte G, byte R) c) => c is { B: > 250, G: > 250, R: > 250 };

    /// <summary>Whether anything but white was drawn in a rectangle of the page.</summary>
    private static bool HasInk(Func<int, int, (byte B, byte G, byte R)> pixel, int left, int top, int right, int bottom)
    {
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (!IsWhite(pixel(x, y))) return true;
        return false;
    }

    private async Task<(PdfDocument Document, string Path)> OpenAsync(string mediaBox = "0 0 200 300", int rotate = 0)
    {
        string path = _temp.Write($"{Guid.NewGuid():N}.pdf", TestPdf.Create(mediaBox: mediaBox, rotate: rotate));
        return (await PdfDocument.OpenAsync(path), path);
    }

    private static async Task<PdfAnnotation> OnlyAnnotationAsync(PdfDocument document) =>
        Assert.Single(await document.GetAllAnnotationsAsync(CancellationToken.None));

    [Fact]
    public async Task Ink_IsDrawnWhereItWasPut_InItsColor()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            await document.AddInkAsync(0, Line(20, 50, 180, 50));
            var pixel = await RenderAsync(document);
            var c = pixel(100, 50);
            Assert.Equal((Red.B, Red.G, Red.R), (c.B, c.G, c.R));
            Assert.True(IsWhite(pixel(100, 60)));
            Assert.True(IsWhite(pixel(100, 40)));
        }
    }

    [Fact]
    public async Task Ink_HasRoundCaps_NotCutOffAtItsBounds()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            await document.AddInkAsync(0, Line(50, 50, 100, 50, width: 20));
            var pixel = await RenderAsync(document);
            // Half the width past each end of the line.
            Assert.False(IsWhite(pixel(42, 50)));
            Assert.False(IsWhite(pixel(108, 50)));
            Assert.True(IsWhite(pixel(42, 42)));
        }
    }

    [Fact]
    public async Task Dot_FromASingleClick_IsDrawn()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            await document.AddInkAsync(0, new InkDrawing([[new Point(60, 60)]], Red, 10));
            Assert.False(IsWhite((await RenderAsync(document))(60, 60)));
        }
    }

    [Fact]
    public async Task HighlighterInk_LetsThePageShowThrough()
    {
        string path = _temp.Write("doc.pdf", TestPdf.Create(text: "WWWWWWWWWWWWWWWWWWWW"));
        using var document = await PdfDocument.OpenAsync(path);
        var yellow = Color.FromArgb(255, 255, 221, 51);
        // The text's first line, and the empty space below it.
        await document.AddInkAsync(0, new InkDrawing([[new Point(10, 18), new Point(150, 18)], [new Point(10, 60), new Point(150, 60)]], yellow, 24, 110));
        var pixel = await RenderAsync(document);
        var tint = pixel(100, 60);
        Assert.True(tint.B < 200 && tint.R > 240, $"Expected a yellow tint, got {tint}");
        Assert.False(tint == (yellow.B, yellow.G, yellow.R), "It's drawn solid.");
        // Black text under it stays darker than the tint around it. The baseline is 14 points down.
        int darkest = 255;
        for (int x = 10; x < 150; x++) darkest = Math.Min(darkest, (int)pixel(x, 10).R);
        Assert.True(darkest < 200, $"The text doesn't show through: {darkest}");
    }

    [Fact]
    public async Task Ink_ReadsBackAsItWasDrawn()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            var strokes = new[] { new[] { new Point(10, 20), new Point(30, 40), new Point(50, 25) }, new[] { new Point(60, 60) } };
            var added = await document.AddInkAsync(0, new InkDrawing(strokes, Blue, 3, 128));

            Assert.Equal(AnnotationKind.Ink, added.Kind);
            Assert.NotNull(added.Ink);
            Assert.Equal(Blue, added.Ink.Color);
            Assert.Equal(Blue, added.Color);
            Assert.Equal(128, added.Ink.Opacity);
            Assert.Equal(3, added.Ink.Width, 3);
            Assert.Equal(2, added.Ink.Strokes.Length);
            Assert.Equal(new Point(30, 40), added.Ink.Strokes[0][1]);
            Assert.Equal(new Point(60, 60), added.Ink.Strokes[1][0]);
        }
    }

    [Fact]
    public async Task Ink_SurvivesSavingAndReopening_AndIsStillDrawn()
    {
        var (document, _) = await OpenAsync();
        string copy = Path.Combine(_temp.Path, "saved.pdf");
        using (document)
        {
            await document.AddInkAsync(0, Line(20, 50, 180, 50));
            await document.SaveAsync(copy);
        }
        using var reopened = await PdfDocument.OpenAsync(copy);
        var ink = await OnlyAnnotationAsync(reopened);
        Assert.Equal(Red, ink.Ink!.Color);
        Assert.False(IsWhite((await RenderAsync(reopened))(100, 50)));
    }

    [Fact]
    public async Task Ink_InAnEncryptedFile_IsStillDrawnWhenReopened()
    {
        string path = _temp.Write("locked.pdf", TestPdf.Create(userPassword: "secret"));
        string copy = Path.Combine(_temp.Path, "saved.pdf");
        using (var document = await PdfDocument.OpenAsync(path, "secret"))
        {
            await document.AddInkAsync(0, Line(20, 50, 180, 50));
            await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 100, 150, 0), "Secret", Blue, 16));
            await document.SaveAsync(copy);
        }
        using var reopened = await PdfDocument.OpenAsync(copy, "secret");
        var pixel = await RenderAsync(reopened);
        Assert.False(IsWhite(pixel(100, 50)));
        Assert.True(HasInk(pixel, 20, 100, 170, 125));
    }

    [Fact]
    public async Task ChangesToSavedDrawings_AreSavedToo()
    {
        var (document, _) = await OpenAsync();
        string first = Path.Combine(_temp.Path, "first.pdf"), second = Path.Combine(_temp.Path, "second.pdf"), third = Path.Combine(_temp.Path, "third.pdf");
        using (document)
        {
            await document.AddInkAsync(0, Line(20, 50, 180, 50));
            await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 100, 160, 0), "Before", Blue, 16));
            await document.SaveAsync(first);
        }
        using (var reopened = await PdfDocument.OpenAsync(first))
        {
            var all = await reopened.GetAllAnnotationsAsync(CancellationToken.None);
            var ink = all.Single(a => a.Kind == AnnotationKind.Ink);
            var text = all.Single(a => a.Kind == AnnotationKind.FreeText);
            await reopened.SetInkAsync(ink, ink.Ink!.Moved(0, 150) with { Color = Blue });
            await reopened.SetFreeTextAsync(text, text.FreeText! with { Text = "After" });
            await reopened.SaveAsync(second);
        }
        using (var again = await PdfDocument.OpenAsync(second))
        {
            var all = await again.GetAllAnnotationsAsync(CancellationToken.None);
            Assert.Equal(new Point(20, 200), all.Single(a => a.Kind == AnnotationKind.Ink).Ink!.Strokes[0][0]);
            Assert.Equal("After", all.Single(a => a.Kind == AnnotationKind.FreeText).FreeText!.Text);
            var pixel = await RenderAsync(again);
            Assert.True(IsWhite(pixel(100, 50)));
            var c = pixel(100, 200);
            Assert.Equal((Blue.B, Blue.G, Blue.R), (c.B, c.G, c.R));

            await again.RemoveAnnotationAsync(all.Single(a => a.Kind == AnnotationKind.Ink));
            await again.SaveAsync(third);
        }
        using var final = await PdfDocument.OpenAsync(third);
        Assert.Equal(AnnotationKind.FreeText, (await OnlyAnnotationAsync(final)).Kind);
        Assert.False(HasInk(await RenderAsync(final), 0, 180, 200, 220));
    }

    [Fact]
    public async Task Drawings_ArePrinted()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            await document.AddInkAsync(0, Line(20, 50, 180, 50));
            await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 100, 160, 0), "Printed", Blue, 20));
            using var buffer = await document.RenderAsync(0, 1, 0, new RectInt32(0, 0, 200, 300), false, WorkPriority.Visible, CancellationToken.None, printing: true);
            var pixels = buffer!.Span.ToArray();
            (byte B, byte G, byte R) Pixel(int x, int y) => (pixels[y * buffer.Stride + x * 4], pixels[y * buffer.Stride + x * 4 + 1], pixels[y * buffer.Stride + x * 4 + 2]);
            Assert.False(IsWhite(Pixel(100, 50)));
            Assert.True(HasInk(Pixel, 20, 100, 180, 130));
        }
    }

    [Fact]
    public async Task SettingInk_MovesItOnThePage_AndReturnsWhatItWas()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            var added = await document.AddInkAsync(0, Line(20, 50, 180, 50));
            var moved = added.Ink!.Moved(0, 100);
            var previous = await document.SetInkAsync(added, moved);

            Assert.Equal(added.Ink.Strokes[0], previous!.Strokes[0]);
            var pixel = await RenderAsync(document);
            Assert.True(IsWhite(pixel(100, 50)));
            Assert.False(IsWhite(pixel(100, 150)));
            Assert.Equal(new Point(20, 150), (await OnlyAnnotationAsync(document)).Ink!.Strokes[0][0]);
        }
    }

    [Fact]
    public async Task SettingInk_CanRecolorIt()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            var added = await document.AddInkAsync(0, Line(20, 50, 180, 50));
            await document.SetInkAsync(added, added.Ink! with { Color = Blue });
            var c = (await RenderAsync(document))(100, 50);
            Assert.Equal((Blue.B, Blue.G, Blue.R), (c.B, c.G, c.R));
            Assert.Equal(Blue, (await OnlyAnnotationAsync(document)).Color);
        }
    }

    [Fact]
    public async Task RemovedInk_IsPutBackAsItWas_WithItsNote()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            var added = await document.AddInkAsync(0, Line(20, 50, 180, 50, opacity: 128));
            await document.SetAnnotationContentsAsync(added, "comment");
            var removed = await document.RemoveAnnotationAsync(added);
            Assert.NotNull(removed);
            Assert.True(IsWhite((await RenderAsync(document))(100, 50)));

            await document.RestoreAnnotationAsync(removed);
            var restored = await OnlyAnnotationAsync(document);
            Assert.Equal("comment", restored.Contents);
            Assert.Equal(added.Name, restored.Name);
            Assert.Equal(128, restored.Ink!.Opacity);
            Assert.False(IsWhite((await RenderAsync(document))(100, 50)));
            // Found again by its name, so what's undone can be redone.
            Assert.NotNull(await document.RemoveAnnotationAsync(added));
        }
    }

    [Fact]
    public async Task AnotherProgramsInk_IsNotEditable_AndCantBeRestored()
    {
        string path = _temp.Write("doc.pdf", TestPdf.Create(annots: ["<< /Type /Annot /Subtype /Ink /Rect [50 50 90 100] /InkList [[50 50 90 100]] /C [1 0 0] /AP << /N {appearance} >> /NM (their-ink) >>"]));
        using var document = await PdfDocument.OpenAsync(path);
        var ink = await OnlyAnnotationAsync(document);

        Assert.Equal(AnnotationKind.Ink, ink.Kind);
        Assert.Null(ink.Ink);
        Assert.Null(await document.SetInkAsync(ink, Line(0, 0, 10, 10)));
        Assert.Null(await document.RemoveAnnotationAsync(ink));
        Assert.Empty(await document.GetAllAnnotationsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FreeText_IsDrawn_ReadsBack_AndHasNoBorder()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            var added = await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 40, 160, 0), "Hello", Blue, 18));

            Assert.Equal(AnnotationKind.FreeText, added.Kind);
            Assert.Equal("Hello", added.Contents);
            Assert.Equal("Hello", added.FreeText!.Text);
            Assert.Equal(Blue, added.FreeText.Color);
            Assert.Equal(18, added.FreeText.FontSize, 3);
            Assert.True(added.Bounds.Height is > 18 and < 40, $"Height {added.Bounds.Height}");

            var pixel = await RenderAsync(document);
            var box = added.Bounds;
            Assert.True(HasInk(pixel, (int)box.Left, (int)box.Top, (int)box.Right, (int)box.Bottom));
            for (int x = (int)box.Left; x < box.Right; x++) Assert.True(IsWhite(pixel(x, (int)box.Top)));
            Assert.False(HasInk(pixel, 60, 0, 200, 300) && HasInk(pixel, 100, (int)box.Top, 200, (int)box.Bottom), "The text runs on further than it should.");
        }
    }

    [Theory]
    [InlineData("中文注释")] // Fonts PDFium falls back on leave these out.
    [InlineData("日本語のテキスト")]
    [InlineData("한국어")]
    [InlineData("Ελληνικά Кириллица")]
    [InlineData("العربية")]
    public async Task FreeText_DrawsAnyScript(string text)
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            var added = await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 40, 170, 0), text, Blue, 20));
            var box = added.Bounds;
            Assert.True(HasInk(await RenderAsync(document), (int)box.Left, (int)box.Top, (int)box.Right, (int)box.Bottom), "Nothing was drawn.");
            Assert.Equal(text, (await OnlyAnnotationAsync(document)).Contents);
        }
    }

    [Fact]
    public async Task FreeText_WrapsToItsWidth_AndGrowsToFit()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            var one = await document.AddFreeTextAsync(0, new FreeText(new Rect(10, 10, 180, 0), "word", Blue, 12));
            var wrapped = await document.AddFreeTextAsync(0, new FreeText(new Rect(10, 40, 60, 0), "word word word word word word", Blue, 12));
            var lines = await document.AddFreeTextAsync(0, new FreeText(new Rect(10, 150, 180, 0), "one\ntwo\nthree", Blue, 12));

            Assert.True(wrapped.Bounds.Height > one.Bounds.Height * 2, $"{wrapped.Bounds.Height} against {one.Bounds.Height}");
            Assert.True(lines.Bounds.Height > one.Bounds.Height * 2.5);
            Assert.Equal("one\ntwo\nthree", lines.FreeText!.Text);
            // A word too long for the box breaks instead of running out of it.
            var pixel = await RenderAsync(document);
            Assert.False(HasInk(pixel, 72, 42, 200, wrapped.Bounds.Bottom > 148 ? 148 : (int)wrapped.Bounds.Bottom));
        }
    }

    [Fact]
    public async Task EmptyAndBlankFreeText_DontFail()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            var blank = await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 40, 160, 0), "   ", Blue, 12));
            Assert.True(blank.Bounds.Height > 0);
            var empty = await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 80, 160, 0), "", Blue, 12));
            Assert.True(empty.Bounds.Height > 0);
        }
    }

    [Fact]
    public async Task SettingFreeText_MovesIt_AndChangesItsTextAndSize()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            var added = await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 40, 160, 0), "Before", Blue, 16));
            var previous = await document.SetFreeTextAsync(added, added.FreeText! with { Bounds = new Rect(20, 200, 160, 0), Text = "After it", FontSize = 20 });

            Assert.Equal("Before", previous!.Text);
            var now = await OnlyAnnotationAsync(document);
            Assert.Equal("After it", now.Contents);
            Assert.Equal(20, now.FreeText!.FontSize, 3);
            Assert.Equal(200, now.Bounds.Top, 2);
            var pixel = await RenderAsync(document);
            Assert.False(HasInk(pixel, 20, 40, 180, 70));
            Assert.True(HasInk(pixel, 20, 200, 180, 235));
        }
    }

    [Fact]
    public async Task EditingAFreeTextsContents_DrawsTheNewText()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            var added = await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 40, 160, 0), "i", Blue, 20));
            var before = await RenderAsync(document);
            Assert.False(HasInk(before, 60, 40, 180, 70));

            Assert.Equal("i", await document.SetAnnotationContentsAsync(added, "A much longer text"));
            Assert.True(HasInk(await RenderAsync(document), 60, 40, 180, 70));
            Assert.Equal("A much longer text", (await OnlyAnnotationAsync(document)).FreeText!.Text);
        }
    }

    [Fact]
    public async Task RemovedFreeText_IsPutBack()
    {
        var (document, _) = await OpenAsync();
        using (document)
        {
            var added = await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 40, 160, 0), "Keep me", Blue, 16));
            var removed = await document.RemoveAnnotationAsync(added);
            Assert.False(HasInk(await RenderAsync(document), 20, 40, 180, 70));

            await document.RestoreAnnotationAsync(removed!);
            var restored = await OnlyAnnotationAsync(document);
            Assert.Equal("Keep me", restored.FreeText!.Text);
            Assert.Equal(Blue, restored.FreeText.Color);
            Assert.True(HasInk(await RenderAsync(document), 20, 40, 180, 70));
        }
    }

    [Fact]
    public async Task FreeText_SurvivesSavingAndReopening()
    {
        var (document, _) = await OpenAsync();
        string copy = Path.Combine(_temp.Path, "saved.pdf");
        using (document)
        {
            await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 40, 160, 0), "中文 and Latin", Red, 15));
            await document.SaveAsync(copy);
        }
        using var reopened = await PdfDocument.OpenAsync(copy);
        var text = await OnlyAnnotationAsync(reopened);
        Assert.Equal("中文 and Latin", text.FreeText!.Text);
        Assert.Equal(Red, text.FreeText.Color);
        Assert.Equal(15, text.FreeText.FontSize, 3);
        Assert.True(HasInk(await RenderAsync(reopened), 20, 40, 180, 70));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task OnARotatedPage_DrawingsLandWhereTheyWerePut(int rotate)
    {
        var (document, _) = await OpenAsync(rotate: rotate);
        using (document)
        {
            var size = document.PageSizes[0];
            double y = size.Height / 2;
            var ink = await document.AddInkAsync(0, Line(20, y, size.Width - 20, y));
            var pixel = await RenderAsync(document);
            Assert.False(IsWhite(pixel((int)(size.Width / 2), (int)y)));
            Assert.True(IsWhite(pixel((int)(size.Width / 2), (int)y + 12)));

            Assert.Equal(new Point(20, y), ink.Ink!.Strokes[0][0]);
            await document.RemoveAnnotationAsync(ink);
            var text = await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 40, size.Width - 40, 0), "Upright", Blue, 18));
            Assert.Equal(40, text.Bounds.Top, 1);
            Assert.Equal(20, text.Bounds.Left, 1);
            Assert.Equal(size.Width - 40, text.Bounds.Width, 1);
            Assert.True(HasInk(await RenderAsync(document), 20, 40, 80, (int)text.Bounds.Bottom));
        }
    }

    [Fact]
    public async Task FreeText_OnARotatedPage_ReadsTheSameAsOnAnUprightOne()
    {
        // The same text reads upright on screen, so it fills the same pixels as on a page without /Rotate.
        var (upright, _) = await OpenAsync("0 0 200 200");
        var (turned, _) = await OpenAsync("0 0 200 200", 90);
        using (upright)
        using (turned)
        {
            foreach (var document in new[] { upright, turned })
                await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 40, 160, 0), "Lp", Blue, 40));
            var a = await RenderAsync(upright);
            var b = await RenderAsync(turned);
            int different = 0;
            for (int y = 0; y < 200; y++)
                for (int x = 0; x < 200; x++)
                    if (Math.Abs(a(x, y).R - b(x, y).R) > 64) different++;
            Assert.True(different < 30, $"{different} pixels differ.");
        }
    }

    [Fact]
    public async Task TextBox_ThatWouldRunOffThePage_MovesUpOntoIt()
    {
        string path = _temp.Write("doc.pdf", TestPdf.Create());
        using var document = await PdfDocument.OpenAsync(path);

        var low = await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 290, 160, 0), "one\ntwo\nthree", Blue, 12));
        Assert.True(low.Bounds.Top < 290);
        Assert.Equal(300, low.Bounds.Bottom, 2);
        var pixel = await RenderAsync(document);
        Assert.True(HasInk(pixel, 20, (int)low.Bounds.Top, 180, 300));

        var tall = await document.AddFreeTextAsync(0, new FreeText(new Rect(20, 250, 160, 0), string.Join('\n', Enumerable.Repeat("line", 40)), Blue, 12));
        Assert.Equal(0, tall.Bounds.Top, 2);
    }

    [Fact]
    public void DefaultAppearance_IsParsed()
    {
        var parsed = PdfDocument.ParseDefaultAppearance("/Helv 12.5 Tf 1 0 0.5 rg");
        Assert.Equal(12.5, parsed!.Value.FontSize);
        Assert.Equal(Color.FromArgb(255, 255, 0, 128), parsed.Value.Color);

        Assert.Equal(Color.FromArgb(255, 0, 255, 0), PdfDocument.ParseDefaultAppearance("0 1 0 rg")!.Value.Color);
        Assert.Null(PdfDocument.ParseDefaultAppearance("0 1 0 rg")!.Value.FontSize);
        Assert.Null(PdfDocument.ParseDefaultAppearance(""));
        Assert.Null(PdfDocument.ParseDefaultAppearance("rg Tf 1 rg"));
        Assert.Null(PdfDocument.ParseDefaultAppearance("/Helv 1e999 Tf 0 0 0 rg")!.Value.FontSize);
        Assert.Null(PdfDocument.ParseDefaultAppearance("NaN 0 0 rg"));
    }

    [Theory]
    [InlineData("<< /Type /Annot /Subtype /Ink /Rect [50 50 90 100] /InkList [] /Border [0 0 2] /DA (1 0 0 rg) /AP << /N {appearance} >> /NM (recto-empty) >>")]
    [InlineData("<< /Type /Annot /Subtype /Ink /Rect [50 50 90 100] /InkList [[50 50 90 10000000]] /Border [0 0 2] /DA (1 0 0 rg) /AP << /N {appearance} >> /NM (recto-far) >>")]
    [InlineData("<< /Type /Annot /Subtype /Ink /Rect [50 50 90 100] /InkList [[50 50 90 100]] /Border [0 0 10000000] /DA (1 0 0 rg) /AP << /N {appearance} >> /NM (recto-wide) >>")]
    public async Task Ink_NamedAsOursButUnusable_IsLeftAlone(string annotation)
    {
        string path = _temp.Write("doc.pdf", TestPdf.Create(annots: [annotation]));
        using var document = await PdfDocument.OpenAsync(path);
        var ink = await OnlyAnnotationAsync(document);

        Assert.Null(ink.Ink);
        Assert.Null(await document.SetInkAsync(ink, Line(10, 10, 20, 20)));
        Assert.False(document.IsModified);
    }

    [Theory]
    [InlineData("<< /Type /Annot /Subtype /FreeText /Rect [50 50 90 100] /DA (/Helv 1e999 Tf 0 0 0 rg) /Contents (x) /AP << /N {appearance} >> /NM (recto-size) >>")]
    [InlineData("<< /Type /Annot /Subtype /FreeText /Rect [50 50 90 100] /DA (/Helv 10000000 Tf 0 0 0 rg) /Contents (x) /AP << /N {appearance} >> /NM (recto-large) >>")]
    [InlineData("<< /Type /Annot /Subtype /FreeText /Rect [50 50 90000000 100] /DA (/Helv 12 Tf 0 0 0 rg) /Contents (x) /AP << /N {appearance} >> /NM (recto-rect) >>")]
    public async Task FreeText_NamedAsOursButUnusable_IsLeftAlone(string annotation)
    {
        string path = _temp.Write("doc.pdf", TestPdf.Create(annots: [annotation]));
        using var document = await PdfDocument.OpenAsync(path);
        var text = await OnlyAnnotationAsync(document);

        Assert.Null(text.FreeText);
        Assert.Null(await document.SetFreeTextAsync(text, new FreeText(new Rect(10, 10, 100, 20), "y", Red, 12)));
        Assert.False(document.IsModified);
    }

    [Fact]
    public void StrokesAt_FindsTheStrokesNearAPoint_NotJustTheirBounds()
    {
        var ink = new InkDrawing([[new Point(0, 0), new Point(100, 0)], [new Point(0, 50), new Point(100, 50)]], Red, 4);

        Assert.Equal([0], ink.StrokesAt(new Point(50, 3), 2));
        Assert.Equal([1], ink.StrokesAt(new Point(50, 49), 0));
        Assert.Empty(ink.StrokesAt(new Point(50, 25), 2));
        Assert.Empty(ink.StrokesAt(new Point(120, 0), 2));
        Assert.Equal([0, 1], ink.StrokesAt(new Point(50, 25), 25));
        Assert.Equal([0], new InkDrawing([[new Point(10, 10)]], Red, 4).StrokesAt(new Point(11, 10), 0));
    }

    [Fact]
    public void Bounds_CoverTheStrokesWithTheirWidth()
    {
        var ink = new InkDrawing([[new Point(10, 20), new Point(30, 25)], [new Point(5, 40)]], Red, 2);
        Assert.Equal(new Rect(4, 19, 27, 22), ink.Bounds());
        Assert.True(new InkDrawing([], Red, 2).Bounds().IsEmpty);
    }
}
