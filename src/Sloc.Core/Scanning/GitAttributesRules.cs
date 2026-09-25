using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace Sloc.Core.Scanning;

/// <summary>
/// Evaluates whether a path is marked vendored or generated according to a set of
/// <c>.gitattributes</c> files, recognizing the <c>linguist-vendored</c> and
/// <c>linguist-generated</c> attributes (GitHub Linguist's convention for third-party or
/// machine-generated code that should be excluded from language statistics), also honored
/// by tools such as <c>cloc</c> and <c>scc</c>.
/// </summary>
/// <remarks>
/// Follows git's attribute resolution: a deeper <c>.gitattributes</c> file takes precedence
/// over a shallower one and a later line over an earlier one, so the first matching line
/// found in that order that mentions an attribute decides it, whether it sets it,
/// unsets it (<c>-attr</c>), gives it a value, or resets it to unspecified (<c>!attr</c>).
/// Macro attributes (<c>[attr]name …</c>) are honored when defined in the top-level file
/// (the repository root's, or the scan root's outside a repository)
/// and expand wherever they are set. Quoted C-style patterns are unquoted, while lines with a
/// negative pattern (<c>!pattern</c>) or an invalid attribute name are ignored, as git does.
/// One deliberate deviation: a directory-only pattern (<c>vendor/</c>) also matches the
/// files beneath that directory, where git would require <c>vendor/**</c>.
/// </remarks>
public sealed class GitAttributesRules
{
    /// <summary>
    /// The attribute Linguist uses to mark generated code.
    /// </summary>
    private const string GeneratedAttribute = "linguist-generated";

    /// <summary>
    /// The attribute Linguist uses to mark third-party code.
    /// </summary>
    private const string VendoredAttribute = "linguist-vendored";

    /// <summary>
    /// The per-path results already computed, keyed by normalized relative path.
    /// </summary>
    private readonly Dictionary<string, bool> _cache = new();

    /// <summary>
    /// The attributes decided so far while evaluating one path, reused across calls.
    /// </summary>
    private readonly Dictionary<string, AttributeState> _decided = new(StringComparer.Ordinal);

    /// <summary>
    /// The compiled files, deepest (highest precedence) first.
    /// </summary>
    private readonly IReadOnlyList<CompiledFile> _files;

    /// <summary>
    /// The macro definitions in effect, by macro name, restricted to relevant attributes.
    /// </summary>
    private readonly IReadOnlyDictionary<string, IReadOnlyList<AttributeState>> _macros;

    /// <summary>
    /// The scan root's <c>/</c>-separated path relative to the root of the repository the
    /// file bases are relative to, empty when the scan root is that root.
    /// </summary>
    private readonly string _scanPrefix;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitAttributesRules"/> class.
    /// </summary>
    /// <param name="files">The compiled files, deepest first.</param>
    /// <param name="macros">The macro definitions in effect.</param>
    /// <param name="scanPrefix">The scan root's repository-relative directory.</param>
    private GitAttributesRules(
        IReadOnlyList<CompiledFile> files,
        IReadOnlyDictionary<string, IReadOnlyList<AttributeState>> macros,
        string scanPrefix)
    {
        _files = files;
        _macros = macros;
        _scanPrefix = scanPrefix;
    }

    /// <summary>
    /// Gets a value indicating whether any recognized patterns were loaded.
    /// </summary>
    public bool IsEmpty => _files.Count == 0;

    /// <summary>
    /// Parses the supplied attributes-file lines as a single <c>.gitattributes</c> rooted at
    /// <paramref name="baseDirectory"/> (relative to the scan root, <c>/</c>-separated).
    /// Intended for tests.
    /// </summary>
    /// <param name="baseDirectory">The base directory the patterns are relative to.</param>
    /// <param name="lines">The raw attributes-file lines.</param>
    /// <param name="ignoreCase">Whether patterns match case-insensitively (git's <c>core.ignoreCase</c>).</param>
    /// <returns>The loaded rule set.</returns>
    public static GitAttributesRules FromLines(string baseDirectory, IEnumerable<string> lines, bool ignoreCase = true)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(lines);

