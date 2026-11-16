// FC4 Control - makes the normal Windows volume control work with the HiBy FC4.
// The FC4 accepts the Windows (hardware) volume but ignores it, so this app mirrors the
// Windows volume and mute of the FC4 device onto every app's audio session (software mixer).
// Result: the Windows speaker icon, keyboard volume keys and this tray slider all change the sound.
// Builds with the .NET Framework C# compiler (C# 5): see build.cmd.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FC4Control
{
    // ---------- Core Audio interop ----------
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public short vt; public short r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(int ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(int ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(int ch, out float level);
        [PreserveSig] int SetMute(bool mute, ref Guid ctx);
        [PreserveSig] int GetMute(out bool mute);
    }

    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute(out bool mute);
    }

    // Connection to the FC4 playback device.
    class Fc4
    {
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

        IMMDevice dev; IAudioEndpointVolume ev; IAudioSessionManager2 mgr;
        public string Name = "";
        public bool Connected { get { return ev != null; } }

        static void Release(object o) { if (o != null) { try { Marshal.ReleaseComObject(o); } catch { } } }

        public void Disconnect() { Release(mgr); Release(ev); Release(dev); mgr = null; ev = null; dev = null; }

        public bool Connect()
        {
            Disconnect();
            IMMDeviceEnumerator en = null; IMMDeviceCollection col = null;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (en.EnumAudioEndpoints(0, 1, out col) != 0) return false;
                int n; col.GetCount(out n);
                PROPERTYKEY nameKey = new PROPERTYKEY();
                nameKey.fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"); nameKey.pid = 14;
                for (int i = 0; i < n; i++)
                {
                    IMMDevice d = null; IPropertyStore ps = null; bool keep = false;
                    try
                    {
                        if (col.Item(i, out d) != 0) continue;
                        string name = "";
                        if (d.OpenPropertyStore(0, out ps) == 0)
                        {
                            PROPVARIANT v;
                            if (ps.GetValue(ref nameKey, out v) == 0)
                            {
                                if (v.vt == 31 && v.p != IntPtr.Zero) name = Marshal.PtrToStringUni(v.p);
                                PropVariantClear(ref v);
                            }
                        }
                        if (name.IndexOf("HiBy", StringComparison.OrdinalIgnoreCase) < 0 &&
                            name.IndexOf("FC4", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        object o1, o2;
                        Guid iidEv = typeof(IAudioEndpointVolume).GUID, iidMgr = typeof(IAudioSessionManager2).GUID;
                        if (d.Activate(ref iidEv, 23, IntPtr.Zero, out o1) != 0) continue;
                        if (d.Activate(ref iidMgr, 23, IntPtr.Zero, out o2) != 0) { Release(o1); continue; }
                        dev = d; ev = (IAudioEndpointVolume)o1; mgr = (IAudioSessionManager2)o2; Name = name; keep = true;
                        return true;
                    }
                    finally { Release(ps); if (!keep) Release(d); }
                }
            }
            catch { Disconnect(); }
            finally { Release(col); Release(en); }
            return false;
        }

        // Windows volume of the device: dB, 0..1 scalar (what the Windows slider shows) and mute.
        public bool ReadMaster(out float db, out float scalar, out bool mute)
        {
            db = 0; scalar = 0; mute = false;
            if (ev == null) return false;
            try
            {
                if (ev.GetMasterVolumeLevel(out db) != 0 || ev.GetMasterVolumeLevelScalar(out scalar) != 0 || ev.GetMute(out mute) != 0)
                { Disconnect(); return false; }
                return true;
            }
            catch { Disconnect(); return false; }
        }

        public void SetScalar(float scalar)
        {
            if (ev == null) return;
            try { Guid g = Guid.Empty; ev.SetMasterVolumeLevelScalar(Math.Max(0f, Math.Min(1f, scalar)), ref g); } catch { Disconnect(); }
        }

        public void SetMute(bool mute)
        {
            if (ev == null) return;
            try { Guid g = Guid.Empty; ev.SetMute(mute, ref g); } catch { Disconnect(); }
        }

        // Puts (level, mute) on every app session of the device. Returns the session count, -1 on failure.
        public int ApplySessions(float level, bool mute, bool apply, TextWriter report)
        {
            if (mgr == null) return -1;
            IAudioSessionEnumerator se = null; int seen = 0;
            try
            {
                if (mgr.GetSessionEnumerator(out se) != 0) { Disconnect(); return -1; }
                int count; se.GetCount(out count);
                for (int s = 0; s < count; s++)
                {
                    object sess = null;
                    try
                    {
                        if (se.GetSession(s, out sess) != 0) continue;
                        ISimpleAudioVolume vol = sess as ISimpleAudioVolume;
                        if (vol == null) continue;
                        seen++;
                        float cur; bool curMute; vol.GetMasterVolume(out cur); vol.GetMute(out curMute);
                        if (report != null)
                            report.WriteLine("   app " + s + ": level=" + cur.ToString("0.000", CultureInfo.InvariantCulture) + " mute=" + curMute);
                        if (!apply) continue;
                        Guid ctx = Guid.Empty;
                        if (Math.Abs(cur - level) > 0.0005f) vol.SetMasterVolume(level, ref ctx);
                        if (curMute != mute) vol.SetMute(mute, ref ctx);
                    }
                    finally { Release(sess); }
                }
            }
            catch { Disconnect(); return -1; }
            finally { Release(se); }
            return seen;
        }
    }

    // ---------- FC4 HID control channel (PEQ bands, preamp, PEQ on/off) ----------
    // Message formats were taken from HiByUSBMaster and confirmed on the device: see fc4-protocol.md.
    class Band { public int Index, On, Filter, Freq, Q, Gain; }

    static class Dac
    {
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] static extern bool HidD_SetFeature(IntPtr h, byte[] buf, int len);
        [DllImport("hid.dll")] static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfoData);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFile(string name, uint access, int share, IntPtr sec, int disposition, int flags, IntPtr template);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid guid; public int flags; public IntPtr reserved; }

        const int ReportId = 5, PacketSize = 62, CmdSet = 7, CmdGet = 9;
        static readonly IntPtr Invalid = new IntPtr(-1);

        // Path of the FC4's vendor HID collection (interface 2, collection 1).
        static string FindPath()
        {
            Guid g; HidD_GetHidGuid(out g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (set == Invalid) return null;
            try
            {
                SP_DEVICE_INTERFACE_DATA d = new SP_DEVICE_INTERFACE_DATA(); d.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
                {
                    int need; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need <= 0) continue;
                    IntPtr mem = Marshal.AllocHGlobal(need);
                    try
                    {
                        Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref d, mem, need, out need, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(mem.ToInt64() + 4));
                        string low = path.ToLowerInvariant();
                        if (low.Contains("vid_32bb&pid_0004") && low.Contains("mi_02&col01")) return path;
                    }
                    finally { Marshal.FreeHGlobal(mem); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return null;
        }

        // Sends one command and returns the device's reply (62 bytes), or null on failure.
        static byte[] Request(int cmd, byte[] payload)
        {
            string path = FindPath();
            if (path == null) return null;
            IntPtr h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == Invalid) return null;
            try
            {
                byte[] pkt = new byte[PacketSize];
                pkt[0] = ReportId; BitConverter.GetBytes(cmd).CopyTo(pkt, 1); payload.CopyTo(pkt, 5);
                if (!HidD_SetFeature(h, pkt, PacketSize)) return null;
                Thread.Sleep(30);
                byte[] reply = new byte[PacketSize]; reply[0] = ReportId;
                if (!HidD_GetFeature(h, reply, PacketSize)) return null;
                Thread.Sleep(30);
                return reply;
            }
            finally { CloseHandle(h); }
        }

        static bool Ok(byte[] r) { return r != null && r[0] == (byte)'s' && r[1] == (byte)'u' && r[2] == (byte)'c'; }

        public static bool GetPreamp(out int value)
        {
            value = 0; byte[] r = Request(CmdGet, new byte[] { 0x0A });
            if (r == null || r[0] != 1) return false;
            value = BitConverter.ToInt32(r, 1); return true;
        }

        public static bool GetPeq(out bool on)
        {
            on = false; byte[] r = Request(CmdGet, new byte[] { 0x0B });
            if (r == null || r[0] != 3) return false;
            on = r[1] != 0; return true;
        }

        public static Band GetBand(int index)
        {
            byte[] r = Request(CmdGet, new byte[] { (byte)index });
            if (r == null || r[0] != 2) return null;
            Band b = new Band();
            b.Freq = BitConverter.ToInt32(r, 1); b.On = BitConverter.ToInt32(r, 5); b.Filter = BitConverter.ToInt32(r, 9);
            b.Index = BitConverter.ToInt32(r, 13); b.Q = BitConverter.ToInt32(r, 17); b.Gain = BitConverter.ToInt32(r, 21);
            return b;
        }

        // value is dB x100, limited to -9.00..+9.00 dB like the HiBy app.
        public static bool SetPreamp(int value)
        {
            value = Math.Max(-900, Math.Min(900, value));
            byte[] p = new byte[5]; p[0] = 1; BitConverter.GetBytes(value).CopyTo(p, 1);
            return Ok(Request(CmdSet, p));
        }

        public static bool SetPeq(bool on) { return Ok(Request(CmdSet, new byte[] { 3, (byte)(on ? 1 : 0) })); }

        // Same limits the HiBy app enforces before sending a band.
        public static bool Valid(Band b)
        {
            return b != null && b.Index >= 0 && b.Index < 10 && (b.On == 0 || b.On == 1) && b.Filter >= 0 && b.Filter < 3
                && b.Freq >= 20 && b.Freq <= 20000 && b.Q >= 20 && b.Q <= 2000 && b.Gain >= -900 && b.Gain <= 900;
        }

        public static bool SetBand(Band b)
        {
            if (!Valid(b)) return false;
            byte[] p = new byte[25]; p[0] = 2;
            int[] f = { b.Index, b.On, b.Filter, b.Freq, b.Q, b.Gain };
            for (int i = 0; i < f.Length; i++) BitConverter.GetBytes(f[i]).CopyTo(p, 1 + i * 4);
            return Ok(Request(CmdSet, p));
        }
    }

    // EQ presets: plain text files holding the 10 bands, preamp and EQ on/off.
    static class Presets
    {
        public static string Dir { get { return Path.Combine(Application.StartupPath, "presets"); } }

        // Reads the EQ now in the DAC and stores it as <dir>\<name>.txt.
        public static bool Save(string dir, string name, out string msg)
        {
            int pre; bool on;
            if (!Dac.GetPreamp(out pre) || !Dac.GetPeq(out on)) { msg = "Could not read the EQ from the FC4."; return false; }
            List<string> lines = new List<string>();
            lines.Add("# FC4 Control EQ preset. band = index,on,filter,freqHz,Q x100,gain dB x100");
            lines.Add("preamp=" + pre); lines.Add("peq=" + (on ? 1 : 0));
            for (int i = 0; i < 10; i++)
            {
                Band b = Dac.GetBand(i);
                if (b == null) { msg = "Could not read EQ band " + i + " from the FC4."; return false; }
                lines.Add("band=" + b.Index + "," + b.On + "," + b.Filter + "," + b.Freq + "," + b.Q + "," + b.Gain);
            }
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, name + ".txt"), lines.ToArray());
            msg = "Saved EQ preset: " + name; return true;
        }

        // Sends a stored preset to the DAC. Nothing is sent unless the whole file is valid.
        public static bool Apply(string file, out string msg)
        {
            int pre = 0; bool on = true; bool havePre = false; List<Band> bands = new List<Band>();
            try
            {
                foreach (string raw in File.ReadAllLines(file))
                {
                    string l = raw.Trim();
                    if (l.Length == 0 || l.StartsWith("#")) continue;
                    int eq = l.IndexOf('='); if (eq < 0) throw new FormatException();
                    string k = l.Substring(0, eq).Trim(), v = l.Substring(eq + 1).Trim();
                    if (k == "preamp") { pre = int.Parse(v, CultureInfo.InvariantCulture); havePre = true; }
                    else if (k == "peq") on = v == "1";
                    else if (k == "band")
                    {
                        string[] p = v.Split(',');
                        Band b = new Band();
                        b.Index = int.Parse(p[0]); b.On = int.Parse(p[1]); b.Filter = int.Parse(p[2]);
                        b.Freq = int.Parse(p[3]); b.Q = int.Parse(p[4]); b.Gain = int.Parse(p[5], CultureInfo.InvariantCulture);
                        if (!Dac.Valid(b)) throw new FormatException();
                        bands.Add(b);
                    }
                }
                if (!havePre || pre < -900 || pre > 900 || bands.Count != 10) throw new FormatException();
            }
            catch { msg = "That preset file is not valid; nothing was sent."; return false; }

            foreach (Band b in bands)
                if (!Dac.SetBand(b)) { msg = "The FC4 did not accept band " + b.Index + "; the preset was only partly applied."; return false; }
            if (!Dac.SetPreamp(pre) || !Dac.SetPeq(on)) { msg = "The FC4 did not accept the preamp or EQ switch."; return false; }
            msg = "Applied EQ preset: " + Path.GetFileNameWithoutExtension(file); return true;
        }

        // First run: keep a copy of whatever EQ is in the DAC before this app ever writes to it.
        public static void BackupOnce()
        {
            try
            {
                string f = Path.Combine(Dir, "Original (backup).txt");
                if (File.Exists(f)) return;
                string msg; Save(Dir, "Original (backup)", out msg);
            }
            catch { }
        }
    }

    // ---------- UI ----------
    class Popup : Form
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        public const string WindowTitle = "FC4Control.ControlWindow";
        // Message that lets "FC4Control.exe --set/--up/--down/--mute" drive the running instance.
        public static readonly int ControlMsg = RegisterWindowMessage("FC4Control.Control");

        const int Step = 2;
        readonly Fc4 fc4 = new Fc4();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly TrackBar bar = new TrackBar();
        readonly Label glyph = new Label();
        readonly Label number = new Label();
        readonly Label status = new Label();
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        readonly CheckBox eqBox = new CheckBox();
        readonly TrackBar preBar = new TrackBar();
        readonly Label preLabel = new Label();
        readonly Label preValue = new Label();
        readonly System.Windows.Forms.Timer preDebounce = new System.Windows.Forms.Timer();
        readonly ToolStripMenuItem presetsMenu = new ToolStripMenuItem("EQ presets");
        readonly string presetDir;
        bool dacLoading; string lastPreset = "";
        // Desktop widget mode: the same panel stays on the desktop, can be dragged, and remembers its place.
        bool pinned, onTop; bool placing;
        readonly ToolStripMenuItem pinItem = new ToolStripMenuItem("Show on desktop");
        readonly ToolStripMenuItem topItem = new ToolStripMenuItem("Always on top");
        readonly string settingsPath = Path.Combine(Application.StartupPath, "settings.ini");
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        readonly string iconFont;
        float db, scalar; bool mute; bool connected; int sessions = -1;
        bool loading; int tick; IntPtr lastIcon = IntPtr.Zero; string lastIconKey = "";

        public Popup()
        {
            iconFont = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            Text = WindowTitle;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; BackColor = Color.FromArgb(32, 32, 36);
            ClientSize = new Size(360, 132); Font = new Font("Segoe UI", 9f); KeyPreview = true;

            glyph.SetBounds(10, 12, 40, 40); glyph.ForeColor = Color.White; glyph.Font = new Font(iconFont, 17f);
            glyph.TextAlign = ContentAlignment.MiddleCenter; glyph.Cursor = Cursors.Hand;
            bar.SetBounds(52, 14, 250, 40); bar.Minimum = 0; bar.Maximum = 100;
            bar.TickStyle = TickStyle.None; bar.SmallChange = 1; bar.LargeChange = 10;
            number.SetBounds(302, 12, 50, 40); number.ForeColor = Color.White; number.Font = new Font("Segoe UI", 13f);
            number.TextAlign = ContentAlignment.MiddleCenter;
            Panel line = new Panel(); line.SetBounds(12, 58, 336, 1); line.BackColor = Color.FromArgb(62, 62, 68);
            eqBox.SetBounds(16, 68, 56, 28); eqBox.Text = "EQ"; eqBox.ForeColor = Color.White;
            preLabel.SetBounds(76, 68, 58, 28); preLabel.Text = "Preamp"; preLabel.ForeColor = Color.Gainsboro; preLabel.TextAlign = ContentAlignment.MiddleLeft;
            preBar.SetBounds(132, 66, 162, 40); preBar.Minimum = -18; preBar.Maximum = 18; // half-dB steps, -9..+9 dB
            preBar.TickStyle = TickStyle.None; preBar.SmallChange = 1; preBar.LargeChange = 2;
            preValue.SetBounds(292, 68, 64, 28); preValue.ForeColor = Color.White; preValue.TextAlign = ContentAlignment.MiddleCenter;
            status.SetBounds(16, 106, 330, 20); status.ForeColor = Color.FromArgb(150, 150, 158);
            Controls.Add(glyph); Controls.Add(bar); Controls.Add(number); Controls.Add(line);
            Controls.Add(eqBox); Controls.Add(preLabel); Controls.Add(preBar); Controls.Add(preValue); Controls.Add(status);

            presetDir = Presets.Dir; Presets.BackupOnce();
            eqBox.CheckedChanged += delegate { if (!dacLoading) { if (!Dac.SetPeq(eqBox.Checked)) LoadDac(); } };
            preBar.ValueChanged += delegate { if (!dacLoading) { preValue.Text = PreText(preBar.Value * 50); preDebounce.Stop(); preDebounce.Start(); } };
            preDebounce.Interval = 250; // send once the slider settles, not on every step
            preDebounce.Tick += delegate { preDebounce.Stop(); if (!Dac.SetPreamp(preBar.Value * 50)) LoadDac(); };

            bar.ValueChanged += delegate { if (!loading) { fc4.SetScalar(bar.Value / 100f); if (mute && bar.Value > 0) fc4.SetMute(false); Sync(true); } };
            glyph.Click += delegate { fc4.SetMute(!mute); Sync(true); };
            Deactivate += delegate { if (!pinned) Hide(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape && !pinned) Hide(); };

            // drag the widget by its background, the percentage or the status line
            foreach (Control c in new Control[] { this, number, status, preLabel, preValue })
                c.MouseDown += delegate(object s, MouseEventArgs e) { if (pinned && e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } };
            LocationChanged += delegate { if (pinned && !placing) SaveSettings(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open volume", null, delegate { ShowPopup(); });
            menu.Items.Add("Mute / unmute", null, delegate { fc4.SetMute(!mute); Sync(true); });
            menu.Items.Add(presetsMenu);
            presetsMenu.DropDownItems.Add("(loading)");
            presetsMenu.DropDownOpening += delegate { BuildPresetMenu(); };
            menu.Items.Add(new ToolStripSeparator());
            pinItem.Click += delegate { SetPinned(!pinned); };
            topItem.Click += delegate { onTop = !onTop; ApplyMode(); SaveSettings(); };
            menu.Items.Add(pinItem); menu.Items.Add(topItem);
            menu.Opening += delegate { pinItem.Checked = pinned; topItem.Checked = onTop; topItem.Enabled = pinned; };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { poll.Stop(); tray.Visible = false; Application.Exit(); });
            tray.ContextMenuStrip = menu;
            ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (pinned) { Show(); Activate(); } else if (Visible) Hide(); else ShowPopup();
            };
            tray.Visible = true;

            IntPtr h = Handle; // create the window so command-line control can find it
            poll.Interval = 100;
            poll.Tick += delegate
            {
                tick++; Sync(false);
                if (pinned && Visible && tick % 50 == 0 && !preDebounce.Enabled && !preBar.Focused) LoadDac(); // every 5 s
            };
            poll.Start();
            Sync(true);
            LoadSettings();
            if (pinned) { ApplyMode(); LoadDac(); Show(); }
        }

        void SetPinned(bool on)
        {
            pinned = on;
            if (pinned)
            {
                if (!Visible) { Rectangle wa = Screen.PrimaryScreen.WorkingArea; Location = new Point(wa.Right - Width - 24, wa.Top + 24); }
                ApplyMode(); LoadDac(); Show();
            }
            else { ApplyMode(); Hide(); }
            SaveSettings();
        }

        void ApplyMode()
        {
            TopMost = !pinned || onTop;
            status.Text = StatusText();
        }

        string StatusText()
        {
            string t = connected ? "HiBy FC4" + (mute ? " - muted" : "") : "HiBy FC4 is not connected";
            return pinned ? t + "   (right-click for options)" : t;
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(settingsPath)) return;
                int x = int.MinValue, y = int.MinValue;
                foreach (string line in File.ReadAllLines(settingsPath))
                {
                    string[] kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    string k = kv[0].Trim(), v = kv[1].Trim();
                    if (k == "desktop") pinned = v == "1";
                    else if (k == "ontop") onTop = v == "1";
                    else if (k == "x") int.TryParse(v, out x);
                    else if (k == "y") int.TryParse(v, out y);
                }
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Point pt = new Point(wa.Right - Width - 24, wa.Top + 24);
                if (x != int.MinValue && y != int.MinValue)
                    foreach (Screen sc in Screen.AllScreens) // only use the saved place if it is still on a screen
                        if (sc.WorkingArea.Contains(new Rectangle(x, y, Width / 2, Height / 2))) { pt = new Point(x, y); break; }
                placing = true; Location = pt; placing = false;
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                File.WriteAllLines(settingsPath, new string[] {
                    "desktop=" + (pinned ? 1 : 0), "ontop=" + (onTop ? 1 : 0),
                    "x=" + Location.X, "y=" + Location.Y });
            }
            catch { }
        }

        static bool FontExists(string name)
        {
            try { using (Font f = new Font(name, 10f)) return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } // Windows 11 rounded corners
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80; cp.ClassStyle |= 0x20000; return cp; } // toolwindow + drop shadow
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Nudge(e.Delta > 0 ? Step : -Step);
            HandledMouseEventArgs he = e as HandledMouseEventArgs; if (he != null) he.Handled = true;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ControlMsg && ControlMsg != 0)
            {
                int cmd = m.WParam.ToInt32();
                if (cmd == 1) fc4.SetScalar((m.LParam.ToInt32()) / 100f);
                else if (cmd == 2) Nudge(Step);
                else if (cmd == 3) Nudge(-Step);
                else if (cmd == 4) fc4.SetMute(!mute);
                Sync(true);
                return;
            }
            base.WndProc(ref m);
        }

        void Nudge(int delta) { fc4.SetScalar((Percent() + delta) / 100f); Sync(true); }

        int Percent() { return (int)Math.Round(scalar * 100f); }

        void ShowPopup()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            placing = true; Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12); placing = false;
            LoadDac();
            Show(); Activate(); bar.Focus();
        }

        static string PreText(int centiDb) { return (centiDb / 100.0).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB"; }

        // Reads preamp and EQ on/off from the DAC into the controls.
        void LoadDac()
        {
            int pre; bool on = false;
            bool ok = Dac.GetPreamp(out pre) && Dac.GetPeq(out on);
            dacLoading = true;
            eqBox.Enabled = ok; preBar.Enabled = ok;
            if (ok)
            {
                preBar.Value = Math.Max(-18, Math.Min(18, (int)Math.Round(pre / 50.0)));
                preValue.Text = PreText(pre); eqBox.Checked = on;
            }
            else preValue.Text = "-";
            dacLoading = false;
        }

        void BuildPresetMenu()
        {
            presetsMenu.DropDownItems.Clear();
            if (Directory.Exists(presetDir))
                foreach (string f in Directory.GetFiles(presetDir, "*.txt"))
                {
                    string file = f; string name = Path.GetFileNameWithoutExtension(f);
                    ToolStripMenuItem it = new ToolStripMenuItem(name, null, delegate { ApplyPreset(file); });
                    it.Checked = name == lastPreset;
                    presetsMenu.DropDownItems.Add(it);
                }
            if (presetsMenu.DropDownItems.Count > 0) presetsMenu.DropDownItems.Add(new ToolStripSeparator());
            presetsMenu.DropDownItems.Add("Save current EQ as...", null, delegate { SavePreset(null); });
            presetsMenu.DropDownItems.Add("Open presets folder", null, delegate { Directory.CreateDirectory(presetDir); Process.Start(presetDir); });
        }

        void Notify(string text) { tray.ShowBalloonTip(3000, "FC4 Control", text, ToolTipIcon.None); }

        void SavePreset(string name)
        {
            if (name == null) name = AskName();
            if (name == null) return;
            string msg; if (Presets.Save(presetDir, name, out msg)) lastPreset = name;
            Notify(msg);
        }

        void ApplyPreset(string file)
        {
            string msg; if (Presets.Apply(file, out msg)) lastPreset = Path.GetFileNameWithoutExtension(file);
            LoadDac(); Notify(msg);
        }

        static string AskName()
        {
            using (Form f = new Form())
            {
                f.Text = "Save EQ preset"; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterScreen;
                f.ClientSize = new Size(300, 96); f.MinimizeBox = false; f.MaximizeBox = false; f.TopMost = true; f.ShowInTaskbar = false;
                Label l = new Label(); l.Text = "Preset name:"; l.SetBounds(12, 12, 276, 18);
                TextBox t = new TextBox(); t.SetBounds(12, 32, 276, 24);
                Button ok = new Button(); ok.Text = "Save"; ok.SetBounds(132, 62, 75, 26); ok.DialogResult = DialogResult.OK;
                Button no = new Button(); no.Text = "Cancel"; no.SetBounds(213, 62, 75, 26); no.DialogResult = DialogResult.Cancel;
                f.Controls.Add(l); f.Controls.Add(t); f.Controls.Add(ok); f.Controls.Add(no); f.AcceptButton = ok; f.CancelButton = no;
                if (f.ShowDialog() != DialogResult.OK) return null;
                string name = t.Text.Trim();
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
                return name.Length == 0 ? null : name;
            }
        }

        // Reads the Windows volume of the FC4 and mirrors it onto the app sessions.
        void Sync(bool force)
        {
            if (!fc4.Connected)
            {
                if (!force && tick % 20 != 0) return; // retry every 2 s
                fc4.Connect(); force = true;
            }
            float newDb, newScalar; bool newMute;
            bool ok = fc4.ReadMaster(out newDb, out newScalar, out newMute);
            bool changed = force || ok != connected || newDb != db || newScalar != scalar || newMute != mute;
            connected = ok; db = newDb; scalar = newScalar; mute = newMute;
            if (ok && (changed || tick % 5 == 0)) // also catch apps that just started playing
            {
                float level = scalar <= 0f ? 0f : (float)Math.Min(1.0, Math.Pow(10.0, db / 20.0));
                int n = fc4.ApplySessions(level, mute, true, null);
                if (n != sessions) { sessions = n; changed = true; }
            }
            if (!changed) return;

            int pct = Percent();
            loading = true; if (bar.Value != pct) bar.Value = pct; bar.Enabled = ok; loading = false;
            number.Text = ok ? pct.ToString() : "-";
            glyph.Text = Glyph(ok, pct);
            status.Text = StatusText();
            UpdateIcon(ok, pct);
        }

        string Glyph(bool ok, int pct)
        {
            if (!ok || mute || pct == 0) return "";      // muted speaker
            if (pct < 34) return "";
            if (pct < 67) return "";
            return "";
        }

        static bool LightTaskbar()
        {
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
                return v is int && (int)v == 1;
            }
            catch { return false; }
        }

        void UpdateIcon(bool ok, int pct)
        {
            string g = Glyph(ok, pct); bool light = LightTaskbar();
            string key = g + light + ok;
            if (key != lastIconKey)
            {
                lastIconKey = key;
                using (Bitmap bmp = new Bitmap(32, 32))
                {
                    using (Graphics gr = Graphics.FromImage(bmp))
                    {
                        gr.SmoothingMode = SmoothingMode.AntiAlias;
                        gr.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        Color c = light ? Color.Black : Color.White;
                        if (!ok) c = Color.FromArgb(140, c);
                        using (Font f = new Font(iconFont, 26f, FontStyle.Regular, GraphicsUnit.Pixel))
                        using (SolidBrush b = new SolidBrush(c))
                        {
                            StringFormat sf = new StringFormat(); sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                            gr.DrawString(g, f, b, new RectangleF(0, 1, 32, 32), sf);
                        }
                    }
                    IntPtr h = bmp.GetHicon();
                    tray.Icon = Icon.FromHandle(h);
                    if (lastIcon != IntPtr.Zero) DestroyIcon(lastIcon);
                    lastIcon = h;
                }
            }
            tray.Text = !ok ? "FC4 Control: not connected" : (mute ? "FC4 Control: muted" : "FC4 Control: " + pct + "%");
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--list")
            {
                Fc4 f = new Fc4();
                if (!f.Connect()) { Console.WriteLine("FC4 not found"); return 0; }
                float db, sc; bool mu; f.ReadMaster(out db, out sc, out mu);
                Console.WriteLine(f.Name + ": Windows volume " + Math.Round(sc * 100) + "% (" + db.ToString("0.0", CultureInfo.InvariantCulture) + " dB) mute=" + mu);
                int n = f.ApplySessions(1f, false, false, Console.Out);
                Console.WriteLine("apps: " + n);
                return 0;
            }
            if (args.Length > 0 && args[0] == "--eq")
            {
                int pre; bool on;
                if (!Dac.GetPreamp(out pre) || !Dac.GetPeq(out on)) { Console.WriteLine("could not read the FC4 EQ"); return 2; }
                Console.WriteLine("preamp=" + pre + " peq=" + (on ? 1 : 0));
                for (int i = 0; i < 10; i++) { Band b = Dac.GetBand(i); Console.WriteLine(b == null ? "band " + i + ": unreadable" : "band=" + b.Index + "," + b.On + "," + b.Filter + "," + b.Freq + "," + b.Q + "," + b.Gain); }
                return 0;
            }
            if (args.Length > 1 && (args[0] == "--save-preset" || args[0] == "--preset"))
            {
                string msg; bool ok = args[0] == "--save-preset"
                    ? Presets.Save(Presets.Dir, args[1], out msg)
                    : Presets.Apply(Path.Combine(Presets.Dir, args[1] + ".txt"), out msg);
                Console.WriteLine(msg); return ok ? 0 : 2;
            }
            if (args.Length > 0 && args[0].StartsWith("--"))
            {
                int cmd = 0, value = 0;
                if (args[0] == "--up") cmd = 2;
                else if (args[0] == "--down") cmd = 3;
                else if (args[0] == "--mute") cmd = 4;
                else if (args[0] == "--set" && args.Length > 1 && int.TryParse(args[1], out value) && value >= 0 && value <= 100) cmd = 1;
                if (cmd == 0) { Console.WriteLine("usage: FC4Control [--list | --eq | --preset <name> | --save-preset <name> | --up | --down | --mute | --set <0..100>]"); return 1; }
                IntPtr target = Popup.FindWindow(null, Popup.WindowTitle);
                if (target == IntPtr.Zero) { Console.WriteLine("FC4 Control is not running"); return 2; }
                Popup.PostMessage(target, Popup.ControlMsg, new IntPtr(cmd), new IntPtr(value));
                return 0;
            }
            bool first;
            using (Mutex m = new Mutex(true, "FC4Control.SingleInstance", out first))
            {
                if (!first) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Popup p = new Popup();
                Application.Run();
                GC.KeepAlive(p);
            }
            return 0;
        }
    }
}
