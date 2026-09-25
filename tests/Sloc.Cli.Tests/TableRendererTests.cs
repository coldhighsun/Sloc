using Sloc.Cli.Output;
using Sloc.Core.Models;
using Spectre.Console.Testing;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains unit tests for <see cref="TableRenderer"/>, rendering to an in-memory
/// <see cref="TestConsole"/> so ANSI output can be asserted without a real terminal.
/// </summary>
public class TableRendererTests
{
    /// <summary>
    /// Verifies that the language table includes the language name, counts, the health
    /// column, and its computed label.
    /// </summary>
    [Fact]
    public void BuildLanguageTable_WithHealth_RendersLanguageRowAndHealthColumn()
    {
        var summary = BuildSummary();
        var console = new TestConsole();
        console.Profile.Width = 120;

        console.Write(new TableRenderer().BuildLanguageTable(summary, noHealth: false, noComplexity: true));
        var output = console.Output;

        Assert.Contains("C#", output);
        Assert.Contains("Comment Health", output);
        Assert.Contains("Good", output);
    }

    /// <summary>
    /// Verifies that <c>noHealth</c> drops the Comment Health column entirely.
    /// </summary>
    [Fact]
    public void BuildLanguageTable_NoHealth_OmitsHealthColumn()
    {
        var summary = BuildSummary();
        var console = new TestConsole();
        console.Profile.Width = 120;

        console.Write(new TableRenderer().BuildLanguageTable(summary, noHealth: true, noComplexity: true));
        var output = console.Output;

        Assert.Contains("C#", output);
        Assert.DoesNotContain("Comment Health", output);
    }

    /// <summary>
    /// Verifies that the Complexity column is present by default with the per-language total.
    /// </summary>
    [Fact]
    public void BuildLanguageTable_WithComplexity_RendersComplexityColumn()
    {
        var file = new FileAnalysis { Path = "a.cs", Language = "C#", Code = 80, Comment = 20, Blank = 5, Complexity = 4 };
        var summary = new AnalysisSummary([file]);
        var console = new TestConsole();
        console.Profile.Width = 120;

        console.Write(new TableRenderer().BuildLanguageTable(summary, noHealth: true, noComplexity: false));
        var output = console.Output;

        Assert.Contains("Complexity", output);
        Assert.Contains("4", output);
    }

    /// <summary>
    /// Verifies that <c>noComplexity</c> drops the Complexity column entirely.
    /// </summary>
    [Fact]
    public void BuildLanguageTable_NoComplexity_OmitsComplexityColumn()
    {
        var summary = BuildSummary();
        var console = new TestConsole();
        console.Profile.Width = 120;

        console.Write(new TableRenderer().BuildLanguageTable(summary, noHealth: true, noComplexity: true));
        var output = console.Output;

        Assert.DoesNotContain("Complexity", output);
    }

    /// <summary>
    /// Verifies that folders differing only by case (possible in a <c>--git-hash</c> tree
    /// from a case-sensitive filesystem) are shown as separate folders, not merged.
    /// </summary>
    [Fact]
    public void BuildFileTable_CaseVariantFolders_KeepsThemSeparate()
    {
        var summary = new AnalysisSummary(
        [
            new FileAnalysis { Path = Path.Combine("Src", "upper.js"), Language = "JavaScript", Code = 1, Comment = 0, Blank = 0 },
            new FileAnalysis { Path = Path.Combine("src", "lower.js"), Language = "JavaScript", Code = 1, Comment = 0, Blank = 0 }
        ]);
        var console = new TestConsole();
        console.Profile.Width = 160;

        console.Write(new TableRenderer().BuildFileTable(summary, noHealth: true, noComplexity: true));
        var output = console.Output;

        Assert.Contains("📁 Src", output);
        Assert.Contains("📁 src", output);
    }

    /// <summary>
    /// Verifies that a drive root folder (files on a different drive than the current
    /// directory, so their path can't be made relative) is labeled with the drive, not blank.
    /// </summary>
    [Fact]
    public void BuildFileTable_FileOnOtherDrive_LabelsDriveRootFolder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive letters exist only on Windows.");
        var otherDrive = char.ToUpperInvariant(Environment.CurrentDirectory[0]) == 'Q' ? 'R' : 'Q';
        var file = new FileAnalysis { Path = $@"{otherDrive}:\proj\a.cs", Language = "C#", Code = 1, Comment = 0, Blank = 0 };
        var console = new TestConsole();
        console.Profile.Width = 160;

        console.Write(new TableRenderer().BuildFileTable(new AnalysisSummary([file]), noHealth: true, noComplexity: true));
        var output = console.Output;

        Assert.Contains($"📁 {otherDrive}:", output);
        Assert.Contains("📁 proj", output);
    }

    private static AnalysisSummary BuildSummary()
    {
        var file = new FileAnalysis
        {
            Path = "a.cs",
            Language = "C#",
            Code = 80,
            Comment = 20,
            Blank = 5
        };
        return new AnalysisSummary([file]);
    }
}
