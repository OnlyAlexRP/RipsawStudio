using RipsawStudio.Interop;

namespace RipsawStudio.Record;

/// <summary>One finished piece of the ring: where it is, when it began, and how long it ran.</summary>
internal sealed record ReplaySegment(string Path, long StartHns, long DurationHns);

/// <summary>
/// Joins a run of replay segments into one MP4. Picture is copied through in passthrough
/// (source reader and sink writer share the same H.264 type, so nothing is decoded or
/// re-encoded). Sound can't use that trick for AAC, so it arrives as raw PCM from
/// <see cref="ReplayAudioRing"/> and is encoded in one pass by <see cref="AacStream"/>.
/// Picture and sound are written interleaved, a segment at a time, rather than one whole
/// track at a time, since a sink writer buffers a stream that gets too far ahead. Every
/// segment starts on a keyframe, which is what makes cutting at segment boundaries safe.
/// </summary>
internal static class ReplayMuxer
{
    private sealed class StreamMap
    {
        public uint SinkStream;
        public bool Ended;
    }

    /// <summary>
    /// Where each sink stream has got to. Timestamps within a track must never go backwards,
    /// and joining at a boundary is the one place they could: a segment's audio can run a
    /// hair past its last video frame, so the next segment would start behind it.
    /// </summary>
    private sealed class StreamClock
    {
        public long LastTime = long.MinValue;
    }

    /// <summary>How many samples went in and how many the sink refused, for the closing note.</summary>
    private sealed class WriteTally
    {
        public int Written, Refused, LastHr;

        public void Record(int hr)
        {
            if (MfHelpers.Failed(hr)) { Refused++; LastHr = hr; }
            else Written++;
        }
    }

    /// <summary>
    /// Feeds the buffered sound in a segment at a time, keeping pace with the picture so the
    /// sink writer never has to hold a whole track in hand waiting for the other one.
    /// </summary>
    private sealed class AudioFeed
    {
        private readonly IMFSinkWriter _writer;
        private readonly uint _stream;
        private readonly ReplayAudioTake _take;
        private readonly long _clipStartHns;
        private readonly long _offsetHns;
        private int _index;

        public readonly WriteTally Tally = new();

        public AudioFeed(IMFSinkWriter writer, uint stream, ReplayAudioTake take,
                         long clipStartHns, int audioOffsetMs)
        {
            _writer = writer;
            _stream = stream;
            _take = take;
            _clipStartHns = clipStartHns;
            _offsetHns = audioOffsetMs * 10_000L;
        }

        /// <summary>Writes every block that belongs before <paramref name="limitHns"/>.</summary>
        public void WriteUpTo(long limitHns)
        {
            while (_index < _take.Blocks.Length)
            {
                var (timeHns, offset) = _take.Blocks[_index];
                long at = timeHns - _clipStartHns + _offsetHns;
                if (at >= limitHns) return;
                _index++;

                int end = _index < _take.Blocks.Length ? _take.Blocks[_index].Offset : _take.Pcm.Length;
                int bytes = end - offset;
                if (bytes <= 0) continue;
                if (at < 0) continue;   // sound from before the clip starts

                Tally.Record(AacStream.Write(_writer, _stream, _take.Pcm, offset, bytes,
                                             _take.SampleRate, _take.Channels, at));
            }
        }

        public void Flush() => WriteUpTo(long.MaxValue);
    }

