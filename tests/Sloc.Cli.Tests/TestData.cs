using Sloc.Core.Models;

namespace Sloc.Cli.Tests;

/// <summary>
/// Provides analysis results shared by several renderer tests.
/// </summary>
internal static class TestData
{
    /// <summary>
    /// Creates one C# file and one Python file, so a <c>top</c> limit of 1 leaves a language out.
    /// </summary>
    /// <returns>The two analyzed files.</returns>
    public static FileAnalysis[] TwoLanguageFiles() =>
    [
        new() { Path = "a.cs", Language = "C#", Code = 3, Comment = 0, Blank = 0 },
        new() { Path = "b.py", Language = "Python", Code = 2, Comment = 0, Blank = 0 }
    ];
}
