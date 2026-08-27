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
    private bool _gone;

    public override string TypeId => Type;

    /// <summary>Whether the name of the track is written on the bar itself.</summary>
    /// <remarks>
    /// Off unless asked for. The name is always in the tooltip, and a bar is
    /// glanced at, not read - a scrolling track title next to three buttons
    /// makes the whole cluster look busier than anything else on it.
    /// </remarks>
    private bool ShowTitle => WidgetOptions.Text(Options, "title") == "shown";

    /// <summary>Hidden until something is playing.</summary>
    [ObservableProperty]
    public partial Visibility Visible { get; set; } = Visibility.Collapsed;

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

    /// <summary>The accent tick under the middle button while sound is playing.</summary>
    [ObservableProperty]
    public partial Visibility PlayingVisible { get; set; } = Visibility.Collapsed;

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
        Spacing = LoadWidget.Gap(Orientation);
        TitleVisible = ShowTitle && Density == DockDensity.Default
            ? Visibility.Visible
            : Visibility.Collapsed;

        Listen();
    }

    /// <summary>Nothing here follows the readings; it follows what is playing.</summary>
    public override void Tick(SensorSnapshot snapshot)
    {
    }

    public override string Summarise() => ShowTitle
        ? Loc.Tr("MediaSummaryShown", "Buttons, with the track written next to them")
        : Loc.Tr("MediaSummaryTooltip", "Buttons; the track is in their tooltip");

    public override FrameworkElement CreateEditor(Action<JsonElement?> changed)
    {
        var title = new ToggleSwitch
        {
            Header = Loc.Tr("MediaTitleHeader", "Write the track on the bar"),
            OnContent = Loc.Tr("MediaTitleOn", "Written next to the buttons"),
            OffContent = Loc.Tr("MediaTitleOff", "Only in the tooltip"),
            IsOn = ShowTitle,
        };

        title.Toggled += (_, _) =>
            changed(WidgetOptions.Merge(Options, ("title", title.IsOn ? "shown" : null)));

        return title;
    }

    public override void Dispose()
    {
        _gone = true;
        Forget();

        if (_sessions is not null)
        {
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

        _sessions.CurrentSessionChanged += OnSessionChanged;
        Follow(_sessions.GetCurrentSession());
    }

    private void OnSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        _ui.TryEnqueue(() => Follow(sender.GetCurrentSession()));

    private void Follow(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_gone)
        {
            return;
        }

        Forget();
        _playing = session;

        if (_playing is null)
        {
            Visible = Visibility.Collapsed;
            Title = string.Empty;
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
        PlayingVisible = playing ? Visibility.Visible : Visibility.Collapsed;

        // Stopped and closed both mean there is nothing to control. Paused does
        // not: a paused track is one button away from playing again.
        Visible = info?.PlaybackStatus is
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused
            or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing
            ? Visibility.Visible
            : Visibility.Collapsed;
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

    [RelayCommand]
    private void Previous() => Ask(s => s.TrySkipPreviousAsync());

    [RelayCommand]
    private void PlayPause() => Ask(s => s.TryTogglePlayPauseAsync());

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
        }
        catch (Exception e)
        {
            Context.Log.LogInformation(e, "media.refused");
        }
    }
}
