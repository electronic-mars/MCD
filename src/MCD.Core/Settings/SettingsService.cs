using Microsoft.Extensions.Logging;

namespace Mcd.Core.Settings;

/// <summary>Why a settings write happened. Logged with every commit.</summary>
public enum WriteReason
{
    /// <summary>A person changed something.</summary>
    UserAction,

    /// <summary>The monitor registry genuinely changed - a screen appeared or went away.</summary>
    TopologyReconcile,

    /// <summary>A widget saved its own configuration.</summary>
    WidgetConfig,

    /// <summary>A schema upgrade on load.</summary>
    Migration,
}

/// <summary>
/// The only thing in the program that writes settings.
/// </summary>
/// <remarks>
/// <para>
/// Every commit carries a <see cref="WriteReason"/> and goes to the log. When a
/// user reports that their dock forgot itself, the log says what wrote and why -
/// which is precisely the evidence missing from the PowerToys reports.
/// </para>
/// <para>
/// Writes are coalesced: dragging a slider must not produce a hundred files.
/// The pending write is flushed on window close, WM_ENDSESSION and process exit.
/// </para>
/// </remarks>
public sealed class SettingsService : IDisposable
{
    private static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<SettingsService> _log;
    private readonly SettingsStore _store;
    private readonly Lock _gate = new();
    private readonly Timer _flushTimer;

    private SettingsModel _current;
    private bool _dirty;

    public SettingsService(ILogger<SettingsService> log, string? directory = null)
    {
        _log = log;
        _store = new SettingsStore(log, directory);
        _flushTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        SettingsModel loaded = _store.Load();
        _current = SettingsMigrations.Apply(loaded, log);

        // An upgraded file is written back, once, so the next start has nothing
        // to migrate and so the change is visible to anyone reading the file.
        if (_current != loaded)
        {
            _dirty = true;
            _flushTimer.Change(WriteDelay, Timeout.InfiniteTimeSpan);
        }

        _log.LogInformation(
            "settings.loaded schema={Schema} monitors={Monitors}",
            _current.SchemaVersion, _current.Monitors.Length);
    }

    /// <summary>The settings as they stand. Immutable, so readers need no lock.</summary>
    public SettingsModel Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<SettingsModel>? Changed;

    /// <summary>
    /// What can be taken back, oldest first.
    /// </summary>
    /// <remarks>
    /// Here rather than in the settings window, which is where it used to be
    /// and where it did not work: the window is built when it is opened and
    /// destroyed when it is closed, so the history went with it - and a widget
    /// dragged off a bar while the window was shut was never written down at
    /// all, which is exactly the change somebody wants back.
    /// </remarks>
    /// <remarks>
    /// Both sides of each change are kept, not only the one being put back.
    /// Undoing by restoring a whole snapshot also un-does everything that
    /// happened afterwards and was never on this stack - a screen coming back,
    /// a bar writing down where it settled - and none of that is anybody's to
    /// take away. Holding what the change produced as well lets the undo touch
    /// only the parts that are still as it left them.
    /// </remarks>
    private readonly List<Change> _undo = [];

    /// <summary>One change, as it was and as it left things.</summary>
    private readonly record struct Change(
        SettingsModel Before, SettingsModel After, string What, string? Where);

    /// <summary>Whether there is anything to take back.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>What the next undo would take back, in words.</summary>
    public string? UndoWhat => _undo.Count > 0 ? _undo[^1].What : null;

    /// <summary>
    /// The screen the next undo belongs to, when it belongs to one.
    /// </summary>
    /// <remarks>
    /// One button takes back the last change on any screen, which is right -
    /// a widget dragged off the second bar is wanted back from wherever the
    /// window happens to be. But it has to say so: pressing undo and watching
    /// nothing move, because the change was on a screen not being looked at,
    /// reads as a broken button.
    /// </remarks>
    public string? UndoWhere => _undo.Count > 0 ? _undo[^1].Where : null;

    /// <summary>
    /// Puts the settings back to before the last change, if there was one.
    /// </summary>
    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        Change change = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        _log.LogInformation("settings.undone {What}", change.What);

        // Committed without a name of its own, so undoing does not itself go
        // on the stack and leave the button rocking between two states.
        Commit(Rollback(Current, change), WriteReason.UserAction);

