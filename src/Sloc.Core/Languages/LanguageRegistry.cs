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
        var extension = Path.GetExtension(path);
        return TryGetByExtension(extension, out language);
    }

    private static IReadOnlyList<LanguageDefinition> CreateLanguages()
    {
        var cStyleBlock = new BlockComment("/*", "*/");
        var htmlBlock = new BlockComment("<!--", "-->");

        return new List<LanguageDefinition>
        {
            new()
            {
                Name = "C#",
                Extensions = [".cs", ".csx"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock]
            },
            new()
            {
                Name = "C/C++",
                Extensions = [".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".hxx", ".ipp"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock]
            },
            new()
            {
                Name = "Java",
                Extensions = [".java"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock]
            },
            new()
            {
                Name = "Kotlin",
                Extensions = [".kt", ".kts"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock]
            },
            new()
            {
                Name = "Swift",
                Extensions = [".swift"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock]
            },
            new()
            {
                Name = "JavaScript",
                Extensions = [".js", ".jsx", ".mjs", ".cjs"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock]
            },
            new()
            {
                Name = "TypeScript",
                Extensions = [".ts", ".tsx", ".mts", ".cts"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock]
            },
            new()
            {
                Name = "Python",
                Extensions = [".py", ".pyw"],
                LineCommentTokens = ["#"],
                BlockComments = [new BlockComment("\"\"\"", "\"\"\""), new BlockComment("'''", "'''")]
            },
            new()
            {
                Name = "Go",
                Extensions = [".go"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock]
            },
            new()
            {
                Name = "Rust",
                Extensions = [".rs"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock]
            },
            new()
            {
                Name = "PHP",
                Extensions = [".php"],
                LineCommentTokens = ["//", "#"],
                BlockComments = [cStyleBlock]
            },
            new()
            {
                Name = "Ruby",
                Extensions = [".rb"],
                LineCommentTokens = ["#"],
                BlockComments = [new BlockComment("=begin", "=end")]
            },
            new()
            {
                Name = "F#",
                Extensions = [".fs", ".fsi", ".fsx"],
                LineCommentTokens = ["//"],
                BlockComments = [new BlockComment("(*", "*)")]
            },
            new()
            {
                Name = "Visual Basic",
                Extensions = [".vb"],
                LineCommentTokens = ["'"]
            },
            new()
            {
                Name = "SQL",
                Extensions = [".sql"],
                LineCommentTokens = ["--"],
                BlockComments = [cStyleBlock],
                ShowHealth = false
            },
            new()
            {
                Name = "PowerShell",
                Extensions = [".ps1", ".psm1", ".psd1"],
                LineCommentTokens = ["#"],
                BlockComments = [new BlockComment("<#", "#>")],
                ShowHealth = false
            },
            new()
            {
                Name = "Shell",
                Extensions = [".sh", ".bash", ".zsh"],
                LineCommentTokens = ["#"]
            },
            new()
            {
                Name = "YAML",
                Extensions = [".yml", ".yaml"],
                LineCommentTokens = ["#"],
                ShowHealth = false
            },
            new()
            {
                Name = "JSON",
                Extensions = [".json", ".jsonc"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
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
                Extensions = [".xml", ".xaml", ".csproj", ".vbproj", ".props", ".targets", ".config", ".resx"],
                BlockComments = [htmlBlock],
                ShowHealth = false
            },
            new()
            {
                Name = "CSS",
                Extensions = [".css"],
                BlockComments = [cStyleBlock],
                ShowHealth = false
            },
            new()
            {
                Name = "SCSS/Less",
                Extensions = [".scss", ".less"],
                LineCommentTokens = ["//"],
                BlockComments = [cStyleBlock],
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
}