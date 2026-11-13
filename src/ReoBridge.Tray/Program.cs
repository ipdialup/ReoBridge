using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace ReoBridge.Tray;

internal static class Program
{
    public const string ServiceName = "ReoBridge";

    public static string DefaultConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ReoBridge", "reobridge.json");

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Elevated helper modes, started by the tray itself through "runas".
        if (args.Length > 0 && args[0] == "--edit-config")
        {
            Application.Run(new ConfigEditorForm(args.Length > 1 ? args[1] : DefaultConfigPath));
            return 0;
        }
        if (args.Length > 0 && args[0] == "--restart-service")
            return RestartService(showResult: true) ? 0 : 1;

        var settings = TraySettings.Load();
        int urlArg = Array.IndexOf(args, "--url");
        if (urlArg >= 0 && urlArg + 1 < args.Length)
        {
            settings.StatusUrl = TraySettings.Normalise(args[urlArg + 1]);
            settings.Save();
        }

        // Running it again while the tray is up opens the status window of the one already running.
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ReoBridge.Tray.Show");
        using var single = new Mutex(true, @"Local\ReoBridge.Tray", out bool first);
        if (!first)
        {
            showSignal.Set();
            return 0;
        }
        Application.Run(new TrayApp(settings, showSignal, showNow: args.Contains("--show")));
        return 0;
    }

    /// <summary>Runs this exe again as administrator with the given arguments. False if the user said no.</summary>
    public static bool RunElevated(string arguments, bool wait = false)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" });
            if (wait) p?.WaitForExit();
            return p is null || !wait || p.ExitCode == 0;
        }
        catch (Win32Exception) { return false; } // UAC prompt cancelled
    }

    public static bool RestartService(bool showResult)
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            return true;
        }
        catch (Exception ex)
        {
            if (showResult)
                MessageBox.Show($"Could not restart the ReoBridge service:\n\n{ex.Message}", "ReoBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
}
