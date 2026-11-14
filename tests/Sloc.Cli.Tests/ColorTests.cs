using Sloc.Cli.Analysis;
using Sloc.Cli.Output;
using Sloc.Cli.Parsing;
using Sloc.Core.Models;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Sloc.Cli.Tests;

/// <summary>
/// Tests for the console color scheme (<see cref="Theme"/>), the <c>--no-color</c> switch,
/// and the places the renderers apply colors.
/// </summary>
public class ColorTests
{
    /// <summary>
    /// Verifies a language always maps to the same palette color.
    /// </summary>
    [Fact]
    public void LanguageColor_SameName_IsStable()
    {
        Assert.Equal(Theme.LanguageColor("C#"), Theme.LanguageColor("C#"));
    }

    /// <summary>
    /// Verifies the language markup escapes brackets so a name like <c>Foo [x]</c> cannot break rendering.
    /// </summary>
    [Fact]
    public void Language_NameWithBrackets_IsEscaped()
    {
        var console = new TestConsole();
        console.Profile.Width = 80;

        console.MarkupLine(Theme.Language("Foo [x]"));

        Assert.Contains("Foo [x]", console.Output);
    }

    /// <summary>
    /// Verifies colors are on by default and <c>--no-color</c> turns the option on.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoColorOption_IsParsed(bool passed)
    {
        AnalyzeOptions? captured = null;
        var command = SlocCommandFactory.Create(options =>
        {
            captured = options;
            return ExitCode.Success;
        });
        using var error = new StringWriter();

        var args = passed ? new[] { ".", "--no-color" } : new[] { "." };
        var exitCode = CommandInvoker.Invoke(command.Parse(args), error);

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.NotNull(captured);
        Assert.Equal(passed, captured.NoColor);
    }

    /// <summary>
    /// Verifies <c>--no-color</c> strips colors but keeps ANSI (needed to redraw in place), that
    /// disposing the scope restores the console, and that leaving the flag off changes nothing.
    /// </summary>
    [Fact]
    public void ApplyColorSetting_NoColor_DisablesColorButKeepsAnsi()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.ColorSystem = ColorSystem.TrueColor;
        console.Profile.Capabilities.Ansi = true;

        using (AnalyzeHandler.ApplyColorSetting(new AnalyzeOptions { Path = "." }, console))
        {
        }

        Assert.Equal(ColorSystem.TrueColor, console.Profile.Capabilities.ColorSystem);
        Assert.True(console.Profile.Capabilities.Ansi);

        using (AnalyzeHandler.ApplyColorSetting(new AnalyzeOptions { Path = ".", NoColor = true }, console))
        {
            Assert.Equal(ColorSystem.NoColors, console.Profile.Capabilities.ColorSystem);
            // Ansi must stay on: without it the spinner/progress/live table print every frame on a new line.
            Assert.True(console.Profile.Capabilities.Ansi);
        }

