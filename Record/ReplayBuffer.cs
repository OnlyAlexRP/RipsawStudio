using System.Collections.Concurrent;
using NAudio.Wave;
using RipsawStudio.Interop;

namespace RipsawStudio.Record;

/// <summary>
/// Keeps the last minute or so of play ready to save. Since a sink writer can't ring-buffer,
/// the ring is made of whole short files instead, rolled over every couple of seconds with
/// the oldest deleted as new ones appear. Saving joins the covering segments into one MP4
/// without re-encoding the picture (see <see cref="ReplayMuxer"/>); sound is kept separately
/// in <see cref="ReplayAudioRing"/> and encoded once at save time. Rollover happens on this
/// class's own thread so the capture thread never blocks on a finalise.
/// </summary>
internal sealed class ReplayBuffer : IDisposable
{
    /// <summary>
    /// Two seconds trades save accuracy against rollover work. Shorter is more precise and
    /// finalises more often; longer overshoots the requested window by more.
    /// </summary>
    public const int SegmentSeconds = 2;

    private readonly object _writeLock = new();
    private readonly object _ringLock = new();
    private readonly BlockingCollection<Action> _jobs = new();
    private readonly List<ReplaySegment> _segments = new();
    /// <summary>Segments a save is still reading; they must outlive the trimmer.</summary>
    private readonly HashSet<string> _inUse = new();
    private readonly List<string> _pendingDelete = new();

    private Thread? _worker;
    private string _folder = "";
    private int _sequence;   // bumped with Interlocked: the worker and the capture thread both build writers

    private Recorder? _current;
    private Recorder? _next;
    private int _preparing;
    private long _segmentStartHns;

    // Everything needed to build the next segment's writer, captured when the buffer starts.
    private RecordSettings? _settings;
    private object? _deviceManager;
    private Guid _subtype;
    private int _width, _height;
    private uint _fpsNum = 60, _fpsDen = 1;
    private WaveFormat? _audioFormat;
    private readonly ReplayAudioRing _audio = new();

    /// <summary>
    /// Volatile because the worker thread turns it off when a segment fails, and the capture
    /// and audio threads read it on every block to decide whether to write at all.
    /// </summary>
    private volatile bool _running;
    public bool IsRunning => _running;

    private int _bufferSeconds = 60;
    /// <summary>How many seconds are kept. Older segments are deleted as new ones arrive.</summary>
    public int BufferSeconds
    {
        get => _bufferSeconds;
        set
        {
            if (value == _bufferSeconds) return;
            _bufferSeconds = value;
            // The sound ring is sized in bytes, so it has to be resized to match. Doing it
            // only on a real change keeps this off the path that runs twice a second.
            _audio.Configure(_audioFormat, value);
        }
    }
    /// <summary>Set when a segment could not be written, so the UI can say so once and stop.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Whether the pieces being kept carry sound. A silent ring is the failure that is
    /// hardest to spot from the outside - the clips look fine until you play them - so it is
    /// worth being able to read it off a diagnostics report.
    /// </summary>
    public bool HasAudio => _audio.HasAudio;

    public event EventHandler<string>? Status;

    /// <summary>Seconds currently saveable, including the segment being written.</summary>
    public double BufferedSeconds
    {
        get
        {
            lock (_ringLock) return _closedHns / 10_000_000.0 + Math.Max(0, _openSeconds);
        }
    }

    /// <summary>
    /// Written under the write lock, read under the ring lock. Deliberately not synchronised
    /// across the two: it is a status readout, a double is written atomically on x64, and
    /// taking both locks on the hot path to keep a progress number exact is not a trade
    /// worth making.
    /// </summary>
    private double _openSeconds;
    /// <summary>Running total of the closed segments, so nothing has to walk the ring.</summary>
    private long _closedHns;