        return FromFiles([new AttributesFile(NormalizeBase(baseDirectory), ParseLines(lines))], ignoreCase);
    }

    /// <summary>
    /// Discovers and loads every <c>.gitattributes</c> file under <paramref name="root"/>,
    /// skipping the supplied excluded directory names, along with the ones in the directories
    /// above <paramref name="root"/> up to the root of the repository it belongs to.
    /// </summary>
    /// <param name="root">The scan root directory.</param>
    /// <param name="excludedDirectoryNames">
    /// Directory names not to descend into when searching for attributes files (e.g. <c>.git</c>).
    /// </param>
    /// <param name="recursive">
    /// Whether to search subdirectories for nested <c>.gitattributes</c> files. When
    /// <see langword="false"/>, only the <paramref name="root"/> directory itself is checked,
    /// matching the shallow file discovery performed by the scanner.
    /// </param>
    /// <param name="onDirectoryVisited">
    /// Invoked once for every directory visited while searching for <c>.gitattributes</c>
    /// files, with a running visited-directory count and the directory path.
    /// </param>
    /// <returns>The loaded rule set (possibly empty).</returns>
    public static GitAttributesRules Load(
        string root,
        IReadOnlySet<string> excludedDirectoryNames,
        bool recursive,
        Action<int, string>? onDirectoryVisited = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(excludedDirectoryNames);

        var ignoreCase = GitIgnoreRules.ResolveIgnoreCase(root);
        var walk = ScanTreeWalker.Walk(
            root, excludedDirectoryNames, recursive, followSymlinks: false, ignoreCase,
            collectGitignore: false, collectGitattributes: true, collectFiles: false, onDirectoryVisited);

        return FromWalk(root, walk.AttributesFiles, ignoreCase);
    }

    /// <summary>
    /// Determines whether the given path (relative to the scan root, using either
    /// separator) is marked <c>linguist-vendored</c> or <c>linguist-generated</c>.
    /// </summary>
    /// <param name="relativePath">The path relative to the scan root.</param>
    /// <returns><see langword="true"/> if the path should be excluded.</returns>
    public bool IsVendoredOrGenerated(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (_files.Count == 0)
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return false;
        }

        normalized = RelativePathResolver.Combine(_scanPrefix, normalized);
        if (_cache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        _decided.Clear();
        var result = false;
        foreach (var file in _files)
        {
            if (!RelativePathResolver.TryGetRelativePath(file.BaseDirectory, normalized, out var relativeToBase))
            {
                continue;
            }

            for (var i = file.Lines.Count - 1; i >= 0; i--)
            {
                var line = file.Lines[i];
                if (!line.Regex.IsMatch(relativeToBase))
                {
                    continue;
                }

                Fill(line.States);
                if (TryGetOutcome(out result))
                {
                    _cache[normalized] = result;
                    return result;
                }
            }
        }

        TryGetOutcome(out result);
        _cache[normalized] = result;
        return result;
    }

    /// <summary>
    /// Parses the lines of one attributes file, keeping the pattern lines and macro
    /// definitions that set at least one attribute and dropping comments, blank lines, and
    /// lines git ignores.
    /// </summary>
    /// <param name="lines">The raw attributes-file lines.</param>
    /// <returns>The parsed lines, in file order.</returns>
    internal static List<AttributeLine> ParseLines(IEnumerable<string> lines)
    {
        var parsed = new List<AttributeLine>();
        foreach (var line in lines)
        {
            if (AttributeLine.TryParse(line, out var attributeLine) && attributeLine.States.Count > 0)
            {
                parsed.Add(attributeLine);
            }
        }

        return parsed;
    }

    /// <summary>
    /// Builds a rule set from <c>.gitattributes</c> files already discovered by a
    /// <see cref="ScanTreeWalker"/> pass, without walking the directory tree again. Macros
    /// are taken from the top-level file only (the last definition wins), and only the lines
    /// that can affect <c>linguist-vendored</c>/<c>linguist-generated</c> (directly or through
    /// a macro) have their patterns compiled.
    /// </summary>
    /// <param name="files">The parsed attributes files, in any order.</param>
    /// <param name="ignoreCase">Whether patterns match case-insensitively (git's <c>core.ignoreCase</c>).</param>
    /// <param name="scanPrefix">
    /// The scan root's <c>/</c>-separated directory relative to the directory the file bases
    /// are relative to (the repository root), prepended to every path looked up.
    /// </param>
    internal static GitAttributesRules FromFiles(IReadOnlyList<AttributesFile> files, bool ignoreCase, string scanPrefix = "")
    {
        var macroDefinitions = new Dictionary<string, IReadOnlyList<AttributeState>>(StringComparer.Ordinal);
        foreach (var file in files.Where(static file => file.BaseDirectory.Length == 0))
        {
            foreach (var line in file.Lines.Where(static line => line.MacroName is not null))
            {
                macroDefinitions[line.MacroName!] = line.States;
            }
        }

        var relevant = RelevantAttributes(macroDefinitions);
        var macros = new Dictionary<string, IReadOnlyList<AttributeState>>(StringComparer.Ordinal);
        foreach (var (name, states) in macroDefinitions)
        {
            if (relevant.Contains(name))
            {
                macros[name] = states.Where(state => relevant.Contains(state.Name)).ToList();
            }
        }

        var compiled = new List<CompiledFile>();
        foreach (var file in files.OrderByDescending(static file => file.BaseDirectory.Length))
        {
            var lines = new List<CompiledLine>();
            foreach (var line in file.Lines)
            {
                if (line.Pattern is null)
                {
                    continue;
                }

                var states = line.States.Where(state => relevant.Contains(state.Name)).ToList();
                if (states.Count > 0 && TryCompilePattern(line.Pattern, ignoreCase, out var regex))
                {
                    lines.Add(new CompiledLine(regex, states));
                }
            }

            if (lines.Count > 0)
            {
                compiled.Add(new CompiledFile(file.BaseDirectory, lines));
            }
        }

        return new GitAttributesRules(compiled, macros, scanPrefix);
    }

    /// <summary>
    /// Builds a rule set from <c>.gitattributes</c> files already discovered under
    /// <paramref name="root"/> by a <see cref="ScanTreeWalker"/> pass, adding, when
    /// <paramref name="root"/> is below the root of its repository, the <c>.gitattributes</c>
    /// files in the directories between them, read from disk, as git applies them (so
    /// macros come from the repository's top-level file).
    /// </summary>
    /// <param name="root">The scan root directory.</param>
    /// <param name="walkedFiles">The attributes files found under <paramref name="root"/>, with scan-root-relative bases.</param>
    /// <param name="ignoreCase">Whether patterns match case-insensitively (git's <c>core.ignoreCase</c>).</param>
    internal static GitAttributesRules FromWalk(string root, IReadOnlyList<AttributesFile> walkedFiles, bool ignoreCase)
    {
        var workTree = GitWorkTree.Find(root);
        var scanPrefix = workTree?.RelativeDirectory(root) ?? string.Empty;
        var ancestorFiles = new List<AttributesFile>();
        if (workTree is not null)
        {
            foreach (var (directory, lines) in workTree.ReadAncestorFiles(scanPrefix, ".gitattributes"))
            {
                ancestorFiles.Add(new AttributesFile(directory, ParseLines(lines)));
            }
        }

        return FromWalk(walkedFiles, ignoreCase, scanPrefix, ancestorFiles);
    }

    /// <summary>
    /// Builds a rule set like <see cref="FromWalk(string, IReadOnlyList{AttributesFile}, bool)"/>,
    /// but with the scan root's position in its repository and the <c>.gitattributes</c>
    /// files above it supplied by the caller (e.g. taken from a git commit instead of the disk).
    /// </summary>
    /// <param name="walkedFiles">The attributes files found under the scan root, with scan-root-relative bases.</param>
    /// <param name="ignoreCase">Whether patterns match case-insensitively (git's <c>core.ignoreCase</c>).</param>
    /// <param name="scanPrefix">The scan root's <c>/</c>-separated repository-relative directory.</param>
    /// <param name="ancestorFiles">The attributes files above the scan root, with repository-relative bases.</param>
    internal static GitAttributesRules FromWalk(
        IReadOnlyList<AttributesFile> walkedFiles,
        bool ignoreCase,
        string scanPrefix,
        IReadOnlyList<AttributesFile> ancestorFiles)
    {
        var files = new List<AttributesFile>(ancestorFiles);
        files.AddRange(walkedFiles.Select(file =>
            new AttributesFile(RelativePathResolver.Combine(scanPrefix, file.BaseDirectory), file.Lines)));
        return FromFiles(files, ignoreCase, scanPrefix);
    }

    /// <summary>
    /// Normalizes a base directory to the <c>/</c>-separated form used for matching, with
    /// the scan root itself as the empty string.
    /// </summary>
    /// <param name="baseDirectory">The base directory relative to the scan root.</param>
    internal static string NormalizeBase(string baseDirectory)
    {
        var normalized = baseDirectory.Replace('\\', '/').Trim('/');
        return normalized == "." ? string.Empty : normalized;
    }

    /// <summary>
    /// Computes the attributes whose value can influence the result: the two Linguist
    /// attributes plus every macro whose expansion reaches one of them, transitively.
    /// </summary>
    /// <param name="macros">The macro definitions in effect.</param>
    private static HashSet<string> RelevantAttributes(IReadOnlyDictionary<string, IReadOnlyList<AttributeState>> macros)
    {
        var relevant = new HashSet<string>(StringComparer.Ordinal) { VendoredAttribute, GeneratedAttribute };
        bool added;
        do
        {
            added = false;
            foreach (var (name, states) in macros)
            {
                if (!relevant.Contains(name) && states.Any(state => relevant.Contains(state.Name)))
                {
                    relevant.Add(name);
                    added = true;
                }
            }
        }
        while (added);

        return relevant;
    }

    /// <summary>
    /// Compiles a pattern-column glob, widening a directory-only pattern to also match the
    /// paths beneath that directory.
    /// </summary>
    /// <param name="pattern">The (already unquoted) pattern.</param>
    /// <param name="ignoreCase">Whether the pattern matches case-insensitively.</param>
    /// <param name="regex">The compiled pattern.</param>
    /// <returns><see langword="false"/> for a pattern git never matches.</returns>
    private static bool TryCompilePattern(string pattern, bool ignoreCase, out Regex regex)
    {
        if (!GitIgnorePattern.TryCompilePattern(pattern, ignoreCase, out regex, out var directoryOnly))
        {
            return false;
        }

        if (directoryOnly)
        {
            // A directory-only pattern (trailing "/") applies to the directory itself and,
            // since IsVendoredOrGenerated checks each file's full path in one shot rather
            // than walking ancestor directories the way GitIgnoreRules does, must also match
            // every path nested under it: the compiled regex always ends in "$" anchoring to
            // the directory name exactly, so that anchor is loosened to allow "/<anything>".
            var source = regex.ToString();
            regex = new Regex(source[..^1] + "(?:$|/.*)", regex.Options);
        }

        return true;
    }

    /// <summary>
    /// Whether an attribute state marks a path for exclusion: set, or given any value other
    /// than <c>false</c> or <c>0</c>.
    /// </summary>
    /// <param name="state">The attribute's decided state.</param>
    private static bool IsTruthy(AttributeState state) => state.Kind switch
    {
        AttributeStateKind.True => true,
        AttributeStateKind.Value => !(state.Value!.Equals("false", StringComparison.OrdinalIgnoreCase) || state.Value == "0"),
        _ => false,
    };

    /// <summary>
    /// Applies a line's (or macro's) states right to left, as git does, deciding only the
    /// attributes not already decided by a higher-precedence line, and expanding each macro
    /// that becomes set.
    /// </summary>
    /// <param name="states">The states to apply.</param>
    private void Fill(IReadOnlyList<AttributeState> states)
    {
        for (var i = states.Count - 1; i >= 0; i--)
        {
            var state = states[i];
            if (_decided.TryAdd(state.Name, state)
                && state.Kind == AttributeStateKind.True
                && _macros.TryGetValue(state.Name, out var expansion))
            {
                Fill(expansion);
            }
        }
    }

    /// <summary>
    /// Reports the result once lower-precedence lines can no longer change it: either
    /// Linguist attribute is decided truthy, or both are decided.
    /// </summary>
    /// <param name="result">The result so far, final when the method returns <see langword="true"/>.</param>
    /// <returns>Whether <paramref name="result"/> is final.</returns>
    private bool TryGetOutcome(out bool result)
    {
        _decided.TryGetValue(VendoredAttribute, out var vendored);
        _decided.TryGetValue(GeneratedAttribute, out var generated);
        result = (vendored is not null && IsTruthy(vendored)) || (generated is not null && IsTruthy(generated));
        return result || (vendored is not null && generated is not null);
    }

    /// <summary>
    /// The parsed lines of one <c>.gitattributes</c> file.
    /// </summary>
    /// <param name="baseDirectory">The directory holding the file, relative to the scan root.</param>
    /// <param name="lines">The file's parsed lines, in file order.</param>
    internal sealed class AttributesFile(string baseDirectory, IReadOnlyList<AttributeLine> lines)
    {
        /// <summary>
        /// Gets the directory holding the file, relative to the scan root (empty for the root).
        /// </summary>
        public string BaseDirectory { get; } = baseDirectory;

        /// <summary>
        /// Gets the file's parsed lines, in file order.
        /// </summary>
        public IReadOnlyList<AttributeLine> Lines { get; } = lines;
    }

    /// <summary>
    /// A file's pattern lines that affect the result, with their patterns compiled.
    /// </summary>
    /// <param name="BaseDirectory">The directory holding the file, relative to the scan root.</param>
    /// <param name="Lines">The compiled lines, in file order.</param>
    private sealed record CompiledFile(string BaseDirectory, IReadOnlyList<CompiledLine> Lines);

    /// <summary>
    /// A pattern line whose pattern has been compiled, keeping only its relevant states.
    /// </summary>
    /// <param name="Regex">The compiled pattern.</param>
    /// <param name="States">The line's relevant states, in line order.</param>
    private sealed record CompiledLine(Regex Regex, IReadOnlyList<AttributeState> States);
}

