using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AtkQuickSwitch
{
    public static class Logger
    {
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "atk.log");

        public static void Log(string msg)
        {
            try
            {
                File.AppendAllText(LogPath, string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}", DateTime.Now, msg, Environment.NewLine));
            }
            catch { }
        }
    }

    public static class AtkHid
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SP_DEVICE_INTERFACE_DETAIL_DATA
        {
            public int cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string DevicePath;
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
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, int MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, ref SP_DEVICE_INTERFACE_DETAIL_DATA DeviceInterfaceDetailData, int DeviceInterfaceDetailDataSize, out int RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);

        private static string _cachedPath;

        public static string FindDevicePath(bool forceRefresh = false)
        {
            if (!forceRefresh && !string.IsNullOrEmpty(_cachedPath))
                return _cachedPath;

            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr hDevInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
            try
            {
                int index = 0;
                SP_DEVICE_INTERFACE_DATA devData = new SP_DEVICE_INTERFACE_DATA();
                devData.cbSize = Marshal.SizeOf(devData);
                while (SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, index, ref devData))
                {
                    index++;
                    int reqSize;
                    SP_DEVICE_INTERFACE_DETAIL_DATA detail = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                    detail.cbSize = IntPtr.Size == 8 ? 8 : 6;
                    if (SetupDiGetDeviceInterfaceDetail(hDevInfo, ref devData, ref detail, 512, out reqSize, IntPtr.Zero))
                    {
                        using (var h = CreateFile(detail.DevicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                        {
                            if (!h.IsInvalid)
                            {
                                HIDD_ATTRIBUTES attr = new HIDD_ATTRIBUTES();
                                attr.Size = Marshal.SizeOf(attr);
                                if (HidD_GetAttributes(h, ref attr) && attr.VendorID == 14139) // 0x373B (ATK)
                                {
                                    IntPtr ppData;
                                    if (HidD_GetPreparsedData(h, out ppData))
                                    {
                                        HIDP_CAPS caps;
                                        HidP_GetCaps(ppData, out caps);
                                        HidD_FreePreparsedData(ppData);
                                        if (caps.UsagePage == 65282 && caps.Usage == 2 && caps.OutputReportByteLength == 17)
                                        {
                                            _cachedPath = detail.DevicePath;
                                            return _cachedPath;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(hDevInfo);
            }
            _cachedPath = null;
            return null;
        }

        public static byte[] Transact(byte[] tx15, int timeoutMs = 450)
        {
            string path = FindDevicePath(false) ?? FindDevicePath(true);
            if (string.IsNullOrEmpty(path)) return null;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var h = CreateFile(path, 0xC0000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero))
                    {
                        if (h.IsInvalid)
                        {
                            path = FindDevicePath(true);
                            if (string.IsNullOrEmpty(path)) return null;
                            continue;
                        }
                        using (var fs = new FileStream(h, FileAccess.ReadWrite, 17, true))
                        {
                            byte[] outBuf = new byte[17];
                            outBuf[0] = 8; // Report ID 8
                            int sum = 8;
                            int count = Math.Min(tx15.Length, 15);
                            for (int i = 0; i < count; i++)
                            {
                                outBuf[i + 1] = tx15[i];
                                sum += tx15[i];
                            }
                            outBuf[16] = (byte)((85 - (sum & 0xFF)) & 0xFF);

                            byte[] inBuf = new byte[17];
                            var readTask = fs.ReadAsync(inBuf, 0, 17);
                            fs.Write(outBuf, 0, 17);
                            fs.Flush();

                            int elapsed = 0;
                            while (elapsed < timeoutMs)
                            {
                                if (readTask.Wait(60))
                                {
                                    if (inBuf[0] == 8 && inBuf[1] == tx15[0])
                                        return inBuf;
                                    readTask = fs.ReadAsync(inBuf, 0, 17);
                                }
                                elapsed += 60;
                            }
                        }
                    }
                }
                catch
                {
                    path = FindDevicePath(true);
                }
            }
            return null;
        }

        public static byte CrcByte(byte val)
        {
            return (byte)((85 - val) & 0xFF);
        }

        public static int DecodePaw3950UltraDpi(byte xDpi, byte dpiEx)
        {
            int highBits = (dpiEx & 12) >> 2;
            int raw = xDpi | (highBits << 8);
            bool above10k = (dpiEx & 2) != 0;
            int dpi = above10k ? (raw * 50 + 10050) : ((raw + 1) * 10);
            if ((dpiEx & 1) != 0) dpi *= 2;
            return dpi;
        }

        public static int RateByteToHz(byte rateByte)
        {
            switch (rateByte)
            {
                case 0x08: return 125;
                case 0x04: return 250;
                case 0x02: return 500;
                case 0x01: return 1000;
                case 0x10: return 2000;
                case 0x20: return 4000;
                case 0x40: return 8000;
                default: return 1000;
            }
        }

        public static byte? HzToRateByte(int hz)
        {
            switch (hz)
            {
                case 125: return 0x08;
                case 250: return 0x04;
                case 500: return 0x02;
                case 1000: return 0x01;
                case 2000: return 0x10;
                case 4000: return 0x20;
                case 8000: return 0x40;
                default: return null;
            }
        }
    }

    public class MouseState
    {
        public byte RateByte = 0x40; // 8000 Hz
        public byte MaxDpiStages = 4;
        public byte CurrentDpiStage = 2; // 0-based: stage 2 = 1600 DPI, stage 3 = 3200 DPI
        public byte Bhop = 0;
        public byte ButtonOpMode = 0;
        public int[] StageDpiValues = new int[] { 400, 800, 1600, 3200, 3200, 3200, 3200, 3200 };
        public int Stage800Index = 1;
        public int Stage1600Index = 2;
        public int Stage3200Index = 3;
        public bool IsOnline = true;

        public int CurrentDpiValue
        {
            get
            {
                if (CurrentDpiStage < StageDpiValues.Length)
                    return StageDpiValues[CurrentDpiStage];
                if (CurrentDpiStage == Stage3200Index) return 3200;
                if (CurrentDpiStage == Stage800Index) return 800;
                return 1600;
            }
        }

        public int CurrentPollingHz
        {
            get { return AtkHid.RateByteToHz(RateByte); }
        }
    }

    public class OsdOverlayForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const int SW_SHOWNOACTIVATE = 4;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private System.Windows.Forms.Timer _hideTimer;
        private System.Windows.Forms.Timer _fadeTimer;
        private string _titleText = "ATK X1 ULTIMATE 2.0";
        private int _dpiValue = 1600;
        private int _pollingHz = 8000;
        private string _changedField = ""; // "DPI", "RATE", "STATUS", "OFFLINE"
        private string _statusMessage = "";

        public OsdOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(360, 108);
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(18, 18, 24);
            Opacity = 0.95;

            UpdateRegion();

            _hideTimer = new System.Windows.Forms.Timer { Interval = 1800 };
            _hideTimer.Tick += (s, e) =>
            {
                _hideTimer.Stop();
                _fadeTimer.Start();
            };

            _fadeTimer = new System.Windows.Forms.Timer { Interval = 25 };
            _fadeTimer.Tick += (s, e) =>
            {
                if (Opacity > 0.12)
                {
                    Opacity -= 0.10;
                }
                else
                {
                    _fadeTimer.Stop();
                    Hide();
                }
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000008 | 0x00000080 | 0x00000020 | 0x08000000;
                return cp;
            }
        }

        private void UpdateRegion()
        {
            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width, Height), 14))
            {
                Region = new Region(path);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
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

        public void ShowNotification(int dpi, int pollingHz, string changedField, string statusMessage = "")
        {
            _dpiValue = dpi;
            _pollingHz = pollingHz;
            _changedField = changedField;
            _statusMessage = statusMessage;

            Screen screen = Screen.PrimaryScreen;
            Location = new Point(
                screen.Bounds.Left + (screen.Bounds.Width - Width) / 2,
                screen.Bounds.Top + 36
            );

            _fadeTimer.Stop();
            _hideTimer.Stop();
            Opacity = 0.96;
            Invalidate();
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            SetWindowPos(Handle, HWND_TOPMOST, Location.X, Location.Y, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _hideTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var bgBrush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(24, 25, 34), Color.FromArgb(14, 15, 20), 90f))
            {
                g.FillRectangle(bgBrush, ClientRectangle);
            }

            Color accentColor = _pollingHz >= 8000 ? Color.FromArgb(255, 85, 35) : Color.FromArgb(0, 195, 255);
            using (GraphicsPath borderPath = RoundedRect(new Rectangle(1, 1, Width - 3, Height - 3), 13))
            using (Pen borderPen = new Pen(Color.FromArgb(140, accentColor), 1.6f))
            {
                g.DrawPath(borderPen, borderPath);
            }

            using (Font headerFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold))
            using (Brush headerBrush = new SolidBrush(Color.FromArgb(160, 170, 190)))
            {
                g.DrawString(_titleText, headerFont, headerBrush, 16, 10);
            }

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                using (Font statusFont = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold))
                using (Brush statusBrush = new SolidBrush(Color.FromArgb(255, 190, 80)))
                {
                    g.DrawString(_statusMessage, statusFont, statusBrush, 16, 48);
                }
                return;
            }

            Rectangle dpiBox = new Rectangle(14, 34, 158, 60);
            Rectangle rateBox = new Rectangle(188, 34, 158, 60);

            bool dpiActive = _changedField == "DPI";
            bool rateActive = _changedField == "RATE";

            Color dpiHighlight = _dpiValue >= 3200
                ? Color.FromArgb(180, 90, 255)
                : (_dpiValue >= 1600 ? Color.FromArgb(0, 210, 140) : Color.FromArgb(40, 175, 255));
            Color rateHighlight = _pollingHz >= 8000 ? Color.FromArgb(255, 85, 35) : Color.FromArgb(45, 155, 255);

            DrawMetricCard(g, dpiBox, "DPI ATUAL", _dpiValue + " DPI", dpiHighlight, dpiActive);
            string rateText = _pollingHz >= 1000 ? (_pollingHz / 1000) + "K (" + _pollingHz + "Hz)" : _pollingHz + " Hz";
            DrawMetricCard(g, rateBox, "POLLING RATE", rateText, rateHighlight, rateActive);
        }

        private void DrawMetricCard(Graphics g, Rectangle rect, string label, string value, Color accent, bool isChanged)
        {
            using (GraphicsPath path = RoundedRect(rect, 10))
            {
                Color fill = isChanged ? Color.FromArgb(42, accent) : Color.FromArgb(28, 30, 42);
                using (Brush b = new SolidBrush(fill))
                {
                    g.FillPath(b, path);
                }
                using (Pen p = new Pen(isChanged ? accent : Color.FromArgb(55, 60, 80), isChanged ? 1.8f : 1f))
                {
                    g.DrawPath(p, path);
                }
            }

            using (Font lblFont = new Font("Segoe UI", 7.5f, FontStyle.Bold))
            using (Brush lblBrush = new SolidBrush(isChanged ? accent : Color.FromArgb(140, 148, 168)))
            {
                g.DrawString(label, lblFont, lblBrush, rect.X + 10, rect.Y + 7);
            }

            using (Font valFont = new Font("Segoe UI", 13.5f, FontStyle.Bold))
            using (Brush valBrush = new SolidBrush(Color.White))
            {
                g.DrawString(value, valFont, valBrush, rect.X + 8, rect.Y + 25);
            }
        }
    }

    public class HotkeyApplicationContext : ApplicationContext
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, byte[] pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_NOREPEAT = 0x4000;

        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIM_TYPEKEYBOARD = 1;
        private const ushort RI_KEY_BREAK = 0x0001;
        private const int WM_HOTKEY = 0x0312;
        private const int WM_INPUT = 0x00FF;

        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const ushort VK_F10 = 0x79;
        private const ushort VK_F11 = 0x7A;
        private const ushort VK_F12 = 0x7B;

        private const int HOTKEY_ID_DPI = 9001;
        private const int HOTKEY_ID_RATE = 9002;
        private const int HOTKEY_ID_STATUS = 9003;

        private NotifyIcon _trayIcon;
        private OsdOverlayForm _osd;
        private HotkeyMessageWindow _msgWindow;
        private MouseState _state = new MouseState();
        private ToolStripMenuItem _statusMenuItem;
        private ToolStripMenuItem _dpi800MenuItem;
        private ToolStripMenuItem _dpi1600MenuItem;
        private ToolStripMenuItem _dpi3200MenuItem;
        private System.Windows.Forms.Timer _lolWatchTimer;
        private bool _lolWasRunning = false;
        private bool _autoSwitchedForLol = false;
        private bool _dpiTableLoaded = false;

        private Stopwatch _inputWatch = Stopwatch.StartNew();
        private long _lastDpiMs = -1000;
        private long _lastRateMs = -1000;
        private long _lastStatusMs = -1000;
        private string _lastDpiSource = "";
        private string _lastRateSource = "";
        private string _lastStatusSource = "";
        private bool _f10Down = false;
        private bool _f11Down = false;
        private bool _f12Down = false;

        private class HotkeyMessageWindow : NativeWindow, IDisposable
        {
            private HotkeyApplicationContext _ctx;
            public HotkeyMessageWindow(HotkeyApplicationContext ctx)
            {
                _ctx = ctx;
                var cp = new CreateParams { Caption = "AtkQuickSwitch_MsgWnd" };
                CreateHandle(cp);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY)
                {
                    int id = m.WParam.ToInt32();
                    _ctx.OnHotkeyPressed(id, "WM_HOTKEY");
                }
                else if (m.Msg == WM_INPUT)
                {
                    _ctx.OnRawInput(m.LParam);
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                DestroyHandle();
            }
        }

        public HotkeyApplicationContext()
        {
            _osd = new OsdOverlayForm();
            _msgWindow = new HotkeyMessageWindow(this);

            bool okDpi = RegisterHotKey(_msgWindow.Handle, HOTKEY_ID_DPI, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.F10);
            bool okRate = RegisterHotKey(_msgWindow.Handle, HOTKEY_ID_RATE, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.F11);
            bool okStat = RegisterHotKey(_msgWindow.Handle, HOTKEY_ID_STATUS, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.F12);

            RAWINPUTDEVICE[] rid = new RAWINPUTDEVICE[1];
            rid[0].usUsagePage = 0x01; // Generic Desktop
            rid[0].usUsage = 0x06;     // Keyboard
            rid[0].dwFlags = RIDEV_INPUTSINK;
            rid[0].hwndTarget = _msgWindow.Handle;
            bool okRaw = RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));

            Logger.Log(string.Format("Started! Hotkeys registered: DPI(Ctrl+Alt+F10)={0}, Rate(Ctrl+Alt+F11)={1}, Status(Ctrl+Alt+F12)={2}, RawInput={3}", okDpi, okRate, okStat, okRaw));

            var menu = new ContextMenuStrip();
            _statusMenuItem = new ToolStripMenuItem("ATK X1 Ultimate 2.0") { Enabled = false };
            menu.Items.Add(_statusMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Alternar DPI (800 -> 1600 -> 3200) [Ctrl+Alt+F10]", null, (s, e) => ToggleDpi());
            _dpi800MenuItem = new ToolStripMenuItem("Definir DPI: 800", null, (s, e) => SetDpiValue(800));
            _dpi1600MenuItem = new ToolStripMenuItem("Definir DPI: 1600", null, (s, e) => SetDpiValue(1600));
            _dpi3200MenuItem = new ToolStripMenuItem("Definir DPI: 3200", null, (s, e) => SetDpiValue(3200));
            menu.Items.Add(_dpi800MenuItem);
            menu.Items.Add(_dpi1600MenuItem);
            menu.Items.Add(_dpi3200MenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Alternar Polling Rate (1K ↔ 8K) [Ctrl+Alt+F11]", null, (s, e) => TogglePollingRate());
            menu.Items.Add("Mostrar Status na Tela [Ctrl+Alt+F12]", null, (s, e) => ShowCurrentStatus());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, (s, e) => ExitThread());

            _trayIcon = new NotifyIcon
            {
                Icon = CreateTrayIcon(),
                Text = "ATK X1 Ultimate 2.0 - Atalhos DPI & Polling Rate",
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => ShowCurrentStatus();

            bool online = RefreshStateFromMouse(true);
            Logger.Log(string.Format("Initial mouse state: Online={0}, DPI={1} (Stage {2}), PollingRate={3}Hz", online, _state.CurrentDpiValue, _state.CurrentDpiStage, _state.CurrentPollingHz));
            UpdateTrayTooltip();
            _osd.ShowNotification(_state.CurrentDpiValue, _state.CurrentPollingHz, "STATUS");

            _lolWatchTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _lolWatchTimer.Tick += (s, e) => CheckLolProcess();
            _lolWatchTimer.Start();
        }

        private void CheckLolProcess()
        {
            try
            {
                Process[] procs = Process.GetProcessesByName("League of Legends");
                bool lolRunning = (procs != null && procs.Length > 0);
                if (procs != null)
                {
                    foreach (var p in procs) p.Dispose();
                }

                if (lolRunning && !_lolWasRunning)
                {
                    _lolWasRunning = true;
                    RefreshStateFromMouse(false);
                    if (_state.RateByte == 0x40) // 8000 Hz -> Auto-switch to 1000 Hz for LoL engine
                    {
                        if (WriteStateToMouse(0x01, _state.CurrentDpiStage))
                        {
                            _autoSwitchedForLol = true;
                            Logger.Log("Auto-switched Polling Rate -> 1000 Hz for League of Legends match");
                            _osd.ShowNotification(_state.CurrentDpiValue, _state.CurrentPollingHz, "RATE");
                        }
                    }
                }
                else if (!lolRunning && _lolWasRunning)
                {
                    _lolWasRunning = false;
                    if (_autoSwitchedForLol)
                    {
                        _autoSwitchedForLol = false;
                        RefreshStateFromMouse(false);
                        if (_state.RateByte == 0x01)
                        {
                            if (WriteStateToMouse(0x40, _state.CurrentDpiStage))
                            {
                                Logger.Log("Auto-restored Polling Rate -> 8000 Hz after League of Legends match");
                                _osd.ShowNotification(_state.CurrentDpiValue, _state.CurrentPollingHz, "RATE");
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static Icon CreateTrayIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush bg = new SolidBrush(Color.FromArgb(255, 85, 35)))
                {
                    g.FillEllipse(bg, 2, 2, 28, 28);
                }
                using (Font f = new Font("Segoe UI", 11f, FontStyle.Bold))
                using (Brush fg = new SolidBrush(Color.White))
                {
                    g.DrawString("8K", f, fg, 3, 6);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private void OnRawInput(IntPtr hRawInput)
        {
            uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            uint dwSize = 0;
            GetRawInputData(hRawInput, RID_INPUT, null, ref dwSize, headerSize);
            if (dwSize < headerSize + (uint)Marshal.SizeOf(typeof(RAWKEYBOARD)))
                return;

            byte[] buffer = new byte[dwSize];
            uint copied = GetRawInputData(hRawInput, RID_INPUT, buffer, ref dwSize, headerSize);
            if (copied == uint.MaxValue || copied < headerSize + (uint)Marshal.SizeOf(typeof(RAWKEYBOARD)))
                return;

            RAWINPUTHEADER header;
            RAWKEYBOARD kb;
            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject();
                header = (RAWINPUTHEADER)Marshal.PtrToStructure(ptr, typeof(RAWINPUTHEADER));
                if (header.dwType != RIM_TYPEKEYBOARD)
                    return;
                kb = (RAWKEYBOARD)Marshal.PtrToStructure(new IntPtr(ptr.ToInt64() + headerSize), typeof(RAWKEYBOARD));
            }
            finally
            {
                handle.Free();
            }

            bool isUp = (kb.Flags & RI_KEY_BREAK) != 0;
            int id;
            if (kb.VKey == VK_F10)
            {
                if (isUp) { _f10Down = false; return; }
                if (_f10Down) return;
                _f10Down = true;
                id = HOTKEY_ID_DPI;
            }
            else if (kb.VKey == VK_F11)
            {
                if (isUp) { _f11Down = false; return; }
                if (_f11Down) return;
                _f11Down = true;
                id = HOTKEY_ID_RATE;
            }
            else if (kb.VKey == VK_F12)
            {
                if (isUp) { _f12Down = false; return; }
                if (_f12Down) return;
                _f12Down = true;
                id = HOTKEY_ID_STATUS;
            }
            else
            {
                return;
            }

            bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            if (ctrlDown && altDown)
                OnHotkeyPressed(id, "RAW_INPUT");
        }

        private void OnHotkeyPressed(int id, string source)
        {
            long now = _inputWatch.ElapsedMilliseconds;
            if (id == HOTKEY_ID_DPI)
            {
                if (source != _lastDpiSource && (now - _lastDpiMs) < 150)
                    return;
                _lastDpiMs = now;
                _lastDpiSource = source;
                Logger.Log("Hotkey pressed: ID=" + id + " (" + source + ")");
                ToggleDpi();
                _lastDpiMs = _inputWatch.ElapsedMilliseconds;
                _lastDpiSource = source;
            }
            else if (id == HOTKEY_ID_RATE)
            {
                if (source != _lastRateSource && (now - _lastRateMs) < 150)
                    return;
                _lastRateMs = now;
                _lastRateSource = source;
                Logger.Log("Hotkey pressed: ID=" + id + " (" + source + ")");
                TogglePollingRate();
                _lastRateMs = _inputWatch.ElapsedMilliseconds;
                _lastRateSource = source;
            }
            else if (id == HOTKEY_ID_STATUS)
            {
                if (source != _lastStatusSource && (now - _lastStatusMs) < 150)
                    return;
                _lastStatusMs = now;
                _lastStatusSource = source;
                Logger.Log("Hotkey pressed: ID=" + id + " (" + source + ")");
                ShowCurrentStatus();
                _lastStatusMs = _inputWatch.ElapsedMilliseconds;
                _lastStatusSource = source;
            }
        }

        private bool RefreshStateFromMouse(bool readDpiTable = false)
        {
            byte[] rx = AtkHid.Transact(new byte[] { 8, 0, 0, 0, 10 });
            if (rx == null)
            {
                _state.IsOnline = false;
                return false;
            }

            _state.IsOnline = true;
            _state.RateByte = rx[6];
            _state.MaxDpiStages = rx[8];
            _state.CurrentDpiStage = rx[10];
            _state.Bhop = rx[12];
            _state.ButtonOpMode = rx[14];

            if (readDpiTable || !_dpiTableLoaded)
            {
                bool loaded12 = false;
                bool loaded34 = false;
                byte[] dpi12 = AtkHid.Transact(new byte[] { 8, 0, 0, 12, 8 });
                if (dpi12 != null)
                {
                    _state.StageDpiValues[0] = AtkHid.DecodePaw3950UltraDpi(dpi12[6], dpi12[8]);
                    _state.StageDpiValues[1] = AtkHid.DecodePaw3950UltraDpi(dpi12[10], dpi12[12]);
                    loaded12 = true;
                }
                byte[] dpi34 = AtkHid.Transact(new byte[] { 8, 0, 0, 20, 8 });
                if (dpi34 != null)
                {
                    _state.StageDpiValues[2] = AtkHid.DecodePaw3950UltraDpi(dpi34[6], dpi34[8]);
                    _state.StageDpiValues[3] = AtkHid.DecodePaw3950UltraDpi(dpi34[10], dpi34[12]);
                    loaded34 = true;
                }

                for (int i = 0; i < 4; i++)
                {
                    if (_state.StageDpiValues[i] == 800) _state.Stage800Index = i;
                    if (_state.StageDpiValues[i] == 1600) _state.Stage1600Index = i;
                    if (_state.StageDpiValues[i] == 3200) _state.Stage3200Index = i;
                }

                if (loaded12 && loaded34)
                {
                    _dpiTableLoaded = true;
                    Logger.Log(string.Format(
                        "DPI table loaded: [S0={0}, S1={1}, S2={2}, S3={3}] -> idx800={4}, idx1600={5}, idx3200={6}",
                        _state.StageDpiValues[0],
                        _state.StageDpiValues[1],
                        _state.StageDpiValues[2],
                        _state.StageDpiValues[3],
                        _state.Stage800Index,
                        _state.Stage1600Index,
                        _state.Stage3200Index
                    ));
                }
            }
            return true;
        }

        private bool WriteStateToMouse(byte newRateByte, byte newDpiStage)
        {
            byte[] tx = new byte[]
            {
                7, // SetEEPROM
                0,
                0, 0, // Address 0
                10,   // ValidLen 10
                newRateByte, AtkHid.CrcByte(newRateByte),
                _state.MaxDpiStages, AtkHid.CrcByte(_state.MaxDpiStages),
                newDpiStage, AtkHid.CrcByte(newDpiStage),
                _state.Bhop, AtkHid.CrcByte(_state.Bhop),
                _state.ButtonOpMode, AtkHid.CrcByte(_state.ButtonOpMode)
            };

            byte[] rx = AtkHid.Transact(tx);
            if (rx != null)
            {
                _state.RateByte = newRateByte;
                _state.CurrentDpiStage = newDpiStage;
                _state.IsOnline = true;
                UpdateTrayTooltip();
                return true;
            }
            return false;
        }

        private void SetDpiValue(int targetDpi)
        {
            RefreshStateFromMouse(false);
            byte targetStage = (targetDpi == 800)
                ? (byte)_state.Stage800Index
                : (targetDpi == 1600 ? (byte)_state.Stage1600Index : (byte)_state.Stage3200Index);
            SetDpiStage(targetStage);
        }

        private void SetDpiStage(byte targetStage)
        {
            bool written = WriteStateToMouse(_state.RateByte, targetStage);
            if (!written)
            {
                Thread.Sleep(120);
                RefreshStateFromMouse(false);
                written = WriteStateToMouse(_state.RateByte, targetStage);
            }

            if (written)
            {
                Logger.Log(string.Format("Switched DPI -> {0} DPI (Stage {1})", _state.CurrentDpiValue, _state.CurrentDpiStage));
                _osd.ShowNotification(_state.CurrentDpiValue, _state.CurrentPollingHz, "DPI");
            }
            else
            {
                Logger.Log("Switch DPI failed: mouse asleep/offline");
                _osd.ShowNotification(_state.CurrentDpiValue, _state.CurrentPollingHz, "OFFLINE", "Mouse em espera - mova o mouse para ativar");
            }
        }

        private void ToggleDpi()
        {
            RefreshStateFromMouse(false);
            byte targetStage;
            if (_state.CurrentDpiStage == (byte)_state.Stage800Index)
                targetStage = (byte)_state.Stage1600Index;
            else if (_state.CurrentDpiStage == (byte)_state.Stage1600Index)
                targetStage = (byte)_state.Stage3200Index;
            else
                targetStage = (byte)_state.Stage800Index;

            SetDpiStage(targetStage);
        }

        private void TogglePollingRate()
        {
            _autoSwitchedForLol = false;
            RefreshStateFromMouse(false);
            byte targetRate = (_state.RateByte == 0x40) ? (byte)0x01 : (byte)0x40;

            bool written = WriteStateToMouse(targetRate, _state.CurrentDpiStage);
            if (!written)
            {
                Thread.Sleep(120);
                if (RefreshStateFromMouse(false))
                    targetRate = (_state.RateByte == 0x40) ? (byte)0x01 : (byte)0x40;
                written = WriteStateToMouse(targetRate, _state.CurrentDpiStage);
            }

            if (written)
            {
                Logger.Log(string.Format("Switched Polling Rate -> {0} Hz", _state.CurrentPollingHz));
                _osd.ShowNotification(_state.CurrentDpiValue, _state.CurrentPollingHz, "RATE");
            }
            else
            {
                Logger.Log("TogglePollingRate failed: mouse asleep/offline");
                _osd.ShowNotification(_state.CurrentDpiValue, _state.CurrentPollingHz, "OFFLINE", "Mouse em espera - mova o mouse para ativar");
            }
        }

        private void ShowCurrentStatus()
        {
            if (RefreshStateFromMouse(false))
            {
                UpdateTrayTooltip();
                _osd.ShowNotification(_state.CurrentDpiValue, _state.CurrentPollingHz, "STATUS");
            }
            else
            {
                _osd.ShowNotification(_state.CurrentDpiValue, _state.CurrentPollingHz, "OFFLINE", "Mouse em espera - mova o mouse para ativar");
            }
        }

        private void UpdateTrayTooltip()
        {
            int dpi = _state.CurrentDpiValue;
            string info = string.Format("ATK X1 Ultimate 2.0: {0} DPI | {1} Hz", dpi, _state.CurrentPollingHz);
            _statusMenuItem.Text = info;
            _trayIcon.Text = info;
            if (_dpi800MenuItem != null) _dpi800MenuItem.Checked = (dpi == 800);
            if (_dpi1600MenuItem != null) _dpi1600MenuItem.Checked = (dpi == 1600);
            if (_dpi3200MenuItem != null) _dpi3200MenuItem.Checked = (dpi == 3200);
        }

        protected override void ExitThreadCore()
        {
            Logger.Log("Exiting AtkQuickSwitch");
            if (_lolWatchTimer != null)
            {
                _lolWatchTimer.Stop();
                _lolWatchTimer.Dispose();
            }
            UnregisterHotKey(_msgWindow.Handle, HOTKEY_ID_DPI);
            UnregisterHotKey(_msgWindow.Handle, HOTKEY_ID_RATE);
            UnregisterHotKey(_msgWindow.Handle, HOTKEY_ID_STATUS);
            _msgWindow.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _osd.Dispose();
            base.ExitThreadCore();
        }
    }

    public static class Program
    {
        private static Mutex _mutex;

        private static bool ApplyPollingRateCli(int hz)
        {
            byte? rateOpt = AtkHid.HzToRateByte(hz);
            if (!rateOpt.HasValue)
            {
                Logger.Log("CLI --set-rate failed: unsupported rate " + hz + "Hz");
                return false;
            }
            byte targetRateByte = rateOpt.Value;

            for (int attempt = 1; attempt <= 4; attempt++)
            {
                byte[] rx = AtkHid.Transact(new byte[] { 8, 0, 0, 0, 10 });
                if (rx != null)
                {
                    byte maxStages = rx[8];
                    byte curStage = rx[10];
                    byte bhop = rx[12];
                    byte btnMode = rx[14];

                    byte[] tx = new byte[]
                    {
                        7, // SetEEPROM
                        0,
                        0, 0, // Address 0
                        10,   // ValidLen 10
                        targetRateByte, AtkHid.CrcByte(targetRateByte),
                        maxStages, AtkHid.CrcByte(maxStages),
                        curStage, AtkHid.CrcByte(curStage),
                        bhop, AtkHid.CrcByte(bhop),
                        btnMode, AtkHid.CrcByte(btnMode)
                    };

                    byte[] writeRx = AtkHid.Transact(tx);
                    if (writeRx != null)
                    {
                        byte[] verifyRx = AtkHid.Transact(new byte[] { 8, 0, 0, 0, 10 });
                        if (verifyRx != null && verifyRx[6] == targetRateByte)
                        {
                            Logger.Log(string.Format("CLI switched Polling Rate -> {0} Hz (confirmed)", hz));
                            return true;
                        }
                    }
                }
                if (attempt < 4)
                    Thread.Sleep(200);
            }

            Logger.Log(string.Format("CLI --set-rate {0} Hz failed after 4 attempts", hz));
            return false;
        }

        [STAThread]
        public static void Main(string[] args)
        {
            if (args != null && args.Length >= 2 && string.Equals(args[0], "--set-rate", StringComparison.OrdinalIgnoreCase))
            {
                int targetHz;
                if (int.TryParse(args[1], out targetHz))
                {
                    bool ok = ApplyPollingRateCli(targetHz);
                    Environment.Exit(ok ? 0 : 1);
                    return;
                }
                Logger.Log("CLI --set-rate failed: invalid value '" + args[1] + "'");
                Environment.Exit(1);
                return;
            }

            bool createdNew;
            _mutex = new Mutex(true, "Local\\AtkQuickSwitch_SingleInstance", out createdNew);
            if (!createdNew)
                return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new HotkeyApplicationContext());
        }
    }
}