    /// <summary>
    /// Writes the segments, in order, to <paramref name="outputPath"/>, with
    /// <paramref name="take"/> as the sound. Returns null when all of it came across, or a
    /// note explaining what did not.
    /// </summary>
    public static string? Write(IReadOnlyList<ReplaySegment> segments, ReplayAudioTake? take,
                                int audioOffsetMs, int audioBitrateKbps, string outputPath)
    {
        if (segments.Count == 0) throw new InvalidOperationException("Nothing has been buffered yet.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        IMFSinkWriter? writer = null;
        uint videoStream = 0, audioStream = 0;
        bool hasVideo = false, hasAudio = false;
        long clipStartHns = segments[0].StartHns;
        var clocks = new Dictionary<uint, StreamClock>();
        var videoTally = new WriteTally();
        AudioFeed? audio = null;
        long offsetHns = 0;
        bool wroteAnything = false;
        string? note = null;

        try
        {
            foreach (var segment in segments)
            {
                // The clock advances by this piece's own measured length whatever happens to
                // it below - including a file that has gone missing, or one carrying nothing
                // usable. Skipping the advance would overlap everything after it.
                //
                // Deliberately not taken from where the samples ended: audio and video finish
                // at different instants inside a piece, and advancing by the later of the two
                // put a gap in the other at every join. With a non-zero A/V offset that gap is
                // the whole offset, once every couple of seconds, for the length of the clip.
                long segmentStart = offsetHns;
                offsetHns += segment.DurationHns;
                if (!File.Exists(segment.Path)) continue;

                IMFSourceReader? reader = null;
                try
                {
                    MfHelpers.Check(Mf.MFCreateSourceReaderFromURL(segment.Path, null, out reader),
                                    "MFCreateSourceReaderFromURL(" + Path.GetFileName(segment.Path) + ")");
                    reader.SetStreamSelection(Mf.ALL_STREAMS, true);

                    var map = new Dictionary<uint, StreamMap>();
                    for (uint index = 0; ; index++)
                    {
                        int hr = reader.GetCurrentMediaType(index, out var type);
                        if (MfHelpers.Failed(hr) || type is null) break;
                        try
                        {
                            // Segments carry picture only, so anything else in one is ignored.
                            if (MfHelpers.GetGuid(type, Mf.MF_MT_MAJOR_TYPE) != Mf.MFMediaType_Video) continue;

                            writer ??= CreateWriter(outputPath);
                            if (!hasVideo)
                            {
                                // No picture is no clip, so this one does throw. The sound
                                // stream is added straight after, and both have to be in
                                // place before BeginWriting - a stream cannot be added later.
                                MfHelpers.Check(AddPassthroughStream(writer, type, out videoStream),
                                                "AddStream(replay video)");
                                hasVideo = true;

                                if (take is not null)
                                {
                                    int hrAudio = AacStream.Add(writer, audioBitrateKbps,
                                                                take.SampleRate, take.Channels, out audioStream);
                                    hasAudio = !MfHelpers.Failed(hrAudio);
                                    if (hasAudio)
                                        audio = new AudioFeed(writer, audioStream, take, clipStartHns, audioOffsetMs);
                                    // A clip without sound beats no clip - the same call the
                                    // recorder makes - but it says so rather than handing back
                                    // a quietly silent file, which is the failure that took a
                                    // run on real hardware to find.
                                    if (!hasAudio)
                                        note = $"the sound could not be encoded (0x{hrAudio:X8}) - the clip is silent";
                                }
                                else
                                {
                                    note = "there was no sound buffered to put in it";
                                }
                            }

                            map[index] = new StreamMap { SinkStream = videoStream };
                        }
                        finally { MfHelpers.Release(type); }
                    }

                    if (writer is null || map.Count == 0) continue;
                    if (!wroteAnything)
                    {
                        MfHelpers.Check(writer.BeginWriting(), "IMFSinkWriter::BeginWriting(replay)");
                        wroteAnything = true;
                    }

                    CopySamples(reader, writer, map, clocks, videoTally, segmentStart);
                }
                finally { MfHelpers.Release(reader); }

                // Sound up to the point the picture has reached, so the two go in together.
                audio?.WriteUpTo(offsetHns);
            }

            if (writer is null || !wroteAnything)
                throw new InvalidOperationException("The replay segments could not be read back.");

            audio?.Flush();

            if (videoTally.Written == 0)
                throw new InvalidOperationException(
                    $"None of the buffered picture could be written (0x{videoTally.LastHr:X8}).");

            MfHelpers.Check(writer.FinalizeWriting(), "IMFSinkWriter::Finalize(replay)");
            return note ?? Describe(videoTally, audio?.Tally);
        }
        catch
        {
            // An unfinalised MP4 will never play, so it must not be left behind looking like
            // a saved clip.
            MfHelpers.Release(writer);
            writer = null;
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            throw;
        }
        finally { MfHelpers.Release(writer); }
    }

    /// <summary>What, if anything, the sink would not take. Null when it took everything.</summary>
    private static string? Describe(WriteTally video, WriteTally? audio)
    {
        if (audio is not null && audio.Written == 0)
            return $"none of the sound could be written (0x{audio.LastHr:X8}) - the clip is silent";
        if (audio is not null && audio.Refused > 0)
            return $"{audio.Refused} sound blocks were refused (0x{audio.LastHr:X8}) - the clip may skip";
        if (video.Refused > 0)
            return $"{video.Refused} frames were refused (0x{video.LastHr:X8}) - the clip may jump";
        return null;
    }

    private static IMFSinkWriter CreateWriter(string path)
    {
        MfHelpers.Check(Mf.MFCreateAttributes(out var attrs, 3), "MFCreateAttributes(replay sink)");
        try
        {
            // The picture needs no transform - its input type is its output type - and the
            // sound needs only the software AAC encoder, so nothing here wants a hardware one.
            MfHelpers.SetU32(attrs, Mf.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 0);
            // Samples are fed interleaved, but a save is not real time and there is nothing to
            // be gained by letting the writer pace us.
            MfHelpers.SetU32(attrs, Mf.MF_SINK_WRITER_DISABLE_THROTTLING, 1);
            MfHelpers.Check(Mf.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out var writer),
                            "MFCreateSinkWriterFromURL(replay)");
            return writer;
        }
        finally { MfHelpers.Release(attrs); }
    }

