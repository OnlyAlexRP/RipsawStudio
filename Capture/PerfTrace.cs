using System.Text;

namespace RipsawStudio.Capture;

internal sealed class TraceContext
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string Format { get; init; }
    public required double SourceFps { get; init; }
    public required string Gpu { get; init; }
    public required double DisplayHz { get; init; }
    public required bool VSync { get; init; }
    public required string Pacing { get; init; }
    public required bool SoftwarePath { get; init; }
    public required long DroppedAtEnd { get; init; }
    public required double AudioBufferedMs { get; init; }
    public required string AudioFormat { get; init; }
    /// <summary>Whether a second encoder was running alongside the preview during the trace.</summary>
    public required bool ReplayArmed { get; init; }
    public required bool Recording { get; init; }
    public required bool MicRunning { get; init; }
}

/// <summary>
/// Collects per-frame timings over a short window and writes them up as distributions.
/// A mean tells you almost nothing about smoothness: a stream that is perfect except for
/// one 40 ms frame per second has a fine average and looks awful, which is exactly the
/// failure mode worth catching. Percentiles show it immediately.
/// </summary>
internal sealed class PerfTrace
{
    private const int Capacity = 4096;   // ~17 s of headroom at 240 Hz

    private readonly long _startTicks;
    private readonly long _endTicks;
    private readonly long _droppedAtStart;

    private readonly long[] _captureTicks = new long[Capacity];
    private int _captureCount;

    private readonly long[] _presentTicks = new long[Capacity];
    private readonly float[] _waitMs = new float[Capacity];
    private readonly float[] _drawMs = new float[Capacity];
    private readonly float[] _lagMs = new float[Capacity];
    private readonly float[] _queued = new float[Capacity];
    private int _presentCount;

    public TaskCompletionSource<string> Completion { get; }

    public PerfTrace(long nowTicks, int seconds, long droppedAtStart, TaskCompletionSource<string> completion)
    {
        _startTicks = nowTicks;
        _endTicks = nowTicks + seconds * TimeSpan.TicksPerSecond;
        _droppedAtStart = droppedAtStart;
        Completion = completion;
    }

    public bool IsFinished(long nowTicks) => nowTicks >= _endTicks;

    /// <summary>Called on the capture thread only.</summary>
    public void AddCapture(long ticks)
    {
        if (_captureCount >= Capacity || ticks > _endTicks) return;
        _captureTicks[_captureCount++] = ticks;
    }

    /// <summary>Called on the render thread only.</summary>
    public void AddPresent(long ticks, double waitMs, double drawMs, double lagMs, int queuedFrames)
    {
        int index = _presentCount;
        if (index >= Capacity || ticks > _endTicks) return;
        _presentTicks[index] = ticks;
        _waitMs[index] = (float)waitMs;
        _drawMs[index] = (float)drawMs;
        _lagMs[index] = (float)lagMs;
        _queued[index] = queuedFrames;
        _presentCount = index + 1;
    }

    public string BuildReport(TraceContext context)
    {
        double seconds = (_endTicks - _startTicks) / (double)TimeSpan.TicksPerSecond;
        var sb = new StringBuilder();

        sb.AppendLine($"Ripsaw Studio performance trace - {seconds:0.0} s");
        sb.AppendLine("================================================");
        sb.AppendLine($"Written        {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Source         {context.Width}x{context.Height} {context.Format} @ {context.SourceFps:0.##} Hz");
        sb.AppendLine($"Display        {context.DisplayHz:0} Hz, vsync {(context.VSync ? "ON" : "off")}");
        sb.AppendLine($"Graphics       {context.Gpu}");
        sb.AppendLine($"Path           {(context.SoftwarePath ? "software" : "GPU")}");
        sb.AppendLine($"Pacing         {context.Pacing}");
        sb.AppendLine($"Audio          {context.AudioFormat}, {context.AudioBufferedMs:0} ms buffered");
        sb.AppendLine($"Microphone     {(context.MicRunning ? "mixing in" : "off")}");
        // Both of these are extra encoders on the same frames, so a trace taken with either
        // running is not comparable with one taken without. Worth stating rather than leaving
        // to be remembered.
        sb.AppendLine($"Encoders       {Describe(context)}");
        sb.AppendLine();

        long dropped = context.DroppedAtEnd - _droppedAtStart;
        sb.AppendLine($"Frames captured  {_captureCount,6}   ({_captureCount / seconds,6:0.0} /s)");
        sb.AppendLine($"Frames shown     {_presentCount,6}   ({_presentCount / seconds,6:0.0} /s)");
        sb.AppendLine($"Frames dropped   {dropped,6}");
        if (_captureCount >= Capacity || _presentCount >= Capacity)
            sb.AppendLine("(sample buffer filled; counts above are truncated)");
        sb.AppendLine();

        sb.AppendLine("                          min   median      p95      max   (ms)");
        sb.AppendLine("                       ------   ------   ------   ------");
        Row(sb, "capture interval", Intervals(_captureTicks, _captureCount));
        Row(sb, "shown interval", Intervals(_presentTicks, _presentCount));
        Row(sb, "display wait", Copy(_waitMs, _presentCount));
        Row(sb, "draw", Copy(_drawMs, _presentCount));
        Row(sb, "lag arrival->screen", Copy(_lagMs, _presentCount));
        Row(sb, "frames queued (count)", Copy(_queued, _presentCount));
        sb.AppendLine();

        AppendVerdict(sb, context, seconds, dropped);
        return sb.ToString();
    }

