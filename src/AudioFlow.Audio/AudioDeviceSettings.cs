using System.Runtime.InteropServices;

namespace AudioFlow.Audio;

/// <summary>Small PolicyConfig adapter for explicitly requested device settings.</summary>
public static unsafe class AudioDeviceSettings
{
    private static readonly Guid ClientId = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    private static readonly Guid InterfaceId = new("f8679f50-850a-41cf-9c72-430f290290c8");
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr result);
    public static void SetDefault(string endpointId, int role)
    {
        Invoke((pointer, table) =>
        {
            var text = Marshal.StringToCoTaskMemUni(endpointId);
            try
            {
                var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)table[13];
                Marshal.ThrowExceptionForHR(method(pointer, text, role));
            }
            finally { Marshal.FreeCoTaskMem(text); }
        });
    }
    public static void SetSampleRate(string endpointId, int rate)
    {
        if (rate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(rate));
        Invoke((pointer, table) =>
        {
            var text = Marshal.StringToCoTaskMemUni(endpointId);
            IntPtr original = IntPtr.Zero, replacement = IntPtr.Zero;
            try
            {
                var get = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)table[4];
                Marshal.ThrowExceptionForHR(get(pointer, text, 0, &original));
                int length = 18 + Marshal.ReadInt16(original, 16);
                var bytes = new byte[length]; Marshal.Copy(original, bytes, 0, length);
                replacement = Marshal.AllocCoTaskMem(length); Marshal.Copy(bytes, 0, replacement, length);
                int block = (ushort)Marshal.ReadInt16(original, 12);
                Marshal.WriteInt32(replacement, 4, rate);
                Marshal.WriteInt32(replacement, 8, rate * block);
                var set = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, int>)table[6];
                Marshal.ThrowExceptionForHR(set(pointer, text, replacement, replacement));
            }
            finally
            {
                Marshal.FreeCoTaskMem(original); Marshal.FreeCoTaskMem(replacement); Marshal.FreeCoTaskMem(text);
            }
        });
    }
    private unsafe delegate void WithPolicy(IntPtr pointer, IntPtr* table);
    private static void Invoke(WithPolicy action)
    {
        int init = CoInitializeEx(IntPtr.Zero, 0);
        if (init < 0 && init != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(init);
        IntPtr pointer = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(ClientId, IntPtr.Zero, 23, InterfaceId, out pointer));
            action(pointer, *(IntPtr**)pointer);
        }
        finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); if (init >= 0) CoUninitialize(); }
    }
}
