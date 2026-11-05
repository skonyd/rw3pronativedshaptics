using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RW3ProNativeDSHaptics;

public sealed record UpdateInfo(string Version, string PackageUrl, string HashUrl, bool IsPortable);

public sealed class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };
    static UpdateService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("RW3ProNativeDSHaptics/0.1");

    public static string InstalledVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
    {
        using var response = await Http.GetAsync(
            "https://api.github.com/repos/skonyd/rw3pronativedshaptics/releases/latest", token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagNode)) return null;
        string latest = (tagNode.GetString() ?? "").TrimStart('v', 'V');
        if (!Version.TryParse(latest, out var offered) ||
            !Version.TryParse(InstalledVersion, out var local) ||
            offered <= local) return null;

        var assetName = SettingsStore.IsPortable ?
            "RW3ProNativeDSHaptics-Portable-win-x64.zip" :
            "RW3ProNativeDSHaptics-Setup-win-x64.exe";
        string? pkg = null, checksum = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == assetName) pkg = url;
            if (name == assetName + ".sha256") checksum = url;
        }
        return pkg != null && checksum != null ? new UpdateInfo(latest, pkg, checksum, SettingsStore.IsPortable) : null;
    }

    public async Task<string> DownloadVerifiedAsync(UpdateInfo info, CancellationToken token = default)
    {
        // Accept only the exact repository/release URLs and known artifact names.
        var packageUri = new Uri(info.PackageUrl);
        var checksumUri = new Uri(info.HashUrl);
        if (packageUri.Scheme != "https" || checksumUri.Scheme != "https" ||
            !packageUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !checksumUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !packageUri.AbsolutePath.StartsWith("/skonyd/rw3pronativedshaptics/releases/download/", StringComparison.Ordinal) ||
            !checksumUri.AbsolutePath.StartsWith("/skonyd/rw3pronativedshaptics/releases/download/", StringComparison.Ordinal))
            throw new InvalidOperationException("Güncelleme bağlantısı güvenilir GitHub Releases yoluyla eşleşmiyor.");

        var expected = (await Http.GetStringAsync(checksumUri, token)).Trim().Split(
            new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (expected == null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidDataException("SHA256 manifesti geçersiz.");
        var fileName = Path.GetFileName(packageUri.LocalPath);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        var destination = Path.Combine(downloads, fileName);
        var partial = destination + ".download";
        try
        {
            using (var response = await Http.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var dest = File.Create(partial);
                await source.CopyToAsync(dest, token);
            }
            await using (var file = File.OpenRead(partial))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA256 doğrulaması BAŞARISIZ. Paket kullanılmayacak.");
            }
            File.Move(partial, destination, true);
            return destination;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
