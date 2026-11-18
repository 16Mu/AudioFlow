using NAudio.CoreAudioApi;

namespace AudioFlow.Audio;

public sealed record RecordingSession(string EndpointId, string Instance, float Volume, bool Muted);

public static class RecordingSessions
{
    private static void Visit(IEnumerable<int> processIds, Action<MMDevice, AudioSessionControl> action)
    {
        var ids = processIds.ToHashSet();
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        for (int i = 0; i < devices.Count; i++)
        {
            using var device = devices[i];
            var sessions = device.AudioSessionManager.Sessions;
            for (int j = 0; j < sessions.Count; j++)
            {
                using var session = sessions[j];
                if (ids.Contains((int)session.GetProcessID) && (int)session.State == 1) action(device, session);
            }
        }
    }
    public static List<RecordingSession> Read(IEnumerable<int> pids)
    {
        var result = new List<RecordingSession>();
        Visit(pids, (device, session) => result.Add(new(device.ID, session.GetSessionInstanceIdentifier,
            session.SimpleAudioVolume.Volume, session.SimpleAudioVolume.Mute)));
        return result;
    }
}