    public ReplayBuffer()
    {
        _worker = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "RipsawStudio.Replay",
            // Below the capture and render threads on purpose: finalising a segment must
            // never take a slice from the frames going to the screen.
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    /// <summary>
    /// Starts rolling. Called on the capture thread, with the same description of the stream
    /// the recorder would be given. Throws if the very first segment cannot be opened, since
    /// that is a real problem worth telling the user about rather than failing silently.
    /// </summary>
    public void Start(RecordSettings settings, object? deviceManager, Guid subtype,
                      int width, int height, uint fpsNum, uint fpsDen, WaveFormat? audioFormat, long nowHns)
    {
        Stop();
        // Let the teardown Stop() queued finish before the folder is swept, or the sweep
        // races the finalise of the segment that was still open.
        DrainWorker();

        _settings = settings;
        _deviceManager = deviceManager;
        _subtype = subtype;
        _width = width;
        _height = height;
        _fpsNum = fpsNum;
        _fpsDen = fpsDen;
        _audioFormat = audioFormat;
        _audio.Configure(audioFormat, BufferSeconds);
        LastError = null;

        _folder = Path.Combine(Path.GetTempPath(), "RipsawStudio", "replay", Environment.ProcessId.ToString());
        Directory.CreateDirectory(_folder);
        PurgeFolder();
        PurgeAbandonedFolders();

        lock (_writeLock)
        {
            _current = CreateSegmentWriter();
            _segmentStartHns = nowHns;
            _openSeconds = 0;
        }
        _running = true;
        Status?.Invoke(this, $"Instant replay armed - keeping the last {BufferSeconds} s");
    }

    public void Stop()
    {
        if (!_running && _current is null) return;
        _running = false;

        Recorder? current;
        lock (_writeLock)
        {
            current = _current;
            _current = null;
        }
        var next = Interlocked.Exchange(ref _next, null);

        Enqueue(() =>
        {
            try { current?.Stop(); } catch { }
            try { next?.Stop(); } catch { }

            // A prepare that was already queued when Stop looked will have run by now - this
            // job is behind it in the queue - so this is where a late writer is caught.
            // Without it, that writer and its part-written file would be orphaned.
            var late = Interlocked.Exchange(ref _next, null);
            if (late is not null)
            {
                try { late.Stop(); } catch { }
                TryDelete(late.FilePath ?? "");
            }
            ClearRing();
        });
    }

    // ---- the hot path, called from the capture and audio threads --------------------------

    /// <summary>
    /// Takes one frame. The sample must be the caller's to stamp - the engine hands over a
    /// clone, because the recorder writing the same frame sets a different time on it.
    /// </summary>
    public void WriteVideo(IMFSample sample, long nowHns, long durationHns)
    {
        if (!IsRunning) return;
        lock (_writeLock)
        {
            if (_current is null) return;
            _openSeconds = (nowHns - _segmentStartHns) / 10_000_000.0;

            if (_openSeconds >= SegmentSeconds && _next is not null) RollOver(nowHns, waitForFinalise: false);
            else if (_openSeconds >= SegmentSeconds * 0.6) PrepareNext();

            try { _current?.WriteVideo(sample, nowHns, durationHns); }
            catch (Exception ex) { Fail(ex); }
        }
    }

    /// <summary>
    /// Takes one captured audio packet. It goes to the memory ring, not to the open segment -
    /// a segment carries picture only. The ring is sized for the whole window, so this needs
    /// no lock against the rollover and cannot be disturbed by one.
    /// </summary>
    public void WriteAudio(byte[] buffer, int count, WaveFormat format, long nowHns)
    {
        if (!IsRunning) return;
        // A format that turns up after the buffer armed - which is every launch, since the
        // preview starts before the audio endpoint - just resizes the ring. It no longer has
        // to restart the whole ring, because the segments do not carry sound any more.
        if (!SameLayout(_audioFormat, format))
        {
            _audioFormat = format;
            _audio.Configure(format, BufferSeconds);
        }
        _audio.Write(buffer, count, format, nowHns);
    }

    private static bool SameLayout(WaveFormat? a, WaveFormat? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.SampleRate == b.SampleRate && a.Channels == b.Channels &&
               a.BitsPerSample == b.BitsPerSample && a.Encoding == b.Encoding &&
               a.BlockAlign == b.BlockAlign;
    }

    /// <summary>
    /// Closes the open segment and writes the last <paramref name="seconds"/> out to
    /// <paramref name="outputPath"/>. Called on the capture thread; only the rollover happens
    /// there, and the joining runs on this class's own thread.
    /// </summary>
    public Task<string> SaveAsync(int seconds, string outputPath, long nowHns)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!IsRunning)
        {
            completion.TrySetException(new InvalidOperationException(
                "The replay buffer is not running. Arm it on the Record page first."));
            return completion.Task;
        }

        // Drained before the lock is taken, never inside it. A worker job needs the write
        // lock to tear a failed buffer down, and waiting for the worker while holding that
        // lock is how the capture thread and the worker deadlock against each other.
        // Draining first also puts any segment still finalising into the ring ahead of this
        // one, which is what keeps the ring in order - a joined clip plays a mis-ordered
        // ring back as a jump. Nothing else can roll over in between: only this thread does.
        DrainWorker();