    private static string Describe(TraceContext context)
    {
        if (context.Recording && context.ReplayArmed) return "recording AND replay buffer - two on top of the preview";
        if (context.Recording) return "recording";
        if (context.ReplayArmed) return "replay buffer armed";
        return "none - preview only";
    }

    private void AppendVerdict(StringBuilder sb, TraceContext context, double seconds, long dropped)
    {
        sb.AppendLine("Reading this");
        sb.AppendLine("------------");

        double capturedPerSecond = _captureCount / seconds;
        double shownPerSecond = _presentCount / seconds;
        // Percentile expects sorted input; Row sorted its own copies, not these.
        var shownIntervals = Intervals(_presentTicks, _presentCount);
        Array.Sort(shownIntervals);
        var lag = Copy(_lagMs, _presentCount);
        Array.Sort(lag);

        if (context.SourceFps > 0 && capturedPerSecond < context.SourceFps * 0.9)
            sb.AppendLine($"* The card is delivering {capturedPerSecond:0.0} /s against a mode of {context.SourceFps:0.##} Hz.");

        if (shownPerSecond < capturedPerSecond * 0.9)
            sb.AppendLine($"* Only {shownPerSecond:0.0} of {capturedPerSecond:0.0} frames a second reach the screen - " +
                          "the display side is the bottleneck, not the card.");

        double expectedInterval = context.DisplayHz > 0 ? 1000.0 / context.DisplayHz : 16.67;
        if (shownIntervals.Length > 0 && Percentile(shownIntervals, 0.5) > expectedInterval * 1.5)
            sb.AppendLine($"* Frames reach the screen every {Percentile(shownIntervals, 0.5):0.0} ms against a " +
                          $"{expectedInterval:0.0} ms refresh - a refresh is being missed regularly.");

        if (lag.Length > 0)
        {
            sb.AppendLine($"* Median added delay is {Percentile(lag, 0.5):0.0} ms, worst {Percentile(lag, 0.95):0.0} ms " +
                          "at the 95th percentile. The capture card's own delay is on top of this.");
        }

        // The signature of the block landing inside Present instead of in our wait: nothing
        // spent waiting, a whole refresh spent "drawing". The frame is then chosen before the
        // block rather than after it, and reaches the glass a refresh stale.
        if (context.VSync && _presentCount > 10)
        {
            var waits = Copy(_waitMs, _presentCount);
            var draws = Copy(_drawMs, _presentCount);
            Array.Sort(waits);
            Array.Sort(draws);
            if (Percentile(waits, 0.5) < 1.0 && Percentile(draws, 0.5) > expectedInterval * 0.7)
                sb.AppendLine("* Present is blocking instead of the frame-latency wait. That costs a refresh of " +
                              "delay, because the frame is picked before the block rather than after it.");
        }

        var queued = Copy(_queued, _presentCount);
        Array.Sort(queued);
        if (queued.Length > 0)
        {
            float median = Percentile(queued, 0.5);
            sb.AppendLine($"* {median:0.#} frames sit in the display queue on average. Each one is a whole " +
                          $"refresh ({expectedInterval:0.0} ms) of delay that arrives after Present returns, " +
                          "so it is not counted in the lag figure above.");
            if (median >= 2)
                sb.AppendLine("  That is more queueing than intended - real delay is higher than lag suggests.");
        }

        if (dropped > seconds * 5)
        {
            sb.AppendLine($"* {dropped} frames dropped before display in {seconds:0.0} s.");
            if (context.Recording || context.ReplayArmed)
                sb.AppendLine("  An encoder was running during this trace. Take another with recording " +
                              "stopped and the replay buffer disarmed to see whether that is the cause.");
        }

        if (context.VSync && context.DisplayHz > 0 && context.DisplayHz < 90)
            sb.AppendLine($"* With vsync on a {context.DisplayHz:0} Hz display, a frame can wait up to " +
                          $"{expectedInterval:0.0} ms. A higher refresh rate lowers that ceiling directly.");
    }

    private static void Row(StringBuilder sb, string label, float[] values)
    {
        if (values.Length == 0)
        {
            sb.AppendLine($"{label,-20}        -        -        -        -");
            return;
        }
        Array.Sort(values);
        sb.AppendLine($"{label,-20} {values[0],8:0.00} {Percentile(values, 0.5),8:0.00} " +
                      $"{Percentile(values, 0.95),8:0.00} {values[^1],8:0.00}");
    }

    private static float[] Copy(float[] source, int count)
    {
        count = Math.Min(count, source.Length);
        var result = new float[Math.Max(0, count)];
        Array.Copy(source, result, result.Length);
        return result;
    }

    private static float[] Intervals(long[] ticks, int count)
    {
        count = Math.Min(count, ticks.Length);
        if (count < 2) return Array.Empty<float>();
        var result = new float[count - 1];
        for (int i = 1; i < count; i++)
            result[i - 1] = (float)((ticks[i] - ticks[i - 1]) / 10_000.0);
        return result;
    }

    /// <summary>Expects a sorted array.</summary>
    private static float Percentile(float[] sorted, double fraction)
    {
        if (sorted.Length == 0) return 0;
        int index = (int)Math.Clamp(Math.Round(fraction * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[index];
    }
}
