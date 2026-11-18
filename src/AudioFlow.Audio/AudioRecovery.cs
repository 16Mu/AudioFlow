using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AudioFlow.Core;

namespace AudioFlow.Audio;

/// <summary>One journal owner; a temporary worker completes restoration after desktop exit.</summary>
public static class AudioRecovery
{
    private static string StopEvent(string directory) => "Local\\AudioFlow.Recovery." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).TrimEnd('\\').ToUpperInvariant())));

    internal static void StopWorker(string directory)
    {
        if (!EventWaitHandle.TryOpenExisting(StopEvent(directory), out var stop)) return;
        using (stop) stop.Set();
    }

    internal static FileStream Acquire(string directory)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            try { return new FileStream(Path.Combine(directory, "audio-recovery.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(50); }
        }
    }

    public static Process Launch(string executable, string directory, int parentId)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--recover-pending");
        start.ArgumentList.Add(Path.GetFullPath(directory));
        start.ArgumentList.Add(parentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(start) ?? throw new InvalidOperationException("声音恢复助手未能启动，恢复记录已保留。");
    }

    public static async Task RunAsync(string directory, int parentId)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEvent(directory));
        int lastPending = int.MinValue;
        void Status(int pending, bool complete)
        {
            if (pending == lastPending) return;
            var file = Path.Combine(directory, "recovery-worker.json");
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new
                { ProcessId = Environment.ProcessId, Pending = pending, Complete = complete }, SceneStore.JsonOptions));
            File.Move(file + ".tmp", file, true); lastPending = pending;
        }
        Status(-1, false);
        if (parentId > 0)
        {
            Process? parent = null;
            try { parent = Process.GetProcessById(parentId); } catch (ArgumentException) { }
            using (parent)
                while (parent != null && !parent.HasExited)
                {
                    if (stop.WaitOne(0)) return;
                    await Task.Delay(100);
                }
        }
        if (stop.WaitOne(0)) return;
        await using var engine = new AudioEngine(directory, message =>
            File.AppendAllText(Path.Combine(directory, "recovery-errors.log"), message + Environment.NewLine), recoveryOnly: true);
        string? lastError = null;
        while (!stop.WaitOne(0))
        {
            try { await engine.RecoverAsync(); lastError = null; }
            catch (Exception error)
            {
                if (lastError != error.Message) File.AppendAllText(Path.Combine(directory, "recovery-errors.log"), error + Environment.NewLine);
                lastError = error.Message;
            }
            Status(engine.PendingRecoveryCount, engine.PendingRecoveryCount == 0);
            if (engine.PendingRecoveryCount == 0) return;
            await Task.Delay(250);
        }
    }
}
