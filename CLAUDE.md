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
1. `DirectoryScanner` — discovers files matching include/exclude glob patterns; built-in excludes cover `bin`, `obj`, `.git`, `node_modules`, etc. Directory symlinks/junctions are never followed (loop protection). When `ScanOptions.RespectGitignore` is set, `GitIgnoreRules` (nested `.gitignore` support plus the global `core.excludesFile` and repo-local `.git/info/exclude`, negation, anchoring, `**`) filters the results. `ScanOptions.IncludeLangs`/`ExcludeLangs` filter by resolved language name. `ScanFiles()` resolves an explicit file list (used by `--list-file`), bypassing globbing/recursion/`.gitignore` entirely.
2. `FileAnalyzer` — reads a file (or in-memory text), delegates per-line classification to `LineClassifier`
3. `LineClassifier` — stateful classifier that tracks open block comments across lines, emitting a `LineKind` (Code / Comment / Blank) per line
4. `Languages/LanguageRegistry` — static map of file extensions → `LanguageDefinition` (comment tokens, block-comment pairs, health-display flag) and string-literal delimiters; 44 built-in languages. Resolution is by extension, with a filename fallback (`LanguageDefinition.Filenames`) for extensionless files like `Makefile`, `Dockerfile`, `CMakeLists.txt`
5. `Models/` — `FileAnalysis` (per-file counts), `AnalysisSummary` (aggregated by language with comment-health percentiles), `SkippedEntry` (path + reason for files that could not be read)

**`src/Sloc.Cli`** — Console entry point. `Program.cs` uses `System.CommandLine` for argument parsing. `AnalyzeHandler` orchestrates scanning → analysis → rendering. Renderers in `Output/` implement `IResultRenderer`: `TableRenderer` (Spectre.Console ANSI table, static), `JsonRenderer`, `HtmlRenderer`, `CsvRenderer`, `MarkdownRenderer`. All user-visible strings are plain English literals inline (no localization/resources layer).

**`tests/Sloc.Core.Tests`** — xUnit tests covering `LineClassifier`, `FileAnalyzer`, and `LanguageRegistry`.

## CLI Options

`sloc <path> [options]` — key flags: `--include`/`-i` and `--exclude`/`-e` (repeatable globs), `--exclude-dir <name>` (repeatable; shorthand for `--exclude "**/<name>/**"`), `--include-lang`/`--exclude-lang` (repeatable language display names, e.g. `"C#"`, matched case-insensitively against the resolved language), `--list-file <path>` (analyze exactly the files listed one-per-line in this file instead of scanning `path`; `-` reads the list from stdin), `--format`/`-f` (`Table`/`Json`/`Html`/`Csv`/`Markdown`), `--output`/`-o` (format inferred from extension if `--format` omitted; `-` writes to stdout), `--no-recursive`, `--no-health`, `--by-file`, `--detailed` (Json/Html/Markdown: emit language summary and per-file breakdown together), `--paged`/`-p`, `--all` (include unknown extensions, grouped as `Other`), `--unique` (count byte-identical files only once; later duplicates are reported as skipped), `--quiet`/`-q`, `--no-progress`, `--min-comment-pct` (threshold gate), `--jobs`/`-j` (parallelism; default processor count, `1` = sequential), `--no-gitignore` (`.gitignore`, `.git/info/exclude`, and the global `core.excludesFile` are honored by default), `--follow-symlinks` (descend into symlinked/junctioned directories instead of skipping them; a symlink that loops directly back to one of its own ancestors is still skipped), `--baseline <report.json>` (diff against a saved report), `--sort <Total|Code|Comment|Blank|Files|Name>`, `--top <N>` (limit language rows), `--no-update-check` (skip the GitHub release check). `Json`/`Html`/`Csv`/`Markdown` all default to stdout when `--output` is omitted. File analysis runs in parallel (`Parallel.For`), merged back in scan order so output is deterministic regardless of `--jobs`. Exit codes: `0` success, `1` path error, `2` threshold not met, `3` unexpected error (see `ExitCode` in `AnalyzeHandler.cs`).

## Known Limitations

Line classification is text-based, not a full lexer. String literals are tracked for most languages (via `LanguageDefinition.StringLiterals`), so comment markers inside strings (e.g. `"// not a comment"`) count as code and escaped quotes are handled, including asymmetric open/close delimiters and doubled-closing-delimiter escaping (`StringLiteral.CloseDelimiter`/`DoubledClosingEscape`, used for C# verbatim strings `@"…""…"` and Rust raw strings `r"…"`/`r#"…"#`); block comments can nest where the language allows (`BlockComment.AllowNested`, e.g. Rust/F#/Swift). Not modeled: Rust raw strings with more than one `#`, and string interpolation expressions. Python triple-quoted literals count as comments only when they begin a statement (docstrings); as a value (`x = """…"""`) they count as code.

## Key Configuration

- **`Directory.Build.props`** — global settings: targets `net8.0`, nullable enabled, warnings-as-errors, artifacts output layout
- **`Directory.Packages.props`** — central NuGet version management; add new packages here, not in individual `.csproj` files
- **Versioning** — MinVer derives the NuGet version automatically from git tags (`v*`); do not set `<Version>` manually. CI publish/pack/release jobs only run on `v*` tag pushes.

## Adding a New Language

Add an entry to `LanguageRegistry.cs` (`src/Sloc.Core/Languages/`) with the extension(s), line-comment tokens, block-comment delimiters, string-literal delimiters (`StringLiterals`, so comment tokens inside strings are not miscounted), and whether to show the comment-health indicator. No other files need changes.

## Error Handling for Unreadable Files

`DirectoryScanner.Scan()` returns a `ScanResult` record containing both `Files` and `Skipped` (`IReadOnlyList<SkippedEntry>`). Enumeration errors (e.g. `UnauthorizedAccessException`) are caught there. Per-file read errors are caught in `AnalyzeHandler` (`AnalyzeAt`), which also catches `BinaryFileException` (files with NUL bytes in the first 8 KB are skipped with reason "binary file"). Both sets of skipped entries are merged and stored in `AnalysisSummary.Skipped`, which renderers use to emit a skipped-files section at the end of output.
