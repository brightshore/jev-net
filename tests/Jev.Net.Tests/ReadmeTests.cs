using System.Text.RegularExpressions;

namespace Jev.Net.Tests;

/// <summary>
/// The .NET answer to upstream's <c>test_docs.py</c>, which EXECUTES its README. Here every C# block in the README
/// must be, verbatim, one or more <c>#region snippet:…</c> blocks of the samples project — which the compiler
/// has therefore already accepted. A block with no marker fails too, so an unverified example cannot slip in.
/// </summary>
[TestClass]
public sealed partial class ReadmeTests
{
    [GeneratedRegex(@"(?:<!-- snippet: (?<names>[^>]+?) -->\n)?```csharp\n(?<code>.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex CSharpBlock();

    [GeneratedRegex(@"^[ \t]*#region snippet:(?<name>\S+)[ \t]*\n(?<body>.*?)\n[ \t]*#endregion", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex Region();

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jev.Net.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Jev.Net.slnx not found above the test binary.");
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Root(), .. path])).ReplaceLineEndings("\n");

    private static string Dedent(string body)
    {
        var lines = body.Split('\n');
        var indent = lines.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart().Length);
        return string.Join('\n', lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimEnd()));
    }

    [TestMethod]
    public void Every_CSharp_Block_In_The_Readme_Is_Compiled_Source()
    {
        var regions = Region().Matches(Read("samples", "Jev.Net.Samples", "ReadmeSnippets.cs"))
            .ToDictionary(m => m.Groups["name"].Value, m => Dedent(m.Groups["body"].Value));
        var blocks = CSharpBlock().Matches(Read("README.md"));

        blocks.Count.Should().BeGreaterThanOrEqualTo(4, "if this finds nothing, the check is checking nothing");
        foreach (Match block in blocks)
        {
            var code = block.Groups["code"].Value;
            block.Groups["names"].Success.Should().BeTrue(
                $"every C# block needs a <!-- snippet: name --> marker tying it to compiled source; this one has none:\n{code}");

            var names = block.Groups["names"].Value.Split('+', StringSplitOptions.TrimEntries);
            names.Should().OnlyContain(name => regions.ContainsKey(name), "each marker must name a region in ReadmeSnippets.cs");
            code.Should().Be(string.Join("\n\n", names.Select(name => regions[name])),
                $"the README block for [{string.Join(" + ", names)}] must match ReadmeSnippets.cs verbatim - edit the source, then copy it here");
        }

        regions.Keys.Should().BeSubsetOf(blocks.SelectMany(b => b.Groups["names"].Value.Split('+', StringSplitOptions.TrimEntries)),
            "a region nobody shows is dead weight");
    }
}
