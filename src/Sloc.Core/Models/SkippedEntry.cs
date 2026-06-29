namespace Sloc.Core.Models;

/// <summary>
/// A file or directory that was skipped during scanning or analysis, together
/// with the reason it could not be read.
/// </summary>
/// <param name="Path">The path that could not be read.</param>
/// <param name="Reason">A short description of why it was skipped.</param>
public sealed record SkippedEntry(string Path, string Reason);
