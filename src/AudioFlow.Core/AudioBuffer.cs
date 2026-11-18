namespace AudioFlow.Core;

/// <summary>Bounded stereo FIFO with interpolation for independent device clocks.</summary>
public sealed class AudioBuffer
{
    private readonly float[] samples;
    private readonly object gate = new();
    private int readFrame;
    private int writeFrame;
    private int countFrames;
    private double fractionalFrame;
    private bool primed;
    private readonly int targetFrames;
    public float Gain { get; set; } = 1;
    public long Overruns { get; private set; }
    public long Underruns { get; private set; }
    public int BufferedFrames { get { lock (gate) return countFrames; } }

    public AudioBuffer(int capacityFrames = 24000, int prefillFrames = 960)
    {
        if (capacityFrames < 4 || prefillFrames < 0 || prefillFrames >= capacityFrames)
            throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        samples = new float[capacityFrames * 2];
        targetFrames = prefillFrames;
    }
    public void Write(ReadOnlySpan<float> input)
    {
        if (input.Length % 2 != 0) throw new ArgumentException("Stereo frames must contain two samples.");
        lock (gate)
        {
            int capacity = samples.Length / 2;
            for (var frame = 0; frame < input.Length / 2; frame++)
            {
                if (countFrames == capacity)
                {
                    readFrame = (readFrame + 1) % capacity;
                    countFrames--;
                    fractionalFrame = 0;
                    Overruns++;
                }
                samples[writeFrame * 2] = input[frame * 2];
                samples[writeFrame * 2 + 1] = input[frame * 2 + 1];
                writeFrame = (writeFrame + 1) % capacity;
                countFrames++;
            }
        }
    }
    public void MixInto(Span<float> output, bool correctClockDrift = true)
    {
        if (output.Length % 2 != 0) throw new ArgumentException("Stereo frames must contain two samples.");
        lock (gate)
        {
            if (!primed)
            {
                if (countFrames < Math.Max(2, targetFrames)) return;
                primed = true;
            }
            var capacity = samples.Length / 2;
            double correction = correctClockDrift && targetFrames > 0
                ? Math.Clamp((countFrames - targetFrames - output.Length / 2.0) / 480000.0, -.002, .002)
                : 0;
            var step = 1 + correction;
            for (var frame = 0; frame < output.Length / 2; frame++)
            {
                if (countFrames < 2)
                {
                    // Silence between render packets is normal for process loopback.
                    // Count one starvation event, not every silent frame.
                    if (countFrames > 0) Underruns++;
                    primed = false;
                    fractionalFrame = 0;
                    break;
                }
                var next = (readFrame + 1) % capacity;
                for (var channel = 0; channel < 2; channel++)
                {
                    float a = samples[readFrame * 2 + channel];
                    float b = samples[next * 2 + channel];
                    output[frame * 2 + channel] +=
                        (float)(a + (b - a) * fractionalFrame) * Gain;
                }
                fractionalFrame += step;
                var advance = (int)fractionalFrame;
                fractionalFrame -= advance;
                advance = Math.Min(advance, countFrames);
                readFrame = (readFrame + advance) % capacity;
                countFrames -= advance;
            }
        }
    }
    public void Clear()
    {
        lock (gate)
        {
            countFrames = readFrame = writeFrame = 0;
            fractionalFrame = 0;
            primed = false;
        }
    }
}
