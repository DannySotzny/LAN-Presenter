using System.Reflection;
using System.Runtime.InteropServices;

namespace BeamerPresenter.App;

internal sealed record BuildInformation(
    string Version,
    string InformationalVersion,
    DateTimeOffset? BuildTimestampUtc,
    string GitCommitSha,
    string RuntimeVersion)
{
    public string ShortGitCommitSha => GitCommitSha.Length > 8 ? GitCommitSha[..8] : GitCommitSha;

    public static BuildInformation Current { get; } = FromAssembly(typeof(Program).Assembly);

    internal static BuildInformation FromAssembly(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Value is not null)
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value!, StringComparer.Ordinal);
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
        var version = assembly.GetName().Version?.ToString(3) ?? "unknown";
        DateTimeOffset? buildTimestamp = metadata.TryGetValue("BuildTimestampUtc", out var timestamp)
            && DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedTimestamp)
                ? parsedTimestamp.ToUniversalTime()
                : null;
        var gitCommitSha = metadata.GetValueOrDefault("GitCommitSha")
            ?? informationalVersion.Split('+', 2).ElementAtOrDefault(1)
            ?? "unknown";

        return new BuildInformation(version, informationalVersion, buildTimestamp, gitCommitSha, RuntimeInformation.FrameworkDescription);
    }
}
