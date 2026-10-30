using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

// Автоповорот экрана - управляющее окно.
// Компилируется штатным csc.exe из .NET Framework, сторонних библиотек не требует.
//
// Фоновый процесс powershell.exe только читает акселерометр и печатает
// значения в стандартный вывод. Вся логика поворота живёт здесь, в C#,
// чтобы она не расходилась между окном и воркером.

// ---------------------------------------------------------------------------
// Сведения об одном экране
// ---------------------------------------------------------------------------
public class ScreenInfo {
    public string Device;     // \\.\DISPLAY1
    public string Friendly;   // имя монитора из EDID, может быть пустым
    public bool IsInternal;   // встроенная панель ноутбука
    public bool IsPrimary;
    public int Width, Height;

    public override string ToString() {
        StringBuilder sb = new StringBuilder();
        sb.Append(Device.Replace(@"\\.\", ""));
        if (!string.IsNullOrEmpty(Friendly)) { sb.Append(" - "); sb.Append(Friendly); }
        else if (Width > 0) { sb.Append(" - "); sb.Append(Width); sb.Append("x"); sb.Append(Height); }
        List<string> tags = new List<string>();
        if (IsInternal) tags.Add("встроенный");
        if (IsPrimary) tags.Add("основной");
        if (tags.Count > 0) sb.Append(" (" + string.Join(", ", tags.ToArray()) + ")");
        return sb.ToString();
    }
}

public class Rot {

    // ---------- GDI ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;
        public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra;
        public int dmFields; public int dmPositionX; public int dmPositionY;
        public int dmDisplayOrientation; public int dmDisplayFixedOutput;
        public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight;
        public int dmDisplayFlags; public int dmDisplayFrequency; public int dmICMMethod; public int dmICMIntent;
        public int dmMediaType; public int dmDitherType; public int dmReserved1; public int dmReserved2;
        public int dmPanningWidth; public int dmPanningHeight;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DISPLAY_DEVICE {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)]  public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet=CharSet.Ansi)]
    static extern int EnumDisplaySettings(string d, int m, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet=CharSet.Ansi)]
    static extern int ChangeDisplaySettingsEx(string d, ref DEVMODE dm, IntPtr h, int f, IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Ansi)]
    static extern bool EnumDisplayDevices(string d, uint i, ref DISPLAY_DEVICE dd, uint f);

    const int ENUM_CURRENT_SETTINGS         = -1;
    const int DISPLAY_DEVICE_ATTACHED       = 0x00000001;
    const int DISPLAY_DEVICE_PRIMARY        = 0x00000004;
    const int CDS_UPDATEREGISTRY            = 0x00000001;
    const int DM_DISPLAYORIENTATION         = 0x00000080;
    const int DM_PELSWIDTH                  = 0x00080000;
    const int DM_PELSHEIGHT                 = 0x00100000;

    // ---------- CCD (Connecting and Configuring Displays) ----------
    // Нужен, чтобы отличить встроенную панель ноутбука от внешнего монитора.
    // EnumDisplayDevices такого не умеет: там есть только флаг "основной".

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_SOURCE_INFO {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_TARGET_INFO {
        public LUID adapterId; public uint id; public uint modeInfoIdx;
        public uint outputTechnology; public uint rotation; public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering;
        public uint targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_INFO {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // Содержимое union нам не нужно, важен только размер структуры (64 байта).
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_MODE_INFO {
        public uint infoType; public uint id; public LUID adapterId;
        public ulong d0, d1, d2, d3, d4, d5;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_DEVICE_INFO_HEADER {
        public uint type; public uint size; public LUID adapterId; public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_SOURCE_DEVICE_NAME {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_TARGET_DEVICE_NAME {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)]  public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPath,
        [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint numMode,
        [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);
    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME r);
    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME r);

    const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    const uint DEVICE_INFO_GET_SOURCE_NAME = 1;
    const uint DEVICE_INFO_GET_TARGET_NAME = 2;

    // Типы выхода, означающие встроенную панель
    const uint OUT_INTERNAL            = 0x80000000;
    const uint OUT_DISPLAYPORT_EMBEDDED = 11;
    const uint OUT_UDI_EMBEDDED         = 13;

    static bool IsEmbedded(uint tech) {
        return tech == OUT_INTERNAL || tech == OUT_DISPLAYPORT_EMBEDDED || tech == OUT_UDI_EMBEDDED;
    }

    // ---------- перечисление экранов ----------

    static readonly object gate = new object();
    static List<ScreenInfo> cache;
    static int cacheStamp;

    public static void Invalidate() {
        lock (gate) { cache = null; }
    }

    public static List<ScreenInfo> Screens() {
        lock (gate) {
            int now = Environment.TickCount;
            if (cache != null && unchecked(now - cacheStamp) < 2000) return cache;
            cache = Enumerate();
            cacheStamp = now;
            return cache;
        }
    }

    static List<ScreenInfo> Enumerate() {
        List<ScreenInfo> list = new List<ScreenInfo>();

        DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++) {
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED) != 0) {
                ScreenInfo si = new ScreenInfo();
                si.Device = dd.DeviceName;
                si.IsPrimary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY) != 0;
                DEVMODE dm = New();
                if (EnumDisplaySettings(si.Device, ENUM_CURRENT_SETTINGS, ref dm) != 0) {
                    si.Width = dm.dmPelsWidth; si.Height = dm.dmPelsHeight;
                }
                list.Add(si);
            }
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
        }

        // Дополняем сведениями из CCD: имя монитора и признак встроенности.
        try {
            uint np, nm;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm) == 0 && np > 0) {
                DISPLAYCONFIG_PATH_INFO[] paths = new DISPLAYCONFIG_PATH_INFO[np];
                DISPLAYCONFIG_MODE_INFO[] modes = new DISPLAYCONFIG_MODE_INFO[nm];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) == 0) {
                    for (int p = 0; p < np; p++) {
                        DISPLAYCONFIG_SOURCE_DEVICE_NAME src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                        src.header.type = DEVICE_INFO_GET_SOURCE_NAME;
                        src.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
                        src.header.adapterId = paths[p].sourceInfo.adapterId;
                        src.header.id = paths[p].sourceInfo.id;
                        if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                        ScreenInfo si = null;
                        foreach (ScreenInfo s in list)
                            if (string.Equals(s.Device, src.viewGdiDeviceName, StringComparison.OrdinalIgnoreCase)) { si = s; break; }
                        if (si == null) continue;

                        if (IsEmbedded(paths[p].targetInfo.outputTechnology)) si.IsInternal = true;

                        DISPLAYCONFIG_TARGET_DEVICE_NAME tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                        tgt.header.type = DEVICE_INFO_GET_TARGET_NAME;
                        tgt.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
                        tgt.header.adapterId = paths[p].targetInfo.adapterId;
                        tgt.header.id = paths[p].targetInfo.id;
                        if (DisplayConfigGetDeviceInfo(ref tgt) == 0) {
                            if (IsEmbedded(tgt.outputTechnology)) si.IsInternal = true;
                            if (string.IsNullOrEmpty(si.Friendly) && !string.IsNullOrEmpty(tgt.monitorFriendlyDeviceName))
                                si.Friendly = tgt.monitorFriendlyDeviceName.Trim();
                        }
                    }
                }
            }
        } catch { /* CCD недоступен - обойдёмся без имён и признака встроенности */ }

        return list;
    }

    // Имя устройства, которое надо крутить.
    // pref пустой - автоматически: встроенная панель, иначе основной экран.
    public static string Resolve(string pref) {
        List<ScreenInfo> list = Screens();
        if (list.Count == 0) return null;

        if (!string.IsNullOrEmpty(pref))
            foreach (ScreenInfo s in list)
                if (string.Equals(s.Device, pref, StringComparison.OrdinalIgnoreCase)) return s.Device;

        foreach (ScreenInfo s in list) if (s.IsInternal) return s.Device;
        foreach (ScreenInfo s in list) if (s.IsPrimary)  return s.Device;
        return list[0].Device;
    }

    public static ScreenInfo Info(string device) {
        if (device == null) return null;
        foreach (ScreenInfo s in Screens())
            if (string.Equals(s.Device, device, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    // ---------- чтение и смена ориентации ----------

    static DEVMODE New() {
        DEVMODE dm = new DEVMODE();
        dm.dmDeviceName = new string(new char[32]);
        dm.dmFormName = new string(new char[32]);
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        return dm;
    }

    public static int Get(string device) {
        if (device == null) return -1;
        DEVMODE dm = New();
        if (EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm) == 0) return -1;
        return dm.dmDisplayOrientation;
    }

    // Возвращает 0 при успехе, 1 если экран уже так повёрнут, иначе код ошибки.
    public static int Set(string device, int o) {
        if (device == null) return -101;
        DEVMODE dm = New();
        if (EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm) == 0) return -102;
        int cur = dm.dmDisplayOrientation;
        if (cur == o) return 1;
        if (((cur + o) % 2) == 1) { int t = dm.dmPelsWidth; dm.dmPelsWidth = dm.dmPelsHeight; dm.dmPelsHeight = t; }
        dm.dmDisplayOrientation = o;
        dm.dmFields = DM_DISPLAYORIENTATION | DM_PELSWIDTH | DM_PELSHEIGHT;
        int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        Invalidate();
        return r;
    }
}

