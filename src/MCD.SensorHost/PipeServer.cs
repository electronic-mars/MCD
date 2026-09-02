using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Mcd.Sensors.Host;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mcd.SensorHost;

/// <summary>
/// Hands the latest snapshot to every bar that asks, once a second, as one
/// line of JSON per second down a named pipe.
/// </summary>
/// <remarks>
/// The pipe is the only door: the service listens on nothing else and takes
/// no commands. Any signed-in user may open it - the figures are not a
/// secret, and the bar runs as whoever is signed in.
/// </remarks>
public sealed class PipeServer(Readings readings, ILogger<PipeServer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;

            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    HostSnapshot.PipeName,
                    PipeDirection.Out,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    security);

                await pipe.WaitForConnectionAsync(stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException e)
            {
                log.LogWarning(e, "the pipe could not be opened; trying again");
                await Task.Delay(TimeSpan.FromSeconds(2), stopping);
                continue;
            }

            _ = Serve(pipe, stopping);
        }
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        using (pipe)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

                do
                {
                    string line = JsonSerializer.Serialize(readings.Latest, HostJson.Default.HostSnapshot) + "\n";
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes(line), stopping);
                    await pipe.FlushAsync(stopping);
                }
                while (await timer.WaitForNextTickAsync(stopping));
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The bar closed its end, or the service is stopping. Either
                // way this client is done.
            }
        }
    }
}