/// <summary>
/// How a <c>.gitattributes</c> line assigns an attribute.
/// </summary>
internal enum AttributeStateKind
{
    /// <summary>
    /// Set (<c>attr</c>).
    /// </summary>
    True,

    /// <summary>
    /// Unset (<c>-attr</c>).
    /// </summary>
    False,

    /// <summary>
    /// Reset to unspecified (<c>!attr</c>), which still overrides lower-precedence lines.
    /// </summary>
    Unset,

    /// <summary>
    /// Set to a value (<c>attr=value</c>).
    /// </summary>
    Value,
}

/// <summary>
/// One attribute assignment on a <c>.gitattributes</c> line.
/// </summary>
/// <param name="Name">The attribute name.</param>
/// <param name="Kind">How the attribute is assigned.</param>
/// <param name="Value">The assigned value, for <see cref="AttributeStateKind.Value"/> only.</param>
internal sealed record AttributeState(string Name, AttributeStateKind Kind, string? Value);

/// <summary>
/// A single parsed <c>.gitattributes</c> line: either a pattern with the attributes it
/// assigns, or a macro definition (<c>[attr]name …</c>).
/// </summary>
internal sealed class AttributeLine
{
    /// <summary>
    /// The characters git treats as separators on an attributes line.
    /// </summary>
    private const string Blanks = " \t\r\n";

