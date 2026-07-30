using System.Diagnostics.CodeAnalysis;

namespace Sloc.Core.Languages;

/// <summary>
/// A static registry of known languages, used to resolve a
/// <see cref="LanguageDefinition"/> from a file path or extension.
/// </summary>
public static class LanguageRegistry
{
    private static readonly IReadOnlyList<LanguageDefinition> AllLanguages = CreateLanguages();
    private static readonly IReadOnlyDictionary<string, LanguageDefinition> ExtensionLookup = CreateLookup(AllLanguages);
    private static readonly IReadOnlyDictionary<string, LanguageDefinition> FilenameLookup = CreateFilenameLookup(AllLanguages);
    private static readonly IReadOnlyList<(string Suffix, LanguageDefinition Language)> SuffixLookup = CreateSuffixLookup(AllLanguages);
    private static readonly IReadOnlyDictionary<string, bool> HealthSupportByName =
        AllLanguages.ToDictionary(language => language.Name, language => language.SupportsHealth, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets every language known to the registry.
    /// </summary>
    public static IReadOnlyList<LanguageDefinition> Languages => AllLanguages;

    /// <summary>
    /// Attempts to resolve a language from a file extension.
    /// </summary>
    /// <param name="extension">The extension to look up, with or without a leading dot.</param>
    /// <param name="language">The resolved language, if any.</param>
    /// <returns>
    /// <see langword="true"/> if a language was found; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryGetByExtension(string? extension, [NotNullWhen(true)] out LanguageDefinition? language)
    {
        language = null;
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        var normalized = extension.StartsWith('.') ? extension : "." + extension;
        return ExtensionLookup.TryGetValue(normalized, out language);
    }

    /// <summary>
    /// Attempts to resolve a language from a file path using its extension.
    /// </summary>
    /// <param name="path">The file path to inspect.</param>
    /// <param name="language">The resolved language, if any.</param>
    /// <returns>
    /// <see langword="true"/> if a language was found; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryGetByPath(string path, [NotNullWhen(true)] out LanguageDefinition? language)
    {
        var fileName = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(fileName))
        {
            if (FilenameLookup.TryGetValue(fileName, out language))
            {
                return true;
            }

            foreach (var (suffix, suffixLanguage) in SuffixLookup)
            {
                if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    language = suffixLanguage;
                    return true;
                }
            }
        }

