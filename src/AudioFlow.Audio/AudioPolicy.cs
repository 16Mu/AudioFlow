// Windows 21H2+ ABI layout based on EarTrumpet (MIT).
// See THIRD_PARTY_NOTICES.md. Only per-application routing is modified.
using System.Runtime.InteropServices;

namespace AudioFlow.Audio;

public static unsafe class AudioPolicy
{
    private static readonly Guid FactoryId = new("ab3d4648-e242-459f-b02f-541c70306324");
    private const string DevicePrefix = @"\\?\SWD#MMDEVAPI#";
    private const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string CaptureSuffix = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, uint length, out IntPtr value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern IntPtr WindowsGetStringRawBuffer(IntPtr value, out uint length);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, in Guid iid, out IntPtr factory);

    public static string? GetOutput(int pid, int role) => GetEndpoint(pid, role, 0);
    public static string? GetInput(int pid, int role) => GetEndpoint(pid, role, 1);
    private static string? GetEndpoint(int pid, int role, int flow)
    {
        using var factory = new Factory();
        IntPtr result = IntPtr.Zero;
        var table = *(IntPtr**)factory.Pointer;
        var function = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr*, int>)table[26];
        int hr = function(factory.Pointer, (uint)pid, flow, role, &result);
        // Windows returns E_INVALIDARG when this application has no persisted
        // per-role override. That is the ordinary "use Windows default" state.
        if (hr == unchecked((int)0x80070057)) return null;
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            if (result == IntPtr.Zero) return null;
            var buffer = WindowsGetStringRawBuffer(result, out uint count);
            var value = Marshal.PtrToStringUni(buffer, (int)count);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        finally { WindowsDeleteString(result); }
    }
    public static void SetOutput(int pid, int role, string? endpointId) => SetEndpoint(pid, role, endpointId, 0);
    public static void SetInput(int pid, int role, string? endpointId) => SetEndpoint(pid, role, endpointId, 1);
    private static void SetEndpoint(int pid, int role, string? endpointId, int flow)
    {
        using var factory = new Factory();
        IntPtr value = IntPtr.Zero;
        if (!string.IsNullOrWhiteSpace(endpointId))
        {
            string packaged = endpointId.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase)
                ? endpointId : DevicePrefix + endpointId + (flow == 0 ? RenderSuffix : CaptureSuffix);
            Marshal.ThrowExceptionForHR(WindowsCreateString(packaged, (uint)packaged.Length, out value));
        }
        try
        {
            var table = *(IntPtr**)factory.Pointer;
            var function = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int, IntPtr, int>)table[25];
            Marshal.ThrowExceptionForHR(function(factory.Pointer, (uint)pid, flow, role, value));
        }
        finally { WindowsDeleteString(value); }
    }
    public static string? Unpack(string? value) =>
        value?.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase) == true
            ? value[DevicePrefix.Length..].Replace(RenderSuffix, "", StringComparison.OrdinalIgnoreCase).Replace(CaptureSuffix, "", StringComparison.OrdinalIgnoreCase) : value;

    private sealed class Factory : IDisposable
    {
        public IntPtr Pointer { get; }
        private readonly bool initialized;
        public Factory()
        {
            int hr = RoInitialize(1);
            initialized = hr >= 0;
            if (hr < 0 && hr != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(hr);
            const string name = "Windows.Media.Internal.AudioPolicyConfig";
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, (uint)name.Length, out var value));
            try
            {
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(value, FactoryId, out var pointer));
                Pointer = pointer;
            }
            finally { WindowsDeleteString(value); }
        }
        public void Dispose()
        {
            if (Pointer != IntPtr.Zero) Marshal.Release(Pointer);
            if (initialized) RoUninitialize();
        }
    }
}
