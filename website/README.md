# Recto website

The landing page, built with [Astro](https://astro.build) into a static site in `dist/`. The styling follows Windows 11 (Fluent 2): Mica behind the page, acrylic under the nav bar, WinUI's buttons, cards and type ramp, and [Fluent System Icons](https://github.com/microsoft/fluentui-system-icons).

```powershell
bun install
bun run dev       # http://localhost:4321
bun run build     # type-check, then build to dist/
bun run preview   # serve dist/
```

- `src/components/FeatureStack.astro`: the feature tour. A colored panel per feature, each pulling up over the last as the page scrolls. It's a plain list with reduced motion, or without JavaScript.
- `src/assets/screens/`: the app screenshots, captured from a Debug build at 1360×820 on a 150% display (so 2022×1221 pixels) with its own data folder (`RECTO_DATA_FOLDER`), with Mica replaced by a solid color: the panel color 71% of the way to white, about what Mica makes of a wallpaper that color. Astro converts them to WebP at build time.
- `public/icon.svg`, `public/favicon.svg`: copies of `tools/Recto.Tools/AppIcon/AppIcon-full.svg` and `AppIcon-small.svg`.
- The benchmark numbers in `src/components/Performance.astro` are the ones in the README chart.
