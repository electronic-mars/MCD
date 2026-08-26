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

    private void OnThemeChanged(FrameworkElement sender, object args) =>
        _always.Theme = Match(sender.ActualTheme);

    private static SystemBackdropTheme Match(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => SystemBackdropTheme.Light,
        ElementTheme.Dark => SystemBackdropTheme.Dark,
        _ => SystemBackdropTheme.Default,
    };
}