public class MainForm : Form {

    // Фоновый скрипт: только читает акселерометр и печатает X;Y;Z.
    // Никакой логики поворота здесь нет - она целиком в классе Rot.
    const string WORKER = @"
param([int]$Interval = 300)
$ErrorActionPreference = 'Stop'
try {
    [void][Windows.Devices.Sensors.Accelerometer, Windows.Devices.Sensors, ContentType = WindowsRuntime]
    $a = [Windows.Devices.Sensors.Accelerometer]::GetDefault()
} catch {
    [Console]::Out.WriteLine('ERR ' + $_.Exception.Message)
    exit 1
}
if (-not $a) { [Console]::Out.WriteLine('ERR accelerometer not found'); exit 1 }
try { $a.ReportInterval = [Math]::Max($a.MinimumReportInterval, 100) } catch { }
$ic = [Globalization.CultureInfo]::InvariantCulture
while ($true) {
    $r = $null
    try { $r = $a.GetCurrentReading() } catch { }
    if ($r) {
        [Console]::Out.WriteLine(
            $r.AccelerationX.ToString('0.0000', $ic) + ';' +
            $r.AccelerationY.ToString('0.0000', $ic) + ';' +
            $r.AccelerationZ.ToString('0.0000', $ic))
        [Console]::Out.Flush()
    }
    Start-Sleep -Milliseconds $Interval
}
";

