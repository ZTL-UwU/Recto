# Building

Requires Windows 11 and the .NET 10 SDK. Publishing also needs Visual Studio's C++ build tools ("Desktop development with C++", plus the ARM64 tools for ARM64), which Native AOT links with.

```powershell
dotnet build src/Recto/Recto.csproj -c Release
src\Recto\bin\Release\net10.0-windows10.0.26100.0\win-x64\Recto.exe [file.pdf]
dotnet test tests/Recto.Tests
dotnet publish src/Recto/Recto.csproj -c Release -o out\win-x64   # Native AOT, as CI ships
```

For ARM64, add `-p:Platform=ARM64` (plus `-r win-arm64` when publishing). CI (`.github/workflows/build.yml`) publishes both architectures, verifies the bundled `pdfium.dll`, `Recto.pri` and license files, and runs the tests.

The tests build the UI-free code (`src/Recto/Pdf`, `Services/LaunchPolicy.cs`, the thumbnail handler in `src/Recto.Thumbnails`) against the real PDFium and generate their own PDFs.

The app is unpackaged and self-contained: it bundles .NET and the Windows App SDK 2.5 runtime. It references only the WinUI and Foundation components, not the `Microsoft.WindowsAppSDK` meta-package (whose AI/ML parts add ~60 MB). `dotnet publish` compiles it with Native AOT, which trims the output and can't JIT, so test a published build after adding reflection-based code; classes that WinUI holds on to (items, controls) must be `partial`. PDFium comes from the `bblanchon.PDFium.Win32` package.

To try a build as your PDF app, measure performance or rebuild the icon, see [Tools](tools.md). For the Store package, see [Packaging](packaging.md).

## Source layout

| Path | Purpose |
| --- | --- |
| `src/Recto/Pdf` | PDFium interop, the single-threaded work queue, and the document model |
| `src/Recto/Controls/DocumentView.cs` | Virtualized, zoomable page surface (pages laid out at 100%; ScrollViewer zoom is the zoom level), selection, links, annotation flyouts; `DocumentView.Drawing.cs` has the drawing and text tools |
| `src/Recto/Controls/PageView.cs` | One page: bitmap, zoomed-in tiles, highlight overlay |
| `src/Recto/MainWindow.xaml` | Title bar, sidebar, start page |
| `src/Recto/Printing`, `PrintDialog.xaml` | Print dialog, page layout on sheets (unit tested), printers and GDI print jobs |
| `src/Recto/Services` | Persisted state, the Windows print dialog, single-instance handoff, link and attachment policy |
| `src/Recto/Package.appxmanifest`, `Assets/Package` | MSIX manifest and logos |
| `src/Recto/Program.cs` | Entry point; forwards files to a running instance |
| `src/Recto.Thumbnails` | File Explorer thumbnail handler (Native AOT COM DLL, MSIX and dev builds only) |
| `tests/Recto.Tests` | xUnit tests |
| `tools` | Developer tools: build-and-try script, benchmark, icon build ([Tools](tools.md)) |
| `LICENSE`, `licenses/`, `THIRD-PARTY-NOTICES.md` | License texts, copied into the build output |