    /// <summary>
    /// The prefix that marks a macro definition.
    /// </summary>
    private const string MacroPrefix = "[attr]";

    /// <summary>
    /// Git's <c>ATTR_MAX_LINE_LENGTH</c>: lines this long or longer (in UTF-8 bytes) are ignored.
    /// </summary>
    private const int MaxLineLength = 2048;

    /// <summary>
    /// The <see cref="Blanks"/> characters, prepared for fast searching.
    /// </summary>
    private static readonly SearchValues<char> BlankValues = SearchValues.Create(Blanks);

    /// <summary>
    /// Initializes a new instance of the <see cref="AttributeLine"/> class.
    /// </summary>
    /// <param name="pattern">The pattern, or <see langword="null"/> for a macro definition.</param>
    /// <param name="macroName">The macro name, or <see langword="null"/> for a pattern line.</param>
    /// <param name="states">The attribute assignments, in line order.</param>
    private AttributeLine(string? pattern, string? macroName, IReadOnlyList<AttributeState> states)
    {
        Pattern = pattern;
        MacroName = macroName;
        States = states;
    }

    /// <summary>
    /// Gets the defined macro's name, or <see langword="null"/> for a pattern line.
    /// </summary>
    public string? MacroName
    {
        get;
    }

    /// <summary>
    /// Gets the (unquoted) pattern, or <see langword="null"/> for a macro definition.
    /// </summary>
    public string? Pattern
    {
        get;
    }