        var extension = Path.GetExtension(path);
        return TryGetByExtension(extension, out language);
    }

    /// <summary>
    /// Determines whether comment-health analysis is meaningful for the language with
    /// the given display name. Unknown names are treated as unsupported.
    /// </summary>
    /// <param name="name">The language display name (e.g. "C#").</param>
    /// <returns>
    /// <see langword="true"/> if the language supports health analysis; otherwise <see langword="false"/>.
    /// </returns>
    public static bool SupportsHealth(string? name) =>
        name is not null && HealthSupportByName.TryGetValue(name, out var supports) && supports;

    private static IReadOnlyList<LanguageDefinition> CreateLanguages()
    {
        var cStyleBlock = new BlockComment("/*", "*/");
        var nestedCStyleBlock = new BlockComment("/*", "*/", AllowNested: true);
        var htmlBlock = new BlockComment("<!--", "-->");

        // Shared string-literal definitions. List longer delimiters before shorter ones
        // that share a prefix (e.g. """ before ") so the longer one matches first.
        var doubleQuote = new StringLiteral("\"");
        var singleQuote = new StringLiteral("'");
        var backtickTemplate = new StringLiteral("`", Multiline: true);
        var backtickRaw = new StringLiteral("`", Multiline: true, AllowEscape: false);
        var pyTripleDouble = new StringLiteral("\"\"\"", Multiline: true, IsDocComment: true);
        var pyTripleSingle = new StringLiteral("'''", Multiline: true, IsDocComment: true);
        var rawTripleDouble = new StringLiteral("\"\"\"", Multiline: true, AllowEscape: false);
        var rawTripleSingle = new StringLiteral("'''", Multiline: true, AllowEscape: false);
        var csVerbatimString = new StringLiteral(
            "@\"",
            Multiline: true,
            AllowEscape: false,
            CloseDelimiter: "\"",
            DoubledClosingEscape: true);
        // Rust raw strings (`r"…"` / `r#"…"#`). Delimiters with more than one `#` are not
        // modeled; they are rare in practice and would require a variable-length delimiter.
        var rustRawStringNoHash = new StringLiteral("r\"", Multiline: true, AllowEscape: false);
        var rustRawStringOneHash = new StringLiteral(
            "r#\"",
            Multiline: true,
            AllowEscape: false,
            CloseDelimiter: "\"#");

        return new List<LanguageDefinition>
        {
            new()
            {
                Name = "C#",
                Extensions = [".cs", ".csx"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [csVerbatimString, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "C",
                Extensions = [".c", ".h"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "C++",
                Extensions = [".cpp", ".hpp", ".cc", ".cxx", ".hxx", ".ipp"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Java",
                Extensions = [".java"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Kotlin",
                Extensions = [".kt", ".kts"],
                LineCommentTokens = ["//"],
                BlockComments = [nestedCStyleBlock],
                StringLiterals = [rawTripleDouble, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Swift",
                Extensions = [".swift"],
                LineCommentTokens = ["//"],
                BlockComments = [nestedCStyleBlock],
                StringLiterals = [rawTripleDouble, doubleQuote]
            },
            new()
            {
                Name = "JavaScript",
                Extensions = [".js", ".jsx", ".mjs", ".cjs"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [backtickTemplate, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "TypeScript",
                Extensions = [".ts", ".tsx", ".mts", ".cts"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [backtickTemplate, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Python",
                Extensions = [".py", ".pyw"],
                LineCommentTokens = ["#"],
                StringLiterals = [pyTripleDouble, pyTripleSingle, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Go",
                Extensions = [".go"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [backtickRaw, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Rust",
                Extensions = [".rs"],
                LineCommentTokens = ["//"],
                BlockComments = [nestedCStyleBlock],
                // Single quotes denote lifetimes as well as char literals in Rust, so they
                // are not treated as string delimiters here.
                StringLiterals = [rustRawStringOneHash, rustRawStringNoHash, doubleQuote]
            },
            new()
            {
                Name = "PHP",
                Extensions = [".php"],
                LineCommentTokens = ["//", "#"],
                BlockComments = [cStyleBlock],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Ruby",
                Extensions = [".rb"],
                Filenames = ["Rakefile", "Gemfile", "Guardfile", "Podfile"],
                LineCommentTokens = ["#"],
                BlockComments = [new BlockComment("=begin", "=end", RequireLineStart: true)],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "F#",
                Extensions = [".fs", ".fsi", ".fsx"],
                LineCommentTokens = ["//"],
                BlockComments = [new BlockComment("(*", "*)", AllowNested: true)],
                // Single quotes appear in generic type parameters (e.g. 'a), so only
                // double quotes are treated as string delimiters.
                StringLiterals = [doubleQuote]
            },
            new()
            {
                Name = "Visual Basic",
                Extensions = [".vb"],
                LineCommentTokens = ["'"],
                StringLiterals = [doubleQuote]
            },
            new()
            {
                Name = "SQL",
                Extensions = [".sql"],
                LineCommentTokens = ["--"],
                BlockComments = [cStyleBlock],
                StringLiterals = [singleQuote, doubleQuote],
                ShowHealth = false
            },
            new()
            {
                Name = "PowerShell",
                Extensions = [".ps1", ".psm1", ".psd1"],
                LineCommentTokens = ["#"],
                BlockComments = [new BlockComment("<#", "#>")],
                StringLiterals = [doubleQuote, singleQuote],
                ShowHealth = false
            },
            new()
            {
                Name = "Shell",
                Extensions = [".sh", ".bash", ".zsh"],
                LineCommentTokens = ["#"],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "YAML",
                Extensions = [".yml", ".yaml"],
                LineCommentTokens = ["#"],
                StringLiterals = [doubleQuote, singleQuote],
                ShowHealth = false
            },
            new()
            {
                Name = "JSON",
                Extensions = [".json", ".jsonc"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [doubleQuote],
                ShowHealth = false
            },
            new()
            {
                Name = "HTML",
                Extensions = [".html", ".htm", ".cshtml", ".vbhtml"],
                BlockComments = [htmlBlock],
                ShowHealth = false
            },
            new()
            {
                Name = "XML",
                Extensions = [".xml", ".xaml", ".vbproj", ".props", ".targets", ".config", ".resx"],
                BlockComments = [htmlBlock],
                ShowHealth = false
            },
            new()
            {
                Name = "CSS",
                Extensions = [".css"],
                BlockComments = [cStyleBlock],
                StringLiterals = [doubleQuote, singleQuote],
                ShowHealth = false
            },
            new()
            {
                Name = "SCSS/Less",
                Extensions = [".scss", ".less"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [doubleQuote, singleQuote],
                ShowHealth = false
            },
            new()
            {
                Name = "Dart",
                Extensions = [".dart"],
                LineCommentTokens = ["//"],
                BlockComments = [nestedCStyleBlock],
                StringLiterals = [rawTripleDouble, rawTripleSingle, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Scala",
                Extensions = [".scala", ".sc"],
                LineCommentTokens = ["//"],
                BlockComments = [nestedCStyleBlock],
                // Single quotes denote char literals and symbols (e.g. 'sym), so only
                // double and triple-double quotes are treated as string delimiters.
                StringLiterals = [rawTripleDouble, doubleQuote]
            },
            new()
            {
                Name = "R",
                Extensions = [".r"],
                LineCommentTokens = ["#"],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Lua",
                Extensions = [".lua"],
                LineCommentTokens = ["--"],
                BlockComments = [new BlockComment("--[[", "]]")],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Perl",
                Extensions = [".pl", ".pm"],
                LineCommentTokens = ["#"],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Elixir",
                Extensions = [".ex", ".exs"],
                LineCommentTokens = ["#"],
                StringLiterals = [rawTripleDouble, doubleQuote]
            },
            new()
            {
                Name = "Haskell",
                Extensions = [".hs"],
                LineCommentTokens = ["--"],
                BlockComments = [new BlockComment("{-", "-}", AllowNested: true)],
                StringLiterals = [doubleQuote]
            },
            new()
            {
                Name = "Objective-C",
                Extensions = [".m", ".mm"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "TOML",
                Extensions = [".toml"],
                LineCommentTokens = ["#"],
                StringLiterals = [rawTripleDouble, rawTripleSingle, doubleQuote, singleQuote],
                ShowHealth = false
            },
            new()
            {
                Name = "Markdown",
                Extensions = [".md", ".markdown"],
                BlockComments = [htmlBlock],
                ShowHealth = false
            },
            new()
            {
                Name = "Terraform",
                Extensions = [".tf", ".tfvars"],
                LineCommentTokens = ["#", "//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [doubleQuote]
            },
            new()
            {
                Name = "Makefile",
                Extensions = [".mk", ".mak"],
                Filenames = ["Makefile", "makefile", "GNUmakefile"],
                LineCommentTokens = ["#"],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Dockerfile",
                Extensions = [".dockerfile"],
                Filenames = ["Dockerfile", "Containerfile"],
                LineCommentTokens = ["#"],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "CMake",
                Extensions = [".cmake"],
                Filenames = ["CMakeLists.txt"],
                LineCommentTokens = ["#"],
                StringLiterals = [doubleQuote]
            },
            new()
            {
                Name = "Groovy",
                Extensions = [".groovy", ".gradle"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [rawTripleDouble, rawTripleSingle, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Julia",
                Extensions = [".jl"],
                LineCommentTokens = ["#"],
                BlockComments = [new BlockComment("#=", "=#", AllowNested: true)],
                StringLiterals = [rawTripleDouble, doubleQuote]
            },
            new()
            {
                Name = "Clojure",
                Extensions = [".clj", ".cljs", ".cljc", ".edn"],
                LineCommentTokens = [";"],
                StringLiterals = [doubleQuote]
            },
            new()
            {
                Name = "Vue",
                Extensions = [".vue"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock, htmlBlock],
                StringLiterals = [backtickTemplate, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Svelte",
                Extensions = [".svelte"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock, htmlBlock],
                StringLiterals = [backtickTemplate, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Protobuf",
                Extensions = [".proto"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Batch",
                Extensions = [".bat", ".cmd"],
                // REM is a line comment (case-insensitive, whole-word); :: is the idiomatic label-comment trick.
                LineCommentTokens = ["REM", "::"],
                CaseInsensitiveLineComments = true,
                StringLiterals = [doubleQuote]
            },
            new()
            {
                Name = "Assembly",
                Extensions = [".asm", ".s"],
                LineCommentTokens = [";"],
                BlockComments = [cStyleBlock],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "INI",
                Extensions = [".ini", ".editorconfig"],
                LineCommentTokens = [";", "#"],
                StringLiterals = [doubleQuote]
            },
            new()
            {
                Name = "Jupyter Notebook",
                Extensions = [".ipynb"],
                ShowHealth = false
            },
            new()
            {
                Name = "BASIC",
                Extensions = [".bas"],
                // REM is a line comment (case-insensitive, whole-word); ' is the modern shorthand (QBasic/VB6).
                LineCommentTokens = ["REM", "'"],
                CaseInsensitiveLineComments = true,
                StringLiterals = [doubleQuote]
            },
            new()
            {
                Name = "Pascal",
                Extensions = [".pas", ".pp", ".inc"],
                LineCommentTokens = ["//"],
                BlockComments = [new BlockComment("{", "}"), new BlockComment("(*", "*)")],
                StringLiterals = [singleQuote]
            },
            new()
            {
                Name = "Zig",
                Extensions = [".zig"],
                LineCommentTokens = ["//"],
                StringLiterals = [doubleQuote]
            },
            new()
            {
                Name = "Nim",
                Extensions = [".nim", ".nims"],
                LineCommentTokens = ["#"],
                BlockComments = [new BlockComment("#[", "]#", AllowNested: true)],
                StringLiterals = [rawTripleDouble, doubleQuote]
            },
            new()
            {
                Name = "OCaml",
                Extensions = [".ml", ".mli"],
                BlockComments = [new BlockComment("(*", "*)", AllowNested: true)],
                StringLiterals = [doubleQuote]
            },
            new()
            {
                Name = "Erlang",
                Extensions = [".erl", ".hrl"],
                LineCommentTokens = ["%"],
                StringLiterals = [doubleQuote]
            },
            new()
            {
                Name = "Elm",
                Extensions = [".elm"],
                LineCommentTokens = ["--"],
                BlockComments = [new BlockComment("{-", "-}", AllowNested: true)],
                StringLiterals = [rawTripleDouble, doubleQuote]
            },
            new()
            {
                Name = "Solidity",
                Extensions = [".sol"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "GraphQL",
                Extensions = [".graphql", ".gql"],
                LineCommentTokens = ["#"],
                StringLiterals = [rawTripleDouble, doubleQuote]
            },
            new()
            {
                Name = "Bazel/Starlark",
                Extensions = [".bzl"],
                Filenames = ["BUILD", "BUILD.bazel", "WORKSPACE", "WORKSPACE.bazel"],
                LineCommentTokens = ["#"],
                StringLiterals = [rawTripleDouble, rawTripleSingle, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Nix",
                Extensions = [".nix"],
                LineCommentTokens = ["#"],
                BlockComments = [cStyleBlock],
                // Nix's indented strings ('' … '') are not modeled; only double-quoted
                // strings are tracked here.
                StringLiterals = [doubleQuote]
            },
            new()
            {
                Name = "Astro",
                Extensions = [".astro"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock, htmlBlock],
                StringLiterals = [backtickTemplate, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "MDX",
                Extensions = [".mdx"],
                BlockComments = [htmlBlock],
                ShowHealth = false
            },
            new()
            {
                Name = "D",
                Extensions = [".d"],
                LineCommentTokens = ["//"],
                BlockComments = [nestedCStyleBlock, new BlockComment("/+", "+/", AllowNested: true)],
                StringLiterals = [backtickRaw, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Crystal",
                Extensions = [".cr"],
                LineCommentTokens = ["#"],
                StringLiterals = [doubleQuote, singleQuote]
            },
            new()
            {
                Name = "MSBuild script",
                Extensions = [".csproj", ".wdproj", ".vcproj", ".wixproj", ".btproj", ".msbuild"],
                BlockComments = [htmlBlock],
                ShowHealth = false
            },
            new()
            {
                Name = "C# Designer",
                FilenameSuffixes = [".designer.cs"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
                StringLiterals = [csVerbatimString, doubleQuote, singleQuote]
            },
            new()
            {
                Name = "Text",
                Extensions = [".txt", ".text"],
                ShowHealth = false
            }
        };
    }

    private static IReadOnlyDictionary<string, LanguageDefinition> CreateLookup(IReadOnlyList<LanguageDefinition> languages)
    {
        var lookup = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in languages)
        {
            foreach (var extension in language.Extensions)
            {
                lookup[extension] = language;
            }
        }

        return lookup;
    }

    private static IReadOnlyDictionary<string, LanguageDefinition> CreateFilenameLookup(IReadOnlyList<LanguageDefinition> languages)
    {
        var lookup = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in languages)
        {
            foreach (var fileName in language.Filenames)
            {
                lookup[fileName] = language;
            }
        }

        return lookup;
    }

    private static IReadOnlyList<(string Suffix, LanguageDefinition Language)> CreateSuffixLookup(IReadOnlyList<LanguageDefinition> languages)
    {
        var entries = new List<(string Suffix, LanguageDefinition Language)>();
        foreach (var language in languages)
        {
            foreach (var suffix in language.FilenameSuffixes)
            {
                entries.Add((suffix, language));
            }
        }

        // Longest suffix first, so the most specific match wins when suffixes overlap.
        entries.Sort((a, b) => b.Suffix.Length.CompareTo(a.Suffix.Length));
        return entries;
    }
}