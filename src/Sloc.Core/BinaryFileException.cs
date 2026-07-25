namespace Sloc.Core;

/// <summary>
/// Thrown when a file appears to be binary (contains NUL bytes) and therefore should be
/// skipped rather than counted as source text.
/// </summary>
public sealed class BinaryFileException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BinaryFileException"/> class.
    /// </summary>
    public BinaryFileException()
        : base("binary file")
    {
    }
}
