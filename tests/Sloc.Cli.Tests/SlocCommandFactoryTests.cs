using Sloc.Cli.Analysis;
using Sloc.Cli.Parsing;

namespace Sloc.Cli.Tests;

/// <summary>
/// Contains tests for <see cref="SlocCommandFactory"/>, pinning how command lines map to
/// <see cref="AnalyzeOptions"/> without running an analysis.
/// </summary>
public sealed class SlocCommandFactoryTests
{
    /// <summary>
    /// Verifies that a repeatable option followed by a bare token leaves that token to the
    /// <c>path</c> argument instead of swallowing it as another value, so
    /// <c>sloc -e "*.md" src</c> analyzes <c>src</c> rather than the current directory.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="expectedPath">The path the run should analyze.</param>
    [Theory]
    [InlineData(new[] { "-e", "*.md", "src" }, "src")]
    [InlineData(new[] { "--exclude", "*.md", "src" }, "src")]
    [InlineData(new[] { "-i", "**/*.cs", "src" }, "src")]
    [InlineData(new[] { "--exclude-dir", "vendor", "./src" }, "./src")]
    [InlineData(new[] { "--include-lang", "C#", "src" }, "src")]
    [InlineData(new[] { "--exclude-lang", "Markdown", "src" }, "src")]
    public void Create_RepeatableOptionThenBareToken_TokenIsPath(string[] args, string expectedPath)
    {
        var (options, exitCode) = Run(args);

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.NotNull(options);
        Assert.Equal(expectedPath, options.Path);
    }

    /// <summary>
    /// Verifies that repeatable options take exactly one value per occurrence, so a second
    /// bare token after <c>-e</c> is the path and not a further exclude glob.
    /// </summary>
    [Fact]
    public void Create_ExcludeFollowedByTwoTokens_SecondTokenIsPath()
    {
        var (options, _) = Run(["-e", "a", "b"]);

        Assert.NotNull(options);
        Assert.Equal(["a"], options.Excludes);
        Assert.Equal("b", options.Path);
    }

    /// <summary>
    /// Verifies that repeating an option accumulates its values, and that
    /// <c>--exclude-dir</c> names are expanded to <c>**/name/**</c> globs after <c>--exclude</c>.
    /// </summary>
    [Fact]
    public void Create_RepeatedOptions_AccumulateValues()
    {
        var (options, _) = Run(["-e", "a", "-e", "b", "--exclude-dir", "vendor", "-i", "x", "-i", "y", "src"]);

        Assert.NotNull(options);
        Assert.Equal(["a", "b", "**/vendor/**"], options.Excludes);
        Assert.Equal(["x", "y"], options.Includes);
        Assert.Equal("src", options.Path);
    }

    /// <summary>
    /// Verifies that the path defaults to the current directory when omitted.
    /// </summary>
    [Fact]
    public void Create_NoPath_DefaultsToCurrentDirectory()
    {
        var (options, _) = Run(["-e", "*.md"]);

        Assert.NotNull(options);
        Assert.Equal(".", options.Path);
    }

    /// <summary>
    /// Verifies that a non-positive <c>--jobs</c> is accepted and passed through, since the
    /// handler treats it as "use the processor count".
    /// </summary>
    /// <param name="jobs">The <c>--jobs</c> argument text.</param>
    /// <param name="expected">The parsed job count.</param>
    [Theory]
    [InlineData("0", 0)]
    [InlineData("-1", -1)]
    [InlineData("4", 4)]
    public void Create_Jobs_AcceptsNonPositiveValues(string jobs, int expected)
    {
        var (options, exitCode) = Run(["--jobs", jobs]);

        Assert.Equal(ExitCode.Success, exitCode);
        Assert.NotNull(options);
        Assert.Equal(expected, options.Jobs);
    }

    /// <summary>
    /// Verifies that an out-of-range <c>--min-comment-pct</c> is rejected before the analysis
    /// runs, with the path-error exit code.
    /// </summary>
    [Fact]
    public void Create_MinCommentPctOutOfRange_ReturnsErrorWithoutExecuting()
    {
        var (options, exitCode) = Run(["--min-comment-pct", "101"]);

        Assert.Equal(ExitCode.Error, exitCode);
        Assert.Null(options);
    }

    /// <summary>
    /// Parses <paramref name="args"/> against a command whose action records the options it
    /// receives instead of analyzing anything.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    private static (AnalyzeOptions? Options, int ExitCode) Run(string[] args)
    {
        AnalyzeOptions? captured = null;
        var command = SlocCommandFactory.Create(options =>
        {
            captured = options;
            return ExitCode.Success;
        });
        using var error = new StringWriter();

        var exitCode = CommandInvoker.Invoke(command.Parse(args), error);

        return (captured, exitCode);
    }
}
