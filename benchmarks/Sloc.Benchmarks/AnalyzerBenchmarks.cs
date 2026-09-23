using BenchmarkDotNet.Attributes;
using Sloc.Core;
using Sloc.Core.Languages;

namespace Sloc.Benchmarks;

/// <summary>
/// Pure CPU cost of line classification (plus complexity counting) over in-memory text,
/// with no file I/O involved.
/// </summary>
[MemoryDiagnoser]
public class ClassifierBenchmarks
{
    private readonly FileAnalyzer _analyzer = new();
    private LanguageDefinition _language = null!;
    private string _content = null!;

    [Params(1 << 20)]
    public int Bytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        LanguageRegistry.TryGetByExtension(".cs", out var language);
        _language = language!;
        _content = SourceCorpus.CSharp(Bytes);
    }

    [Benchmark]
    public int AnalyzeText() => _analyzer.AnalyzeText(_content, _language).Code;
}

/// <summary>
/// End-to-end cost of <see cref="FileAnalyzer.Analyze"/> over a directory of files on
/// disk (binary/encoding detection, decoding, classification, and optional hashing).
/// </summary>
[MemoryDiagnoser]
public class FileAnalyzerBenchmarks
{
    private const int FileCount = 200;

    private readonly FileAnalyzer _analyzer = new();
    private LanguageDefinition _language = null!;
    private string _directory = null!;
    private string[] _paths = null!;

    [Params(4 * 1024, 64 * 1024, 1024 * 1024)]
    public int FileBytes { get; set; }

    [Params(false, true)]
    public bool ComputeHash { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        LanguageRegistry.TryGetByExtension(".cs", out var language);
        _language = language!;

        _directory = Path.Combine(Path.GetTempPath(), $"sloc-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);

        // Fewer files for the large size so each iteration stays in a sensible time range.
        var count = FileBytes >= 1024 * 1024 ? FileCount / 10 : FileCount;
        var content = SourceCorpus.CSharp(FileBytes);
        _paths = new string[count];
        for (var i = 0; i < count; i++)
        {
            _paths[i] = Path.Combine(_directory, $"File{i}.cs");
            File.WriteAllText(_paths[i], content);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Benchmark]
    public int Analyze()
    {
        var code = 0;
        foreach (var path in _paths)
        {
            code += _analyzer.Analyze(path, _language, ComputeHash).Code;
        }

        return code;
    }
}
