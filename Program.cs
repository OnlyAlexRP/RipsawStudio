using System.Reflection;
using System.Runtime.Loader;
using RipsawStudio.UI;

namespace RipsawStudio;

internal static class Program
{
    /// <summary>
    /// The portable release keeps NAudio / Vortice / SharpGen DLLs inside their own
    /// subfolders instead of the root (see RipsawStudio.csproj's OrganizeReleaseFolder
    /// target). The default probing only looks next to the exe, so this teaches the
    /// runtime where else to look. It must be wired up before anything touches a type
    /// from those assemblies, hence it's the very first thing Main does.
    /// </summary>
    private static Assembly? ResolveFromSubfolders(AssemblyLoadContext context, AssemblyName name)
    {
        string[] subfolders = { "NAudio", "Vortice", "SharpGen" };
        foreach (string folder in subfolders)
        {
            string candidate = Path.Combine(AppContext.BaseDirectory, folder, name.Name + ".dll");
            if (File.Exists(candidate))
                return context.LoadFromAssemblyPath(candidate);
        }
        return null;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += ResolveFromSubfolders;

        if (args.Any(a => a.Equals("--diag", StringComparison.OrdinalIgnoreCase)))
        {
            RunDiagnostics();
            return;
        }

        int inspectAt = Array.FindIndex(args, a => a.Equals("--inspect", StringComparison.OrdinalIgnoreCase));
        if (inspectAt >= 0 && inspectAt + 1 < args.Length)
        {
            // Reads a recording back and writes what is in it next to it, for checking a file
            // from a script without opening a player.
            var thread = new Thread(() =>
            {
                Interop.MfHelpers.Startup();
                string file = args[inspectAt + 1];
                try { File.WriteAllText(file + ".txt", EncoderSelfTest.Inspect(file)); }
                catch (Exception ex) { File.WriteAllText(file + ".txt", "FAILED: " + ex.Message); }
                finally { Interop.MfHelpers.Shutdown(); }
            });
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            thread.Join();
            return;
        }

        int testAt = Array.FindIndex(args, a => a.Equals("--encoder-test", StringComparison.OrdinalIgnoreCase));
        if (testAt >= 0)
        {
            string folder = testAt + 1 < args.Length
                ? args[testAt + 1]
                : Path.Combine(Path.GetTempPath(), "RipsawStudio", "encoder-test");
            string report = EncoderSelfTest.Run(folder);
            if (!args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase)))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(report) { UseShellExecute = true });
            return;
        }

        // A blocking gen2 collection is tens of milliseconds - visible as a dropped frame.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(e.ExceptionObject as Exception, "A background thread failed");
        Application.ThreadException += (_, e) => Report(e.Exception, "Something went wrong");

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            Report(ex, "Ripsaw Studio could not start");
        }
    }

    /// <summary>Headless report, for when the window itself will not come up.</summary>
    private static void RunDiagnostics()
    {
        try
        {
            var settings = AppSettings.Load();
            using var engine = new Capture.CaptureEngine(settings.Adapter);
            string path = Diagnostics.WriteAsync(engine, settings).GetAwaiter().GetResult();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Report(ex, "Could not collect diagnostics");
        }
    }

    private static void Report(Exception? ex, string title)
    {
        if (ex is null) return;
        try
        {
            string log = Path.Combine(Path.GetDirectoryName(AppSettings.SettingsPath)!, "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTime.Now:u}  {ex}{Environment.NewLine}{Environment.NewLine}");
            MessageBox.Show($"{ex.Message}{Environment.NewLine}{Environment.NewLine}Details were written to:{Environment.NewLine}{log}",
                title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
