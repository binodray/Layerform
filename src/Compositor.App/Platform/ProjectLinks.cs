using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Compositor.App.Platform;

/// <summary>Where Help › Report a Bug and Request a Feature send people.
/// Bug reports and feature requests open the repository's GitHub issue forms with
/// the version and Windows fields already filled in, so reports arrive triaged.</summary>
public static class ProjectLinks
{
    public const string Repository = "https://github.com/binodray/Layerform";
    public const string Changelog = Repository + "/blob/main/CHANGELOG.md";
    public const string License = Repository + "/blob/main/LICENSE";
    public const string Upstream = "https://github.com/robbietilton/Compositor";

    public static string AppVersion => typeof(ProjectLinks).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>"Windows 11 (build 26200), x64" — Windows 11 still reports itself as 10.0, so the build number decides.</summary>
    public static string WindowsDescription
    {
        get
        {
            var version = Environment.OSVersion.Version;
            string name = version.Major == 10 && version.Build >= 22000 ? "Windows 11" : $"Windows {version.Major}";
            return $"{name} (build {version.Build}), {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
        }
    }

    public static string BugReport =>
        $"{Repository}/issues/new?template=bug_report.yml&version={Uri.EscapeDataString(AppVersion)}&windows={Uri.EscapeDataString(WindowsDescription)}";

    public static string FeatureRequest => $"{Repository}/issues/new?template=feature_request.yml";

    /// <summary>Opens a link in the default browser; failures are logged rather than shown, since there's nothing to recover.</summary>
    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Diagnostics.Log("Couldn’t open link: " + e.Message); }
    }
}