        List<ReplaySegment> chosen;
        lock (_writeLock) RollOver(nowHns, waitForFinalise: true);
        lock (_ringLock)
        {
            long wanted = seconds * 10_000_000L;
            long total = 0;
            chosen = new List<ReplaySegment>();
            for (int i = _segments.Count - 1; i >= 0 && total < wanted; i--)
            {
                chosen.Insert(0, _segments[i]);
                total += _segments[i].DurationHns;
            }
            foreach (var segment in chosen) _inUse.Add(segment.Path);
        }

        if (chosen.Count == 0)
        {
            completion.TrySetException(new InvalidOperationException("Nothing has been buffered yet."));
            return completion.Task;
        }

        long clipStartHns = chosen[0].StartHns;
        int audioOffsetMs = _settings?.AudioOffsetMs ?? 0;
        int audioBitrate = _settings?.AudioBitrateKbps ?? 192;

        bool queued = Enqueue(() =>
        {
            try
            {
                // Taken here rather than on the capture thread: a minute of sound is about
                // eleven megabytes, and an allocation that size goes straight to the large
                // object heap - a collection there is exactly the sort of pause that shows up
                // as a dropped frame. The ring is indexed by time, so taking it a moment later
                // costs nothing but a few extra milliseconds of tail.
                var take = _audio.Take(clipStartHns);
                string? note = ReplayMuxer.Write(chosen, take, audioOffsetMs, audioBitrate, outputPath);
                if (note is not null) Status?.Invoke(this, "Instant replay: " + note);
                completion.TrySetResult(outputPath);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { ReleaseSegments(chosen); }
        });

        // Nothing will ever run the job once the queue has closed, and an awaiter left on a
        // task that never completes is a save button stuck on "Saving..." for good.
        if (!queued)
        {
            ReleaseSegments(chosen);
            completion.TrySetException(new InvalidOperationException("The replay buffer is shutting down."));
        }
        return completion.Task;
    }

    private void ReleaseSegments(List<ReplaySegment> segments)
    {
        lock (_ringLock)
        {
            foreach (var segment in segments) _inUse.Remove(segment.Path);
            FlushPendingDeletes();
        }
    }

    // ---- segment lifecycle -------------------------------------------------------------------

    /// <summary>Must be called holding <see cref="_writeLock"/>.</summary>
    private void RollOver(long nowHns, bool waitForFinalise)
    {
        var finished = _current;
        string? finishedPath = finished?.FilePath;
        long startedAt = _segmentStartHns;
        long duration = Math.Max(0, nowHns - _segmentStartHns);

        Recorder? replacement = Interlocked.Exchange(ref _next, null);
        if (replacement is null)
        {
            // Nothing was ready - a save asked for one right after the last rollover. Building
            // it here costs a frame or two, which a deliberate save can afford.
            try { replacement = CreateSegmentWriter(); }
            catch (Exception ex) { Fail(ex); return; }
        }

        _current = replacement;
        _segmentStartHns = nowHns;
        _openSeconds = 0;

        if (finished is null || finishedPath is null) return;

        if (waitForFinalise)
        {
            // The caller has already drained the worker, so everything earlier is in the ring
            // and this segment appends in the right place.
            try { finished.Stop(); } catch { }
            AddSegment(finishedPath, startedAt, duration);
        }
        else
        {
            Enqueue(() =>
            {
                try { finished.Stop(); } catch { }
                AddSegment(finishedPath, startedAt, duration);
            });
        }
    }

    private void AddSegment(string path, long startHns, long durationHns)
    {
        lock (_ringLock)
        {
            _segments.Add(new ReplaySegment(path, startHns, durationHns));
            _closedHns += durationHns;

            // One segment of slack, so the oldest is only dropped once it is fully outside
            // the window rather than the moment it starts to leave it.
            long keep = (BufferSeconds + SegmentSeconds) * 10_000_000L;
            while (_segments.Count > 1 && _closedHns - _segments[0].DurationHns >= keep)
            {
                _closedHns -= _segments[0].DurationHns;
                Retire(_segments[0].Path);
                _segments.RemoveAt(0);
            }
        }
    }

    /// <summary>Deletes a segment, or defers it while a save is still reading it.</summary>
    private void Retire(string path)
    {
        if (_inUse.Contains(path)) { _pendingDelete.Add(path); return; }
        TryDelete(path);
    }