        // Disposing the scope restores what the console had before.
        Assert.Equal(ColorSystem.TrueColor, console.Profile.Capabilities.ColorSystem);
        Assert.True(console.Profile.Capabilities.Ansi);
    }

    /// <summary>
    /// Verifies the language table emits ANSI colors on a color-capable console and none otherwise.
    /// </summary>
    [Fact]
    public void BuildLanguageTable_ColorConsole_EmitsAnsi()
    {
        var summary = new AnalysisSummary([new FileAnalysis { Path = "a.cs", Language = "C#", Code = 80, Comment = 20, Blank = 5 }]);
        var table = new TableRenderer().BuildLanguageTable(summary, noHealth: false, noComplexity: true);

        var colored = ColorConsole(ColorSystem.Standard);
        colored.Write(table);
        Assert.Contains("\u001b[", colored.Output);

        var plain = new TestConsole();
        plain.Profile.Width = 120;
        plain.Write(table);
        Assert.DoesNotContain("\u001b[", plain.Output);
    }

    /// <summary>
    /// Verifies a Dense comment-health level is no longer shown in the error color (red).
    /// </summary>
    [Fact]
    public void BuildLanguageTable_DenseHealth_IsNotRed()
    {
        var summary = new AnalysisSummary([new FileAnalysis { Path = "a.cs", Language = "C#", Code = 30, Comment = 70, Blank = 0 }]);
        Assert.Equal(CommentHealthLevel.Dense, summary.ByLanguage[0].Health);

        var console = ColorConsole(ColorSystem.Standard);
        console.Write(new TableRenderer().BuildLanguageTable(summary, noHealth: false, noComplexity: true));

        Assert.Contains("Dense", console.Output);
        // Magenta, as a normal (35) or bright (95) ANSI color depending on the named color's mapping.
        Assert.Matches("\u001b\\[(35|95)m■ Dense", console.Output);
    }

    /// <summary>
    /// Verifies growth in blank lines is not rendered in the "good" green of the diff.
    /// </summary>
    [Fact]
    public void RenderTable_BlankGrowth_IsNotGreen()
    {
        var current = new AnalysisSummary([new FileAnalysis { Path = "a.cs", Language = "TypeScript", Code = 0, Comment = 0, Blank = 9 }]);
        // Code shrinks by as much as Blank grows, so no other delta column is positive (green); the
        // language is blue (not a green like C#) so its own name cannot add a green code either.
        var baseline = new AnalysisSummary([new FileAnalysis { Path = "a.cs", Language = "TypeScript", Code = 8, Comment = 0, Blank = 1 }]);
        var console = ColorConsole(ColorSystem.Standard);

        DiffRenderer.RenderTable(current, baseline, console);

        Assert.Contains("+8", console.Output);
        Assert.Contains("\u001b[", console.Output); // colored output is actually emitted
        Assert.DoesNotContain("\u001b[32m", console.Output);
    }

    /// <summary>
    /// Documents that text decorations (bold) are still emitted when ANSI is on and colors are off,
    /// which is the state <c>--no-color</c> leaves the console in (only the colors are dropped).
    /// </summary>
    [Fact]
    public void NoColorsWithAnsi_BoldMarkup_StillEmitsDecoration()
    {
        var console = ColorConsole(ColorSystem.NoColors);

        console.MarkupLine("[bold]x[/]");

        Assert.Contains("[1m", console.Output);
    }

    /// <summary>
    /// Verifies well-known languages get their assigned color, case-insensitively.
    /// </summary>
    [Fact]
    public void LanguageColor_KnownLanguage_UsesConventionalColor()
    {
        Assert.Equal(new Color(0xC9, 0xAE, 0x1C), Theme.LanguageColor("JavaScript"));
        Assert.Equal(Theme.LanguageColor("JavaScript"), Theme.LanguageColor("javascript"));
        Assert.NotEqual(Theme.LanguageColor("JavaScript"), Theme.LanguageColor("TypeScript"));
    }

    /// <summary>
    /// Verifies a language without a conventional color still gets a stable fallback color.
    /// </summary>
    [Fact]
    public void LanguageColor_UnknownLanguage_FallsBackToStableColor()
    {
        Assert.Equal(Theme.LanguageColor("Some Future Language"), Theme.LanguageColor("Some Future Language"));
    }

    /// <summary>
    /// Verifies the most common languages are all given distinct colors.
    /// </summary>
    [Fact]
    public void LanguageColor_CommonLanguages_AreDistinct()
    {
        string[] common = ["JavaScript", "TypeScript", "Python", "Java", "C#", "C++", "C", "PHP", "Go", "Rust", "Ruby", "Swift", "Kotlin", "HTML", "CSS", "SQL", "Shell"];

        Assert.Equal(common.Length, common.Select(Theme.LanguageColor).Distinct().Count());
    }

    /// <summary>
    /// Verifies a status spinner on a <c>--no-color</c> console still redraws in place instead of
    /// printing every frame on its own line (which made the output scroll).
    /// </summary>
    [Fact]
    public void NoColor_StatusSpinner_RedrawsInPlace()
    {
        var console = new TestConsole();
        console.Profile.Width = 60;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.Interactive = true;

        using (AnalyzeHandler.ApplyColorSetting(new AnalyzeOptions { Path = ".", NoColor = true }, console))
        {
            console.Status().Start("first", ctx =>
            {
                Thread.Sleep(250);
                ctx.Status("second");
                Thread.Sleep(250);
            });
        }

        // Without ANSI the fallback prints "first" and "second" as plain lines.
        Assert.DoesNotContain($"first{Environment.NewLine}second", console.Output);
        Assert.Contains("second", console.Output);
    }

    private static TestConsole ColorConsole(ColorSystem colorSystem)
    {
        var console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Profile.Capabilities.ColorSystem = colorSystem;
        return console;
    }
}
