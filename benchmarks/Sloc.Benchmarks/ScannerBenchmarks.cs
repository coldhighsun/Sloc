using BenchmarkDotNet.Attributes;
using Sloc.Core.Scanning;

namespace Sloc.Benchmarks;

/// <summary>
/// Cost of <see cref="DirectoryScanner.Scan"/> over a synthetic tree of many small files,
/// i.e. the discovery phase (tree walk, glob matching, ignore rules, language resolution)
/// with no file content analysis.
/// </summary>
[MemoryDiagnoser]
public class ScannerBenchmarks
{
    /// <summary>
    /// Number of top-level directories in the synthetic tree.
    /// </summary>
    private const int TopLevelDirectories = 20;

    /// <summary>
    /// Number of nested directories under each top-level directory.
    /// </summary>
    private const int SubDirectories = 10;

    /// <summary>
    /// Number of files in each leaf directory.
    /// </summary>
    private const int FilesPerDirectory = 25;

    private readonly DirectoryScanner _scanner = new();
    private string _root = null!;

    /// <summary>
    /// Gets or sets the scan configuration being measured.
    /// </summary>
    [Params(ScanMode.Default, ScanMode.Gitignore, ScanMode.ExcludeGlob)]
    public ScanMode Mode { get; set; }

    /// <summary>
    /// Builds the synthetic tree (5,000 files across 220 directories, with a
    /// <c>.gitignore</c> in every top-level directory).
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sloc-scan-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        for (var t = 0; t < TopLevelDirectories; t++)
        {
            var top = Path.Combine(_root, $"proj{t}");
            Directory.CreateDirectory(top);
            File.WriteAllText(Path.Combine(top, ".gitignore"), "*.log\nbuild/\n");

            for (var s = 0; s < SubDirectories; s++)
            {
                var directory = Path.Combine(top, "src", $"mod{s}");
                Directory.CreateDirectory(directory);
                for (var f = 0; f < FilesPerDirectory; f++)
                {
                    var extension = f % 5 == 0 ? ".log" : ".cs";
                    File.WriteAllText(Path.Combine(directory, $"File{f}{extension}"), "// x\n");
                }
            }
        }
    }

    /// <summary>
    /// Deletes the synthetic tree.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Scans the tree with the configured options.
    /// </summary>
    /// <returns>The number of files found.</returns>
    [Benchmark]
    public int Scan()
    {
        var options = Mode switch
        {
            ScanMode.Gitignore => new ScanOptions { RespectGitignore = true },
            ScanMode.ExcludeGlob => new ScanOptions { Excludes = ["**/*.log"] },
            _ => new ScanOptions()
        };

        return _scanner.Scan(_root, options).Files.Count;
    }

    /// <summary>
    /// The scan configurations compared by <see cref="ScannerBenchmarks"/>.
    /// </summary>
    public enum ScanMode
    {
        /// <summary>
        /// Default options: no globs, no ignore rules (the glob-free fast path).
        /// </summary>
        Default,

        /// <summary>
        /// Honors <c>.gitignore</c> files.
        /// </summary>
        Gitignore,

        /// <summary>
        /// A single exclude glob, so the <c>Matcher</c> is still used.
        /// </summary>
        ExcludeGlob
    }
}
