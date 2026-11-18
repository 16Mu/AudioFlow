using NAudio.CoreAudioApi;

namespace AudioFlow.Audio;

public sealed record EndpointVolumeState(float Volume, bool Muted);

public sealed class EndpointVolumeBinding : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDevice device;
    private readonly AudioEndpointVolume endpoint;
    private bool disposed;
    public event Action<EndpointVolumeState>? Changed;
    public string EndpointId { get; }
    public EndpointVolumeState State => new(endpoint.MasterVolumeLevelScalar, endpoint.Mute);
    public EndpointVolumeBinding(string endpointId)
    {
        EndpointId = endpointId; device = enumerator.GetDevice(endpointId);
        endpoint = device.AudioEndpointVolume;
        endpoint.OnVolumeNotification += OnChanged;
    }
    private void OnChanged(AudioVolumeNotificationData state)
    {
        if (!disposed) Changed?.Invoke(new(state.MasterVolume, state.Muted));
    }
    public void SetVolume(float volume) => endpoint.MasterVolumeLevelScalar = Math.Clamp(volume, 0, 1);
    public void SetMuted(bool muted) => endpoint.Mute = muted;
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        endpoint.OnVolumeNotification -= OnChanged;
        endpoint.Dispose(); device.Dispose(); enumerator.Dispose();
    }
}
