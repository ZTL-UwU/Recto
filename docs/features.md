# Features

- **Start page**: recent documents as thumbnail cards, with a search box (Ctrl+F) that filters them by name or path; drag and drop PDFs onto the window.
- **Viewer**: continuous or single-page, dual pages (optionally odd on the left), fit page / fit width / custom zoom, Ctrl+wheel and pinch zoom, rotation, night mode, full screen, presentation mode.
- **Sidebar**: thumbnails, outline, annotations, attachments.
- **Search**: results with snippets in the sidebar, selecting the next one from the current page; match case, whole words; F3 / Shift+F3, or Enter / Shift+Enter in the search box. Finds words hyphenated across lines, and plain quotes and hyphens match curly quotes and dashes (each can be turned off in Settings). Pages are read once, starting when search opens, so searching again is instant.
- **Text**: select, copy, double-click a word; internal and web links.
- **Annotations**: five highlight colors and sticky notes; a pen, a highlighter and an eraser for drawing on pages, with a mouse or a stylus (a finger scrolls); text boxes in any language, which move, resize and take a color and size; edit, delete, undo and redo (Ctrl+Z, Ctrl+Y), and save to a copy of the PDF (Ctrl+S) as an incremental update, leaving the original bytes (and signatures) intact. Drawings and text boxes are standard PDF annotations, so other viewers show them too.
- **Printing** from its own dialog with a live preview: page ranges, odd or even pages, copies, several pages per sheet, scaling, paper size, color, two-sided, and the printer's own settings. Pages go to the printer as vector output, so text prints sharp; Ctrl+Shift+P uses the Windows print dialog instead.
- A **Properties** dialog, and **password-protected** documents.
- Remembers page, zoom and view mode per document, and reloads when the file changes on disk (encrypted files reuse the session password).
- One process per session: PDFs opened from Explorer open as new windows in the running instance.
- **Explorer thumbnails** (Store/MSIX build): File Explorer shows the first page as the PDF's thumbnail, with a small Recto badge in the corner, while Recto is the default PDF app.

Press F1 for keyboard shortcuts.

## Limitations

- **Forms** aren't interactive; saved values show only if the file includes appearances.
- **Signatures** are shown but not validated. After saving annotations, a signature covers only the earlier revision.
- **Drawings and text boxes from other programs** can be deleted from the Annotations sidebar, but aren't moved, edited or erased, as what they look like is only in their appearance stream. Deleting one can't be undone.
- **JavaScript** and **launch actions** are ignored. Only `http`, `https` and `mailto` links open, and never ones with a user name (`https://bank.example@evil.example/`). Link hover shows the host in ASCII to expose look-alike names.
- **Permission flags** (no copy/print/modify) aren't enforced, as in Papers and Evince: they don't protect the content and get in the way of legitimate use.
- **Attachments**: PDFs open in Recto; other files open in their default app after confirmation. Executables, scripts, shortcuts and installers are only saved, never opened, and types Windows parses on folder view (`.url`, `.lnk`, `.scf`, `.library-ms`, …) get `.txt` appended. Extracted files get the Mark of the Web, and aren't opened if the drive can't store it.
- **Explorer thumbnails** skip password-protected PDFs and files over 4 GB.
- **Size**: files up to 8 GB, up to 1,000,000 pages, attachments up to 2 GB.
- **Screen readers** get the page number, but page text isn't exposed to UI Automation yet.
