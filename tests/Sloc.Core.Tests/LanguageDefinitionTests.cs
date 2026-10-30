using Sloc.Core.Languages;

namespace Sloc.Core.Tests;

/// <summary>
/// Contains unit tests for <see cref="LanguageDefinition"/> and <see cref="StringLiteral"/>.
/// </summary>
public class LanguageDefinitionTests
{
    /// <summary>
    /// Verifies that threads released together on the first use of the lazily built members
    /// (the complexity regex, the first-character lookups, and the token start set) all
    /// receive the same instances and that each member is built only once: the definition's
    /// token lists are read exactly as often as by a single-threaded first use.
    /// </summary>
    [Fact]
    public async Task LazyMembers_ConcurrentFirstAccess_BuildEachMemberOnce()
    {
        const int ThreadCount = 8;
        var singleThreadedReads = CountReads(language => TouchAll(language));

        var keywords = new CountingList<string>(["if", "while", "&&"]);
        var lineComments = new CountingList<string>(["//"]);
        var language = new LanguageDefinition
        {
            Name = "Test",
            Extensions = [".t"],
            LineCommentTokens = lineComments,
            BlockComments = [new BlockComment("/*", "*/")],
            StringLiterals = [new StringLiteral("\"")],
            ComplexityKeywords = keywords
        };
        using var barrier = new Barrier(ThreadCount);

        // Dedicated long-running tasks, so all of them are running at once and wait at the
        // barrier; a failure surfaces as a task exception rather than crashing the test host.
        var tasks = Enumerable.Range(0, ThreadCount).Select(_ => Task.Factory.StartNew(
            () =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(30));
                return TouchAll(language);
            },
            TaskCreationOptions.LongRunning)).ToArray();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        for (var member = 0; member < results[0].Length; member++)
        {
            Assert.All(results, result => Assert.Same(results[0][member], result[member]));
        }

        Assert.Equal(singleThreadedReads, keywords.Reads + lineComments.Reads);
    }

    /// <summary>
    /// Reads every lazily built member of <paramref name="language"/>.
    /// </summary>
    /// <param name="language">The definition to touch.</param>
    /// <returns>The member instances, in a fixed order.</returns>
    private static object[] TouchAll(LanguageDefinition language) =>
    [
        language.ComplexityRegex,
        language.BlockCommentsByFirstChar,
        language.LineCommentsByFirstChar,
        language.StringLiteralsByFirstChar,
        language.TokenStartChars
    ];

    /// <summary>
    /// Counts the reads of the token lists made by one single-threaded first use of every lazy member.
    /// </summary>
    /// <param name="touch">The action that touches the members.</param>
    /// <returns>The total number of reads.</returns>
    private static int CountReads(Action<LanguageDefinition> touch)
    {
        var keywords = new CountingList<string>(["if", "while", "&&"]);
        var lineComments = new CountingList<string>(["//"]);
        touch(new LanguageDefinition
        {
            Name = "Test",
            Extensions = [".t"],
            LineCommentTokens = lineComments,
            BlockComments = [new BlockComment("/*", "*/")],
            StringLiterals = [new StringLiteral("\"")],
            ComplexityKeywords = keywords
        });
        return keywords.Reads + lineComments.Reads;
    }

    /// <summary>
    /// A read-only list that counts how often it is read, to observe how many times a lazily built member is built.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="items">The wrapped items.</param>
    private sealed class CountingList<T>(T[] items) : IReadOnlyList<T>
    {
        /// <summary>
        /// The number of reads (enumerations, counts, and indexer calls) so far.
        /// </summary>
        private int _reads;

        /// <summary>
        /// Gets the number of reads so far.
        /// </summary>
        public int Reads => Volatile.Read(ref _reads);

        /// <inheritdoc />
        public int Count
        {
            get
            {
                Interlocked.Increment(ref _reads);
                return items.Length;
            }
        }

        /// <inheritdoc />
        public T this[int index]
        {
            get
            {
                Interlocked.Increment(ref _reads);
                return items[index];
            }
        }

        /// <inheritdoc />
        public IEnumerator<T> GetEnumerator()
        {
            Interlocked.Increment(ref _reads);
            return ((IEnumerable<T>)items).GetEnumerator();
        }

        /// <inheritdoc />
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Verifies that <see cref="StringLiteral.Closing"/> falls back to <see cref="StringLiteral.Delimiter"/>
    /// when no distinct <see cref="StringLiteral.CloseDelimiter"/> is set.
    /// </summary>
    [Fact]
    public void StringLiteral_Closing_DefaultsToDelimiter()
    {
        var literal = new StringLiteral("\"");

        Assert.Equal("\"", literal.Closing);
    }

    /// <summary>
    /// Verifies that <see cref="StringLiteral.Closing"/> returns the explicit
    /// <see cref="StringLiteral.CloseDelimiter"/> when one is set, as with C# verbatim
    /// strings (<c>@"…"</c>) and Rust hash-delimited raw strings (<c>r#"…"#</c>).
    /// </summary>
    [Fact]
    public void StringLiteral_Closing_UsesExplicitCloseDelimiter()
    {
        var literal = new StringLiteral("@\"", CloseDelimiter: "\"");

        Assert.Equal("\"", literal.Closing);
    }

    /// <summary>
    /// Verifies that a language with at least one line-comment token and
    /// <see cref="LanguageDefinition.ShowHealth"/> left at its default (<see langword="true"/>)
    /// supports comment-health analysis.
    /// </summary>
    [Fact]
    public void SupportsHealth_ShowHealthTrueWithLineComment_ReturnsTrue()
    {
        var language = new LanguageDefinition { Name = "Test", LineCommentTokens = ["#"] };

        Assert.True(language.SupportsHealth);
    }

    /// <summary>
    /// Verifies that a language with only block comments (no line-comment tokens) still
    /// supports comment-health analysis.
    /// </summary>
    [Fact]
    public void SupportsHealth_OnlyBlockComments_ReturnsTrue()
    {
        var language = new LanguageDefinition
        {
            Name = "Test",
            BlockComments = [new BlockComment("/*", "*/")]
        };

        Assert.True(language.SupportsHealth);
    }

    /// <summary>
    /// Verifies that <see cref="LanguageDefinition.ShowHealth"/> set to <see langword="false"/>
    /// suppresses health support even when comment tokens are defined, as with data/markup
    /// languages (YAML, JSON, XML).
    /// </summary>
    [Fact]
    public void SupportsHealth_ShowHealthFalse_ReturnsFalseEvenWithCommentTokens()
    {
        var language = new LanguageDefinition
        {
            Name = "Test",
            LineCommentTokens = ["#"],
            ShowHealth = false
        };

        Assert.False(language.SupportsHealth);
    }

    /// <summary>
    /// Verifies that a language with no comment tokens at all (e.g. a pure data format)
    /// does not support health analysis even if <see cref="LanguageDefinition.ShowHealth"/>
    /// is left at its default.
    /// </summary>
    [Fact]
    public void SupportsHealth_NoCommentTokensOrBlocks_ReturnsFalse()
    {
        var language = new LanguageDefinition { Name = "Test" };

        Assert.False(language.SupportsHealth);
    }
}
