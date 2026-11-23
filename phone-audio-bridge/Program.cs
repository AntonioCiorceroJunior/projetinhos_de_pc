using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PhoneAudioBridge
{
    #region Data Models & Native Protocol Constants

    internal enum AdaptiveState
    {
        Warmup = 0,
        Monitoring = 1,
        ProbingDown = 2,
        PromptPending = 3,
        Stabilizing = 4,
        FloorLocked = 5,
        Manual = 6
    }

    internal sealed class AudioDeviceInfo
    {
        public string Id;
        public string Name;
        public bool IsDefault;

        public override string ToString()
        {
            return Name;
        }
    }

    internal sealed class AdbDeviceInfo
    {
        public string Serial;
        public string ModelFriendly;

        public string DisplayLabel
        {
            get
            {
                if (string.IsNullOrEmpty(ModelFriendly))
                {
                    return string.Format("Android USB ({0})", Serial);
                }
                return string.Format("{0} ({1})", ModelFriendly, Serial);
            }
        }

        public override string ToString()
        {
            return DisplayLabel;
        }
    }

    internal sealed class ReceiverStats
    {
        public uint SeqLast;
        public double BufMs;
        public double MinBufMs;
        public int PktGapMaxMs;
        public int AAudioBurstFrames;
        public int AAudioBufFrames;
        public int AAudioFlags;
        public uint PlcTotal;
        public uint XRunTotal;
        public uint ResyncTotal;
        public uint AAudioSampleRate;
        public int RttMs;

        public bool IsLowLatency { get { return (AAudioFlags & 1) != 0; } }
        public bool IsExclusive { get { return (AAudioFlags & 2) != 0; } }
        public bool IsMmap { get { return (AAudioFlags & 4) != 0; } }
        public uint TotalGlitches { get { return PlcTotal + XRunTotal + ResyncTotal; } }
    }

    internal static class BridgeProtocol
    {
        public const byte MsgConfig = 0x01;
        public const byte MsgPcm = 0x02;
        public const byte MsgSetTargetMs = 0x03;
        public const byte MsgSetGain = 0x04;
        public const byte MsgPing = 0x05;
        public const byte MsgStop = 0x06;

        public const byte MsgStats = 0x81;
        public const byte MsgPong = 0x85;
        public const byte MsgDisconnectedByPhone = 0x86;

        public static void WriteU16(byte[] dst, int offset, ushort val)
        {
            dst[offset] = (byte)(val & 0xFF);
            dst[offset + 1] = (byte)((val >> 8) & 0xFF);
        }

        public static void WriteU32(byte[] dst, int offset, uint val)
        {
            dst[offset] = (byte)(val & 0xFF);
            dst[offset + 1] = (byte)((val >> 8) & 0xFF);
            dst[offset + 2] = (byte)((val >> 16) & 0xFF);
            dst[offset + 3] = (byte)((val >> 24) & 0xFF);
        }

        public static void WriteI64(byte[] dst, int offset, long val)
        {
            ulong u = (ulong)val;
            for (int i = 0; i < 8; i++)
            {
                dst[offset + i] = (byte)((u >> (i * 8)) & 0xFF);
            }
        }

        public static ushort ReadU16(byte[] src, int offset)
        {
            return (ushort)(src[offset] | (src[offset + 1] << 8));
        }

        public static uint ReadU32(byte[] src, int offset)
        {
            return (uint)(src[offset] |
                          (src[offset + 1] << 8) |
                          (src[offset + 2] << 16) |
                          (src[offset + 3] << 24));
        }

        public static long ReadI64(byte[] src, int offset)
        {
            ulong u = 0;
            for (int i = 0; i < 8; i++)
            {
                u |= ((ulong)src[offset + i]) << (i * 8);
            }
            return (long)u;
        }
    }

    #endregion

    #region Native Win32 & WASAPI COM Interop

    internal static class NativeWin32
    {
        public const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int dwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);

        [DllImport("ole32.dll")]
        public static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

        [DllImport("ole32.dll")]
        public static extern void CoUninitialize();

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant pvar);

        [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        public static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(Guid guid, uint propertyId)
        {
            fmtid = guid;
            pid = propertyId;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p1;
        public IntPtr p2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr pClient);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out int pcDevices);

        [PreserveSig]
        int Item(int nDevice, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

        [PreserveSig]
        int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

        [PreserveSig]
        int GetState(out int pdwState);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out int cProps);

        [PreserveSig]
        int GetAt(int iProp, out PROPERTYKEY pkey);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PropVariant pv);

        [PreserveSig]
        int SetValue(ref PROPERTYKEY key, ref PropVariant pv);

        [PreserveSig]
        int Commit();
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint pNumBufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long phnsLatency);

        [PreserveSig]
        int GetCurrentPadding(out uint pNumPaddingFrames);

        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr ppDeviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);

        [PreserveSig]
        int ReleaseBuffer(uint numFramesRead);

        [PreserveSig]
        int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    [ComImport]
    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig]
        int GetBuffer(uint numFramesRequested, out IntPtr ppData);

        [PreserveSig]
        int ReleaseBuffer(uint numFramesWritten, uint dwFlags);
    }

    #endregion

    #region WASAPI Loopback Capture Engine (10 ms Event-Driven + Silent Render Clock Anchor)

    internal sealed class WasapiLoopbackCapture : IDisposable
    {
        private const int ERender = 0;
        private const int ERoleConsole = 0;
        private const int DeviceStateActive = 0x00000001;
        private const int StgmRead = 0x00000000;
        private const int ClsCtxAll = 23;
        private const int AudclntShareModeShared = 0;
        private const int AudclntStreamFlagsLoopback = 0x00020000;
        private const int AudclntStreamFlagsEventCallback = 0x00040000;
        private const uint AudclntBufferFlagsSilent = 0x2;
        private const int AudclntDeviceInvalidated = unchecked((int)0x88890004);
        private const ushort WaveFormatIeeeFloat = 3;
        private const ushort WaveFormatExtensible = 0xFFFE;

        private static readonly Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static readonly Guid IidAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        private static readonly Guid IidAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        private static readonly Guid SubFormatIeeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid SubFormatPcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private static readonly PROPERTYKEY PkeyDeviceFriendlyName = new PROPERTYKEY(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

        private readonly object _sync = new object();
        private volatile bool _running;
        private volatile bool _restartRequested;
        private const int ChunkMs = 10;
        private string _requestedDeviceId = "";
        private Thread _thread;

        public volatile float CurrentLevel;
        public volatile int CurrentSampleRate = 48000;
        public volatile string ActiveDeviceName = "Padrão do Windows";
        public volatile string LastError = "";

        public Action<byte[], int> OnPcmPacket;
        public Action<int, string> OnFormatChanged;

        public void SelectDevice(string deviceId)
        {
            lock (_sync)
            {
                string norm = deviceId ?? "";
                if (!string.Equals(_requestedDeviceId, norm, StringComparison.Ordinal))
                {
                    _requestedDeviceId = norm;
                    _restartRequested = true;
                }
            }
        }

        public static List<AudioDeviceInfo> ListOutputDevices()
        {
            var result = new List<AudioDeviceInfo>();
            result.Add(new AudioDeviceInfo
            {
                Id = "",
                Name = "Padrão do Windows (automático)",
                IsDefault = true
            });

            int hrCo = NativeWin32.CoInitializeEx(IntPtr.Zero, 0);
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                string defaultId = "";
                IMMDevice defDev;
                if (enumerator.GetDefaultAudioEndpoint(ERender, ERoleConsole, out defDev) == 0 && defDev != null)
                {
                    string did;
                    if (defDev.GetId(out did) == 0 && did != null)
                    {
                        defaultId = did;
                    }
                    Marshal.ReleaseComObject(defDev);
                }

                IMMDeviceCollection collection;
                if (enumerator.EnumAudioEndpoints(ERender, DeviceStateActive, out collection) == 0 && collection != null)
                {
                    int count;
                    if (collection.GetCount(out count) == 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            IMMDevice dev;
                            if (collection.Item(i, out dev) == 0 && dev != null)
                            {
                                try
                                {
                                    string id;
                                    if (dev.GetId(out id) != 0 || string.IsNullOrEmpty(id))
                                    {
                                        continue;
                                    }

                                    string friendly = ReadFriendlyName(dev);
                                    if (string.IsNullOrEmpty(friendly))
                                    {
                                        friendly = id;
                                    }

                                    bool isDef = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                                    result.Add(new AudioDeviceInfo
                                    {
                                        Id = id,
                                        Name = isDef ? friendly + " [Padrão]" : friendly,
                                        IsDefault = isDef
                                    });
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(dev);
                                }
                            }
                        }
                    }
                    Marshal.ReleaseComObject(collection);
                }
                Marshal.ReleaseComObject(enumerator);
            }
            catch
            {
            }
            finally
            {
                if (hrCo == 0 || hrCo == 1)
                {
                    NativeWin32.CoUninitialize();
                }
            }

            return result;
        }

        public static string ProbeLoopbackQuick(string deviceId)
        {
            int hrCo = NativeWin32.CoInitializeEx(IntPtr.Zero, 0);
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient audioClient = null;
            IAudioCaptureClient captureClient = null;
            IntPtr mixFormatPtr = IntPtr.Zero;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                int hr = string.IsNullOrEmpty(deviceId)
                    ? enumerator.GetDefaultAudioEndpoint(ERender, ERoleConsole, out device)
                    : enumerator.GetDevice(deviceId, out device);

                if (hr != 0 || device == null)
                {
                    return string.Format("ERR GetEndpoint HR=0x{0:X8}", hr);
                }

                string friendly = ReadFriendlyName(device);
                object clientObj;
                Guid iidClient = IidAudioClient;
                hr = device.Activate(ref iidClient, ClsCtxAll, IntPtr.Zero, out clientObj);
                if (hr != 0 || clientObj == null)
                {
                    return string.Format("ERR Activate IAudioClient HR=0x{0:X8}", hr);
                }
                audioClient = (IAudioClient)clientObj;

                hr = audioClient.GetMixFormat(out mixFormatPtr);
                if (hr != 0 || mixFormatPtr == IntPtr.Zero)
                {
                    return string.Format("ERR GetMixFormat HR=0x{0:X8}", hr);
                }

                WAVEFORMATEX wfx = (WAVEFORMATEX)Marshal.PtrToStructure(mixFormatPtr, typeof(WAVEFORMATEX));
                bool eventDriven = false;
                AutoResetEvent probeEvent = null;
                try
                {
                    hr = audioClient.Initialize(AudclntShareModeShared, AudclntStreamFlagsLoopback | AudclntStreamFlagsEventCallback, 200000, 0, mixFormatPtr, IntPtr.Zero);
                    if (hr == 0)
                    {
                        probeEvent = new AutoResetEvent(false);
                        if (audioClient.SetEventHandle(probeEvent.SafeWaitHandle.DangerousGetHandle()) == 0)
                        {
                            eventDriven = true;
                        }
                    }

                    if (!eventDriven)
                    {
                        if (probeEvent != null) { probeEvent.Close(); probeEvent = null; }
                        Marshal.ReleaseComObject(audioClient);
                        audioClient = null;
                        hr = device.Activate(ref iidClient, ClsCtxAll, IntPtr.Zero, out clientObj);
                        if (hr != 0 || clientObj == null)
                        {
                            return string.Format("ERR ReActivate IAudioClient HR=0x{0:X8}", hr);
                        }
                        audioClient = (IAudioClient)clientObj;
                        hr = audioClient.Initialize(AudclntShareModeShared, AudclntStreamFlagsLoopback, 200000, 0, mixFormatPtr, IntPtr.Zero);
                        if (hr != 0)
                        {
                            return string.Format("ERR Initialize Loopback HR=0x{0:X8}", hr);
                        }
                    }

                    object capObj;
                    Guid iidCap = IidAudioCaptureClient;
                    hr = audioClient.GetService(ref iidCap, out capObj);
                    if (hr != 0 || capObj == null)
                    {
                        return string.Format("ERR GetService IAudioCaptureClient HR=0x{0:X8}", hr);
                    }
                    captureClient = (IAudioCaptureClient)capObj;

                    hr = audioClient.Start();
                    if (hr != 0)
                    {
                        return string.Format("ERR Start HR=0x{0:X8}", hr);
                    }
                    if (probeEvent != null) probeEvent.WaitOne(25);
                    else Thread.Sleep(25);
                    uint nextPkt;
                    captureClient.GetNextPacketSize(out nextPkt);
                    audioClient.Stop();

                    return string.Format("OK device=\"{0}\" rate={1}Hz ch={2} bits={3} tag=0x{4:X} eventDriven={5}", friendly, wfx.nSamplesPerSec, wfx.nChannels, wfx.wBitsPerSample, wfx.wFormatTag, eventDriven);
                }
                finally
                {
                    if (probeEvent != null) probeEvent.Close();
                }
            }
            catch (Exception ex)
            {
                return "ERR Exception: " + ex.Message;
            }
            finally
            {
                if (mixFormatPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormatPtr);
                if (captureClient != null) Marshal.ReleaseComObject(captureClient);
                if (audioClient != null) Marshal.ReleaseComObject(audioClient);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
                if (hrCo == 0 || hrCo == 1) NativeWin32.CoUninitialize();
            }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(CaptureThreadMain);
            _thread.IsBackground = true;
            _thread.Name = "WASAPI-Loopback-Capture";
            _thread.Priority = ThreadPriority.Highest;
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null && _thread.IsAlive)
            {
                _thread.Join(1200);
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private static string ReadFriendlyName(IMMDevice device)
        {
            IPropertyStore props = null;
            PropVariant pv = new PropVariant();
            try
            {
                if (device.OpenPropertyStore(StgmRead, out props) != 0 || props == null)
                {
                    return "";
                }
                PROPERTYKEY key = PkeyDeviceFriendlyName;
                if (props.GetValue(ref key, out pv) == 0 && pv.vt == 31 && pv.p1 != IntPtr.Zero)
                {
                    return Marshal.PtrToStringUni(pv.p1) ?? "";
                }
            }
            catch
            {
            }
            finally
            {
                try { NativeWin32.PropVariantClear(ref pv); } catch { }
                if (props != null) Marshal.ReleaseComObject(props);
            }
            return "";
        }

        private void CaptureThreadMain()
        {
            int hrCo = NativeWin32.CoInitializeEx(IntPtr.Zero, 0);
            bool timerPeriodSet = false;
            IntPtr mmcssHandle = IntPtr.Zero;
            try
            {
                timerPeriodSet = NativeWin32.timeBeginPeriod(1) == 0;
            }
            catch
            {
            }
            try
            {
                uint taskIdx = 0;
                mmcssHandle = NativeWin32.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIdx);
            }
            catch
            {
            }

            try
            {
                while (_running)
                {
                    _restartRequested = false;
                    try
                    {
                        RunCaptureSession();
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                    }

                    CurrentLevel = 0f;
                    if (_running && !_restartRequested)
                    {
                        Thread.Sleep(500);
                    }
                }
            }
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                {
                    try { NativeWin32.AvRevertMmThreadCharacteristics(mmcssHandle); } catch { }
                }
                if (timerPeriodSet)
                {
                    try { NativeWin32.timeEndPeriod(1); } catch { }
                }
                if (hrCo == 0 || hrCo == 1)
                {
                    NativeWin32.CoUninitialize();
                }
            }
        }

        private void RunCaptureSession()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient audioClient = null;
            IAudioCaptureClient captureClient = null;
            IAudioClient silentRenderAc = null;
            IAudioRenderClient silentRenderClient = null;
            IntPtr mixFormatPtr = IntPtr.Zero;
            IntPtr silentRenderFmt = IntPtr.Zero;
            uint silentRenderBufFrames = 0;
            AutoResetEvent audioEvent = null;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                string targetId;
                lock (_sync)
                {
                    targetId = _requestedDeviceId;
                }

                int hr;
                if (string.IsNullOrEmpty(targetId))
                {
                    hr = enumerator.GetDefaultAudioEndpoint(ERender, ERoleConsole, out device);
                }
                else
                {
                    hr = enumerator.GetDevice(targetId, out device);
                    if (hr != 0 || device == null)
                    {
                        hr = enumerator.GetDefaultAudioEndpoint(ERender, ERoleConsole, out device);
                    }
                }

                if (hr != 0 || device == null)
                {
                    LastError = string.Format("Nenhum dispositivo de saída ativo (0x{0:X8}).", hr);
                    return;
                }

                string friendly = ReadFriendlyName(device);
                if (string.IsNullOrEmpty(friendly))
                {
                    friendly = "Saída padrão do Windows";
                }
                ActiveDeviceName = friendly;

                // Silent shared-mode render anchor keeps Windows audio engine clocking loopback at exact 48 kHz even during silence
                try
                {
                    object renAcObj;
                    Guid iidAc = IidAudioClient;
                    if (device.Activate(ref iidAc, ClsCtxAll, IntPtr.Zero, out renAcObj) == 0 && renAcObj != null)
                    {
                        silentRenderAc = (IAudioClient)renAcObj;
                        if (silentRenderAc.GetMixFormat(out silentRenderFmt) == 0 &&
                            silentRenderFmt != IntPtr.Zero &&
                            silentRenderAc.Initialize(AudclntShareModeShared, 0, 1000000, 0, silentRenderFmt, IntPtr.Zero) == 0 &&
                            silentRenderAc.GetBufferSize(out silentRenderBufFrames) == 0 &&
                            silentRenderBufFrames > 0)
                        {
                            object rcObj;
                            Guid iidRc = IidAudioRenderClient;
                            if (silentRenderAc.GetService(ref iidRc, out rcObj) == 0 && rcObj != null)
                            {
                                silentRenderClient = (IAudioRenderClient)rcObj;
                                IntPtr rPtr;
                                if (silentRenderClient.GetBuffer(silentRenderBufFrames, out rPtr) == 0)
                                {
                                    silentRenderClient.ReleaseBuffer(silentRenderBufFrames, AudclntBufferFlagsSilent);
                                }
                                if (silentRenderAc.Start() != 0)
                                {
                                    Marshal.ReleaseComObject(silentRenderClient);
                                    silentRenderClient = null;
                                }
                            }
                        }
                    }
                    if (silentRenderClient == null && silentRenderAc != null)
                    {
                        Marshal.ReleaseComObject(silentRenderAc);
                        silentRenderAc = null;
                    }
                }
                catch
                {
                    if (silentRenderClient != null) { try { Marshal.ReleaseComObject(silentRenderClient); } catch { } silentRenderClient = null; }
                    if (silentRenderAc != null) { try { Marshal.ReleaseComObject(silentRenderAc); } catch { } silentRenderAc = null; }
                }

                object clientObj;
                Guid iidClient = IidAudioClient;
                hr = device.Activate(ref iidClient, ClsCtxAll, IntPtr.Zero, out clientObj);
                if (hr != 0 || clientObj == null)
                {
                    LastError = string.Format("Falha ao ativar áudio (0x{0:X8}).", hr);
                    return;
                }
                audioClient = (IAudioClient)clientObj;

                hr = audioClient.GetMixFormat(out mixFormatPtr);
                if (hr != 0 || mixFormatPtr == IntPtr.Zero)
                {
                    LastError = string.Format("Falha em GetMixFormat (0x{0:X8}).", hr);
                    return;
                }

                WAVEFORMATEX wfx = (WAVEFORMATEX)Marshal.PtrToStructure(mixFormatPtr, typeof(WAVEFORMATEX));
                bool isFloat = DetectFloatFormat(mixFormatPtr, wfx);
                int srcRate = (int)wfx.nSamplesPerSec;
                int srcChannels = (int)wfx.nChannels;
                int srcBits = (int)wfx.wBitsPerSample;
                int blockAlign = (int)wfx.nBlockAlign;

                int decimFactor = 1;
                int outRate = srcRate;
                while (outRate > 48000 && (outRate % 2) == 0)
                {
                    outRate /= 2;
                    decimFactor *= 2;
                }
                if (outRate > 48000)
                {
                    outRate = 48000;
                }

                CurrentSampleRate = outRate;
                if (OnFormatChanged != null)
                {
                    OnFormatChanged(outRate, friendly);
                }

                hr = audioClient.Initialize(AudclntShareModeShared, AudclntStreamFlagsLoopback | AudclntStreamFlagsEventCallback, 200000, 0, mixFormatPtr, IntPtr.Zero);
                if (hr == 0)
                {
                    audioEvent = new AutoResetEvent(false);
                    if (audioClient.SetEventHandle(audioEvent.SafeWaitHandle.DangerousGetHandle()) != 0)
                    {
                        audioEvent.Close();
                        audioEvent = null;
                    }
                }

                if (audioEvent == null)
                {
                    Marshal.ReleaseComObject(audioClient);
                    audioClient = null;
                    hr = device.Activate(ref iidClient, ClsCtxAll, IntPtr.Zero, out clientObj);
                    if (hr != 0 || clientObj == null)
                    {
                        LastError = string.Format("Falha ao reativar áudio (0x{0:X8}).", hr);
                        return;
                    }
                    audioClient = (IAudioClient)clientObj;
                    hr = audioClient.Initialize(AudclntShareModeShared, AudclntStreamFlagsLoopback, 200000, 0, mixFormatPtr, IntPtr.Zero);
                    if (hr != 0)
                    {
                        LastError = string.Format("Falha em Initialize Loopback (0x{0:X8}).", hr);
                        return;
                    }
                }

                object capObj;
                Guid iidCap = IidAudioCaptureClient;
                hr = audioClient.GetService(ref iidCap, out capObj);
                if (hr != 0 || capObj == null)
                {
                    LastError = string.Format("Falha em GetService Capture (0x{0:X8}).", hr);
                    return;
                }
                captureClient = (IAudioCaptureClient)capObj;

                hr = audioClient.Start();
                if (hr != 0)
                {
                    LastError = string.Format("Falha ao iniciar captura (0x{0:X8}).", hr);
                    return;
                }

                LastError = "";

                int targetChunkBytes = CalculateChunkBytes(outRate, ChunkMs);
                byte[] silentChunk = new byte[targetChunkBytes];
                byte[] accum = new byte[targetChunkBytes * 4];
                int accumOffset = 0;

                byte[] rawScratch = new byte[65536];
                Stopwatch pacingWatch = Stopwatch.StartNew();
                long lastEmitMs = pacingWatch.ElapsedMilliseconds;
                long lastAudioDataMs = lastEmitMs;
                long lastDefaultCheckMs = lastEmitMs;

                string activeDevId = "";
                device.GetId(out activeDevId);

                while (_running && !_restartRequested)
                {
                    if (silentRenderAc != null && silentRenderClient != null && silentRenderBufFrames > 0)
                    {
                        uint pad;
                        if (silentRenderAc.GetCurrentPadding(out pad) == 0 && pad < silentRenderBufFrames)
                        {
                            uint avail = silentRenderBufFrames - pad;
                            IntPtr rPtr;
                            if (silentRenderClient.GetBuffer(avail, out rPtr) == 0)
                            {
                                silentRenderClient.ReleaseBuffer(avail, AudclntBufferFlagsSilent);
                            }
                        }
                    }

                    uint nextFrames;
                    hr = captureClient.GetNextPacketSize(out nextFrames);
                    if (hr == AudclntDeviceInvalidated)
                    {
                        break;
                    }
                    if (hr != 0)
                    {
                        LastError = string.Format("GetNextPacketSize erro 0x{0:X8}", hr);
                        break;
                    }

                    if (nextFrames == 0)
                    {
                        long nowMs = pacingWatch.ElapsedMilliseconds;
                        if ((nowMs - lastAudioDataMs) > 250)
                        {
                            CurrentLevel = 0f;
                        }

                        // Fallback keepalive ONLY if silent render anchor could not be opened on this driver
                        if (silentRenderAc == null && (nowMs - lastEmitMs) >= 45)
                        {
                            accumOffset = 0;
                            lastEmitMs = nowMs;
                            if (OnPcmPacket != null)
                            {
                                OnPcmPacket(silentChunk, outRate);
                            }
                        }

                        if (string.IsNullOrEmpty(targetId) && (nowMs - lastDefaultCheckMs) > 1500)
                        {
                            lastDefaultCheckMs = nowMs;
                            IMMDevice curDef;
                            if (enumerator.GetDefaultAudioEndpoint(ERender, ERoleConsole, out curDef) == 0 && curDef != null)
                            {
                                string curDefId = "";
                                curDef.GetId(out curDefId);
                                Marshal.ReleaseComObject(curDef);
                                if (!string.IsNullOrEmpty(curDefId) && !string.Equals(curDefId, activeDevId, StringComparison.OrdinalIgnoreCase))
                                {
                                    break;
                                }
                            }
                        }

                        if (audioEvent != null)
                        {
                            audioEvent.WaitOne(12);
                        }
                        else
                        {
                            Thread.Sleep(1);
                        }
                        continue;
                    }

                    while (nextFrames > 0 && _running && !_restartRequested)
                    {
                        IntPtr dataPtr;
                        uint numFrames;
                        uint flags;
                        ulong devPos;
                        ulong qpcPos;
                        hr = captureClient.GetBuffer(out dataPtr, out numFrames, out flags, out devPos, out qpcPos);
                        if (hr == AudclntDeviceInvalidated || hr != 0)
                        {
                            return;
                        }

                        try
                        {
                            int frames = (int)numFrames;
                            if (frames > 0)
                            {
                                bool isSilentFlag = (flags & AudclntBufferFlagsSilent) != 0;
                                int outFrames = (frames + decimFactor - 1) / decimFactor;
                                int pcmBytes = outFrames * 4;

                                if (accumOffset == 0 && pcmBytes == targetChunkBytes && !isSilentFlag && dataPtr != IntPtr.Zero)
                                {
                                    int rawBytesFast = frames * blockAlign;
                                    if (rawBytesFast > rawScratch.Length)
                                    {
                                        rawScratch = new byte[rawBytesFast];
                                    }
                                    Marshal.Copy(dataPtr, rawScratch, 0, rawBytesFast);
                                    byte[] directChunk = new byte[targetChunkBytes];
                                    float peakFast = ConvertToInt16Stereo(
                                        rawScratch,
                                        frames,
                                        srcChannels,
                                        srcBits,
                                        blockAlign,
                                        isFloat,
                                        decimFactor,
                                        directChunk,
                                        0);
                                    CurrentLevel = CurrentLevel * 0.65f + Math.Min(1f, peakFast * 1.4f) * 0.35f;
                                    long nowEmit = pacingWatch.ElapsedMilliseconds;
                                    lastEmitMs = nowEmit;
                                    lastAudioDataMs = nowEmit;
                                    if (OnPcmPacket != null)
                                    {
                                        OnPcmPacket(directChunk, outRate);
                                    }
                                }
                                else
                                {
                                    if (accumOffset + pcmBytes > accum.Length)
                                    {
                                        byte[] grown = new byte[(accumOffset + pcmBytes) * 2];
                                        Buffer.BlockCopy(accum, 0, grown, 0, accumOffset);
                                        accum = grown;
                                    }

                                    float peak = 0f;
                                    if (!isSilentFlag && dataPtr != IntPtr.Zero)
                                    {
                                        int rawBytes = frames * blockAlign;
                                        if (rawBytes > rawScratch.Length)
                                        {
                                            rawScratch = new byte[rawBytes];
                                        }
                                        Marshal.Copy(dataPtr, rawScratch, 0, rawBytes);

                                        peak = ConvertToInt16Stereo(
                                            rawScratch,
                                            frames,
                                            srcChannels,
                                            srcBits,
                                            blockAlign,
                                            isFloat,
                                            decimFactor,
                                            accum,
                                            accumOffset);
                                        lastAudioDataMs = pacingWatch.ElapsedMilliseconds;
                                    }
                                    else
                                    {
                                        Array.Clear(accum, accumOffset, pcmBytes);
                                    }

                                    CurrentLevel = CurrentLevel * 0.65f + Math.Min(1f, peak * 1.4f) * 0.35f;
                                    accumOffset += pcmBytes;
                                    int sendOffset = 0;
                                    while (accumOffset - sendOffset >= targetChunkBytes)
                                    {
                                        byte[] chunk = new byte[targetChunkBytes];
                                        Buffer.BlockCopy(accum, sendOffset, chunk, 0, targetChunkBytes);
                                        lastEmitMs = pacingWatch.ElapsedMilliseconds;
                                        if (OnPcmPacket != null)
                                        {
                                            OnPcmPacket(chunk, outRate);
                                        }
                                        sendOffset += targetChunkBytes;
                                    }

                                    if (sendOffset > 0)
                                    {
                                        int rem = accumOffset - sendOffset;
                                        if (rem > 0)
                                        {
                                            Buffer.BlockCopy(accum, sendOffset, accum, 0, rem);
                                        }
                                        accumOffset = rem;
                                    }
                                }
                            }
                        }
                        finally
                        {
                            captureClient.ReleaseBuffer(numFrames);
                        }

                        hr = captureClient.GetNextPacketSize(out nextFrames);
                        if (hr != 0)
                        {
                            nextFrames = 0;
                        }
                    }
                }
            }
            finally
            {
                if (audioClient != null)
                {
                    try { audioClient.Stop(); } catch { }
                }
                if (silentRenderAc != null)
                {
                    try { silentRenderAc.Stop(); } catch { }
                }
                if (audioEvent != null)
                {
                    try { audioEvent.Close(); } catch { }
                }
                if (mixFormatPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormatPtr);
                if (silentRenderFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(silentRenderFmt);
                if (captureClient != null) Marshal.ReleaseComObject(captureClient);
                if (audioClient != null) Marshal.ReleaseComObject(audioClient);
                if (silentRenderClient != null) Marshal.ReleaseComObject(silentRenderClient);
                if (silentRenderAc != null) Marshal.ReleaseComObject(silentRenderAc);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        private static int CalculateChunkBytes(int sampleRate, int chunkMs)
        {
            int frames = (sampleRate * chunkMs) / 1000;
            if (frames < 64) frames = 64;
            return frames * 4;
        }

        private static bool DetectFloatFormat(IntPtr fmtPtr, WAVEFORMATEX wfx)
        {
            if (wfx.wFormatTag == WaveFormatIeeeFloat)
            {
                return true;
            }
            if (wfx.wFormatTag == WaveFormatExtensible && wfx.cbSize >= 22)
            {
                byte[] guidBytes = new byte[16];
                IntPtr subPtr = new IntPtr(fmtPtr.ToInt64() + 24);
                Marshal.Copy(subPtr, guidBytes, 0, 16);
                Guid subFormat = new Guid(guidBytes);
                if (subFormat == SubFormatIeeeFloat) return true;
                if (subFormat == SubFormatPcm) return false;
            }
            return wfx.wBitsPerSample == 32;
        }

        private static float ConvertToInt16Stereo(
            byte[] src,
            int frames,
            int srcChannels,
            int srcBits,
            int blockAlign,
            bool isFloat,
            int decimFactor,
            byte[] dst,
            int dstOffset)
        {
            float peak = 0f;
            int outIdx = dstOffset;

            for (int f = 0; f < frames; f += decimFactor)
            {
                int frameOffset = f * blockAlign;
                float left = 0f;
                float right = 0f;

                if (srcChannels == 1)
                {
                    left = ReadSampleNormalized(src, frameOffset, srcBits, isFloat);
                    right = left;
                }
                else if (srcChannels == 2)
                {
                    int step = blockAlign / srcChannels;
                    left = ReadSampleNormalized(src, frameOffset, srcBits, isFloat);
                    right = ReadSampleNormalized(src, frameOffset + step, srcBits, isFloat);
                }
                else
                {
                    int step = blockAlign / srcChannels;
                    float fl = ReadSampleNormalized(src, frameOffset, srcBits, isFloat);
                    float fr = ReadSampleNormalized(src, frameOffset + step, srcBits, isFloat);
                    float fc = srcChannels >= 3 ? ReadSampleNormalized(src, frameOffset + step * 2, srcBits, isFloat) : 0f;
                    float rl = srcChannels >= 5 ? ReadSampleNormalized(src, frameOffset + step * 4, srcBits, isFloat) : 0f;
                    float rr = srcChannels >= 6 ? ReadSampleNormalized(src, frameOffset + step * 5, srcBits, isFloat) : 0f;
                    float sl = srcChannels >= 7 ? ReadSampleNormalized(src, frameOffset + step * 6, srcBits, isFloat) : 0f;
                    float sr = srcChannels >= 8 ? ReadSampleNormalized(src, frameOffset + step * 7, srcBits, isFloat) : 0f;
                    left = fl + fc * 0.707f + (rl + sl) * 0.5f;
                    right = fr + fc * 0.707f + (rr + sr) * 0.5f;
                }

                float absL = left < 0f ? -left : left;
                float absR = right < 0f ? -right : right;
                if (absL > peak) peak = absL;
                if (absR > peak) peak = absR;

                if (left > 1f) left = 1f;
                else if (left < -1f) left = -1f;
                if (right > 1f) right = 1f;
                else if (right < -1f) right = -1f;

                short sL = (short)(left * 32767f);
                short sR = (short)(right * 32767f);

                dst[outIdx] = (byte)(sL & 0xFF);
                dst[outIdx + 1] = (byte)((sL >> 8) & 0xFF);
                dst[outIdx + 2] = (byte)(sR & 0xFF);
                dst[outIdx + 3] = (byte)((sR >> 8) & 0xFF);
                outIdx += 4;
            }

            return peak;
        }

        private static float ReadSampleNormalized(byte[] buf, int offset, int bits, bool isFloat)
        {
            if (isFloat && bits == 32)
            {
                return BitConverter.ToSingle(buf, offset);
            }
            if (!isFloat && bits == 16)
            {
                short s = (short)(buf[offset] | (buf[offset + 1] << 8));
                return s / 32768f;
            }
            if (!isFloat && bits == 32)
            {
                int val = BitConverter.ToInt32(buf, offset);
                return val / 2147483648f;
            }
            if (!isFloat && bits == 24)
            {
                int b0 = buf[offset];
                int b1 = buf[offset + 1];
                int b2 = (sbyte)buf[offset + 2];
                int val = b0 | (b1 << 8) | (b2 << 16);
                return val / 8388608f;
            }
            return 0f;
        }
    }

    #endregion

    #region ADB Discovery, Embedded APK Auto-Deploy & Tunnel Helper

    internal static class AdbHelper
    {
        public const string PackageName = "com.antoniociorcero.phoneaudiobridge";
        public const string ActivityName = "com.antoniociorcero.phoneaudiobridge/.BridgeActivity";
        public const string AbstractSocket = "localabstract:phoneaudiobridge";
        public const string ExpectedVersionCodeMarker = "versionCode=2";
        public const string ResourceName = "PhoneAudioReceiver.apk";

        public static string FindAdbExecutable()
        {
            try
            {
                string appDir = Path.GetDirectoryName(Application.ExecutablePath);
                if (!string.IsNullOrEmpty(appDir))
                {
                    string localAdb = Path.Combine(appDir, "adb.exe");
                    if (File.Exists(localAdb)) return localAdb;
                }

                string[] candidates = new string[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Android\Sdk\platform-tools\adb.exe"),
                    @"C:\platform-tools\adb.exe",
                    @"C:\Android\platform-tools\adb.exe",
                    @"C:\Android\Sdk\platform-tools\adb.exe"
                };
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (File.Exists(candidates[i])) return candidates[i];
                }

                string[] pathSources = new string[]
                {
                    Environment.GetEnvironmentVariable("PATH") ?? "",
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "",
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? ""
                };
                for (int s = 0; s < pathSources.Length; s++)
                {
                    string[] parts = pathSources[s].Split(';');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string dir = Environment.ExpandEnvironmentVariables(parts[i].Trim().Trim('"'));
                        if (dir.Length == 0) continue;
                        string p = Path.Combine(dir, "adb.exe");
                        if (File.Exists(p)) return p;
                    }
                }

                string wingetPkgs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages");
                if (Directory.Exists(wingetPkgs))
                {
                    string[] dirs = Directory.GetDirectories(wingetPkgs, "Google.PlatformTools*");
                    for (int i = 0; i < dirs.Length; i++)
                    {
                        string p = Path.Combine(dirs[i], @"platform-tools\adb.exe");
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch
            {
            }
            return "";
        }

        public static Stream OpenEmbeddedApkStream()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            Stream s = asm.GetManifestResourceStream(ResourceName);
            if (s != null) return s;

            string[] names = asm.GetManifestResourceNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].EndsWith(ResourceName, StringComparison.OrdinalIgnoreCase))
                {
                    return asm.GetManifestResourceStream(names[i]);
                }
            }
            return null;
        }

        public static List<AdbDeviceInfo> ListOnlineDevices(string adbExe)
        {
            var list = new List<AdbDeviceInfo>();
            if (string.IsNullOrEmpty(adbExe) || !File.Exists(adbExe))
            {
                return list;
            }

            try
            {
                int exitCode;
                string outText = RunCommand(adbExe, "devices -l", 2000, out exitCode);
                string[] lines = outText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string[] toks = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (toks.Length < 2) continue;
                    string serial = toks[0];
                    string state = toks[1];
                    if (!string.Equals(state, "device", StringComparison.OrdinalIgnoreCase)) continue;

                    string model = "";
                    for (int j = 2; j < toks.Length; j++)
                    {
                        if (toks[j].StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                        {
                            model = toks[j].Substring(6).Replace('_', ' ').Trim();
                            break;
                        }
                    }

                    list.Add(new AdbDeviceInfo
                    {
                        Serial = serial,
                        ModelFriendly = model
                    });
                }
            }
            catch
            {
            }

            return list;
        }

        public static bool EnsureReceiverInstalled(string adbExe, string serial, out string status)
        {
            status = "";
            if (string.IsNullOrEmpty(adbExe) || string.IsNullOrEmpty(serial))
            {
                status = "ADB ou serial ausente.";
                return false;
            }

            int exitCode;
            string pkgDump = RunCommand(adbExe, string.Format("-s {0} shell dumpsys package {1}", serial, PackageName), 3000, out exitCode);
            bool alreadyCurrent = !string.IsNullOrEmpty(pkgDump) &&
                pkgDump.IndexOf(ExpectedVersionCodeMarker, StringComparison.OrdinalIgnoreCase) >= 0 &&
                pkgDump.IndexOf("Unable to find package", StringComparison.OrdinalIgnoreCase) < 0;

            if (!alreadyCurrent)
            {
                string tempApk = Path.Combine(Path.GetTempPath(), "PhoneAudioReceiver_v2.apk");
                using (Stream res = OpenEmbeddedApkStream())
                {
                    if (res == null)
                    {
                        status = "APK receptor embutido não encontrado no executável.";
                        return false;
                    }
                    using (var fs = new FileStream(tempApk, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        byte[] buf = new byte[16384];
                        int n;
                        while ((n = res.Read(buf, 0, buf.Length)) > 0)
                        {
                            fs.Write(buf, 0, n);
                        }
                    }
                }

                string instOut = RunCommand(adbExe, string.Format("-s {0} install -r -g \"{1}\"", serial, tempApk), 25000, out exitCode);
                if (exitCode != 0 || instOut.IndexOf("Success", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    status = "Falha ao instalar receptor no celular: " + instOut.Trim();
                    return false;
                }
            }

            // Best-effort notification & doze whitelist for reliable background FGS playback
            RunCommand(adbExe, string.Format("-s {0} shell pm grant {1} android.permission.POST_NOTIFICATIONS", serial, PackageName), 2000, out exitCode);
            RunCommand(adbExe, string.Format("-s {0} shell cmd deviceidle whitelist +{1}", serial, PackageName), 2000, out exitCode);

            status = alreadyCurrent ? "Receptor nativo v2.0 já instalado." : "Receptor nativo v2.0 instalado no celular!";
            return true;
        }

        public static bool SetupForwardAndLaunch(string adbExe, string serial, int localPort, int targetMs, int sampleRate, int gainPct, out string error)
        {
            error = "";
            int exitCode;

            string fwdArgs = string.Format("-s {0} forward tcp:{1} {2}", serial, localPort, AbstractSocket);
            string fwdOut = RunCommand(adbExe, fwdArgs, 2500, out exitCode);
            if (exitCode != 0)
            {
                error = "Falha ao abrir túnel adb forward: " + fwdOut.Trim();
                return false;
            }

            string startArgs = string.Format(
                "-s {0} shell am start -W -n {1} --ei target_ms {2} --ei sample_rate {3} --ei gain_pct {4}",
                serial,
                ActivityName,
                targetMs,
                sampleRate,
                gainPct);
            string startOut = RunCommand(adbExe, startArgs, 5000, out exitCode);
            if (exitCode != 0 || startOut.IndexOf("Error:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                error = "Falha ao iniciar app receptor no celular: " + startOut.Trim();
                return false;
            }

            return true;
        }

        public static void RemoveForward(string adbExe, string serial, int localPort)
        {
            if (string.IsNullOrEmpty(adbExe) || string.IsNullOrEmpty(serial)) return;
            try
            {
                int exitCode;
                RunCommand(adbExe, string.Format("-s {0} forward --remove tcp:{1}", serial, localPort), 1500, out exitCode);
            }
            catch
            {
            }
        }

        public static string RunCommand(string exe, string args, int timeoutMs, out int exitCode)
        {
            exitCode = -1;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var proc = Process.Start(psi))
            {
                if (proc == null) return "";
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(); } catch { }
                    return stdout + "\n" + stderr + "\n[TIMEOUT]";
                }
                exitCode = proc.ExitCode;
                return (stdout + "\n" + stderr).Trim();
            }
        }
    }

    #endregion

    #region Native Binary USB Client

    internal sealed class NativeUsbAudioClient : IDisposable
    {
        private const int MaxQueueSize = 24;

        private readonly object _queueLock = new object();
        private readonly Queue<byte[]> _sendQueue = new Queue<byte[]>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        private TcpClient _tcp;
        private NetworkStream _stream;
        private Thread _writerThread;
        private Thread _readerThread;
        private volatile bool _closed;
        private volatile bool _connected;
        private uint _seqCounter;

        private readonly Stopwatch _pingWatch = Stopwatch.StartNew();
        public volatile int LatestRttMs;
        public ReceiverStats LatestStats { get { lock (_statsLock) { return _latestStats; } } }
        private readonly object _statsLock = new object();
        private ReceiverStats _latestStats;

        public Action<ReceiverStats> OnStatsReceived;
        public Action<int, string> OnDisconnected;

        public bool IsConnected { get { return _connected && !_closed; } }

        public bool Connect(string host, int port, int sampleRate, int targetMs, int gainPct, out string error)
        {
            error = "";
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(4000);
            Exception lastEx = null;

            while (DateTime.UtcNow < deadline && !_closed)
            {
                TcpClient candidate = null;
                NetworkStream stream = null;
                try
                {
                    candidate = new TcpClient();
                    candidate.NoDelay = true;
                    candidate.SendBufferSize = 65536;
                    candidate.ReceiveTimeout = 700;
                    candidate.SendTimeout = 2000;
                    candidate.Connect(host, port);
                    stream = candidate.GetStream();

                    // Send CONFIG (0x01) + initial PING (0x05) and wait briefly for PONG/STATS
                    // to prove Android's localabstract:phoneaudiobridge accepted via adb forward.
                    byte[] cfg = BuildConfigFrame(sampleRate, targetMs, gainPct);
                    byte[] ping = BuildPingFrame(_pingWatch.ElapsedTicks);
                    stream.Write(cfg, 0, cfg.Length);
                    stream.Write(ping, 0, ping.Length);
                    stream.Flush();

                    byte[] hdr = new byte[3];
                    if (ReadExactInto(stream, hdr, 3))
                    {
                        byte msgType = hdr[0];
                        ushort msgLen = BridgeProtocol.ReadU16(hdr, 1);
                        byte[] payload = msgLen > 0 ? new byte[msgLen] : new byte[0];
                        if (msgLen == 0 || ReadExactInto(stream, payload, msgLen))
                        {
                            HandleInboundMessage(msgType, payload);
                            candidate.ReceiveTimeout = 5000;
                            _tcp = candidate;
                            _stream = stream;
                            _connected = true;
                            StartThreads();
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }

                try { if (stream != null) stream.Close(); } catch { }
                try { if (candidate != null) candidate.Close(); } catch { }
                Thread.Sleep(150);
            }

            error = lastEx != null
                ? ("Falha na conexão USB nativa: " + lastEx.Message)
                : "O receptor Android não abriu o canal USB dentro do tempo limite.";
            return false;
        }

        private void StartThreads()
        {
            _writerThread = new Thread(WriterLoop);
            _writerThread.IsBackground = true;
            _writerThread.Priority = ThreadPriority.Highest;
            _writerThread.Name = "USB-Native-Writer";
            _writerThread.Start();

            _readerThread = new Thread(ReaderLoop);
            _readerThread.IsBackground = true;
            _readerThread.Name = "USB-Native-Reader";
            _readerThread.Start();
        }

        public void EnqueuePcm(byte[] pcm)
        {
            if (!_connected || _closed || pcm == null || pcm.Length == 0) return;

            uint seq = unchecked(++_seqCounter);
            int payloadLen = 4 + pcm.Length;
            byte[] frame = new byte[3 + payloadLen];
            frame[0] = BridgeProtocol.MsgPcm;
            BridgeProtocol.WriteU16(frame, 1, (ushort)payloadLen);
            BridgeProtocol.WriteU32(frame, 3, seq);
            Buffer.BlockCopy(pcm, 0, frame, 7, pcm.Length);

            lock (_queueLock)
            {
                if (_closed) return;
                while (_sendQueue.Count >= MaxQueueSize)
                {
                    _sendQueue.Dequeue();
                }
                _sendQueue.Enqueue(frame);
            }
            _signal.Set();
        }

        public void SendTargetMs(int targetMs)
        {
            if (!_connected || _closed) return;
            if (targetMs < 1) targetMs = 1;
            if (targetMs > 120) targetMs = 120;

            byte[] frame = new byte[5];
            frame[0] = BridgeProtocol.MsgSetTargetMs;
            BridgeProtocol.WriteU16(frame, 1, 2);
            BridgeProtocol.WriteU16(frame, 3, (ushort)targetMs);
            EnqueueControlFrame(frame);
        }

        public void SendGainPct(int gainPct)
        {
            if (!_connected || _closed) return;
            if (gainPct < 0) gainPct = 0;
            if (gainPct > 150) gainPct = 150;

            byte[] frame = new byte[5];
            frame[0] = BridgeProtocol.MsgSetGain;
            BridgeProtocol.WriteU16(frame, 1, 2);
            BridgeProtocol.WriteU16(frame, 3, (ushort)gainPct);
            EnqueueControlFrame(frame);
        }

        public void SendPing()
        {
            if (!_connected || _closed) return;
            EnqueueControlFrame(BuildPingFrame(_pingWatch.ElapsedTicks));
        }

        public void SendStopAndClose()
        {
            if (_closed) return;
            try
            {
                if (_stream != null && _connected)
                {
                    byte[] stopFrame = new byte[] { BridgeProtocol.MsgStop, 0, 0 };
                    _stream.Write(stopFrame, 0, stopFrame.Length);
                    _stream.Flush();
                }
            }
            catch
            {
            }
            CloseInternal(0, "Sessão encerrada pelo PC.", false);
        }

        private void EnqueueControlFrame(byte[] frame)
        {
            lock (_queueLock)
            {
                if (_closed) return;
                _sendQueue.Enqueue(frame);
            }
            _signal.Set();
        }

        private static byte[] BuildConfigFrame(int sampleRate, int targetMs, int gainPct)
        {
            byte[] frame = new byte[11];
            frame[0] = BridgeProtocol.MsgConfig;
            BridgeProtocol.WriteU16(frame, 1, 8);
            BridgeProtocol.WriteU32(frame, 3, (uint)sampleRate);
            BridgeProtocol.WriteU16(frame, 7, (ushort)Math.Max(1, Math.Min(120, targetMs)));
            BridgeProtocol.WriteU16(frame, 9, (ushort)Math.Max(0, Math.Min(150, gainPct)));
            return frame;
        }

        private static byte[] BuildPingFrame(long ticks)
        {
            byte[] frame = new byte[11];
            frame[0] = BridgeProtocol.MsgPing;
            BridgeProtocol.WriteU16(frame, 1, 8);
            BridgeProtocol.WriteI64(frame, 3, ticks);
            return frame;
        }

        private void WriterLoop()
        {
            IntPtr mmcssHandle = IntPtr.Zero;
            try
            {
                uint taskIdx = 0;
                mmcssHandle = NativeWin32.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIdx);
            }
            catch
            {
            }

            try
            {
                while (!_closed)
                {
                    _signal.WaitOne(500);
                    while (!_closed)
                    {
                        byte[] next = null;
                        lock (_queueLock)
                        {
                            if (_sendQueue.Count > 0)
                            {
                                next = _sendQueue.Dequeue();
                            }
                        }
                        if (next == null) break;
                        _stream.Write(next, 0, next.Length);
                    }
                }
            }
            catch
            {
                CloseInternal(3, "Canal USB interrompido.", true);
            }
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                {
                    try { NativeWin32.AvRevertMmThreadCharacteristics(mmcssHandle); } catch { }
                }
            }
        }

        private void ReaderLoop()
        {
            byte[] hdr = new byte[3];
            byte[] scratch = new byte[256];
            try
            {
                while (!_closed)
                {
                    if (!ReadExactInto(_stream, hdr, 3)) break;
                    byte msgType = hdr[0];
                    ushort msgLen = BridgeProtocol.ReadU16(hdr, 1);
                    if (msgLen > 65530) break;

                    byte[] buf = msgLen <= scratch.Length ? scratch : new byte[msgLen];
                    if (msgLen > 0 && !ReadExactInto(_stream, buf, msgLen)) break;

                    int reason;
                    if (HandleInboundMessage(msgType, buf, msgLen, out reason))
                    {
                        string msg = reason == 1
                            ? "Desconectado (Bluetooth/fone removido no celular — não reconecta sozinho)."
                            : (reason == 2 ? "Desconectado pelo celular." : "Conexão encerrada pelo celular.");
                        CloseInternal(reason, msg, true);
                        return;
                    }
                }
                CloseInternal(3, "Conexão USB com o celular encerrada.", true);
            }
            catch
            {
                CloseInternal(3, "Conexão USB perdida.", true);
            }
        }

        private void HandleInboundMessage(byte msgType, byte[] payload)
        {
            int unused;
            HandleInboundMessage(msgType, payload, payload.Length, out unused);
        }

        private bool HandleInboundMessage(byte msgType, byte[] buf, int len, out int stopReason)
        {
            stopReason = 0;
            if (msgType == BridgeProtocol.MsgPong && len >= 8)
            {
                long sentTicks = BridgeProtocol.ReadI64(buf, 0);
                double rtt = ((_pingWatch.ElapsedTicks - sentTicks) * 1000.0) / Stopwatch.Frequency;
                LatestRttMs = Math.Max(1, (int)Math.Round(rtt));
            }
            else if (msgType == BridgeProtocol.MsgStats && len >= 32)
            {
                var st = new ReceiverStats
                {
                    SeqLast = BridgeProtocol.ReadU32(buf, 0),
                    BufMs = BridgeProtocol.ReadU16(buf, 4) / 10.0,
                    MinBufMs = BridgeProtocol.ReadU16(buf, 6) / 10.0,
                    PktGapMaxMs = BridgeProtocol.ReadU16(buf, 8),
                    AAudioBurstFrames = BridgeProtocol.ReadU16(buf, 10),
                    AAudioBufFrames = BridgeProtocol.ReadU16(buf, 12),
                    AAudioFlags = BridgeProtocol.ReadU16(buf, 14),
                    PlcTotal = BridgeProtocol.ReadU32(buf, 16),
                    XRunTotal = BridgeProtocol.ReadU32(buf, 20),
                    ResyncTotal = BridgeProtocol.ReadU32(buf, 24),
                    AAudioSampleRate = BridgeProtocol.ReadU32(buf, 28),
                    RttMs = LatestRttMs
                };
                lock (_statsLock)
                {
                    _latestStats = st;
                }
                if (OnStatsReceived != null)
                {
                    OnStatsReceived(st);
                }
            }
            else if (msgType == BridgeProtocol.MsgDisconnectedByPhone)
            {
                stopReason = len >= 1 ? buf[0] : 1;
                return true;
            }
            return false;
        }

        private static bool ReadExactInto(Stream s, byte[] dst, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(dst, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        private void CloseInternal(int reason, string message, bool notify)
        {
            if (_closed) return;
            _closed = true;
            _connected = false;
            try { _signal.Set(); } catch { }
            try { if (_stream != null) _stream.Close(); } catch { }
            try { if (_tcp != null) _tcp.Close(); } catch { }

            if (notify && OnDisconnected != null)
            {
                OnDisconnected(reason, message);
            }
        }

        public void Dispose()
        {
            SendStopAndClose();
            try { _signal.Close(); } catch { }
        }
    }

    #endregion

    #region Adaptive Latency Controller (1-120 ms Auto-Downshift & Stabilize)

    internal sealed class AdaptiveLatencyController
    {
        private struct GlitchSample
        {
            public long TimeMs;
            public uint Count;
        }

        private readonly Queue<GlitchSample> _recentGlitches = new Queue<GlitchSample>();

        public int TargetMs { get; private set; }
        public bool AutoOptimize { get; private set; }
        public AdaptiveState State { get; private set; }
        public int KnownUnsafeFloorMs { get; private set; }

        private bool _stabilizePromptSuppressed;
        private uint _prevTotalGlitches;
        private bool _hasPrevSample;
        private long _warmupUntilMs;
        private long _stateStartMs;
        private long _zeroStartMs;
        private long _lastStepMs;

        public Action<int, string> OnTargetMsChanged;
        public Action<int, double> OnStabilizePromptRequested;

        public AdaptiveLatencyController(int initialTargetMs)
        {
            TargetMs = ClampMs(initialTargetMs);
            AutoOptimize = true;
            State = AdaptiveState.Warmup;
        }

        public void Reset(int initialTargetMs, long nowMs)
        {
            TargetMs = ClampMs(initialTargetMs);
            KnownUnsafeFloorMs = 0;
            _prevTotalGlitches = 0;
            _hasPrevSample = false;
            _recentGlitches.Clear();
            _stateStartMs = nowMs;
            _zeroStartMs = nowMs;
            _lastStepMs = nowMs;
            _warmupUntilMs = nowMs + 1500;
            State = AutoOptimize ? AdaptiveState.Warmup : AdaptiveState.Manual;
        }

        public void SetAutoOptimize(bool enabled, long nowMs)
        {
            AutoOptimize = enabled;
            _recentGlitches.Clear();
            _zeroStartMs = nowMs;
            _stateStartMs = nowMs;
            if (!enabled)
            {
                State = AdaptiveState.Manual;
            }
            else if (State == AdaptiveState.Manual)
            {
                _stabilizePromptSuppressed = false;
                _warmupUntilMs = nowMs + 1500;
                State = AdaptiveState.Warmup;
            }
        }

        public void OnManualTargetChanged(int ms, long nowMs)
        {
            TargetMs = ClampMs(ms);
            KnownUnsafeFloorMs = 0;
            _recentGlitches.Clear();
            _stateStartMs = nowMs;
            _zeroStartMs = nowMs;
            _lastStepMs = nowMs;
            _warmupUntilMs = nowMs + 10000;
            State = AutoOptimize ? AdaptiveState.Warmup : AdaptiveState.Manual;
        }

        public void StartStabilizing(long nowMs)
        {
            if (TargetMs >= 120)
            {
                State = AdaptiveState.FloorLocked;
                return;
            }
            KnownUnsafeFloorMs = Math.Max(KnownUnsafeFloorMs, TargetMs);
            int step = TargetMs < 20 ? 2 : 5;
            TargetMs = ClampMs(TargetMs + step);
            State = AdaptiveState.Stabilizing;
            _stateStartMs = nowMs;
            _lastStepMs = nowMs;
            _zeroStartMs = nowMs;
            _recentGlitches.Clear();
            if (OnTargetMsChanged != null)
            {
                OnTargetMsChanged(TargetMs, "Estabilizando");
            }
        }

        public void DeclineStabilize(long nowMs)
        {
            _stabilizePromptSuppressed = true;
            _recentGlitches.Clear();
            _zeroStartMs = nowMs;
            _stateStartMs = nowMs;
            State = AutoOptimize ? AdaptiveState.Monitoring : AdaptiveState.Manual;
        }

        public void ProcessStats(uint totalGlitches, long nowMs)
        {
            uint delta = 0;
            if (_hasPrevSample && totalGlitches >= _prevTotalGlitches)
            {
                delta = totalGlitches - _prevTotalGlitches;
            }
            _prevTotalGlitches = totalGlitches;
            _hasPrevSample = true;

            if (!AutoOptimize || State == AdaptiveState.Manual)
            {
                State = AdaptiveState.Manual;
                return;
            }

            if (State == AdaptiveState.Warmup)
            {
                _zeroStartMs = nowMs;
                if (nowMs >= _warmupUntilMs)
                {
                    State = AdaptiveState.Monitoring;
                    _stateStartMs = nowMs;
                    _zeroStartMs = nowMs;
                    _recentGlitches.Clear();
                }
                return;
            }

            if (State == AdaptiveState.PromptPending)
            {
                return;
            }

            if (delta > 0)
            {
                _recentGlitches.Enqueue(new GlitchSample { TimeMs = nowMs, Count = delta });
                _zeroStartMs = nowMs;
            }
            while (_recentGlitches.Count > 0 && (nowMs - _recentGlitches.Peek().TimeMs) > 3000)
            {
                _recentGlitches.Dequeue();
            }

            if (State == AdaptiveState.ProbingDown)
            {
                if (delta > 0)
                {
                    int failedMs = TargetMs;
                    KnownUnsafeFloorMs = Math.Max(KnownUnsafeFloorMs, failedMs);
                    TargetMs = ClampMs(failedMs + 1);
                    State = AdaptiveState.FloorLocked;
                    _stateStartMs = nowMs;
                    _zeroStartMs = nowMs;
                    _recentGlitches.Clear();
                    if (OnTargetMsChanged != null)
                    {
                        OnTargetMsChanged(TargetMs, "Piso mínimo encontrado");
                    }
                }
                else if ((nowMs - _stateStartMs) >= 3500)
                {
                    _zeroStartMs = nowMs;
                    if (TargetMs <= 1 || (TargetMs - 1) <= KnownUnsafeFloorMs)
                    {
                        State = AdaptiveState.FloorLocked;
                        _stateStartMs = nowMs;
                    }
                    else
                    {
                        State = AdaptiveState.Monitoring;
                        _stateStartMs = nowMs;
                    }
                }
                return;
            }

            if (State == AdaptiveState.Stabilizing)
            {
                if (delta > 0)
                {
                    if ((nowMs - _lastStepMs) >= 2500 && TargetMs < 120)
                    {
                        KnownUnsafeFloorMs = Math.Max(KnownUnsafeFloorMs, TargetMs);
                        int step = TargetMs < 20 ? 2 : 5;
                        TargetMs = ClampMs(TargetMs + step);
                        _lastStepMs = nowMs;
                        _zeroStartMs = nowMs;
                        _recentGlitches.Clear();
                        if (OnTargetMsChanged != null)
                        {
                            OnTargetMsChanged(TargetMs, "Estabilizando");
                        }
                    }
                }
                else if ((nowMs - _zeroStartMs) >= 4000)
                {
                    State = AdaptiveState.FloorLocked;
                    _stateStartMs = nowMs;
                    _recentGlitches.Clear();
                }
                return;
            }

            // Monitoring or FloorLocked: check sustained glitches for interactive stabilize prompt
            uint sumRecent = 0;
            foreach (GlitchSample s in _recentGlitches)
            {
                sumRecent += s.Count;
            }
            if (sumRecent >= 4 && TargetMs < 120 && !_stabilizePromptSuppressed)
            {
                State = AdaptiveState.PromptPending;
                _stateStartMs = nowMs;
                if (OnStabilizePromptRequested != null)
                {
                    OnStabilizePromptRequested(TargetMs, sumRecent / 3.0);
                }
                return;
            }

            // Silent auto-downshift while Monitoring
            if (State == AdaptiveState.Monitoring && delta == 0)
            {
                if (TargetMs > 1 && (TargetMs - 1) > KnownUnsafeFloorMs && (nowMs - _zeroStartMs) >= 6000)
                {
                    TargetMs = ClampMs(TargetMs - 1);
                    State = AdaptiveState.ProbingDown;
                    _stateStartMs = nowMs;
                    _recentGlitches.Clear();
                    if (OnTargetMsChanged != null)
                    {
                        OnTargetMsChanged(TargetMs, "Testando redução silenciosa");
                    }
                }
            }
        }

        public string GetStatusLabelPt()
        {
            switch (State)
            {
                case AdaptiveState.Warmup: return string.Format("Aquecendo em {0} ms...", TargetMs);
                case AdaptiveState.Monitoring: return string.Format("Estável em {0} ms (testando menor ms se liso)", TargetMs);
                case AdaptiveState.ProbingDown: return string.Format("Testando {0} ms silenciosamente...", TargetMs);
                case AdaptiveState.PromptPending: return string.Format("Cortes em {0} ms — aguardando resposta", TargetMs);
                case AdaptiveState.Stabilizing: return string.Format("Estabilizando → {0} ms...", TargetMs);
                case AdaptiveState.FloorLocked:
                    return KnownUnsafeFloorMs > 0
                        ? string.Format("Piso ótimo travado em {0} ms", TargetMs)
                        : string.Format("Estabilizado em {0} ms", TargetMs);
                default: return string.Format("Manual ({0} ms)", TargetMs);
            }
        }

        private static int ClampMs(int ms)
        {
            if (ms < 1) return 1;
            if (ms > 120) return 120;
            return ms;
        }
    }

    #endregion

    #region Custom Dark Prompt Dialog

    internal sealed class DarkPromptDialog : Form
    {
        public DarkPromptDialog(string title, string headline, string body, string yesText, string noText, Color accentColor)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 210);
            BackColor = Color.FromArgb(20, 24, 33);
            ForeColor = Color.FromArgb(241, 245, 249);
            Font = new Font("Segoe UI", 9.5f);

            var lblHead = new Label
            {
                Text = headline,
                Location = new Point(20, 18),
                Size = new Size(400, 28),
                Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
                ForeColor = accentColor
            };

            var lblBody = new Label
            {
                Text = body,
                Location = new Point(20, 52),
                Size = new Size(400, 90),
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(203, 213, 225)
            };

            var btnYes = new Button
            {
                Text = yesText,
                DialogResult = DialogResult.Yes,
                Location = new Point(20, 152),
                Size = new Size(215, 38),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(22, 163, 74),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnYes.FlatAppearance.BorderSize = 0;

            var btnNo = new Button
            {
                Text = noText,
                DialogResult = DialogResult.No,
                Location = new Point(245, 152),
                Size = new Size(175, 38),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(39, 48, 66),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnNo.FlatAppearance.BorderSize = 0;

            Controls.Add(lblHead);
            Controls.Add(lblBody);
            Controls.Add(btnYes);
            Controls.Add(btnNo);
            AcceptButton = btnYes;
            CancelButton = btnNo;
        }
    }

    #endregion

    #region Main Window & Tray Application

    internal sealed class MainBridgeForm : Form
    {
        private const int ForwardPort = 8765;
        private const string RunRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRunName = "PhoneAudioBridge";

        private readonly WasapiLoopbackCapture _capture;
        private readonly AdaptiveLatencyController _adaptive;
        private readonly Stopwatch _sessionClock = Stopwatch.StartNew();
        private readonly object _clientLock = new object();
        private NativeUsbAudioClient _usbClient;

        private readonly HashSet<string> _promptedSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _activeSerial = "";
        private string _activeModel = "";
        private string _adbExe = "";
        private bool _forceExit;
        private bool _popupOpen;
        private bool _connecting;
        private bool _autoConnectRequested;
        private long _lastPingMs;
        private long _lastLiveJsonMs;
        private int _adbPollCounter;
        private int _pendingGainPct = 100;
        private uint _prevGlitchForRate;
        private long _prevGlitchRateMs;
        private int _glitchesPerSec;
        private string _lastDisconnectReason = "";

        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private ToolStripMenuItem _miStartup;

        private Label _lblConnectionBadge;
        private Label _lblPhoneState;
        private ComboBox _cmbDevices;
        private ComboBox _cmbAudio;
        private Button _btnConnect;
        private Button _btnDisconnect;
        private CheckBox _chkAutoPrompt;
        private CheckBox _chkAutoOptimize;
        private TrackBar _trkTargetMs;
        private Label _lblTargetMsVal;
        private TrackBar _trkGain;
        private Label _lblGainVal;
        private Panel _vuHost;
        private Panel _vuBar;
        private Label _lblAdaptiveStatus;
        private Label _lblLiveDiag;
        private System.Windows.Forms.Timer _uiTimer;

        public MainBridgeForm(bool startMinimized, bool autoConnect)
        {
            _autoConnectRequested = autoConnect;
            _adbExe = AdbHelper.FindAdbExecutable();

            _capture = new WasapiLoopbackCapture();
            _adaptive = new AdaptiveLatencyController(10);

            _capture.OnPcmPacket = delegate(byte[] pcm, int rate)
            {
                NativeUsbAudioClient c;
                lock (_clientLock) { c = _usbClient; }
                if (c != null && c.IsConnected)
                {
                    c.EnqueuePcm(pcm);
                }
            };
            _capture.OnFormatChanged = delegate(int rate, string devName)
            {
                // Kept synchronized cleanly when connecting/restarting
            };

            _adaptive.OnTargetMsChanged = delegate(int newMs, string reason)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        ApplyTargetMsFromAdaptive(newMs);
                    });
                }
                catch { }
            };
            _adaptive.OnStabilizePromptRequested = delegate(int curMs, double gRate)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        ShowStabilizePrompt(curMs, gRate);
                    });
                }
                catch { }
            };

            BuildUi();
            BuildTray();

            RefreshAudioDevices();
            RefreshAdbDevices(true);
            _capture.Start();

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 120;
            _uiTimer.Tick += OnUiTick;
            _uiTimer.Start();

            if (startMinimized)
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
            }
        }

        private void BuildUi()
        {
            Text = "Phone Audio Bridge · USB Nativo (AAudio)";
            ClientSize = new Size(560, 620);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(18, 21, 28);
            ForeColor = Color.FromArgb(241, 245, 249);
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Icon = CreateTrayIcon();

            int lx = 22;
            int lw = 516;

            var lblTitle = new Label
            {
                Text = "Phone Audio Bridge (USB Nativo)",
                Location = new Point(lx, 16),
                Size = new Size(340, 28),
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = Color.White
            };
            _lblConnectionBadge = new Label
            {
                Text = "Aguardando USB",
                Location = new Point(386, 18),
                Size = new Size(152, 26),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(56, 189, 248),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            var lblSub = new Label
            {
                Text = "Pluge o cabo USB com Depuração USB ativa — conexão direta C++ AAudio sem navegador.",
                Location = new Point(lx, 48),
                Size = new Size(lw, 20),
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(148, 163, 184)
            };
            Controls.Add(lblTitle);
            Controls.Add(_lblConnectionBadge);
            Controls.Add(lblSub);

            // Section 1: USB Phone
            Controls.Add(MakeSectionLabel("1. CELULAR ANDROID CONECTADO VIA CABO USB (ADB)", lx, 80));
            _cmbDevices = new ComboBox
            {
                Location = new Point(lx, 102),
                Size = new Size(422, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(27, 32, 44),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            var btnRefreshUsb = MakeButton("Atualizar", lx + 432, 101, 84, 28, Color.FromArgb(35, 43, 59));
            btnRefreshUsb.Click += delegate { RefreshAdbDevices(true); };
            _lblPhoneState = new Label
            {
                Text = "Procurando celular USB...",
                Location = new Point(lx, 136),
                Size = new Size(lw, 22),
                Font = new Font("Segoe UI", 8.75f, FontStyle.Bold),
                ForeColor = Color.FromArgb(245, 158, 11)
            };
            Controls.Add(_cmbDevices);
            Controls.Add(btnRefreshUsb);
            Controls.Add(_lblPhoneState);

            // Connect / Disconnect Buttons
            _btnConnect = MakeButton("Conectar e Ouvir Agora", lx, 166, 332, 40, Color.FromArgb(22, 163, 74));
            _btnConnect.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            _btnConnect.Click += delegate { ConnectSelectedDevice(); };

            _btnDisconnect = MakeButton("Desconectar", lx + 344, 166, 172, 40, Color.FromArgb(65, 28, 34));
            _btnDisconnect.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            _btnDisconnect.Enabled = false;
            _btnDisconnect.Click += delegate { DisconnectManual("Desconectado pelo PC."); };
            Controls.Add(_btnConnect);
            Controls.Add(_btnDisconnect);

            _chkAutoPrompt = new CheckBox
            {
                Text = "Perguntar automaticamente ao plugar o cabo USB",
                Location = new Point(lx, 214),
                Size = new Size(lw, 22),
                Checked = true,
                ForeColor = Color.FromArgb(203, 213, 225)
            };
            Controls.Add(_chkAutoPrompt);

            // Section 2: Windows Audio Source + VU
            Controls.Add(MakeSectionLabel("2. FONTE DE ÁUDIO NO WINDOWS", lx, 248));
            _cmbAudio = new ComboBox
            {
                Location = new Point(lx, 270),
                Size = new Size(422, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(27, 32, 44),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _cmbAudio.SelectedIndexChanged += delegate
            {
                AudioDeviceInfo info = _cmbAudio.SelectedItem as AudioDeviceInfo;
                if (info != null) _capture.SelectDevice(info.Id);
            };
            var btnRefreshAudio = MakeButton("Atualizar", lx + 432, 269, 84, 28, Color.FromArgb(35, 43, 59));
            btnRefreshAudio.Click += delegate { RefreshAudioDevices(); };

            _vuHost = new Panel
            {
                Location = new Point(lx, 306),
                Size = new Size(lw, 8),
                BackColor = Color.FromArgb(12, 15, 22)
            };
            _vuBar = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(0, 8),
                BackColor = Color.FromArgb(34, 197, 94)
            };
            _vuHost.Controls.Add(_vuBar);
            Controls.Add(_cmbAudio);
            Controls.Add(btnRefreshAudio);
            Controls.Add(_vuHost);

            // Section 3: Adaptive Buffer & Gain
            Controls.Add(MakeSectionLabel("3. LATÊNCIA ADAPTATIVA E VOLUME (PADRÃO 10 MS)", lx, 326));
            var ctrlCard = new Panel
            {
                Location = new Point(lx, 348),
                Size = new Size(lw, 154),
                BackColor = Color.FromArgb(25, 31, 43)
            };

            var lblMsTitle = new Label
            {
                Text = "Buffer Alvo (1–120 ms):",
                Location = new Point(12, 10),
                Size = new Size(190, 20),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _lblTargetMsVal = new Label
            {
                Text = "10 ms",
                Location = new Point(400, 10),
                Size = new Size(102, 20),
                TextAlign = ContentAlignment.TopRight,
                Font = new Font("Consolas", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            };
            _trkTargetMs = new TrackBar
            {
                Location = new Point(10, 32),
                Size = new Size(494, 34),
                Minimum = 1,
                Maximum = 120,
                Value = 10,
                TickFrequency = 10,
                AutoSize = false
            };
            _trkTargetMs.Scroll += delegate
            {
                int val = _trkTargetMs.Value;
                _lblTargetMsVal.Text = val + " ms";
                _adaptive.OnManualTargetChanged(val, _sessionClock.ElapsedMilliseconds);
                SendTargetMsIfConnected(val);
            };

            _chkAutoOptimize = new CheckBox
            {
                Text = "Ajuste automático (reduz até 1 ms se liso; pergunta p/ estabilizar se cortar)",
                Location = new Point(14, 70),
                Size = new Size(490, 22),
                Checked = true,
                Font = new Font("Segoe UI", 8.25f),
                ForeColor = Color.FromArgb(134, 239, 172)
            };
            _chkAutoOptimize.CheckedChanged += delegate
            {
                _adaptive.SetAutoOptimize(_chkAutoOptimize.Checked, _sessionClock.ElapsedMilliseconds);
            };

            var lblGainTitle = new Label
            {
                Text = "Ganho no Celular:",
                Location = new Point(12, 100),
                Size = new Size(150, 20),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _lblGainVal = new Label
            {
                Text = "100%",
                Location = new Point(400, 100),
                Size = new Size(102, 20),
                TextAlign = ContentAlignment.TopRight,
                Font = new Font("Consolas", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            };
            _trkGain = new TrackBar
            {
                Location = new Point(10, 120),
                Size = new Size(494, 28),
                Minimum = 0,
                Maximum = 150,
                Value = 100,
                TickFrequency = 25,
                AutoSize = false
            };
            _trkGain.Scroll += delegate
            {
                _pendingGainPct = _trkGain.Value;
                _lblGainVal.Text = _pendingGainPct + "%";
                NativeUsbAudioClient c;
                lock (_clientLock) { c = _usbClient; }
                if (c != null && c.IsConnected) c.SendGainPct(_pendingGainPct);
            };

            ctrlCard.Controls.Add(lblMsTitle);
            ctrlCard.Controls.Add(_lblTargetMsVal);
            ctrlCard.Controls.Add(_trkTargetMs);
            ctrlCard.Controls.Add(_chkAutoOptimize);
            ctrlCard.Controls.Add(lblGainTitle);
            ctrlCard.Controls.Add(_lblGainVal);
            ctrlCard.Controls.Add(_trkGain);
            Controls.Add(ctrlCard);

            // Adaptive & Live Telemetry Labels
            _lblAdaptiveStatus = new Label
            {
                Text = "Modo adaptativo: pronto em 10 ms",
                Location = new Point(lx, 510),
                Size = new Size(lw, 20),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            };
            _lblLiveDiag = new Label
            {
                Text = "Telemetria ao vivo: aguardando conexão USB...",
                Location = new Point(lx, 534),
                Size = new Size(lw, 36),
                BackColor = Color.FromArgb(14, 17, 24),
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Consolas", 8.25f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 8, 0)
            };

            var btnHideTray = MakeButton("Ocultar na Bandeja (^)", lx, 578, lw, 30, Color.FromArgb(35, 43, 59));
            btnHideTray.Click += delegate { HideToTray(); };

            Controls.Add(_lblAdaptiveStatus);
            Controls.Add(_lblLiveDiag);
            Controls.Add(btnHideTray);
        }

        private void BuildTray()
        {
            _trayMenu = new ContextMenuStrip();
            var miOpen = new ToolStripMenuItem("Abrir janela", null, delegate { ShowFromTray(); });
            miOpen.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _miStartup = new ToolStripMenuItem("Iniciar com o Windows", null, delegate { ToggleStartup(); });
            _miStartup.Checked = IsStartupEnabled();
            var miExit = new ToolStripMenuItem("Sair", null, delegate { ExitCleanly(); });

            _trayMenu.Items.Add(miOpen);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(_miStartup);
            _trayMenu.Items.Add(miExit);

            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "Phone Audio Bridge (USB Nativo)";
            _trayIcon.Icon = CreateTrayIcon();
            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += delegate { ShowFromTray(); };
        }

        private static Label MakeSectionLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184)
            };
        }

        private static Button MakeButton(string text, int x, int y, int w, int h, Color bg)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.75f, FontStyle.Bold)
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(56, 68, 92);
            btn.FlatAppearance.BorderSize = 1;
            return btn;
        }

        private void RefreshAudioDevices()
        {
            AudioDeviceInfo current = _cmbAudio.SelectedItem as AudioDeviceInfo;
            string prevId = current != null ? current.Id : "";

            var list = WasapiLoopbackCapture.ListOutputDevices();
            _cmbAudio.Items.Clear();
            int selIdx = 0;
            for (int i = 0; i < list.Count; i++)
            {
                _cmbAudio.Items.Add(list[i]);
                if (!string.IsNullOrEmpty(prevId) && string.Equals(list[i].Id, prevId, StringComparison.Ordinal))
                {
                    selIdx = i;
                }
            }
            if (_cmbAudio.Items.Count > 0)
            {
                _cmbAudio.SelectedIndex = selIdx;
            }
        }

        private void RefreshAdbDevices(bool rescanAdbPath)
        {
            if (rescanAdbPath || string.IsNullOrEmpty(_adbExe))
            {
                _adbExe = AdbHelper.FindAdbExecutable();
            }

            if (string.IsNullOrEmpty(_adbExe))
            {
                _cmbDevices.Items.Clear();
                _lblPhoneState.ForeColor = Color.FromArgb(248, 113, 113);
                _lblPhoneState.Text = "adb.exe não encontrado — instale Google Platform-Tools ou coloque adb.exe na mesma pasta.";
                return;
            }

            var devs = AdbHelper.ListOnlineDevices(_adbExe);
            var presentSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AdbDeviceInfo prevSel = _cmbDevices.SelectedItem as AdbDeviceInfo;
            string prevSerial = prevSel != null ? prevSel.Serial : _activeSerial;

            _cmbDevices.Items.Clear();
            int selIdx = 0;
            for (int i = 0; i < devs.Count; i++)
            {
                presentSerials.Add(devs[i].Serial);
                _cmbDevices.Items.Add(devs[i]);
                if (!string.IsNullOrEmpty(prevSerial) && string.Equals(devs[i].Serial, prevSerial, StringComparison.OrdinalIgnoreCase))
                {
                    selIdx = i;
                }
            }
            if (_cmbDevices.Items.Count > 0)
            {
                _cmbDevices.SelectedIndex = selIdx;
            }

            // Clear prompted serials once physically unplugged so replugging asks again!
            var toRemove = new List<string>();
            foreach (string s in _promptedSerials)
            {
                if (!presentSerials.Contains(s))
                {
                    toRemove.Add(s);
                }
            }
            for (int i = 0; i < toRemove.Count; i++)
            {
                _promptedSerials.Remove(toRemove[i]);
            }

            NativeUsbAudioClient activeCli;
            lock (_clientLock) { activeCli = _usbClient; }
            bool connectedNow = activeCli != null && activeCli.IsConnected;

            if (devs.Count == 0)
            {
                _lblPhoneState.ForeColor = Color.FromArgb(245, 158, 11);
                _lblPhoneState.Text = "Nenhum celular Android USB detectado (ative Depuração USB no aparelho).";
                return;
            }

            if (!connectedNow && !_connecting)
            {
                AdbDeviceInfo first = devs[0];
                _lblPhoneState.ForeColor = Color.FromArgb(74, 222, 128);
                _lblPhoneState.Text = "Celular pronto no cabo: " + first.DisplayLabel;

                if (_autoConnectRequested)
                {
                    _autoConnectRequested = false;
                    _promptedSerials.Add(first.Serial);
                    ConnectDevice(first);
                    return;
                }

                if (_chkAutoPrompt.Checked && !_popupOpen && !_promptedSerials.Contains(first.Serial))
                {
                    _promptedSerials.Add(first.Serial);
                    PromptConnectOnPlug(first);
                }
            }
        }

        private void PromptConnectOnPlug(AdbDeviceInfo dev)
        {
            if (_popupOpen || _connecting) return;
            _popupOpen = true;
            try
            {
                string title = "Celular USB detectado";
                string head = dev.DisplayLabel;
                string body = "Deseja conectar o áudio do PC neste aparelho pelo cabo USB agora?\n\n" +
                    "O túnel nativo C++ AAudio inicia em ~1 segundo (padrão 10 ms), sem abrir navegador nem QR Code.";
                using (var dlg = new DarkPromptDialog(title, head, body, "Sim, Conectar Áudio", "Não agora", Color.FromArgb(74, 222, 128)))
                {
                    if (dlg.ShowDialog(this) == DialogResult.Yes)
                    {
                        ConnectDevice(dev);
                    }
                }
            }
            finally
            {
                _popupOpen = false;
            }
        }

        private void ConnectSelectedDevice()
        {
            AdbDeviceInfo sel = _cmbDevices.SelectedItem as AdbDeviceInfo;
            if (sel == null)
            {
                RefreshAdbDevices(true);
                sel = _cmbDevices.SelectedItem as AdbDeviceInfo;
            }
            if (sel == null)
            {
                MessageBox.Show(this, "Nenhum celular Android USB online encontrado. Plugue o cabo e autorize a Depuração USB.", "Phone Audio Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _promptedSerials.Add(sel.Serial);
            ConnectDevice(sel);
        }

        private void ConnectDevice(AdbDeviceInfo dev)
        {
            if (_connecting || dev == null) return;
            DisconnectClientOnly(0, "", false);

            _connecting = true;
            _activeSerial = dev.Serial;
            _activeModel = string.IsNullOrEmpty(dev.ModelFriendly) ? dev.Serial : dev.ModelFriendly;
            _lastDisconnectReason = "";

            _btnConnect.Enabled = false;
            _btnDisconnect.Enabled = false;
            _lblConnectionBadge.Text = "Conectando...";
            _lblConnectionBadge.BackColor = Color.FromArgb(66, 46, 16);
            _lblConnectionBadge.ForeColor = Color.FromArgb(251, 191, 36);
            _lblPhoneState.ForeColor = Color.FromArgb(56, 189, 248);
            _lblPhoneState.Text = "Preparando receptor nativo e túnel USB...";

            int targetMs = _adaptive.TargetMs;
            int rate = _capture.CurrentSampleRate > 8000 ? _capture.CurrentSampleRate : 48000;
            int gainPct = _pendingGainPct;
            string adbPath = _adbExe;

            var bg = new Thread(delegate()
            {
                string err = "";
                string instMsg;
                if (!AdbHelper.EnsureReceiverInstalled(adbPath, dev.Serial, out instMsg))
                {
                    FinishConnectAttempt(false, null, instMsg);
                    return;
                }

                UpdatePhoneStatusAsync(instMsg + " Abrindo túnel AAudio...");
                if (!AdbHelper.SetupForwardAndLaunch(adbPath, dev.Serial, ForwardPort, targetMs, rate, gainPct, out err))
                {
                    FinishConnectAttempt(false, null, err);
                    return;
                }

                var cli = new NativeUsbAudioClient();
                cli.OnStatsReceived = delegate(ReceiverStats st)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            HandleStatsOnUi(st);
                        });
                    }
                    catch { }
                };
                cli.OnDisconnected = delegate(int reason, string msg)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            HandleRemoteDisconnected(reason, msg);
                        });
                    }
                    catch { }
                };

                if (!cli.Connect("127.0.0.1", ForwardPort, rate, targetMs, gainPct, out err))
                {
                    cli.Dispose();
                    AdbHelper.RemoveForward(adbPath, dev.Serial, ForwardPort);
                    FinishConnectAttempt(false, null, err);
                    return;
                }

                FinishConnectAttempt(true, cli, "Conectado via USB Nativo!");
            });
            bg.IsBackground = true;
            bg.Start();
        }

        private void UpdatePhoneStatusAsync(string text)
        {
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (_connecting) _lblPhoneState.Text = text;
                });
            }
            catch { }
        }

        private void FinishConnectAttempt(bool ok, NativeUsbAudioClient cli, string statusMsg)
        {
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _connecting = false;
                    if (!ok || cli == null)
                    {
                        _btnConnect.Enabled = true;
                        _btnDisconnect.Enabled = false;
                        _lblConnectionBadge.Text = "Erro USB";
                        _lblConnectionBadge.BackColor = Color.FromArgb(65, 22, 26);
                        _lblConnectionBadge.ForeColor = Color.FromArgb(248, 113, 113);
                        _lblPhoneState.ForeColor = Color.FromArgb(248, 113, 113);
                        _lblPhoneState.Text = statusMsg;
                        return;
                    }

                    lock (_clientLock)
                    {
                        _usbClient = cli;
                    }
                    long now = _sessionClock.ElapsedMilliseconds;
                    _adaptive.Reset(_trkTargetMs.Value, now);
                    _prevGlitchForRate = 0;
                    _prevGlitchRateMs = now;
                    _glitchesPerSec = 0;
                    _lastPingMs = now;

                    _btnConnect.Enabled = false;
                    _btnDisconnect.Enabled = true;
                    _lblConnectionBadge.Text = "USB AO VIVO";
                    _lblConnectionBadge.BackColor = Color.FromArgb(18, 52, 34);
                    _lblConnectionBadge.ForeColor = Color.FromArgb(74, 222, 128);
                    _lblPhoneState.ForeColor = Color.FromArgb(74, 222, 128);
                    _lblPhoneState.Text = string.Format("Conectado em {0} via localabstract (Zero HTTP/WebSocket)", _activeModel);
                });
            }
            catch
            {
                if (cli != null) cli.Dispose();
            }
        }

        private void HandleStatsOnUi(ReceiverStats st)
        {
            if (st == null) return;
            long now = _sessionClock.ElapsedMilliseconds;

            if ((now - _prevGlitchRateMs) >= 1000)
            {
                uint curG = st.TotalGlitches;
                uint diff = curG >= _prevGlitchForRate ? (curG - _prevGlitchForRate) : 0;
                double sec = (now - _prevGlitchRateMs) / 1000.0;
                _glitchesPerSec = (int)Math.Round(diff / Math.Max(0.2, sec));
                _prevGlitchForRate = curG;
                _prevGlitchRateMs = now;
            }

            _adaptive.ProcessStats(st.TotalGlitches, now);

            _lblAdaptiveStatus.Text = "Modo adaptativo: " + _adaptive.GetStatusLabelPt();
            _lblAdaptiveStatus.ForeColor = (_adaptive.State == AdaptiveState.FloorLocked || _adaptive.State == AdaptiveState.Monitoring)
                ? Color.FromArgb(74, 222, 128)
                : (_adaptive.State == AdaptiveState.Stabilizing || _adaptive.State == AdaptiveState.PromptPending
                    ? Color.FromArgb(251, 191, 36)
                    : Color.FromArgb(56, 189, 248));

            string flags = (st.IsLowLatency ? "LL" : "STD") + (st.IsExclusive ? "+EX" : "") + (st.IsMmap ? "+MMAP" : "");
            _lblLiveDiag.Text = string.Format(
                "AAudio {0} b={1} | Buf {2:0.0}ms (mín {3:0.0}ms) | Gap {4}ms | Cortes/s: {5} (P{6}/X{7}) | Ping {8}ms",
                flags,
                st.AAudioBurstFrames,
                st.BufMs,
                st.MinBufMs,
                st.PktGapMaxMs,
                _glitchesPerSec,
                st.PlcTotal,
                st.XRunTotal,
                st.RttMs);
            _lblLiveDiag.ForeColor = _glitchesPerSec > 0 ? Color.FromArgb(248, 113, 113) : Color.FromArgb(74, 222, 128);
        }

        private void ApplyTargetMsFromAdaptive(int newMs)
        {
            if (newMs < _trkTargetMs.Minimum) newMs = _trkTargetMs.Minimum;
            if (newMs > _trkTargetMs.Maximum) newMs = _trkTargetMs.Maximum;
            _trkTargetMs.Value = newMs;
            _lblTargetMsVal.Text = newMs + " ms";
            SendTargetMsIfConnected(newMs);
            _lblAdaptiveStatus.Text = "Modo adaptativo: " + _adaptive.GetStatusLabelPt();
        }

        private void SendTargetMsIfConnected(int ms)
        {
            NativeUsbAudioClient c;
            lock (_clientLock) { c = _usbClient; }
            if (c != null && c.IsConnected)
            {
                c.SendTargetMs(ms);
            }
        }

        private void ShowStabilizePrompt(int curMs, double gRate)
        {
            if (_popupOpen) return;
            NativeUsbAudioClient c;
            lock (_clientLock) { c = _usbClient; }
            if (c == null || !c.IsConnected) return;

            _popupOpen = true;
            try
            {
                string title = "Cortes detectados no áudio";
                string head = string.Format("Cortes frequentes em {0} ms (~{1:0.0} cortes/s)", curMs, gRate);
                string body = "Deseja que o app estabilize automaticamente aumentando o buffer aos poucos até zerar os cortes?\n\n" +
                    "(Se clicar em Não, este aviso não aparecerá novamente.)";
                using (var dlg = new DarkPromptDialog(title, head, body, "Sim, Estabilizar Agora", "Não, manter", Color.FromArgb(251, 191, 36)))
                {
                    if (dlg.ShowDialog(this) == DialogResult.Yes)
                    {
                        _adaptive.StartStabilizing(_sessionClock.ElapsedMilliseconds);
                        ApplyTargetMsFromAdaptive(_adaptive.TargetMs);
                    }
                    else
                    {
                        _adaptive.DeclineStabilize(_sessionClock.ElapsedMilliseconds);
                    }
                }
            }
            finally
            {
                _popupOpen = false;
            }
        }

        private void HandleRemoteDisconnected(int reason, string msg)
        {
            DisconnectClientOnly(reason, msg, false);
            if (!string.IsNullOrEmpty(_activeSerial))
            {
                // Keep in _promptedSerials when Bluetooth/phone turned off so it NEVER auto-reconnects or re-prompts
                _promptedSerials.Add(_activeSerial);
            }
        }

        private void DisconnectManual(string msg)
        {
            DisconnectClientOnly(0, msg, true);
        }

        private void DisconnectClientOnly(int reason, string msg, bool sendStop)
        {
            NativeUsbAudioClient old;
            lock (_clientLock)
            {
                old = _usbClient;
                _usbClient = null;
            }
            if (old != null)
            {
                if (sendStop) old.SendStopAndClose();
                old.Dispose();
            }
            if (!string.IsNullOrEmpty(_adbExe) && !string.IsNullOrEmpty(_activeSerial))
            {
                AdbHelper.RemoveForward(_adbExe, _activeSerial, ForwardPort);
            }

            _lastDisconnectReason = msg;
            _btnConnect.Enabled = true;
            _btnDisconnect.Enabled = false;
            _lblConnectionBadge.Text = reason == 1 ? "Fone desconectado" : "Aguardando USB";
            _lblConnectionBadge.BackColor = Color.FromArgb(30, 41, 59);
            _lblConnectionBadge.ForeColor = reason == 1 ? Color.FromArgb(245, 158, 11) : Color.FromArgb(56, 189, 248);

            if (!string.IsNullOrEmpty(msg))
            {
                _lblPhoneState.ForeColor = reason == 1 ? Color.FromArgb(251, 191, 36) : Color.FromArgb(148, 163, 184);
                _lblPhoneState.Text = msg;
            }
            _lblLiveDiag.ForeColor = Color.FromArgb(148, 163, 184);
            _lblLiveDiag.Text = "Telemetria ao vivo: desconectado.";
            WriteLiveSnapshot();
        }

        private void OnUiTick(object sender, EventArgs e)
        {
            float lvl = _capture.CurrentLevel;
            int barW = (int)Math.Round(Math.Min(1f, Math.Max(0f, lvl)) * _vuHost.Width);
            if (_vuBar.Width != barW)
            {
                _vuBar.Width = barW;
            }

            long now = _sessionClock.ElapsedMilliseconds;
            NativeUsbAudioClient activeCli;
            lock (_clientLock) { activeCli = _usbClient; }
            if (activeCli != null && activeCli.IsConnected && (now - _lastPingMs) >= 1500)
            {
                _lastPingMs = now;
                activeCli.SendPing();
            }

            if ((now - _lastLiveJsonMs) >= 500)
            {
                _lastLiveJsonMs = now;
                WriteLiveSnapshot();
            }

            _adbPollCounter++;
            int pollEvery = (activeCli != null && activeCli.IsConnected) ? 25 : 12;
            if (_adbPollCounter >= pollEvery)
            {
                _adbPollCounter = 0;
                RefreshAdbDevices(false);
            }
        }

        private void WriteLiveSnapshot()
        {
            try
            {
                NativeUsbAudioClient cli;
                lock (_clientLock) { cli = _usbClient; }
                bool conn = cli != null && cli.IsConnected;
                ReceiverStats st = cli != null ? cli.LatestStats : null;

                var sb = new StringBuilder();
                sb.Append("{");
                sb.AppendFormat("\"connected\":{0},", conn ? "true" : "false");
                sb.AppendFormat("\"serial\":\"{0}\",", EscapeJson(_activeSerial));
                sb.AppendFormat("\"model\":\"{0}\",", EscapeJson(_activeModel));
                sb.AppendFormat("\"targetMs\":{0},", _adaptive.TargetMs);
                sb.AppendFormat("\"gainPct\":{0},", _pendingGainPct);
                sb.AppendFormat("\"adaptiveState\":\"{0}\",", _adaptive.State.ToString());
                sb.AppendFormat("\"knownFloorMs\":{0},", _adaptive.KnownUnsafeFloorMs);
                sb.AppendFormat("\"glitchesPerSec\":{0},", _glitchesPerSec);
                sb.AppendFormat("\"disconnectReason\":\"{0}\",", EscapeJson(_lastDisconnectReason));
                if (st != null)
                {
                    sb.AppendFormat("\"seqLast\":{0},", st.SeqLast);
                    sb.AppendFormat(CultureInfo.InvariantCulture, "\"bufMs\":{0:0.0},", st.BufMs);
                    sb.AppendFormat(CultureInfo.InvariantCulture, "\"minBufMs\":{0:0.0},", st.MinBufMs);
                    sb.AppendFormat("\"pktGapMaxMs\":{0},", st.PktGapMaxMs);
                    sb.AppendFormat("\"rttMs\":{0},", st.RttMs);
                    sb.AppendFormat("\"plcTotal\":{0},", st.PlcTotal);
                    sb.AppendFormat("\"xrunTotal\":{0},", st.XRunTotal);
                    sb.AppendFormat("\"resyncTotal\":{0},", st.ResyncTotal);
                    sb.AppendFormat("\"aaudioRate\":{0},", st.AAudioSampleRate);
                    sb.AppendFormat("\"burstFrames\":{0},", st.AAudioBurstFrames);
                    sb.AppendFormat("\"bufFrames\":{0},", st.AAudioBufFrames);
                    sb.AppendFormat("\"aaudioLowLatency\":{0},", st.IsLowLatency ? "true" : "false");
                    sb.AppendFormat("\"aaudioExclusive\":{0},", st.IsExclusive ? "true" : "false");
                    sb.AppendFormat("\"aaudioMmap\":{0}", st.IsMmap ? "true" : "false");
                }
                else
                {
                    sb.Append("\"seqLast\":0,\"bufMs\":0,\"minBufMs\":0,\"pktGapMaxMs\":0,\"rttMs\":0,\"plcTotal\":0,\"xrunTotal\":0,\"resyncTotal\":0,\"aaudioRate\":0,\"burstFrames\":0,\"bufFrames\":0,\"aaudioLowLatency\":false,\"aaudioExclusive\":false,\"aaudioMmap\":false");
                }
                sb.Append("}");

                string path = Path.Combine(Path.GetTempPath(), "PhoneAudioBridge_live.json");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            _trayIcon.ShowBalloonTip(1800, "Phone Audio Bridge", "Rodando na bandeja (^). Detectará o cabo USB automaticamente.", ToolTipIcon.Info);
        }

        private void ShowFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
                ShowInTaskbar = false;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            NativeUsbAudioClient cli;
            lock (_clientLock) { cli = _usbClient; }
            bool active = cli != null && cli.IsConnected;

            if (!_forceExit && e.CloseReason == CloseReason.UserClosing && active)
            {
                DialogResult res = MessageBox.Show(
                    this,
                    "O áudio do PC está transmitindo pelo cabo USB. Deseja ocultar na bandeja (^) ou encerrar?\n\nSim = Ocultar na bandeja\nNão = Encerrar tudo",
                    "Phone Audio Bridge",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);
                if (res == DialogResult.Cancel) { e.Cancel = true; return; }
                if (res == DialogResult.Yes) { e.Cancel = true; HideToTray(); return; }
            }

            CleanupAll();
            base.OnFormClosing(e);
        }

        private void ExitCleanly()
        {
            _forceExit = true;
            Close();
        }

        private void CleanupAll()
        {
            if (_uiTimer != null)
            {
                _uiTimer.Stop();
                _uiTimer.Dispose();
            }
            DisconnectClientOnly(0, "Encerrado.", true);
            _capture.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegPath, false))
                {
                    if (key == null) return false;
                    object val = key.GetValue(AppRunName);
                    return val != null && val.ToString().Length > 0;
                }
            }
            catch { return false; }
        }

        private void ToggleStartup()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunRegPath, true))
                {
                    if (key == null) return;
                    if (IsStartupEnabled())
                    {
                        key.DeleteValue(AppRunName, false);
                        _miStartup.Checked = false;
                    }
                    else
                    {
                        key.SetValue(AppRunName, "\"" + Application.ExecutablePath + "\" --minimized");
                        _miStartup.Checked = true;
                    }
                }
            }
            catch { }
        }

        private static Icon CreateTrayIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(24, 34, 52), Color.FromArgb(14, 19, 28), 45f))
                    {
                        g.FillEllipse(bg, 1, 1, 30, 30);
                    }
                    using (var border = new Pen(Color.FromArgb(34, 197, 94), 2f))
                    {
                        g.DrawEllipse(border, 2, 2, 28, 28);
                    }
                    using (var arcPen = new Pen(Color.FromArgb(56, 189, 248), 2.2f))
                    {
                        arcPen.StartCap = LineCap.Round;
                        arcPen.EndCap = LineCap.Round;
                        g.DrawArc(arcPen, 8, 8, 16, 16, 180, 180);
                    }
                    using (var earBrush = new SolidBrush(Color.FromArgb(34, 197, 94)))
                    {
                        g.FillRectangle(earBrush, 7, 15, 4, 8);
                        g.FillRectangle(earBrush, 21, 15, 4, 8);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
                finally
                {
                    NativeWin32.DestroyIcon(hIcon);
                }
            }
        }
    }

    #endregion

    #region Self-Test Runner & Entry Point

    internal static class SelfTestRunner
    {
        public static int Run(string[] args)
        {
            NativeWin32.AttachConsole(NativeWin32.ATTACH_PARENT_PROCESS);
            string reportPath = args.Length >= 2 ? args[1] : "";

            var sb = new StringBuilder();
            bool allOk = true;

            // 1. WASAPI Loopback Event-Driven Probe
            var devs = WasapiLoopbackCapture.ListOutputDevices();
            string probe = WasapiLoopbackCapture.ProbeLoopbackQuick("");
            bool wasapiOk = devs.Count >= 2 && probe.StartsWith("OK ", StringComparison.Ordinal);
            sb.AppendLine(string.Format("[{0}] WASAPI: count={1} probe={2}", wasapiOk ? "PASS" : "FAIL", devs.Count, probe));
            if (!wasapiOk) allOk = false;

            // 2. Embedded PhoneAudioReceiver.apk Resource Verification
            bool apkOk = false;
            string apkInfo = "missing";
            using (Stream s = AdbHelper.OpenEmbeddedApkStream())
            {
                if (s != null && s.Length > 10000)
                {
                    byte[] raw = new byte[s.Length];
                    int got = 0;
                    while (got < raw.Length)
                    {
                        int n = s.Read(raw, got, raw.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    bool zipMagic = got > 4 && raw[0] == (byte)'P' && raw[1] == (byte)'K';
                    bool hasDex = ContainsAscii(raw, got, "classes.dex");
                    bool hasSo = ContainsAscii(raw, got, "lib/arm64-v8a/libphoneaudioreceiver.so");
                    apkOk = zipMagic && hasDex && hasSo;
                    apkInfo = string.Format("bytes={0} zip={1} dex={2} arm64so={3}", got, zipMagic, hasDex, hasSo);
                }
            }
            sb.AppendLine(string.Format("[{0}] EmbeddedAPK: {1}", apkOk ? "PASS" : "FAIL", apkInfo));
            if (!apkOk) allOk = false;

            // 3. ADB Discovery Verification
            string adbPath = AdbHelper.FindAdbExecutable();
            bool adbOk = !string.IsNullOrEmpty(adbPath) && File.Exists(adbPath);
            sb.AppendLine(string.Format("[{0}] ADB: path={1}", adbOk ? "PASS" : "FAIL", adbOk ? adbPath : "not found"));
            if (!adbOk) allOk = false;

            // 4. Binary USB Protocol Round-Trip Verification
            string protoDetails;
            bool protoOk = TestBinaryProtocolRoundTrip(out protoDetails);
            sb.AppendLine(string.Format("[{0}] BinaryProtocol: {1}", protoOk ? "PASS" : "FAIL", protoDetails));
            if (!protoOk) allOk = false;

            // 5. AdaptiveLatencyController Deterministic Verification
            string adaptDetails;
            bool adaptOk = TestAdaptiveController(out adaptDetails);
            sb.AppendLine(string.Format("[{0}] AdaptiveController: {1}", adaptOk ? "PASS" : "FAIL", adaptDetails));
            if (!adaptOk) allOk = false;

            sb.AppendLine("OVERALL: " + (allOk ? "PASS" : "FAIL"));
            string report = sb.ToString();
            Console.WriteLine(report);

            if (!string.IsNullOrEmpty(reportPath))
            {
                File.WriteAllText(reportPath, report, Encoding.UTF8);
            }
            return allOk ? 0 : 1;
        }

        private static bool ContainsAscii(byte[] haystack, int len, string needle)
        {
            byte[] pat = Encoding.ASCII.GetBytes(needle);
            int limit = len - pat.Length;
            for (int i = 0; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < pat.Length; j++)
                {
                    if (haystack[i + j] != pat[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
            return false;
        }

        private static bool TestBinaryProtocolRoundTrip(out string details)
        {
            details = "";
            TcpListener srv = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                srv.Start();
                int port = ((IPEndPoint)srv.LocalEndpoint).Port;

                bool serverSawConfig = false;
                bool serverSawPcm = false;
                bool serverSawTarget = false;
                bool serverSawStop = false;

                var srvThread = new Thread(delegate()
                {
                    try
                    {
                        using (TcpClient acc = srv.AcceptTcpClient())
                        using (NetworkStream ns = acc.GetStream())
                        {
                            acc.NoDelay = true;
                            byte[] hdr = new byte[3];
                            while (ReadExactTest(ns, hdr, 3))
                            {
                                byte t = hdr[0];
                                ushort len = BridgeProtocol.ReadU16(hdr, 1);
                                byte[] p = len > 0 ? new byte[len] : new byte[0];
                                if (len > 0 && !ReadExactTest(ns, p, len)) break;

                                if (t == BridgeProtocol.MsgConfig && len == 8)
                                {
                                    serverSawConfig = BridgeProtocol.ReadU32(p, 0) == 48000;
                                    // Send mock STATS (0x81)
                                    byte[] st = new byte[35];
                                    st[0] = BridgeProtocol.MsgStats;
                                    BridgeProtocol.WriteU16(st, 1, 32);
                                    BridgeProtocol.WriteU32(st, 3, 1);
                                    BridgeProtocol.WriteU16(st, 7, 105);   // 10.5 ms
                                    BridgeProtocol.WriteU16(st, 9, 55);    // 5.5 ms min
                                    BridgeProtocol.WriteU16(st, 11, 11);   // 11 ms gap
                                    BridgeProtocol.WriteU16(st, 13, 192);  // burst
                                    BridgeProtocol.WriteU16(st, 15, 384);  // buf
                                    BridgeProtocol.WriteU16(st, 17, 1);    // LL
                                    BridgeProtocol.WriteU32(st, 31, 48000);
                                    ns.Write(st, 0, st.Length);
                                    ns.Flush();
                                }
                                else if (t == BridgeProtocol.MsgPing && len == 8)
                                {
                                    byte[] pong = new byte[11];
                                    pong[0] = BridgeProtocol.MsgPong;
                                    BridgeProtocol.WriteU16(pong, 1, 8);
                                    Buffer.BlockCopy(p, 0, pong, 3, 8);
                                    ns.Write(pong, 0, pong.Length);
                                    ns.Flush();
                                }
                                else if (t == BridgeProtocol.MsgPcm && len == 4 + 1920)
                                {
                                    serverSawPcm = true;
                                }
                                else if (t == BridgeProtocol.MsgSetTargetMs && len == 2)
                                {
                                    serverSawTarget = BridgeProtocol.ReadU16(p, 0) == 8;
                                }
                                else if (t == BridgeProtocol.MsgStop)
                                {
                                    serverSawStop = true;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                });
                srvThread.IsBackground = true;
                srvThread.Start();

                using (var cli = new NativeUsbAudioClient())
                {
                    string err;
                    if (!cli.Connect("127.0.0.1", port, 48000, 10, 100, out err))
                    {
                        details = "connect failed: " + err;
                        return false;
                    }
                    cli.EnqueuePcm(new byte[1920]);
                    cli.SendTargetMs(8);
                    cli.SendPing();
                    Thread.Sleep(150);
                    cli.SendStopAndClose();
                }

                srvThread.Join(1200);
                bool statsParsed = serverSawConfig && serverSawPcm && serverSawTarget && serverSawStop;
                details = string.Format("cfg={0} pcm={1} target={2} stop={3}", serverSawConfig, serverSawPcm, serverSawTarget, serverSawStop);
                return statsParsed;
            }
            finally
            {
                try { srv.Stop(); } catch { }
            }
        }

        private static bool ReadExactTest(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private static bool TestAdaptiveController(out string details)
        {
            // Scenario 1: Silent downshift 10 -> 9 -> 8, glitch on probe rolls back 8 -> 9 + locks floor
            var c1 = new AdaptiveLatencyController(10);
            c1.Reset(10, 0);
            c1.ProcessStats(0, 500);
            c1.ProcessStats(0, 1600);   // exits Warmup -> Monitoring (zeroStart=1600)
            c1.ProcessStats(0, 7800);   // +6200ms clean -> downshift to 9 (ProbingDown, stateStart=7800)
            if (c1.TargetMs != 9 || c1.State != AdaptiveState.ProbingDown)
            {
                details = string.Format("S1 step1 fail: ms={0} st={1}", c1.TargetMs, c1.State);
                return false;
            }
            c1.ProcessStats(0, 11400);  // +3600ms clean -> back to Monitoring (zeroStart=11400)
            c1.ProcessStats(0, 17600);  // +6200ms clean -> downshift to 8 (ProbingDown)
            if (c1.TargetMs != 8 || c1.State != AdaptiveState.ProbingDown)
            {
                details = string.Format("S1 step2 fail: ms={0} st={1}", c1.TargetMs, c1.State);
                return false;
            }
            c1.ProcessStats(1, 18200);  // glitch during probe -> rollback to 9 + FloorLocked (unsafe=8)
            if (c1.TargetMs != 9 || c1.State != AdaptiveState.FloorLocked || c1.KnownUnsafeFloorMs != 8)
            {
                details = string.Format("S1 rollback fail: ms={0} st={1} floor={2}", c1.TargetMs, c1.State, c1.KnownUnsafeFloorMs);
                return false;
            }
            c1.ProcessStats(1, 30000);  // remains locked at 9 ms
            if (c1.TargetMs != 9)
            {
                details = "S1 drift after lock";
                return false;
            }

            // Scenario 2: Sustained glitches -> stabilize prompt -> Yes -> ramps 10->12->14 -> locks after 4s clean
            var c2 = new AdaptiveLatencyController(10);
            int promptedAt = -1;
            c2.OnStabilizePromptRequested = delegate(int ms, double rate) { promptedAt = ms; };
            c2.Reset(10, 0);
            c2.ProcessStats(0, 500);
            c2.ProcessStats(0, 1600);   // Monitoring
            c2.ProcessStats(2, 2200);
            c2.ProcessStats(4, 3000);   // 4 glitches in <3s -> PromptPending
            if (promptedAt != 10 || c2.State != AdaptiveState.PromptPending)
            {
                details = string.Format("S2 prompt fail: promptedAt={0} st={1}", promptedAt, c2.State);
                return false;
            }
            c2.StartStabilizing(3200);  // immediate step 10 -> 12
            if (c2.TargetMs != 12 || c2.State != AdaptiveState.Stabilizing)
            {
                details = string.Format("S2 ramp1 fail: ms={0} st={1}", c2.TargetMs, c2.State);
                return false;
            }
            c2.ProcessStats(5, 5800);   // glitch >= 2.5s later -> ramp 12 -> 14
            if (c2.TargetMs != 14)
            {
                details = string.Format("S2 ramp2 fail: ms={0}", c2.TargetMs);
                return false;
            }
            c2.ProcessStats(5, 10000);  // >4.0s with 0 delta -> FloorLocked at 14
            if (c2.State != AdaptiveState.FloorLocked || c2.TargetMs != 14)
            {
                details = string.Format("S2 lock fail: ms={0} st={1}", c2.TargetMs, c2.State);
                return false;
            }

            // Scenario 3: Declining prompt suppresses it permanently across glitches, manual changes, and reconnects
            var c3 = new AdaptiveLatencyController(10);
            int promptCount = 0;
            c3.OnStabilizePromptRequested = delegate(int ms, double rate) { promptCount++; };
            c3.Reset(10, 0);
            c3.ProcessStats(0, 500);
            c3.ProcessStats(0, 1600);
            c3.ProcessStats(4, 2500);
            if (promptCount != 1)
            {
                details = string.Format("S3 initial prompt fail: count={0}", promptCount);
                return false;
            }
            c3.DeclineStabilize(3000);
            c3.ProcessStats(8, 4000);
            c3.OnManualTargetChanged(8, 5000);
            c3.ProcessStats(8, 16000);
            c3.ProcessStats(14, 17000);
            c3.Reset(10, 20000);
            c3.ProcessStats(0, 22000);
            c3.ProcessStats(6, 23000);
            if (promptCount != 1)
            {
                details = string.Format("S3 re-prompted after decline: count={0}", promptCount);
                return false;
            }

            details = "downshift 10->9->8 rollback->9 lock=8; stabilize 10->12->14 lock; decline suppress OK";
            return true;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length > 0 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                return SelfTestRunner.Run(args);
            }

            bool startMinimized = false;
            bool autoConnect = false;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--minimized", StringComparison.OrdinalIgnoreCase)) startMinimized = true;
                    if (string.Equals(args[i], "--auto-connect", StringComparison.OrdinalIgnoreCase)) autoConnect = true;
                }
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "Local\\PhoneAudioBridge_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "O Phone Audio Bridge já está aberto! Procure o ícone na bandeja do sistema (^).",
                        "Phone Audio Bridge",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainBridgeForm(startMinimized, autoConnect));
                return 0;
            }
        }
    }

    #endregion
}
