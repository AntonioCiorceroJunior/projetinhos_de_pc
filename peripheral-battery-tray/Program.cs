using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBatteryTray
{
    internal enum DeviceKind
    {
        Mouse,
        Headset,
        Bluetooth
    }

    internal enum DeviceState
    {
        Online,
        Sleeping,
        Offline
    }

    internal sealed class BatteryReading
    {
        public string Id;
        public string Name;
        public DeviceKind Kind;
        public DeviceState State;
        public int Percent;
        public bool IsCharging;
        public bool IsFullyCharged;
        public int VoltageMv;
        public string Details;
    }

    internal sealed class HidInterfaceInfo
    {
        public string Path;
        public ushort VendorId;
        public ushort ProductId;
        public string ProductName;
        public ushort UsagePage;
        public ushort Usage;
        public ushort InputReportLength;
        public ushort OutputReportLength;
    }

    internal static class NativeHid
    {
        public const uint GENERIC_READ = 0x80000000u;
        public const uint GENERIC_WRITE = 0x40000000u;
        public const uint FILE_SHARE_READ = 0x00000001u;
        public const uint FILE_SHARE_WRITE = 0x00000002u;
        public const uint OPEN_EXISTING = 3u;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000u;

        private const uint DIGCF_PRESENT = 0x00000002u;
        private const uint DIGCF_ALLCLASSES = 0x00000004u;
        private const uint DIGCF_DEVICEINTERFACE = 0x00000010u;
        private const uint ERROR_IO_PENDING = 997u;
        private const uint ERROR_INSUFFICIENT_BUFFER = 122u;

        public const uint DEVPROP_TYPE_BYTE = 3u;
        public const uint DEVPROP_TYPE_GUID = 13u;
        public const uint DEVPROP_TYPE_STRING = 18u;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool HidD_GetProductString(SafeFileHandle hidDeviceObject, StringBuilder buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FlushQueue(SafeFileHandle hidDeviceObject);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsAll(IntPtr classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref DEVPROPKEY propertyKey, out uint propertyType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        public static List<HidInterfaceInfo> EnumerateHidInterfaces(ushort vid, ushort usagePage, ushort usage, ushort inLen, ushort outLen)
        {
            List<HidInterfaceInfo> results = new List<HidInterfaceInfo>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr infoSet = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (infoSet == IntPtr.Zero || infoSet == new IntPtr(-1))
            {
                return results;
            }

            try
            {
                SP_DEVICE_INTERFACE_DATA ifaceData = new SP_DEVICE_INTERFACE_DATA();
                ifaceData.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                uint idx = 0;

                while (SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref hidGuid, idx++, ref ifaceData))
                {
                    uint reqSize;
                    SetupDiGetDeviceInterfaceDetail(infoSet, ref ifaceData, IntPtr.Zero, 0, out reqSize, IntPtr.Zero);
                    if (reqSize == 0)
                    {
                        continue;
                    }

                    IntPtr detailBuf = Marshal.AllocHGlobal((int)reqSize);
                    try
                    {
                        Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(infoSet, ref ifaceData, detailBuf, reqSize, out reqSize, IntPtr.Zero))
                        {
                            continue;
                        }

                        string path = Marshal.PtrToStringAuto(IntPtr.Add(detailBuf, 4));
                        if (string.IsNullOrEmpty(path))
                        {
                            continue;
                        }

                        using (SafeFileHandle h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                        {
                            if (h.IsInvalid)
                            {
                                continue;
                            }

                            HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
                            attr.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                            if (!HidD_GetAttributes(h, ref attr) || attr.VendorID != vid)
                            {
                                continue;
                            }

                            IntPtr pp;
                            if (!HidD_GetPreparsedData(h, out pp))
                            {
                                continue;
                            }

                            HIDP_CAPS caps;
                            try
                            {
                                HidP_GetCaps(pp, out caps);
                            }
                            finally
                            {
                                HidD_FreePreparsedData(pp);
                            }

                            if (caps.UsagePage != usagePage || caps.Usage != usage)
                            {
                                continue;
                            }
                            if (inLen > 0 && caps.InputReportByteLength != inLen)
                            {
                                continue;
                            }
                            if (outLen > 0 && caps.OutputReportByteLength != outLen)
                            {
                                continue;
                            }

                            StringBuilder prodSb = new StringBuilder(256);
                            HidD_GetProductString(h, prodSb, prodSb.Capacity * 2);

                            HidInterfaceInfo info = new HidInterfaceInfo();
                            info.Path = path;
                            info.VendorId = attr.VendorID;
                            info.ProductId = attr.ProductID;
                            info.ProductName = prodSb.ToString().Trim();
                            info.UsagePage = caps.UsagePage;
                            info.Usage = caps.Usage;
                            info.InputReportLength = caps.InputReportByteLength;
                            info.OutputReportLength = caps.OutputReportByteLength;
                            results.Add(info);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailBuf);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(infoSet);
            }

            return results;
        }

        public static byte[] ReadWithTimeout(SafeFileHandle h, int len, int timeoutMs)
        {
            using (ManualResetEvent ev = new ManualResetEvent(false))
            {
                byte[] buf = new byte[len];
                GCHandle pinnedBuf = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try
                {
                    NativeOverlapped ov = new NativeOverlapped();
                    ov.EventHandle = ev.SafeWaitHandle.DangerousGetHandle();

                    uint read;
                    bool ok = ReadFile(h, buf, (uint)len, out read, ref ov);
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != ERROR_IO_PENDING)
                        {
                            return null;
                        }

                        if (!ev.WaitOne(timeoutMs))
                        {
                            CancelIoEx(h, IntPtr.Zero);
                            GetOverlappedResult(h, ref ov, out read, true);
                            return null;
                        }

                        if (!GetOverlappedResult(h, ref ov, out read, false))
                        {
                            return null;
                        }
                    }

                    return buf;
                }
                finally
                {
                    if (pinnedBuf.IsAllocated)
                    {
                        pinnedBuf.Free();
                    }
                }
            }
        }

        public static IntPtr OpenAllPresentDevices()
        {
            return SetupDiGetClassDevsAll(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        }

        public static bool EnumDeviceInfo(IntPtr infoSet, uint index, ref SP_DEVINFO_DATA devInfo)
        {
            return SetupDiEnumDeviceInfo(infoSet, index, ref devInfo);
        }

        public static void CloseDeviceInfoSet(IntPtr infoSet)
        {
            if (infoSet != IntPtr.Zero && infoSet != new IntPtr(-1))
            {
                SetupDiDestroyDeviceInfoList(infoSet);
            }
        }

        public static byte[] GetDevicePropertyBytes(IntPtr infoSet, ref SP_DEVINFO_DATA devInfo, DEVPROPKEY key, out uint propType)
        {
            uint reqSize = 0;
            propType = 0;
            byte[] initial = new byte[256];
            if (SetupDiGetDevicePropertyW(infoSet, ref devInfo, ref key, out propType, initial, (uint)initial.Length, out reqSize, 0))
            {
                byte[] exact = new byte[reqSize];
                Array.Copy(initial, exact, (int)reqSize);
                return exact;
            }

            if (Marshal.GetLastWin32Error() == ERROR_INSUFFICIENT_BUFFER && reqSize > 0)
            {
                byte[] larger = new byte[reqSize];
                if (SetupDiGetDevicePropertyW(infoSet, ref devInfo, ref key, out propType, larger, reqSize, out reqSize, 0))
                {
                    return larger;
                }
            }

            return null;
        }
    }

    internal sealed class AtkMouseReader
    {
        private const ushort VID_COMPX = 0x373B;
        private const ushort UP_ATK = 0xFF02;
        private const ushort U_ATK = 0x0002;
        private const int REPORT_LEN = 17;

        private int _lastPercent = -1;
        private int _lastVoltageMv = 0;
        private int _consecutiveMisses = 0;

        public BatteryReading Read()
        {
            List<HidInterfaceInfo> ifaces = NativeHid.EnumerateHidInterfaces(VID_COMPX, UP_ATK, U_ATK, REPORT_LEN, REPORT_LEN);
            if (ifaces.Count == 0)
            {
                _consecutiveMisses = 0;
                BatteryReading offline = new BatteryReading();
                offline.Id = "atk-mouse";
                offline.Name = "ATK X1 Ultimate 2.0";
                offline.Kind = DeviceKind.Mouse;
                offline.State = DeviceState.Offline;
                offline.Percent = -1;
                offline.Details = "Desconectado";
                return offline;
            }

            string displayName = "ATK X1 Ultimate 2.0";
            BatteryReading best = null;

            for (int i = 0; i < ifaces.Count; i++)
            {
                HidInterfaceInfo iface = ifaces[i];
                if (!string.IsNullOrEmpty(iface.ProductName))
                {
                    displayName = NormalizeMouseName(iface.ProductName);
                }

                BatteryReading candidate = QueryInterface(iface.Path, displayName);
                if (candidate != null)
                {
                    if (best == null)
                    {
                        best = candidate;
                    }
                    else if (candidate.Percent > 0 && best.Percent <= 0)
                    {
                        best = candidate;
                    }
                    else if (candidate.IsCharging && !best.IsCharging && candidate.Percent > 0)
                    {
                        best = candidate;
                    }
                }
            }

            if (best != null && best.Percent > 0)
            {
                _lastPercent = best.Percent;
                _lastVoltageMv = best.VoltageMv;
                _consecutiveMisses = 0;
                return best;
            }

            _consecutiveMisses++;
            if (_lastPercent >= 0 && _consecutiveMisses < 3)
            {
                BatteryReading cached = new BatteryReading();
                cached.Id = "atk-mouse";
                cached.Name = displayName;
                cached.Kind = DeviceKind.Mouse;
                cached.State = DeviceState.Online;
                cached.Percent = _lastPercent;
                cached.VoltageMv = _lastVoltageMv;
                cached.Details = FormatOnlineDetails(false, _lastVoltageMv);
                return cached;
            }

            BatteryReading sleep = new BatteryReading();
            sleep.Id = "atk-mouse";
            sleep.Name = displayName;
            sleep.Kind = DeviceKind.Mouse;
            sleep.State = DeviceState.Sleeping;
            sleep.Percent = _lastPercent;
            sleep.VoltageMv = _lastVoltageMv;
            sleep.Details = _lastPercent >= 0
                ? string.Format("Em repouso ({0}%)", _lastPercent)
                : "Em repouso";
            return sleep;
        }

        private static string NormalizeMouseName(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return "ATK X1 Ultimate 2.0";
            }
            if (raw.IndexOf("ATK X1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                raw.IndexOf("8k dongle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                raw.IndexOf("Wireless mouse", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "ATK X1 Ultimate 2.0";
            }
            return raw;
        }

        private static string FormatOnlineDetails(bool charging, int voltageMv)
        {
            string mode = charging ? "Carregando" : "Sem fio";
            if (voltageMv > 0)
            {
                return string.Format("{0}, {1:0.00}V", mode, voltageMv / 1000.0);
            }
            return mode;
        }

        private static BatteryReading QueryInterface(string path, string name)
        {
            using (SafeFileHandle hw = NativeHid.CreateFile(path, NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE, NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE, IntPtr.Zero, NativeHid.OPEN_EXISTING, 0, IntPtr.Zero))
            using (SafeFileHandle hr = NativeHid.CreateFile(path, NativeHid.GENERIC_READ, NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE, IntPtr.Zero, NativeHid.OPEN_EXISTING, NativeHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero))
            {
                if (hw.IsInvalid || hr.IsInvalid)
                {
                    return null;
                }

                NativeHid.HidD_FlushQueue(hr);

                byte[] cmd = new byte[REPORT_LEN];
                cmd[0] = 0x08;
                cmd[1] = 0x04;
                cmd[16] = 0x49;

                uint written;
                if (!NativeHid.WriteFile(hw, cmd, REPORT_LEN, out written, IntPtr.Zero) || written != REPORT_LEN)
                {
                    return null;
                }

                for (int attempt = 0; attempt < 4; attempt++)
                {
                    byte[] resp = NativeHid.ReadWithTimeout(hr, REPORT_LEN, 250);
                    if (resp == null)
                    {
                        break;
                    }

                    if (resp[0] != 0x08 || resp[1] != 0x04)
                    {
                        continue;
                    }

                    int sum = 0;
                    for (int b = 0; b < REPORT_LEN; b++)
                    {
                        sum += resp[b];
                    }
                    if ((sum & 0xFF) != 0x55)
                    {
                        continue;
                    }

                    int pct = resp[6];
                    bool charging = resp[7] == 1;
                    int mv = (resp[8] << 8) | resp[9];

                    if (pct == 0 && mv == 0)
                    {
                        return null;
                    }
                    if (pct > 100)
                    {
                        pct = 100;
                    }

                    BatteryReading reading = new BatteryReading();
                    reading.Id = "atk-mouse";
                    reading.Name = name;
                    reading.Kind = DeviceKind.Mouse;
                    reading.State = DeviceState.Online;
                    reading.Percent = pct;
                    reading.IsCharging = charging;
                    reading.IsFullyCharged = charging && pct >= 100;
                    reading.VoltageMv = mv;
                    reading.Details = FormatOnlineDetails(charging, mv);
                    return reading;
                }

                return null;
            }
        }
    }

    internal sealed class CorsairHs80Reader
    {
        private const ushort VID_CORSAIR = 0x1B1C;
        private const ushort PID_HS80_WIRED = 0x0A69;
        private const ushort PID_HS80_DONGLE = 0x0A6B;
        private const ushort UP_BRAGI = 0xFF42;
        private const ushort U_BRAGI = 0x0001;
        private const int REPORT_LEN = 64;

        public BatteryReading Read()
        {
            List<HidInterfaceInfo> all = NativeHid.EnumerateHidInterfaces(VID_CORSAIR, UP_BRAGI, U_BRAGI, REPORT_LEN, REPORT_LEN);
            HidInterfaceInfo wired = null;
            HidInterfaceInfo dongle = null;

            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].ProductId == PID_HS80_WIRED)
                {
                    wired = all[i];
                }
                else if (all[i].ProductId == PID_HS80_DONGLE)
                {
                    dongle = all[i];
                }
            }

            if (wired == null && dongle == null)
            {
                return null;
            }

            if (wired != null)
            {
                BatteryReading wiredReading = QueryDevice(wired.Path, 0x08, true);
                if (wiredReading != null)
                {
                    return wiredReading;
                }
            }

            if (dongle != null)
            {
                BatteryReading dongleReading = QueryDongle(dongle.Path);
                if (dongleReading != null)
                {
                    return dongleReading;
                }
            }

            BatteryReading offline = new BatteryReading();
            offline.Id = "corsair-hs80";
            offline.Name = "Corsair HS80 RGB";
            offline.Kind = DeviceKind.Headset;
            offline.State = DeviceState.Offline;
            offline.Percent = -1;
            offline.Details = "Desligado";
            return offline;
        }

        private static BatteryReading QueryDongle(string path)
        {
            using (SafeFileHandle hw = NativeHid.CreateFile(path, NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE, NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE, IntPtr.Zero, NativeHid.OPEN_EXISTING, 0, IntPtr.Zero))
            using (SafeFileHandle hr = NativeHid.CreateFile(path, NativeHid.GENERIC_READ, NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE, IntPtr.Zero, NativeHid.OPEN_EXISTING, NativeHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero))
            {
                if (hw.IsInvalid || hr.IsInvalid)
                {
                    return null;
                }

                byte[] paired = BragiGetProperty(hw, hr, 0x08, 0x36);
                if (paired != null && paired[3] == 0x00 && paired[4] == 0x00 && paired[5] == 0x00)
                {
                    return null;
                }

                return ReadBatteryProperties(hw, hr, 0x09, false);
            }
        }

        private static BatteryReading QueryDevice(string path, byte target, bool isWired)
        {
            using (SafeFileHandle hw = NativeHid.CreateFile(path, NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE, NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE, IntPtr.Zero, NativeHid.OPEN_EXISTING, 0, IntPtr.Zero))
            using (SafeFileHandle hr = NativeHid.CreateFile(path, NativeHid.GENERIC_READ, NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE, IntPtr.Zero, NativeHid.OPEN_EXISTING, NativeHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero))
            {
                if (hw.IsInvalid || hr.IsInvalid)
                {
                    return null;
                }

                return ReadBatteryProperties(hw, hr, target, isWired);
            }
        }

        private static BatteryReading ReadBatteryProperties(SafeFileHandle hw, SafeFileHandle hr, byte target, bool isWired)
        {
            byte[] levelResp = BragiGetProperty(hw, hr, target, 0x0F);
            if (levelResp == null || levelResp[3] != 0x00)
            {
                return null;
            }

            ushort rawLevel = (ushort)(levelResp[4] | (levelResp[5] << 8));
            int pct = (int)Math.Round(rawLevel / 10.0);
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;

            bool charging = isWired;
            bool full = false;
            byte[] statusResp = BragiGetProperty(hw, hr, target, 0x10);
            if (statusResp != null && statusResp[3] == 0x00)
            {
                if (statusResp[4] == 1)
                {
                    charging = true;
                }
                else if (statusResp[4] == 3)
                {
                    charging = true;
                    full = true;
                }
            }

            BatteryReading reading = new BatteryReading();
            reading.Id = "corsair-hs80";
            reading.Name = "Corsair HS80 RGB";
            reading.Kind = DeviceKind.Headset;
            reading.State = DeviceState.Online;
            reading.Percent = pct;
            reading.IsCharging = charging;
            reading.IsFullyCharged = full;
            if (full)
            {
                reading.Details = "Carregado";
            }
            else if (charging)
            {
                reading.Details = isWired ? "USB / Carregando" : "Carregando";
            }
            else
            {
                reading.Details = "Sem fio";
            }
            return reading;
        }

        private static byte[] BragiGetProperty(SafeFileHandle hw, SafeFileHandle hr, byte target, byte propLo)
        {
            NativeHid.HidD_FlushQueue(hr);

            byte[] pkt = new byte[REPORT_LEN];
            pkt[0] = 0x02;
            pkt[1] = target;
            pkt[2] = 0x02;
            pkt[3] = propLo;
            pkt[4] = 0x00;

            uint written;
            if (!NativeHid.WriteFile(hw, pkt, REPORT_LEN, out written, IntPtr.Zero) || written != REPORT_LEN)
            {
                return null;
            }

            byte expectedSrc = (byte)(target - 0x08);
            for (int i = 0; i < 6; i++)
            {
                byte[] resp = NativeHid.ReadWithTimeout(hr, REPORT_LEN, 200);
                if (resp == null)
                {
                    break;
                }

                if (resp[0] == 0x01 && (resp[1] == expectedSrc || resp[1] == target) && resp[2] == 0x02)
                {
                    return resp;
                }
            }

            return null;
        }
    }

    internal static class BluetoothBatteryReader
    {
        private static readonly NativeHid.DEVPROPKEY DEVPKEY_Bluetooth_Battery = new NativeHid.DEVPROPKEY
        {
            fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
            pid = 2
        };

        private static readonly NativeHid.DEVPROPKEY DEVPKEY_Device_FriendlyName = new NativeHid.DEVPROPKEY
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };

        private static readonly NativeHid.DEVPROPKEY DEVPKEY_Device_DeviceDesc = new NativeHid.DEVPROPKEY
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 2
        };

        private static readonly NativeHid.DEVPROPKEY DEVPKEY_Device_ContainerId = new NativeHid.DEVPROPKEY
        {
            fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
            pid = 2
        };

        public static List<BatteryReading> ReadAll()
        {
            List<BatteryReading> list = new List<BatteryReading>();
            HashSet<Guid> seenContainers = new HashSet<Guid>();

            IntPtr infoSet = NativeHid.OpenAllPresentDevices();
            if (infoSet == IntPtr.Zero || infoSet == new IntPtr(-1))
            {
                return list;
            }

            try
            {
                NativeHid.SP_DEVINFO_DATA devInfo = new NativeHid.SP_DEVINFO_DATA();
                devInfo.cbSize = (uint)Marshal.SizeOf(typeof(NativeHid.SP_DEVINFO_DATA));
                uint idx = 0;

                while (NativeHid.EnumDeviceInfo(infoSet, idx++, ref devInfo))
                {
                    uint propType;
                    byte[] batBytes = NativeHid.GetDevicePropertyBytes(infoSet, ref devInfo, DEVPKEY_Bluetooth_Battery, out propType);
                    if (batBytes == null || propType != NativeHid.DEVPROP_TYPE_BYTE || batBytes.Length < 1)
                    {
                        continue;
                    }

                    int pct = batBytes[0];
                    if (pct > 100)
                    {
                        continue;
                    }

                    Guid containerId = Guid.Empty;
                    byte[] cidBytes = NativeHid.GetDevicePropertyBytes(infoSet, ref devInfo, DEVPKEY_Device_ContainerId, out propType);
                    if (cidBytes != null && propType == NativeHid.DEVPROP_TYPE_GUID && cidBytes.Length == 16)
                    {
                        containerId = new Guid(cidBytes);
                        if (containerId != Guid.Empty && seenContainers.Contains(containerId))
                        {
                            continue;
                        }
                    }

                    string name = GetStringProp(infoSet, ref devInfo, DEVPKEY_Device_FriendlyName);
                    if (string.IsNullOrEmpty(name))
                    {
                        name = GetStringProp(infoSet, ref devInfo, DEVPKEY_Device_DeviceDesc);
                    }
                    if (string.IsNullOrEmpty(name))
                    {
                        name = "Dispositivo Bluetooth";
                    }

                    if (containerId != Guid.Empty)
                    {
                        seenContainers.Add(containerId);
                    }

                    BatteryReading reading = new BatteryReading();
                    reading.Id = containerId != Guid.Empty ? ("bt-" + containerId.ToString("N")) : ("bt-" + name);
                    reading.Name = name;
                    reading.Kind = DeviceKind.Bluetooth;
                    reading.State = DeviceState.Online;
                    reading.Percent = pct;
                    reading.Details = "Bluetooth";
                    list.Add(reading);
                }
            }
            finally
            {
                NativeHid.CloseDeviceInfoSet(infoSet);
            }

            return list;
        }

        private static string GetStringProp(IntPtr infoSet, ref NativeHid.SP_DEVINFO_DATA devInfo, NativeHid.DEVPROPKEY key)
        {
            uint propType;
            byte[] bytes = NativeHid.GetDevicePropertyBytes(infoSet, ref devInfo, key, out propType);
            if (bytes != null && propType == NativeHid.DEVPROP_TYPE_STRING && bytes.Length >= 2)
            {
                return Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
            }
            return null;
        }
    }

    internal sealed class RenderedIcon
    {
        public Icon Icon;
        public IntPtr Handle;
    }

    internal static class TrayIconRenderer
    {
        public static RenderedIcon Render(BatteryReading reading)
        {
            Color accent = ResolveAccentColor(reading);
            Color textColor = reading.State == DeviceState.Online
                ? Color.White
                : Color.FromArgb(190, 198, 208);

            string topText;
            if (reading.State == DeviceState.Online && reading.Percent >= 0)
            {
                topText = reading.Percent.ToString();
            }
            else if (reading.State == DeviceState.Sleeping)
            {
                topText = "Zz";
            }
            else
            {
                topText = "--";
            }

            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                using (GraphicsPath bgPath = CreateRoundedRect(new Rectangle(1, 1, 29, 29), 6))
                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(245, 24, 26, 33)))
                using (Pen borderPen = new Pen(accent, 2f))
                {
                    g.FillPath(bgBrush, bgPath);
                    g.DrawPath(borderPen, bgPath);
                }

                float fontSize = topText.Length >= 3 ? 12.5f : 15.5f;
                using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush textBrush = new SolidBrush(textColor))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString(topText, font, textBrush, new RectangleF(0f, 1f, 32f, 19f), sf);
                }

                DrawKindGlyph(g, reading.Kind, accent);
                DrawMiniBatteryBar(g, reading, accent);

                IntPtr hIcon = bmp.GetHicon();
                RenderedIcon result = new RenderedIcon();
                result.Handle = hIcon;
                result.Icon = Icon.FromHandle(hIcon);
                return result;
            }
        }

        private static Color ResolveAccentColor(BatteryReading reading)
        {
            if (reading.State == DeviceState.Offline)
            {
                return Color.FromArgb(95, 102, 114);
            }
            if (reading.State == DeviceState.Sleeping)
            {
                return Color.FromArgb(128, 140, 154);
            }
            if (reading.IsCharging)
            {
                return Color.FromArgb(34, 184, 207);
            }
            if (reading.Percent >= 50)
            {
                return Color.FromArgb(46, 204, 113);
            }
            if (reading.Percent >= 21)
            {
                return Color.FromArgb(241, 196, 15);
            }
            return Color.FromArgb(231, 76, 60);
        }

        private static void DrawKindGlyph(Graphics g, DeviceKind kind, Color color)
        {
            using (Pen p = new Pen(color, 1.4f))
            using (SolidBrush b = new SolidBrush(color))
            {
                if (kind == DeviceKind.Mouse)
                {
                    g.DrawEllipse(p, 4.5f, 20.5f, 5.5f, 7.5f);
                    g.DrawLine(p, 7.25f, 21f, 7.25f, 23.8f);
                }
                else if (kind == DeviceKind.Headset)
                {
                    g.DrawArc(p, 4.2f, 21f, 6.6f, 6f, 180f, 180f);
                    g.FillRectangle(b, 3.8f, 24f, 1.8f, 3.8f);
                    g.FillRectangle(b, 9.4f, 24f, 1.8f, 3.8f);
                }
                else
                {
                    g.DrawLine(p, 7f, 20.5f, 7f, 28f);
                    g.DrawLine(p, 7f, 20.8f, 10f, 22.8f);
                    g.DrawLine(p, 10f, 22.8f, 5f, 25.6f);
                    g.DrawLine(p, 5f, 22.8f, 10f, 25.8f);
                    g.DrawLine(p, 10f, 25.8f, 7f, 27.8f);
                }
            }
        }

        private static void DrawMiniBatteryBar(Graphics g, BatteryReading reading, Color accent)
        {
            Rectangle barFrame = new Rectangle(13, 22, 13, 5);
            using (Pen framePen = new Pen(Color.FromArgb(180, 190, 204), 1f))
            {
                g.DrawRectangle(framePen, barFrame);
            }
            using (SolidBrush tipBrush = new SolidBrush(Color.FromArgb(180, 190, 204)))
            {
                g.FillRectangle(tipBrush, 27, 23, 1, 3);
            }

            if (reading.Percent > 0 && reading.State != DeviceState.Offline)
            {
                int fillWidth = (int)Math.Round(11.0 * reading.Percent / 100.0);
                if (fillWidth < 1) fillWidth = 1;
                if (fillWidth > 11) fillWidth = 11;
                using (SolidBrush fillBrush = new SolidBrush(accent))
                {
                    g.FillRectangle(fillBrush, 14, 23, fillWidth, 3);
                }
            }

            if (reading.IsCharging)
            {
                PointF[] bolt = new PointF[]
                {
                    new PointF(21.5f, 20f),
                    new PointF(18.5f, 24f),
                    new PointF(20.5f, 24f),
                    new PointF(18.5f, 28.5f),
                    new PointF(22.5f, 24f),
                    new PointF(20.3f, 24f)
                };
                using (SolidBrush boltBrush = new SolidBrush(Color.FromArgb(255, 220, 70)))
                {
                    g.FillPolygon(boltBrush, bolt);
                }
            }
        }

        private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class TrayEntry : IDisposable
    {
        public string Id;
        public NotifyIcon NotifyIcon;
        public ContextMenuStrip ContextMenu;
        public Icon CurrentIcon;
        public IntPtr CurrentHIcon;
        public string LastIconKey;
        public bool LowBatteryAlerted;

        public void Dispose()
        {
            if (NotifyIcon != null)
            {
                NotifyIcon.Visible = false;
                NotifyIcon.Dispose();
                NotifyIcon = null;
            }
            if (ContextMenu != null)
            {
                ContextMenu.Dispose();
                ContextMenu = null;
            }
            if (CurrentIcon != null)
            {
                CurrentIcon.Dispose();
                CurrentIcon = null;
            }
            if (CurrentHIcon != IntPtr.Zero)
            {
                NativeHid.DestroyIcon(CurrentHIcon);
                CurrentHIcon = IntPtr.Zero;
            }
        }
    }

    internal sealed class TrayAppContext : ApplicationContext
    {
        private const string AppName = "PeripheralBatteryTray";
        private const string RegRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RegSettingsKey = @"Software\PeripheralBatteryTray";
        private const string RegNotifyIconKey = @"Control Panel\NotifyIconSettings";
        private const int PollIntervalMs = 10000;

        private readonly AtkMouseReader _atkReader = new AtkMouseReader();
        private readonly CorsairHs80Reader _hs80Reader = new CorsairHs80Reader();
        private readonly Dictionary<string, TrayEntry> _entries = new Dictionary<string, TrayEntry>();
        private readonly List<BatteryReading> _latestReadings = new List<BatteryReading>();
        private readonly Control _uiMarshal;
        private readonly System.Threading.Timer _pollTimer;

        private int _pollingFlag = 0;
        private bool _hideOffline = true;
        private bool _pinToTaskbar = true;
        private bool _disposed = false;

        public TrayAppContext()
        {
            _uiMarshal = new Control();
            _uiMarshal.CreateControl();
            IntPtr marshalHandle = _uiMarshal.Handle;
            if (marshalHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create UI marshal handle.");
            }

            _hideOffline = LoadHideOfflineSetting();
            _pinToTaskbar = LoadPinToTaskbarSetting();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            _pollTimer = new System.Threading.Timer(OnTimerTick, null, 0, PollIntervalMs);
        }

        public static List<BatteryReading> PollOnce(AtkMouseReader atk, CorsairHs80Reader hs80)
        {
            List<BatteryReading> list = new List<BatteryReading>();

            BatteryReading mouse = atk.Read();
            if (mouse != null)
            {
                list.Add(mouse);
            }

            BatteryReading headset = hs80.Read();
            if (headset != null)
            {
                list.Add(headset);
            }

            List<BatteryReading> bt = BluetoothBatteryReader.ReadAll();
            for (int i = 0; i < bt.Count; i++)
            {
                list.Add(bt[i]);
            }

            return list;
        }

        private void OnTimerTick(object state)
        {
            TriggerPollAsync();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                TriggerPollAsync();
            }
        }

        private void TriggerPollAsync()
        {
            if (_disposed || Interlocked.CompareExchange(ref _pollingFlag, 1, 0) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<BatteryReading> readings = PollOnce(_atkReader, _hs80Reader);
                    if (!_disposed && _uiMarshal.IsHandleCreated)
                    {
                        _uiMarshal.BeginInvoke(new Action<List<BatteryReading>>(ApplyReadings), readings);
                    }
                }
                catch (Exception ex)
                {
                    Program.LogError("TriggerPollAsync", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _pollingFlag, 0);
                }
            });
        }

        private ContextMenuStrip CreateContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Opening += OnMenuOpening;
            return menu;
        }

        private void ApplyReadings(List<BatteryReading> readings)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _latestReadings.Clear();
                _latestReadings.AddRange(readings);

                HashSet<string> activeIds = new HashSet<string>();
                bool createdNewIcon = false;

                for (int i = 0; i < readings.Count; i++)
                {
                    BatteryReading r = readings[i];
                    if (_hideOffline && r.State == DeviceState.Offline)
                    {
                        continue;
                    }

                    activeIds.Add(r.Id);

                    TrayEntry entry;
                    if (!_entries.TryGetValue(r.Id, out entry))
                    {
                        entry = new TrayEntry();
                        entry.Id = r.Id;
                        entry.ContextMenu = CreateContextMenu();
                        entry.NotifyIcon = new NotifyIcon();
                        entry.NotifyIcon.ContextMenuStrip = entry.ContextMenu;
                        entry.NotifyIcon.DoubleClick += delegate { TriggerPollAsync(); };
                        _entries[r.Id] = entry;
                        createdNewIcon = true;
                    }

                    string iconKey = string.Format("{0}|{1}|{2}|{3}", r.State, r.Percent, r.IsCharging, r.IsFullyCharged);
                    if (entry.LastIconKey != iconKey)
                    {
                        RenderedIcon rendered = TrayIconRenderer.Render(r);
                        Icon prevIcon = entry.CurrentIcon;
                        IntPtr prevHandle = entry.CurrentHIcon;

                        entry.CurrentIcon = rendered.Icon;
                        entry.CurrentHIcon = rendered.Handle;
                        entry.LastIconKey = iconKey;
                        entry.NotifyIcon.Icon = rendered.Icon;

                        if (prevIcon != null)
                        {
                            prevIcon.Dispose();
                        }
                        if (prevHandle != IntPtr.Zero)
                        {
                            NativeHid.DestroyIcon(prevHandle);
                        }
                    }

                    entry.NotifyIcon.Text = BuildTooltipText(r);
                    if (!entry.NotifyIcon.Visible)
                    {
                        entry.NotifyIcon.Visible = true;
                    }

                    if (r.State == DeviceState.Online && r.Percent >= 0)
                    {
                        if (r.IsCharging || r.Percent >= 25)
                        {
                            entry.LowBatteryAlerted = false;
                        }
                        else if (r.Percent <= 15 && !entry.LowBatteryAlerted)
                        {
                            entry.LowBatteryAlerted = true;
                            entry.NotifyIcon.ShowBalloonTip(
                                5000,
                                r.Name,
                                string.Format("Bateria baixa: {0}% restantes.", r.Percent),
                                ToolTipIcon.Warning);
                        }
                    }
                }

                List<string> toRemove = new List<string>();
                foreach (string existingId in _entries.Keys)
                {
                    if (!activeIds.Contains(existingId))
                    {
                        toRemove.Add(existingId);
                    }
                }

                for (int i = 0; i < toRemove.Count; i++)
                {
                    string id = toRemove[i];
                    _entries[id].Dispose();
                    _entries.Remove(id);
                }

                if (createdNewIcon && _pinToTaskbar)
                {
                    SchedulePromoteOwnTrayIcons();
                }
            }
            catch (Exception ex)
            {
                Program.LogError("ApplyReadings", ex);
            }
        }

        private static string BuildTooltipText(BatteryReading r)
        {
            string full;
            if (r.State == DeviceState.Online && r.Percent >= 0)
            {
                full = string.Format("{0}: {1}% ({2})", r.Name, r.Percent, r.Details);
            }
            else
            {
                full = string.Format("{0}: {1}", r.Name, r.Details);
            }

            if (full.Length > 63)
            {
                return full.Substring(0, 60) + "...";
            }
            return full;
        }

        private void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            ContextMenuStrip menu = sender as ContextMenuStrip;
            if (menu == null)
            {
                return;
            }

            menu.Items.Clear();

            if (_latestReadings.Count == 0)
            {
                ToolStripMenuItem emptyItem = new ToolStripMenuItem("Nenhum periférico compatível");
                emptyItem.Enabled = false;
                menu.Items.Add(emptyItem);
            }
            else
            {
                for (int i = 0; i < _latestReadings.Count; i++)
                {
                    BatteryReading r = _latestReadings[i];
                    string summary = r.State == DeviceState.Online && r.Percent >= 0
                        ? string.Format("{0} — {1}% ({2})", r.Name, r.Percent, r.Details)
                        : string.Format("{0} — {1}", r.Name, r.Details);
                    ToolStripMenuItem item = new ToolStripMenuItem(summary);
                    item.Enabled = false;
                    menu.Items.Add(item);
                }
            }

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem refreshItem = new ToolStripMenuItem("Atualizar agora");
            refreshItem.Click += delegate { TriggerPollAsync(); };
            menu.Items.Add(refreshItem);

            ToolStripMenuItem hideOfflineItem = new ToolStripMenuItem("Ocultar periféricos desligados");
            hideOfflineItem.CheckOnClick = true;
            hideOfflineItem.Checked = _hideOffline;
            hideOfflineItem.Click += OnToggleHideOffline;
            menu.Items.Add(hideOfflineItem);

            ToolStripMenuItem pinTaskbarItem = new ToolStripMenuItem("Fixar ícones na barra de tarefas");
            pinTaskbarItem.CheckOnClick = true;
            pinTaskbarItem.Checked = _pinToTaskbar;
            pinTaskbarItem.Click += OnTogglePinToTaskbar;
            menu.Items.Add(pinTaskbarItem);

            ToolStripMenuItem startupItem = new ToolStripMenuItem("Iniciar com o Windows");
            startupItem.CheckOnClick = true;
            startupItem.Checked = IsStartupEnabled();
            startupItem.Click += OnToggleStartup;
            menu.Items.Add(startupItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Sair");
            exitItem.Click += delegate { ExitApp(); };
            menu.Items.Add(exitItem);
        }

        private void OnToggleHideOffline(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null)
            {
                return;
            }
            _hideOffline = item.Checked;
            SaveHideOfflineSetting(_hideOffline);
            ApplyReadings(new List<BatteryReading>(_latestReadings));
        }

        private void OnTogglePinToTaskbar(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null)
            {
                return;
            }
            _pinToTaskbar = item.Checked;
            SavePinToTaskbarSetting(_pinToTaskbar);
            SetOwnTrayIconsPromoted(_pinToTaskbar);
        }

        private void OnToggleStartup(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null)
            {
                return;
            }
            SetStartupEnabled(item.Checked);
            item.Checked = IsStartupEnabled();
        }

        private static bool LoadHideOfflineSetting()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegSettingsKey, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("HideOffline");
                        if (val != null)
                        {
                            return Convert.ToInt32(val) != 0;
                        }
                    }
                }
            }
            catch
            {
            }
            return true;
        }

        private static void SaveHideOfflineSetting(bool hideOffline)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegSettingsKey))
                {
                    if (key != null)
                    {
                        key.SetValue("HideOffline", hideOffline ? 1 : 0, RegistryValueKind.DWord);
                    }
                }
            }
            catch
            {
            }
        }

        private static bool LoadPinToTaskbarSetting()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegSettingsKey, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("PinToTaskbar");
                        if (val != null)
                        {
                            return Convert.ToInt32(val) != 0;
                        }
                    }
                }
            }
            catch
            {
            }
            return true;
        }

        private static void SavePinToTaskbarSetting(bool pinToTaskbar)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegSettingsKey))
                {
                    if (key != null)
                    {
                        key.SetValue("PinToTaskbar", pinToTaskbar ? 1 : 0, RegistryValueKind.DWord);
                    }
                }
            }
            catch
            {
            }
        }

        private void SchedulePromoteOwnTrayIcons()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Thread.Sleep(700);
                    if (_disposed || !_pinToTaskbar)
                    {
                        return;
                    }
                    SetOwnTrayIconsPromoted(true);

                    Thread.Sleep(1500);
                    if (_disposed || !_pinToTaskbar)
                    {
                        return;
                    }
                    SetOwnTrayIconsPromoted(true);
                }
                catch (Exception ex)
                {
                    Program.LogError("SchedulePromoteOwnTrayIcons", ex);
                }
            });
        }

        private static void SetOwnTrayIconsPromoted(bool promoted)
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                int desired = promoted ? 1 : 0;

                using (RegistryKey parent = Registry.CurrentUser.OpenSubKey(RegNotifyIconKey, false))
                {
                    if (parent == null)
                    {
                        return;
                    }

                    string[] subNames = parent.GetSubKeyNames();
                    for (int i = 0; i < subNames.Length; i++)
                    {
                        using (RegistryKey sub = parent.OpenSubKey(subNames[i], true))
                        {
                            if (sub == null)
                            {
                                continue;
                            }

                            object pathObj = sub.GetValue("ExecutablePath");
                            string pathStr = pathObj as string;
                            if (string.IsNullOrEmpty(pathStr) || !string.Equals(pathStr, exePath, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            object currentPromoted = sub.GetValue("IsPromoted");
                            if (currentPromoted == null || Convert.ToInt32(currentPromoted) != desired)
                            {
                                sub.SetValue("IsPromoted", desired, RegistryValueKind.DWord);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogError("SetOwnTrayIconsPromoted", ex);
            }
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegRunKey, false))
                {
                    if (key != null)
                    {
                        return key.GetValue(AppName) != null;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static void SetStartupEnabled(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegRunKey))
                {
                    if (key == null)
                    {
                        return;
                    }
                    if (enable)
                    {
                        string exePath = Process.GetCurrentProcess().MainModule.FileName;
                        key.SetValue(AppName, "\"" + exePath + "\"", RegistryValueKind.String);
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch
            {
            }
        }

        private void ExitApp()
        {
            DisposeResources();
            ExitThread();
        }

        private void DisposeResources()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            SystemEvents.PowerModeChanged -= OnPowerModeChanged;

            if (_pollTimer != null)
            {
                _pollTimer.Dispose();
            }

            foreach (TrayEntry entry in _entries.Values)
            {
                entry.Dispose();
            }
            _entries.Clear();

            if (_uiMarshal != null)
            {
                _uiMarshal.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeResources();
            }
            base.Dispose(disposing);
        }
    }

    internal static class Program
    {
        private static readonly object LogLock = new object();

        public static void LogError(string context, Exception ex)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
                string msg = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] [{1}] {2}{3}",
                    DateTime.Now,
                    context,
                    ex != null ? ex.ToString() : "(null exception)",
                    Environment.NewLine);
                lock (LogLock)
                {
                    File.AppendAllText(logPath, msg, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length >= 2 && string.Equals(args[0], "--probe", StringComparison.OrdinalIgnoreCase))
            {
                RunProbe(args[1]);
                return;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                LogError("UIThread", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                LogError("AppDomain", e.ExceptionObject as Exception);
            };

            bool createdNew;
            using (Mutex mutex = new Mutex(true, @"Local\PeripheralBatteryTray_SingleInstance_v1", out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (TrayAppContext ctx = new TrayAppContext())
                    {
                        Application.Run(ctx);
                    }
                }
                catch (Exception ex)
                {
                    LogError("Main", ex);
                }
            }
        }

        private static void RunProbe(string outputPath)
        {
            AtkMouseReader atk = new AtkMouseReader();
            CorsairHs80Reader hs80 = new CorsairHs80Reader();
            List<BatteryReading> readings = TrayAppContext.PollOnce(atk, hs80);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("PeripheralBatteryTray Probe - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            for (int i = 0; i < readings.Count; i++)
            {
                BatteryReading r = readings[i];
                sb.AppendLine(string.Format(
                    "Id={0} | Name={1} | Kind={2} | State={3} | Percent={4} | Charging={5} | Full={6} | VoltageMv={7} | Details={8}",
                    r.Id, r.Name, r.Kind, r.State, r.Percent, r.IsCharging, r.IsFullyCharged, r.VoltageMv, r.Details));
            }

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }
    }
}
