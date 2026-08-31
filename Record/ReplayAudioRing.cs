using NAudio.Wave;
using RipsawStudio.Audio;

namespace RipsawStudio.Record;

/// <summary>A slice of buffered sound, ready to be encoded into a saved replay.</summary>
internal sealed class ReplayAudioTake
{
    public required byte[] Pcm { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    /// <summary>One entry per captured packet: when it arrived, and where it starts in Pcm.</summary>
    public required (long TimeHns, int Offset)[] Blocks { get; init; }
}

/// <summary>
/// The replay buffer's sound, kept as plain 16-bit PCM in memory rather than encoded into the
/// video segments - passthrough joining works for H.264 but silently drops AAC, so audio is
/// buffered raw instead and encoded once on save by the same code that records sound normally.
/// Cheap to do: 48 kHz stereo is about 11 MB a minute, and it stays continuous across segment
/// joins with no boundaries to line up.
/// </summary>
internal sealed class ReplayAudioRing
{
    private readonly object _lock = new();

    private byte[] _pcm = Array.Empty<byte>();
    private (long TimeHns, long Position)[] _marks = Array.Empty<(long, long)>();
    private int _markHead, _markCount;

    /// <summary>Total bytes ever written; the ring holds the last <see cref="_pcm"/>.Length of them.</summary>
    private long _written;
    private byte[] _scratch = Array.Empty<byte>();

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public bool HasAudio => Channels > 0 && _pcm.Length > 0;

    /// <summary>
    /// Sizes the ring for a format and a window. Resets whatever was held if either changed -
    /// two formats cannot share one buffer, and a clip made of both would be noise.
    /// </summary>
    public void Configure(WaveFormat? format, int seconds)
    {
        lock (_lock)
        {
            int channels = format is null ? 0 : Math.Min(2, Math.Max(1, format.Channels));
            int rate = format?.SampleRate ?? 0;
            seconds = Math.Max(1, seconds) + 2;   // the same slack the segment ring keeps

            if (channels == 0 || rate == 0)
            {
                Reset(0, 0, 0, 0);
                return;
            }
            int capacity = rate * channels * 2 * seconds;
            if (rate == SampleRate && channels == Channels && _pcm.Length == capacity) return;
            Reset(rate, channels, capacity, seconds);
        }
    }

    private void Reset(int rate, int channels, int capacity, int seconds)
    {
        SampleRate = rate;
        Channels = channels;
        _pcm = capacity > 0 ? new byte[capacity] : Array.Empty<byte>();
        // One mark per captured packet, sized by time rather than by byte count: a low sample
        // rate makes the buffer small without making the packets any less frequent, so sizing
        // from bytes ran out of marks and started losing the front of the window.
        // Four hundred a second is far more than WASAPI delivers.
        _marks = capacity > 0 ? new (long, long)[Math.Max(1024, seconds * 400)] : Array.Empty<(long, long)>();
        _markHead = _markCount = 0;
        _written = 0;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _markHead = _markCount = 0;
            _written = 0;
        }
    }

    /// <summary>Appends one captured packet, converting it to 16-bit PCM on the way in.</summary>
    public void Write(byte[] buffer, int count, WaveFormat format, long timeHns)
    {
        lock (_lock)
        {
            if (!HasAudio) return;

            if (format.BlockAlign <= 0) return;
            int maxBytes = count / format.BlockAlign * Channels * 2;
            if (maxBytes <= 0) return;
            if (_scratch.Length < maxBytes) _scratch = new byte[maxBytes * 2];

            int frames = PcmConvert.ToPcm16(buffer, count, format, Channels, _scratch);
            int bytes = frames * Channels * 2;
            if (bytes <= 0) return;
            if (bytes >= _pcm.Length) return;   // an absurd packet; dropping beats wrapping

            AddMark(timeHns, _written);

            int at = (int)(_written % _pcm.Length);
            int first = Math.Min(bytes, _pcm.Length - at);
            Array.Copy(_scratch, 0, _pcm, at, first);
            if (bytes > first) Array.Copy(_scratch, first, _pcm, 0, bytes - first);
            _written += bytes;

            DropMarksBefore(_written - _pcm.Length);
        }
    }

    private void AddMark(long timeHns, long position)
    {
        if (_markCount == _marks.Length)
        {
            _markHead = (_markHead + 1) % _marks.Length;
            _markCount--;
        }
        _marks[(_markHead + _markCount) % _marks.Length] = (timeHns, position);
        _markCount++;
    }

    private void DropMarksBefore(long position)
    {
        while (_markCount > 0 && Mark(0).Position < position)
        {
            _markHead = (_markHead + 1) % _marks.Length;
            _markCount--;
        }
    }

    private (long TimeHns, long Position) Mark(int index) => _marks[(_markHead + index) % _marks.Length];

    /// <summary>
    /// Copies out everything buffered from <paramref name="fromHns"/> onwards. Returns null when
    /// there is nothing to take, which is a clip without sound rather than an error.
    /// </summary>
    public ReplayAudioTake? Take(long fromHns)
    {
        lock (_lock)
        {
            if (!HasAudio || _markCount == 0) return null;

            // The first packet at or after the cut. Starting a hair late is better than
            // starting early: early would mean sound from before the clip begins.
            int start = 0;
            while (start < _markCount && Mark(start).TimeHns < fromHns) start++;
            // Everything held is older than the clip. Nothing usable, which the caller turns
            // into a clip without sound - clamping to the last block instead would hand back
            // audio timestamped before the clip begins, which is then thrown away anyway.
            if (start >= _markCount) return null;

            long startPosition = Mark(start).Position;
            long oldest = Math.Max(0, _written - _pcm.Length);
            if (startPosition < oldest) startPosition = oldest;

            int bytes = (int)(_written - startPosition);
            if (bytes <= 0) return null;

            var pcm = new byte[bytes];
            int at = (int)(startPosition % _pcm.Length);
            int first = Math.Min(bytes, _pcm.Length - at);
            Array.Copy(_pcm, at, pcm, 0, first);
            if (bytes > first) Array.Copy(_pcm, 0, pcm, first, bytes - first);

            var blocks = new List<(long, int)>(_markCount - start);
            for (int i = start; i < _markCount; i++)
            {
                var mark = Mark(i);
                if (mark.Position < startPosition) continue;
                blocks.Add((mark.TimeHns, (int)(mark.Position - startPosition)));
            }
            if (blocks.Count == 0) return null;

            return new ReplayAudioTake
            {
                Pcm = pcm,
                SampleRate = SampleRate,
                Channels = Channels,
                Blocks = blocks.ToArray(),
            };
        }
    }
}
