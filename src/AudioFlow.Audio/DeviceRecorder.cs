using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioFlow.Audio;

public delegate void DeviceSamplesHandler(ReadOnlySpan<float> samples);

/// <summary>
/// Shared native-format capture. Some virtual drivers reject the AutoConvertPcm
/// flags used by WasapiRecorder in NAudio 3.3. The compatibility API omits those
/// flags; AudioFlow performs format conversion itself without exclusive access.
/// </summary>
#pragma warning disable CS0618
public sealed class DeviceRecorder : IDisposable
{
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly SampleConverter converter;
    public event DeviceSamplesHandler? DataAvailable;
    public event Action<Exception>? Failed;
    public DeviceRecorder(string endpointId, bool loopback = false)
    {
        using var enumerator = new MMDeviceEnumerator();
        device = enumerator.GetDevice(endpointId);
        capture = new NativeFormatCapture(device, loopback);
        converter = new(capture.WaveFormat);
        capture.DataAvailable += (_, e) => DataAvailable?.Invoke(converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded)));
        capture.RecordingStopped += (_, e) => { if (e.Exception != null) Failed?.Invoke(e.Exception); };
    }
    public void Start() => capture.StartRecording();
    public void Stop() => capture.StopRecording();
    public void Dispose() { capture.Dispose(); device.Dispose(); }
    private sealed class NativeFormatCapture(MMDevice endpoint, bool loopback) : WasapiCapture(endpoint, true, 40)
    {
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None;
    }
}
#pragma warning restore CS0618
