using Spectre.Console;

namespace Sloc.Cli.Output;

/// <summary>
/// Central place for the console color scheme, so the table, by-file, diff, and skipped-file
/// renderers stay visually consistent. Everything here only produces Spectre markup or
/// colors; whether color is actually emitted is decided by the console's capabilities.
/// </summary>
internal static class Theme
{
    /// <summary>Border color for every table.</summary>
    public static readonly Color Border = Color.Grey;

    /// <summary>Markup style for the Comment column (comments are the "healthy" content).</summary>
    public const string CommentStyle = "green";

    /// <summary>Markup style for the Blank column (de-emphasized).</summary>
    public const string BlankStyle = "grey";

    /// <summary>Markup style for percentages shown next to counts.</summary>
    public const string PercentStyle = "grey";

    /// <summary>Markup style for folder rows in the by-file tree.</summary>
    public const string FolderStyle = "bold yellow";

    /// <summary>Markup style for tree connectors (<c>├──</c>, <c>│</c>).</summary>
    public const string TreeStyle = "grey";

    /// <summary>
    /// The conventional color of each well-known language, as used by GitHub's language bar
    /// (linguist). Very dark shades (C, Lua, PowerShell, Python, Ruby, ...) are lightened and very light ones
    /// (JavaScript, Pascal, INI, ...) are darkened so they stay readable on both dark and light terminals. Languages missing here fall back to
    /// <see cref="FallbackPalette"/>, so a new language needs no entry.
    /// </summary>
    private static readonly Dictionary<string, Color> KnownLanguageColors = new(StringComparer.OrdinalIgnoreCase)
    {
        // Most widely used.
        ["JavaScript"] = Hex(0xC9AE1C),
        ["TypeScript"] = Hex(0x3178C6),
        ["Python"] = Hex(0x4B8BBE),
        ["Java"] = Hex(0xB07219),
        ["C#"] = Hex(0x3FAE2A),
        ["C++"] = Hex(0xF34B7D),
        ["C"] = Hex(0x8A8A8A),
        ["PHP"] = Hex(0x777BB4),
        ["Go"] = Hex(0x00ADD8),
        ["Rust"] = Hex(0xDEA584),
        ["Ruby"] = Hex(0xCC342D),
        ["Swift"] = Hex(0xF05138),
        ["Kotlin"] = Hex(0xA97BFF),
        ["HTML"] = Hex(0xE34C26),
        ["CSS"] = Hex(0x8D63C9),
        ["SQL"] = Hex(0xE38C00),
        ["Shell"] = Hex(0x5DB83A),
        ["PowerShell"] = Hex(0x5391FE),

        // Other general-purpose languages.
        ["Dart"] = Hex(0x00B4AB),
        ["Scala"] = Hex(0xC22D40),
        ["R"] = Hex(0x198CE7),
        ["Lua"] = Hex(0x5B7FDB),
        ["Perl"] = Hex(0x0298C3),
        ["Objective-C"] = Hex(0x438EFF),
        ["Groovy"] = Hex(0x4298B8),
        ["Julia"] = Hex(0xA270BA),
        ["F#"] = Hex(0xB845FC),
        ["Visual Basic"] = Hex(0x945DB7),
        ["Elixir"] = Hex(0x9A6FB0),
        ["Haskell"] = Hex(0x8F7FC4),
        ["Clojure"] = Hex(0xDB5855),
        ["Erlang"] = Hex(0xB83998),
        ["OCaml"] = Hex(0xEF7A08),
        ["Elm"] = Hex(0x60B5CC),
        ["Zig"] = Hex(0xEC915C),
        ["Nim"] = Hex(0xD9A800),
        ["D"] = Hex(0xBA595E),
        ["Crystal"] = Hex(0xB0B0B0),
        ["Pascal"] = Hex(0xA8B820),
        ["Assembly"] = Hex(0xC08A3E),
        ["BASIC"] = Hex(0xFF8C69),
        ["Fortran"] = Hex(0x7B6FD6),
        ["COBOL"] = Hex(0x4682B4),
        ["Solidity"] = Hex(0xAA6746),
        ["Scheme"] = Hex(0x1E4AEC),
        ["Racket"] = Hex(0x3C5CAA),
        ["Common Lisp"] = Hex(0x3FB68B),
        ["Emacs Lisp"] = Hex(0xC065DB),
        ["Verilog"] = Hex(0x8C93E8),
        ["SystemVerilog"] = Hex(0xA3B06A),
        ["VHDL"] = Hex(0x8F95B8),
        ["TeX"] = Hex(0x5FA83C),
        ["Nix"] = Hex(0x7E7EFF),
        ["Jupyter Notebook"] = Hex(0xDA5B0B),
        ["Batch"] = Hex(0x8FBF1F),

        // Web frameworks and templating.
        ["Vue"] = Hex(0x41B883),
        ["Svelte"] = Hex(0xFF3E00),
        ["Astro"] = Hex(0xFF5A03),
        ["Razor"] = Hex(0x7A5AD8),
        ["XAML"] = Hex(0x0C54C2),
        ["C# Designer"] = Hex(0x3FAE2A),
        ["SCSS/Less"] = Hex(0xC6538C),
        ["MDX"] = Hex(0xE09A1C),

        // Markup, data and build/config formats.
        ["JSON"] = Hex(0xB5B52E),
        ["YAML"] = Hex(0xCB171E),
        ["XML"] = Hex(0x0060AC),
        ["TOML"] = Hex(0x9C4221),
        ["INI"] = Hex(0x8FA3AD),
        ["Markdown"] = Hex(0x4F8FF7),
        ["GraphQL"] = Hex(0xE10098),
        ["Protobuf"] = Hex(0x6AB0DE),
        ["Terraform"] = Hex(0x7B42BB),
        ["Dockerfile"] = Hex(0x2496ED),
        ["Makefile"] = Hex(0x8AB84A),
        ["CMake"] = Hex(0xDA3434),
        ["Bazel/Starlark"] = Hex(0x76D275),
        ["MSBuild script"] = Hex(0x9B4F96),
        ["Text"] = Hex(0xB0B0B0)
    };

