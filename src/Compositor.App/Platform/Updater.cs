using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Compositor.App.Platform;

/// <summary>A newer release than the running one, and where to get its installer.</summary>
public sealed record UpdateInfo(Version Version, Uri Installer, string? Sha256, string Notes)
{
    public string VersionText => Version.ToString(3);
}

/// <summary>Finds, downloads and launches Layer Form updates.
///
/// Two feeds are read: <c>update.json</c> on the Layer Form website, and the latest GitHub
/// release. The newer of the two wins, so either one being down or stale doesn't hide an update.
/// Installers are verified against their SHA-256 before they run.</summary>
public static class Updater
{
    public const string WebsiteFeed = "https://hastamev.com/layerform/update.json";
    public const string GitHubLatest = "https://api.github.com/repos/binodray/Layerform/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LayerForm/{CurrentText} (+{ProjectLinks.Repository})");
        return client;
    }

    public static Version Current => Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));
    public static string CurrentText => Current.ToString(3);

    /// <summary>True when running from an installation (the Inno Setup uninstaller sits beside the exe),
    /// as opposed to a development build; only installations check for updates on their own.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public static async Task<UpdateInfo?> FindNewerAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var results = await Task.WhenAll(Try(FromWebsite, timeout.Token, "website"), Try(FromGitHub, timeout.Token, "GitHub"));
        if (!results.Any(r => r.Reached)) throw new HttpRequestException("Neither update source could be reached.");
        var newest = results.Select(r => r.Update).OfType<UpdateInfo>().MaxBy(u => u.Version);
        return newest != null && newest.Version > Current ? newest : null;
    }

    private static async Task<(UpdateInfo? Update, bool Reached)> Try(Func<CancellationToken, Task<UpdateInfo?>> read, CancellationToken cancel, string source)
    {
        try { return (await read(cancel).ConfigureAwait(false), true); }
        catch (Exception e)
        {
            Diagnostics.Log($"Update check ({source}): {e.Message}");
            return (null, false);
        }
    }

    /// <summary>update.json: <c>{ "version": "1.1.0", "url": "…/LayerForm-Setup-1.1.0.exe", "sha256": "…", "notes": "…" }</c>.
    /// A relative <c>url</c> resolves against the feed; the hash is required.</summary>
    private static async Task<UpdateInfo?> FromWebsite(CancellationToken cancel)
    {
        using var response = await Http.GetAsync(WebsiteFeed, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        var root = json.RootElement;
        if (ParseVersion(Text(root, "version")) is not { } version || Text(root, "url") is not { } url || Text(root, "sha256") is not { } sha) return null;
        var installer = new Uri(new Uri(WebsiteFeed), url);
        return installer.Scheme == Uri.UriSchemeHttps ? new UpdateInfo(version, installer, sha, Text(root, "notes") ?? "") : null;
    }

    /// <summary>The latest published (non-draft, non-prerelease) GitHub release and its Setup .exe asset.</summary>
    private static async Task<UpdateInfo?> FromGitHub(CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubLatest);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, cancel).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        var root = json.RootElement;
        if (ParseVersion(Text(root, "tag_name")) is not { } version || !root.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = Text(asset, "name") ?? "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) continue;
            if (Text(asset, "browser_download_url") is not { } url) continue;
            // GitHub publishes each asset's digest as "sha256:<hex>".
            var digest = Text(asset, "digest");
            string? sha = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null;
            return new UpdateInfo(version, new Uri(url), sha, Text(root, "body") ?? "");
        }
        return null;
    }

    /// <summary>Downloads the installer to a temporary folder and checks its hash.</summary>
    /// <param name="progress">Bytes received and total bytes (0 when the server doesn't say).</param>
    public static async Task<string> DownloadAsync(UpdateInfo update, Action<long, long> progress, CancellationToken cancel)
    {
        string folder = Path.Combine(Path.GetTempPath(), "LayerForm-Update");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"LayerForm-Setup-{update.VersionText}.exe");
        using var response = await Http.GetAsync(update.Installer, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var file = File.Create(path))
            {
                var buffer = new byte[1 << 16];
                long received = 0, reported = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    received += read;
                    if (received - reported >= 1 << 18 || received == total) { reported = received; progress(received, total); }
                }
            }
            string actual = Convert.ToHexString(hash.GetHashAndReset());
            if (update.Sha256 != null && !actual.Equals(update.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded installer didn’t match its published checksum, so it wasn’t run. Please try again.");
            return path;
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    /// <summary>Runs the installer silently; it waits for Layer Form to exit, updates it, and reopens it.</summary>
    public static void LaunchInstaller(string path)
    {
        try { Process.Start(new ProcessStartInfo(path, "/SP- /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /UPDATE=1") { UseShellExecute = true }); }
        catch (Exception e) { Diagnostics.Log("Couldn’t start installer: " + e.Message); }
    }

    // Skipped versions are stored as a number in settings.json, which only holds numbers.
    private static double Key(Version v) => v.Major * 1_000_000.0 + v.Minor * 1_000 + v.Build;
    public static bool IsSkipped(Version version) => Settings.Get("update.skipped", 0) == Key(version);
    public static void Skip(Version version) => Settings.Set("update.skipped", Key(version));

    private static Version? ParseVersion(string? text) =>
        Version.TryParse(text?.Trim().TrimStart('v', 'V'), out var v) ? Normalize(v) : null;

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
