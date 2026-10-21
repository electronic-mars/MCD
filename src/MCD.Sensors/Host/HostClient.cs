using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mcd.Sensors.Host;

/// <summary>
/// The bar's end of the pipe to the sensor service.
/// </summary>
/// <remarks>
/// <para>
/// One client for the whole program, shared by the providers that read the
/// processor and the memory from it. It connects when asked and reads lines
/// on a thread of its own, keeping only the latest; a provider looks at that
/// and never waits on the pipe.
/// </para>
/// <para>
/// When the service is not there the connect fails inside half a second and
/// the client says so; the hub asks again every half minute, which is how
/// a service installed while the bar is up is found without a restart.
/// </para>
/// </remarks>
public sealed class HostClient(ILogger<HostClient> log) : IDisposable
{
    private readonly Lock _gate = new();
    private NamedPipeClientStream? _pipe;
    private HostSnapshot _latest = HostSnapshot.Empty;
    private long _heard;
    private bool _saidMissing;

    /// <summary>The last snapshot the service sent, or the empty one.</summary>
    public HostSnapshot Latest => Volatile.Read(ref _latest);

    /// <summary>Whether a snapshot arrived within the last few seconds.</summary>
    public bool Connected =>
        _pipe is { IsConnected: true } && Environment.TickCount64 - Volatile.Read(ref _heard) < 5000;

    /// <summary>
    /// Connects if not connected, and waits briefly for the first snapshot.
    /// </summary>
    /// <returns>True when a snapshot is in hand.</returns>
    public bool EnsureConnected()
    {
        lock (_gate)
        {
            if (Connected)
            {
                return true;
            }

            _pipe?.Dispose();
            _pipe = null;

            // Identification only: whoever is at the other end may learn who
            // is asking, and may not act as them.
            var pipe = new NamedPipeClientStream(
                ".", HostSnapshot.PipeName, PipeDirection.In, PipeOptions.None,
                System.Security.Principal.TokenImpersonationLevel.Identification);

            try
            {
                pipe.Connect(500);
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
            {
                pipe.Dispose();

                if (!_saidMissing)
                {
                    _saidMissing = true;
                    log.LogInformation("sensors.host the sensor service is not running");
                }

                return false;
            }

            // The service runs in session nought, where no signed-in person's
            // programs run. Anything else holding this name - which any
            // program can take while the service is not there, the ordinary
            // case on a machine without it - would be feeding the bar its
            // own figures.
            if (!GetNamedPipeServerSessionId(pipe.SafePipeHandle, out uint session) || session != 0)
            {
                pipe.Dispose();
                log.LogWarning("sensors.host the pipe is held by something that is not the sensor service");
                return false;
            }

            _pipe = pipe;
            _saidMissing = false;

            var reader = new Thread(() => Read(pipe))
            {
                IsBackground = true,
                Name = "mcd-sensor-host",
            };

            reader.Start();
        }

        // The first line comes within a second; a provider discovering its
        // sensors needs it now rather than on the next probe.
        for (int waited = 0; waited < 30 && !Connected; waited++)
        {
            Thread.Sleep(50);
        }

        return Connected;
    }

    private void Read(NamedPipeClientStream pipe)
    {
        try
        {
            using var text = new StreamReader(pipe);

            while (Line(text) is { } line)
            {
                // A reading the service is still sending but no longer
                // taking is not heard: the source goes quiet and is looked
                // for again, instead of showing old figures as new.
                if (JsonSerializer.Deserialize(line, HostJson.Default.HostSnapshot) is { Stale: false } snapshot)
                {
                    Volatile.Write(ref _latest, snapshot);
                    Volatile.Write(ref _heard, Environment.TickCount64);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or JsonException or InvalidDataException)
        {
            // The service went away, the pipe was closed under us, or what
            // came down it was not a snapshot.
        }

        log.LogInformation("sensors.host the sensor service went quiet");
    }

    /// <summary>One line, no longer than a snapshot can be. A line without end ran the bar out of memory.</summary>
    private static string? Line(StreamReader text)
    {
        var line = new System.Text.StringBuilder();

        for (int c = text.Read(); c >= 0; c = text.Read())
        {
            if (c == '\n')
            {
                return line.ToString();
            }

            if (line.Length >= 64 * 1024)
            {
                throw new InvalidDataException("a line longer than any snapshot");
            }

            line.Append((char)c);
        }

        return line.Length > 0 ? line.ToString() : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint session);

    public void Dispose()
    {
        lock (_gate)
        {
            _pipe?.Dispose();
            _pipe = null;
        }
    }
}