    private void FlushPendingDeletes()
    {
        for (int i = _pendingDelete.Count - 1; i >= 0; i--)
        {
            if (_inUse.Contains(_pendingDelete[i])) continue;
            TryDelete(_pendingDelete[i]);
            _pendingDelete.RemoveAt(i);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Builds the next segment's writer on the worker, so a rollover is just a swap.</summary>
    private void PrepareNext()
    {
        if (_next is not null || !_running) return;
        // The flag is claimed atomically: the capture thread sets it and the worker clears it,
        // and two writers for one slot would leave an open file with nothing to close it.
        if (Interlocked.CompareExchange(ref _preparing, 1, 0) != 0) return;

        bool queued = Enqueue(() =>
        {
            Recorder? recorder = null;
            try
            {
                recorder = CreateSegmentWriter();
                if (!_running || Interlocked.CompareExchange(ref _next, recorder, null) is not null)
                {
                    recorder.Stop();
                    TryDelete(recorder.FilePath ?? "");
                }
            }
            catch (Exception ex)
            {
                try { recorder?.Stop(); } catch { }
                Fail(ex);
            }
            finally { Interlocked.Exchange(ref _preparing, 0); }
        });
        if (!queued) Interlocked.Exchange(ref _preparing, 0);
    }

    private Recorder CreateSegmentWriter()
    {
        if (_settings is null) throw new InvalidOperationException("The replay buffer was not configured.");
        var recorder = new Recorder();
        string path = Path.Combine(_folder, $"seg_{Interlocked.Increment(ref _sequence):D6}.mp4");
        // Video only: the sound is buffered raw and encoded when a clip is saved.
        recorder.Start(path, _settings, _deviceManager, _subtype, _width, _height, _fpsNum, _fpsDen, null);
        return recorder;
    }

    /// <summary>
    /// Gives up on the buffer after a write failed. Tearing down matters as much as the
    /// message: the open segment still holds a sink writer and a file that will never be
    /// finalised, and leaving those behind leaks both until the app closes.
    /// </summary>
    private void Fail(Exception ex)
    {
        if (LastError is not null) return;
        LastError = ex.Message;
        Status?.Invoke(this, "Instant replay stopped: " + ex.Message);
        Stop();
    }

    private void ClearRing()
    {
        lock (_ringLock)
        {
            foreach (var segment in _segments) Retire(segment.Path);
            _segments.Clear();
            _closedHns = 0;
            FlushPendingDeletes();
            _audio.Clear();
        }
        _openSeconds = 0;
    }

    private void PurgeFolder()
    {
        if (_folder.Length == 0) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_folder, "seg_*.mp4")) TryDelete(file);
        }
        catch { }
    }

    /// <summary>
    /// Clears out what a previous run left behind - a crash cannot clean up after itself. Each
    /// run owns a folder named for its process, and only folders whose process is gone are
    /// swept, so a second copy of the app running at the same time is left alone.
    /// </summary>
    private void PurgeAbandonedFolders()
    {
        try
        {
            var root = Path.GetDirectoryName(_folder);
            if (root is null) return;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (!int.TryParse(Path.GetFileName(directory), out int pid) || pid == Environment.ProcessId)
                    continue;
                try { System.Diagnostics.Process.GetProcessById(pid); continue; }   // still alive
                catch (ArgumentException) { }
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }
        catch { }
    }

    // ---- worker thread -------------------------------------------------------------------------

    private void WorkerMain()
    {
        // Its own MFStartup, because every thread that calls into Media Foundation needs one.
        MfHelpers.Startup();
        try
        {
            foreach (var job in _jobs.GetConsumingEnumerable())
            {
                try { job(); }
                catch (Exception ex) { Status?.Invoke(this, "Instant replay: " + ex.Message); }
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { /* the queue was completed while waiting */ }
        finally { MfHelpers.Shutdown(); }
    }

    /// <summary>Queues work for the buffer's own thread. False once the queue has closed.</summary>
    private bool Enqueue(Action job)
    {
        try { _jobs.Add(job); return true; }
        catch (Exception) { return false; /* shutting down */ }
    }

    /// <summary>
    /// Waits until everything queued before this point has run. The barrier is a task rather
    /// than an event because the wait can time out: an event disposed by the waiter and then
    /// set by the worker throws, and completing an abandoned task does nothing.
    /// </summary>
    private void DrainWorker()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(() => done.TrySetResult())) return;
        done.Task.Wait(3000);
    }

    public void Dispose()
    {
        Stop();
        DrainWorker();
        _jobs.CompleteAdding();
        _worker?.Join(2000);
        _worker = null;
        PurgeFolder();
        try { if (_folder.Length > 0) Directory.Delete(_folder); } catch { }
        _jobs.Dispose();
    }
}
