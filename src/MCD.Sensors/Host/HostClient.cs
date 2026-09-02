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

            var pipe = new NamedPipeClientStream(".", HostSnapshot.PipeName, PipeDirection.In);

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

            while (text.ReadLine() is { } line)
            {
                if (JsonSerializer.Deserialize(line, HostJson.Default.HostSnapshot) is { } snapshot)
                {
                    Volatile.Write(ref _latest, snapshot);
                    Volatile.Write(ref _heard, Environment.TickCount64);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or JsonException)
        {
            // The service went away, or the pipe was closed under us.
        }

        log.LogInformation("sensors.host the sensor service went quiet");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pipe?.Dispose();
            _pipe = null;
        }
    }
}