        return true;
    }

    /// <summary>Replaces the settings and schedules a write.</summary>
    /// <param name="what">
    /// What the change is called, when it is one a person could want back.
    /// Null for the program's own housekeeping - a topology reconcile, a
    /// schema upgrade, a bar writing down where it settled its widgets - none
    /// of which anybody asked for and none of which they can undo.
    /// </param>
    /// <param name="where">
    /// The screen it happened on, when it happened on one - so the button can
    /// say whose change it is about to take back.
    /// </param>
    public void Commit(
        SettingsModel model, WriteReason reason, string? what = null, string? where = null)
    {
        SettingsModel previous;

        lock (_gate)
        {
            if (model == _current)
            {
                return;
            }

            previous = _current;
            _current = model;
            _dirty = true;
            _flushTimer.Change(WriteDelay, Timeout.InfiniteTimeSpan);
        }

        // Only a change that actually happened, and only one somebody could
        // ask for back.
        if (what is not null)
        {
            _undo.Add(new Change(previous, model, what, where));

            // Twenty-five is plenty, and unbounded is a session's worth of
            // settings held for a button nobody presses twenty-six times.
            while (_undo.Count > 25)
            {
                _undo.RemoveAt(0);
            }
        }

        _log.LogInformation("settings.commit reason={Reason}", reason);
        Changed?.Invoke(this, model);
    }

    /// <summary>
    /// The settings as they are, with the parts one change touched put back.
    /// </summary>
    /// <remarks>
    /// Part by part, and only where the part is still exactly as the change
    /// left it. A screen that has been unplugged since, a bar that has written
    /// down where it settled its widgets, a widget's own option changed
    /// afterwards - none of those were what the button offered to take back,
    /// and none of them are touched.
    /// </remarks>
    private static SettingsModel Rollback(SettingsModel now, Change change)
    {
        SettingsModel was = change.Before;
        SettingsModel then = change.After;

        // The screens themselves. A monitor still as the change left it goes
        // back to how it was; one that has since been added or taken away by
        // the reconciler is left alone, in the order this machine has now.
        var before = was.Monitors.ToDictionary(m => m.StableId);
        var after = then.Monitors.ToDictionary(m => m.StableId);

        var monitors = new List<MonitorConfig>();

        foreach (MonitorConfig mine in now.Monitors)
        {
            monitors.Add(
                after.TryGetValue(mine.StableId, out MonitorConfig? left) && left == mine
                && before.TryGetValue(mine.StableId, out MonitorConfig? original)
                    ? original
                    : mine);
        }

        // A screen the change itself removed - "forget this screen" - comes
        // back where the rest of them leave room for it.
        foreach (MonitorConfig lost in was.Monitors)
        {
            if (!after.ContainsKey(lost.StableId)
                && !monitors.Any(m => m.StableId == lost.StableId))
            {
                monitors.Add(lost);
            }
        }

        return now with
        {
            App = now.App == then.App ? was.App : now.App,
            Sensors = now.Sensors == then.Sensors ? was.Sensors : now.Sensors,
            Monitors = [.. monitors],
        };
    }

    /// <summary>Writes a copy of the settings where somebody asks for it.</summary>
    public void Export(string path)
    {
        _store.Export(path, Current);
        _log.LogInformation("settings.exported to={Path}", path);
    }

    /// <summary>
    /// Takes the settings from a file, fitting them to this machine's screens.
    /// </summary>
    /// <remarks>
    /// The screens are the whole difficulty. A bar belongs to a screen by an
    /// identity built from the hardware, so the same file on another machine
    /// describes bars for screens that are not there - which is why copying
    /// config.json across has always arrived with every layout missing. The
    /// screens here keep their identities and take the arrangements in the
    /// order they are listed: the first bar in the file lands on the first
    /// screen. Everything that is not about a particular screen - the theme,
    /// the keys, the icons, what the readings are called - comes across whole.
    /// </remarks>
    /// <returns>How many bars were filled in, or null if the file is not one.</returns>
    public int? Import(string path, string what)
    {
        if (_store.Import(path) is not { } incoming)
        {
            return null;
        }

        SettingsModel theirs = SettingsMigrations.Apply(incoming, _log);
        SettingsModel here = Current;

        int filled = Math.Min(theirs.Monitors.Length, here.Monitors.Length);

        SettingsModel next = theirs with
        {
            Monitors =
            [
                .. here.Monitors.Select((mine, i) => i < filled
                    ? theirs.Monitors[i] with
                    {
                        StableId = mine.StableId,
                        FriendlyName = mine.FriendlyName,
                    }
                    : mine),
            ],
        };

        Commit(next, WriteReason.UserAction, what);

        _log.LogInformation(
            "settings.imported from={Path} bars={Bars} of={Of}",
            path, filled, here.Monitors.Length);

        return filled;
    }

    /// <summary>Writes any pending change out now.</summary>
    public void Flush()
    {
        SettingsModel snapshot;

        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            snapshot = _current;
        }

        try
        {
            _store.Save(snapshot);
            _log.LogDebug("settings.saved monitors={Monitors}", snapshot.Monitors.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keep the change in memory and try again on the next commit. Losing
            // the session's settings is better than taking the dock down.
            lock (_gate)
            {
                _dirty = true;
            }

            _log.LogError(e, "settings.save failed");
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        Flush();
    }
}
