# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build Sloc.slnx

# Run all tests
dotnet test Sloc.slnx

# Run a single test class
dotnet test tests/Sloc.Core.Tests --filter "FullyQualifiedName~LineClassifierTests"

# Pack as NuGet tool
dotnet pack src/Sloc.Cli/Sloc.Cli.csproj -c Release -o ./nupkg

# Install the tool locally and run it
dotnet tool install --global --add-source ./nupkg Sloc
sloc ./src
```

## Architecture

The solution has three projects:

**`src/Sloc.Core`** — Analysis engine (library). The pipeline is:
1. `DirectoryScanner` — discovers files matching include/exclude glob patterns; built-in excludes cover `bin`, `obj`, `.git`, `node_modules`, etc.
2. `FileAnalyzer` — reads a file (or in-memory text), delegates per-line classification to `LineClassifier`
3. `LineClassifier` — stateful classifier that tracks open block comments across lines, emitting a `LineKind` (Code / Comment / Blank) per line
4. `Languages/LanguageRegistry` — static map of file extensions → `LanguageDefinition` (comment tokens, block-comment pairs, health-display flag); 23 built-in languages
5. `Models/` — `FileAnalysis` (per-file counts), `AnalysisSummary` (aggregated by language with comment-health percentiles), `SkippedEntry` (path + reason for files that could not be read)

**`src/Sloc.Cli`** — Console entry point. `Program.cs` uses `System.CommandLine` for argument parsing. `AnalyzeHandler` orchestrates scanning → analysis → rendering. Renderers in `Output/` implement `IResultRenderer`: `TableRenderer` (Spectre.Console ANSI table), `JsonRenderer`, `HtmlRenderer`. All user-visible strings are plain English literals inline (no localization/resources layer).

**`tests/Sloc.Core.Tests`** — xUnit tests covering `LineClassifier`, `FileAnalyzer`, and `LanguageRegistry`.

## Key Configuration

- **`Directory.Build.props`** — global settings: targets `net8.0`, nullable enabled, warnings-as-errors, artifacts output layout
- **`Directory.Packages.props`** — central NuGet version management; add new packages here, not in individual `.csproj` files
- **Versioning** — MinVer derives the NuGet version automatically from git tags (`v*`); do not set `<Version>` manually. CI publish/pack/release jobs only run on `v*` tag pushes.

## Adding a New Language

Add an entry to `LanguageRegistry.cs` (`src/Sloc.Core/Languages/`) with the extension(s), line-comment tokens, block-comment delimiters, and whether to show the comment-health indicator. No other files need changes.

## Error Handling for Unreadable Files

`DirectoryScanner.Scan()` returns a `ScanResult` record containing both `Files` and `Skipped` (`IReadOnlyList<SkippedEntry>`). Enumeration errors (e.g. `UnauthorizedAccessException`) are caught there. Per-file read errors are caught in `AnalyzeHandler.AnalyzeFile()`. Both sets of skipped entries are merged and stored in `AnalysisSummary.Skipped`, which renderers use to emit a skipped-files section at the end of output.
