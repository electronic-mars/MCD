namespace Mcd.Core.Infrastructure;

/// <summary>
/// How a second launch talks to the copy that is already running.
/// </summary>
/// <remarks>
/// <para>
/// The program must not be killed: the AppBar registrations would stay behind
/// and the desktop work area would remain short until the next sign-out. An
/// installer, an uninstaller, an updater and the build scripts all need a way to
/// say "please stop" and wait.
/// </para>
/// <para>
/// The second signal is what makes the shortcut behave the way people expect. A
/// program with no window and no tray icon has to answer somehow when its icon
/// is double-clicked a second time; here it brings up its settings. It is also
/// the way back in if every dock has been switched off.
/// </para>
/// <para>
/// Local, not Global. A standard user cannot create objects in the Global
/// namespace, and these only ever have to reach a process in the same session.
/// The default security descriptor already gives the creating user full access;
/// setting one by hand only creates ways to lock ourselves out - which is
/// exactly what happened the first time this was written.
/// </para>
/// </remarks>
public sealed class InstanceSignal(string name) : IDisposable
{
    public static InstanceSignal Shutdown => new("Local\\MasterControlDockShutdown");

    public static InstanceSignal ShowSettings => new("Local\\MasterControlDockShowSettings");

    private EventWaitHandle? _listener;

    /// <summary>
    /// Starts listening. Only the running copy does this.
    /// </summary>
    /// <remarks>
    /// Cleared as it is picked up. The constructor opens an event of this name
    /// if one already exists rather than making a fresh one, and an event left
    /// standing by a copy that has since gone would fire the moment this one
    /// began waiting - so a new dock would shut itself down on startup, for a
    /// request meant for a program that no longer exists.
    /// </remarks>
    public EventWaitHandle Listen()
    {
        if (_listener is not null)
        {
            return _listener;
        }

        _listener = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, name);
        _listener.Reset();
        return _listener;
    }

    /// <summary>Raises the signal. False when nothing is listening.</summary>
    public bool Raise()
    {
        if (!EventWaitHandle.TryOpenExisting(name, out EventWaitHandle? handle))
        {
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }
}