    const double THRESHOLD = 0.55;  // порог наклона
    const double FLAT      = 0.88;  // выше - планшет лежит плашмя, поворот не трогаем

    Process worker;
    Label lblState, lblOrient, lblTarget;
    Button btnToggle;
    CheckBox chkSwap, chkAutostart;
    ComboBox cmbManual, cmbScreen;
    NotifyIcon tray;
    Timer timer;
    string scriptPath;
    bool reallyClose = false;
    bool fillingScreens = false;
    bool screensDirty = false;

    // читаются из фонового потока
    volatile string prefDevice = "";
    volatile bool swapFlag = true;
    volatile bool sensorError = false;
    volatile string sensorMessage = null;
    int pending = -1, streak = 0, lastApplied = -1;

    static readonly string[] NAMES = {
        "альбомная", "книжная", "альбомная (перевёрнутая)", "книжная (перевёрнутая)"
    };

    [STAThread]
    static void Main() {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }

    public MainForm() {
        Text = "Автоповорот экрана";
        ClientSize = new Size(380, 356);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        prefDevice = LoadScreen();
        swapFlag = LoadSwap();

        lblState = new Label();
        lblState.Location = new Point(16, 16);
        lblState.Size = new Size(348, 22);
        lblState.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        Controls.Add(lblState);

        lblTarget = new Label();
        lblTarget.Location = new Point(16, 40);
        lblTarget.Size = new Size(348, 20);
        lblTarget.ForeColor = Color.DimGray;
        Controls.Add(lblTarget);

        lblOrient = new Label();
        lblOrient.Location = new Point(16, 60);
        lblOrient.Size = new Size(348, 20);
        lblOrient.ForeColor = Color.DimGray;
        Controls.Add(lblOrient);

        btnToggle = new Button();
        btnToggle.Location = new Point(16, 88);
        btnToggle.Size = new Size(348, 42);
        btnToggle.Click += new EventHandler(OnToggle);
        Controls.Add(btnToggle);

        Label ls = new Label();
        ls.Location = new Point(16, 145);
        ls.Size = new Size(54, 22);
        ls.Text = "Экран:";
        Controls.Add(ls);

        cmbScreen = new ComboBox();
        cmbScreen.Location = new Point(74, 142);
        cmbScreen.Size = new Size(290, 24);
        cmbScreen.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbScreen.SelectedIndexChanged += new EventHandler(OnScreenChanged);
        Controls.Add(cmbScreen);
        FillScreens();

        chkSwap = new CheckBox();
        chkSwap.Location = new Point(16, 174);
        chkSwap.Size = new Size(348, 22);
        chkSwap.Text = "Поменять лево и право местами";
        chkSwap.Checked = swapFlag;
        chkSwap.CheckedChanged += new EventHandler(OnSwapChanged);
        Controls.Add(chkSwap);

        chkAutostart = new CheckBox();
        chkAutostart.Location = new Point(16, 198);
        chkAutostart.Size = new Size(348, 22);
        chkAutostart.Text = "Запускать при входе в Windows";
        chkAutostart.Checked = IsAutostart();
        chkAutostart.CheckedChanged += new EventHandler(OnAutostartChanged);
        Controls.Add(chkAutostart);

        Label lm = new Label();
        lm.Location = new Point(16, 233);
        lm.Size = new Size(120, 22);
        lm.Text = "Повернуть вручную:";
        Controls.Add(lm);

        cmbManual = new ComboBox();
        cmbManual.Location = new Point(140, 230);
        cmbManual.Size = new Size(224, 24);
        cmbManual.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbManual.Items.AddRange(NAMES);
        cmbManual.SelectedIndexChanged += new EventHandler(OnManual);
        Controls.Add(cmbManual);

        Label hint = new Label();
        hint.Location = new Point(16, 262);
        hint.Size = new Size(348, 86);
        hint.ForeColor = Color.DimGray;
        hint.Text = "Крутится только выбранный экран, внешний монитор\n" +
                    "остаётся как был. В режиме дублирования экранов\n" +
                    "повернётся всё - это ограничение Windows.\n" +
                    "Закрытие окна сворачивает программу в трей.\n" +
                    "Не крутится? Проверь Блокировку вращения в Параметрах.";
        Controls.Add(hint);

        Icon appIcon;
        try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { appIcon = SystemIcons.Application; }
        this.Icon = appIcon;

        tray = new NotifyIcon();
        tray.Icon = appIcon;
        tray.Text = "Автоповорот экрана";
        tray.Visible = true;
        tray.DoubleClick += new EventHandler(OnTrayClick);
        ContextMenu cm = new ContextMenu();
        cm.MenuItems.Add("Показать окно", new EventHandler(OnTrayClick));
        cm.MenuItems.Add("-");
        cm.MenuItems.Add("Выход", new EventHandler(OnExit));
        tray.ContextMenu = cm;

        scriptPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoRotate");
        Directory.CreateDirectory(scriptPath);
        scriptPath = Path.Combine(scriptPath, "worker.ps1");
        File.WriteAllText(scriptPath, WORKER, new UTF8Encoding(true));

        timer = new Timer();
        timer.Interval = 1000;
        timer.Tick += new EventHandler(OnTick);
        timer.Start();

        UpdateUi();

        // Ключ /auto - сразу запустить слежение (используется для автозапуска)
        foreach (string a in Environment.GetCommandLineArgs()) {
            if (a.ToLower() == "/auto") { StartWorker(); WindowState = FormWindowState.Minimized; ShowInTaskbar = false; Hide(); }
        }
    }