    /// <summary>
    /// Adds a stream that carries the segment's own encoded samples through untouched: the
    /// sink writer only passes samples through when its input type is its output type.
    /// Returns the HRESULT rather than throwing, so the caller decides what is fatal.
    /// </summary>
    private static int AddPassthroughStream(IMFSinkWriter writer, IMFMediaType type, out uint stream)
    {
        int hr = writer.AddStream(type, out stream);
        if (MfHelpers.Failed(hr)) return hr;
        return writer.SetInputMediaType(stream, type, null);
    }

    /// <summary>
    /// Copies one segment's samples across, shifted by <paramref name="offsetHns"/> so they
    /// continue from where the previous segment left off.
    /// </summary>
    private static void CopySamples(IMFSourceReader reader, IMFSinkWriter writer,
                                    Dictionary<uint, StreamMap> map,
                                    Dictionary<uint, StreamClock> clocks, WriteTally tally, long offsetHns)
    {
        int remaining = map.Count;
        // A reader that keeps returning nothing without ever saying end-of-stream would spin
        // here forever. Two seconds of 60 Hz video is a few hundred samples, so anything past
        // this is a broken file rather than a long one.
        int emptyReads = 0;
        const int MaxEmptyReads = 4096;

        while (remaining > 0)
        {
            int hr = reader.ReadSample(Mf.ALL_STREAMS, 0, out uint index, out uint rawFlags, out _, out var sample);
            if (MfHelpers.Failed(hr)) break;

            var flags = (Mf.SourceReaderFlags)rawFlags;
            try
            {
                bool ending = flags.HasFlag(Mf.SourceReaderFlags.EndOfStream);
                if (map.TryGetValue(index, out var stream))
                {
                    if (ending && !stream.Ended) { stream.Ended = true; remaining--; }
                    if (sample is not null)
                    {
                        long time = sample.GetSampleTime(out long t) >= 0 ? t : 0;
                        long shifted = time + offsetHns;

                        // Nudged forward by a tick rather than allowed to go backwards. A
                        // track with a decreasing timestamp is rejected outright by the MP4
                        // sink, and losing the clip over a stray audio sample at a join
                        // would be a poor trade for the microsecond this costs.
                        if (!clocks.TryGetValue(stream.SinkStream, out var clock))
                            clocks[stream.SinkStream] = clock = new StreamClock();
                        if (shifted <= clock.LastTime) shifted = clock.LastTime + 1;
                        clock.LastTime = shifted;

                        sample.SetSampleTime(shifted);
                        // Counted, not ignored and not fatal. A rejected sample used to vanish
                        // without a word - which is how a whole track went missing unnoticed -
                        // but losing a whole clip over one bad frame would be its own mistake.
                        tally.Record(writer.WriteSample(stream.SinkStream, sample));
                    }
                }
                else if (ending)
                {
                    // An end-of-stream on a stream we are not carrying still has to be seen,
                    // or the loop would never notice the file had run out.
                    foreach (var other in map.Values) other.Ended = true;
                    remaining = 0;
                }

                if (sample is null && !ending && ++emptyReads > MaxEmptyReads) break;
                if (sample is not null) emptyReads = 0;
            }
            finally { MfHelpers.Release(sample); }
        }
    }
}
