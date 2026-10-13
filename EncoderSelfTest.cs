using System.Diagnostics;
using System.Text;
using NAudio.Wave;
using RipsawStudio.Interop;
using RipsawStudio.Record;
using RipsawStudio.Render;

namespace RipsawStudio;

/// <summary>
/// Encodes a few seconds of synthetic frames with every encoder this machine offers and reads
/// each file back, so "does HEVC on the Intel GPU actually work here" has a measured answer
/// rather than a guess. Also runs one instant replay end to end. Needs no capture card.
/// Run with --encoder-test, or from the Diagnostics section.
/// </summary>
internal static class EncoderSelfTest
{
    private const int Width = 1920, Height = 1080, Fps = 60, Seconds = 3;

    public static string Run(string folder)
    {
        var report = new StringBuilder();
        report.AppendLine($"Ripsaw Studio encoder test ({DateTime.Now:u})");
        report.AppendLine($"Frames: synthetic {Width}x{Height} NV12 at {Fps} fps, {Seconds} s per file, on the GPU");
        report.AppendLine();
        Directory.CreateDirectory(folder);

        var thread = new Thread(() =>
        {
            MfHelpers.Startup();
            try { RunCore(folder, report); }
            catch (Exception ex) { report.AppendLine("TEST ABORTED: " + ex); }
            finally { MfHelpers.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();

        string path = Path.Combine(folder, "encoder-test.txt");
        File.WriteAllText(path, report.ToString());
        return path;
    }

    private static void RunCore(string folder, StringBuilder report)
    {
        var encoders = EncoderCatalog.Enumerate();
        report.AppendLine("GPUs:");
        foreach (var (luid, name) in EncoderCatalog.AdapterNamesByLuid())
            report.AppendLine($"  {name}  luid=0x{luid:X16}");
        report.AppendLine("Hardware encoder transforms as Windows lists them:");
        foreach (var subtype in new[] { Mf.MFVideoFormat_H264, Mf.MFVideoFormat_HEVC })
            foreach (var (name, vendor, luid) in EncoderCatalog.Find(subtype, Mf.MFT_ENUM_FLAG_HARDWARE | Mf.MFT_ENUM_FLAG_SORTANDFILTER))
                report.AppendLine($"  {Mf.DescribeSubtype(subtype)}  {name}  vendor={vendor ?? "-"}  luid={(luid is { } l ? $"0x{l:X16}" : "-")}");
        report.AppendLine();
        report.AppendLine("Encoders found:");
        foreach (var e in encoders)
            report.AppendLine($"  {e.Codec,-5} {(e.Hardware ? "GPU" : "CPU"),-4} {e.Label,-50} [{e.FriendlyName}]  id={e.Id}");
        report.AppendLine();

        string frameAdapter = Render.Presenter.ListAdapters().FirstOrDefault() ?? "";
        report.AppendLine($"Frames are created on: {frameAdapter}");
        report.AppendLine();
        using var frames = new EncoderDevice(frameAdapter);
        var others = new Dictionary<string, EncoderDevice>(StringComparer.OrdinalIgnoreCase);

        EncoderPlan PlanFor(EncoderInfo e, VideoCodec codec)
        {
            if (!e.Hardware) return EncoderPlan.Software(codec);
            if (EncoderCatalog.Same(e.AdapterName, frameAdapter))
                return new EncoderPlan(codec, true, frames.DeviceManager, false, e.Label);
            if (!others.TryGetValue(e.AdapterName!, out var device))
                others[e.AdapterName!] = device = new EncoderDevice(e.AdapterName!);
            return new EncoderPlan(codec, true, device.DeviceManager, true, e.Label + " (copied across GPUs)");
        }

        var samples = BuildFrames(frames);
        try
        {
            int n = 0;
            foreach (var e in encoders)
            {
                var cases = new List<(string Name, RecordSettings Settings, ContainerFormat Container, int AudioRate)>
                {
                    ("CBR 20 Mbps, fMP4", new RecordSettings { Codec = e.Codec, VideoBitrateKbps = 20_000 }, ContainerFormat.FragmentedMp4, 48000),
                };
                if (e.Codec == VideoCodec.Hevc)
                    cases.Add(("CBR 20 Mbps, MP4", new RecordSettings { Codec = e.Codec, VideoBitrateKbps = 20_000 }, ContainerFormat.Mp4, 48000));
                if (e.Codec == VideoCodec.H264 && (e.Hardware ? EncoderCatalog.Same(e.AdapterName, frameAdapter) : true))
                {
                    cases.Add(("CBR 20 Mbps, MP4, 96 kHz audio", new RecordSettings { Codec = e.Codec, VideoBitrateKbps = 20_000 }, ContainerFormat.Mp4, 96000));
                    cases.Add(("VBR 10 Mbps, MP4", new RecordSettings { Codec = e.Codec, VideoBitrateKbps = 10_000, RateControl = RateControl.Vbr }, ContainerFormat.Mp4, 48000));
                    cases.Add(("Quality 60, fMP4, full range", new RecordSettings { Codec = e.Codec, RateControl = RateControl.Quality, Quality = 60, FullRangeSource = true }, ContainerFormat.FragmentedMp4, 48000));
                }

                foreach (var (name, settings, container, rate) in cases)
                {
                    string file = Path.Combine(folder, $"test_{++n:D2}_{e.Codec}_{(e.Hardware ? "gpu" : "cpu")}.mp4");
                    report.Append($"{e.Label} {e.Codec}, {name}: ");
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        string active = WriteFile(file, settings, PlanFor(e, e.Codec), container, samples, rate);
                        double ms = sw.Elapsed.TotalMilliseconds;
                        report.AppendLine($"OK in {ms:0} ms ({ms / (Seconds * Fps):0.0} ms/frame), encoder used: {active}");
                        report.AppendLine("    " + Inspect(file));
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("FAILED: " + ex.Message);
                    }
                }
            }

            report.AppendLine();
            report.Append("Instant replay (15 s buffer, 8 s fed, save 10 s): ");
            try
            {
                var preview = EncoderCatalog.Resolve(encoders, VideoCodec.H264, "auto", frameAdapter);
                string clip = Path.Combine(folder, "test_replay.mp4");
                ReplayTest(clip, preview is null ? EncoderPlan.Software(VideoCodec.H264) : PlanFor(preview, VideoCodec.H264), samples);
                report.AppendLine("OK");
                report.AppendLine("    " + Inspect(clip));
            }
            catch (Exception ex) { report.AppendLine("FAILED: " + ex.Message); }
        }
        finally
        {
            foreach (var s in samples) MfHelpers.Release(s);
            foreach (var d in others.Values) d.Dispose();
        }
    }

    /// <summary>A moving gradient, so the encoder has real motion to spend bits on.</summary>
    private static List<IMFSample> BuildFrames(EncoderDevice device)
    {
        var list = new List<IMFSample>();
        var nv12 = new byte[Width * Height * 3 / 2];
        for (int f = 0; f < 30; f++)
        {
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                    nv12[y * Width + x] = (byte)(16 + ((x + y + f * 24) % 220));
            for (int i = Width * Height; i < nv12.Length; i += 2)
            {
                nv12[i] = (byte)(96 + f * 2);
                nv12[i + 1] = (byte)(160 - f * 2);
            }
            list.Add(device.CreateNv12Sample(Width, Height, nv12));
        }
        return list;
    }

    private static string WriteFile(string file, RecordSettings settings, EncoderPlan plan, ContainerFormat container,
                                    List<IMFSample> frames, int audioRate)
    {
        var audioFormat = new WaveFormat(audioRate, 16, 2);
        using var recorder = new Recorder();
        recorder.Start(file, settings, plan, container, Mf.MFVideoFormat_NV12, Width, Height, Fps, 1, audioFormat);

        long frameHns = 10_000_000L / Fps;
        var tone = Tone(audioFormat);
        int total = Seconds * Fps;
        for (int i = 0; i < total; i++)
        {
            long t = i * frameHns;
            var clone = MfHelpers.CloneSample(frames[i % frames.Count]);
            try { recorder.WriteVideo(clone, t, frameHns); }
            finally { MfHelpers.Release(clone); }
            if (i % 6 == 0) recorder.WriteAudio(tone, tone.Length, audioFormat, t);
        }
        string active = recorder.ActiveEncoder;
        recorder.Stop();
        return active;
    }

    private static void ReplayTest(string clip, EncoderPlan plan, List<IMFSample> frames)
    {
        var audioFormat = new WaveFormat(48000, 16, 2);
        using var replay = new ReplayBuffer { BufferSeconds = 15 };
        var settings = new RecordSettings { VideoBitrateKbps = 15_000 };
        replay.Start(settings, plan, Mf.MFVideoFormat_NV12, Width, Height, Fps, 1, audioFormat, 0);

        long frameHns = 10_000_000L / Fps;
        var tone = Tone(audioFormat);
        for (int i = 0; i < 8 * Fps; i++)
        {
            long t = i * frameHns;
            var clone = MfHelpers.CloneSample(frames[i % frames.Count]);
            try { replay.WriteVideo(clone, t, frameHns); }
            finally { MfHelpers.Release(clone); }
            if (i % 6 == 0) replay.WriteAudio(tone, tone.Length, audioFormat, t);
            // Real time is not needed, but the worker has to keep up with segment rollovers.
            if (i % Fps == 0) Thread.Sleep(20);
        }
        if (!replay.SaveAsync(10, clip, 8 * Fps * frameHns).Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("the save did not finish within 30 s");
        if (replay.LastError is { } error) throw new InvalidOperationException(error);
    }

    /// <summary>100 ms of a 440 Hz tone - one block per six video frames at 60 fps.</summary>
    private static byte[] Tone(WaveFormat format)
    {
        int samples = format.SampleRate / 10;
        var bytes = new byte[samples * format.BlockAlign];
        for (int i = 0; i < samples; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 440 * i / format.SampleRate) * 8000);
            for (int c = 0; c < format.Channels; c++)
            {
                int at = i * format.BlockAlign + c * 2;
                bytes[at] = (byte)v;
                bytes[at + 1] = (byte)(v >> 8);
            }
        }
        return bytes;
    }

    /// <summary>Reads the file back through Media Foundation and describes what is actually in it.</summary>
    public static string Inspect(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists) return "file missing";
        MfHelpers.Check(Mf.MFCreateSourceReaderFromURL(file, null, out var reader), "open for reading");
        try
        {
            var parts = new List<string>();
            var streams = new Dictionary<uint, (bool Video, long First, long Last, int Count)>();
            for (uint i = 0; ; i++)
            {
                if (reader.GetCurrentMediaType(i, out var type) < 0 || type is null) break;
                try
                {
                    bool isVideo = MfHelpers.GetGuid(type, Mf.MF_MT_MAJOR_TYPE) == Mf.MFMediaType_Video;
                    if (isVideo)
                    {
                        var (w, h) = MfHelpers.Unpack(MfHelpers.GetU64(type, Mf.MF_MT_FRAME_SIZE));
                        uint range = MfHelpers.GetU32(type, Mf.MF_MT_VIDEO_NOMINAL_RANGE);
                        parts.Add($"video {Mf.DescribeSubtype(MfHelpers.GetGuid(type, Mf.MF_MT_SUBTYPE))} {w}x{h}" +
                                  (range == 1 ? " full-range" : range == 2 ? " limited-range" : ""));
                    }
                    else
                    {
                        parts.Add($"audio {MfHelpers.GetU32(type, Mf.MF_MT_AUDIO_SAMPLES_PER_SECOND)} Hz " +
                                  $"{MfHelpers.GetU32(type, Mf.MF_MT_AUDIO_NUM_CHANNELS)} ch");
                    }
                    streams[i] = (isVideo, long.MaxValue, 0, 0);
                }
                finally { MfHelpers.Release(type); }
            }
            reader.SetStreamSelection(Mf.ALL_STREAMS, true);

            var ended = new HashSet<uint>();
            for (int guard = 0; guard < 100_000 && ended.Count < streams.Count; guard++)
            {
                int hr = reader.ReadSample(Mf.ALL_STREAMS, 0, out uint index, out uint flags, out long time, out var sample);
                if (hr < 0) break;
                if (sample is not null && streams.TryGetValue(index, out var s))
                    streams[index] = (s.Video, Math.Min(s.First, time), Math.Max(s.Last, time), s.Count + 1);
                MfHelpers.Release(sample);
                if (((Mf.SourceReaderFlags)flags).HasFlag(Mf.SourceReaderFlags.EndOfStream)) ended.Add(index);
            }

            foreach (var (_, s) in streams)
            {
                parts.Add(s.Video
                    ? $"{s.Count} frames from {s.First / 1e7:0.00}s to {s.Last / 1e7:0.00}s"
                    : $"{s.Count} audio packets from {s.First / 1e7:0.00}s to {s.Last / 1e7:0.00}s");
            }
            var video = streams.Values.FirstOrDefault(v => v.Video);
            double seconds = video.Count > 0 ? (video.Last - video.First) / 1e7 + 1.0 / Fps : 0;
            if (seconds > 0) parts.Add($"~{info.Length * 8 / seconds / 1e6:0.0} Mbps");
            parts.Add($"{info.Length / 1024} KB");
            return string.Join(", ", parts);
        }
        finally { MfHelpers.Release(reader); }
    }
}
