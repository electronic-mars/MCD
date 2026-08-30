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
    private readonly List<(SettingsModel Model, string What)> _undo = [];

    /// <summary>Whether there is anything to take back.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>What the next undo would take back, in words.</summary>
    public string? UndoWhat => _undo.Count > 0 ? _undo[^1].What : null;

    /// <summary>
    /// Puts the settings back to before the last change, if there was one.
    /// </summary>
    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        (SettingsModel model, string what) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        _log.LogInformation("settings.undone {What}", what);

        // Committed without a name of its own, so undoing does not itself go
        // on the stack and leave the button rocking between two states.
        Commit(model, WriteReason.UserAction);

        return true;
    }

    /// <summary>Replaces the settings and schedules a write.</summary>
    /// <param name="what">
    /// What the change is called, when it is one a person could want back.
    /// Null for the program's own housekeeping - a topology reconcile, a
    /// schema upgrade, a bar writing down where it settled its widgets - none
    /// of which anybody asked for and none of which they can undo.
    /// </param>
    public void Commit(SettingsModel model, WriteReason reason, string? what = null)
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
            _undo.Add((previous, what));

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
