namespace RipsawStudio.Audio;

/// <summary>
/// Converts interleaved 16-bit PCM between sample rates, keeping its position across calls so
/// consecutive blocks join without a click. Needed because Windows' AAC encoder only takes
/// 44.1 and 48 kHz, and some capture cards hand out 96 kHz (or 32 kHz): fed straight in,
/// those used to give a recording with no sound track at all.
///
/// Linear interpolation - the same trade as the mic path makes: this only ever runs between
/// rates close enough that the content above the new Nyquist limit is inaudible anyway.
/// </summary>
internal sealed class PcmResampler
{
    private readonly int _channels;
    private int _inRate, _outRate;
    private double _position;
    private readonly short[] _last;
    private bool _primed;
    private byte[] _output = Array.Empty<byte>();

    public PcmResampler(int channels)
    {
        _channels = channels;
        _last = new short[channels];
    }

    /// <summary>The nearest rate the AAC encoder accepts.</summary>
    public static int AacRate(int rate) => rate == 44100 ? 44100 : 48000;

    /// <summary>
    /// Resamples <paramref name="frames"/> frames from <paramref name="input"/>. Returns the
    /// buffer holding the result - the input itself when no conversion is needed - and the
    /// number of frames in it.
    /// </summary>
    public (byte[] Buffer, int Frames) Process(byte[] input, int frames, int inRate, int outRate)
    {
        if (inRate == outRate || inRate <= 0 || outRate <= 0 || frames <= 0) return (input, frames);
        if (inRate != _inRate || outRate != _outRate)
        {
            _inRate = inRate;
            _outRate = outRate;
            _position = 0;
            _primed = false;
        }

        double step = (double)inRate / outRate;
        int capacity = (int)Math.Ceiling((frames + 1) / step) + 2;
        int bytesNeeded = capacity * _channels * 2;
        if (_output.Length < bytesNeeded) _output = new byte[bytesNeeded * 2];

        if (!_primed)
        {
            for (int c = 0; c < _channels; c++) _last[c] = Read(input, 0, c);
            _primed = true;
        }

        // Position -1 is the last frame of the previous block, 0 the first frame of this one.
        int written = 0;
        while (_position < frames - 1 && written < capacity)
        {
            int i = (int)Math.Floor(_position);
            float t = (float)(_position - i);
            for (int c = 0; c < _channels; c++)
            {
                float a = i < 0 ? _last[c] : Read(input, i, c);
                float b = Read(input, i + 1, c);
                Write(_output, written, c, (short)Math.Round(a + (b - a) * t));
            }
            written++;
            _position += step;
        }

        _position -= frames;
        for (int c = 0; c < _channels; c++) _last[c] = Read(input, frames - 1, c);
        return (_output, written);
    }

    private short Read(byte[] buffer, int frame, int channel)
    {
        int at = (frame * _channels + channel) * 2;
        return (short)(buffer[at] | (buffer[at + 1] << 8));
    }

    private void Write(byte[] buffer, int frame, int channel, short value)
    {
        int at = (frame * _channels + channel) * 2;
        buffer[at] = (byte)(value & 0xFF);
        buffer[at + 1] = (byte)((value >> 8) & 0xFF);
    }
}
