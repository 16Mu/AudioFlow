using NAudio.Wave;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AudioFlow.Audio;

/// <summary>Decode the capture device's native format to 48 kHz stereo float.</summary>
public sealed class SampleConverter(WaveFormat format)
{
    private float[] decoded = new float[16384], converted = new float[16384];
    private double position = 1;
    private float lastLeft, lastRight;
    private bool first = true;
    private readonly bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
        format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
    public ReadOnlySpan<float> Convert(ReadOnlySpan<byte> input)
    {
        if (floating && format.BitsPerSample == 32 && format.Channels == 2 && format.SampleRate == 48000)
            return MemoryMarshal.Cast<byte, float>(input);
        int frames = input.Length / format.BlockAlign;
        int outputFrames = (int)Math.Ceiling((frames + 2) * 48000.0 / format.SampleRate);
        if (decoded.Length < (frames + 1) * 2) decoded = new float[(frames + 1) * 2];
        if (converted.Length < outputFrames * 2) converted = new float[outputFrames * 2];
        int sampleBytes = format.BitsPerSample / 8;
        for (int frame = 0; frame < frames; frame++)
        {
            int start = frame * format.BlockAlign;
            decoded[(frame + 1) * 2] = Decode(input.Slice(start, sampleBytes));
            decoded[(frame + 1) * 2 + 1] = format.Channels == 1
                ? decoded[(frame + 1) * 2] : Decode(input.Slice(start + sampleBytes, sampleBytes));
        }
        if (frames == 0) return [];
        if (first) { lastLeft = decoded[2]; lastRight = decoded[3]; first = false; }
        decoded[0] = lastLeft; decoded[1] = lastRight;
        int count = 0;
        double step = format.SampleRate / 48000.0;
        while (position < frames)
        {
            int index = (int)position;
            double fraction = position - index;
            for (int channel = 0; channel < 2; channel++)
            {
                float a = decoded[index * 2 + channel], b = decoded[(index + 1) * 2 + channel];
                converted[count++] = (float)(a + (b - a) * fraction);
            }
            position += step;
        }
        position -= frames;
        lastLeft = decoded[frames * 2]; lastRight = decoded[frames * 2 + 1];
        return converted.AsSpan(0, count);
    }
    private float Decode(ReadOnlySpan<byte> sample) => floating
        ? format.BitsPerSample == 64 ? (float)BitConverter.ToDouble(sample) : BitConverter.ToSingle(sample)
        : format.BitsPerSample switch
        {
            8 => (sample[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
            24 => ((sample[0] << 8) | (sample[1] << 16) | (sample[2] << 24)) / 2147483648f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
            _ => throw new NotSupportedException("录音设备的采样格式不受支持。")
        };
}