    /// <summary>Colors for languages that have no entry in <see cref="KnownLanguageColors"/>.</summary>
    private static readonly Color[] FallbackPalette =
    [
        Hex(0xE5C07B),
        Hex(0x56B6C2),
        Hex(0xC678DD),
        Hex(0x98C379),
        Hex(0xE06C75),
        Hex(0x61AFEF),
        Hex(0xD19A66),
        Hex(0xBE5046)
    ];

    /// <summary>Wraps a column header in the header style.</summary>
    /// <param name="text">The plain header text.</param>
    /// <returns>Markup for the header.</returns>
    public static string Header(string text) => $"[bold cyan]{Markup.Escape(text)}[/]";

    /// <summary>
    /// Returns the color for a language: its conventional color when it has one, otherwise a
    /// palette color picked by a stable hash of the name (not <see cref="string.GetHashCode()"/>,
    /// which differs between runs). Either way a language keeps its color across runs, sorts,
    /// and filters.
    /// </summary>
    /// <param name="language">The language display name.</param>
    /// <returns>The color assigned to the language.</returns>
    public static Color LanguageColor(string language)
    {
        ArgumentNullException.ThrowIfNull(language);

        if (KnownLanguageColors.TryGetValue(language, out var known))
        {
            return known;
        }

        // FNV-1a, 32-bit.
        var hash = 2166136261u;
        foreach (var ch in language)
        {
            hash = (hash ^ ch) * 16777619u;
        }

        return FallbackPalette[(int)(hash % (uint)FallbackPalette.Length)];
    }

    /// <summary>Wraps an escaped language name in its palette color.</summary>
    /// <param name="language">The language display name (unescaped).</param>
    /// <returns>Markup for the colored language name.</returns>
    public static string Language(string language) =>
        $"[{LanguageColor(language).ToMarkup()}]{Markup.Escape(language)}[/]";

    private static Color Hex(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
