# Notes for coding agents

Building and testing: [docs/building.md](docs/building.md). Run every command from the root of your checkout or worktree; the tools act on the checkout they're in. Details: [docs/tools.md](docs/tools.md).

- **Let the user try a change:** `pwsh tools/dev.ps1`. It builds your checkout (Release, Native AOT; takes a minute or two), registers it as "Recto Dev (*branch*)" and starts it. Add PDF paths to open them. It only closes Recto builds from your own checkout, so it's safe to run while other worktrees have theirs open. Then tell the user the build is running, and that they can also open PDFs with it from File Explorer's **Open with** menu.
- **Just check that it builds:** `dotnet build src/Recto/Recto.csproj` and `dotnet test tests/Recto.Tests`.
- **Benchmark:** `dotnet run --project tools/Recto.Tools -- bench`. It takes several minutes and opens viewer windows, so only run it when asked. If it stops because a viewer is running, ask the user to close it; don't pass `--close-running` unless they say so, as it discards unsaved work.
- **App icon:** edit the SVGs in `tools/Recto.Tools/AppIcon`, then run `dotnet run --project tools/Recto.Tools -- icon` and commit the regenerated files in `src/Recto/Assets`.
