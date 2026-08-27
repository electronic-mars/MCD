using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mcd.App.Dock;

/// <summary>
/// The bar's background: acrylic, in its thin form.
/// </summary>
/// <remarks>
/// <para>
/// Thin rather than the ordinary base acrylic, which is what the PowerToys dock
/// uses and what makes a bar sitting on the screen edge read as part of the
/// desktop instead of as a window lying on top of it. The XAML
/// <c>DesktopAcrylicBackdrop</c> has no say in which kind it gets, so the
/// controller is driven directly.
/// </para>
/// <para>
/// Adapted in approach from microsoft/PowerToys,
/// src/modules/cmdpal/Microsoft.CmdPal.UI/Dock/DockWindow.xaml.cs (MIT).
/// Copyright (c) Microsoft Corporation. See licenses/PowerToys-MIT.txt.
/// </para>
/// </remarks>
public sealed class ThinAcrylicBackdrop : SystemBackdrop
{
    private readonly Windows.UI.Color? _tint;
    private readonly float? _opacity;

    public ThinAcrylicBackdrop()
    {
    }

    /// <summary>
    /// Acrylic tinted with the person's own colour.
    /// </summary>
    /// <remarks>
    /// This is what "a colour of my own" is made of. A plain brush with alpha
    /// composits against the window's own opaque surface - against black, not
    /// against the desktop - so a translucent colour has to be an acrylic
    /// tint, where the compositor blends with what is really behind the bar.
    /// </remarks>
    public ThinAcrylicBackdrop(Windows.UI.Color tint, float opacity)
    {
        _tint = tint;
        _opacity = Math.Clamp(opacity, 0.05f, 1f);
    }
    /// <summary>
    /// Always "active", whatever the window is doing.
    /// </summary>
    /// <remarks>
    /// The configuration the framework hands out ties the backdrop to window
    /// activation: an unfocused window loses its acrylic and falls back to a
    /// flat colour. That is right for an ordinary window and wrong for a bar
    /// that is never focused by design - it spends its whole life inactive, so
    /// it would spend its whole life as a grey rectangle, turning briefly
    /// translucent on a click and going grey again the moment the pointer left.
    /// </remarks>
    private readonly SystemBackdropConfiguration _always = new() { IsInputActive = true };

    private DesktopAcrylicController? _controller;
    private FrameworkElement? _watchingTheme;

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);

        _controller ??= new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };

        if (_tint is { } tint && _opacity is { } opacity)
        {
            _controller.TintColor = tint;
            _controller.TintOpacity = opacity;
            _controller.FallbackColor = tint;
        }

        if (xamlRoot.Content is FrameworkElement content)
        {
            _watchingTheme = content;
            _watchingTheme.ActualThemeChanged += OnThemeChanged;
            _always.Theme = Match(content.ActualTheme);
        }

        _controller.SetSystemBackdropConfiguration(_always);
        _controller.AddSystemBackdropTarget(target);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);

        if (_watchingTheme is not null)
        {
            _watchingTheme.ActualThemeChanged -= OnThemeChanged;
            _watchingTheme = null;
        }

        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
    }

    /// <summary>
    /// Ignores the framework's own configuration changing.
    /// </summary>
    /// <remarks>
    /// Deliberately does not call the base. This bar supplies its own
    /// configuration - always active, whatever the window is doing - so the
    /// default one is not in use here, and passing the change on makes the
    /// framework raise "the parameter is incorrect" for a target it was never
    /// handed. That exception arrives on the interface thread and ends the
    /// process, which is what was killing the docks at random: the notification
    /// fires when a window is activated or the theme changes, so it depended
    /// entirely on what the person happened to click next.
    /// </remarks>
    protected override void OnDefaultSystemBackdropConfigurationChanged(
        ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
    }

    private void OnThemeChanged(FrameworkElement sender, object args) =>
        _always.Theme = Match(sender.ActualTheme);

    private static SystemBackdropTheme Match(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => SystemBackdropTheme.Light,
        ElementTheme.Dark => SystemBackdropTheme.Dark,
        _ => SystemBackdropTheme.Default,
    };
}
