using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FloatBar
{
    // ==========================================
    // WIN32 NATIVE INTEROP
    // ==========================================
    internal static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        // Multi-Monitor Topology Interop
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MONITORINFO
        {
            public int cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        public const int MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, [In, Out] MONITORINFO lpmi);

        // Memory Status
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        // Kernel NT Processor Performance Information (Task Manager's true internal CPU source)
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
        {
            public long IdleTime;
            public long KernelTime;
            public long UserTime;
            public long DpcTime;
            public long InterruptTime;
            public uint InterruptCount;
        }

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(
            int SystemInformationClass,
            IntPtr SystemInformation,
            int SystemInformationLength,
            out int ReturnLength);
    }

    // ==========================================
    // DAEMON METADATA
    // ==========================================
    public class DaemonInfo
    {
        public int Pid;
        public string Name = "node.exe";
        public string Description = "MCP Server";
        public double WorkingSetMB = 0.0;
    }

    // ==========================================
    // TELEMETRY SNAPSHOT
    // ==========================================
    public class TelemetrySnapshot
    {
        public double CpuPercent = 0.0;
        public double RamUsedGB = 0.0;
        public double RamTotalGB = 0.0;
        public double RamAvailGB = 0.0;
        public double RamPercent = 0.0;
        public double CommitTotalGB = 0.0;
        public double CommitUsedGB = 0.0;
        public double CommitAvailGB = 0.0;
        public double CommitPercent = 0.0;
        public bool IsAnomaly = false;
        public string AnomalyMessage = string.Empty;
        public bool IsSaturated = false;
        public double SustainedSeconds = 0.0;
        public List<DaemonInfo> OrphanDaemons = new List<DaemonInfo>();
    }

    // ==========================================
    // NT KERNEL TELEMETRY ENGINE
    // ==========================================
    public class TelemetryEngine
    {
        private volatile TelemetrySnapshot _currentSnapshot;
        private Thread _workerThread;
        private volatile bool _isRunning = false;

        private readonly int _processorCount;
        private readonly int _structSize;
        private readonly int _bufferSize;
        private IntPtr _pPrevBuffer;
        private IntPtr _pCurrBuffer;
        private bool _isFirstSample = true;

        private int _cpuHighTicks = 0;
        private int _ramHighTicks = 0;
        private DateTime? _saturationStartTime = null;
        private bool _isInCooldown = false;
        private DateTime _cooldownEndTime = DateTime.MinValue;
        private int _orphanScanCounter = 0;

        public TelemetrySnapshot CurrentSnapshot
        {
            get { return _currentSnapshot; }
        }

        public TelemetryEngine()
        {
            _currentSnapshot = new TelemetrySnapshot();
            _processorCount = Environment.ProcessorCount;
            _structSize = Marshal.SizeOf(typeof(NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION));
            _bufferSize = _structSize * _processorCount;

            _pPrevBuffer = Marshal.AllocHGlobal(_bufferSize);
            _pCurrBuffer = Marshal.AllocHGlobal(_bufferSize);
        }

        ~TelemetryEngine()
        {
            if (_pPrevBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_pPrevBuffer); _pPrevBuffer = IntPtr.Zero; }
            if (_pCurrBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_pCurrBuffer); _pCurrBuffer = IntPtr.Zero; }
        }

        public void Start()
        {
            _isRunning = true;
            _workerThread = new Thread(WorkerLoop);
            _workerThread.IsBackground = true;
            _workerThread.Priority = ThreadPriority.Normal;
            _workerThread.Start();
        }

        public void Stop()
        {
            _isRunning = false;
        }

        private void WorkerLoop()
        {
            // Initial warm-up baseline
            int retLen;
            NativeMethods.NtQuerySystemInformation(8, _pPrevBuffer, _bufferSize, out retLen);
            Thread.Sleep(500);

            var sw = new Stopwatch();
            while (_isRunning)
            {
                try
                {
                    sw.Restart();
                    SampleMetrics();
                    sw.Stop();

                    int elapsed = (int)sw.ElapsedMilliseconds;
                    int sleepMs = Math.Max(100, 1000 - elapsed);
                    Thread.Sleep(sleepMs);
                }
                catch
                {
                    Thread.Sleep(1000);
                }
            }
        }

        private void SampleMetrics()
        {
            var snap = new TelemetrySnapshot();

            // 1. Task Manager Exact CPU Calculation via NtQuerySystemInformation(8)
            int retLen;
            int status = NativeMethods.NtQuerySystemInformation(8, _pCurrBuffer, _bufferSize, out retLen);
            if (status == 0)
            {
                if (_isFirstSample)
                {
                    byte[] initialCopy = new byte[_bufferSize];
                    Marshal.Copy(_pCurrBuffer, initialCopy, 0, _bufferSize);
                    Marshal.Copy(initialCopy, 0, _pPrevBuffer, _bufferSize);
                    _isFirstSample = false;
                }
                else
                {
                    long totalBusy = 0;
                    long totalTime = 0;

                    for (int i = 0; i < _processorCount; i++)
                    {
                        IntPtr ptrPrev = new IntPtr(_pPrevBuffer.ToInt64() + i * _structSize);
                        IntPtr ptrCurr = new IntPtr(_pCurrBuffer.ToInt64() + i * _structSize);

                        var prev = (NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION)Marshal.PtrToStructure(ptrPrev, typeof(NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION));
                        var curr = (NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION)Marshal.PtrToStructure(ptrCurr, typeof(NativeMethods.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION));

                        long dIdle = curr.IdleTime - prev.IdleTime;
                        long dKernel = curr.KernelTime - prev.KernelTime;
                        long dUser = curr.UserTime - prev.UserTime;

                        long coreBusy = Math.Max(0, (dKernel - dIdle)) + Math.Max(0, dUser);
                        long coreTotal = dKernel + dUser;

                        if (coreTotal > 0)
                        {
                            totalBusy += coreBusy;
                            totalTime += coreTotal;
                        }
                    }

                    // Copy current to prev
                    byte[] copyBuf = new byte[_bufferSize];
                    Marshal.Copy(_pCurrBuffer, copyBuf, 0, _bufferSize);
                    Marshal.Copy(copyBuf, 0, _pPrevBuffer, _bufferSize);

                    double cpu = totalTime > 0 ? ((double)totalBusy / totalTime) * 100.0 : 0.0;
                    if (cpu < 0.0) cpu = 0.0;
                    if (cpu > 100.0) cpu = 100.0;
                    snap.CpuPercent = cpu;
                }
            }

            // 2. Exact Physical RAM & Virtual Commit Telemetry via GlobalMemoryStatusEx
            try
            {
                var mem = new NativeMethods.MEMORYSTATUSEX();
                if (NativeMethods.GlobalMemoryStatusEx(mem))
                {
                    double totalPhysGb = (double)mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                    double availPhysGb = (double)mem.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                    double usedPhysGb = Math.Max(0.0, totalPhysGb - availPhysGb);
                    double physPct = (double)mem.dwMemoryLoad;

                    double totalCommitGb = (double)mem.ullTotalPageFile / (1024.0 * 1024.0 * 1024.0);
                    double availCommitGb = (double)mem.ullAvailPageFile / (1024.0 * 1024.0 * 1024.0);
                    double usedCommitGb = Math.Max(0.0, totalCommitGb - availCommitGb);
                    double commitPct = totalCommitGb > 0 ? (usedCommitGb / totalCommitGb) * 100.0 : 0.0;

                    snap.RamTotalGB = totalPhysGb;
                    snap.RamUsedGB = usedPhysGb;
                    snap.RamAvailGB = availPhysGb;
                    snap.RamPercent = physPct;

                    snap.CommitTotalGB = totalCommitGb;
                    snap.CommitUsedGB = usedCommitGb;
                    snap.CommitAvailGB = availCommitGb;
                    snap.CommitPercent = commitPct;
                }
            }
            catch { }

            // Periodically scan for orphan daemons (every 5 ticks = ~5s)
            _orphanScanCounter++;
            if (_orphanScanCounter >= 5)
            {
                _orphanScanCounter = 0;
                snap.OrphanDaemons = GovernorController.ScanOrphanDaemons();
            }
            else if (_currentSnapshot != null)
            {
                snap.OrphanDaemons = _currentSnapshot.OrphanDaemons;
            }

            // 3. Sustained Saturation & Governor Logic (Wall-Clock Precision)
            bool isSaturated = (snap.RamPercent >= 95.0) ||
                               (snap.CommitPercent >= 95.0) ||
                               (snap.RamAvailGB < 0.4) ||
                               (snap.CommitAvailGB < 0.5) ||
                               (snap.CpuPercent >= 98.0);

            if (isSaturated)
            {
                if (_saturationStartTime == null)
                    _saturationStartTime = DateTime.UtcNow;

                double elapsedSec = (DateTime.UtcNow - _saturationStartTime.Value).TotalSeconds;
                snap.SustainedSeconds = elapsedSec;

                if (elapsedSec >= 150.0)
                {
                    snap.IsSaturated = true;
                    if (!_isInCooldown)
                    {
                        EmitCriticalState(snap);
                        _isInCooldown = true;
                        _cooldownEndTime = DateTime.UtcNow.AddSeconds(300); // 5-minute cooldown
                    }
                }
            }
            else
            {
                _saturationStartTime = null;
                snap.SustainedSeconds = 0.0;
                snap.IsSaturated = false;

                if (snap.RamPercent < 88.0 && snap.CommitPercent < 88.0)
                {
                    ClearStateFiles();
                }
            }

            if (_isInCooldown && DateTime.UtcNow >= _cooldownEndTime)
            {
                _isInCooldown = false;
            }

            // 4. Intelligent Anomaly Message Generation
            if (snap.IsSaturated)
            {
                snap.IsAnomaly = true;
                snap.AnomalyMessage = string.Format("CRITICAL GOVERNOR SATURATION: {0:0}s sustained! RAM: {1:0}%, Commit: {2:0}%", snap.SustainedSeconds, snap.RamPercent, snap.CommitPercent);
            }
            else
            {
                if (snap.CpuPercent >= 95.0) _cpuHighTicks++;
                else _cpuHighTicks = Math.Max(0, _cpuHighTicks - 1);

                if (snap.RamPercent >= 98.0 && snap.RamAvailGB < 0.25) _ramHighTicks++;
                else _ramHighTicks = Math.Max(0, _ramHighTicks - 1);

                if (_cpuHighTicks >= 3 && _ramHighTicks >= 3)
                {
                    snap.IsAnomaly = true;
                    snap.AnomalyMessage = string.Format("CRITICAL: CPU ({0:0}%) & RAM ({1:0}%) pegged!", snap.CpuPercent, snap.RamPercent);
                }
                else if (_cpuHighTicks >= 3)
                {
                    snap.IsAnomaly = true;
                    snap.AnomalyMessage = string.Format("HIGH LOAD: CPU pegged at {0:0.0}%!", snap.CpuPercent);
                }
                else if (_ramHighTicks >= 3)
                {
                    snap.IsAnomaly = true;
                    snap.AnomalyMessage = string.Format("MEMORY EXHAUSTION: RAM at {0:0.0}% ({1:0.#} GB available)!", snap.RamPercent, snap.RamAvailGB);
                }
                else
                {
                    snap.IsAnomaly = false;
                    snap.AnomalyMessage = string.Empty;
                }
            }

            _currentSnapshot = snap;
        }

        private void EmitCriticalState(TelemetrySnapshot snap)
        {
            try
            {
                string agentsDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents");
                if (!System.IO.Directory.Exists(agentsDir)) System.IO.Directory.CreateDirectory(agentsDir);
                string agentsStatePath = System.IO.Path.Combine(agentsDir, "system_health.json");

                string geminiDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config");
                if (!System.IO.Directory.Exists(geminiDir)) System.IO.Directory.CreateDirectory(geminiDir);
                string geminiStatePath = System.IO.Path.Combine(geminiDir, "system_health.json");

                string json = BuildStateJson(snap);

                WriteFileAtomic(agentsStatePath, json);
                WriteFileAtomic(geminiStatePath, json);
            }
            catch { }
        }

        private void ClearStateFiles()
        {
            try
            {
                string agentsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "system_health.json");
                if (System.IO.File.Exists(agentsPath)) System.IO.File.Delete(agentsPath);

                string geminiPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config", "system_health.json");
                if (System.IO.File.Exists(geminiPath)) System.IO.File.Delete(geminiPath);
            }
            catch { }
        }

        private static string BuildStateJson(TelemetrySnapshot snap)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"status\": \"CRITICAL\",");
            sb.AppendLine("  \"is_saturated\": true,");
            sb.AppendLine(string.Format("  \"timestamp\": \"{0:o}\",", DateTime.UtcNow));
            sb.AppendLine("  \"metrics\": {");
            sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "    \"cpu_percent\": {0:0.0},", snap.CpuPercent));
            sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "    \"ram_percent\": {0:0.0},", snap.RamPercent));
            sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "    \"ram_available_gb\": {0:0.00},", snap.RamAvailGB));
            sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "    \"commit_percent\": {0:0.0},", snap.CommitPercent));
            sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "    \"commit_available_gb\": {0:0.00}", snap.CommitAvailGB));
            sb.AppendLine("  },");
            sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "  \"sustained_seconds\": {0:0.0},", snap.SustainedSeconds));
            sb.Append("  \"orphan_pids\": [");
            if (snap.OrphanDaemons != null && snap.OrphanDaemons.Count > 0)
            {
                sb.Append(string.Join(", ", snap.OrphanDaemons.ConvertAll(d => d.Pid.ToString()).ToArray()));
            }
            sb.AppendLine("],");
            sb.AppendLine("  \"recommendation\": \"Pause parallel subagent spawns. Terminate idle MCP daemons. Sequentialize tasks.\"");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void WriteFileAtomic(string destPath, string content)
        {
            string tmpPath = destPath + ".tmp." + Guid.NewGuid().ToString("N");
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    File.WriteAllText(tmpPath, content);
                    if (File.Exists(destPath)) File.Delete(destPath);
                    File.Move(tmpPath, destPath);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
                finally
                {
                    if (File.Exists(tmpPath))
                    {
                        try { File.Delete(tmpPath); } catch { }
                    }
                }
            }
        }
    }

    // ==========================================
    // GOVERNOR CONTROLLER & DAEMON RECLAIMER
    // ==========================================
    public static class GovernorController
    {
        public static List<DaemonInfo> ScanOrphanDaemons()
        {
            var list = new List<DaemonInfo>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'node.exe'"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        try
                        {
                            object cmdObj = mo["CommandLine"];
                            if (cmdObj != null)
                            {
                                string cmd = cmdObj.ToString().ToLowerInvariant();
                                if (cmd.Contains("chrome-devtools-mcp") || cmd.Contains("puppeteer") || cmd.Contains("playwright") || cmd.Contains("@modelcontextprotocol"))
                                {
                                    object pidObj = mo["ProcessId"];
                                    if (pidObj != null)
                                    {
                                        int pid = Convert.ToInt32(pidObj);
                                        double wsMb = 0.0;
                                        try
                                        {
                                            var p = Process.GetProcessById(pid);
                                            wsMb = p.WorkingSet64 / (1024.0 * 1024.0);
                                        }
                                        catch { }

                                        string desc = "chrome-devtools-mcp";
                                        if (cmd.Contains("puppeteer")) desc = "puppeteer-daemon";
                                        else if (cmd.Contains("playwright")) desc = "playwright-worker";
                                        else if (cmd.Contains("@modelcontextprotocol")) desc = "mcp-server";

                                        list.Add(new DaemonInfo
                                        {
                                            Pid = pid,
                                            Name = "node.exe",
                                            Description = desc,
                                            WorkingSetMB = wsMb
                                        });
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return list;
        }

        public static int ReclaimOrphanDaemons()
        {
            var list = ScanOrphanDaemons();
            int count = 0;
            foreach (var d in list)
            {
                try
                {
                    var proc = Process.GetProcessById(d.Pid);
                    proc.Kill();
                    count++;
                }
                catch { }
            }
            return count;
        }
    }

    // ==========================================
    // CONFIGURATION STORAGE
    // ==========================================
    public class WindowConfig
    {
        public double IconLeft;
        public double IconTop;

        public WindowConfig()
        {
            IconLeft = -1;
            IconTop = -1;
        }

        private static string GetConfigPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = System.IO.Path.Combine(appData, "FloatBar");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "settings.txt");
        }

        public static WindowConfig Load()
        {
            var config = new WindowConfig();
            try
            {
                string path = GetConfigPath();
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('=');
                        if (parts.Length == 2)
                        {
                            string key = parts[0].Trim();
                            string val = parts[1].Trim();
                            if (key == "IconLeft") config.IconLeft = double.Parse(val);
                            if (key == "IconTop") config.IconTop = double.Parse(val);
                        }
                    }
                }
            }
            catch { }
            return config;
        }

        public void Save()
        {
            try
            {
                string path = GetConfigPath();
                string content = string.Format("IconLeft={0}\nIconTop={1}\n", IconLeft, IconTop);
                File.WriteAllText(path, content);
            }
            catch { }
        }
    }

    // ==========================================
    // SEAMLESS FLOATING WINDOW
    // ==========================================
    public class MainWindow : Window
    {
        private const double HITBOX_SIZE = 44.0;
        private const double ORB_IDLE_SIZE = 26.0;
        private const double ORB_HOVER_SIZE = 30.0;
        private const double POPUP_WIDTH = 285.0;

        private readonly TelemetryEngine _engine;
        private readonly WindowConfig _config;

        private DispatcherTimer _uiRefreshTimer;
        private DispatcherTimer _hideGraceTimer;
        private DispatcherTimer _snapAnimTimer;

        // UI Controls
        private Border _iconContainer;
        private Border _visualOrb;
        private Ellipse _iconStatusDot;
        private DropShadowEffect _statusGlow;

        private Popup _hudPopup;
        private Border _popupContainer;
        private TextBlock _cpuValueText;
        private TextBlock _cpuStatusBadge;
        private ProgressBar _cpuBar;
        private TextBlock _ramValueText;
        private TextBlock _ramFreeText;
        private TextBlock _commitValueText;
        private ProgressBar _ramBar;
        private Border _anomalyBanner;
        private TextBlock _anomalyText;

        // Process Sentinel UI Controls
        private Border _sentinelCard;
        private TextBlock _sentinelHeaderTitle;
        private Border _sentinelStatusPill;
        private TextBlock _sentinelStatusPillText;
        private StackPanel _sentinelBodyStack;
        private Border _actionButton;
        private TextBlock _actionButtonText;

        public MainWindow()
        {
            _config = WindowConfig.Load();
            _engine = new TelemetryEngine();

            InitializeWindowProperties();
            BuildVisualTree();
            BuildPopupHud();

            _engine.Start();

            // UI Refresh Timer
            _uiRefreshTimer = new DispatcherTimer(DispatcherPriority.Render);
            _uiRefreshTimer.Interval = TimeSpan.FromMilliseconds(400);
            _uiRefreshTimer.Tick += OnUiRefreshTick;
            _uiRefreshTimer.Start();

            // Hover dismissal grace timer
            _hideGraceTimer = new DispatcherTimer();
            _hideGraceTimer.Interval = TimeSpan.FromMilliseconds(350);
            _hideGraceTimer.Tick += (s, e) =>
            {
                _hideGraceTimer.Stop();
                if (!_iconContainer.IsMouseOver && !_popupContainer.IsMouseOver)
                {
                    _hudPopup.IsOpen = false;
                }
            };

            Loaded += (s, e) =>
            {
                try
                {
                    string bootLog = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "boot.log");
                    System.IO.File.AppendAllText(bootLog, string.Format("MainWindow Loaded! Left={0}, Top={1}, Width={2}, Height={3}, IsVisible={4}\n", Left, Top, Width, Height, IsVisible));
                }
                catch { }
            };

            Closing += (s, e) =>
            {
                try
                {
                    string bootLog = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "boot.log");
                    System.IO.File.AppendAllText(bootLog, "MainWindow Closing called!\n");
                }
                catch { }
            };

            Closed += (s, e) =>
            {
                try
                {
                    string bootLog = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "boot.log");
                    System.IO.File.AppendAllText(bootLog, "MainWindow Closed called!\n");
                }
                catch { }
            };
        }

        private void InitializeWindowProperties()
        {
            Title = "FloatBar";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;

            Width = HITBOX_SIZE;
            Height = HITBOX_SIZE;

            double defaultLeft = SystemParameters.WorkArea.Right - HITBOX_SIZE - 10;
            double defaultTop = 80;

            if (_config.IconLeft >= 0 && _config.IconTop >= 0 &&
                _config.IconLeft < SystemParameters.VirtualScreenWidth - 20 &&
                _config.IconTop < SystemParameters.VirtualScreenHeight - 20)
            {
                Left = _config.IconLeft;
                Top = _config.IconTop;
            }
            else
            {
                Left = defaultLeft;
                Top = defaultTop;
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.Handle;

            // WS_EX_TOOLWINDOW: hide from Alt+Tab
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOOLWINDOW);
        }

        private Rect GetActiveMonitorWorkArea(Point dipPoint)
        {
            try
            {
                var source = PresentationSource.FromVisual(this);
                if (source != null && source.CompositionTarget != null)
                {
                    var transformToDevice = source.CompositionTarget.TransformToDevice;
                    var transformFromDevice = source.CompositionTarget.TransformFromDevice;

                    Point physPoint = transformToDevice.Transform(dipPoint);
                    var pt = new NativeMethods.POINT { X = (int)physPoint.X, Y = (int)physPoint.Y };
                    IntPtr hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);

                    var mi = new NativeMethods.MONITORINFO();
                    if (NativeMethods.GetMonitorInfo(hMonitor, mi))
                    {
                        Point dipTopLeft = transformFromDevice.Transform(new Point(mi.rcWork.Left, mi.rcWork.Top));
                        Point dipBottomRight = transformFromDevice.Transform(new Point(mi.rcWork.Right, mi.rcWork.Bottom));
                        return new Rect(dipTopLeft, dipBottomRight);
                    }
                }
            }
            catch { }

            return new Rect(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top,
                            SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
        }

        private void AnimateSnapTo(double targetLeft, double targetTop)
        {
            if (_snapAnimTimer != null) _snapAnimTimer.Stop();

            double startLeft = Left;
            double startTop = Top;
            int step = 0;
            const int totalSteps = 8; // 8 frames over ~130ms

            _snapAnimTimer = new DispatcherTimer(DispatcherPriority.Render);
            _snapAnimTimer.Interval = TimeSpan.FromMilliseconds(16);
            _snapAnimTimer.Tick += (s, e) =>
            {
                step++;
                double t = (double)step / totalSteps;
                // Ease-out quadratic: f(t) = 1 - (1 - t)^2
                double ease = 1.0 - Math.Pow(1.0 - t, 2);

                Left = startLeft + (targetLeft - startLeft) * ease;
                Top = startTop + (targetTop - startTop) * ease;

                if (step >= totalSteps)
                {
                    _snapAnimTimer.Stop();
                    Left = targetLeft;
                    Top = targetTop;
                    _config.IconLeft = Left;
                    _config.IconTop = Top;
                    _config.Save();
                }
            };
            _snapAnimTimer.Start();
        }

        private void BuildVisualTree()
        {
            _iconContainer = new Border
            {
                Width = HITBOX_SIZE,
                Height = HITBOX_SIZE,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            _visualOrb = new Border
            {
                Width = ORB_IDLE_SIZE,
                Height = ORB_IDLE_SIZE,
                CornerRadius = new CornerRadius(ORB_IDLE_SIZE / 2.0),
                Background = new SolidColorBrush(Color.FromArgb(242, 16, 19, 26)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1.2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 10,
                    ShadowDepth = 2,
                    Opacity = 0.7
                }
            };

            _statusGlow = new DropShadowEffect
            {
                Color = Color.FromRgb(52, 211, 153),
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.85
            };

            _iconStatusDot = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(Color.FromRgb(52, 211, 153)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = _statusGlow
            };

            _visualOrb.Child = _iconStatusDot;
            _iconContainer.Child = _visualOrb;

            // Drag and Click Handling with Magnetic Edge Snapping
            Point dragStart = new Point(0, 0);
            _iconContainer.MouseLeftButtonDown += (s, e) =>
            {
                dragStart = PointToScreen(e.GetPosition(this));
                e.Handled = true;

                DragMove();

                Point dragEnd = PointToScreen(e.GetPosition(this));
                double dist = Math.Sqrt(Math.Pow(dragEnd.X - dragStart.X, 2) + Math.Pow(dragEnd.Y - dragStart.Y, 2));

                if (dist < 4.0)
                {
                    _hudPopup.IsOpen = !_hudPopup.IsOpen;
                }
                else
                {
                    // Magnetic Edge Snapping
                    double currentLeft = Left;
                    double currentTop = Top;
                    Rect workArea = GetActiveMonitorWorkArea(new Point(currentLeft + HITBOX_SIZE / 2.0, currentTop + HITBOX_SIZE / 2.0));

                    const double SNAP_ZONE = 36.0;
                    double targetLeft = currentLeft;
                    double targetTop = currentTop;
                    bool didSnap = false;

                    // Left edge snap
                    if (currentLeft - workArea.Left < SNAP_ZONE && currentLeft >= workArea.Left - 10)
                    {
                        targetLeft = workArea.Left + 2.0;
                        didSnap = true;
                    }
                    // Right edge snap
                    else if (workArea.Right - (currentLeft + HITBOX_SIZE) < SNAP_ZONE && currentLeft + HITBOX_SIZE <= workArea.Right + 10)
                    {
                        targetLeft = workArea.Right - HITBOX_SIZE - 2.0;
                        didSnap = true;
                    }

                    // Top edge snap
                    if (currentTop - workArea.Top < SNAP_ZONE && currentTop >= workArea.Top - 10)
                    {
                        targetTop = workArea.Top + 2.0;
                        didSnap = true;
                    }
                    // Bottom edge snap
                    else if (workArea.Bottom - (currentTop + HITBOX_SIZE) < SNAP_ZONE && currentTop + HITBOX_SIZE <= workArea.Bottom + 10)
                    {
                        targetTop = workArea.Bottom - HITBOX_SIZE - 2.0;
                        didSnap = true;
                    }

                    // Clamp to work area
                    targetLeft = Math.Max(workArea.Left, Math.Min(targetLeft, workArea.Right - HITBOX_SIZE));
                    targetTop = Math.Max(workArea.Top, Math.Min(targetTop, workArea.Bottom - HITBOX_SIZE));

                    if (didSnap)
                    {
                        AnimateSnapTo(targetLeft, targetTop);
                    }
                    else
                    {
                        Left = targetLeft;
                        Top = targetTop;
                        _config.IconLeft = Left;
                        _config.IconTop = Top;
                        _config.Save();
                    }
                }
            };

            // Hover Handling: Subtle Tactile Expansion of the Visual Orb
            _iconContainer.MouseEnter += (s, e) =>
            {
                _visualOrb.Width = ORB_HOVER_SIZE;
                _visualOrb.Height = ORB_HOVER_SIZE;
                _visualOrb.CornerRadius = new CornerRadius(ORB_HOVER_SIZE / 2.0);

                _hideGraceTimer.Stop();
                _hudPopup.IsOpen = true;
            };

            _iconContainer.MouseLeave += (s, e) =>
            {
                _visualOrb.Width = ORB_IDLE_SIZE;
                _visualOrb.Height = ORB_IDLE_SIZE;
                _visualOrb.CornerRadius = new CornerRadius(ORB_IDLE_SIZE / 2.0);

                _hideGraceTimer.Stop();
                _hideGraceTimer.Start();
            };

            _iconContainer.ContextMenu = CreateContextMenu();
            Content = _iconContainer;
        }

        private void BuildPopupHud()
        {
            _hudPopup = new Popup
            {
                PlacementTarget = _iconContainer,
                Placement = PlacementMode.Custom,
                AllowsTransparency = true,
                StaysOpen = true,
                PopupAnimation = PopupAnimation.Fade
            };

            // Smart Edge Placement with Monitor Awareness & Hover Bridge
            _hudPopup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            {
                Point screenPt = _iconContainer.PointToScreen(new Point(0, 0));
                Rect workArea = GetActiveMonitorWorkArea(new Point(Left + HITBOX_SIZE / 2.0, Top + HITBOX_SIZE / 2.0));

                double activeCenter = workArea.Left + (workArea.Width / 2.0);
                bool onRightSide = screenPt.X > activeCenter;

                // Slightly overlap hit padding to construct an unbroken geometric hover bridge
                double x = onRightSide ? -popupSize.Width + 4.0 : targetSize.Width - 4.0;
                double y = 0;

                // Clamping to active monitor work area bounds
                if (screenPt.Y + popupSize.Height > workArea.Bottom)
                {
                    y = targetSize.Height - popupSize.Height;
                }
                if (screenPt.Y + y < workArea.Top)
                {
                    y = workArea.Top - screenPt.Y;
                }

                return new CustomPopupPlacement[]
                {
                    new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Horizontal)
                };
            };

            _popupContainer = new Border
            {
                Width = POPUP_WIDTH,
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromArgb(248, 14, 17, 24)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                BorderThickness = new Thickness(1.2),
                Padding = new Thickness(12, 11, 12, 11),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 18,
                    ShadowDepth = 4,
                    Opacity = 0.7
                }
            };

            _popupContainer.MouseEnter += (s, e) =>
            {
                _hideGraceTimer.Stop();
            };

            _popupContainer.MouseLeave += (s, e) =>
            {
                _hideGraceTimer.Stop();
                _hideGraceTimer.Start();
            };

            var popStack = new StackPanel();
            _popupContainer.Child = popStack;

            // Header
            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleBlock = new TextBlock
            {
                Text = "SYSTEM TELEMETRY",
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                FontWeight = FontWeights.Bold,
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleBlock, 0);

            var closeBtn = new TextBlock
            {
                Text = "✕",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBtn.MouseEnter += (s, e) => closeBtn.Foreground = Brushes.Red;
            closeBtn.MouseLeave += (s, e) => closeBtn.Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
            closeBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                _engine.Stop();
                Application.Current.Shutdown();
            };
            Grid.SetColumn(closeBtn, 1);

            headerGrid.Children.Add(titleBlock);
            headerGrid.Children.Add(closeBtn);
            popStack.Children.Add(headerGrid);

            // Anomaly Banner
            _anomalyBanner = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 185, 28, 28)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 8),
                Visibility = Visibility.Collapsed
            };
            _anomalyText = new TextBlock
            {
                Text = "⚠️ Sustained Saturation Detected!",
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap
            };
            _anomalyBanner.Child = _anomalyText;
            popStack.Children.Add(_anomalyBanner);

            // CPU Section
            var cpuGrid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            var cpuLabel = new TextBlock
            {
                Text = "CPU UTILIZATION",
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var cpuValueStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _cpuValueText = new TextBlock
            {
                Text = "0.0%",
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            };
            _cpuStatusBadge = new TextBlock
            {
                Text = " NORMAL",
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0)
            };
            cpuValueStack.Children.Add(_cpuValueText);
            cpuValueStack.Children.Add(_cpuStatusBadge);
            cpuGrid.Children.Add(cpuLabel);
            cpuGrid.Children.Add(cpuValueStack);
            popStack.Children.Add(cpuGrid);

            _cpuBar = new ProgressBar
            {
                Height = 5,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 9)
            };
            popStack.Children.Add(_cpuBar);

            // RAM Section
            var ramGrid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            var ramLabel = new TextBlock
            {
                Text = "MEMORY (RAM)",
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _ramValueText = new TextBlock
            {
                Text = "0.0 GB (0%)",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            ramGrid.Children.Add(ramLabel);
            ramGrid.Children.Add(_ramValueText);
            popStack.Children.Add(ramGrid);

            _ramBar = new ProgressBar
            {
                Height = 5,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Foreground = new SolidColorBrush(Color.FromRgb(168, 85, 247)),
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 3)
            };
            popStack.Children.Add(_ramBar);

            _ramFreeText = new TextBlock
            {
                Text = "Available: 0.0 GB / 0.0 GB Total",
                FontSize = 8.5,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 0, 3)
            };
            popStack.Children.Add(_ramFreeText);

            _commitValueText = new TextBlock
            {
                Text = "Commit Limit: 0.0% (0.0 GB avail)",
                FontSize = 8.5,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 0, 6)
            };
            popStack.Children.Add(_commitValueText);

            // Hairline separator before Sentinel Card
            var sentSep = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                Margin = new Thickness(0, 4, 0, 9)
            };
            popStack.Children.Add(sentSep);

            // ==========================================
            // PROCESS SENTINEL CARD (SAMDE / Linear Aesthetic)
            // ==========================================
            _sentinelCard = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(170, 15, 20, 28)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 8, 9, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var sentinelStack = new StackPanel();

            // Card Header: Title left, Status Pill right
            var sentinelHeaderGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            sentinelHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sentinelHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _sentinelHeaderTitle = new TextBlock
            {
                Text = "PROCESS SENTINEL",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_sentinelHeaderTitle, 0);

            _sentinelStatusPill = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(60, 5, 46, 22)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 52, 211, 153)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 1.5, 6, 1.5),
                VerticalAlignment = VerticalAlignment.Center
            };
            _sentinelStatusPillText = new TextBlock
            {
                Text = "● DORMANT",
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153))
            };
            _sentinelStatusPill.Child = _sentinelStatusPillText;
            Grid.SetColumn(_sentinelStatusPill, 1);

            sentinelHeaderGrid.Children.Add(_sentinelHeaderTitle);
            sentinelHeaderGrid.Children.Add(_sentinelStatusPill);
            sentinelStack.Children.Add(sentinelHeaderGrid);

            // Sentinel Body Stack: dynamic rows (active daemons or dormant status)
            _sentinelBodyStack = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
            sentinelStack.Children.Add(_sentinelBodyStack);

            // Unified Action Button (Border + TextBlock with state-aware hover)
            _actionButton = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 20, 26, 38)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Cursor = Cursors.Hand
            };

            _actionButtonText = new TextBlock
            {
                Text = "⟳  Scan Background Daemons",
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _actionButton.Child = _actionButtonText;

            _actionButton.MouseEnter += (s, e) =>
            {
                var snap = _engine.CurrentSnapshot;
                if (snap != null && snap.OrphanDaemons != null && snap.OrphanDaemons.Count > 0)
                {
                    _actionButton.Background = new SolidColorBrush(Color.FromArgb(245, 45, 26, 14));
                    _actionButton.BorderBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                    _actionButtonText.Foreground = new SolidColorBrush(Color.FromRgb(254, 243, 199));
                    _actionButton.Effect = new DropShadowEffect
                    {
                        Color = Color.FromRgb(251, 191, 36),
                        BlurRadius = 8,
                        ShadowDepth = 0,
                        Opacity = 0.5
                    };
                }
                else
                {
                    _actionButton.Background = new SolidColorBrush(Color.FromArgb(240, 24, 32, 45));
                    _actionButton.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                    _actionButtonText.Foreground = Brushes.White;
                    _actionButton.Effect = new DropShadowEffect
                    {
                        Color = Color.FromRgb(56, 189, 248),
                        BlurRadius = 8,
                        ShadowDepth = 0,
                        Opacity = 0.35
                    };
                }
            };

            _actionButton.MouseLeave += (s, e) =>
            {
                var snap = _engine.CurrentSnapshot;
                if (snap != null && snap.OrphanDaemons != null && snap.OrphanDaemons.Count > 0)
                {
                    _actionButton.Background = new SolidColorBrush(Color.FromArgb(210, 30, 22, 16));
                    _actionButton.BorderBrush = new SolidColorBrush(Color.FromArgb(180, 251, 191, 36));
                    _actionButtonText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                }
                else
                {
                    _actionButton.Background = new SolidColorBrush(Color.FromArgb(200, 20, 26, 38));
                    _actionButton.BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
                    _actionButtonText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                }
                _actionButton.Effect = null;
            };

            _actionButton.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                var snap = _engine.CurrentSnapshot;
                if (snap != null && snap.OrphanDaemons != null && snap.OrphanDaemons.Count > 0)
                {
                    int freed = GovernorController.ReclaimOrphanDaemons();
                    _actionButtonText.Text = string.Format("✓ Reclaimed {0} Daemon(s)", freed);
                    _actionButtonText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                    _actionButton.BorderBrush = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                }
                else
                {
                    _actionButtonText.Text = "✓ 0 Daemons Running";
                    _actionButtonText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                    _actionButton.BorderBrush = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                }
            };

            sentinelStack.Children.Add(_actionButton);
            _sentinelCard.Child = sentinelStack;
            popStack.Children.Add(_sentinelCard);

            // Footer hint
            var hintText = new TextBlock
            {
                Text = "Drag icon to reposition • Right-click for options",
                FontSize = 8,
                Foreground = new SolidColorBrush(Color.FromArgb(85, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            popStack.Children.Add(hintText);

            _hudPopup.Child = _popupContainer;
        }

        private ContextMenu CreateContextMenu()
        {
            var menu = new ContextMenu();
            menu.Background = new SolidColorBrush(Color.FromRgb(24, 27, 34));
            menu.Foreground = Brushes.White;
            menu.BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));

            var resetItem = new MenuItem { Header = "🔄 Reset Position to Top Right" };
            resetItem.Click += (s, e) =>
            {
                Rect workArea = GetActiveMonitorWorkArea(new Point(Left + HITBOX_SIZE / 2.0, Top + HITBOX_SIZE / 2.0));
                Left = workArea.Right - HITBOX_SIZE - 6;
                Top = workArea.Top + 70;
                _config.IconLeft = Left;
                _config.IconTop = Top;
                _config.Save();
            };

            var exitItem = new MenuItem { Header = "❌ Exit FloatBar" };
            exitItem.Click += (s, e) =>
            {
                _config.IconLeft = Left;
                _config.IconTop = Top;
                _config.Save();
                _engine.Stop();
                Application.Current.Shutdown();
            };

            menu.Items.Add(resetItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(exitItem);

            return menu;
        }

        // ==========================================
        // UI REFRESH
        // ==========================================
        private void OnUiRefreshTick(object sender, EventArgs e)
        {
            try
            {
                TelemetrySnapshot snap = _engine.CurrentSnapshot;
                if (snap == null) return;
                double cpu = snap.CpuPercent;
                double ramUsed = snap.RamUsedGB;
                double ramTotal = snap.RamTotalGB;
                double ramAvail = snap.RamAvailGB;
                double ramPct = snap.RamPercent;
                bool isAnomaly = snap.IsAnomaly;

            // 1. Micro-Icon
            if (isAnomaly)
            {
                _iconStatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                _visualOrb.BorderBrush = new SolidColorBrush(Color.FromArgb(200, 239, 68, 68));
                if (_statusGlow != null) _statusGlow.Color = Color.FromRgb(239, 68, 68);
            }
            else if (cpu >= 75.0 || ramPct >= 92.0)
            {
                _iconStatusDot.Fill = new SolidColorBrush(Color.FromRgb(251, 191, 36)); // Amber
                _visualOrb.BorderBrush = new SolidColorBrush(Color.FromArgb(140, 251, 191, 36));
                if (_statusGlow != null) _statusGlow.Color = Color.FromRgb(251, 191, 36);
            }
            else
            {
                _iconStatusDot.Fill = new SolidColorBrush(Color.FromRgb(52, 211, 153)); // Emerald
                _visualOrb.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
                if (_statusGlow != null) _statusGlow.Color = Color.FromRgb(52, 211, 153);
            }

            // 2. Pop-up HUD
            if (_hudPopup != null && _hudPopup.IsOpen)
            {
                // CPU
                _cpuValueText.Text = string.Format("{0:0.0}%", cpu);
                _cpuBar.Value = cpu;

                if (cpu >= 90.0)
                {
                    _cpuStatusBadge.Text = " CRITICAL";
                    _cpuStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    _cpuBar.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                }
                else if (cpu >= 70.0)
                {
                    _cpuStatusBadge.Text = " HIGH";
                    _cpuStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                    _cpuBar.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                }
                else
                {
                    _cpuStatusBadge.Text = " NORMAL";
                    _cpuStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                    _cpuBar.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                }

                // RAM
                _ramValueText.Text = string.Format("{0:0.#} GB ({1:0}%)", ramUsed, ramPct);
                _ramBar.Value = ramPct;
                _ramFreeText.Text = string.Format("Available: {0:0.#} GB / {1:0.#} GB Total", ramAvail, totalGbText(ramTotal));

                if (ramPct >= 95.0)
                    _ramBar.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                else if (ramPct >= 85.0)
                    _ramBar.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                else
                    _ramBar.Foreground = new SolidColorBrush(Color.FromRgb(168, 85, 247));

                // Commit Limit
                if (_commitValueText != null)
                {
                    _commitValueText.Text = string.Format("Commit Limit: {0:0.0}% ({1:0.#} GB avail)", snap.CommitPercent, snap.CommitAvailGB);
                    if (snap.CommitPercent >= 95.0)
                        _commitValueText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    else
                        _commitValueText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                }

                // ==========================================
                // PROCESS SENTINEL DYNAMIC CARD RENDER
                // ==========================================
                var daemons = snap.OrphanDaemons;
                if (_sentinelBodyStack != null && _sentinelStatusPill != null && _actionButtonText != null)
                {
                    _sentinelBodyStack.Children.Clear();

                    if (daemons == null || daemons.Count == 0)
                    {
                        // --- DORMANT STATE ---
                        _sentinelStatusPill.Background = new SolidColorBrush(Color.FromArgb(60, 5, 46, 22));
                        _sentinelStatusPill.BorderBrush = new SolidColorBrush(Color.FromArgb(140, 52, 211, 153));
                        _sentinelStatusPillText.Text = "● DORMANT";
                        _sentinelStatusPillText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));

                        var dormantRow = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                        dormantRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        dormantRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                        var checkIcon = new TextBlock
                        {
                            Text = "✓",
                            FontWeight = FontWeights.Bold,
                            FontSize = 9.5,
                            Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153)),
                            Margin = new Thickness(0, 0, 6, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        Grid.SetColumn(checkIcon, 0);

                        var dormantLabel = new TextBlock
                        {
                            Text = "No orphan MCP or background workers active",
                            FontSize = 8.5,
                            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                            VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        };
                        Grid.SetColumn(dormantLabel, 1);

                        dormantRow.Children.Add(checkIcon);
                        dormantRow.Children.Add(dormantLabel);
                        _sentinelBodyStack.Children.Add(dormantRow);

                        // Subtle scan chip button
                        if (!_actionButtonText.Text.StartsWith("✓ Reclaimed"))
                        {
                            _actionButton.Background = new SolidColorBrush(Color.FromArgb(200, 20, 26, 38));
                            _actionButton.BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
                            _actionButtonText.Text = "⟳  Scan Background Daemons";
                            _actionButtonText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                        }
                    }
                    else
                    {
                        // --- ACTIVE DAEMONS DETECTED STATE ---
                        double totalMb = 0.0;
                        foreach (var d in daemons) totalMb += d.WorkingSetMB;

                        _sentinelStatusPill.Background = new SolidColorBrush(Color.FromArgb(70, 69, 26, 3));
                        _sentinelStatusPill.BorderBrush = new SolidColorBrush(Color.FromArgb(160, 251, 191, 36));
                        _sentinelStatusPillText.Text = string.Format("⚠️ {0} ACTIVE", daemons.Count);
                        _sentinelStatusPillText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));

                        // Recessed list panel with subtle border
                        var daemonListBox = new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(220, 10, 14, 20)),
                            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(5),
                            Padding = new Thickness(6, 4, 6, 4),
                            Margin = new Thickness(0, 1, 0, 3)
                        };

                        var listStack = new StackPanel();

                        foreach (var d in daemons)
                        {
                            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                            var leftStack = new StackPanel { Orientation = Orientation.Horizontal };

                            var dot = new TextBlock
                            {
                                Text = "●",
                                FontSize = 7,
                                Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(0, 0, 4, 0)
                            };
                            leftStack.Children.Add(dot);

                            var descText = new TextBlock
                            {
                                Text = d.Description,
                                FontSize = 8.5,
                                FontWeight = FontWeights.SemiBold,
                                Foreground = Brushes.White,
                                VerticalAlignment = VerticalAlignment.Center
                            };
                            leftStack.Children.Add(descText);

                            var pidBadge = new TextBlock
                            {
                                Text = string.Format(" [PID {0}]", d.Pid),
                                FontFamily = new FontFamily("Consolas, Courier New"),
                                FontSize = 8,
                                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                                VerticalAlignment = VerticalAlignment.Center
                            };
                            leftStack.Children.Add(pidBadge);
                            Grid.SetColumn(leftStack, 0);

                            var rightText = new TextBlock
                            {
                                Text = string.Format("{0:0.#} MB", d.WorkingSetMB),
                                FontFamily = new FontFamily("Consolas, Courier New"),
                                FontSize = 8.5,
                                FontWeight = FontWeights.Bold,
                                Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36)),
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(6, 0, 0, 0)
                            };
                            Grid.SetColumn(rightText, 1);

                            row.Children.Add(leftStack);
                            row.Children.Add(rightText);
                            listStack.Children.Add(row);
                        }

                        daemonListBox.Child = listStack;
                        _sentinelBodyStack.Children.Add(daemonListBox);

                        // Prominent amber/warning reclaim button
                        if (!_actionButtonText.Text.StartsWith("✓ Reclaimed"))
                        {
                            _actionButton.Background = new SolidColorBrush(Color.FromArgb(210, 30, 22, 16));
                            _actionButton.BorderBrush = new SolidColorBrush(Color.FromArgb(160, 251, 191, 36));
                            _actionButtonText.Text = string.Format("⚡  Purge {0} Daemon(s)  (Free ~{1:0.#} MB)", daemons.Count, totalMb);
                            _actionButtonText.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));
                        }
                    }
                }

                // Anomaly Banner
                if (isAnomaly)
                {
                    _anomalyBanner.Visibility = Visibility.Visible;
                    _anomalyText.Text = snap.AnomalyMessage;
                }
                else
                {
                    _anomalyBanner.Visibility = Visibility.Collapsed;
                }
            }
            }
            catch (Exception ex)
            {
                try
                {
                    string bootLog = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "boot.log");
                    System.IO.File.AppendAllText(bootLog, "OnUiRefreshTick ex: " + ex.ToString() + "\n");
                }
                catch { }
            }
        }

        private string totalGbText(double val)
        {
            return val > 0 ? val.ToString("0.#") : "8.0";
        }
    }

    // ==========================================
    // PROGRAM ENTRY POINT (SINGLE INSTANCE MUTEX)
    // ==========================================
    public static class Program
    {
        private static Mutex _singleInstanceMutex;

        [STAThread]
        public static void Main()
        {
            string bootLog = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "boot.log");
            System.IO.File.WriteAllText(bootLog, "Step 1: Main entered\n");

            bool createdNew;
            _singleInstanceMutex = new Mutex(true, "FloatBar_Global_SingleInstance_Mutex_101", out createdNew);
            System.IO.File.AppendAllText(bootLog, "Step 2: Mutex createdNew=" + createdNew + "\n");
            if (!createdNew)
            {
                System.IO.File.AppendAllText(bootLog, "Exiting because another instance holds mutex.\n");
                return;
            }

            try
            {
                NativeMethods.SetProcessDPIAware();
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText(bootLog, "DPIAware ex: " + ex.Message + "\n");
            }

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            app.DispatcherUnhandledException += (s, e) =>
            {
                System.IO.File.AppendAllText(bootLog, "DispatcherUnhandledException: " + e.Exception.ToString() + "\n");
                e.Handled = false;
            };

            try
            {
                System.IO.File.AppendAllText(bootLog, "Step 3: Creating MainWindow\n");
                var window = new MainWindow();
                System.IO.File.AppendAllText(bootLog, "Step 4: Showing MainWindow\n");
                window.Show();
                System.IO.File.AppendAllText(bootLog, "Step 5: Calling app.Run\n");
                app.Run();
                System.IO.File.AppendAllText(bootLog, "Step 6: app.Run exited\n");
            }
            catch (Exception ex)
            {
                System.IO.File.AppendAllText(bootLog, "MainException: " + ex.ToString() + "\n");
            }

            GC.KeepAlive(_singleInstanceMutex);
        }
    }
}
