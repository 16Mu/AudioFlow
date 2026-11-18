using AudioFlow.Audio;
using AudioFlow.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Length > 0 && args[0] == "--unit") { GraphChecks.Run(); return; }
if (args.Length > 0 && args[0] == "--input-checks") { await InputChecks.RunAsync(); return; }
if (args.Length > 2 && args[0] == "--record-input") { await InputChecks.RecordAsync(args[1], args[2]); return; }
if (args.Length > 0 && args[0] == "--live") { await LiveChecks.RunAsync(args.Length > 1 ? int.Parse(args[1]) : 1800); return; }
if (args.Length > 1 && args[0] == "--recover")
{
    await using var engine = new AudioEngine(args[1], Console.WriteLine);
    await engine.RecoverAsync();
    Console.WriteLine(engine.PendingRecoveryCount == 0 ? "Audio settings recovery completed." :
        $"Restored available audio sessions; {engine.PendingRecoveryCount} recovery record(s) remain pending.");
    return;
}
if (args.Length > 0 && args[0] == "--channel-tests")
{
    foreach (var channel in new AudioCatalog().Read().SuggestedChannels.Where(c => c.Provider == "Cable"))
    {
        var result = await ChannelTest.RunAsync(channel);
        Console.WriteLine(channel.Name + " " + JsonSerializer.Serialize(result));
        if (!result.Passed) throw new Exception("Virtual channel did not forward the test signal.");
    }
    return;
}
if (args.Length > 0 && args[0] == "--channel-isolation-tests")
{
    var snapshot = new AudioCatalog().Read();
    var channels = snapshot.SuggestedChannels.Where(c => c.Provider == "Cable").ToArray();
    foreach (var channel in channels)
    {
        var result = await ChannelTest.RunIsolationAsync(channel, channels);
        Console.WriteLine(channel.Name + " " + JsonSerializer.Serialize(result));
        if (!result.Passed) throw new Exception("Independent channel isolation failed.");
    }
    return;
}
if (args.Length > 0 && args[0] == "--legacy-probe")
{
    using var enumerator = new MMDeviceEnumerator();
    using var device = enumerator.GetDevice(new AudioCatalog().Read().SuggestedChannels[0].CaptureEndpointId);
#pragma warning disable CS0618
    using var capture = new WasapiCapture(device, true, 40);
#pragma warning restore CS0618
    capture.StartRecording();
    await Task.Delay(1500);
    capture.StopRecording();
    Console.WriteLine("Legacy shared capture initialized.");
    return;
}
if (args.Length == 0 || args[0] == "--devices")
{
    Console.WriteLine(JsonSerializer.Serialize(new AudioCatalog().Read(), SceneStore.JsonOptions));
    return;
}
if (args[0] == "--policy-get")
{
    var pid = args.Length > 1 ? int.Parse(args[1]) : Environment.ProcessId;
    for (int role = 0; role < 3; role++) Console.WriteLine($"role={role} output={AudioPolicy.GetOutput(pid, role) ?? "(Windows default)"}");
    return;
}
if (args[0] == "--tone")
{
    double frequency = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 440;
    int seconds = args.Length > 2 ? int.Parse(args[2]) : 60;
    using var player = await new WasapiPlayerBuilder().WithDefaultDeviceStreamRouting().WithLatency(40).BuildAsync();
    player.Init(new ToneProvider(frequency));
    player.Play();
    Console.WriteLine($"TONE pid={Environment.ProcessId} frequency={frequency}");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    player.Stop();
    return;
}
if (args[0] == "--idle")
{
    Console.WriteLine($"IDLE pid={Environment.ProcessId}");
    await Task.Delay(TimeSpan.FromSeconds(120));
    return;
}
if (args[0] == "--capture")
{
    int pid = int.Parse(args[1]);
    long total = 0; float peak = 0;
    using var recorder = await new WasapiRecorderBuilder().WithProcessLoopback((uint)pid)
        .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).WithBufferLength(40).BuildAsync();
    recorder.DataAvailable += (buffer, flags, _, _) =>
    {
        Interlocked.Add(ref total, buffer.Length);
        foreach (float sample in MemoryMarshal.Cast<byte, float>(buffer)) peak = Math.Max(peak, Math.Abs(sample));
    };
    recorder.StartRecording();
    await Task.Delay(3000);
    recorder.StopRecording();
    Console.WriteLine($"capturedBytes={total} peak={peak}");
    return;
}
if (args[0] == "--policy-set")
{
    int pid = int.Parse(args[1]);
    for (int role = 0; role < 3; role++) AudioPolicy.SetOutput(pid, role, args[2] == "default" ? null : args[2]);
    Console.WriteLine("Output preference set.");
    return;
}
if (args[0] == "--device-default")
{
    AudioDeviceSettings.SetDefault(args[1], int.Parse(args[2]));
    Console.WriteLine("Default endpoint set.");
    return;
}
if (args[0] == "--sample-rate")
{
    AudioDeviceSettings.SetSampleRate(args[1], int.Parse(args[2]));
    Console.WriteLine("Endpoint sample rate set.");
    return;
}
if (args[0] == "--gain")
{
    new AudioCatalog().SetApplicationVolume([int.Parse(args[1])],
        float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), false);
    return;
}

internal sealed class ToneProvider(double frequency) : IWaveProvider
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private long frame;
    public int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));
    public int Read(Span<byte> buffer)
    {
        var samples = MemoryMarshal.Cast<byte, float>(buffer);
        for (int i = 0; i < samples.Length; i += 2)
        {
            float sample = (float)(Math.Sin(2 * Math.PI * frequency * frame++ / 48000) * .015);
            samples[i] = samples[i + 1] = sample;
        }
        return buffer.Length;
    }
}
