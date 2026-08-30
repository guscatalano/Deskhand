using System.Runtime.InteropServices;

namespace Deskhand.Core.Services;

public record AudioEndpointDto(string? Name, string? Id, int? VolumePercent, bool? Muted);
public record AudioDefaultsDto(AudioEndpointDto? Playback, AudioEndpointDto? Recording);

/// <summary>
/// Read-only default-audio state via the Core Audio (MMDevice) APIs: the default playback and recording
/// endpoints, each with its friendly name, master volume (0–100 %) and mute state. Nothing is changed —
/// it only reads. Every COM step is guarded, so a missing endpoint or an interop hiccup degrades to nulls
/// rather than throwing.
/// </summary>
public static class AudioService
{
    public static AudioDefaultsDto Defaults() => new(Get(EDataFlow.eRender), Get(EDataFlow.eCapture));

    private static AudioEndpointDto? Get(EDataFlow flow)
    {
        object? enumObj = null, devObj = null, volObj = null, storeObj = null;
        try
        {
            enumObj = new MMDeviceEnumerator();
            var en = (IMMDeviceEnumerator)enumObj;
            if (en.GetDefaultAudioEndpoint(flow, ERole.eMultimedia, out IMMDevice dev) != 0 || dev is null) return null;
            devObj = dev;

            dev.GetId(out string? id);

            int? vol = null; bool? mute = null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out object vo) == 0 && vo is IAudioEndpointVolume v)
            {
                volObj = vo;
                if (v.GetMasterVolumeLevelScalar(out float f) == 0) vol = (int)Math.Round(Math.Clamp(f, 0, 1) * 100);
                if (v.GetMute(out int m) == 0) mute = m != 0;
            }

            string? name = null;
            try
            {
                if (dev.OpenPropertyStore(0 /*STGM_READ*/, out IPropertyStore store) == 0 && store is not null)
                {
                    storeObj = store;
                    var key = PKEY_Device_FriendlyName;
                    if (store.GetValue(ref key, out PROPVARIANT pv) == 0)
                    {
                        if (pv.vt == 31 /*VT_LPWSTR*/ && pv.p != IntPtr.Zero) name = Marshal.PtrToStringUni(pv.p);
                        PropVariantClear(ref pv);
                    }
                }
            }
            catch { }

            return new AudioEndpointDto(name, id, vol, mute);
        }
        catch { return null; }
        finally
        {
            foreach (var o in new[] { storeObj, volObj, devObj, enumObj })
                if (o is not null && Marshal.IsComObject(o)) try { Marshal.ReleaseComObject(o); } catch { }
        }
    }

    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
        // (remaining methods unused)
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    // Only slots up to GetMute (13) matter; earlier ones are placeholders to preserve vtable order.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr n);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr n);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float level, IntPtr ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel();
        [PreserveSig] int SetChannelVolumeLevelScalar();
        [PreserveSig] int GetChannelVolumeLevel();
        [PreserveSig] int GetChannelVolumeLevelScalar();
        [PreserveSig] int SetMute(int mute, IntPtr ctx);
        [PreserveSig] int GetMute(out int mute);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public int pid; }

    // PROPVARIANT: on x64 the value union starts at offset 8 (after vt + 3 reserved WORDs).
    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
    }

    private static PROPERTYKEY PKEY_Device_FriendlyName = new()
    { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pvar);
}
