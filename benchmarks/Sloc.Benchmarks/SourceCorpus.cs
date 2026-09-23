using System.Text;

namespace Sloc.Benchmarks;

/// <summary>
/// Generates deterministic, realistic-looking source text for benchmarks: a mix of code,
/// line comments, block comments, doc comments, string literals containing comment
/// markers, and blank lines.
/// </summary>
internal static class SourceCorpus
{
    private static readonly string[] CSharpLines =
    [
        "using System.Collections.Generic;",
        "",
        "namespace Example.Generated;",
        "",
        "/// <summary>",
        "/// A generated type used to exercise the line classifier.",
        "/// </summary>",
        "public sealed class Widget",
        "{",
        "    private readonly Dictionary<string, int> _counts = new();",
        "",
        "    // Increments the counter for the given key.",
        "    public void Increment(string key)",
        "    {",
        "        if (key is null || key.Length == 0) { return; }",
        "        var url = \"https://example.com/path // not a comment\";",
        "        _counts[key] = _counts.TryGetValue(key, out var n) ? n + 1 : 1; // trailing comment",
        "    }",
        "",
        "    /* A block comment",
        "       spanning several lines",
        "       with /* nested-looking */ text */",
        "    public int Sum()",
        "    {",
        "        var total = 0;",
        "        foreach (var pair in _counts)",
        "        {",
        "            total += pair.Value; /* inline block */",
        "        }",
        "",
        "        var verbatim = @\"C:\\temp\\\"\"quoted\"\"\\file\";",
        "        return total > 0 && verbatim.Length > 0 ? total : -1;",
        "    }",
        "}",
        "",
    ];

    /// <summary>
    /// Builds C# source of approximately <paramref name="approximateBytes"/> bytes.
    /// </summary>
    public static string CSharp(int approximateBytes)
    {
        var builder = new StringBuilder(approximateBytes + 1024);
        var i = 0;
        while (builder.Length < approximateBytes)
        {
            builder.Append(CSharpLines[i % CSharpLines.Length]).Append('\n');
            i++;
        }

        return builder.ToString();
    }
}
