using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PhoneAudioBridge
{
    internal enum BridgeMode
    {
        Wifi = 0,
        Usb = 1
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

    internal sealed class NetEndpointOption
    {
        public string InterfaceName;
        public string Description;
        public string IpAddress;
        public bool IsUsb;
        public bool IsAdb;
        public bool IsVirtual;

        public string DisplayLabel
        {
            get
            {
                if (IsAdb)
                {
                    return string.Format("[ADB Cabo USB] {0} ({1})", IpAddress, InterfaceName);
                }
                if (IsUsb)
                {
                    return string.Format("[Cabo USB] {0} ({1})", IpAddress, InterfaceName);
                }
                if (IsVirtual)
                {
                    return string.Format("[Virtual] {0} ({1})", IpAddress, InterfaceName);
                }
                return string.Format("[Wi-Fi/LAN] {0} ({1})", IpAddress, InterfaceName);
            }
        }

        public override string ToString()
        {
            return DisplayLabel;
        }
    }

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

    #endregion

    #region WASAPI Loopback Capture Engine

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
        private const ushort WaveFormatPcm = 1;
        private const ushort WaveFormatIeeeFloat = 3;
        private const ushort WaveFormatExtensible = 0xFFFE;

        private static readonly Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static readonly Guid IidAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        private static readonly Guid SubFormatIeeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid SubFormatPcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private static readonly PROPERTYKEY PkeyDeviceFriendlyName = new PROPERTYKEY(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

        private readonly object _sync = new object();
        private volatile bool _running;
        private volatile bool _restartRequested;
        private volatile int _chunkMs = 20;
        private string _requestedDeviceId = "";
        private Thread _thread;

        public volatile float CurrentLevel;
        public volatile int CurrentSampleRate = 48000;
        public volatile string ActiveDeviceName = "Padrão do Windows";
        public volatile string LastError = "";

        public Action<byte[], int> OnPcmPacket;
        public Action<int, string> OnFormatChanged;

        public void SetModeChunkMs(int chunkMs)
        {
            if (chunkMs < 5) chunkMs = 5;
            if (chunkMs > 50) chunkMs = 50;
            _chunkMs = chunkMs;
        }

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
                int hr;
                if (string.IsNullOrEmpty(deviceId))
                {
                    hr = enumerator.GetDefaultAudioEndpoint(ERender, ERoleConsole, out device);
                }
                else
                {
                    hr = enumerator.GetDevice(deviceId, out device);
                }

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
            IntPtr mixFormatPtr = IntPtr.Zero;

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

                int activeChunkMs = _chunkMs;
                int targetChunkBytes = CalculateChunkBytes(outRate, activeChunkMs);
                byte[] accum = new byte[targetChunkBytes * 4];
                int accumOffset = 0;

                byte[] rawScratch = new byte[65536];
                long lastPacketTick = Environment.TickCount;
                long lastDefaultCheckTick = lastPacketTick;
                string activeDevId = "";
                device.GetId(out activeDevId);

                while (_running && !_restartRequested)
                {
                    if (_chunkMs != activeChunkMs)
                    {
                        activeChunkMs = _chunkMs;
                        targetChunkBytes = CalculateChunkBytes(outRate, activeChunkMs);
                        if (accum.Length < targetChunkBytes * 4)
                        {
                            accum = new byte[targetChunkBytes * 4];
                        }
                        accumOffset = 0;
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
                        long nowTick = Environment.TickCount;
                        if (unchecked(nowTick - lastPacketTick) > 1200)
                        {
                            CurrentLevel = 0f;
                            accumOffset = 0;
                        }

                        if (string.IsNullOrEmpty(targetId) && unchecked(nowTick - lastDefaultCheckTick) > 1500)
                        {
                            lastDefaultCheckTick = nowTick;
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
                        if (hr == AudclntDeviceInvalidated)
                        {
                            return;
                        }
                        if (hr != 0)
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
                                    lastPacketTick = Environment.TickCount;
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
                                    }
                                    else
                                    {
                                        Array.Clear(accum, accumOffset, pcmBytes);
                                    }

                                    float smoothed = CurrentLevel * 0.65f + Math.Min(1f, peak * 1.4f) * 0.35f;
                                    CurrentLevel = smoothed;
                                    lastPacketTick = Environment.TickCount;

                                    accumOffset += pcmBytes;
                                    int sendOffset = 0;
                                    while (accumOffset - sendOffset >= targetChunkBytes)
                                    {
                                        byte[] chunk = new byte[targetChunkBytes];
                                        Buffer.BlockCopy(accum, sendOffset, chunk, 0, targetChunkBytes);
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
                if (audioEvent != null)
                {
                    try { audioEvent.Close(); } catch { }
                }
                if (mixFormatPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormatPtr);
                if (captureClient != null) Marshal.ReleaseComObject(captureClient);
                if (audioClient != null) Marshal.ReleaseComObject(audioClient);
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
                if (subFormat == SubFormatIeeeFloat)
                {
                    return true;
                }
                if (subFormat == SubFormatPcm)
                {
                    return false;
                }
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

    #region QR Code Generator (ISO/IEC 18004, Versions 1-5, Level M)

    internal sealed class QrMatrixResult
    {
        public int Version;
        public int Size;
        public bool[,] Modules;
        public int Mask;
        public List<byte[]> DataBlocks;
        public List<byte[]> EcBlocks;
    }

    internal static class QrCodeGenerator
    {
        private static readonly int[] GfExp = new int[512];
        private static readonly int[] GfLog = new int[256];

        private struct VersionSpec
        {
            public int Version;
            public int Size;
            public int TotalDataBytes;
            public int EcBytesPerBlock;
            public int NumBlocks;
            public int AlignPos;
        }

        private static readonly VersionSpec[] SpecsM = new VersionSpec[]
        {
            new VersionSpec { Version = 1, Size = 21, TotalDataBytes = 16, EcBytesPerBlock = 10, NumBlocks = 1, AlignPos = 0 },
            new VersionSpec { Version = 2, Size = 25, TotalDataBytes = 28, EcBytesPerBlock = 16, NumBlocks = 1, AlignPos = 18 },
            new VersionSpec { Version = 3, Size = 29, TotalDataBytes = 44, EcBytesPerBlock = 26, NumBlocks = 1, AlignPos = 22 },
            new VersionSpec { Version = 4, Size = 33, TotalDataBytes = 64, EcBytesPerBlock = 18, NumBlocks = 2, AlignPos = 26 },
            new VersionSpec { Version = 5, Size = 37, TotalDataBytes = 86, EcBytesPerBlock = 24, NumBlocks = 2, AlignPos = 30 }
        };

        static QrCodeGenerator()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                GfExp[i] = x;
                GfLog[x] = i;
                x <<= 1;
                if ((x & 0x100) != 0)
                {
                    x ^= 0x11D;
                }
            }
            for (int i = 255; i < 512; i++)
            {
                GfExp[i] = GfExp[i - 255];
            }
        }

        private static int GfMul(int a, int b)
        {
            if (a == 0 || b == 0) return 0;
            return GfExp[GfLog[a] + GfLog[b]];
        }

        private static byte[] BuildGeneratorPoly(int degree)
        {
            byte[] poly = new byte[degree + 1];
            poly[0] = 1;
            for (int i = 0; i < degree; i++)
            {
                int root = GfExp[i];
                for (int j = i + 1; j >= 1; j--)
                {
                    poly[j] = (byte)(poly[j] ^ GfMul(poly[j - 1], root));
                }
            }
            return poly;
        }

        private static byte[] ComputeRsRemainder(byte[] data, int ecCount)
        {
            byte[] gen = BuildGeneratorPoly(ecCount);
            byte[] rem = new byte[ecCount];
            for (int i = 0; i < data.Length; i++)
            {
                byte factor = (byte)(data[i] ^ rem[0]);
                Buffer.BlockCopy(rem, 1, rem, 0, ecCount - 1);
                rem[ecCount - 1] = 0;
                for (int j = 0; j < ecCount; j++)
                {
                    rem[j] ^= (byte)GfMul(gen[j + 1], factor);
                }
            }
            return rem;
        }

        public static bool VerifyReedSolomonSyndromes(QrMatrixResult qr)
        {
            if (qr == null || qr.DataBlocks.Count != qr.EcBlocks.Count) return false;
            for (int b = 0; b < qr.DataBlocks.Count; b++)
            {
                byte[] d = qr.DataBlocks[b];
                byte[] e = qr.EcBlocks[b];
                int n = d.Length + e.Length;
                byte[] cw = new byte[n];
                Buffer.BlockCopy(d, 0, cw, 0, d.Length);
                Buffer.BlockCopy(e, 0, cw, d.Length, e.Length);

                for (int i = 0; i < e.Length; i++)
                {
                    int alpha = GfExp[i];
                    int eval = 0;
                    for (int k = 0; k < n; k++)
                    {
                        eval = GfMul(eval, alpha) ^ cw[k];
                    }
                    if (eval != 0)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public static QrMatrixResult Encode(string text)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            VersionSpec spec = new VersionSpec();
            bool found = false;
            for (int i = 0; i < SpecsM.Length; i++)
            {
                int maxBytes = (SpecsM[i].TotalDataBytes * 8 - 12) / 8;
                if (payload.Length <= maxBytes)
                {
                    spec = SpecsM[i];
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new ArgumentException("Texto muito longo para QR Code V1-V5: " + payload.Length + " bytes.");
            }

            List<int> bits = new List<int>(spec.TotalDataBytes * 8);
            AppendBits(bits, 4, 4); // Mode Byte = 0100
            AppendBits(bits, payload.Length, 8);
            for (int i = 0; i < payload.Length; i++)
            {
                AppendBits(bits, payload[i], 8);
            }

            int totalDataBits = spec.TotalDataBytes * 8;
            int term = Math.Min(4, totalDataBits - bits.Count);
            AppendBits(bits, 0, term);
            while ((bits.Count % 8) != 0)
            {
                bits.Add(0);
            }

            byte[] dataBytes = new byte[spec.TotalDataBytes];
            int byteCount = bits.Count / 8;
            for (int i = 0; i < byteCount; i++)
            {
                int v = 0;
                for (int b = 0; b < 8; b++)
                {
                    v = (v << 1) | bits[i * 8 + b];
                }
                dataBytes[i] = (byte)v;
            }
            for (int i = byteCount; i < spec.TotalDataBytes; i++)
            {
                dataBytes[i] = ((i - byteCount) % 2 == 0) ? (byte)0xEC : (byte)0x11;
            }

            int blockDataLen = spec.TotalDataBytes / spec.NumBlocks;
            var dataBlocks = new List<byte[]>();
            var ecBlocks = new List<byte[]>();
            for (int b = 0; b < spec.NumBlocks; b++)
            {
                byte[] blk = new byte[blockDataLen];
                Buffer.BlockCopy(dataBytes, b * blockDataLen, blk, 0, blockDataLen);
                byte[] ec = ComputeRsRemainder(blk, spec.EcBytesPerBlock);
                dataBlocks.Add(blk);
                ecBlocks.Add(ec);
            }

            byte[] finalCodewords = new byte[spec.TotalDataBytes + spec.NumBlocks * spec.EcBytesPerBlock];
            int idx = 0;
            for (int i = 0; i < blockDataLen; i++)
            {
                for (int b = 0; b < spec.NumBlocks; b++)
                {
                    finalCodewords[idx++] = dataBlocks[b][i];
                }
            }
            for (int i = 0; i < spec.EcBytesPerBlock; i++)
            {
                for (int b = 0; b < spec.NumBlocks; b++)
                {
                    finalCodewords[idx++] = ecBlocks[b][i];
                }
            }

            int size = spec.Size;
            bool[,] baseMods = new bool[size, size];
            bool[,] isFunc = new bool[size, size];

            DrawFinderWithSeparator(baseMods, isFunc, 0, 0, size);
            DrawFinderWithSeparator(baseMods, isFunc, 0, size - 7, size);
            DrawFinderWithSeparator(baseMods, isFunc, size - 7, 0, size);

            if (spec.AlignPos > 0)
            {
                DrawAlignmentPattern(baseMods, isFunc, spec.AlignPos, spec.AlignPos);
            }

            for (int i = 8; i < size - 8; i++)
            {
                bool dark = (i % 2) == 0;
                SetFuncModule(baseMods, isFunc, 6, i, dark);
                SetFuncModule(baseMods, isFunc, i, 6, dark);
            }

            // Dark module always at (size - 8, 8)
            SetFuncModule(baseMods, isFunc, size - 8, 8, true);

            // Reserve format information cells
            ReserveFormatModules(isFunc, size);

            // Place data bits in zig-zag
            int bitPos = 0;
            int totalCodewordBits = finalCodewords.Length * 8;
            int dir = -1;
            int col = size - 1;
            while (col > 0)
            {
                if (col == 6) col--;
                int r = (dir == -1) ? (size - 1) : 0;
                while (r >= 0 && r < size)
                {
                    for (int dc = 0; dc < 2; dc++)
                    {
                        int c = col - dc;
                        if (!isFunc[r, c])
                        {
                            bool bitVal = false;
                            if (bitPos < totalCodewordBits)
                            {
                                bitVal = ((finalCodewords[bitPos >> 3] >> (7 - (bitPos & 7))) & 1) != 0;
                                bitPos++;
                            }
                            baseMods[r, c] = bitVal;
                        }
                    }
                    r += dir;
                }
                dir = -dir;
                col -= 2;
            }

            int bestMask = 0;
            int bestPenalty = int.MaxValue;
            bool[,] bestGrid = null;

            for (int mask = 0; mask < 8; mask++)
            {
                bool[,] cand = new bool[size, size];
                for (int r = 0; r < size; r++)
                {
                    for (int c = 0; c < size; c++)
                    {
                        if (isFunc[r, c])
                        {
                            cand[r, c] = baseMods[r, c];
                        }
                        else
                        {
                            cand[r, c] = baseMods[r, c] ^ IsMasked(mask, r, c);
                        }
                    }
                }
                WriteFormatInfo(cand, size, mask);
                int pen = ComputePenalty(cand, size);
                if (pen < bestPenalty)
                {
                    bestPenalty = pen;
                    bestMask = mask;
                    bestGrid = cand;
                }
            }

            return new QrMatrixResult
            {
                Version = spec.Version,
                Size = size,
                Modules = bestGrid,
                Mask = bestMask,
                DataBlocks = dataBlocks,
                EcBlocks = ecBlocks
            };
        }

        public static Bitmap RenderBitmap(QrMatrixResult qr, int pixelsPerModule, int quietModules)
        {
            int totalMods = qr.Size + quietModules * 2;
            int imgSize = totalMods * pixelsPerModule;
            Bitmap bmp = new Bitmap(imgSize, imgSize, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.None;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                using (SolidBrush black = new SolidBrush(Color.FromArgb(14, 18, 28)))
                {
                    for (int r = 0; r < qr.Size; r++)
                    {
                        for (int c = 0; c < qr.Size; c++)
                        {
                            if (qr.Modules[r, c])
                            {
                                g.FillRectangle(
                                    black,
                                    (c + quietModules) * pixelsPerModule,
                                    (r + quietModules) * pixelsPerModule,
                                    pixelsPerModule,
                                    pixelsPerModule);
                            }
                        }
                    }
                }
            }
            return bmp;
        }

        private static void AppendBits(List<int> bits, int value, int bitCount)
        {
            for (int i = bitCount - 1; i >= 0; i--)
            {
                bits.Add((value >> i) & 1);
            }
        }

        private static void SetFuncModule(bool[,] mods, bool[,] isFunc, int r, int c, bool dark)
        {
            mods[r, c] = dark;
            isFunc[r, c] = true;
        }

        private static void DrawFinderWithSeparator(bool[,] mods, bool[,] isFunc, int top, int left, int size)
        {
            for (int dr = -1; dr <= 7; dr++)
            {
                for (int dc = -1; dc <= 7; dc++)
                {
                    int r = top + dr;
                    int c = left + dc;
                    if (r < 0 || r >= size || c < 0 || c >= size) continue;
                    bool dark = false;
                    if (dr >= 0 && dr <= 6 && dc >= 0 && dc <= 6)
                    {
                        int dist = Math.Max(Math.Abs(dr - 3), Math.Abs(dc - 3));
                        dark = dist != 2;
                    }
                    SetFuncModule(mods, isFunc, r, c, dark);
                }
            }
        }

        private static void DrawAlignmentPattern(bool[,] mods, bool[,] isFunc, int centerR, int centerC)
        {
            for (int dr = -2; dr <= 2; dr++)
            {
                for (int dc = -2; dc <= 2; dc++)
                {
                    int dist = Math.Max(Math.Abs(dr), Math.Abs(dc));
                    SetFuncModule(mods, isFunc, centerR + dr, centerC + dc, dist != 1);
                }
            }
        }

        private static void ReserveFormatModules(bool[,] isFunc, int size)
        {
            for (int i = 0; i <= 8; i++)
            {
                if (i != 6)
                {
                    isFunc[8, i] = true;
                    isFunc[i, 8] = true;
                }
            }
            for (int i = 0; i < 8; i++)
            {
                isFunc[8, size - 1 - i] = true;
                if (i < 7)
                {
                    isFunc[size - 1 - i, 8] = true;
                }
            }
        }

        private static bool IsMasked(int mask, int r, int c)
        {
            switch (mask)
            {
                case 0: return ((r + c) % 2) == 0;
                case 1: return (r % 2) == 0;
                case 2: return (c % 3) == 0;
                case 3: return ((r + c) % 3) == 0;
                case 4: return (((r / 2) + (c / 3)) % 2) == 0;
                case 5: return (((r * c) % 2) + ((r * c) % 3)) == 0;
                case 6: return ((((r * c) % 2) + ((r * c) % 3)) % 2) == 0;
                case 7: return ((((r + c) % 2) + ((r * c) % 3)) % 2) == 0;
                default: return false;
            }
        }

        private static void WriteFormatInfo(bool[,] grid, int size, int mask)
        {
            // Level M = 00
            int data5 = mask & 0x7;
            int rem = data5 << 10;
            for (int i = 14; i >= 10; i--)
            {
                if (((rem >> i) & 1) != 0)
                {
                    rem ^= (0x537 << (i - 10));
                }
            }
            int fmt = ((data5 << 10) | rem) ^ 0x5412;

            // Copy 1 (top-left)
            for (int i = 0; i <= 5; i++)
            {
                grid[8, i] = ((fmt >> (14 - i)) & 1) != 0;
            }
            grid[8, 7] = ((fmt >> 8) & 1) != 0;
            grid[8, 8] = ((fmt >> 7) & 1) != 0;
            grid[7, 8] = ((fmt >> 6) & 1) != 0;
            for (int i = 0; i <= 5; i++)
            {
                grid[5 - i, 8] = ((fmt >> (5 - i)) & 1) != 0;
            }

            // Copy 2 (bottom-left + top-right)
            for (int i = 0; i < 7; i++)
            {
                grid[size - 1 - i, 8] = ((fmt >> (14 - i)) & 1) != 0;
            }
            for (int i = 0; i < 8; i++)
            {
                grid[8, size - 8 + i] = ((fmt >> (7 - i)) & 1) != 0;
            }
        }

        private static int ComputePenalty(bool[,] m, int size)
        {
            int pen = 0;

            // Rule 1: 5+ consecutive same-color modules
            for (int r = 0; r < size; r++)
            {
                int run = 1;
                for (int c = 1; c < size; c++)
                {
                    if (m[r, c] == m[r, c - 1])
                    {
                        run++;
                    }
                    else
                    {
                        if (run >= 5) pen += 3 + (run - 5);
                        run = 1;
                    }
                }
                if (run >= 5) pen += 3 + (run - 5);
            }
            for (int c = 0; c < size; c++)
            {
                int run = 1;
                for (int r = 1; r < size; r++)
                {
                    if (m[r, c] == m[r - 1, c])
                    {
                        run++;
                    }
                    else
                    {
                        if (run >= 5) pen += 3 + (run - 5);
                        run = 1;
                    }
                }
                if (run >= 5) pen += 3 + (run - 5);
            }

            // Rule 2: 2x2 blocks
            for (int r = 0; r < size - 1; r++)
            {
                for (int c = 0; c < size - 1; c++)
                {
                    bool v = m[r, c];
                    if (m[r, c + 1] == v && m[r + 1, c] == v && m[r + 1, c + 1] == v)
                    {
                        pen += 3;
                    }
                }
            }

            // Rule 3: finder-like patterns 1:1:3:1:1 with 4 light modules on either side
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c <= size - 11; c++)
                {
                    if (MatchesFinderLikeRow(m, r, c) || MatchesFinderLikeRowRev(m, r, c)) pen += 40;
                }
            }
            for (int c = 0; c < size; c++)
            {
                for (int r = 0; r <= size - 11; r++)
                {
                    if (MatchesFinderLikeCol(m, r, c) || MatchesFinderLikeColRev(m, r, c)) pen += 40;
                }
            }

            // Rule 4: dark ratio deviation from 50%
            int dark = 0;
            int total = size * size;
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c < size; c++)
                {
                    if (m[r, c]) dark++;
                }
            }
            int pct5 = Math.Abs(dark * 100 - total * 50) * 2 / (total * 10);
            pen += pct5 * 10;

            return pen;
        }

        private static bool MatchesFinderLikeRow(bool[,] m, int r, int c)
        {
            return m[r, c] && !m[r, c + 1] && m[r, c + 2] && m[r, c + 3] && m[r, c + 4] && !m[r, c + 5] && m[r, c + 6]
                && !m[r, c + 7] && !m[r, c + 8] && !m[r, c + 9] && !m[r, c + 10];
        }

        private static bool MatchesFinderLikeRowRev(bool[,] m, int r, int c)
        {
            return !m[r, c] && !m[r, c + 1] && !m[r, c + 2] && !m[r, c + 3]
                && m[r, c + 4] && !m[r, c + 5] && m[r, c + 6] && m[r, c + 7] && m[r, c + 8] && !m[r, c + 9] && m[r, c + 10];
        }

        private static bool MatchesFinderLikeCol(bool[,] m, int r, int c)
        {
            return m[r, c] && !m[r + 1, c] && m[r + 2, c] && m[r + 3, c] && m[r + 4, c] && !m[r + 5, c] && m[r + 6, c]
                && !m[r + 7, c] && !m[r + 8, c] && !m[r + 9, c] && !m[r + 10, c];
        }

        private static bool MatchesFinderLikeColRev(bool[,] m, int r, int c)
        {
            return !m[r, c] && !m[r + 1, c] && !m[r + 2, c] && !m[r + 3, c]
                && m[r + 4, c] && !m[r + 5, c] && m[r + 6, c] && m[r + 7, c] && m[r + 8, c] && !m[r + 9, c] && m[r + 10, c];
        }
    }

    #endregion

    #region Network, USB Tethering & ADB Tunnel Helper

    internal static class NetworkHelper
    {
        private static readonly string[] UsbKeywords = new string[]
        {
            "rndis", "usb", "android", "apple mobile", "iphone", "tethering",
            "gadget", "samsung mobile", "ndis based internet sharing", "ncm", "ecm", "usbn"
        };

        private static readonly string[] VirtualKeywords = new string[]
        {
            "vethernet", "hyper-v", "wsl", "virtualbox", "vmware", "docker",
            "tailscale", "zerotier", "radmin", "hamachi", "wireguard", "nordlynx",
            "loopback", "tap-windows", "wintun", "teredo", "isatap"
        };

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
                    @"C:\Android\platform-tools\adb.exe"
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

        public static bool TrySetupAdbReverse(int port, out string statusMessage)
        {
            statusMessage = "adb.exe não encontrado";
            string adb = FindAdbExecutable();
            if (string.IsNullOrEmpty(adb))
            {
                return false;
            }

            try
            {
                string devsOut = RunShortCommand(adb, "devices", 1800);
                bool hasOnlineDevice = false;
                string[] lines = devsOut.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.EndsWith("\tdevice", StringComparison.OrdinalIgnoreCase) || line.Contains(" device"))
                    {
                        if (!line.Contains("offline") && !line.Contains("unauthorized"))
                        {
                            hasOnlineDevice = true;
                            break;
                        }
                    }
                }

                if (!hasOnlineDevice)
                {
                    statusMessage = "Nenhum celular Android com Depuração USB conectado";
                    return false;
                }

                string revArgs = string.Format("reverse tcp:{0} tcp:{0}", port);
                RunShortCommand(adb, revArgs, 1800);
                statusMessage = string.Format("Túnel ADB ativo (tcp:{0} -> tcp:{0})", port);
                return true;
            }
            catch (Exception ex)
            {
                statusMessage = "Erro ADB: " + ex.Message;
                return false;
            }
        }

        private static string RunShortCommand(string exe, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var proc = Process.Start(psi))
            {
                if (proc == null) return "";
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(timeoutMs);
                return output ?? "";
            }
        }

        public static List<NetEndpointOption> DiscoverEndpoints(bool includeAdbActive)
        {
            var list = new List<NetEndpointOption>();
            var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (includeAdbActive)
            {
                list.Add(new NetEndpointOption
                {
                    InterfaceName = "Túnel ADB",
                    Description = "Android Debug Bridge Reverse",
                    IpAddress = "127.0.0.1",
                    IsUsb = true,
                    IsAdb = true,
                    IsVirtual = false
                });
                seenIps.Add("127.0.0.1");
            }

            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < nics.Length; i++)
                {
                    NetworkInterface nic = nics[i];
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    string combined = ((nic.Name ?? "") + " " + (nic.Description ?? "")).ToLowerInvariant();
                    bool isUsb = ContainsAny(combined, UsbKeywords);
                    bool isVirtual = !isUsb && ContainsAny(combined, VirtualKeywords);

                    IPInterfaceProperties ipProps = nic.GetIPProperties();
                    UnicastIPAddressInformationCollection addrs = ipProps.UnicastAddresses;
                    for (int j = 0; j < addrs.Count; j++)
                    {
                        IPAddress ip = addrs[j].Address;
                        if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ipStr = ip.ToString();
                        if (ipStr.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                        if (ipStr == "127.0.0.1") continue;

                        if (!isUsb && ipStr.StartsWith("192.168.42.", StringComparison.Ordinal))
                        {
                            isUsb = true;
                        }

                        if (!seenIps.Add(ipStr)) continue;

                        list.Add(new NetEndpointOption
                        {
                            InterfaceName = nic.Name ?? "Rede",
                            Description = nic.Description ?? "",
                            IpAddress = ipStr,
                            IsUsb = isUsb,
                            IsAdb = false,
                            IsVirtual = isVirtual
                        });
                    }
                }
            }
            catch
            {
            }

            list.Sort(delegate(NetEndpointOption a, NetEndpointOption b)
            {
                int ra = EndpointRank(a);
                int rb = EndpointRank(b);
                if (ra != rb) return ra.CompareTo(rb);
                return string.Compare(a.IpAddress, b.IpAddress, StringComparison.Ordinal);
            });

            if (list.Count == 0)
            {
                list.Add(new NetEndpointOption
                {
                    InterfaceName = "Localhost",
                    Description = "Nenhuma rede externa detectada",
                    IpAddress = "127.0.0.1",
                    IsUsb = false,
                    IsAdb = false,
                    IsVirtual = false
                });
            }

            return list;
        }

        private static int EndpointRank(NetEndpointOption opt)
        {
            if (opt.IsAdb) return 0;
            if (opt.IsUsb) return 1;
            if (!opt.IsVirtual) return 2;
            return 3;
        }

        private static bool ContainsAny(string text, string[] keywords)
        {
            for (int i = 0; i < keywords.Length; i++)
            {
                if (text.Contains(keywords[i])) return true;
            }
            return false;
        }

        public static bool IsFirewallRulePresent(int port)
        {
            try
            {
                string ruleName = "PhoneAudioBridge TCP " + port;
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = string.Format("advfirewall firewall show rule name=\"{0}\"", ruleName),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return false;
                    string stdout = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(2000);
                    return proc.ExitCode == 0 && !string.IsNullOrEmpty(stdout) && stdout.Contains(port.ToString());
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool AddFirewallRuleElevated(int port, out string error)
        {
            error = "";
            try
            {
                string ruleName = "PhoneAudioBridge TCP " + port;
                string cmdArgs = string.Format(
                    "/c netsh advfirewall firewall delete rule name=\"{0}\" >nul 2>&1 & netsh advfirewall firewall add rule name=\"{0}\" dir=in action=allow protocol=TCP localport={1} profile=any",
                    ruleName,
                    port);

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmdArgs,
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        proc.WaitForExit(10000);
                        return proc.ExitCode == 0;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    #endregion

    #region Embedded Mobile Web Player HTML

    internal static class MobilePlayerHtml
    {
        public static string BuildPage()
        {
            return @"<!DOCTYPE html>
<html lang=""pt-BR"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no"">
<meta name=""theme-color"" content=""#0f131a"">
<title>Phone Audio Bridge</title>
<style>
:root{
  --bg:#0f131a;--card:#181e29;--card2:#1f2736;--border:#2b364a;
  --text:#f1f5f9;--muted:#94a3b8;--green:#22c55e;--blue:#38bdf8;--amber:#f59e0b;--red:#ef4444;
}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent;}
body{margin:0;padding:16px;background:var(--bg);color:var(--text);font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;min-height:100vh;display:flex;flex-direction:column;align-items:center;}
.wrap{width:100%;max-width:440px;display:flex;flex-direction:column;gap:14px;}
.header{display:flex;align-items:center;justify-content:space-between;gap:8px;padding:4px 2px;}
.title{font-size:20px;font-weight:700;letter-spacing:-0.3px;}
.badge{font-size:11px;font-weight:700;padding:5px 10px;border-radius:999px;background:rgba(56,189,248,0.16);color:var(--blue);border:1px solid rgba(56,189,248,0.35);text-transform:uppercase;}
.badge.usb{background:rgba(34,197,94,0.16);color:var(--green);border-color:rgba(34,197,94,0.4);}
.card{background:var(--card);border:1px solid var(--border);border-radius:16px;padding:16px;}
.status-row{display:flex;align-items:center;gap:10px;margin-bottom:12px;}
.dot{width:11px;height:11px;border-radius:50%;background:var(--muted);flex-shrink:0;}
.dot.live{background:var(--green);box-shadow:0 0 10px rgba(34,197,94,0.8);}
.dot.wait{background:var(--amber);}
.dot.err{background:var(--red);}
.status-text{font-size:14px;font-weight:600;}
.sub{font-size:12px;color:var(--muted);margin-top:2px;}
.btn-main{width:100%;padding:16px;border-radius:14px;border:none;font-size:16px;font-weight:700;cursor:pointer;background:linear-gradient(135deg,#22c55e,#16a34a);color:#06210f;box-shadow:0 6px 20px rgba(34,197,94,0.28);transition:transform .08s,opacity .15s;}
.btn-main:active{transform:scale(0.985);}
.btn-main.stop{background:linear-gradient(135deg,#ef4444,#dc2626);color:#fff;box-shadow:0 6px 20px rgba(239,68,68,0.25);}
.meter-bg{height:8px;background:#0b0e14;border-radius:999px;overflow:hidden;margin-top:14px;border:1px solid #222b3b;}
.meter-fill{height:100%;width:0%;background:linear-gradient(90deg,#22c55e,#38bdf8);transition:width 45ms linear;}
.grid{display:grid;grid-template-columns:1fr 1fr;gap:10px;}
.stat{background:var(--card2);border-radius:12px;padding:10px 12px;border:1px solid rgba(255,255,255,0.04);}
.stat-lbl{font-size:11px;color:var(--muted);text-transform:uppercase;letter-spacing:0.4px;}
.stat-val{font-size:17px;font-weight:700;margin-top:3px;font-variant-numeric:tabular-nums;}
.ctrl{margin-bottom:14px;}
.ctrl:last-child{margin-bottom:0;}
.ctrl-head{display:flex;justify-content:space-between;align-items:center;font-size:13px;font-weight:600;margin-bottom:6px;}
.ctrl-val{color:var(--blue);font-variant-numeric:tabular-nums;}
input[type=range]{width:100%;accent-color:#38bdf8;height:6px;}
.presets{display:flex;gap:8px;margin-top:8px;}
.chip{flex:1;padding:8px 6px;border-radius:10px;border:1px solid var(--border);background:var(--card2);color:var(--text);font-size:12px;font-weight:600;cursor:pointer;}
.chip.active{border-color:var(--green);background:rgba(34,197,94,0.14);color:var(--green);}
.row-between{display:flex;align-items:center;justify-content:space-between;gap:10px;}
.switch-btn{padding:8px 12px;border-radius:10px;border:1px solid var(--border);background:var(--card2);color:var(--text);font-size:12px;font-weight:600;cursor:pointer;}
.switch-btn.on{border-color:var(--blue);color:var(--blue);background:rgba(56,189,248,0.14);}
.tip{font-size:12px;line-height:1.45;color:var(--muted);}
</style>
</head>
<body>
<div class=""wrap"">
  <div class=""header"">
    <div class=""title"">Phone Audio Bridge</div>
    <div id=""modeBadge"" class=""badge"">WI-FI</div>
  </div>

  <div class=""card"">
    <div class=""status-row"">
      <div id=""dot"" class=""dot""></div>
      <div>
        <div id=""statusText"" class=""status-text"">Pronto para conectar</div>
        <div id=""deviceText"" class=""sub"">Conecte seu fone Bluetooth no celular e toque abaixo</div>
      </div>
    </div>
    <button id=""playBtn"" class=""btn-main"" type=""button"">Conectar e Ouvir Áudio</button>
    <div class=""meter-bg""><div id=""meterFill"" class=""meter-fill""></div></div>
  </div>

  <div class=""grid"">
    <div class=""stat"">
      <div class=""stat-lbl"">Latência Buffer</div>
      <div id=""bufStat"" class=""stat-val"">-- ms</div>
    </div>
    <div class=""stat"">
      <div class=""stat-lbl"">Ping Rede/USB</div>
      <div id=""rttStat"" class=""stat-val"">-- ms</div>
    </div>
    <div class=""stat"">
      <div class=""stat-lbl"">Formato</div>
      <div id=""fmtStat"" class=""stat-val"">48kHz Estéreo</div>
    </div>
    <div class=""stat"">
      <div class=""stat-lbl"">Resyncs</div>
      <div id=""dropStat"" class=""stat-val"">0</div>
    </div>
  </div>

  <div class=""card"">
    <div class=""ctrl"">
      <div class=""ctrl-head"">
        <span>Buffer Alvo (menor = menos atraso)</span>
        <span id=""bufLbl"" class=""ctrl-val"">60 ms</span>
      </div>
      <input id=""bufSlider"" type=""range"" min=""10"" max=""180"" step=""5"" value=""60"">
      <div class=""presets"">
        <button class=""chip"" data-ms=""10"" type=""button"">Ultra 10ms</button>
        <button class=""chip"" data-ms=""15"" type=""button"">USB 15ms</button>
        <button class=""chip"" data-ms=""60"" type=""button"">Wi-Fi 60ms</button>
      </div>
    </div>
    <div class=""ctrl"">
      <div class=""ctrl-head"">
        <span>Volume no Celular</span>
        <span id=""volLbl"" class=""ctrl-val"">100%</span>
      </div>
      <input id=""volSlider"" type=""range"" min=""0"" max=""150"" step=""5"" value=""100"">
    </div>
    <div class=""row-between"">
      <span style=""font-size:13px;font-weight:600;"">Seguir modo do PC automático</span>
      <button id=""autoModeBtn"" class=""switch-btn on"" type=""button"">Ativo</button>
    </div>
    <div class=""row-between"" style=""margin-top:10px;"">
      <span style=""font-size:13px;font-weight:600;"">Manter tela acesa</span>
      <button id=""wakeBtn"" class=""switch-btn"" type=""button"">Desativado</button>
    </div>
  </div>

  <div class=""card tip"">
    Dica: no <b>Cabo USB</b> (15 ms), use <b>Túnel ADB</b> para ativar o <b>AudioWorklet (2,7 ms)</b> no Chrome. No Wi-Fi, 55–75 ms mantém estabilidade mesmo com oscilação do roteador. Lembre também que o próprio fone Bluetooth adiciona ~100–200 ms dependendo do codec/Modo Jogo do fone.
  </div>
</div>

<script>
(function(){
  var params = new URLSearchParams(location.search);
  var initialMode = params.get('mode') === 'usb' ? 'usb' : 'wifi';

  var playBtn = document.getElementById('playBtn');
  var dot = document.getElementById('dot');
  var statusText = document.getElementById('statusText');
  var deviceText = document.getElementById('deviceText');
  var modeBadge = document.getElementById('modeBadge');
  var meterFill = document.getElementById('meterFill');
  var bufStat = document.getElementById('bufStat');
  var rttStat = document.getElementById('rttStat');
  var fmtStat = document.getElementById('fmtStat');
  var dropStat = document.getElementById('dropStat');
  var bufSlider = document.getElementById('bufSlider');
  var bufLbl = document.getElementById('bufLbl');
  var volSlider = document.getElementById('volSlider');
  var volLbl = document.getElementById('volLbl');
  var autoModeBtn = document.getElementById('autoModeBtn');
  var wakeBtn = document.getElementById('wakeBtn');
  var chips = document.querySelectorAll('.chip');

  var running = false;
  var ws = null;
  var netWorker = null;
  var ctx = null;
  var gainNode = null;
  var silentInGain = null;
  var audioNode = null;
  var workletLoaded = false;
  var workletLoading = false;
  var useWorklet = false;
  var workerPipelineActive = false;
  var engineLabel = 'Ring';
  var dspFallback = null;
  var pendingFrames = [];
  var telemetryTimer = null;
  var keepAliveAudio = null;
  var wakeLock = null;
  var pingTimer = null;
  var wasAudioRunning = false;
  var keepAlivePlaying = false;
  var intentionalPause = false;
  var latestWsGapMax = 0;
  var latestRttMs = 0;
  var lastStatSendTime = 0;

  var sampleRate = 48000;
  var targetBufferMs = initialMode === 'usb' ? 15 : 60;
  var maxBufferMs = initialMode === 'usb' ? 55 : 160;
  var autoFollowPc = true;

  function createRingDspEngine(outRate) {
    var CAP = 16384;
    var MASK = 16383;
    var ringL = new Float32Array(CAP);
    var ringR = new Float32Array(CAP);
    var srcRate = 48000;
    var targetMs = 15;
    var maxMs = 55;
    var pktFrames = 480;
    var writePos = 0;
    var readPos = 0;
    var primed = false;
    var avgAvail = 0;
    var integral = 0;
    var smoothedRate = srcRate / (outRate || 48000);
    var plcMode = false;
    var plcFrames = 0;
    var plcAnchor = 0;
    var plcStep = 1;
    var plcGain = 1.0;
    var fadeLen = 48;
    var fadeRem = 0;
    var prevOutL = 0;
    var prevOutR = 0;
    var resyncCount = 0;
    var plcEvents = 0;
    var plcDelta = 0;
    var minAvailFrames = 999999;
    var smoothLevel = 0;
    var lastBufMs = 0;
    var lastMinBufMs = 0;

    function startFade(len) {
      fadeLen = len > 0 ? len : 1;
      fadeRem = fadeLen;
    }

    function reset() {
      writePos = 0;
      readPos = 0;
      primed = false;
      avgAvail = 0;
      integral = 0;
      smoothedRate = srcRate / (outRate || 48000);
      plcMode = false;
      plcFrames = 0;
      plcStep = 1;
      plcGain = 1.0;
      fadeLen = 48;
      fadeRem = 0;
      prevOutL = 0;
      prevOutR = 0;
      plcDelta = 0;
      minAvailFrames = 999999;
      smoothLevel = 0;
      lastBufMs = 0;
      lastMinBufMs = 0;
    }

    function setConfig(sr, tMs, mMs) {
      if (sr && sr !== srcRate) {
        srcRate = sr;
        smoothedRate = srcRate / (outRate || 48000);
      }
      if (tMs) targetMs = tMs;
      if (mMs) maxMs = mMs;
    }

    function setBufferTargets(tMs, mMs) {
      if (tMs) targetMs = tMs;
      if (mMs) maxMs = mMs;
    }

    function pushInt16Bytes(ab) {
      if (!ab || ab.byteLength <= 8) return;
      var frames = ((ab.byteLength - 8) / 4) | 0;
      if (frames <= 0) return;
      pktFrames = frames;
      var i16 = new Int16Array(ab, 8, frames * 2);
      var peak = 0;
      var w = writePos;
      for (var i = 0; i < frames; i++) {
        var l = i16[i * 2] / 32768;
        var r = i16[i * 2 + 1] / 32768;
        var idx = w & MASK;
        ringL[idx] = l;
        ringR[idx] = r;
        w++;
        var a = l < 0 ? -l : l;
        if (a > peak) peak = a;
      }
      writePos = w;
      smoothLevel = smoothLevel * 0.65 + Math.min(1, peak * 1.4) * 0.35;

      var targetFrames = Math.max(96, Math.round((targetMs * srcRate) / 1000));
      var primeFrames = targetFrames + (pktFrames >> 1);
      if (!primed) {
        if ((writePos - Math.floor(readPos)) >= primeFrames) {
          primed = true;
          readPos = writePos - primeFrames;
          avgAvail = targetFrames;
          minAvailFrames = targetFrames;
          integral = 0;
          smoothedRate = srcRate / (outRate || 48000);
          startFade(48);
          prevOutL = 0;
          prevOutR = 0;
        }
      } else if ((writePos - readPos) > (CAP - 1024)) {
        readPos = writePos - primeFrames;
        startFade(48);
        avgAvail = targetFrames;
        integral = 0;
        resyncCount++;
      }

      if (readPos > 100000000 && writePos > 100000000) {
        var shift = CAP * 1000;
        readPos -= shift;
        writePos -= shift;
        plcAnchor -= shift;
      }
    }

    function hermite(arr, idxInt, f) {
      var y0 = arr[(idxInt - 1) & MASK];
      var y1 = arr[idxInt & MASK];
      var y2 = arr[(idxInt + 1) & MASK];
      var y3 = arr[(idxInt + 2) & MASK];
      var c0 = y1;
      var c1 = 0.5 * (y2 - y0);
      var c2 = y0 - 2.5 * y1 + 2.0 * y2 - 0.5 * y3;
      var c3 = 0.5 * (y3 - y0) + 1.5 * (y1 - y2);
      return ((c3 * f + c2) * f + c1) * f + c0;
    }

    function renderBlock(outL, outR) {
      var n = outL.length;
      if (!primed) {
        for (var z = 0; z < n; z++) {
          outL[z] = 0;
          if (outR !== outL) outR[z] = 0;
        }
        lastBufMs = Math.round((Math.max(0, writePos - readPos) * 1000) / srcRate);
        return;
      }

      var targetFrames = Math.max(96, (targetMs * srcRate) / 1000);
      var maxFrames = Math.max((maxMs * srcRate) / 1000, targetFrames + Math.round(srcRate * 0.030));
      var availNow = writePos - readPos;
      if (availNow > maxFrames) {
        readPos = writePos - (targetFrames + (pktFrames >> 1));
        startFade(48);
        avgAvail = targetFrames;
        integral = 0;
        resyncCount++;
        availNow = targetFrames;
      }

      var avail = Math.max(0, availNow);
      if (avail < minAvailFrames) minAvailFrames = avail;
      var availMid = Math.max(0, avail - n * 0.5);
      avgAvail = avgAvail * 0.94 + availMid * 0.06;
      var dt = n / (outRate || 48000);
      var errSec = (avgAvail - targetFrames) / srcRate;
      integral += errSec * dt;
      if (integral > 0.02) integral = 0.02;
      if (integral < -0.02) integral = -0.02;

      var corr = errSec * 0.45 + integral * 0.12;
      if (corr > 0.014) corr = 0.014;
      if (corr < -0.014) corr = -0.014;

      var targetRate = (srcRate / (outRate || 48000)) * (1.0 + corr);
      smoothedRate = smoothedRate * 0.88 + targetRate * 0.12;

      var maxPlcDecayFrames = Math.round(srcRate * 0.028);
      var maxPlcResetFrames = Math.round(srcRate * 0.085);

      for (var i = 0; i < n; i++) {
        var curAvail = writePos - readPos;
        if (curAvail < minAvailFrames) minAvailFrames = Math.max(0, curAvail);
        if (readPos + 1.0 < writePos) {
          if (plcMode) {
            plcMode = false;
            plcFrames = 0;
            startFade(32);
          }
          var idx = Math.floor(readPos);
          var frac = readPos - idx;
          var sL;
          var sR;
          if (idx + 2 < writePos) {
            sL = hermite(ringL, idx, frac);
            sR = hermite(ringR, idx, frac);
          } else {
            var i0 = idx & MASK;
            var i1 = (idx + 1) & MASK;
            sL = ringL[i0] + (ringL[i1] - ringL[i0]) * frac;
            sR = ringR[i0] + (ringR[i1] - ringR[i0]) * frac;
          }
          if (fadeRem > 0) {
            var t = (fadeLen - fadeRem) / fadeLen;
            var w = 0.5 - 0.5 * Math.cos(Math.PI * t);
            sL = prevOutL * (1.0 - w) + sL * w;
            sR = prevOutR * (1.0 - w) + sR * w;
            fadeRem--;
          }
          outL[i] = sL;
          if (outR !== outL) outR[i] = sR;
          prevOutL = sL;
          prevOutR = sR;
          readPos += smoothedRate;
        } else {
          minAvailFrames = 0;
          if (!plcMode) {
            plcMode = true;
            plcFrames = 0;
            plcAnchor = Math.max(0, writePos - 1);
            plcStep = 1;
            plcGain = 1.0;
            plcEvents++;
            plcDelta++;
          }
          plcFrames++;
          if (plcFrames > maxPlcResetFrames) {
            primed = false;
            plcMode = false;
            readPos = writePos;
            resyncCount++;
            for (var k = i; k < n; k++) {
              prevOutL *= 0.9;
              prevOutR *= 0.9;
              outL[k] = prevOutL;
              if (outR !== outL) outR[k] = prevOutR;
            }
            break;
          } else if (plcFrames > maxPlcDecayFrames) {
            prevOutL *= 0.92;
            prevOutR *= 0.92;
            outL[i] = prevOutL;
            if (outR !== outL) outR[i] = prevOutR;
          } else {
            var mIdx = (plcAnchor - plcStep) & MASK;
            plcStep++;
            if (plcStep >= 160) plcStep = 1;
            plcGain *= 0.993;
            var mL = ringL[mIdx] * plcGain;
            var mR = ringR[mIdx] * plcGain;
            outL[i] = mL;
            if (outR !== outL) outR[i] = mR;
            prevOutL = mL;
            prevOutR = mR;
          }
        }
      }

      lastBufMs = Math.max(0, Math.round(((writePos - readPos) * 1000) / srcRate));
      if (minAvailFrames < 999999) {
        lastMinBufMs = Math.max(0, Math.round((minAvailFrames * 1000) / srcRate));
      } else {
        lastMinBufMs = lastBufMs;
      }
    }

    function getTelemetry() {
      var curDelta = plcDelta;
      plcDelta = 0;
      minAvailFrames = Math.max(0, writePos - readPos);
      return {
        bufMs: lastBufMs,
        minBufMs: lastMinBufMs,
        resyncs: resyncCount,
        plc: plcEvents,
        plcDelta: curDelta,
        level: smoothLevel
      };
    }

    return {
      reset: reset,
      setConfig: setConfig,
      setBufferTargets: setBufferTargets,
      pushInt16Bytes: pushInt16Bytes,
      renderBlock: renderBlock,
      getTelemetry: getTelemetry
    };
  }

  function applyModeBadge(mode) {
    if (mode === 'usb') {
      modeBadge.textContent = 'CABO USB';
      modeBadge.className = 'badge usb';
    } else {
      modeBadge.textContent = 'WI-FI';
      modeBadge.className = 'badge';
    }
  }

  function updateBufferUi(ms, customMax) {
    targetBufferMs = ms;
    maxBufferMs = customMax ? customMax : Math.max(ms * 2 + 25, ms + 40);
    bufSlider.value = String(ms);
    bufLbl.textContent = ms + ' ms';
    for (var i = 0; i < chips.length; i++) {
      var cms = parseInt(chips[i].getAttribute('data-ms'), 10);
      chips[i].className = cms === ms ? 'chip active' : 'chip';
    }
    if (useWorklet && audioNode && audioNode.port) {
      audioNode.port.postMessage({ type: 'buf', targetMs: targetBufferMs, maxMs: maxBufferMs });
    } else if (dspFallback) {
      dspFallback.setBufferTargets(targetBufferMs, maxBufferMs);
    }
  }

  applyModeBadge(initialMode);
  updateBufferUi(targetBufferMs, maxBufferMs);

  function setStatus(state, text, sub) {
    dot.className = 'dot ' + state;
    statusText.textContent = text;
    if (sub !== undefined) deviceText.textContent = sub;
  }

  function buildSilentWavUrl() {
    var sr = 8000, n = 8000;
    var buf = new ArrayBuffer(44 + n * 2);
    var dv = new DataView(buf);
    function wsStr(o, s){ for (var i=0;i<s.length;i++) dv.setUint8(o+i, s.charCodeAt(i)); }
    wsStr(0,'RIFF'); dv.setUint32(4, 36 + n*2, true); wsStr(8,'WAVE');
    wsStr(12,'fmt '); dv.setUint32(16, 16, true); dv.setUint16(20, 1, true);
    dv.setUint16(22, 1, true); dv.setUint32(24, sr, true); dv.setUint32(28, sr*2, true);
    dv.setUint16(32, 2, true); dv.setUint16(34, 16, true); wsStr(36,'data'); dv.setUint32(40, n*2, true);
    var blob = new Blob([buf], { type: 'audio/wav' });
    return URL.createObjectURL(blob);
  }

  function stopKeepAlive() {
    intentionalPause = true;
    keepAlivePlaying = false;
    try { if (keepAliveAudio) keepAliveAudio.pause(); } catch (e) {}
  }

  function updateFormatLabel() {
    fmtStat.textContent = (Math.round(sampleRate / 100) / 10) + 'kHz · ' + engineLabel;
  }

  function sendStatsToPc(t) {
    if (!running || !t) return;
    var now = performance.now();
    var elapsed = lastStatSendTime > 0 ? (now - lastStatSendTime) : 400;
    lastStatSendTime = now;
    var plcPerSec = Math.round(((t.plcDelta || 0) * 1000) / Math.max(120, elapsed));
    var payload = JSON.stringify({
      type: 'stats',
      engine: engineLabel,
      bufMs: t.bufMs,
      minBufMs: t.minBufMs,
      wsGapMaxMs: latestWsGapMax,
      plc: t.plc,
      plcPerSec: plcPerSec,
      resyncs: t.resyncs,
      rttMs: latestRttMs,
      targetMs: targetBufferMs
    });
    if (!workerPipelineActive) latestWsGapMax = 0;
    if (netWorker) {
      try { netWorker.postMessage({ type: 'sendText', text: payload }); } catch (e) {}
    } else if (ws && ws.readyState === 1) {
      try { ws.send(payload); } catch (e) {}
    }
  }

  function applyTelemetry(t) {
    if (!t) return;
    bufStat.textContent = t.bufMs + ' ms';
    dropStat.textContent = String(t.resyncs) + (t.plc > 0 ? ' (PLC ' + t.plc + ')' : '');
    meterFill.style.width = Math.round(Math.min(1, t.level) * 100) + '%';
    sendStatsToPc(t);
  }

  function disconnectSession(state, title, sub) {
    if (!running && !ws && !netWorker) {
      setStatus(state, title, sub);
      return;
    }
    running = false;
    wasAudioRunning = false;
    workerPipelineActive = false;
    pendingFrames = [];
    latestWsGapMax = 0;
    lastStatSendTime = 0;
    if (pingTimer) { clearInterval(pingTimer); pingTimer = null; }
    if (telemetryTimer) { clearInterval(telemetryTimer); telemetryTimer = null; }
    if (netWorker) {
      try { netWorker.postMessage({ type: 'close' }); } catch (e) {}
      try { netWorker.terminate(); } catch (e) {}
      netWorker = null;
    }
    if (ws) {
      try {
        ws.onopen = null;
        ws.onmessage = null;
        ws.onclose = null;
        ws.onerror = null;
        ws.close();
      } catch (e) {}
      ws = null;
    }
    if (useWorklet && audioNode && audioNode.port) {
      try { audioNode.port.postMessage({ type: 'stop' }); } catch (e) {}
    } else if (dspFallback) {
      dspFallback.reset();
    }
    if (ctx && ctx.state === 'running') {
      try { ctx.suspend(); } catch (e) {}
    }
    stopKeepAlive();
    if (wakeLock) {
      try { wakeLock.release(); } catch (e) {}
      wakeLock = null;
      wakeBtn.className = 'switch-btn';
      wakeBtn.textContent = 'Desativado';
    }
    playBtn.textContent = 'Conectar e Ouvir Áudio';
    playBtn.className = 'btn-main';
    meterFill.style.width = '0%';
    bufStat.textContent = '-- ms';
    rttStat.textContent = '-- ms';
    setStatus(state, title, sub);
  }

  function startKeepAlive() {
    try {
      if (!keepAliveAudio) {
        keepAliveAudio = document.createElement('audio');
        keepAliveAudio.src = buildSilentWavUrl();
        keepAliveAudio.loop = true;
        keepAliveAudio.volume = 0.01;
        keepAliveAudio.addEventListener('playing', function() {
          if (running) keepAlivePlaying = true;
        });
        keepAliveAudio.addEventListener('pause', function() {
          if (running && keepAlivePlaying && !intentionalPause) {
            disconnectSession('', 'Desconectado (fone/Bluetooth removido)', 'Não reconecta sozinho. Toque acima quando quiser ouvir o PC novamente');
          }
        });
      }
      intentionalPause = false;
      var p = keepAliveAudio.play();
      if (p && p.catch) p.catch(function(){});
      if ('mediaSession' in navigator) {
        navigator.mediaSession.metadata = new MediaMetadata({
          title: 'Áudio do PC ao Vivo',
          artist: 'Phone Audio Bridge'
        });
      }
    } catch (e) {}
  }

  function initScriptProcessorEngine() {
    if (audioNode && !useWorklet && dspFallback) {
      dspFallback.reset();
      dspFallback.setConfig(sampleRate, targetBufferMs, maxBufferMs);
      startScriptTelemetry();
      flushPendingFrames();
      return;
    }
    dspFallback = createRingDspEngine(ctx.sampleRate);
    dspFallback.setConfig(sampleRate, targetBufferMs, maxBufferMs);
    var sizes = initialMode === 'usb' ? [256, 512, 1024] : [512, 1024];
    var sp = null;
    var chosenSize = 512;
    for (var i = 0; i < sizes.length; i++) {
      try {
        sp = ctx.createScriptProcessor(sizes[i], 1, 2);
        chosenSize = sizes[i];
        break;
      } catch (e) {}
    }
    if (!sp) return;
    if (!silentInGain) {
      silentInGain = ctx.createGain();
      silentInGain.gain.value = 0;
    }
    sp.onaudioprocess = function(ev) {
      var outL = ev.outputBuffer.getChannelData(0);
      var outR = ev.outputBuffer.getChannelData(1);
      if (!running) {
        outL.fill(0);
        outR.fill(0);
        return;
      }
      dspFallback.renderBlock(outL, outR);
    };
    silentInGain.connect(sp);
    sp.connect(gainNode);
    audioNode = sp;
    useWorklet = false;
    engineLabel = 'Ring ' + (Math.round((chosenSize * 10000) / ctx.sampleRate) / 10) + 'ms';
    updateFormatLabel();
    startScriptTelemetry();
    flushPendingFrames();
  }

  function startScriptTelemetry() {
    if (telemetryTimer) clearInterval(telemetryTimer);
    telemetryTimer = setInterval(function() {
      if (running && !useWorklet && dspFallback) {
        applyTelemetry(dspFallback.getTelemetry());
      }
    }, 400);
  }

  function flushPendingFrames() {
    if (!running || pendingFrames.length === 0) return;
    for (var i = 0; i < pendingFrames.length; i++) {
      dispatchAudioFrame(pendingFrames[i]);
    }
    pendingFrames = [];
  }

  function dispatchAudioFrame(ab) {
    if (useWorklet && workletLoaded && audioNode && audioNode.port) {
      audioNode.port.postMessage(ab, [ab]);
    } else if (!useWorklet && dspFallback) {
      dspFallback.pushInt16Bytes(ab);
    } else {
      if (pendingFrames.length < 16) pendingFrames.push(ab);
    }
  }

  function buildWorkletCode() {
    return 'var createRingDspEngine = ' + createRingDspEngine.toString() + ';\n' +
      'class PhoneAudioBridgeWorklet extends AudioWorkletProcessor {\n' +
      '  constructor() {\n' +
      '    super();\n' +
      '    this.dsp = createRingDspEngine(sampleRate);\n' +
      '    this.active = false;\n' +
      '    this.tick = 0;\n' +
      '    this.netPort = null;\n' +
      '    var self = this;\n' +
      '    this.port.onmessage = function(ev) {\n' +
      '      var d = ev.data;\n' +
      '      if (d instanceof ArrayBuffer) {\n' +
      '        if (self.active) self.dsp.pushInt16Bytes(d);\n' +
      '      } else if (d && d.type === \'netPort\' && d.port) {\n' +
      '        if (self.netPort) { try { self.netPort.onmessage = null; self.netPort.close(); } catch (e) {} }\n' +
      '        self.netPort = d.port;\n' +
      '        self.netPort.onmessage = function(nev) {\n' +
      '          if (self.active && nev.data instanceof ArrayBuffer) self.dsp.pushInt16Bytes(nev.data);\n' +
      '        };\n' +
      '      } else if (d && d.type === \'cfg\') {\n' +
      '        self.dsp.setConfig(d.sampleRate, d.targetMs, d.maxMs);\n' +
      '      } else if (d && d.type === \'buf\') {\n' +
      '        self.dsp.setBufferTargets(d.targetMs, d.maxMs);\n' +
      '      } else if (d && d.type === \'start\') {\n' +
      '        self.dsp.reset();\n' +
      '        self.active = true;\n' +
      '      } else if (d && d.type === \'stop\') {\n' +
      '        self.active = false;\n' +
      '        self.dsp.reset();\n' +
      '      }\n' +
      '    };\n' +
      '  }\n' +
      '  process(inputs, outputs) {\n' +
      '    var out = outputs[0];\n' +
      '    if (!out || out.length === 0) return true;\n' +
      '    var outL = out[0];\n' +
      '    var outR = out.length > 1 ? out[1] : null;\n' +
      '    if (!this.active) {\n' +
      '      outL.fill(0);\n' +
      '      if (outR) outR.fill(0);\n' +
      '      return true;\n' +
      '    }\n' +
      '    this.dsp.renderBlock(outL, outR || outL);\n' +
      '    if (++this.tick >= 150) {\n' +
      '      this.tick = 0;\n' +
      '      this.port.postMessage(this.dsp.getTelemetry());\n' +
      '    }\n' +
      '    return true;\n' +
      '  }\n' +
      '}\n' +
      'registerProcessor(\'phone-audio-bridge-proc\', PhoneAudioBridgeWorklet);\n';
  }

  function buildNetWorkerCode() {
    return 'var ws = null;\n' +
      'var audioPort = null;\n' +
      'var loopbackAck = false;\n' +
      'var ackBuf = new Uint8Array([6]);\n' +
      'var pingTimer = null;\n' +
      'var gapTimer = null;\n' +
      'var lastBinMs = 0;\n' +
      'var maxGapMs = 0;\n' +
      'self.onmessage = function(ev) {\n' +
      '  var d = ev.data;\n' +
      '  if (!d) return;\n' +
      '  if (d.type === \'init\') {\n' +
      '    audioPort = d.audioPort;\n' +
      '    loopbackAck = !!d.loopbackAck;\n' +
      '    ws = new WebSocket(d.wsUrl);\n' +
      '    ws.binaryType = \'arraybuffer\';\n' +
      '    ws.onopen = function() {\n' +
      '      self.postMessage({ type: \'open\' });\n' +
      '      pingTimer = setInterval(function() {\n' +
      '        if (ws && ws.readyState === 1) ws.send(JSON.stringify({ type: \'ping\', t: performance.now() }));\n' +
      '      }, 2000);\n' +
      '      gapTimer = setInterval(function() {\n' +
      '        self.postMessage({ type: \'wsGap\', maxGapMs: maxGapMs });\n' +
      '        maxGapMs = 0;\n' +
      '      }, 400);\n' +
      '      ws.send(JSON.stringify({ type: \'ping\', t: performance.now() }));\n' +
      '    };\n' +
      '    ws.onmessage = function(wev) {\n' +
      '      if (typeof wev.data === \'string\') {\n' +
      '        try {\n' +
      '          var msg = JSON.parse(wev.data);\n' +
      '          if (msg.type === \'pong\' && typeof msg.t === \'number\') {\n' +
      '            self.postMessage({ type: \'rtt\', ms: Math.max(1, Math.round(performance.now() - msg.t)) });\n' +
      '          } else if (msg.type === \'config\') {\n' +
      '            self.postMessage({ type: \'config\', msg: msg });\n' +
      '          }\n' +
      '        } catch (e) {}\n' +
      '      } else if (wev.data instanceof ArrayBuffer) {\n' +
      '        var now = performance.now();\n' +
      '        if (lastBinMs > 0) {\n' +
      '          var dt = Math.round(now - lastBinMs);\n' +
      '          if (dt > maxGapMs) maxGapMs = dt;\n' +
      '        }\n' +
      '        lastBinMs = now;\n' +
      '        if (audioPort) audioPort.postMessage(wev.data, [wev.data]);\n' +
      '        if (loopbackAck && ws && ws.readyState === 1) { try { ws.send(ackBuf); } catch (e) {} }\n' +
      '      }\n' +
      '    };\n' +
      '    ws.onclose = function() {\n' +
      '      if (pingTimer) { clearInterval(pingTimer); pingTimer = null; }\n' +
      '      if (gapTimer) { clearInterval(gapTimer); gapTimer = null; }\n' +
      '      self.postMessage({ type: \'close\' });\n' +
      '    };\n' +
      '    ws.onerror = function() {\n' +
      '      self.postMessage({ type: \'error\' });\n' +
      '    };\n' +
      '  } else if (d.type === \'sendText\' && ws && ws.readyState === 1) {\n' +
      '    ws.send(d.text);\n' +
      '  } else if (d.type === \'close\') {\n' +
      '    if (pingTimer) { clearInterval(pingTimer); pingTimer = null; }\n' +
      '    if (gapTimer) { clearInterval(gapTimer); gapTimer = null; }\n' +
      '    if (ws) { try { ws.onclose = null; ws.onerror = null; ws.close(); } catch (e) {} ws = null; }\n' +
      '    if (audioPort) { try { audioPort.close(); } catch (e) {} audioPort = null; }\n' +
      '  }\n' +
      '};\n';
  }

  function handleConfigFromServer(msg) {
    if (!msg) return;
    if (msg.sampleRate) sampleRate = msg.sampleRate;
    updateFormatLabel();
    if (msg.device) deviceText.textContent = 'Fonte PC: ' + msg.device;
    var tr = msg.transport || msg.mode;
    if (tr) applyModeBadge(tr);
    if (autoFollowPc && msg.targetBufferMs) {
      updateBufferUi(msg.targetBufferMs, msg.maxBufferMs);
    }
    if (useWorklet && workletLoaded && audioNode && audioNode.port) {
      audioNode.port.postMessage({ type: 'cfg', sampleRate: sampleRate, targetMs: targetBufferMs, maxMs: maxBufferMs });
    } else if (dspFallback) {
      dspFallback.setConfig(sampleRate, targetBufferMs, maxBufferMs);
    }
  }

  function startWorkerWebSocket() {
    if (!running) return;
    if (netWorker) {
      try { netWorker.terminate(); } catch (e) {}
      netWorker = null;
    }
    var proto = location.protocol === 'https:' ? 'wss://' : 'ws://';
    var wsUrl = proto + location.host + '/ws';
    var isLoopbackHost = (location.hostname === '127.0.0.1' || location.hostname === 'localhost');
    setStatus('wait', 'Conectando ao PC...', 'Abrindo canal Worker + Worklet');
    var ch = new MessageChannel();
    audioNode.port.postMessage({ type: 'netPort', port: ch.port1 }, [ch.port1]);

    var wUrl = URL.createObjectURL(new Blob([buildNetWorkerCode()], { type: 'application/javascript' }));
    netWorker = new Worker(wUrl);
    URL.revokeObjectURL(wUrl);
    workerPipelineActive = true;
    engineLabel = 'Worklet+Worker';
    updateFormatLabel();

    netWorker.onmessage = function(ev) {
      var d = ev.data;
      if (!d || !running) return;
      if (d.type === 'open') {
        setStatus('live', 'Conectado e recebendo áudio', 'Áudio pronto — toque algo no PC para ouvir');
      } else if (d.type === 'rtt') {
        latestRttMs = d.ms;
        rttStat.textContent = d.ms + ' ms';
      } else if (d.type === 'wsGap') {
        latestWsGapMax = d.maxGapMs;
      } else if (d.type === 'config') {
        handleConfigFromServer(d.msg);
      } else if (d.type === 'close') {
        disconnectSession('', 'Conexão com o PC encerrada', 'Não reconecta sozinho. Toque acima quando quiser conectar novamente');
      } else if (d.type === 'error') {
        disconnectSession('err', 'Falha na conexão', 'Verifique se o app está aberto e o firewall liberado');
      }
    };

    netWorker.postMessage({ type: 'init', wsUrl: wsUrl, audioPort: ch.port2, loopbackAck: isLoopbackHost }, [ch.port2]);
  }

  function startSessionPipeline() {
    var AC = window.AudioContext || window.webkitAudioContext;
    if (!ctx) {
      try {
        ctx = new AC({ latencyHint: 'interactive', sampleRate: sampleRate });
      } catch (e) {
        ctx = new AC();
      }
      gainNode = ctx.createGain();
      gainNode.gain.value = parseInt(volSlider.value, 10) / 100;
      gainNode.connect(ctx.destination);
      ctx.onstatechange = function() {
        if (!ctx) return;
        if (ctx.state === 'running') {
          wasAudioRunning = true;
        } else if (running && wasAudioRunning && (ctx.state === 'suspended' || ctx.state === 'interrupted')) {
          disconnectSession('', 'Desconectado (Bluetooth/áudio pausado)', 'Não reconecta sozinho. Toque acima quando quiser ouvir o PC novamente');
        }
      };
    }

    wasAudioRunning = (ctx.state === 'running');
    if (ctx.state !== 'running') {
      try {
        var p = ctx.resume();
        if (p && p.then) {
          p.then(function() {
            if (ctx && ctx.state === 'running') wasAudioRunning = true;
          }).catch(function(){});
        }
      } catch (e) {}
    }

    var canWorklet = !!(window.isSecureContext && ctx.audioWorklet);
    var canWorker = !!(canWorklet && window.Worker && window.MessageChannel);

    if (canWorklet) {
      if (workletLoaded && audioNode) {
        useWorklet = true;
        engineLabel = canWorker ? 'Worklet+Worker' : 'Worklet 2.7ms';
        updateFormatLabel();
        audioNode.port.postMessage({ type: 'cfg', sampleRate: sampleRate, targetMs: targetBufferMs, maxMs: maxBufferMs });
        audioNode.port.postMessage({ type: 'start' });
        if (canWorker) {
          startWorkerWebSocket();
        } else {
          connectWsMainThread();
        }
        return;
      }
      if (!workletLoading) {
        workletLoading = true;
        useWorklet = true;
        engineLabel = canWorker ? 'Worklet+Worker' : 'Worklet 2.7ms';
        updateFormatLabel();
        var blobUrl = URL.createObjectURL(new Blob([buildWorkletCode()], { type: 'application/javascript' }));
        ctx.audioWorklet.addModule(blobUrl).then(function() {
          URL.revokeObjectURL(blobUrl);
          workletLoading = false;
          workletLoaded = true;
          audioNode = new AudioWorkletNode(ctx, 'phone-audio-bridge-proc', { numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [2] });
          audioNode.port.onmessage = function(ev) {
            if (running) applyTelemetry(ev.data);
          };
          audioNode.connect(gainNode);
          audioNode.port.postMessage({ type: 'cfg', sampleRate: sampleRate, targetMs: targetBufferMs, maxMs: maxBufferMs });
          if (running) {
            audioNode.port.postMessage({ type: 'start' });
            if (canWorker) {
              startWorkerWebSocket();
            } else {
              flushPendingFrames();
            }
          }
        }).catch(function() {
          URL.revokeObjectURL(blobUrl);
          workletLoading = false;
          useWorklet = false;
          initScriptProcessorEngine();
          if (running && !ws) connectWsMainThread();
        });
        if (!canWorker) {
          connectWsMainThread();
        }
      }
    } else {
      initScriptProcessorEngine();
      connectWsMainThread();
    }
  }

  function connectWsMainThread() {
    if (!running || workerPipelineActive) return;
    if (ws) {
      try { ws.close(); } catch (e) {}
      ws = null;
    }

    var proto = location.protocol === 'https:' ? 'wss://' : 'ws://';
    var isLoopbackHost = (location.hostname === '127.0.0.1' || location.hostname === 'localhost');
    var ackBuf = new Uint8Array([6]);
    var lastBinMs = 0;
    setStatus('wait', 'Conectando ao PC...', 'Abrindo canal de baixa latência');
    ws = new WebSocket(proto + location.host + '/ws');
    ws.binaryType = 'arraybuffer';

    ws.onopen = function() {
      setStatus('live', 'Conectado e recebendo áudio', 'Áudio pronto — toque algo no PC para ouvir');
      if (pingTimer) clearInterval(pingTimer);
      pingTimer = setInterval(function(){
        if (ws && ws.readyState === 1) {
          ws.send(JSON.stringify({ type: 'ping', t: performance.now() }));
        }
      }, 2000);
      ws.send(JSON.stringify({ type: 'ping', t: performance.now() }));
    };

    ws.onmessage = function(ev) {
      if (typeof ev.data === 'string') {
        try {
          var msg = JSON.parse(ev.data);
          if (msg.type === 'pong' && typeof msg.t === 'number') {
            latestRttMs = Math.max(1, Math.round(performance.now() - msg.t));
            rttStat.textContent = latestRttMs + ' ms';
          } else if (msg.type === 'config') {
            handleConfigFromServer(msg);
          }
        } catch (e) {}
      } else if (ev.data instanceof ArrayBuffer) {
        var now = performance.now();
        if (lastBinMs > 0) {
          var dt = Math.round(now - lastBinMs);
          if (dt > latestWsGapMax) latestWsGapMax = dt;
        }
        lastBinMs = now;
        dispatchAudioFrame(ev.data);
        if (isLoopbackHost && ws && ws.readyState === 1) { try { ws.send(ackBuf); } catch (e) {} }
      }
    };

    ws.onclose = function() {
      if (running) {
        disconnectSession('', 'Conexão com o PC encerrada', 'Não reconecta sozinho. Toque acima quando quiser conectar novamente');
      }
    };

    ws.onerror = function() {
      if (running) {
        disconnectSession('err', 'Falha na conexão', 'Verifique se o app está aberto e o firewall liberado');
      }
    };
  }

  playBtn.addEventListener('click', function() {
    if (!running) {
      running = true;
      pendingFrames = [];
      latestWsGapMax = 0;
      lastStatSendTime = 0;
      dropStat.textContent = '0';
      playBtn.textContent = 'Desconectar';
      playBtn.className = 'btn-main stop';
      startKeepAlive();
      startSessionPipeline();
    } else {
      disconnectSession('', 'Desconectado', 'Toque acima para conectar novamente');
    }
  });

  bufSlider.addEventListener('input', function() {
    autoFollowPc = false;
    autoModeBtn.className = 'switch-btn';
    autoModeBtn.textContent = 'Manual';
    updateBufferUi(parseInt(bufSlider.value, 10));
  });

  for (var i = 0; i < chips.length; i++) {
    chips[i].addEventListener('click', function() {
      autoFollowPc = false;
      autoModeBtn.className = 'switch-btn';
      autoModeBtn.textContent = 'Manual';
      updateBufferUi(parseInt(this.getAttribute('data-ms'), 10));
    });
  }

  volSlider.addEventListener('input', function() {
    var v = parseInt(volSlider.value, 10);
    volLbl.textContent = v + '%';
    if (gainNode) gainNode.gain.value = v / 100;
  });

  autoModeBtn.addEventListener('click', function() {
    autoFollowPc = !autoFollowPc;
    autoModeBtn.className = autoFollowPc ? 'switch-btn on' : 'switch-btn';
    autoModeBtn.textContent = autoFollowPc ? 'Ativo' : 'Manual';
  });

  wakeBtn.addEventListener('click', async function() {
    if (!('wakeLock' in navigator)) {
      wakeBtn.textContent = 'Indisponível';
      return;
    }
    if (wakeLock) {
      try { await wakeLock.release(); } catch (e) {}
      wakeLock = null;
      wakeBtn.className = 'switch-btn';
      wakeBtn.textContent = 'Desativado';
    } else {
      try {
        wakeLock = await navigator.wakeLock.request('screen');
        wakeBtn.className = 'switch-btn on';
        wakeBtn.textContent = 'Ativo';
        wakeLock.addEventListener('release', function(){
          wakeLock = null;
          wakeBtn.className = 'switch-btn';
          wakeBtn.textContent = 'Desativado';
        });
      } catch (e) {
        wakeBtn.textContent = 'Bloqueado';
      }
    }
  });
})();
</script>
</body>
</html>";
        }
    }

    #endregion

    #region HTTP & WebSocket Server (RFC 6455)

    internal sealed class WsClientSession : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly Queue<byte[]> _sendQueue = new Queue<byte[]>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly object _queueLock = new object();
        private readonly Action<WsClientSession> _onDisconnect;
        private volatile bool _closed;
        private int _maxQueueSize = 24;

        public readonly string LocalIp;
        public readonly bool IsUsbTransport;
        public volatile string LatestStatsJson = "";
        public int LatestStatsTick;

        public WsClientSession(TcpClient tcp, NetworkStream stream, string localIp, bool isUsbTransport, int maxQueueSize, Action<WsClientSession> onDisconnect)
        {
            _tcp = tcp;
            _stream = stream;
            LocalIp = localIp ?? "";
            IsUsbTransport = isUsbTransport;
            _maxQueueSize = maxQueueSize;
            _onDisconnect = onDisconnect;
        }

        public void SetMaxQueueSize(int maxQueueSize)
        {
            _maxQueueSize = maxQueueSize < 4 ? 4 : maxQueueSize;
        }

        public void StartLoops()
        {
            var writer = new Thread(WriterLoop);
            writer.IsBackground = true;
            writer.Priority = ThreadPriority.Highest;
            writer.Name = "WS-Writer";
            writer.Start();

            var reader = new Thread(ReaderLoop);
            reader.IsBackground = true;
            reader.Name = "WS-Reader";
            reader.Start();
        }

        public void EnqueueText(string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            byte[] frame = BuildFrame(0x81, payload);
            lock (_queueLock)
            {
                if (_closed) return;
                _sendQueue.Enqueue(frame);
            }
            _signal.Set();
        }

        public void EnqueuePrebuiltAudioFrame(byte[] wsFrame)
        {
            lock (_queueLock)
            {
                if (_closed) return;
                while (_sendQueue.Count >= _maxQueueSize)
                {
                    _sendQueue.Dequeue();
                }
                _sendQueue.Enqueue(wsFrame);
            }
            _signal.Set();
        }

        private void EnqueueControl(byte firstByte, byte[] payload)
        {
            byte[] frame = BuildFrame(firstByte, payload ?? new byte[0]);
            lock (_queueLock)
            {
                if (_closed) return;
                _sendQueue.Enqueue(frame);
            }
            _signal.Set();
        }

        private static byte[] BuildFrame(byte firstByte, byte[] payload)
        {
            int len = payload.Length;
            int hdrLen;
            if (len <= 125) hdrLen = 2;
            else if (len <= 65535) hdrLen = 4;
            else hdrLen = 10;

            byte[] frame = new byte[hdrLen + len];
            frame[0] = firstByte;
            if (len <= 125)
            {
                frame[1] = (byte)len;
            }
            else if (len <= 65535)
            {
                frame[1] = 126;
                frame[2] = (byte)((len >> 8) & 0xFF);
                frame[3] = (byte)(len & 0xFF);
            }
            else
            {
                frame[1] = 127;
                long l64 = len;
                for (int i = 0; i < 8; i++)
                {
                    frame[2 + i] = (byte)((l64 >> ((7 - i) * 8)) & 0xFF);
                }
            }
            if (len > 0)
            {
                Buffer.BlockCopy(payload, 0, frame, hdrLen, len);
            }
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
            }
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                {
                    try { NativeWin32.AvRevertMmThreadCharacteristics(mmcssHandle); } catch { }
                }
                Dispose();
            }
        }

        private void ReaderLoop()
        {
            byte[] hdr2 = new byte[2];
            byte[] extLenBuf = new byte[8];
            byte[] maskBuf = new byte[4];
            byte[] smallBinBuf = new byte[16];
            try
            {
                while (!_closed)
                {
                    if (!ReadExactInto(_stream, hdr2, 2)) break;

                    int opcode = hdr2[0] & 0x0F;
                    bool masked = (hdr2[1] & 0x80) != 0;
                    long len = hdr2[1] & 0x7F;

                    if (len == 126)
                    {
                        if (!ReadExactInto(_stream, extLenBuf, 2)) break;
                        len = (extLenBuf[0] << 8) | extLenBuf[1];
                    }
                    else if (len == 127)
                    {
                        if (!ReadExactInto(_stream, extLenBuf, 8)) break;
                        len = 0;
                        for (int i = 0; i < 8; i++) len = (len << 8) | extLenBuf[i];
                    }

                    if (masked)
                    {
                        if (!ReadExactInto(_stream, maskBuf, 4)) break;
                    }

                    if (len > 1048576 || len < 0) break;

                    if (opcode == 0x2 && len <= smallBinBuf.Length)
                    {
                        if (len > 0 && !ReadExactInto(_stream, smallBinBuf, (int)len)) break;
                        continue;
                    }

                    byte[] payload = len > 0 ? new byte[(int)len] : new byte[0];
                    if (len > 0 && !ReadExactInto(_stream, payload, (int)len)) break;

                    if (masked)
                    {
                        for (int i = 0; i < payload.Length; i++)
                        {
                            payload[i] ^= maskBuf[i & 3];
                        }
                    }

                    if (opcode == 0x8)
                    {
                        EnqueueControl(0x88, new byte[0]);
                        break;
                    }
                    if (opcode == 0x9)
                    {
                        EnqueueControl(0x8A, payload);
                    }
                    else if (opcode == 0x1)
                    {
                        string msg = Encoding.UTF8.GetString(payload);
                        if (msg.Contains("\"ping\""))
                        {
                            string pong = msg.Replace("\"ping\"", "\"pong\"");
                            EnqueueText(pong);
                        }
                        else if (msg.Contains("\"stats\""))
                        {
                            LatestStatsJson = msg;
                            LatestStatsTick = Environment.TickCount;
                        }
                    }
                }
            }
            catch
            {
            }
            finally
            {
                Dispose();
            }
        }

        private static bool ReadExactInto(Stream s, byte[] buf, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            try { _signal.Set(); } catch { }
            try { _stream.Close(); } catch { }
            try { _tcp.Close(); } catch { }
            if (_onDisconnect != null)
            {
                _onDisconnect(this);
            }
        }
    }

    internal sealed class AudioWebServer : IDisposable
    {
        private readonly int _port;
        private readonly List<WsClientSession> _clients = new List<WsClientSession>();
        private readonly object _clientsLock = new object();
        private readonly HashSet<string> _usbLocalIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _usbIpLock = new object();
        private readonly byte[] _htmlBytes;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private uint _seqCounter;

        public volatile string Mode = "wifi";
        public volatile int SampleRate = 48000;
        public volatile int ChunkMs = 20;
        public volatile int TargetBufferMs = 60;
        public volatile int MaxBufferMs = 160;
        public volatile string DeviceName = "Padrão do Windows";
        public volatile bool UsbDetected;
        public volatile bool AdbActive;
        public volatile string LastError = "";

        public int ListeningPort
        {
            get
            {
                if (_listener != null && _listener.LocalEndpoint is IPEndPoint)
                {
                    return ((IPEndPoint)_listener.LocalEndpoint).Port;
                }
                return _port;
            }
        }

        public int ClientCount
        {
            get
            {
                lock (_clientsLock)
                {
                    return _clients.Count;
                }
            }
        }

        public string ConnectedTransportSummary
        {
            get
            {
                bool hasUsb = false;
                bool hasWifi = false;
                lock (_clientsLock)
                {
                    for (int i = 0; i < _clients.Count; i++)
                    {
                        if (_clients[i].IsUsbTransport) hasUsb = true;
                        else hasWifi = true;
                    }
                }
                if (hasUsb && hasWifi) return "both";
                if (hasUsb) return "usb";
                if (hasWifi) return "wifi";
                return "";
            }
        }

        public AudioWebServer(int port)
        {
            _port = port;
            _htmlBytes = Encoding.UTF8.GetBytes(MobilePlayerHtml.BuildPage());
        }

        public void UpdateUsbIps(HashSet<string> ips)
        {
            lock (_usbIpLock)
            {
                _usbLocalIps.Clear();
                if (ips != null)
                {
                    foreach (string ip in ips)
                    {
                        if (!string.IsNullOrEmpty(ip))
                        {
                            _usbLocalIps.Add(ip);
                        }
                    }
                }
            }
        }

        private bool IsUsbLocalEndpoint(string localIp)
        {
            if (string.IsNullOrEmpty(localIp)) return false;
            if (string.Equals(localIp, "127.0.0.1", StringComparison.Ordinal)) return true;
            lock (_usbIpLock)
            {
                return _usbLocalIps.Contains(localIp);
            }
        }

        private static void GetClientTransportProfile(bool isUsbTransport, out string transport, out int targetMs, out int maxMs, out int qLimit)
        {
            if (isUsbTransport)
            {
                transport = "usb";
                targetMs = 15;
                maxMs = 55;
                qLimit = 24;
            }
            else
            {
                transport = "wifi";
                targetMs = 60;
                maxMs = 160;
                qLimit = 24;
            }
        }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "AudioBridge-HTTP-WS";
            _acceptThread.Start();
        }

        public void UpdateProfile(BridgeMode mode, int sampleRate, string deviceName)
        {
            bool isUsb = (mode == BridgeMode.Usb);
            Mode = isUsb ? "usb" : "wifi";
            SampleRate = sampleRate > 0 ? sampleRate : 48000;
            ChunkMs = isUsb ? 10 : 20;
            TargetBufferMs = isUsb ? 15 : 60;
            MaxBufferMs = isUsb ? 55 : 160;
            DeviceName = string.IsNullOrEmpty(deviceName) ? "Padrão do Windows" : deviceName;

            lock (_clientsLock)
            {
                for (int i = 0; i < _clients.Count; i++)
                {
                    string tr;
                    int tMs;
                    int mMs;
                    int qLim;
                    GetClientTransportProfile(_clients[i].IsUsbTransport, out tr, out tMs, out mMs, out qLim);
                    _clients[i].SetMaxQueueSize(qLim);
                    _clients[i].EnqueueText(BuildConfigJson(_clients[i].IsUsbTransport));
                }
            }
        }

        public void BroadcastPcm(byte[] pcm16Stereo)
        {
            lock (_clientsLock)
            {
                if (_clients.Count == 0) return;

                uint seq = ++_seqCounter;
                uint tick = unchecked((uint)Environment.TickCount);
                int payloadLen = 8 + pcm16Stereo.Length;
                int wsHdrLen = payloadLen <= 125 ? 2 : (payloadLen <= 65535 ? 4 : 10);
                byte[] wsFrame = new byte[wsHdrLen + payloadLen];
                wsFrame[0] = 0x82;
                if (payloadLen <= 125)
                {
                    wsFrame[1] = (byte)payloadLen;
                }
                else if (payloadLen <= 65535)
                {
                    wsFrame[1] = 126;
                    wsFrame[2] = (byte)((payloadLen >> 8) & 0xFF);
                    wsFrame[3] = (byte)(payloadLen & 0xFF);
                }
                else
                {
                    wsFrame[1] = 127;
                    long l64 = payloadLen;
                    for (int k = 0; k < 8; k++)
                    {
                        wsFrame[2 + k] = (byte)((l64 >> ((7 - k) * 8)) & 0xFF);
                    }
                }

                wsFrame[wsHdrLen] = (byte)(seq & 0xFF);
                wsFrame[wsHdrLen + 1] = (byte)((seq >> 8) & 0xFF);
                wsFrame[wsHdrLen + 2] = (byte)((seq >> 16) & 0xFF);
                wsFrame[wsHdrLen + 3] = (byte)((seq >> 24) & 0xFF);
                wsFrame[wsHdrLen + 4] = (byte)(tick & 0xFF);
                wsFrame[wsHdrLen + 5] = (byte)((tick >> 8) & 0xFF);
                wsFrame[wsHdrLen + 6] = (byte)((tick >> 16) & 0xFF);
                wsFrame[wsHdrLen + 7] = (byte)((tick >> 24) & 0xFF);
                Buffer.BlockCopy(pcm16Stereo, 0, wsFrame, wsHdrLen + 8, pcm16Stereo.Length);

                for (int i = 0; i < _clients.Count; i++)
                {
                    _clients[i].EnqueuePrebuiltAudioFrame(wsFrame);
                }
            }
        }

        private string BuildConfigJson(bool isUsbTransport)
        {
            string tr;
            int targetMs;
            int maxMs;
            int qLim;
            GetClientTransportProfile(isUsbTransport, out tr, out targetMs, out maxMs, out qLim);
            return string.Format(
                "{{\"type\":\"config\",\"sampleRate\":{0},\"channels\":2,\"bits\":16,\"mode\":\"{1}\",\"transport\":\"{2}\",\"chunkMs\":{3},\"targetBufferMs\":{4},\"maxBufferMs\":{5},\"device\":\"{6}\"}}",
                SampleRate,
                Mode,
                tr,
                ChunkMs,
                targetMs,
                maxMs,
                EscapeJson(DeviceName));
        }

        public string GetLiveClientStatsSummary(out bool hasGlitches)
        {
            hasGlitches = false;
            string latestJson = "";
            int clientTotal = 0;
            lock (_clientsLock)
            {
                clientTotal = _clients.Count;
                for (int i = 0; i < _clients.Count; i++)
                {
                    if (!string.IsNullOrEmpty(_clients[i].LatestStatsJson))
                    {
                        latestJson = _clients[i].LatestStatsJson;
                        break;
                    }
                }
            }

            if (clientTotal == 0)
            {
                return "Telemetria ao vivo: aguardando conexão do celular...";
            }
            if (string.IsNullOrEmpty(latestJson))
            {
                return "Telemetria ao vivo: conectado (aguardando métricas do player...)";
            }

            string engine = ExtractJsonStr(latestJson, "engine");
            int bufMs = ExtractJsonInt(latestJson, "bufMs");
            int minBufMs = ExtractJsonInt(latestJson, "minBufMs");
            int gapMs = ExtractJsonInt(latestJson, "wsGapMaxMs");
            int plcPerSec = ExtractJsonInt(latestJson, "plcPerSec");
            int resyncs = ExtractJsonInt(latestJson, "resyncs");
            int rttMs = ExtractJsonInt(latestJson, "rttMs");
            if (plcPerSec > 0) hasGlitches = true;

            return string.Format(
                "{0} | Buf {1}ms (mín {2}ms) | Gap {3}ms | Cortes/s: {4} | Resync: {5} | Ping {6}ms",
                string.IsNullOrEmpty(engine) ? "Player" : engine,
                bufMs,
                minBufMs,
                gapMs,
                plcPerSec,
                resyncs,
                rttMs);
        }

        private static string ExtractJsonStr(string json, string key)
        {
            string pat = "\"" + key + "\":\"";
            int idx = json.IndexOf(pat, StringComparison.Ordinal);
            if (idx < 0) return "";
            int start = idx + pat.Length;
            int end = json.IndexOf('"', start);
            if (end <= start) return "";
            return json.Substring(start, end - start);
        }

        private static int ExtractJsonInt(string json, string key)
        {
            string pat = "\"" + key + "\":";
            int idx = json.IndexOf(pat, StringComparison.Ordinal);
            if (idx < 0) return 0;
            int i = idx + pat.Length;
            while (i < json.Length && json[i] == ' ') i++;
            int start = i;
            if (i < json.Length && json[i] == '-') i++;
            while (i < json.Length && char.IsDigit(json[i])) i++;
            if (i <= start) return 0;
            int val;
            if (int.TryParse(json.Substring(start, i - start), out val)) return val;
            return 0;
        }

        private string BuildInfoJson()
        {
            var usbListSb = new StringBuilder();
            lock (_usbIpLock)
            {
                bool first = true;
                foreach (string ip in _usbLocalIps)
                {
                    if (!first) usbListSb.Append(",");
                    usbListSb.Append("\"").Append(EscapeJson(ip)).Append("\"");
                    first = false;
                }
            }

            var statsSb = new StringBuilder();
            lock (_clientsLock)
            {
                bool first = true;
                for (int i = 0; i < _clients.Count; i++)
                {
                    string s = (_clients[i].LatestStatsJson ?? "").Trim();
                    if (s.StartsWith("{", StringComparison.Ordinal) && s.EndsWith("}", StringComparison.Ordinal))
                    {
                        if (!first) statsSb.Append(",");
                        statsSb.Append(s);
                        first = false;
                    }
                }
            }

            return string.Format(
                "{{\"mode\":\"{0}\",\"sampleRate\":{1},\"channels\":2,\"bits\":16,\"chunkMs\":{2},\"targetBufferMs\":{3},\"maxBufferMs\":{4},\"device\":\"{5}\",\"clients\":{6},\"clientTransports\":\"{7}\",\"usbDetected\":{8},\"adbActive\":{9},\"usbIps\":[{10}],\"clientStats\":[{11}]}}",
                Mode,
                SampleRate,
                ChunkMs,
                TargetBufferMs,
                MaxBufferMs,
                EscapeJson(DeviceName),
                ClientCount,
                ConnectedTransportSummary,
                UsbDetected ? "true" : "false",
                AdbActive ? "true" : "false",
                usbListSb.ToString(),
                statsSb.ToString());
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate(object state)
                    {
                        HandleClient((TcpClient)state);
                    }, client);
                }
                catch (SocketException)
                {
                    if (!_running) break;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            bool keepOpen = false;
            try
            {
                client.NoDelay = true;
                client.SendBufferSize = 65536;
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                NetworkStream stream = client.GetStream();

                string headerText = ReadHttpHeaders(stream);
                if (string.IsNullOrEmpty(headerText))
                {
                    return;
                }

                string[] lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0) return;

                string[] reqLine = lines[0].Split(' ');
                if (reqLine.Length < 2) return;
                string rawPath = reqLine[1];
                int qIdx = rawPath.IndexOf('?');
                string path = qIdx >= 0 ? rawPath.Substring(0, qIdx) : rawPath;

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon > 0)
                    {
                        string k = lines[i].Substring(0, colon).Trim();
                        string v = lines[i].Substring(colon + 1).Trim();
                        headers[k] = v;
                    }
                }

                if (string.Equals(path, "/ws", StringComparison.OrdinalIgnoreCase) &&
                    headers.ContainsKey("Sec-WebSocket-Key"))
                {
                    string wsKey = headers["Sec-WebSocket-Key"];
                    string accept = ComputeWebSocketAccept(wsKey);
                    string upgradeResp =
                        "HTTP/1.1 101 Switching Protocols\r\n" +
                        "Upgrade: websocket\r\n" +
                        "Connection: Upgrade\r\n" +
                        "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
                    byte[] respBytes = Encoding.ASCII.GetBytes(upgradeResp);
                    stream.Write(respBytes, 0, respBytes.Length);
                    stream.Flush();

                    client.ReceiveTimeout = 0;
                    client.SendTimeout = 3000;

                    string localIp = "";
                    IPEndPoint lep = client.Client.LocalEndPoint as IPEndPoint;
                    if (lep != null) localIp = lep.Address.ToString();
                    bool isUsbClient = IsUsbLocalEndpoint(localIp);

                    string tr;
                    int tMs;
                    int mMs;
                    int qLimit;
                    GetClientTransportProfile(isUsbClient, out tr, out tMs, out mMs, out qLimit);

                    var session = new WsClientSession(client, stream, localIp, isUsbClient, qLimit, OnSessionClosed);
                    lock (_clientsLock)
                    {
                        _clients.Add(session);
                    }
                    session.StartLoops();
                    session.EnqueueText(BuildConfigJson(isUsbClient));
                    keepOpen = true;
                    return;
                }

                if (string.Equals(path, "/api/info", StringComparison.OrdinalIgnoreCase))
                {
                    WriteHttpResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(BuildInfoJson()));
                    return;
                }

                if (string.Equals(path, "/favicon.ico", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] emptyResp = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
                    stream.Write(emptyResp, 0, emptyResp.Length);
                    return;
                }

                WriteHttpResponse(stream, "200 OK", "text/html; charset=utf-8", _htmlBytes);
            }
            catch
            {
            }
            finally
            {
                if (!keepOpen)
                {
                    try { client.Close(); } catch { }
                }
            }
        }

        private void OnSessionClosed(WsClientSession session)
        {
            lock (_clientsLock)
            {
                _clients.Remove(session);
            }
        }

        private static string ReadHttpHeaders(NetworkStream stream)
        {
            byte[] buf = new byte[8192];
            int total = 0;
            while (total < buf.Length)
            {
                int b = stream.ReadByte();
                if (b < 0) break;
                buf[total++] = (byte)b;
                if (total >= 4 &&
                    buf[total - 4] == 13 &&
                    buf[total - 3] == 10 &&
                    buf[total - 2] == 13 &&
                    buf[total - 1] == 10)
                {
                    return Encoding.ASCII.GetString(buf, 0, total);
                }
            }
            return "";
        }

        private static void WriteHttpResponse(NetworkStream stream, string status, string contentType, byte[] body)
        {
            string hdr = string.Format(
                "HTTP/1.1 {0}\r\nContent-Type: {1}\r\nContent-Length: {2}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n",
                status,
                contentType,
                body.Length);
            byte[] hdrBytes = Encoding.ASCII.GetBytes(hdr);
            stream.Write(hdrBytes, 0, hdrBytes.Length);
            if (body.Length > 0)
            {
                stream.Write(body, 0, body.Length);
            }
            stream.Flush();
        }

        private static string ComputeWebSocketAccept(string key)
        {
            string concat = key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(concat));
                return Convert.ToBase64String(hash);
            }
        }

        public void Dispose()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            lock (_clientsLock)
            {
                var copy = new List<WsClientSession>(_clients);
                _clients.Clear();
                for (int i = 0; i < copy.Count; i++)
                {
                    copy[i].Dispose();
                }
            }
        }
    }

    #endregion

    #region Windows Desktop UI & System Tray

    internal sealed class MainBridgeForm : Form
    {
        private const int DefaultPort = 8765;
        private const string RunRegPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppRunName = "PhoneAudioBridge";

        private readonly WasapiLoopbackCapture _capture;
        private readonly AudioWebServer _server;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly ToolStripMenuItem _miStartup;
        private readonly ToolStripMenuItem _miModeWifi;
        private readonly ToolStripMenuItem _miModeUsb;

        private BridgeMode _mode = BridgeMode.Wifi;
        private bool _adbActive;
        private string _adbMessage = "";
        private string _currentUrl = "";
        private Bitmap _currentQrBmp;
        private bool _forceExit;
        private int _netTickDivider;
        private bool _updatingCombos;

        private Label _lblStatusBadge;
        private Button _btnWifi;
        private Button _btnUsb;
        private ComboBox _cmbAudio;
        private ComboBox _cmbNetwork;
        private Panel _vuMeterHost;
        private Panel _vuMeterBar;
        private Panel _usbGuideCard;
        private Label _lblUsbStatus;
        private Label _lblUsbSteps;
        private Button _btnAdbTunnel;
        private Button _btnFirewall;
        private Button _btnCopyUrl;
        private PictureBox _picQr;
        private TextBox _txtUrl;
        private Label _lblQrHint;
        private Label _lblLiveDiag;
        private System.Windows.Forms.Timer _uiTimer;

        public MainBridgeForm(BridgeMode initialMode)
        {
            _capture = new WasapiLoopbackCapture();
            _server = new AudioWebServer(DefaultPort);

            _capture.OnPcmPacket = delegate(byte[] pcm, int rate)
            {
                _server.BroadcastPcm(pcm);
            };
            _capture.OnFormatChanged = delegate(int rate, string devName)
            {
                _server.UpdateProfile(_mode, rate, devName);
            };

            BuildUi();

            _trayMenu = new ContextMenuStrip();
            var miOpen = new ToolStripMenuItem("Abrir janela", null, delegate { ShowFromTray(); });
            miOpen.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _miModeWifi = new ToolStripMenuItem("Modo Wi-Fi / Rede Local", null, delegate { SetMode(BridgeMode.Wifi); });
            _miModeUsb = new ToolStripMenuItem("Modo Cabo USB (Menor Delay)", null, delegate { SetMode(BridgeMode.Usb); });
            _miStartup = new ToolStripMenuItem("Iniciar com o Windows", null, delegate { ToggleStartup(); });
            _miStartup.Checked = IsStartupEnabled();
            var miExit = new ToolStripMenuItem("Sair", null, delegate { ExitCleanly(); });

            _trayMenu.Items.Add(miOpen);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(_miModeWifi);
            _trayMenu.Items.Add(_miModeUsb);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(_miStartup);
            _trayMenu.Items.Add(miExit);

            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "Phone Audio Bridge";
            _trayIcon.Icon = CreateTrayIcon();
            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += delegate { ShowFromTray(); };

            RefreshAudioDevices();
            RefreshAdbAndNetwork(true);
            SetMode(initialMode);

            _server.Start();
            _capture.Start();
            UpdateFirewallButtonState();

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 120;
            _uiTimer.Tick += OnUiTick;
            _uiTimer.Start();
        }

        private void BuildUi()
        {
            Text = "Phone Audio Bridge — Áudio do PC no Fone via Celular";
            ClientSize = new Size(780, 594);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(18, 21, 28);
            ForeColor = Color.FromArgb(241, 245, 249);
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Icon = CreateTrayIcon();

            // Header
            var lblTitle = new Label
            {
                Text = "Phone Audio Bridge",
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(22, 16),
                AutoSize = true
            };
            var lblSub = new Label
            {
                Text = "Conecte o fone Bluetooth no celular e escaneie o QR Code para ouvir o áudio do PC",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(24, 46),
                AutoSize = true
            };
            _lblStatusBadge = new Label
            {
                Text = "Aguardando celular...",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(56, 189, 248),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(548, 20),
                Size = new Size(210, 30)
            };
            Controls.Add(lblTitle);
            Controls.Add(lblSub);
            Controls.Add(_lblStatusBadge);

            // Left panel controls
            int lx = 22;
            int lw = 408;

            var lblMode = MakeSectionLabel("1. MODO DE CONEXÃO", lx, 78);
            Controls.Add(lblMode);

            _btnWifi = new Button
            {
                Text = "Wi-Fi / Rede Local\r\nSem cabos (~60 ms)",
                Location = new Point(lx, 100),
                Size = new Size(198, 52),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnWifi.FlatAppearance.BorderSize = 2;
            _btnWifi.Click += delegate { SetMode(BridgeMode.Wifi); };

            _btnUsb = new Button
            {
                Text = "Cabo USB (Menor Delay)\r\nUltra-baixo (10–15 ms)",
                Location = new Point(lx + 210, 100),
                Size = new Size(198, 52),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnUsb.FlatAppearance.BorderSize = 2;
            _btnUsb.Click += delegate { SetMode(BridgeMode.Usb); };

            Controls.Add(_btnWifi);
            Controls.Add(_btnUsb);

            // Audio source
            Controls.Add(MakeSectionLabel("2. FONTE DE ÁUDIO DO WINDOWS", lx, 166));
            _cmbAudio = new ComboBox
            {
                Location = new Point(lx, 188),
                Size = new Size(322, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(27, 32, 44),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _cmbAudio.SelectedIndexChanged += delegate
            {
                if (_updatingCombos) return;
                AudioDeviceInfo sel = _cmbAudio.SelectedItem as AudioDeviceInfo;
                if (sel != null)
                {
                    _capture.SelectDevice(sel.Id);
                }
            };
            var btnRefreshAudio = MakeSmallButton("Atualizar", lx + 330, 187, 78, 28);
            btnRefreshAudio.Click += delegate { RefreshAudioDevices(); };

            _vuMeterHost = new Panel
            {
                Location = new Point(lx, 222),
                Size = new Size(lw, 8),
                BackColor = Color.FromArgb(12, 15, 22)
            };
            _vuMeterBar = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(0, 8),
                BackColor = Color.FromArgb(34, 197, 94)
            };
            _vuMeterHost.Controls.Add(_vuMeterBar);

            var lblAudioTip = new Label
            {
                Text = "Dica: se não quiser som saindo no PC ao mesmo tempo, escolha uma saída silenciosa (ex.: monitor sem caixa).",
                Location = new Point(lx, 234),
                Size = new Size(lw, 32),
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(148, 163, 184)
            };
            Controls.Add(_cmbAudio);
            Controls.Add(btnRefreshAudio);
            Controls.Add(_vuMeterHost);
            Controls.Add(lblAudioTip);

            // Network interface
            Controls.Add(MakeSectionLabel("3. REDE / INTERFACE PARA O QR CODE", lx, 274));
            _cmbNetwork = new ComboBox
            {
                Location = new Point(lx, 296),
                Size = new Size(322, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(27, 32, 44),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _cmbNetwork.SelectedIndexChanged += delegate
            {
                if (_updatingCombos) return;
                RegenerateQr();
            };
            var btnRefreshNet = MakeSmallButton("Atualizar", lx + 330, 295, 78, 28);
            btnRefreshNet.Click += delegate { RefreshAdbAndNetwork(true); };
            Controls.Add(_cmbNetwork);
            Controls.Add(btnRefreshNet);

            // USB guide card
            _usbGuideCard = new Panel
            {
                Location = new Point(lx, 336),
                Size = new Size(lw, 148),
                BackColor = Color.FromArgb(25, 31, 43)
            };
            _lblUsbStatus = new Label
            {
                Location = new Point(12, 10),
                Size = new Size(384, 22),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            };
            _lblUsbSteps = new Label
            {
                Location = new Point(12, 34),
                Size = new Size(384, 72),
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(203, 213, 225)
            };
            _btnAdbTunnel = MakeSmallButton("Tentar Túnel ADB", 12, 110, 146, 28);
            _btnAdbTunnel.Click += delegate
            {
                RefreshAdbAndNetwork(true);
                MessageBox.Show(this, _adbMessage, "Túnel ADB (Cabo USB)", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            _usbGuideCard.Controls.Add(_lblUsbStatus);
            _usbGuideCard.Controls.Add(_lblUsbSteps);
            _usbGuideCard.Controls.Add(_btnAdbTunnel);
            Controls.Add(_usbGuideCard);

            _lblLiveDiag = new Label
            {
                Text = "Telemetria ao vivo: aguardando conexão do celular...",
                Location = new Point(lx, 492),
                Size = new Size(lw, 34),
                BackColor = Color.FromArgb(15, 19, 26),
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Consolas", 8f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 8, 0)
            };
            Controls.Add(_lblLiveDiag);

            // Bottom utility buttons
            _btnFirewall = MakeSmallButton("Liberar Firewall (Admin)", lx, 536, 198, 36);
            _btnFirewall.Click += OnFirewallClick;

            var btnHideTray = MakeSmallButton("Ocultar na Bandeja (^)", lx + 210, 536, 198, 36);
            btnHideTray.Click += delegate { HideToTray(); };

            Controls.Add(_btnFirewall);
            Controls.Add(btnHideTray);

            // Right column: QR Code Card
            var qrCard = new Panel
            {
                Location = new Point(450, 78),
                Size = new Size(308, 494),
                BackColor = Color.FromArgb(25, 31, 43)
            };
            var lblQrTitle = new Label
            {
                Text = "ESCANEIE COM A CÂMERA DO CELULAR",
                Location = new Point(14, 14),
                Size = new Size(280, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184)
            };
            _picQr = new PictureBox
            {
                Location = new Point(24, 42),
                Size = new Size(260, 260),
                BackColor = Color.White,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            _txtUrl = new TextBox
            {
                Location = new Point(18, 316),
                Size = new Size(272, 26),
                ReadOnly = true,
                TextAlign = HorizontalAlignment.Center,
                BackColor = Color.FromArgb(15, 19, 26),
                ForeColor = Color.FromArgb(56, 189, 248),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9.5f, FontStyle.Bold)
            };
            _btnCopyUrl = MakeSmallButton("Copiar Link", 18, 350, 272, 32);
            _btnCopyUrl.Click += delegate
            {
                if (!string.IsNullOrEmpty(_currentUrl))
                {
                    Clipboard.SetText(_currentUrl);
                    _btnCopyUrl.Text = "Link copiado!";
                }
            };
            _lblQrHint = new Label
            {
                Location = new Point(16, 392),
                Size = new Size(276, 54),
                TextAlign = ContentAlignment.TopCenter,
                Font = new Font("Segoe UI", 8.25f),
                ForeColor = Color.FromArgb(148, 163, 184)
            };
            qrCard.Controls.Add(lblQrTitle);
            qrCard.Controls.Add(_picQr);
            qrCard.Controls.Add(_txtUrl);
            qrCard.Controls.Add(_btnCopyUrl);
            qrCard.Controls.Add(_lblQrHint);
            Controls.Add(qrCard);
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

        private static Button MakeSmallButton(string text, int x, int y, int w, int h)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(35, 43, 59),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 8.75f, FontStyle.Bold)
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(56, 68, 92);
            btn.FlatAppearance.BorderSize = 1;
            return btn;
        }

        private void SetMode(BridgeMode mode)
        {
            _mode = mode;
            bool isUsb = (mode == BridgeMode.Usb);

            _btnWifi.BackColor = !isUsb ? Color.FromArgb(20, 48, 72) : Color.FromArgb(27, 32, 44);
            _btnWifi.ForeColor = !isUsb ? Color.FromArgb(125, 211, 252) : Color.FromArgb(148, 163, 184);
            _btnWifi.FlatAppearance.BorderColor = !isUsb ? Color.FromArgb(56, 189, 248) : Color.FromArgb(45, 55, 72);

            _btnUsb.BackColor = isUsb ? Color.FromArgb(18, 52, 34) : Color.FromArgb(27, 32, 44);
            _btnUsb.ForeColor = isUsb ? Color.FromArgb(134, 239, 172) : Color.FromArgb(148, 163, 184);
            _btnUsb.FlatAppearance.BorderColor = isUsb ? Color.FromArgb(34, 197, 94) : Color.FromArgb(45, 55, 72);

            if (_miModeWifi != null) _miModeWifi.Checked = !isUsb;
            if (_miModeUsb != null) _miModeUsb.Checked = isUsb;

            _capture.SetModeChunkMs(isUsb ? 10 : 20);
            _server.UpdateProfile(mode, _capture.CurrentSampleRate, _capture.ActiveDeviceName);

            SelectPreferredNetworkForMode();
            UpdateGuideCardText();
            RegenerateQr();
        }

        private void RefreshAudioDevices()
        {
            _updatingCombos = true;
            try
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
            finally
            {
                _updatingCombos = false;
            }
        }

        private void RefreshAdbAndNetwork(bool tryAdbReverse)
        {
            if (tryAdbReverse)
            {
                string msg;
                _adbActive = NetworkHelper.TrySetupAdbReverse(DefaultPort, out msg);
                _adbMessage = msg;
                _server.AdbActive = _adbActive;
            }

            _updatingCombos = true;
            try
            {
                NetEndpointOption prev = _cmbNetwork.SelectedItem as NetEndpointOption;
                string prevIp = prev != null ? prev.IpAddress : "";
                bool prevWasUsb = (prev != null && (prev.IsUsb || prev.IsAdb));

                var eps = NetworkHelper.DiscoverEndpoints(_adbActive);
                var usbIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool anyUsb = _adbActive;
                _cmbNetwork.Items.Clear();
                int foundIdx = -1;
                for (int i = 0; i < eps.Count; i++)
                {
                    _cmbNetwork.Items.Add(eps[i]);
                    if (eps[i].IsUsb || eps[i].IsAdb)
                    {
                        anyUsb = true;
                        usbIps.Add(eps[i].IpAddress);
                    }
                    if (!string.IsNullOrEmpty(prevIp) && string.Equals(eps[i].IpAddress, prevIp, StringComparison.Ordinal))
                    {
                        foundIdx = i;
                    }
                }
                _server.UsbDetected = anyUsb;
                _server.UpdateUsbIps(usbIps);

                if (_mode == BridgeMode.Usb && anyUsb && !prevWasUsb)
                {
                    foundIdx = -1;
                }
                else if (_mode == BridgeMode.Wifi && prevWasUsb)
                {
                    foundIdx = -1;
                }

                if (foundIdx >= 0)
                {
                    _cmbNetwork.SelectedIndex = foundIdx;
                }
                else
                {
                    SelectPreferredNetworkForMode();
                }
            }
            finally
            {
                _updatingCombos = false;
            }

            UpdateGuideCardText();
            RegenerateQr();
        }

        private void SelectPreferredNetworkForMode()
        {
            if (_cmbNetwork.Items.Count == 0) return;

            _updatingCombos = true;
            try
            {
                int best = 0;
                if (_mode == BridgeMode.Usb)
                {
                    for (int i = 0; i < _cmbNetwork.Items.Count; i++)
                    {
                        NetEndpointOption opt = _cmbNetwork.Items[i] as NetEndpointOption;
                        if (opt != null && (opt.IsAdb || opt.IsUsb))
                        {
                            best = i;
                            break;
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < _cmbNetwork.Items.Count; i++)
                    {
                        NetEndpointOption opt = _cmbNetwork.Items[i] as NetEndpointOption;
                        if (opt != null && !opt.IsUsb && !opt.IsVirtual)
                        {
                            best = i;
                            break;
                        }
                    }
                }
                _cmbNetwork.SelectedIndex = best;
            }
            finally
            {
                _updatingCombos = false;
            }
        }

        private void UpdateGuideCardText()
        {
            NetEndpointOption sel = _cmbNetwork.SelectedItem as NetEndpointOption;
            bool usbInterfaceReady = (sel != null && sel.IsUsb);
            bool hasAdb = !string.IsNullOrEmpty(NetworkHelper.FindAdbExecutable());
            _btnAdbTunnel.Visible = hasAdb;

            if (_mode == BridgeMode.Usb)
            {
                if (_adbActive && sel != null && sel.IsAdb)
                {
                    _lblUsbStatus.ForeColor = Color.FromArgb(34, 197, 94);
                    _lblUsbStatus.Text = "Cabo USB pronto via Túnel ADB (menor delay possível)";
                    _lblUsbSteps.Text = "O túnel USB já está ativo na porta " + DefaultPort + ".\r\nBasta abrir ou escanear o link ao lado no navegador do celular e apertar Play.";
                }
                else if (usbInterfaceReady)
                {
                    _lblUsbStatus.ForeColor = Color.FromArgb(34, 197, 94);
                    _lblUsbStatus.Text = "Cabo USB (Ancoragem USB) detectado automaticamente!";
                    _lblUsbSteps.Text = "Interface ativa: " + sel.IpAddress + " (" + sel.InterfaceName + ").\r\nSe o celular não abrir a página, clique em 'Liberar Firewall (Admin)' abaixo.";
                }
                else
                {
                    _lblUsbStatus.ForeColor = Color.FromArgb(245, 158, 11);
                    _lblUsbSteps.Text =
                        "1. Conecte o celular no PC com cabo USB de dados.\r\n" +
                        "2. No Android: Configurações > Rede / Roteador > ative 'Ancoragem USB' (USB Tethering).\r\n" +
                        "   No iPhone: Ajustes > Acesso Pessoal > ative e selecione 'Confiar'.";
                    _lblUsbStatus.Text = "Aguardando ativar Ancoragem USB no celular...";
                }
            }
            else
            {
                _lblUsbStatus.ForeColor = Color.FromArgb(56, 189, 248);
                _lblUsbStatus.Text = "Modo Wi-Fi / Rede Local ativo";
                _lblUsbSteps.Text =
                    "Celular e PC precisam estar na mesma rede (Wi-Fi/roteador).\r\n" +
                    "Se quiser o menor delay possível para jogos ou filmes, plugue o cabo USB e mude para 'Cabo USB'.";
            }
        }

        private void RegenerateQr()
        {
            NetEndpointOption sel = _cmbNetwork.SelectedItem as NetEndpointOption;
            string ip = (sel != null && !string.IsNullOrEmpty(sel.IpAddress)) ? sel.IpAddress : "127.0.0.1";
            string modeStr = (_mode == BridgeMode.Usb) ? "usb" : "wifi";
            string url = string.Format("http://{0}:{1}/?mode={2}", ip, DefaultPort, modeStr);

            if (string.Equals(url, _currentUrl, StringComparison.Ordinal))
            {
                return;
            }
            _currentUrl = url;
            _txtUrl.Text = url;
            _btnCopyUrl.Text = "Copiar Link";

            try
            {
                QrMatrixResult qr = QrCodeGenerator.Encode(url);
                Bitmap bmp = QrCodeGenerator.RenderBitmap(qr, 7, 4);
                Bitmap old = _currentQrBmp;
                _currentQrBmp = bmp;
                _picQr.Image = bmp;
                if (old != null) old.Dispose();
            }
            catch
            {
            }

            if (_mode == BridgeMode.Usb)
            {
                _lblQrHint.Text = "Perfil Cabo USB: blocos 10 ms · buffer 15 ms (Ring/Worklet).\r\nUse Túnel ADB para AudioWorklet 2,7 ms sem jitter.";
            }
            else
            {
                _lblQrHint.Text = "Perfil Wi-Fi: blocos 20 ms · buffer 60 ms.\r\nEscaneie com a câmera do celular ou digite o link acima.";
            }
        }

        private void OnUiTick(object sender, EventArgs e)
        {
            int clients = _server.ClientCount;
            if (clients > 0)
            {
                string tr = _server.ConnectedTransportSummary;
                if (tr == "usb")
                {
                    _lblStatusBadge.Text = string.Format("Transmitindo (USB) · {0}", clients);
                    _lblStatusBadge.BackColor = Color.FromArgb(18, 52, 34);
                    _lblStatusBadge.ForeColor = Color.FromArgb(74, 222, 128);
                }
                else if (tr == "wifi" && _mode == BridgeMode.Usb)
                {
                    _lblStatusBadge.Text = string.Format("Wi-Fi ativo · use QR USB", clients);
                    _lblStatusBadge.BackColor = Color.FromArgb(66, 46, 16);
                    _lblStatusBadge.ForeColor = Color.FromArgb(251, 191, 36);
                }
                else
                {
                    _lblStatusBadge.Text = string.Format("Transmitindo (Wi-Fi) · {0}", clients);
                    _lblStatusBadge.BackColor = Color.FromArgb(18, 52, 34);
                    _lblStatusBadge.ForeColor = Color.FromArgb(74, 222, 128);
                }
            }
            else if (!string.IsNullOrEmpty(_capture.LastError))
            {
                _lblStatusBadge.Text = "Erro de áudio";
                _lblStatusBadge.BackColor = Color.FromArgb(65, 22, 26);
                _lblStatusBadge.ForeColor = Color.FromArgb(248, 113, 113);
            }
            else
            {
                _lblStatusBadge.Text = "Aguardando celular...";
                _lblStatusBadge.BackColor = Color.FromArgb(30, 41, 59);
                _lblStatusBadge.ForeColor = Color.FromArgb(56, 189, 248);
            }

            float lvl = _capture.CurrentLevel;
            int barW = (int)Math.Round(Math.Min(1f, Math.Max(0f, lvl)) * _vuMeterHost.Width);
            if (_vuMeterBar.Width != barW)
            {
                _vuMeterBar.Width = barW;
            }

            bool hasGlitches;
            string diagText = _server.GetLiveClientStatsSummary(out hasGlitches);
            if (!string.Equals(_lblLiveDiag.Text, diagText, StringComparison.Ordinal))
            {
                _lblLiveDiag.Text = diagText;
            }
            if (clients == 0)
            {
                _lblLiveDiag.ForeColor = Color.FromArgb(148, 163, 184);
            }
            else if (hasGlitches)
            {
                _lblLiveDiag.ForeColor = Color.FromArgb(248, 113, 113);
            }
            else
            {
                _lblLiveDiag.ForeColor = Color.FromArgb(74, 222, 128);
            }

            _netTickDivider++;
            int netInterval = clients > 0 ? 80 : 25;
            if (_netTickDivider >= netInterval)
            {
                _netTickDivider = 0;
                RefreshAdbAndNetwork(_mode == BridgeMode.Usb && clients == 0);
            }
        }

        private void UpdateFirewallButtonState()
        {
            if (NetworkHelper.IsFirewallRulePresent(DefaultPort))
            {
                _btnFirewall.Text = "Firewall Liberado (8765)";
                _btnFirewall.ForeColor = Color.FromArgb(134, 239, 172);
            }
            else
            {
                _btnFirewall.Text = "Liberar Firewall (Admin)";
                _btnFirewall.ForeColor = Color.White;
            }
        }

        private void OnFirewallClick(object sender, EventArgs e)
        {
            string err;
            bool ok = NetworkHelper.AddFirewallRuleElevated(DefaultPort, out err);
            UpdateFirewallButtonState();
            if (ok)
            {
                MessageBox.Show(this, "Porta TCP " + DefaultPort + " liberada no Firewall do Windows!", "Firewall", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (!string.IsNullOrEmpty(err))
            {
                MessageBox.Show(this, "Não foi possível alterar o Firewall: " + err, "Firewall", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void HideToTray()
        {
            Hide();
            _trayIcon.ShowBalloonTip(2000, "Phone Audio Bridge", "Rodando em segundo plano na bandeja (^).", ToolTipIcon.Info);
        }

        private void ShowFromTray()
        {
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
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_forceExit && e.CloseReason == CloseReason.UserClosing && _server.ClientCount > 0)
            {
                DialogResult res = MessageBox.Show(
                    this,
                    "Há um celular conectado recebendo áudio. Deseja apenas ocultar na bandeja (^) ou encerrar o aplicativo?\n\nSim = Ocultar na bandeja\nNão = Encerrar agora",
                    "Phone Audio Bridge",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (res == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (res == DialogResult.Yes)
                {
                    e.Cancel = true;
                    HideToTray();
                    return;
                }
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
            _capture.Dispose();
            _server.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            if (_currentQrBmp != null)
            {
                _currentQrBmp.Dispose();
                _currentQrBmp = null;
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
            catch
            {
                return false;
            }
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
                        key.SetValue(AppRunName, "\"" + Application.ExecutablePath + "\"");
                        _miStartup.Checked = true;
                    }
                }
            }
            catch
            {
            }
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
            string qrPngPath = args.Length >= 3 ? args[2] : "";

            var sb = new StringBuilder();
            bool allOk = true;

            // 1. WASAPI Enumeration & Loopback Probe
            var devs = WasapiLoopbackCapture.ListOutputDevices();
            string probe = WasapiLoopbackCapture.ProbeLoopbackQuick("");
            bool wasapiOk = devs.Count >= 2 && probe.StartsWith("OK ", StringComparison.Ordinal);
            sb.AppendLine(string.Format("[{0}] WASAPI: count={1} probe={2}", wasapiOk ? "PASS" : "FAIL", devs.Count, probe));
            if (!wasapiOk) allOk = false;

            // 2. Network Interface Discovery
            var nets = NetworkHelper.DiscoverEndpoints(false);
            bool netOk = nets.Count >= 1 && nets[0].IpAddress != "127.0.0.1";
            sb.AppendLine(string.Format("[{0}] Network: count={1} primary={2}", netOk ? "PASS" : "FAIL", nets.Count, nets.Count > 0 ? nets[0].DisplayLabel : "none"));
            if (!netOk) allOk = false;

            // 3. QR Code Generator (V1-V5 RS syndromes & structural checks)
            string[] testUrls = new string[]
            {
                "http://192.168.15.5:8765/?mode=wifi",
                "http://192.168.42.129:8765/?mode=usb",
                "http://127.0.0.1:8765/?mode=usb",
                "http://192.168.100.200:8765/?mode=wifi&extra=abcdefghijklmnopqrstuvwxyz123456"
            };
            bool qrOk = true;
            for (int i = 0; i < testUrls.Length; i++)
            {
                QrMatrixResult qr = QrCodeGenerator.Encode(testUrls[i]);
                bool rsOk = QrCodeGenerator.VerifyReedSolomonSyndromes(qr);
                bool darkMod = qr.Modules[qr.Size - 8, 8];
                if (!rsOk || !darkMod)
                {
                    qrOk = false;
                }
                sb.AppendLine(string.Format("  QR[{0}] len={1} V{2}-M size={3} mask={4} RS={5} darkMod={6}",
                    i, testUrls[i].Length, qr.Version, qr.Size, qr.Mask, rsOk, darkMod));

                if (i == 0 && !string.IsNullOrEmpty(qrPngPath))
                {
                    using (Bitmap bmp = QrCodeGenerator.RenderBitmap(qr, 8, 4))
                    {
                        bmp.Save(qrPngPath, ImageFormat.Png);
                    }
                }
            }
            sb.AppendLine(string.Format("[{0}] QRCode: all versions RS & structure verified", qrOk ? "PASS" : "FAIL"));
            if (!qrOk) allOk = false;

            // 4. HTTP + WebSocket Server End-to-End Loopback Test
            bool serverOk = false;
            string serverDetails = "";
            AudioWebServer srv = null;
            try
            {
                srv = new AudioWebServer(0);
                srv.UpdateProfile(BridgeMode.Usb, 48000, "Test Device");
                srv.Start();
                int port = srv.ListeningPort;

                // Test GET /
                string htmlResp = HttpGetText("127.0.0.1", port, "/?mode=usb");
                bool htmlValid = htmlResp.Contains("200 OK") &&
                    htmlResp.Contains("AudioWorklet") &&
                    htmlResp.Contains("buildNetWorkerCode") &&
                    htmlResp.Contains("loopbackAck") &&
                    htmlResp.Contains("ringL") &&
                    htmlResp.Contains("disconnectSession") &&
                    !htmlResp.Contains("reconnectTimer") &&
                    !htmlResp.Contains("resumeBtn");

                // Test GET /api/info
                string infoResp = HttpGetText("127.0.0.1", port, "/api/info");
                bool infoValid = infoResp.Contains("200 OK") && infoResp.Contains("\"mode\":\"usb\"") && infoResp.Contains("\"chunkMs\":10") && infoResp.Contains("\"targetBufferMs\":15") && infoResp.Contains("\"clientStats\":");

                // Test WebSocket upgrade + config + ping->pong + stats->clientStats
                bool wsValid = TestWebSocketHandshakeAndPing("127.0.0.1", port, out serverDetails);

                serverOk = htmlValid && infoValid && wsValid;
                sb.AppendLine(string.Format("[{0}] HTTP/WS Server: port={1} html={2} info={3} ws={4} ({5})",
                    serverOk ? "PASS" : "FAIL", port, htmlValid, infoValid, wsValid, serverDetails));
            }
            catch (Exception ex)
            {
                sb.AppendLine("[FAIL] HTTP/WS Server exception: " + ex.Message);
            }
            finally
            {
                if (srv != null) srv.Dispose();
            }
            if (!serverOk) allOk = false;

            sb.AppendLine("OVERALL: " + (allOk ? "PASS" : "FAIL"));
            string report = sb.ToString();
            Console.WriteLine(report);

            if (!string.IsNullOrEmpty(reportPath))
            {
                File.WriteAllText(reportPath, report, Encoding.UTF8);
            }
            return allOk ? 0 : 1;
        }

        private static string HttpGetText(string host, int port, string path)
        {
            using (var tcp = new TcpClient(host, port))
            {
                tcp.ReceiveTimeout = 3000;
                using (var ns = tcp.GetStream())
                {
                    string req = string.Format("GET {0} HTTP/1.1\r\nHost: {1}:{2}\r\nConnection: close\r\n\r\n", path, host, port);
                    byte[] reqBytes = Encoding.ASCII.GetBytes(req);
                    ns.Write(reqBytes, 0, reqBytes.Length);
                    using (var ms = new MemoryStream())
                    {
                        byte[] buf = new byte[4096];
                        int n;
                        while ((n = ns.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                        }
                        return Encoding.UTF8.GetString(ms.ToArray());
                    }
                }
            }
        }

        private static bool TestWebSocketHandshakeAndPing(string host, int port, out string info)
        {
            info = "";
            using (var tcp = new TcpClient(host, port))
            {
                tcp.NoDelay = true;
                tcp.ReceiveTimeout = 3000;
                using (var ns = tcp.GetStream())
                {
                    string wsKey = Convert.ToBase64String(Encoding.ASCII.GetBytes("PhoneAudioTest16"));
                    string req = string.Format(
                        "GET /ws HTTP/1.1\r\nHost: {0}:{1}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {2}\r\nSec-WebSocket-Version: 13\r\n\r\n",
                        host, port, wsKey);
                    byte[] reqBytes = Encoding.ASCII.GetBytes(req);
                    ns.Write(reqBytes, 0, reqBytes.Length);

                    // Read HTTP 101 headers
                    byte[] hdrBuf = new byte[1024];
                    int total = 0;
                    while (total < hdrBuf.Length)
                    {
                        int b = ns.ReadByte();
                        if (b < 0) break;
                        hdrBuf[total++] = (byte)b;
                        if (total >= 4 && hdrBuf[total - 4] == 13 && hdrBuf[total - 3] == 10 && hdrBuf[total - 2] == 13 && hdrBuf[total - 1] == 10)
                        {
                            break;
                        }
                    }
                    string hdrText = Encoding.ASCII.GetString(hdrBuf, 0, total);
                    if (!hdrText.Contains("101 Switching Protocols"))
                    {
                        info = "missing 101";
                        return false;
                    }

                    // Read initial config frame from server
                    string cfgMsg = ReadServerTextFrame(ns);
                    if (!cfgMsg.Contains("\"type\":\"config\""))
                    {
                        info = "missing config frame";
                        return false;
                    }

                    // Send 5 masked 1-byte binary ACK frames (loopback reverse-ACK fast path)
                    byte[] ackFrame = BuildMaskedClientBinaryFrame(new byte[] { 0x06 });
                    for (int i = 0; i < 5; i++)
                    {
                        ns.Write(ackFrame, 0, ackFrame.Length);
                    }
                    ns.Flush();

                    // Send masked ping text frame
                    byte[] pingPayload = Encoding.UTF8.GetBytes("{\"type\":\"ping\",\"t\":123}");
                    byte[] pingFrame = BuildMaskedClientTextFrame(pingPayload);
                    ns.Write(pingFrame, 0, pingFrame.Length);

                    string pongMsg = ReadServerTextFrame(ns);
                    if (!pongMsg.Contains("\"type\":\"pong\""))
                    {
                        info = "missing pong frame";
                        return false;
                    }

                    // Send masked stats text frame and verify /api/info returns it
                    byte[] statsPayload = Encoding.UTF8.GetBytes("{\"type\":\"stats\",\"engine\":\"Worklet+Worker\",\"bufMs\":12,\"minBufMs\":6,\"wsGapMaxMs\":11,\"plc\":0,\"plcPerSec\":0,\"resyncs\":0,\"rttMs\":1,\"targetMs\":15}");
                    byte[] statsFrame = BuildMaskedClientTextFrame(statsPayload);
                    ns.Write(statsFrame, 0, statsFrame.Length);
                    ns.Flush();
                    Thread.Sleep(80);

                    string infoWithStats = HttpGetText(host, port, "/api/info");
                    if (!infoWithStats.Contains("\"Worklet+Worker\"") || !infoWithStats.Contains("\"wsGapMaxMs\":11"))
                    {
                        info = "missing clientStats in /api/info";
                        return false;
                    }

                    info = "101+config+ack5+pong+stats OK";
                    return true;
                }
            }
        }

        private static byte[] BuildMaskedClientTextFrame(byte[] payload)
        {
            return BuildMaskedClientFrame(0x81, payload);
        }

        private static byte[] BuildMaskedClientBinaryFrame(byte[] payload)
        {
            return BuildMaskedClientFrame(0x82, payload);
        }

        private static byte[] BuildMaskedClientFrame(byte firstByte, byte[] payload)
        {
            byte[] mask = new byte[] { 0x12, 0x34, 0x56, 0x78 };
            int hdrLen = payload.Length <= 125 ? 6 : 8;
            byte[] frame = new byte[hdrLen + payload.Length];
            frame[0] = firstByte;
            if (payload.Length <= 125)
            {
                frame[1] = (byte)(0x80 | payload.Length);
                Buffer.BlockCopy(mask, 0, frame, 2, 4);
            }
            else
            {
                frame[1] = (byte)(0x80 | 126);
                frame[2] = (byte)((payload.Length >> 8) & 0xFF);
                frame[3] = (byte)(payload.Length & 0xFF);
                Buffer.BlockCopy(mask, 0, frame, 4, 4);
            }
            for (int i = 0; i < payload.Length; i++)
            {
                frame[hdrLen + i] = (byte)(payload[i] ^ mask[i & 3]);
            }
            return frame;
        }

        private static string ReadServerTextFrame(NetworkStream ns)
        {
            int b0 = ns.ReadByte();
            int b1 = ns.ReadByte();
            if (b0 < 0 || b1 < 0) return "";
            int len = b1 & 0x7F;
            if (len == 126)
            {
                int hi = ns.ReadByte();
                int lo = ns.ReadByte();
                len = (hi << 8) | lo;
            }
            byte[] data = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = ns.Read(data, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            return Encoding.UTF8.GetString(data, 0, read);
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

            BridgeMode startMode = BridgeMode.Wifi;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--usb", StringComparison.OrdinalIgnoreCase))
                    {
                        startMode = BridgeMode.Usb;
                    }
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
                Application.Run(new MainBridgeForm(startMode));
                return 0;
            }
        }
    }

    #endregion
}