    /// <summary>
    /// Gets the attribute assignments, in line order.
    /// </summary>
    public IReadOnlyList<AttributeState> States
    {
        get;
    }

    /// <summary>
    /// Parses one attributes-file line the way git's <c>parse_attr_line</c> does.
    /// </summary>
    /// <param name="rawLine">The raw line.</param>
    /// <param name="line">The parsed line.</param>
    /// <returns>
    /// <see langword="false"/> for a comment, a blank line, or a line git ignores (too long,
    /// a negative pattern, or an invalid attribute name).
    /// </returns>
    public static bool TryParse(string rawLine, out AttributeLine line)
    {
        line = null!;

        var start = SkipBlanks(rawLine, 0);
        if (start == rawLine.Length || rawLine[start] == '#')
        {
            return false;
        }

        if (Encoding.UTF8.GetByteCount(rawLine) >= MaxLineLength)
        {
            return false;
        }

        string pattern;
        int statesStart;
        if (rawLine[start] == '"' && TryUnquoteCStyle(rawLine, start, out var unquoted, out var end))
        {
            pattern = unquoted;
            statesStart = end;
        }
        else
        {
            var tokenEnd = FindBlank(rawLine, start);
            pattern = rawLine[start..tokenEnd];
            statesStart = tokenEnd;
        }

        string? macroName = null;
        if (pattern.Length > MacroPrefix.Length && pattern.StartsWith(MacroPrefix, StringComparison.Ordinal))
        {
            var nameStart = SkipBlanks(pattern, MacroPrefix.Length);
            macroName = pattern[nameStart..FindBlank(pattern, nameStart)];
            if (!IsValidName(macroName))
            {
                return false;
            }
        }
        else if (pattern.StartsWith('!'))
        {
            // Git rejects negative patterns in attributes files ("\!" is a literal "!").
            return false;
        }

        var states = new List<AttributeState>();
        var position = SkipBlanks(rawLine, statesStart);
        while (position < rawLine.Length)
        {
            var tokenEnd = FindBlank(rawLine, position);
            if (!TryParseState(rawLine[position..tokenEnd], out var state))
            {
                // One invalid attribute name voids the whole line, as in git.
                return false;
            }

            states.Add(state);
            position = SkipBlanks(rawLine, tokenEnd);
        }

        line = new AttributeLine(macroName is null ? pattern : null, macroName, states);
        return true;
    }

