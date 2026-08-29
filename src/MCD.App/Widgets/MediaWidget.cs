using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mcd.App.Dock;
using Mcd.Core.Settings;
using Mcd.Sensors.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Control;

namespace Mcd.App.Widgets;

/// <summary>
/// Whatever is playing, and the three buttons for it.
/// </summary>
/// <remarks>
/// <para>
/// Through the same session Windows itself uses for the media keys and the
/// volume flyout, so it drives whatever is playing - a browser tab, a music
/// program, a game - without knowing anything about any of them.
/// </para>
/// <para>
/// Invisible while nothing is playing. A row of dead buttons on a bar is worse
/// than a gap, and this is the one widget that genuinely has nothing to say
/// most of the time.
/// </para>
/// </remarks>
public sealed partial class MediaWidget(WidgetContext context, WidgetConfig entry)
    : WidgetViewModel(context, entry)
{
    public const string Type = "mcd.media";

    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    private GlobalSystemMediaTransportControlsSessionManager? _sessions;
    private GlobalSystemMediaTransportControlsSession? _playing;
    private string? _lastApp;
    private bool _gone;

    /// <summary>Players seen this session, id to short name, for the editor.</summary>
    private static readonly Dictionary<string, string> Seen = [];

    /// <summary>The player the person nominated to always receive the buttons.</summary>
    private string Nominated => WidgetOptions.Text(Options, "player") ?? string.Empty;

    public override string TypeId => Type;

    /// <summary>Whether the name of the track is written on the bar itself.</summary>
    /// <remarks>
    /// Off unless asked for. The name is always in the tooltip, and a bar is
    /// glanced at, not read - a scrolling track title next to three buttons
    /// makes the whole cluster look busier than anything else on it.
    /// </remarks>
    private bool ShowTitle => WidgetOptions.Text(Options, "title") == "shown";

    /// <summary>
    /// Faded while there is nothing to control, never gone.
    /// </summary>
    /// <remarks>
    /// It used to hide itself entirely, and that confused more than it
    /// tidied: the widget was on the bar and in the settings but nowhere to
    /// be seen, and the Add menu greyed it out as already present. Half-lit
    /// buttons say both things at once - this is where the music will be,
    /// and there is none right now.
    /// </remarks>
    [ObservableProperty]
    public partial double Dim { get; set; } = 0.35;

    /// <summary>What is playing, for the full-size bar and the tooltip.</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>The playing thing's own artwork, when it offers any.</summary>
    [ObservableProperty]
    public partial ImageSource? Art { get; set; }

    [ObservableProperty]
    public partial Visibility ArtVisible { get; set; } = Visibility.Collapsed;

    /// <summary>
    /// The two icons that never change, as properties rather than literals.
    /// </summary>
    /// <remarks>
    /// A compiled binding with no path binds to the object itself, so a
    /// constant has to be reachable as a property for the converter to be
    /// handed a name rather than the widget.
    /// </remarks>
    public string PreviousIcon => "Previous";

    public string NextIcon => "Next";

    /// <summary>What the middle button's tooltip says: the track, when one is known.</summary>
    public string PlayTip => Title.Length > 0 ? Title : Loc.Tr("MediaPlayPause", "Play or pause");

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(PlayTip));

    /// <summary>
    /// The two middle glyphs, cross-faded rather than swapped.
    /// </summary>
    /// <remarks>
    /// Both are always in the tree and trade opacity over a tenth of a
    /// second, so the button never changes size and the switch reads as a
    /// morph instead of a jump.
    /// </remarks>
    public string PlayGlyph => "Play";

    public string PauseGlyph => "Pause";

    [ObservableProperty]
    public partial double PlayShown { get; set; } = 1;

    [ObservableProperty]
    public partial double PauseShown { get; set; }

    [ObservableProperty]
    public partial double IconSize { get; set; } = 17;

    [ObservableProperty]
    public partial double FontSize { get; set; } = 10;

    [ObservableProperty]
    public partial Visibility TitleVisible { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Thickness Spacing { get; set; }

    public override void Attach()
    {
        IconSize = DockMetrics.ReadingIcon(Density);
        FontSize = 10;
        Spacing = GaugeWidget.Gap(Orientation);
        TitleVisible = ShowTitle && Density == DockDensity.Default
            ? Visibility.Visible
            : Visibility.Collapsed;

        Listen();
    }

    /// <summary>Nothing here follows the readings; it follows what is playing.</summary>
    public override void Tick(SensorSnapshot snapshot)
    {
    }

    /// <summary>
    /// The artwork, three keys, and the track's name when it is written out.
    /// </summary>
    /// <remarks>
    /// Sized for the widest it gets rather than for what is playing now, so the
    /// bar does not re-settle every time a track changes. The player is the one
    /// widget that comes and goes on its own, and slots it might need later
    /// have to be its own while it is there.
    /// </remarks>
    public override double Length()
    {
        // Down a bar at the side of the screen the keys stay in a row and the
        // track's name goes under them, so its length is that stack's height.
        if (Orientation == Orientation.Vertical)
        {
            return 8 + IconSize + 10 + (TitleVisible == Visibility.Visible ? FontSize + 6 : 0);
        }

        return 4 + 24 + (3 * (IconSize + 8)) + (TitleVisible == Visibility.Visible ? 120 : 0);
    }

    public override string Summarise() => Nominated.Length > 0
        ? string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Loc.Tr("MediaSummaryNominated", "The buttons always drive {0}"),
            WidgetOptions.Text(Options, "playerName") ?? Short(Nominated))
        : ShowTitle
            ? Loc.Tr("MediaSummaryShown", "Buttons, with the track written next to them")
            : Loc.Tr("MediaSummaryTooltip", "Buttons; the track is in their tooltip");

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed)
    {
        // One label that says what being on means, not two that swap with the
        // state: a caption beside a switch reading "Only in the tooltip" could
        // be describing where the track is now or where pressing would put it,
        // and there is no way to tell which.
        StackPanel title = Mcd.App.Settings.Braun.Field(
            Loc.Tr("MediaTitleHeader", "Write the track on the bar"),
            Mcd.App.Settings.Braun.Switch(
                ShowTitle,
                on => changed(WidgetOptions.Merge(Options, ("title", on ? "shown" : null)))),
            Loc.Tr("MediaTitleHint", "Off puts it in the tooltip instead."));

        // The nomination. The chosen player is always offered, running or
        // not, or the setting could not be seen - let alone taken away.
        var ids = new List<string> { string.Empty };
        var names = new List<string> { Loc.Tr("MediaPriorityNone", "Automatic: whoever is playing now") };

        Dictionary<string, string> offer = new(Seen);

        if (Nominated.Length > 0 && !offer.ContainsKey(Nominated))
        {
            offer[Nominated] = WidgetOptions.Text(Options, "playerName") ?? Short(Nominated);
        }

        foreach ((string id, string name) in offer.OrderBy(kv => kv.Value))
        {
            ids.Add(id);
            names.Add(name);
        }

        StackPanel players = Mcd.App.Settings.Braun.Field(
            Loc.Tr("MediaPriorityHeader", "The buttons always drive"),
            Mcd.App.Settings.Braun.Choice(
                names,
                Math.Max(0, ids.IndexOf(Nominated)),
                i =>
                {
                    string id = ids[i];

                    changed(WidgetOptions.Merge(
                        Options,
                        ("player", id.Length > 0 ? id : null),
                        ("playerName", id.Length > 0 ? Short(id) : null)));
                }));

        return new StackPanel { Spacing = 14, Children = { title, players } };
    }

    public override void Dispose()
    {
        _gone = true;
        Forget();

        if (_sessions is not null)
        {
            _sessions.SessionsChanged -= OnSessionsChanged;
            _sessions.CurrentSessionChanged -= OnSessionChanged;
            _sessions = null;
        }

        base.Dispose();
    }

    /// <summary>
    /// Finds the session manager and follows it.
    /// </summary>
    /// <remarks>
    /// Everything from here arrives on a thread of the system's choosing, so
    /// every one of these handlers hands its work back to the interface thread
    /// before touching anything the bar draws.
    /// </remarks>
    private async void Listen()
    {
        try
        {
            _sessions = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception e)
        {
            // No session service on this machine, or it refused. There is
            // nothing to fall back on and nothing to show.
            Context.Log.LogInformation(e, "media.unavailable");
            return;
        }

        if (_gone)
        {
            return;
        }

        _sessions.SessionsChanged += OnSessionsChanged;
        _sessions.CurrentSessionChanged += OnSessionChanged;
        Repick();
    }

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) =>
        _ui.TryEnqueue(Repick);

    private void OnSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        _ui.TryEnqueue(Repick);

    /// <summary>
    /// Chooses which player the buttons control - by a rule you can state,
    /// not by the system's guess.
    /// </summary>
    /// <remarks>
    /// The rule is Master Audio Switcher's, ported whole, because it was
    /// measured against the alternative there: Windows hands the keys to
    /// whoever did something last, and with a browser and a music program
    /// both open the target hops between them every few seconds. Here: a
    /// nominated player wins outright; otherwise whoever is playing - and of
    /// several, the one these buttons drove last; otherwise the one they
    /// drove last even if silent; otherwise the system's pick.
    /// </remarks>
    private void Repick()
    {
        if (_gone || _sessions is null)
        {
            return;
        }

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> all = _sessions.GetSessions();

        foreach (GlobalSystemMediaTransportControlsSession s in all)
        {
            Seen[s.SourceAppUserModelId] = Short(s.SourceAppUserModelId);
        }

        Follow(Pick(all));
    }

    private GlobalSystemMediaTransportControlsSession? Pick(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> all)
    {
        if (all.Count == 0)
        {
            return null;
        }

        if (Nominated.Length > 0
            && all.FirstOrDefault(s => s.SourceAppUserModelId == Nominated) is { } chosen)
        {
            return chosen;
        }

        List<GlobalSystemMediaTransportControlsSession> playing = [.. all.Where(IsPlaying)];

        if (playing.Count > 0)
        {
            return playing.FirstOrDefault(s => s.SourceAppUserModelId == _lastApp) ?? playing[0];
        }

        if (all.FirstOrDefault(s => s.SourceAppUserModelId == _lastApp) is { } last)
        {
            return last;
        }

        return _sessions?.GetCurrentSession() ?? all[0];
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>"SpotifyAB.SpotifyMusic_...!Spotify" said the way a person would.</summary>
    private static string Short(string appId)
    {
        string name = appId;

        int bang = name.LastIndexOf('!');
        if (bang >= 0 && bang < name.Length - 1)
        {
            name = name[(bang + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        else if (name.Contains('.'))
        {
            name = name[(name.LastIndexOf('.') + 1)..];
        }

        return name.Length > 1 ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    private void Follow(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_gone)
        {
            return;
        }

        if (ReferenceEquals(session, _playing))
        {
            Refresh();
            return;
        }

        Forget();
        _playing = session;

        if (_playing is null)
        {
            Dim = 0.35;
            Title = string.Empty;
            Art = null;
            ArtVisible = Visibility.Collapsed;
            return;
        }

        _playing.PlaybackInfoChanged += OnPlaybackChanged;
        _playing.MediaPropertiesChanged += OnTrackChanged;

        Refresh();
        ReadTitle(_playing);
    }

    private void Forget()
    {
        if (_playing is null)
        {
            return;
        }

        _playing.PlaybackInfoChanged -= OnPlaybackChanged;
        _playing.MediaPropertiesChanged -= OnTrackChanged;
        _playing = null;
    }

    private void OnPlaybackChanged(
        GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        _ui.TryEnqueue(Refresh);

    private void OnTrackChanged(
        GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _ui.TryEnqueue(() => ReadTitle(sender));

    private void Refresh()
    {
        if (_playing is null || _gone)
        {
            return;
        }

        GlobalSystemMediaTransportControlsSessionPlaybackInfo? info = null;

        try
        {
            info = _playing.GetPlaybackInfo();
        }
        catch (Exception)
        {
            // The program that was playing has gone while we were asking.
        }

        bool playing = info?.PlaybackStatus
            == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        PlayShown = playing ? 0 : 1;
        PauseShown = playing ? 1 : 0;

        // Stopped and closed both mean there is nothing to control. Paused does
        // not: a paused track is one button away from playing again.
        Dim = info?.PlaybackStatus is
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
            or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing
            ? 1.0
            : 0.35;
    }

    private async void ReadTitle(GlobalSystemMediaTransportControlsSession session)
    {
        string title;
        Windows.Storage.Streams.IRandomAccessStreamReference? art = null;

        try
        {
            GlobalSystemMediaTransportControlsSessionMediaProperties properties =
                await session.TryGetMediaPropertiesAsync();

            title = properties.Artist.Length > 0
                ? $"{properties.Artist} — {properties.Title}"
                : properties.Title;

            art = properties.Thumbnail;
        }
        catch (Exception)
        {
            title = string.Empty;
        }

        if (_gone || !ReferenceEquals(session, _playing))
        {
            return;
        }

        Title = title;
        await ShowArt(session, art);
    }

    /// <summary>
    /// Puts the artwork next to the buttons, on the full-size bar.
    /// </summary>
    /// <remarks>
    /// The 20-pixel square is the now-playing cue: real artwork says what the
    /// buttons will act on without a single letter. A compact bar has no room
    /// for it, and a track with no artwork simply has none - a placeholder
    /// note glyph would be a picture of nothing.
    /// </remarks>
    private async Task ShowArt(
        GlobalSystemMediaTransportControlsSession session,
        Windows.Storage.Streams.IRandomAccessStreamReference? art)
    {
        if (art is null || Density == DockDensity.Compact)
        {
            Art = null;
            ArtVisible = Visibility.Collapsed;
            return;
        }

        try
        {
            using Windows.Storage.Streams.IRandomAccessStreamWithContentType stream =
                await art.OpenReadAsync();

            var image = new BitmapImage { DecodePixelHeight = 40 };
            await image.SetSourceAsync(stream);

            if (!_gone && ReferenceEquals(session, _playing))
            {
                Art = image;
                ArtVisible = Visibility.Visible;
            }
        }
        catch (Exception)
        {
            // The program that was playing withdrew the stream mid-read.
            ArtVisible = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Brings the playing program forward - the artwork is the door to it.
    /// </summary>
    /// <remarks>
    /// Through the shell's own apps folder, which resolves an AppUserModelId
    /// to whatever registered it - packaged or plain. A single-instance
    /// player asked to start again raises its window instead, which is
    /// exactly the wanted effect; a player this cannot reach simply does not
    /// come forward, and nothing else happens.
    /// </remarks>
    [RelayCommand]
    private void RaisePlayer()
    {
        if (_playing?.SourceAppUserModelId is not { Length: > 0 } app)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:AppsFolder\\" + app,
                UseShellExecute = false,
            });

            Context.Log.LogInformation("media.raise {App}", app);
        }
        catch (Exception e)
        {
            Context.Log.LogInformation(e, "media.raise failed");
        }
    }

    [RelayCommand]
    private void Previous() => Ask(s => s.TrySkipPreviousAsync());

    [RelayCommand]
    private void PlayPause()
    {
        // Starting the nominated player pauses everyone else first. Without
        // this a short video in a browser and the music simply play on top
        // of each other - the very thing nominating is there to end.
        if (Nominated.Length > 0
            && _playing is { } mine
            && mine.SourceAppUserModelId == Nominated
            && !IsPlaying(mine)
            && _sessions is not null)
        {
            foreach (GlobalSystemMediaTransportControlsSession other in _sessions.GetSessions())
            {
                if (other.SourceAppUserModelId != Nominated && IsPlaying(other))
                {
                    Hush(other);
                }
            }
        }

        Ask(s => s.TryTogglePlayPauseAsync());
    }

    private async void Hush(GlobalSystemMediaTransportControlsSession other)
    {
        try
        {
            await other.TryPauseAsync();
        }
        catch (Exception)
        {
            // A player that will not pause; its overlap is its own doing.
        }
    }

    [RelayCommand]
    private void Next() => Ask(s => s.TrySkipNextAsync());

    /// <summary>
    /// Asks the playing program to do something, and does not care if it will not.
    /// </summary>
    /// <remarks>
    /// A session may refuse any of the three - a radio stream has no previous
    /// track - and refusing is not a fault worth a message. The buttons stay
    /// where they are and nothing happens, which is what a person expects from
    /// a media key that a program ignores.
    /// </remarks>
    private async void Ask(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> what)
    {
        if (_playing is not { } session)
        {
            return;
        }

        try
        {
            await what(session);
            _lastApp = session.SourceAppUserModelId;
        }
        catch (Exception e)
        {
            Context.Log.LogInformation(e, "media.refused");
        }
    }
}
