using Sloc.Cli.Analysis;
using Sloc.Cli.Parsing;
using System.CommandLine;

namespace Sloc.Cli.Tests;

/// <summary>
/// Tests for <see cref="CommandInvoker"/>.
/// </summary>
public sealed class CommandInvokerTests
{
    /// <summary>
    /// Verifies that an exception escaping the command's action maps to
    /// <see cref="ExitCode.Unexpected"/> and is reported, instead of the generic <c>1</c>
    /// System.CommandLine's default handler returns.
    /// </summary>
    [Fact]
    public void Invoke_ActionThrows_ReturnsUnexpectedAndReportsError()
    {
        // Arrange
        var command = new RootCommand();
        command.SetAction(int (_) => throw new InvalidOperationException("boom"));
        using var error = new StringWriter();

        // Act
        var exitCode = CommandInvoker.Invoke(command.Parse([]), error);

        // Assert
        Assert.Equal(ExitCode.Unexpected, exitCode);
        Assert.Contains("sloc: unexpected error:", error.ToString());
        Assert.Contains("boom", error.ToString());
    }

    /// <summary>
    /// Verifies that the action's own exit code is returned unchanged.
    /// </summary>
    [Fact]
    public void Invoke_ActionReturnsCode_ReturnsThatCode()
    {
        // Arrange
        var command = new RootCommand();
        command.SetAction(_ => ExitCode.ThresholdNotMet);
        using var error = new StringWriter();

        // Act
        var exitCode = CommandInvoker.Invoke(command.Parse([]), error);

        // Assert
        Assert.Equal(ExitCode.ThresholdNotMet, exitCode);
        Assert.Empty(error.ToString());
    }
}
