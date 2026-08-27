using Mcd.Core.Monitors;

namespace Mcd.App.Dock;

/// <summary>Someone asked for the settings from a bar.</summary>
/// <param name="Screen">
/// The screen the bar is on, so the window opens where the person is looking
/// rather than on the primary monitor.
/// </param>
/// <param name="WidgetId">
/// The widget whose own settings were asked for, or null for the dock's. The
/// bar is where a widget is pointed at; the settings are where it is set up,
/// and the two have to be joined by something.
/// </param>
public sealed record DockSettingsRequest(MonitorInfo Screen, string? WidgetId);
