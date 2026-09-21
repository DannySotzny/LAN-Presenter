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
