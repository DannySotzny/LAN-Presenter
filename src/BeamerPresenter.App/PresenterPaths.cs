namespace BeamerPresenter.App;

internal sealed record PresenterPaths(
    string RootDirectory,
    string DataDirectory,
    string LogsDirectory,
    string BackupDirectory,
    string ChromeProfileDirectory,
    string ToolsDirectory)
{
    public static PresenterPaths CreateDefault()
    {
        var rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HouseOfLAN",
            "Presenter");
        return Create(rootDirectory);
    }

    internal static PresenterPaths Create(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);
        var paths = new PresenterPaths(
            root,
            Path.Combine(root, "Data"),
            Path.Combine(root, "Logs"),
            Path.Combine(root, "Backup"),
            Path.Combine(root, "ChromeProfile"),
            Path.Combine(root, "Tools"));

        Directory.CreateDirectory(paths.RootDirectory);
        Directory.CreateDirectory(paths.DataDirectory);
        Directory.CreateDirectory(paths.LogsDirectory);
        Directory.CreateDirectory(paths.BackupDirectory);
        Directory.CreateDirectory(paths.ChromeProfileDirectory);
        Directory.CreateDirectory(paths.ToolsDirectory);
        return paths;
    }
}
