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

    // Any fence a Markdown renderer would show as C#: indented up to three spaces, tilde or backtick, and
    // tagged csharp / cs / c#. Deliberately LOOSER than CSharpBlock above - this one counts, that one verifies,
    // and the two numbers must agree. An example written in a shape the verifier cannot read therefore fails
    // the test instead of slipping past it.
    [GeneratedRegex(@"^[ ]{0,3}(?:```+|~~~+)[ \t]*(?:(?:csharp|cs)\b|c\#(?![\w#]))", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AnyCSharpFence();

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
    [DataRow("```csharp\n", true)]
    [DataRow("```cs\n", true)]
    [DataRow("```c#\n", true)]      // `\b` after '#' never matched - this spelling used to slip past the detector
    [DataRow("```C# title\n", true)]
    [DataRow("   ~~~~cs\n", true)]
    [DataRow("```css\n", false)]
    [DataRow("```bash\n", false)]
    [DataRow("    ```csharp\n", false)] // four spaces is an indented code block, not a fence
    public void The_Fence_Detector_Sees_Every_Spelling_Of_A_CSharp_Fence(string line, bool isCSharp) =>
        AnyCSharpFence().IsMatch(line).Should().Be(isCSharp);

    [TestMethod]
    public void Every_CSharp_Block_In_The_Readme_Is_Compiled_Source()
    {
        var regions = Region().Matches(Read("samples", "Jev.Net.Samples", "ReadmeSnippets.cs"))
            .ToDictionary(m => m.Groups["name"].Value, m => Dedent(m.Groups["body"].Value));
        var readme = Read("README.md");
        var blocks = CSharpBlock().Matches(readme);

        blocks.Count.Should().BeGreaterThanOrEqualTo(4, "if this finds nothing, the check is checking nothing");
        AnyCSharpFence().Matches(readme).Count.Should().Be(blocks.Count,
            "every C# fence must be one the verifier can read: column zero, three backticks, tagged exactly `csharp`");
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