    // ---------- фоновый процесс ----------

    void StartWorker() {
        if (IsRunning()) return;
        sensorError = false; sensorMessage = null;
        pending = -1; streak = 0; lastApplied = -1;
        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = "powershell.exe";
        psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"";
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.StandardOutputEncoding = Encoding.ASCII;
        try {
            worker = Process.Start(psi);
            worker.OutputDataReceived += new DataReceivedEventHandler(OnSensorData);
            worker.BeginOutputReadLine();
        }
        catch (Exception ex) {
            worker = null;
            MessageBox.Show("Не удалось запустить фоновый процесс:\n" + ex.Message,
                            "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateUi();
    }

    void StopWorker() {
        if (worker != null) {
            try { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(2000); } } catch { }
        }
        worker = null;
        UpdateUi();
    }

    bool IsRunning() {
        return worker != null && !worker.HasExited;
    }

    // Вызывается из фонового потока чтения stdout.
    void OnSensorData(object sender, DataReceivedEventArgs e) {
        if (e.Data == null) return;
        string line = e.Data.Trim();
        if (line.Length == 0) return;

        if (line.StartsWith("ERR")) {
            sensorError = true;
            sensorMessage = line.Length > 4 ? line.Substring(4) : "датчик недоступен";
            return;
        }

        string[] p = line.Split(';');
        if (p.Length != 3) return;
        double x, y, z;
        if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return;
        if (!double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return;
        if (!double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return;
        sensorError = false;
        Apply(x, y, z);
    }

    void Apply(double x, double y, double z) {
        if (Math.Abs(z) > FLAT) return;   // лежит плашмя - ориентацию не определить

        bool swap = swapFlag;
        int cw  = swap ? 3 : 1;
        int ccw = swap ? 1 : 3;

        int target = -1;
        if      (y < -THRESHOLD) target = 0;
        else if (y >  THRESHOLD) target = 2;
        else if (x < -THRESHOLD) target = cw;
        else if (x >  THRESHOLD) target = ccw;
        if (target < 0) return;

        if (target == pending) streak++; else { pending = target; streak = 1; }
        if (streak < 2 || target == lastApplied) return;

        string dev = Rot.Resolve(prefDevice);
        if (dev == null) return;
        if (Rot.Set(dev, target) <= 1) lastApplied = target;
    }

    // ---------- обработчики ----------

    void OnToggle(object s, EventArgs e) {
        if (IsRunning()) StopWorker(); else StartWorker();
    }

    void OnSwapChanged(object s, EventArgs e) {
        swapFlag = chkSwap.Checked;
        SaveSwap(swapFlag);
        lastApplied = -1;   // пересчитать на следующем отсчёте, перезапуск не нужен
    }

    void OnScreenChanged(object s, EventArgs e) {
        if (fillingScreens) return;
        int i = cmbScreen.SelectedIndex;
        List<ScreenInfo> list = Rot.Screens();
        prefDevice = (i <= 0 || i > list.Count) ? "" : list[i - 1].Device;
        SaveScreen(prefDevice);
        lastApplied = -1;
        UpdateUi();
    }

    void OnManual(object s, EventArgs e) {
        int i = cmbManual.SelectedIndex;
        if (i < 0) return;
        if (IsRunning()) {
            MessageBox.Show("Сначала выключи автоповорот, иначе он тут же вернёт экран обратно.",
                            "Автоповорот включён", MessageBoxButtons.OK, MessageBoxIcon.Information);
            cmbManual.SelectedIndex = -1;
            return;
        }
        int r = Rot.Set(Rot.Resolve(prefDevice), i);
        if (r != 0 && r != 1) {
            MessageBox.Show("Повернуть не удалось, код " + r +
                            ".\nПроверь Блокировку вращения в Параметрах.",
                            "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void OnTick(object s, EventArgs e) {
        if (screensDirty) { screensDirty = false; Rot.Invalidate(); FillScreens(); }
        UpdateUi();
    }

    void OnTrayClick(object s, EventArgs e) {
        Show(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; Activate();
    }

    void OnExit(object s, EventArgs e) {
        reallyClose = true;
        StopWorker();
        tray.Visible = false;
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (!reallyClose && e.CloseReason == CloseReason.UserClosing) {
            e.Cancel = true;
            Hide(); ShowInTaskbar = false;
            tray.ShowBalloonTip(2000, "Автоповорот экрана",
                "Программа свёрнута в трей и продолжает работать.", ToolTipIcon.Info);
            return;
        }
        StopWorker();
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    // Подключили или отключили монитор - перечитать список экранов.
    protected override void WndProc(ref Message m) {
        const int WM_DISPLAYCHANGE = 0x007E;
        if (m.Msg == WM_DISPLAYCHANGE) {
            Rot.Invalidate();
            lastApplied = -1;
            screensDirty = true;
        }
        base.WndProc(ref m);
    }

    // ---------- интерфейс ----------

    void FillScreens() {
        fillingScreens = true;
        try {
            List<ScreenInfo> list = Rot.Screens();
            cmbScreen.Items.Clear();
            cmbScreen.Items.Add("Встроенный экран (авто)");
            int sel = 0;
            for (int i = 0; i < list.Count; i++) {
                cmbScreen.Items.Add(list[i].ToString());
                if (string.Equals(list[i].Device, prefDevice, StringComparison.OrdinalIgnoreCase)) sel = i + 1;
            }
            if (!string.IsNullOrEmpty(prefDevice) && sel == 0) {
                // сохранённый экран отключили - вернёмся к автоопределению
                prefDevice = ""; SaveScreen("");
            }
            cmbScreen.SelectedIndex = sel;
        } finally { fillingScreens = false; }
    }

    void UpdateUi() {
        bool run = IsRunning();

        if (run && sensorError) {
            lblState.Text = "Автоповорот: ДАТЧИК НЕ ОТВЕЧАЕТ";
            lblState.ForeColor = Color.FromArgb(180, 90, 0);
        } else {
            lblState.Text = run ? "Автоповорот: ВКЛЮЧЁН" : "Автоповорот: выключен";
            lblState.ForeColor = run ? Color.FromArgb(0, 128, 0) : Color.FromArgb(160, 0, 0);
        }
        btnToggle.Text = run ? "Выключить" : "Включить";

        string dev = Rot.Resolve(prefDevice);
        ScreenInfo si = Rot.Info(dev);
        lblTarget.Text = "Поворачиваю: " + (si != null ? si.ToString() : "экран не найден");

        if (run && sensorError && sensorMessage != null) {
            lblOrient.Text = sensorMessage;
        } else {
            int o = Rot.Get(dev);
            lblOrient.Text = "Сейчас: " + (o >= 0 && o < 4 ? NAMES[o] : "неизвестно");
        }

        tray.Text = run ? "Автоповорот включён" : "Автоповорот выключен";
    }

    // ---------- настройки ----------

    const string REG_KEY = @"Software\AutoRotate";
    const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";

    bool LoadSwap() {
        try {
            RegistryKey k = Registry.CurrentUser.OpenSubKey(REG_KEY);
            if (k == null) return true;   // по умолчанию включено
            object v = k.GetValue("Swap");
            return v == null || Convert.ToInt32(v) != 0;
        } catch { return true; }
    }

    void SaveSwap(bool v) {
        try {
            RegistryKey k = Registry.CurrentUser.CreateSubKey(REG_KEY);
            k.SetValue("Swap", v ? 1 : 0, RegistryValueKind.DWord);
        } catch { }
    }

    string LoadScreen() {
        try {
            RegistryKey k = Registry.CurrentUser.OpenSubKey(REG_KEY);
            if (k == null) return "";
            object v = k.GetValue("Screen");
            return v == null ? "" : v.ToString();
        } catch { return ""; }
    }

    void SaveScreen(string v) {
        try {
            RegistryKey k = Registry.CurrentUser.CreateSubKey(REG_KEY);
            k.SetValue("Screen", v == null ? "" : v, RegistryValueKind.String);
        } catch { }
    }

    bool IsAutostart() {
        try {
            RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY);
            return k != null && k.GetValue("AutoRotate") != null;
        } catch { return false; }
    }

    void OnAutostartChanged(object s, EventArgs e) {
        try {
            RegistryKey k = Registry.CurrentUser.CreateSubKey(RUN_KEY);
            if (chkAutostart.Checked)
                k.SetValue("AutoRotate", "\"" + Application.ExecutablePath + "\" /auto");
            else
                k.DeleteValue("AutoRotate", false);
        } catch (Exception ex) {
            MessageBox.Show("Не удалось изменить автозапуск:\n" + ex.Message,
                            "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
