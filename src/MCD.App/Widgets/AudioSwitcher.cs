using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Mcd.App.Widgets;

/// <summary>One sound device as Master Audio Switcher sees it.</summary>
public sealed record SwitcherDevice(string Id, string Name, string Icon, bool Current);

/// <summary>
/// The line to Master Audio Switcher, which then lives on the bar instead of
/// in the tray.
/// </summary>
/// <remarks>
/// <para>
/// Its local HTTP interface, as its docs/DOCK.md describes: a note in its data
/// folder says where it listens and with what token, and a hello once a
/// second both fetches its state and keeps its tray icon away. Fifteen
/// seconds without one and the icon comes back by itself - so a bar that has
/// crashed never leaves the program without a face.
/// </para>
/// <para>
/// One line for the whole program, not one per chip. The bar rebuilds its
/// widgets whenever a setting moves, and there is a chip on every screen: a
/// line per chip would hand the icon back and take it again on every
/// rebuild, and the tray would blink. So chips only count themselves in and
/// out, and the icon goes back a few seconds after the last one leaves, or at
/// once when the program stops.
/// </para>
/// </remarks>
public static partial class AudioSwitcher
{
    private const int Protocol = 1;

    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Long enough for its hello, which enumerates every sound device and
    /// was measured at three seconds. At two, nearly every hello was cut off:
    /// the chip hid itself while the program, which had heard the hello all
    /// the same, kept its tray icon away.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Hellos lost in a row before the chip hides and the icon is handed back.</summary>
    private const int Patience = 3;

    private static readonly Lock Gate = new();

    private static int _chips;
    private static bool _running;
    private static ILogger? _log;

    private static (string Url, string Token)? _line;
    private static bool _hosting;
    private static bool _warned;
    private static int _missed;
    private static readonly Dictionary<string, string> Drawings = [];

    /// <summary>What the program said last, or null while it is not there.</summary>
    public static IReadOnlyList<SwitcherDevice>? Outputs { get; private set; }

    /// <summary>A chip is on a bar and wants the program drawn there.</summary>
    public static void Join(ILogger log)
    {
        lock (Gate)
        {
            _log ??= log;
            _chips++;

            if (_running)
            {
                return;
            }

            _running = true;
        }

        _ = Task.Run(Run);
    }

    /// <summary>A chip has left its bar.</summary>
    public static void Leave()
    {
        lock (Gate)
        {
            _chips = Math.Max(0, _chips - 1);
        }
    }

