using System.Diagnostics;
using Mcd.SensorHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The service's three lives: installed and started by "--install" (run
// elevated, which is the one time rights are asked for), removed by
// "--uninstall", and otherwise run - as a Windows service when the service
// controller started it, as a console program when a person did.
switch (args.Length > 0 ? args[0] : string.Empty)
{
    case "--install":
        return ServiceInstaller.Install();

    case "--uninstall":
        return ServiceInstaller.Uninstall();
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = ServiceInstaller.ServiceName);
builder.Services.AddSingleton<Readings>();
builder.Services.AddHostedService<Reader>();
builder.Services.AddHostedService<PipeServer>();

builder.Logging.AddEventLog(settings => settings.SourceName = ServiceInstaller.ServiceName);

await builder.Build().RunAsync();
return 0;

namespace Mcd.SensorHost
{
    /// <summary>Puts the service in, or takes it out, with sc.exe - which is what an installer would do too.</summary>
    internal static class ServiceInstaller
    {
        public const string ServiceName = "MasterControlDockSensors";

        public static int Install()
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no own path");

            // Created, or repointed when it already exists: an upgrade lands
            // in the same folder, and the old entry has to follow.
            int created = Sc($"create {ServiceName} binPath= \"{exe}\" start= auto DisplayName= \"Master Control Dock Sensors\"");

            if (created == 1073)
            {
                Sc($"stop {ServiceName}");
                created = Sc($"config {ServiceName} binPath= \"{exe}\" start= auto");
            }

            if (created != 0)
            {
                return created;
            }

            Sc($"description {ServiceName} \"Reads the processor's and the memory's temperatures for Master Control Dock, through the PawnIO driver.\"");
            return Sc($"start {ServiceName}");
        }

        public static int Uninstall()
        {
            Sc($"stop {ServiceName}");
            return Sc($"delete {ServiceName}");
        }

        private static int Sc(string arguments)
        {
            using Process process = Process.Start(new ProcessStartInfo("sc.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("sc.exe did not start");

            process.WaitForExit();
            return process.ExitCode;
        }
    }
}
