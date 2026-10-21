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
    /// <remarks>
    /// The service runs as the system, so the files it runs from must be ones
    /// only an administrator can change. The program is usually installed for
    /// one person, into a folder that person can write to: a service pointed
    /// there let anything running as that person put its own code in the
    /// service's place and be run as the system at the next start. So the
    /// service is copied, at the one moment it has the rights to, into a
    /// folder of its own under Program Files, and registered from there.
    /// </remarks>
    internal static class ServiceInstaller
    {
        public const string ServiceName = "MasterControlDockSensors";

        /// <summary>Where the service runs from: writable by administrators only, like everything under Program Files.</summary>
        private static string Home => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MasterControlDock Sensors");

        public static int Install()
        {
            string own = Environment.ProcessPath ?? throw new InvalidOperationException("no own path");
            string from = Path.GetDirectoryName(own)!;
            string exe = Path.Combine(Home, Path.GetFileName(own));

            // Stopped first, or its files are held open and cannot be replaced.
            Sc($"stop {ServiceName}");

            if (!string.Equals(Path.GetFullPath(from), Path.GetFullPath(Home), StringComparison.OrdinalIgnoreCase))
            {
                Copy(from, Home);
            }

            // Quoted inside the argument: sc takes the outer quotes off, and a
            // path with a space left unquoted is a path Windows tries in
            // pieces - "C:\Program.exe" first.
            int created = Sc($"create {ServiceName} binPath= \"\\\"{exe}\\\"\" start= auto DisplayName= \"Master Control Dock Sensors\"");

            // Already there: repointed, so an upgrade or a service registered
            // by an older version from somewhere else ends up here.
            if (created == 1073)
            {
                created = Sc($"config {ServiceName} binPath= \"\\\"{exe}\\\"\" start= auto");
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
            int deleted = Sc($"delete {ServiceName}");

            try
            {
                // Not the folder this is running from, when it is that one:
                // Windows will not delete a running program.
                if (Directory.Exists(Home)
                    && !Path.GetFullPath(AppContext.BaseDirectory).StartsWith(Path.GetFullPath(Home), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(Home, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left behind is harmless: only administrators can change it.
            }

            return deleted;
        }

        /// <summary>Everything in one folder into another, replacing what is there.</summary>
        private static void Copy(string from, string to)
        {
            if (Directory.Exists(to))
            {
                Directory.Delete(to, recursive: true);
            }

            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
            }

            Directory.CreateDirectory(to);

            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
            }
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