    /// <summary>The tray icon back, now: the program is stopping.</summary>
    public static void HandBack()
    {
        lock (Gate)
        {
            _chips = 0;
        }

        if (_hosting && _line is { } line)
        {
            _hosting = false;
            Call(line, "dock_take_over", new JsonObject { ["hosting"] = false }).Wait(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// Whether the program is running: its note is there, and so is the
    /// process the note names. A crash leaves the note behind.
    /// </summary>
    public static bool Present()
    {
        if (Find() is not { } line)
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(line.Pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>What a click on its tray icon does.</summary>
    public static void Next() => Fire("switch_next", []);

    /// <summary>The sound moved to one device.</summary>
    public static void SwitchTo(string id) => Fire("switch_to", new JsonObject { ["device_id"] = id });

    /// <summary>The outline drawn for a device, once it has been fetched; null until then.</summary>
    public static string? Drawing(string icon)
    {
        lock (Drawings)
        {
            return Drawings.GetValueOrDefault(icon);
        }
    }

    private static async Task Run()
    {
        DateTime alone = DateTime.MaxValue;

        while (true)
        {
            int chips;

            lock (Gate)
            {
                chips = _chips;
            }

            if (chips > 0)
            {
                alone = DateTime.MaxValue;
            }
            else if (alone == DateTime.MaxValue)
            {
                alone = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - alone >= Grace)
            {
                lock (Gate)
                {
                    if (_chips == 0)
                    {
                        _running = false;
                        break;
                    }
                }

                continue;
            }

            await Hello(want: chips > 0);
            await Task.Delay(Beat);
        }

        if (_line is { } line)
        {
            await Release(line, "no chip on any bar");
        }

        Publish(null);
    }

    private static async Task Hello(bool want)
    {
        _line ??= Find() is { } found ? (found.Url, found.Token) : null;

        if (_line is not { } line)
        {
            Publish(null);
            return;
        }

        JsonNode? said = await Call(line, "dock_hello", []);

        if (said is null || said["protocol"]?.GetValue<int>() != Protocol)
        {
            // Gone, restarted on another port, or a protocol this does not
            // speak. The note is read again next time.
            if (said is not null && !_warned)
            {
                _warned = true;
                _log?.LogWarning("switcher.protocol {Protocol} not understood", said["protocol"]);
            }

            // One slow answer is not an absence. Past that, the chip is
            // gone from the bar, and so the icon goes back to the tray - a
            // program drawn nowhere is the one thing this must not cause.
            if (++_missed >= Patience)
            {
                _line = null;
                Publish(null);
                await Release(line, "lost");
            }

            return;
        }

        _missed = 0;
        List<SwitcherDevice> outputs = [];

        foreach (JsonNode? device in said["state"]?["outputs"]?.AsArray() ?? [])
        {
            if (device is null)
            {
                continue;
            }

            var entry = new SwitcherDevice(
                device["id"]?.GetValue<string>() ?? string.Empty,
                device["name"]?.GetValue<string>() ?? string.Empty,
                device["icon"]?.GetValue<string>() ?? string.Empty,
                device["is_default"]?.GetValue<bool>() == true);

            outputs.Add(entry);
            await Fetch(line, entry.Icon);
        }

        Publish(outputs);

        // Taken only once a chip is on a bar and has something to draw, and
        // never let go here otherwise - the loop's end does that, after the
        // grace, so a rebuild does not blink the tray.
        if (want && outputs.Count > 0 && said["hosting"]?.GetValue<bool>() != true
            && await Call(line, "dock_take_over", new JsonObject { ["hosting"] = true }) is not null)
        {
            _log?.LogInformation("switcher.took-over version={Version}", said["version"]);
        }

        _hosting |= want && outputs.Count > 0;
    }

    /// <summary>The tray icon handed back, if it was ours.</summary>
    private static async Task Release((string Url, string Token) line, string why)
    {
        if (!_hosting)
        {
            return;
        }

        _hosting = false;
        await Call(line, "dock_take_over", new JsonObject { ["hosting"] = false });
        _log?.LogInformation("switcher.handed-back {Why}", why);
    }

    /// <summary>
    /// Fetches the outline for a device's icon, once.
    /// </summary>
    /// <remarks>
    /// Its outlines are strokes on the same 24-unit grid as ours, so the
    /// paths of one drawing, run together, are one path this bar can draw in
    /// its own colour.
    /// </remarks>
    private static async Task Fetch((string Url, string Token) line, string icon)
    {
        if (icon.Length == 0 || Drawing(icon) is not null || !Regex.IsMatch(icon, "^[a-z0-9-]+$"))
        {
            return;
        }

        try
        {
            string svg = await Http.GetStringAsync($"{line.Url}icons/devices/{icon}.svg");
            string path = string.Join(' ', PathData().Matches(svg).Select(m => m.Groups[1].Value));

            if (path.Length > 0)
            {
                lock (Drawings)
                {
                    Drawings[icon] = path;
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            _log?.LogInformation("switcher.icon {Icon} not fetched: {Error}", icon, e.Message);
        }
    }

    private static void Publish(List<SwitcherDevice>? outputs) => Outputs = outputs;

    private static void Fire(string method, JsonObject arguments)
    {
        if (_line is { } line)
        {
            _ = Task.Run(async () =>
            {
                await Call(line, method, arguments);
                await Hello(want: _hosting);
            });
        }
    }

    private static async Task<JsonNode?> Call((string Url, string Token) line, string method, JsonObject arguments)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{line.Url}api/{method}")
            {
                Content = new StringContent(arguments.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
            };

            request.Headers.Add("X-MAS-Token", line.Token);

            using HttpResponseMessage response = await Http.SendAsync(request);
            JsonNode? body = JsonNode.Parse(await response.Content.ReadAsStringAsync());

            if (body?["ok"]?.GetValue<bool>() != true)
            {
                _log?.LogWarning("switcher.{Method} refused: {Error}", method, body?["error"]);
                return null;
            }

            return body["result"];
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The note it leaves when it starts: the ordinary copy's, then the Store
    /// copy's, whose writes to the data folder may be redirected.
    /// </summary>
    private static (string Url, string Token, int Pid)? Find()
    {
        string local = Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] notes =
        [
            Path.Combine(local, "MasterAudioSwitcher", "bridge.json"),
            Path.Combine(
                local,
                "Packages",
                "ElectronicMARS.MasterAudioSwitcher_8b14r1jdzqs64",
                "LocalCache",
                "Local",
                "MasterAudioSwitcher",
                "bridge.json"),
        ];

        foreach (string note in notes)
        {
            try
            {
                JsonNode? read = JsonNode.Parse(File.ReadAllText(note));

                if (read?["url"]?.GetValue<string>() is { Length: > 0 } url
                    && read["token"]?.GetValue<string>() is { Length: > 0 } token
                    && url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
                {
                    return (url.EndsWith('/') ? url : url + "/", token, read["pid"]?.GetValue<int>() ?? 0);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException)
            {
            }
        }

        return null;
    }

    [GeneratedRegex("<path[^>]*\\sd=\"([^\"]+)\"")]
    private static partial Regex PathData();
}
