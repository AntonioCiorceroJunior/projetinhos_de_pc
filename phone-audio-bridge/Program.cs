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
                hr = audioClient.Initialize(AudclntShareModeShared, AudclntStreamFlagsLoopback, 500000, 0, mixFormatPtr, IntPtr.Zero);
                if (hr != 0)
                {
                    return string.Format("ERR Initialize Loopback HR=0x{0:X8}", hr);
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
                Thread.Sleep(25);
                uint nextPkt;
                captureClient.GetNextPacketSize(out nextPkt);
                audioClient.Stop();

                return string.Format("OK device=\"{0}\" rate={1}Hz ch={2} bits={3} tag=0x{4:X}", friendly, wfx.nSamplesPerSec, wfx.nChannels, wfx.wBitsPerSample, wfx.wFormatTag);
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
            _thread.Priority = ThreadPriority.AboveNormal;
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
            try
            {
                timerPeriodSet = NativeWin32.timeBeginPeriod(1) == 0;
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

                hr = audioClient.Initialize(AudclntShareModeShared, AudclntStreamFlagsLoopback, 500000, 0, mixFormatPtr, IntPtr.Zero);
                if (hr != 0)
                {
                    LastError = string.Format("Falha em Initialize Loopback (0x{0:X8}).", hr);
                    return;
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
                long lastAudioTick = Environment.TickCount;
                long lastDefaultCheckTick = lastAudioTick;
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
                        if (unchecked(nowTick - lastAudioTick) > 260)
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

                        Thread.Sleep(2);
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

                                long nowTick = Environment.TickCount;
                                if (peak > 0.00018f)
                                {
                                    lastAudioTick = nowTick;
                                }

                                if (unchecked(nowTick - lastAudioTick) <= 260)
                                {
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
                                else
                                {
                                    accumOffset = 0;
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

                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] parts = pathEnv.Split(';');
                for (int i = 0; i < parts.Length; i++)
                {
                    string dir = parts[i].Trim();
                    if (dir.Length == 0) continue;
                    string p = Path.Combine(dir, "adb.exe");
                    if (File.Exists(p)) return p;
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
        <span id=""bufLbl"" class=""ctrl-val"">55 ms</span>
      </div>
      <input id=""bufSlider"" type=""range"" min=""15"" max=""150"" step=""5"" value=""55"">
      <div class=""presets"">
        <button class=""chip"" data-ms=""20"" type=""button"">USB 20ms</button>
        <button class=""chip"" data-ms=""45"" type=""button"">Rápido 45ms</button>
        <button class=""chip"" data-ms=""90"" type=""button"">Estável 90ms</button>
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
    Dica: se notar picotes no áudio, aumente um pouco o <b>Buffer Alvo</b> (ex.: 30-45 ms no USB ou 70-90 ms no Wi-Fi). Em músicas/vídeos, deixe a tela bloquear normalmente se o sistema mantiver a guia em segundo plano.
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
  var ctx = null;
  var gainNode = null;
  var keepAliveAudio = null;
  var wakeLock = null;
  var pingTimer = null;
  var wasAudioRunning = false;
  var keepAlivePlaying = false;
  var intentionalPause = false;

  var sampleRate = 48000;
  var channels = 2;
  var nextPlayTime = 0;
  var targetBufferMs = initialMode === 'usb' ? 20 : 55;
  var maxBufferMs = initialMode === 'usb' ? 50 : 125;
  var autoFollowPc = true;
  var resyncCount = 0;
  var smoothLevel = 0;

  function applyModeBadge(mode) {
    if (mode === 'usb') {
      modeBadge.textContent = 'CABO USB';
      modeBadge.className = 'badge usb';
    } else {
      modeBadge.textContent = 'WI-FI';
      modeBadge.className = 'badge';
    }
  }

  function updateBufferUi(ms) {
    targetBufferMs = ms;
    maxBufferMs = Math.max(ms * 2 + 12, ms + 25);
    bufSlider.value = String(ms);
    bufLbl.textContent = ms + ' ms';
    for (var i = 0; i < chips.length; i++) {
      var cms = parseInt(chips[i].getAttribute('data-ms'), 10);
      chips[i].className = cms === ms ? 'chip active' : 'chip';
    }
  }

  applyModeBadge(initialMode);
  updateBufferUi(targetBufferMs);

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

  function disconnectSession(state, title, sub) {
    if (!running && !ws) {
      setStatus(state, title, sub);
      return;
    }
    running = false;
    wasAudioRunning = false;
    if (pingTimer) { clearInterval(pingTimer); pingTimer = null; }
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

  function ensureAudioContext() {
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
    nextPlayTime = ctx.currentTime + (targetBufferMs / 1000);
  }

  function schedulePcmFrame(arrayBuffer) {
    if (!ctx || !running) return;
    if (arrayBuffer.byteLength <= 8) return;

    var pcmBytes = arrayBuffer.byteLength - 8;
    var frames = (pcmBytes / 4) | 0;
    if (frames <= 0) return;

    var i16 = new Int16Array(arrayBuffer, 8, frames * 2);
    var abuf = ctx.createBuffer(2, frames, sampleRate);
    var ch0 = abuf.getChannelData(0);
    var ch1 = abuf.getChannelData(1);

    var peak = 0;
    for (var i = 0; i < frames; i++) {
      var l = i16[i * 2] / 32768;
      var r = i16[i * 2 + 1] / 32768;
      ch0[i] = l;
      ch1[i] = r;
      var a = l < 0 ? -l : l;
      if (a > peak) peak = a;
    }

    smoothLevel = smoothLevel * 0.65 + Math.min(1, peak * 1.4) * 0.35;
    meterFill.style.width = Math.round(smoothLevel * 100) + '%';

    var now = ctx.currentTime;
    var targetSec = targetBufferMs / 1000;
    var maxSec = maxBufferMs / 1000;
    var queued = nextPlayTime - now;

    if (queued < 0.003) {
      nextPlayTime = now + targetSec;
      var fadeSamples = Math.min(frames, Math.round(sampleRate * 0.002));
      for (var f = 0; f < fadeSamples; f++) {
        var g = f / fadeSamples;
        ch0[f] *= g;
        ch1[f] *= g;
      }
      resyncCount++;
      dropStat.textContent = String(resyncCount);
    } else if (queued > maxSec) {
      nextPlayTime = now + targetSec;
      var fade2 = Math.min(frames, Math.round(sampleRate * 0.002));
      for (var k = 0; k < fade2; k++) {
        var g2 = k / fade2;
        ch0[k] *= g2;
        ch1[k] *= g2;
      }
      resyncCount++;
      dropStat.textContent = String(resyncCount);
    }

    queued = nextPlayTime - now;
    var err = queued - targetSec;
    var rate = 1.0 + err * 0.25;
    if (rate > 1.006) rate = 1.006;
    if (rate < 0.994) rate = 0.994;

    var src = ctx.createBufferSource();
    src.buffer = abuf;
    src.playbackRate.value = rate;
    src.connect(gainNode);
    src.start(nextPlayTime);

    nextPlayTime += abuf.duration / rate;
    bufStat.textContent = Math.max(0, Math.round((nextPlayTime - now) * 1000)) + ' ms';
  }

  function connectWs() {
    if (!running) return;
    if (ws) {
      try { ws.close(); } catch (e) {}
      ws = null;
    }

    var proto = location.protocol === 'https:' ? 'wss://' : 'ws://';
    setStatus('wait', 'Conectando ao PC...', 'Abrindo canal de baixa latência');
    ws = new WebSocket(proto + location.host + '/ws');
    ws.binaryType = 'arraybuffer';

    ws.onopen = function() {
      setStatus('live', 'Conectado e recebendo áudio', 'Áudio pronto — toque algo no PC para ouvir');
      nextPlayTime = ctx.currentTime + (targetBufferMs / 1000);
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
            rttStat.textContent = Math.max(1, Math.round(performance.now() - msg.t)) + ' ms';
          } else if (msg.type === 'config') {
            if (msg.sampleRate) sampleRate = msg.sampleRate;
            fmtStat.textContent = Math.round(sampleRate / 100) / 10 + 'kHz Estéreo';
            if (msg.device) deviceText.textContent = 'Fonte PC: ' + msg.device;
            if (msg.mode) applyModeBadge(msg.mode);
            if (autoFollowPc && msg.targetBufferMs) {
              updateBufferUi(msg.targetBufferMs);
            }
          }
        } catch (e) {}
      } else if (ev.data instanceof ArrayBuffer) {
        schedulePcmFrame(ev.data);
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
      resyncCount = 0;
      dropStat.textContent = '0';
      playBtn.textContent = 'Desconectar';
      playBtn.className = 'btn-main stop';
      ensureAudioContext();
      startKeepAlive();
      connectWs();
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
        private int _maxQueueSize = 4;

        public WsClientSession(TcpClient tcp, NetworkStream stream, int maxQueueSize, Action<WsClientSession> onDisconnect)
        {
            _tcp = tcp;
            _stream = stream;
            _maxQueueSize = maxQueueSize;
            _onDisconnect = onDisconnect;
        }

        public void SetMaxQueueSize(int maxQueueSize)
        {
            _maxQueueSize = maxQueueSize < 2 ? 2 : maxQueueSize;
        }

        public void StartLoops()
        {
            var writer = new Thread(WriterLoop);
            writer.IsBackground = true;
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

        public void EnqueueBinaryAudio(byte[] packetWithHeader)
        {
            byte[] frame = BuildFrame(0x82, packetWithHeader);
            lock (_queueLock)
            {
                if (_closed) return;
                while (_sendQueue.Count >= _maxQueueSize)
                {
                    _sendQueue.Dequeue();
                }
                _sendQueue.Enqueue(frame);
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
                Dispose();
            }
        }

        private void ReaderLoop()
        {
            try
            {
                while (!_closed)
                {
                    int b0 = _stream.ReadByte();
                    if (b0 < 0) break;
                    int b1 = _stream.ReadByte();
                    if (b1 < 0) break;

                    int opcode = b0 & 0x0F;
                    bool masked = (b1 & 0x80) != 0;
                    long len = b1 & 0x7F;

                    if (len == 126)
                    {
                        byte[] ext = ReadExact(_stream, 2);
                        if (ext == null) break;
                        len = (ext[0] << 8) | ext[1];
                    }
                    else if (len == 127)
                    {
                        byte[] ext = ReadExact(_stream, 8);
                        if (ext == null) break;
                        len = 0;
                        for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
                    }

                    byte[] maskKey = null;
                    if (masked)
                    {
                        maskKey = ReadExact(_stream, 4);
                        if (maskKey == null) break;
                    }

                    if (len > 1048576) break;
                    byte[] payload = len > 0 ? ReadExact(_stream, (int)len) : new byte[0];
                    if (payload == null) break;

                    if (masked && maskKey != null)
                    {
                        for (int i = 0; i < payload.Length; i++)
                        {
                            payload[i] ^= maskKey[i & 3];
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

        private static byte[] ReadExact(Stream s, int count)
        {
            byte[] buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) return null;
                read += n;
            }
            return buf;
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
        private readonly byte[] _htmlBytes;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private uint _seqCounter;

        public volatile string Mode = "wifi";
        public volatile int SampleRate = 48000;
        public volatile int ChunkMs = 20;
        public volatile int TargetBufferMs = 55;
        public volatile int MaxBufferMs = 125;
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

        public AudioWebServer(int port)
        {
            _port = port;
            _htmlBytes = Encoding.UTF8.GetBytes(MobilePlayerHtml.BuildPage());
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
            TargetBufferMs = isUsb ? 20 : 55;
            MaxBufferMs = isUsb ? 50 : 125;
            DeviceName = string.IsNullOrEmpty(deviceName) ? "Padrão do Windows" : deviceName;

            int qLimit = isUsb ? 3 : 4;
            string cfgJson = BuildConfigJson();
            lock (_clientsLock)
            {
                for (int i = 0; i < _clients.Count; i++)
                {
                    _clients[i].SetMaxQueueSize(qLimit);
                    _clients[i].EnqueueText(cfgJson);
                }
            }
        }

        public void BroadcastPcm(byte[] pcm16Stereo)
        {
            int count;
            lock (_clientsLock)
            {
                count = _clients.Count;
                if (count == 0) return;

                uint seq = ++_seqCounter;
                uint tick = unchecked((uint)Environment.TickCount);
                byte[] pkt = new byte[8 + pcm16Stereo.Length];
                pkt[0] = (byte)(seq & 0xFF);
                pkt[1] = (byte)((seq >> 8) & 0xFF);
                pkt[2] = (byte)((seq >> 16) & 0xFF);
                pkt[3] = (byte)((seq >> 24) & 0xFF);
                pkt[4] = (byte)(tick & 0xFF);
                pkt[5] = (byte)((tick >> 8) & 0xFF);
                pkt[6] = (byte)((tick >> 16) & 0xFF);
                pkt[7] = (byte)((tick >> 24) & 0xFF);
                Buffer.BlockCopy(pcm16Stereo, 0, pkt, 8, pcm16Stereo.Length);

                for (int i = 0; i < _clients.Count; i++)
                {
                    _clients[i].EnqueueBinaryAudio(pkt);
                }
            }
        }

        private string BuildConfigJson()
        {
            return string.Format(
                "{{\"type\":\"config\",\"sampleRate\":{0},\"channels\":2,\"bits\":16,\"mode\":\"{1}\",\"chunkMs\":{2},\"targetBufferMs\":{3},\"maxBufferMs\":{4},\"device\":\"{5}\"}}",
                SampleRate,
                Mode,
                ChunkMs,
                TargetBufferMs,
                MaxBufferMs,
                EscapeJson(DeviceName));
        }

        private string BuildInfoJson()
        {
            return string.Format(
                "{{\"mode\":\"{0}\",\"sampleRate\":{1},\"channels\":2,\"bits\":16,\"chunkMs\":{2},\"targetBufferMs\":{3},\"maxBufferMs\":{4},\"device\":\"{5}\",\"clients\":{6},\"usbDetected\":{7},\"adbActive\":{8}}}",
                Mode,
                SampleRate,
                ChunkMs,
                TargetBufferMs,
                MaxBufferMs,
                EscapeJson(DeviceName),
                ClientCount,
                UsbDetected ? "true" : "false",
                AdbActive ? "true" : "false");
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

                    int qLimit = Mode == "usb" ? 3 : 4;
                    var session = new WsClientSession(client, stream, qLimit, OnSessionClosed);
                    lock (_clientsLock)
                    {
                        _clients.Add(session);
                    }
                    session.StartLoops();
                    session.EnqueueText(BuildConfigJson());
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
        private System.Windows.Forms.Timer _uiTimer;

        public MainBridgeForm()
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
            SetMode(BridgeMode.Wifi);

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
            ClientSize = new Size(780, 560);
            MinimumSize = new Size(780, 560);
            MaximumSize = new Size(780, 560);
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
                Text = "Wi-Fi / Rede Local\r\nSem cabos (~55 ms)",
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
                Text = "Cabo USB (Menor Delay)\r\nSuper responsivo (~20 ms)",
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

            // Bottom utility buttons
            _btnFirewall = MakeSmallButton("Liberar Firewall (Admin)", lx, 498, 198, 36);
            _btnFirewall.Click += OnFirewallClick;

            var btnHideTray = MakeSmallButton("Ocultar na Bandeja (^)", lx + 210, 498, 198, 36);
            btnHideTray.Click += delegate { HideToTray(); };

            Controls.Add(_btnFirewall);
            Controls.Add(btnHideTray);

            // Right column: QR Code Card
            var qrCard = new Panel
            {
                Location = new Point(450, 78),
                Size = new Size(308, 456),
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

                var eps = NetworkHelper.DiscoverEndpoints(_adbActive);
                bool anyUsb = _adbActive;
                _cmbNetwork.Items.Clear();
                int foundIdx = -1;
                for (int i = 0; i < eps.Count; i++)
                {
                    _cmbNetwork.Items.Add(eps[i]);
                    if (eps[i].IsUsb) anyUsb = true;
                    if (!string.IsNullOrEmpty(prevIp) && string.Equals(eps[i].IpAddress, prevIp, StringComparison.Ordinal))
                    {
                        foundIdx = i;
                    }
                }
                _server.UsbDetected = anyUsb;

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
                _lblQrHint.Text = "Perfil Cabo USB: pacotes de 10 ms · buffer inicial 20 ms.\r\nSe o link usar 192.168.x.x, libere o Firewall abaixo.";
            }
            else
            {
                _lblQrHint.Text = "Perfil Wi-Fi: pacotes de 20 ms · buffer inicial 55 ms.\r\nEscaneie com a câmera do celular ou digite o link acima.";
            }
        }

        private void OnUiTick(object sender, EventArgs e)
        {
            int clients = _server.ClientCount;
            if (clients > 0)
            {
                _lblStatusBadge.Text = string.Format("Transmitindo · {0} conectado(s)", clients);
                _lblStatusBadge.BackColor = Color.FromArgb(18, 52, 34);
                _lblStatusBadge.ForeColor = Color.FromArgb(74, 222, 128);
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

            _netTickDivider++;
            if (_netTickDivider >= 25)
            {
                _netTickDivider = 0;
                RefreshAdbAndNetwork(_mode == BridgeMode.Usb);
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
                bool htmlValid = htmlResp.Contains("200 OK") && htmlResp.Contains("WebSocket") && htmlResp.Contains("AudioContext");

                // Test GET /api/info
                string infoResp = HttpGetText("127.0.0.1", port, "/api/info");
                bool infoValid = infoResp.Contains("200 OK") && infoResp.Contains("\"mode\":\"usb\"") && infoResp.Contains("\"targetBufferMs\":20");

                // Test WebSocket upgrade + config + ping->pong
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

                    // Send masked ping text frame
                    byte[] pingPayload = Encoding.UTF8.GetBytes("{\"type\":\"ping\",\"t\":123}");
                    byte[] mask = new byte[] { 0x12, 0x34, 0x56, 0x78 };
                    byte[] frame = new byte[6 + pingPayload.Length];
                    frame[0] = 0x81;
                    frame[1] = (byte)(0x80 | pingPayload.Length);
                    Buffer.BlockCopy(mask, 0, frame, 2, 4);
                    for (int i = 0; i < pingPayload.Length; i++)
                    {
                        frame[6 + i] = (byte)(pingPayload[i] ^ mask[i & 3]);
                    }
                    ns.Write(frame, 0, frame.Length);

                    string pongMsg = ReadServerTextFrame(ns);
                    if (!pongMsg.Contains("\"type\":\"pong\""))
                    {
                        info = "missing pong frame";
                        return false;
                    }

                    info = "101+config+pong OK";
                    return true;
                }
            }
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
                Application.Run(new MainBridgeForm());
                return 0;
            }
        }
    }

    #endregion
}