    /// <summary>
    /// Decodes a C-style quoted string (git's <c>unquote_c_style</c>) starting at
    /// <paramref name="start"/>: the escapes <c>\a \b \f \n \r \t \v \\ \"</c> and three-digit
    /// octal bytes, with the resulting bytes decoded as UTF-8.
    /// </summary>
    /// <param name="text">The text holding the quoted string.</param>
    /// <param name="start">The index of the opening quote.</param>
    /// <param name="value">The decoded string.</param>
    /// <param name="end">The index just past the closing quote.</param>
    /// <returns><see langword="false"/> if the string is unterminated or has an invalid escape.</returns>
    internal static bool TryUnquoteCStyle(string text, int start, out string value, out int end)
    {
        value = string.Empty;
        end = start;
        if (start >= text.Length || text[start] != '"')
        {
            return false;
        }

        var bytes = new List<byte>();
        var i = start + 1;
        var runStart = i;
        while (true)
        {
            if (i >= text.Length)
            {
                return false;
            }

            var c = text[i];
            if (c != '"' && c != '\\')
            {
                i++;
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(text[runStart..i]));
            if (c == '"')
            {
                value = Encoding.UTF8.GetString([.. bytes]);
                end = i + 1;
                return true;
            }

            if (i + 1 >= text.Length)
            {
                return false;
            }

            var escape = text[i + 1];
            i += 2;
            byte decoded;
            switch (escape)
            {
                case 'a': decoded = 0x07; break;
                case 'b': decoded = 0x08; break;
                case 'f': decoded = 0x0C; break;
                case 'n': decoded = 0x0A; break;
                case 'r': decoded = 0x0D; break;
                case 't': decoded = 0x09; break;
                case 'v': decoded = 0x0B; break;
                case '\\' or '"': decoded = (byte)escape; break;
                case >= '0' and <= '3':
                    if (i + 1 >= text.Length || !IsOctalDigit(text[i]) || !IsOctalDigit(text[i + 1]))
                    {
                        return false;
                    }

                    decoded = (byte)(((escape - '0') << 6) | ((text[i] - '0') << 3) | (text[i + 1] - '0'));
                    i += 2;
                    break;
                default:
                    return false;
            }

            bytes.Add(decoded);
            runStart = i;
        }
    }

