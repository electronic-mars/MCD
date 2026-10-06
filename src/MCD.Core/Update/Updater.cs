using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcd.Core.Update;

/// <summary>What the release page offers.</summary>
public sealed record UpdateOffer(string Version, string Notes, string Url, byte[] Signature);

/// <summary>
/// Updating from the release page, checked against a key inside the program.
/// </summary>
/// <remarks>
/// <para>
/// The program downloads an installer and runs it, which is only defensible if
/// the file can be proved to be ours. A checksum published beside the file
/// proves nothing: whoever serves a false installer serves a false checksum.
/// So each release carries a signature made by a key that exists only in the
/// build server's secrets, and the program carries just the public half
/// (<see cref="ReleaseKey"/>). An installer that key did not sign is deleted,
/// not run.
/// </para>
/// <para>
/// Nothing here runs by itself. It happens when a person presses the button.
/// </para>
/// </remarks>
public static class Updater
{
    private const string Feed = "https://github.com/electronic-mars/MCD/releases/latest/download/latest.json";

    // Where a download may come from. The signature already makes a
    // substitution useless; there is just no reason to go anywhere else.
    private static readonly string[] Hosts =
        ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    private const long MaxBytes = 400L * 1024 * 1024;

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    /// <summary>"1.2.10" to (1, 2, 10); a leading v and anything after the numbers go.</summary>
    public static Version AsNumbers(string text)
    {
        int[] parts = [.. Regex.Matches(text ?? string.Empty, @"\d+")
            .Select(m => int.TryParse(m.Value, out int n) ? n : 0).Take(3)];

        return new Version(
            parts.Length > 0 ? parts[0] : 0,
            parts.Length > 1 ? parts[1] : 0,
            parts.Length > 2 ? parts[2] : 0);
    }

    public static bool IsNewer(string offered, string current) => AsNumbers(offered) > AsNumbers(current);

    private static Uri Allowed(string url)
    {
        var uri = new Uri(url);

        if (uri.Scheme != Uri.UriSchemeHttps || !Hosts.Contains(uri.Host))
        {
            throw new InvalidOperationException($"{uri.Host} is not a host updates are fetched from");
        }

        return uri;
    }

    /// <summary>A GET that follows redirects itself, each hop held to the same list.</summary>
    private static async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancel)
    {
        Uri at = Allowed(url);

        for (int hop = 0; hop < 6; hop++)
        {
            HttpResponseMessage answer = await Http.GetAsync(at, HttpCompletionOption.ResponseHeadersRead, cancel);

            if ((int)answer.StatusCode is >= 300 and < 400 && answer.Headers.Location is { } next)
            {
                answer.Dispose();
                at = Allowed(new Uri(at, next).ToString());
                continue;
            }

            return answer.EnsureSuccessStatusCode();
        }

        throw new InvalidOperationException("too many redirects");
    }

    /// <summary>What the release page offers. Throws when it cannot be read.</summary>
    public static async Task<UpdateOffer> LatestAsync(CancellationToken cancel = default)
    {
        using HttpResponseMessage answer = await GetAsync(Feed, cancel);
        using JsonDocument feed = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancel));
        JsonElement root = feed.RootElement;

        JsonElement windows = root.GetProperty("platforms").GetProperty("windows-x86_64");
        string url = windows.GetProperty("url").GetString() ?? string.Empty;
        string signature = windows.GetProperty("signature").GetString() ?? string.Empty;
        string version = root.GetProperty("version").GetString() ?? string.Empty;

        if (version.Length == 0 || url.Length == 0 || signature.Length != 128)
        {
            throw new InvalidOperationException("the release description is incomplete");
        }

        Allowed(url);

        return new UpdateOffer(
            version,
            root.TryGetProperty("notes", out JsonElement notes) ? notes.GetString() ?? string.Empty : string.Empty,
            url,
            Convert.FromHexString(signature));
    }

    /// <summary>Whether this is the file our key signed.</summary>
    public static bool Verify(string path, byte[] signature) => Verify(path, signature, ReleaseKey.Public);

    /// <summary>Whether this key signed the file. The key is a parameter so the check can be tested.</summary>
    public static bool Verify(string path, byte[] signature, byte[] publicKey)
    {
        if (publicKey.Length != 64 || publicKey.All(b => b == 0) || signature.Length != 64)
        {
            return false;
        }

        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[..32], Y = publicKey[32..] },
        });

        using FileStream file = File.OpenRead(path);

        return key.VerifyData(file, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    /// Fetches the installer and checks it. Returns the path, or throws; a file
    /// that fails the check is deleted rather than kept.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateOffer offer, IProgress<double>? progress, CancellationToken cancel = default)
    {
        // Beside the settings, never in the program's own folder: that folder
        // is what the installer is about to replace. One fixed name: a name
        // that arrives from outside is somebody else's choice of where to write.
        string folder = Path.Combine(Infrastructure.AppPaths.Root, "update");
        Directory.CreateDirectory(folder);

        string target = Path.Combine(folder, "update-setup.exe");
        string part = target + ".part";

        using (HttpResponseMessage answer = await GetAsync(offer.Url, cancel))
        {
            long total = answer.Content.Headers.ContentLength ?? 0;

            if (total > MaxBytes)
            {
                throw new InvalidOperationException("the installer is larger than anything this program ships");
            }

            await using Stream from = await answer.Content.ReadAsStreamAsync(cancel);
            await using FileStream to = File.Create(part);

            byte[] block = new byte[64 * 1024];
            long got = 0;
            int read;

            while ((read = await from.ReadAsync(block, cancel)) > 0)
            {
                got += read;

                if (got > MaxBytes)
                {
                    throw new InvalidOperationException("the download outgrew what was promised");
                }

                await to.WriteAsync(block.AsMemory(0, read), cancel);

                if (total > 0)
                {
                    progress?.Report((double)got / total);
                }
            }
        }

        File.Move(part, target, overwrite: true);

        if (!Verify(target, offer.Signature))
        {
            File.Delete(target);
            throw new InvalidOperationException("the installer was not signed by this program's release key");
        }

        return target;
    }

    /// <summary>
    /// Hands over to the installer, silently: the person said yes by pressing
    /// the button. The installer closes the running copy properly and starts
    /// the program again itself.
    /// </summary>
    public static void Install(string installer) =>
        Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /RELAUNCH=1")
        {
            UseShellExecute = false,
        });
}
