using System.Collections.Immutable;
using System.Text.Json;
using Mcd.App.Widgets;
using Mcd.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Mcd.App.Dock;

/// <summary>
/// Editing the bar on the bar: a press on a widget opens its own settings and
/// a Remove button, a press on a free stretch opens the list of things that
/// can stand there. The settings window is still there for everything else.
/// </summary>
public sealed partial class DockWindow
{
    private bool _editing;

    /// <summary>Whether the bar is being edited.</summary>
    public bool Editing => _editing;

    /// <summary>Starts or ends editing this bar.</summary>
    public void Edit(bool on)
    {
        if (_editing == on)
        {
            return;
        }

        _editing = on;

        // While editing, a press must not also start the program or toggle the
        // mute: the widgets take no pointer, and the bar itself answers.
        Strip.IsHitTestVisible = !on;

        foreach (WidgetHost host in _hosts)
        {
            host.Outline(on);
        }

        ShowSlots(on);

        if (on)
        {
            Root.Tapped += OnEditTapped;
        }
        else
        {
            Root.Tapped -= OnEditTapped;
        }

        _log.LogInformation("dock.edit {State} monitor={Monitor}", on ? "on" : "off", Monitor.Identity.FriendlyName);
    }

    /// <summary>Opens the editor of the first widget and the list of additions, as presses do. For the self-test.</summary>
    public string Rehearse_Edit()
    {
        Edit(true);

        if (_hosts.FirstOrDefault(h => h.Widget.CreateEditor(_ => { }) is not null) is { } host)
        {
            EditWidget(host);
        }

        return $"{_hosts.Count} widgets, free slot {DockGrid.Free(_placed, _capacity, null).Order().FirstOrDefault()}";
    }

    public void Rehearse_Add() => AddWidget(DockGrid.Free(_placed, _capacity, null).Order().FirstOrDefault());

    private FlyoutPlacementMode Side => Config.Edge switch
    {
        Mcd.Interop.AppBar.AppBarEdge.Top => FlyoutPlacementMode.Bottom,
        Mcd.Interop.AppBar.AppBarEdge.Left => FlyoutPlacementMode.Right,
        Mcd.Interop.AppBar.AppBarEdge.Right => FlyoutPlacementMode.Left,
        _ => FlyoutPlacementMode.Top,
    };

    private void OnEditTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;

        int cell = CellAt(e.GetPosition(Bar));

        if (DockGrid.At(_placed, cell) is { } sitting
            && _hosts.FirstOrDefault(h => h.Entry.InstanceId == sitting.InstanceId) is { } host)
        {
            EditWidget(host);
        }
        else
        {
            AddWidget(cell);
        }
    }

    private Flyout NewFlyout(UIElement content)
    {
        return new Flyout
        {
            Content = content,
            Placement = Side,
            ShouldConstrainToRootBounds = false,
        };
    }

    private void EditWidget(WidgetHost host)
    {
        WidgetConfig entry = host.Entry;

        var panel = new StackPanel { Spacing = 10, MinWidth = 240, MaxWidth = 420 };

        panel.Children.Add(new TextBlock
        {
            Text = host.Widget.Called,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        FrameworkElement? editor = host.Widget.CreateEditor(config =>
        {
            ImmutableArray<WidgetConfig> next =
                [.. Config.Widgets.Select(w => w.InstanceId == entry.InstanceId ? w with { Config = config } : w)];

            Rearranged?.Invoke(this, next);
        });

        panel.Children.Add(editor ?? new TextBlock
        {
            Text = Loc.Tr("NothingToSetUp", "This widget has nothing to set up."),
            Opacity = 0.7,
        });

        var remove = new Button
        {
            Content = Loc.Tr("RemoveFromBar", "Remove from bar"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        Flyout flyout = NewFlyout(panel);
        flyout.XamlRoot = Root.XamlRoot;

        remove.Click += (_, _) =>
        {
            flyout.Hide();
            Rearranged?.Invoke(this, [.. Config.Widgets.Where(w => w.InstanceId != entry.InstanceId)]);
        };

        panel.Children.Add(remove);
        flyout.ShowAt(host);
    }

    private void AddWidget(int cell)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
            MaxHeight = 360,
            MinWidth = 260,
        };

        WidgetOffer[] offers = [.. WidgetCatalog.Offers(_sensors)];

        foreach (WidgetOffer offer in offers)
        {
            list.Items.Add(new TextBlock { Text = offer.Name, Tag = offer, Margin = new Thickness(0, 4, 0, 4) });
        }

        Flyout flyout = NewFlyout(list);
        flyout.XamlRoot = Root.XamlRoot;

        list.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is TextBlock { Tag: WidgetOffer chosen })
            {
                flyout.Hide();
                Place(chosen.Make(), cell);
            }
        };

        // Anchored on the bar at the pressed slot's place.
        flyout.ShowAt(Root, new FlyoutShowOptions
        {
            Position = CellRect(cell, 1) is var r && DockMetrics.IsHorizontal(Config.Edge)
                ? new Windows.Foundation.Point(r.X, 0)
                : new Windows.Foundation.Point(0, r.Y),
            Placement = Side,
        });
    }
}