    /// <summary>
    /// Parses one attribute token (<c>attr</c>, <c>-attr</c>, <c>!attr</c>, or
    /// <c>attr=value</c>) the way git's <c>parse_attr</c> does.
    /// </summary>
    /// <param name="token">The token, with no blanks.</param>
    /// <param name="state">The parsed assignment.</param>
    /// <returns><see langword="false"/> if the attribute name is invalid or reserved.</returns>
    private static bool TryParseState(string token, out AttributeState state)
    {
        state = null!;

        var equals = token.IndexOf('=');
        var name = equals < 0 ? token : token[..equals];
        var kind = equals < 0 ? AttributeStateKind.True : AttributeStateKind.Value;
        if (name.Length > 0 && name[0] is '-' or '!')
        {
            kind = name[0] == '-' ? AttributeStateKind.False : AttributeStateKind.Unset;
            name = name[1..];
        }

        if (!IsValidName(name) || name.StartsWith("builtin_", StringComparison.Ordinal))
        {
            return false;
        }

        state = new AttributeState(name, kind, kind == AttributeStateKind.Value ? token[(equals + 1)..] : null);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a valid attribute name (git's
    /// <c>attr_name_valid</c>): ASCII letters, digits, <c>-</c>, <c>.</c>, and <c>_</c>, not
    /// starting with <c>-</c>.
    /// </summary>
    /// <param name="name">The candidate name.</param>
    private static bool IsValidName(string name) =>
        name.Length > 0
        && name[0] != '-'
        && name.All(static c => c is '-' or '.' or '_' || char.IsAsciiLetterOrDigit(c));

    /// <summary>
    /// Whether <paramref name="c"/> is an octal digit.
    /// </summary>
    /// <param name="c">The character to test.</param>
    private static bool IsOctalDigit(char c) => c is >= '0' and <= '7';

    /// <summary>
    /// Returns the index of the first non-blank character at or after <paramref name="start"/>.
    /// </summary>
    /// <param name="text">The text to scan.</param>
    /// <param name="start">The index to start at.</param>
    private static int SkipBlanks(string text, int start)
    {
        var index = text.AsSpan(start).IndexOfAnyExcept(BlankValues);
        return index < 0 ? text.Length : start + index;
    }

    /// <summary>
    /// Returns the index of the first blank character at or after <paramref name="start"/>,
    /// or the text's length if there is none.
    /// </summary>
    /// <param name="text">The text to scan.</param>
    /// <param name="start">The index to start at.</param>
    private static int FindBlank(string text, int start)
    {
        var index = text.AsSpan(start).IndexOfAny(BlankValues);
        return index < 0 ? text.Length : start + index;
    }
}
