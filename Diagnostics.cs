using System.Text;
using NAudio.CoreAudioApi;
using RipsawStudio.Audio;
using RipsawStudio.Capture;

namespace RipsawStudio;

/// <summary>
/// Writes everything worth knowing about this machine's capture setup to one text file.
/// Exists so that reporting a problem is a single click instead of a scavenger hunt.
/// </summary>
public static class Diagnostics
{
    public static string FilePath => Path.Combine(
        Path.GetDirectoryName(AppSettings.SettingsPath)!, "diagnostics.txt");

    public static async Task<string> WriteAsync(CaptureEngine engine, AppSettings settings)
    {
        string report = await BuildAsync(engine, settings);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, report);
        return FilePath;
    }

    private static async Task<string> BuildAsync(CaptureEngine engine, AppSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ripsaw Studio diagnostics");
        sb.AppendLine("=========================");
        sb.AppendLine($"Written        {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"App version    {typeof(Diagnostics).Assembly.GetName().Version}");
        sb.AppendLine($"Windows        {Environment.OSVersion.VersionString}");
        sb.AppendLine($".NET           {Environment.Version}");
        sb.AppendLine($"Machine        {Environment.ProcessorCount} logical cores, 64-bit process: {Environment.Is64BitProcess}");
        sb.AppendLine();

        try
        {
            sb.AppendLine($"Graphics       {await engine.DescribeAdapterAsync()}");
            sb.AppendLine($"Pacing         {await engine.DescribePacingAsync()}");
        }
        catch (Exception ex) { sb.AppendLine($"Graphics       FAILED: {ex.Message}"); }
        sb.AppendLine();

        sb.AppendLine("Live state");
        sb.AppendLine("----------");
        sb.AppendLine($"Streaming      {engine.IsStreaming}");
        sb.AppendLine($"Recording      {engine.IsRecording}");
        sb.AppendLine($"Audio running  {engine.Audio.IsRunning}");
        if (engine.Audio.CaptureFormat is { } format)
            sb.AppendLine($"Audio format   {format.SampleRate} Hz, {format.Channels} ch, {format.BitsPerSample}-bit {format.Encoding}");
        sb.AppendLine($"Audio buffered {engine.Audio.BufferedMs:0} ms");
        sb.AppendLine($"Microphone     {(engine.Audio.Mic.Wanted ? engine.Audio.MicRunning ? "running" : "asked for, not running" : "off")}");
        sb.AppendLine($"Replay buffer  {(engine.IsReplayArmed ? $"armed, {engine.ReplayBufferedSeconds:0} s buffered, " + (engine.ReplayHasAudio ? "with sound" : "NO SOUND BUFFERED - a clip saved now would be silent") : "not armed")}");
        sb.AppendLine($"Profile        {settings.ActiveProfile} (of {settings.Profiles.Count})");
        sb.AppendLine();

        await AppendVideoDevices(sb, engine);
        AppendAudioDevices(sb);
        AppendSettings(sb, settings);
        AppendCrashLog(sb);
        return sb.ToString();
    }

    private static async Task AppendVideoDevices(StringBuilder sb, CaptureEngine engine)
    {
        sb.AppendLine("Capture devices");
        sb.AppendLine("---------------");
        List<VideoDeviceInfo> devices;
        try
        {
            devices = await engine.EnumerateDevicesAsync();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"FAILED to enumerate: {ex.Message}");
            sb.AppendLine();
            return;
        }

        if (devices.Count == 0) sb.AppendLine("(none found)");

        foreach (var device in devices)
        {
            sb.AppendLine($"* {device.Name}");
            sb.AppendLine($"  {device.SymbolicLink}");

            if (engine.IsStreaming)
            {
                sb.AppendLine("  Formats not re-read: the preview is running and the card allows one reader.");
                sb.AppendLine("  Stop the preview and save diagnostics again for the full list.");
                continue;
            }

            try
            {
                var formats = await engine.QueryFormatsAsync(device);
                sb.AppendLine($"  {formats.Count} formats:");
                foreach (var f in formats)
                    sb.AppendLine($"    {f.Width,5} x {f.Height,-5} {f.Fps,6:0.##} Hz  {f.SubtypeName}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  FAILED to open: {ex.Message}");
            }
        }
        sb.AppendLine();
    }

    private static void AppendAudioDevices(StringBuilder sb)
    {
        sb.AppendLine("Audio endpoints");
        sb.AppendLine("---------------");
        foreach (var (flow, title) in new[] { (DataFlow.Capture, "Inputs"), (DataFlow.Render, "Outputs") })
        {
            sb.AppendLine(title + ":");
            try
            {
                var devices = AudioMonitor.Enumerate(flow);
                if (devices.Count == 0) sb.AppendLine("  (none active)");
                foreach (var device in devices) sb.AppendLine($"  {device.Name}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  FAILED: {ex.Message}");
            }
        }
        sb.AppendLine();
    }

    private static void AppendSettings(StringBuilder sb, AppSettings settings)
    {
        sb.AppendLine("Settings");
        sb.AppendLine("--------");
        try
        {
            sb.AppendLine(File.Exists(AppSettings.SettingsPath)
                ? File.ReadAllText(AppSettings.SettingsPath)
                : "(no settings file yet)");
        }
        catch (Exception ex) { sb.AppendLine($"FAILED to read: {ex.Message}"); }
        sb.AppendLine();
    }

    private static void AppendCrashLog(StringBuilder sb)
    {
        sb.AppendLine("Recent crash log");
        sb.AppendLine("----------------");
        string log = Path.Combine(Path.GetDirectoryName(AppSettings.SettingsPath)!, "crash.log");
        try
        {
            if (!File.Exists(log)) { sb.AppendLine("(none - good)"); return; }
            var lines = File.ReadAllLines(log);
            foreach (var line in lines.TakeLast(80)) sb.AppendLine(line);
        }
        catch (Exception ex) { sb.AppendLine($"FAILED to read: {ex.Message}"); }
    }
}
