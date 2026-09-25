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

# Run performance benchmarks (BenchmarkDotNet; pass --filter '*Classifier*' etc. to narrow)
dotnet run -c Release --project benchmarks/Sloc.Benchmarks -- --filter '*'

# Pack as NuGet tool
dotnet pack src/Sloc.Cli/Sloc.Cli.csproj -c Release -o ./nupkg

# Install the tool locally and run it
dotnet tool install --global --add-source ./nupkg Sloc
sloc ./src
```

## Architecture

The solution has three projects:

**`src/Sloc.Core`** — Analysis engine (library). The pipeline is:
1. `DirectoryScanner` — discovers files matching include/exclude glob patterns; built-in excludes cover `bin`, `obj`, `.git`, `node_modules`, etc. Directory symlinks/junctions are never followed (loop protection); other reparse points without a link target (e.g. OneDrive Files-On-Demand folders and cloud files) are scanned as ordinary directories/files (`SymlinkGuard.IsLink`). When `ScanOptions.RespectGitignore` is set, `GitIgnoreRules` (nested `.gitignore` support plus the global `core.excludesFile` and repo-local `.git/info/exclude`, negation, anchoring, `**`, character classes negated with `!` or `^`, backslash escapes) filters the results. Both rule sets are anchored at the enclosing git working tree (`GitWorkTree.Find`, which also follows a `.git` file for linked worktrees/submodules and reads `config`/`info/exclude` from the common directory): rule-file bases are repository-relative, so scanning a subdirectory also applies the `.gitignore`/`.gitattributes` files above it up to the repository root, while the scan root and its ancestors are never treated as ignored. With `RespectGitignore` set and the scan root inside a repository, `ScanTreeWalker` also skips nested repositories (subdirectories holding a `.git` directory or file, e.g. submodules), reporting them as skipped, just as the `--git-hash`/`--compare-to` snapshot skips gitlink entries. When `ScanOptions.RespectGitAttributes` is set (default), `GitAttributesRules` (nested `.gitattributes` support, sharing `GitIgnoreRules`' glob-to-regex compiler) excludes files marked `linguist-vendored`/`linguist-generated`. `ScanOptions.IncludeLangs`/`ExcludeLangs` filter by resolved language name. `ScanFiles()` resolves an explicit file list (used by `--list-file`), bypassing globbing/recursion/`.gitignore`/`.gitattributes` entirely. `ScanSnapshot()` applies `Scan()`'s filtering to files given with a scan-root-relative path instead of a real tree (used by `--compare-to` for the extracted commit, passing a `SnapshotRepository` so the rule files above the scan root come from that commit rather than the working tree).
2. `FileAnalyzer` — reads a file (or in-memory text), delegates per-line classification to `LineClassifier`
3. `LineClassifier` — stateful classifier that tracks open block comments across lines, emitting a `LineKind` (Code / Comment / Blank) per line
4. `Languages/LanguageRegistry` — static map of file extensions → `LanguageDefinition` (comment tokens, block-comment pairs, health-display flag) and string-literal delimiters; 58 built-in languages. Resolution is by extension, with a filename lookup (`LanguageDefinition.Filenames`, checked first; case-insensitive unless `CaseSensitiveFilenames`, set for Bazel's `BUILD`/`WORKSPACE`) for files like `Makefile`, `Dockerfile`, `CMakeLists.txt`
5. `Models/` — `FileAnalysis` (per-file counts), `AnalysisSummary` (aggregated by language with comment-health percentiles), `SkippedEntry` (path + reason for files that could not be read)
6. `GitSnapshotExtractor` — used by `--git-hash`; shells out to `git` (`ls-tree`, `cat-file --batch`) to dump a commit/tree-ish's blobs into a temporary directory without checking it out; blobs whose path has a smudge filter driver (e.g. Git LFS, which may download) or a `working-tree-encoding` attribute (found via `check-attr`) are dumped one by one with `cat-file --filters` instead, so their content matches a checkout, and a failing filter skips the blob. It returns a `GitSnapshot` (temp-path ↔ git-relative-path pairs, plus symlinks/submodules as `SkippedEntry`). `AnalyzeHandler` scans the temp files via `ScanFiles()` like `--list-file`, then remaps result paths back to git-relative paths before rendering.

**`src/Sloc.Cli`** — Console entry point. `Program.cs` uses `System.CommandLine` for argument parsing. `AnalyzeHandler` orchestrates scanning → analysis → rendering. Renderers in `Output/` implement `IResultRenderer`: `TableRenderer` (Spectre.Console ANSI table, static), `JsonRenderer`, `HtmlRenderer`, `CsvRenderer`, `MarkdownRenderer`. All user-visible strings are plain English literals inline (no localization/resources layer).

**`tests/Sloc.Core.Tests`** — xUnit tests covering `LineClassifier`, `FileAnalyzer`, and `LanguageRegistry`.

**`benchmarks/Sloc.Benchmarks`** — BenchmarkDotNet benchmarks for the analysis hot path (`LineClassifier` over in-memory text, and `FileAnalyzer.Analyze` over files on disk with/without hashing). `FileAnalyzer` reads files up to `InMemoryThreshold` (4 MB) into a pooled buffer once and classifies lines as spans; larger files use the streaming path. Run before and after changes to either.

## CLI Options

`sloc <path> [options]` — key flags: `--include`/`-i` and `--exclude`/`-e` (repeatable globs), `--exclude-dir <name>` (repeatable; shorthand for `--exclude "**/<name>/**"`), `--include-lang`/`--exclude-lang` (repeatable language display names, e.g. `"C#"`, matched case-insensitively against the resolved language), `--list-file <path>` (analyze exactly the files listed one-per-line in this file instead of scanning `path`; `-` reads the list from stdin), `--git-hash <commit>` (analyze the repository tree as of this commit/tree-ish without checking it out; `path` is used as the repo root; mutually exclusive with `--list-file`; requires `git` on `PATH`; symlink and submodule tree entries are skipped and reported in the skipped list; `--include`/`--exclude` globs, `--no-recursive`, `--follow-symlinks`, `--no-gitignore`, and `--no-gitattributes` have no effect in this mode since `git ls-tree` only ever lists tracked blobs at that commit), `--format`/`-f` (`Table`/`Json`/`Html`/`Csv`/`Markdown`), `--output`/`-o` (format inferred from extension if `--format` omitted; `-` writes to stdout), `--no-recursive`, `--no-health`, `--by-file`, `--detailed` (Json/Html/Markdown: emit language summary and per-file breakdown together), `--paged`/`-p`, `--all` (include unknown extensions, grouped as `Other`), `--unique` (count byte-identical files only once; later duplicates are reported as skipped), `--quiet`/`-q`, `--no-progress`, `--watch`/`-w` (Table format only; re-scans and refreshes the live table when files under `path` change, debounced ~400ms, until Ctrl+C; mutually exclusive with `--git-hash`, `--list-file`, and `--baseline`), `--min-comment-pct` (threshold gate), `--jobs`/`-j` (parallelism; default processor count, `1` = sequential), `--no-gitignore` (`.gitignore`, `.git/info/exclude`, and the global `core.excludesFile` are honored by default), `--no-gitattributes` (files marked `linguist-vendored`/`linguist-generated` in `.gitattributes` are excluded by default), `--follow-symlinks` (descend into symlinked/junctioned directories instead of skipping them; a symlink that loops back to one of its own ancestors, or points at a directory containing one, is still skipped; links whose real target is inside `path` are skipped too, and an external file or directory reached through several links is counted once; paths are compared after resolving every link via `SymlinkGuard.GetRealPath`, so this holds even when `path` itself is a symlink/junction), `--baseline <report.json>` (diff against a saved report), `--compare-to <commit>` (diff against `path` as of another commit/tree-ish, extracted via `GitSnapshotExtractor` the same way as `--git-hash` and scoped to the same subdirectory (or single file) as `path`, without checking it out or saving a baseline file first; unless combined with `--git-hash`, the commit's files go through `DirectoryScanner.ScanSnapshot`, which applies the same globs, built-in excludes, `--no-recursive`, and the commit's own `.gitignore`/`.gitattributes` as the working-tree `Scan()`, so unchanged files diff to zero; requires `git` on `PATH`; mutually exclusive with `--baseline`, `--watch`, and `--list-file`; combine with `--git-hash` to diff two commits directly; output is Table/Json only, same restriction as `--baseline`), `--sort <Total|Code|Comment|Blank|Files|Name|CommentPct>`, `--top <N>` (limit language rows), `--no-update-check` (skip the GitHub release check). `Json`/`Html`/`Csv`/`Markdown` all default to stdout when `--output` is omitted. File analysis runs in parallel (`Parallel.For`), merged back in scan order so output is deterministic regardless of `--jobs`. Exit codes: `0` success, `1` path error, `2` threshold not met, `3` unexpected error (see `ExitCode` in `AnalyzeHandler.cs`).

## Known Limitations

Line classification is text-based, not a full lexer. String literals are tracked for most languages (via `LanguageDefinition.StringLiterals`), so comment markers inside strings (e.g. `"// not a comment"`) count as code and escaped quotes are handled, including asymmetric open/close delimiters and doubled-closing-delimiter escaping (`StringLiteral.CloseDelimiter`/`DoubledClosingEscape`, used for C# verbatim strings `@"…""…"` and Rust raw strings `r"…"`/`r#"…"#`; C# raw string literals and Java text blocks `"""…"""` are multi-line literals); block comments can nest where the language allows (`BlockComment.AllowNested`, e.g. Rust/F#/Swift). JavaScript-family regex literals (`LanguageDefinition.RegexLiterals`, e.g. `/\/*$/`) are recognized heuristically: a `/` starts one only where an operand is expected (not after an identifier, number, `)`, `]`, or string) and the literal closes on the same line. `LanguageDefinition.LineCommentExceptions` lists tokens that start like a line comment but are not one (PHP 8 attributes `#[…]`); `LanguageDefinition.BlockCommentExceptions` does the same for block-comment opens (F#'s `(*)` operator, which neither opens nor nests a comment). C/C++ digit separators (`1'000`, `LanguageDefinition.QuoteDigitSeparators`) do not open a character literal. Not modeled: Rust raw strings with more than one `#`, C# raw strings delimited by four or more quotes, C++ raw strings with a custom delimiter (`R"x(…)x"`; only `R"(…)"` is recognized), Lua long-bracket comments beyond level 4 (`--[=====[`), Perl POD blocks opened by commands other than the common ones (`=pod`, `=head1`–`=head6`, `=over`, `=item`, `=back`, `=begin`, `=end`, `=for`, `=encoding`), and string interpolation expressions. The complexity metric's alphabetic keywords match whole words only (so single-word forms like `foreach`/`elseif`/`elif` are listed explicitly), and C-style ternaries (`a ? b : c`) are not counted: the `?:` token only matches that literal operator (e.g. PHP's shorthand ternary). Tokens are matched textually, so ones that are not branches in context still inflate the count: TypeScript's optional-member `?:` (`name?: string`), Rust's empty closure parameters `||`, and C++'s rvalue/forwarding reference `&&` (`T&&`). Python triple-quoted literals count as comments only when they begin a statement (docstrings, optionally prefixed with `r`/`u`, `LanguageDefinition.DocStringPrefixes`); as a value (`x = """…"""`), inside brackets left open by an earlier line, or after a trailing `\` continuation they count as code.

