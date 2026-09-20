namespace BeamerPresenter.Application;

public static class MediaFileSupport
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v"
    };

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));
}
