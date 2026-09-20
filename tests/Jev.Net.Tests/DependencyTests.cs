namespace Jev.Net.Tests;

/// <summary>
/// "One dependency" is the README's first claim. The packed nuspec is checked by
/// <c>scripts/check-dependencies.sh</c> in CI; this is the half that runs on every local <c>dotnet test</c>:
/// the ASSEMBLY may reference the framework and the logging abstractions, and nothing else.
/// </summary>
[TestClass]
public sealed class DependencyTests
{
    private static readonly string[] Allowed = ["Microsoft.Extensions.Logging.Abstractions"];

    [TestMethod]
    public void The_Assembly_References_Only_The_Framework_And_The_Logging_Abstractions()
    {
        var outside = typeof(TypeSafeClient).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name != "netstandard" && name != "mscorlib" && !name.StartsWith("System.", StringComparison.Ordinal))
            .ToList();

        outside.Should().BeEquivalentTo(Allowed,
            "a second dependency is a change to what this package IS - update the README before this list");
    }

    [TestMethod]
    public void The_Project_File_Declares_Exactly_One_Package()
    {
        // Assembly references only show what the code USES; a PackageReference that nothing calls yet would
        // still ship in the nuspec. So the project file is read too.
        var project = Path.Combine(RepoRoot(), "src", "Jev.Net", "Jev.Net.csproj");
        var packages = System.Xml.Linq.XDocument.Load(project).Descendants("PackageReference")
            .Select(reference => (string?)reference.Attribute("Include")).ToList();

        packages.Should().BeEquivalentTo(Allowed);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jev.Net.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Jev.Net.slnx not found above the test binary.");
    }
}
