using System.Reflection;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using BeamerPresenter.Web;

namespace BeamerPresenter.Architecture.Tests;

public sealed class CleanArchitectureTests
{
    [Fact]
    public void Domain_has_no_project_dependencies() =>
        AssertProjectReferences(typeof(PresenterState).Assembly);

    [Fact]
    public void Application_references_only_domain() =>
        AssertProjectReferences(typeof(PlaybackController).Assembly, "BeamerPresenter.Domain");

    [Fact]
    public void Infrastructure_stays_outside_ui_and_composition_root() =>
        AssertProjectReferences(
            typeof(PresenterDbContext).Assembly,
            "BeamerPresenter.Application",
            "BeamerPresenter.Domain");

    [Fact]
    public void Web_stays_outside_infrastructure_and_composition_root() =>
        AssertProjectReferences(
            typeof(PresenterConnectionState).Assembly,
            "BeamerPresenter.Application",
            "BeamerPresenter.Domain");

    [Fact]
    public void Product_and_test_sources_stay_in_their_designated_roots()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src") + Path.DirectorySeparatorChar;
        var testRoot = Path.Combine(root, "tests") + Path.DirectorySeparatorChar;
        var misplaced = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildArtifact(path))
            .Where(path => !path.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase) &&
                           !path.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(misplaced);
        Assert.True(File.Exists(Path.Combine(root, "BeamerPresenterForLanParties.slnx")));
        Assert.False(File.Exists(Path.Combine(root, "BeamerPresenterForLanParties.sln")));
    }

    [Fact]
    public void Package_versions_are_centralized_and_every_project_has_a_lock_file()
    {
        var root = FindRepositoryRoot();
        var centralPackagesPath = Path.Combine(root, "Directory.Packages.props");
        var centralPackages = System.Xml.Linq.XDocument.Load(centralPackagesPath);
        Assert.Equal(
            "true",
            centralPackages.Descendants("ManagePackageVersionsCentrally").Single().Value,
            ignoreCase: true);

        var projectFiles = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories))
            .ToArray();
        Assert.NotEmpty(projectFiles);
        foreach (var projectFile in projectFiles)
        {
            var project = System.Xml.Linq.XDocument.Load(projectFile);
            Assert.DoesNotContain(
                project.Descendants("PackageReference"),
                reference => reference.Attribute("Version") is not null || reference.Element("Version") is not null);
            Assert.True(
                File.Exists(Path.Combine(Path.GetDirectoryName(projectFile)!, "packages.lock.json")),
                $"Lockfile fehlt für {Path.GetRelativePath(root, projectFile)}.");
        }
    }

    private static void AssertProjectReferences(Assembly assembly, params string[] allowed)
    {
        var actual = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name?.StartsWith("BeamerPresenter.", StringComparison.Ordinal) == true)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(allowed.OrderBy(name => name, StringComparer.Ordinal), actual);
    }

    private static bool IsBuildArtifact(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BeamerPresenterForLanParties.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
