using AudioFlow.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AudioFlow.Audio;

public sealed record ChannelTestResult(bool Passed, double Amplitude, int Frames, double LatencyMilliseconds);
public sealed record ChannelIsolationResult(bool Passed, double Amplitude, IReadOnlyList<string> Crosstalk);

public static class ChannelTest
{
    public const double TestFrequency = 1003;
    public static async Task<ChannelIsolationResult> RunIsolationAsync(VirtualChannel channel, IReadOnlyList<VirtualChannel> channels)
    {
        return await Task.Run(async () =>
        {
            using var enumerator = new MMDeviceEnumerator(); using var render = enumerator.GetDevice(channel.RenderEndpointId);
            using var player = new WasapiPlayerBuilder().WithDevice(render).WithLatency(40).Build();
            var probes = new List<(VirtualChannel channel, DeviceRecorder recorder, ToneAnalyzer analyzer)>();
            try
            {
                foreach (var candidate in channels.Append(channel).DistinctBy(c => c.CaptureEndpointId))
                {
                    var recorder = new DeviceRecorder(candidate.CaptureEndpointId); var analyzer = new ToneAnalyzer([TestFrequency]);
                    recorder.DataAvailable += samples => analyzer.Add(samples); probes.Add((candidate, recorder, analyzer)); recorder.Start();
                }
                player.Init(new TestToneProvider(TestFrequency, .03f)); player.Play(); await Task.Delay(1800); player.Stop();
                double amplitude = probes.First(p => p.channel.CaptureEndpointId == channel.CaptureEndpointId).analyzer.Amplitude(TestFrequency);
                var crosstalk = probes.Where(p => p.channel.CaptureEndpointId != channel.CaptureEndpointId && p.analyzer.Amplitude(TestFrequency) > .003)
                    .Select(p => p.channel.Name).ToArray();
                return new ChannelIsolationResult(amplitude > .003 && crosstalk.Length == 0, amplitude, crosstalk);
            }
            finally { player.Stop(); foreach (var probe in probes) { probe.recorder.Stop(); probe.recorder.Dispose(); } }
        });
    }
    public static async Task<ChannelTestResult> RunAsync(VirtualChannel channel)
    {
        return await Task.Run(async () =>
        {
            using var enumerator = new MMDeviceEnumerator();
            using var renderDevice = enumerator.GetDevice(channel.RenderEndpointId);
            using var recorder = new DeviceRecorder(channel.CaptureEndpointId);
            using var player = new WasapiPlayerBuilder().WithDevice(renderDevice).WithLatency(40).Build();
            var analyzer = new ToneAnalyzer([TestFrequency]);
            long start = 0, first = 0;
            recorder.DataAvailable += floats =>
            {
                if (start != 0 && first == 0 && floats.IndexOfAnyExcept(0f) >= 0) first = Stopwatch.GetTimestamp();
                analyzer.Add(floats);
            };
            recorder.Start();
            player.Init(new TestToneProvider(TestFrequency, .03f));
            start = Stopwatch.GetTimestamp(); player.Play();
            await Task.Delay(1800);
            player.Stop(); recorder.Stop();
            double amplitude = analyzer.Amplitude(TestFrequency);
            double latency = first == 0 ? 0 : (first - start) * 1000.0 / Stopwatch.Frequency;
            return new ChannelTestResult(amplitude > .003 && analyzer.Frames > 4800, amplitude, analyzer.Frames, latency);
        });
    }
}

public sealed class TestToneProvider(double frequency, float amplitude = .015f) : IWaveProvider
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private long frame;
    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public int Read(Span<byte> buffer)
    {
        var samples = MemoryMarshal.Cast<byte, float>(buffer);
        for (int i = 0; i < samples.Length; i += 2)
        {
            float value = (float)Math.Sin(2 * Math.PI * frequency * frame++ / 48000) * amplitude;
            samples[i] = samples[i + 1] = value;
        }
        return buffer.Length;
    }
}

public sealed class ToneAnalyzer
{
    private readonly object gate = new();
    private readonly double[] frequencies, real, imaginary, windowPower;
    private long frames;
    private int windowFrames;
    private long windows;
    public int Frames { get { lock (gate) return (int)Math.Min(frames, int.MaxValue); } }
    public ToneAnalyzer(double[] frequencies)
    { this.frequencies = frequencies; real = new double[frequencies.Length]; imaginary = new double[frequencies.Length]; windowPower = new double[frequencies.Length]; }
    public void Add(ReadOnlySpan<float> samples)
    {
        lock (gate)
        {
            for (int i = 0; i < samples.Length; i += 2, frames++)
            {
                for (int f = 0; f < frequencies.Length; f++)
                {
                    var phase = 2 * Math.PI * frequencies[f] * windowFrames / 48000;
                    real[f] += samples[i] * Math.Cos(phase);
                    imaginary[f] += samples[i] * Math.Sin(phase);
                }
                if (++windowFrames == 960)
                {
                    for (int f = 0; f < frequencies.Length; f++)
                        windowPower[f] += 4 * (real[f] * real[f] + imaginary[f] * imaginary[f]) / (960.0 * 960);
                    Array.Clear(real); Array.Clear(imaginary); windowFrames = 0; windows++;
                }
            }
        }
    }
    public double Amplitude(double frequency)
    {
        lock (gate)
        {
            int i = Array.IndexOf(frequencies, frequency);
            return windows == 0 || i < 0 ? 0 : Math.Sqrt(windowPower[i] / windows);
        }
    }
    public void Reset() { lock (gate) { frames = 0; windowFrames = 0; windows = 0; Array.Clear(real); Array.Clear(imaginary); Array.Clear(windowPower); } }
}