`--git-hash` requires a `git` executable on `PATH` (no bundled/vendored git); `--include`/`--exclude` glob filtering does not apply in this mode (same limitation as `--list-file`); git symlink and submodule (gitlink) tree entries are always skipped, never analyzed.

## Key Configuration

- **`Directory.Build.props`** — global settings: targets `net10.0`, nullable enabled, warnings-as-errors, artifacts output layout
- **`Directory.Packages.props`** — central NuGet version management; add new packages here, not in individual `.csproj` files
- **Versioning** — MinVer derives the NuGet version automatically from git tags (`v*`); do not set `<Version>` manually. CI publish/pack/release jobs only run on `v*` tag pushes.

## Adding a New Language

Add an entry to `LanguageRegistry.cs` (`src/Sloc.Core/Languages/`) with the extension(s), line-comment tokens, block-comment delimiters, string-literal delimiters (`StringLiterals`, so comment tokens inside strings are not miscounted), and whether to show the comment-health indicator. No other files need changes.

## Error Handling for Unreadable Files

`DirectoryScanner.Scan()` returns a `ScanResult` record containing both `Files` and `Skipped` (`IReadOnlyList<SkippedEntry>`). Enumeration errors (e.g. `UnauthorizedAccessException`) are caught there. Per-file read errors are caught in `AnalyzeHandler` (`AnalyzeAt`), which also catches `BinaryFileException` (files with a NUL byte anywhere in the file, not just a leading window, are skipped with reason "binary file"). Both sets of skipped entries are merged and stored in `AnalysisSummary.Skipped`, which renderers use to emit a skipped-files section at the end of output.
