using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.IO.Ports;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MaimaiLauncher;

public sealed class MainForm : Form
{
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIM_TYPEHID = 2;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;

    private static readonly int[] ButtonBits = { 2, 3, 0, 15, 14, 13, 12, 11 };
    private static readonly object LogLock = new();
    private static uint _lastRaw = uint.MaxValue;
    private static byte[]? _lastReport;
    private static bool _hasRaw;

    private readonly LauncherConfig _config;
    private readonly WebView2 _web = new();
    private System.Windows.Forms.Timer? _inputTimer;
    private string _lastButtonKey = "";
    private IntPtr _segaDevice;
    private TouchReader? _touch;
    private bool _gameRunning;
    private int _deviceEpoch;

    public MainForm(LauncherConfig config)
    {
        _config = config;

        Text = "舞萌手台启动器";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "app.ico")); } catch { }
        ApplyScreenBounds();
        TopMost = true;

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);

        Load += async (_, _) => await InitAsync();
        FormClosing += (_, _) =>
        {
            _inputTimer?.Stop();
            _inputTimer?.Dispose();
            _touch?.Stop();
        };
    }

    private async Task InitAsync()
    {
        var pageName = string.Equals(_config.Ui, "v2", StringComparison.OrdinalIgnoreCase)
            ? "menu-v2.html"
            : "maimai-tv-standalone.html";
        var page = Path.Combine(AppContext.BaseDirectory, "web", pageName);
        if (!File.Exists(page))
        {
            page = Path.Combine(AppContext.BaseDirectory, "web", "maimai-tv-standalone.html");
        }
        if (!File.Exists(page))
        {
            MessageBox.Show(this, "找不到 web\\maimai-tv-standalone.html。", "启动器文件缺失",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        try
        {
            Log("init");
            await _web.EnsureCoreWebView2Async();
            Log("webview ready");

            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            StartTouch();
        StartLed();
        StartStatusTimer();
        SetupTray();

        // 游戏已经在跑：启动器不要盖在游戏上面
        if (Process.GetProcessesByName("Sinmai").Length > 0)
        {
            Log("game already running, hiding launcher");
            Hide();
        }
            RegisterRawInput();

            var settings = _web.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;

            _web.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                Log("navigation completed");
                StartInput();
            };
            _web.CoreWebView2.Navigate(new Uri(page).AbsoluteUri + "?bridge=0");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "WebView2 初始化失败：\n" + ex.Message, "启动器错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void RegisterRawInput()
    {
        // 游戏在跑时不要把 io4 抢过来
        if (Process.GetProcessesByName("Sinmai").Length > 0)
            return; // 游戏在跑时不注册，也不刷日志

        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x04,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = Handle,
            },
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x05,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = Handle,
            },
        };

        var ok = RegisterRawInputDevices(devices, (uint)devices.Length,
            (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        Log($"register raw input = {ok}");
    }

    private void UnregisterRawInput()
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x04,
                dwFlags = RIDEV_REMOVE,
                hwndTarget = IntPtr.Zero,
            },
            new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x05,
                dwFlags = RIDEV_REMOVE,
                hwndTarget = IntPtr.Zero,
            },
        };

        var ok = RegisterRawInputDevices(devices, (uint)devices.Length,
            (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        Log($"unregister raw input = {ok}");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
            HandleRawInput(m.LParam);

        base.WndProc(ref m);
    }

    private void HandleRawInput(IntPtr lParam)
    {
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0)
            return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var read = GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize);
            if (read == 0 || read > size)
                return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType != RIM_TYPEHID)
                return;
            if (!IsSegaDevice(header.hDevice))
                return;

            var hid = Marshal.PtrToStructure<RAWHID>(IntPtr.Add(buffer, Marshal.SizeOf<RAWINPUTHEADER>()));
            var dataSize = (int)(hid.dwSizeHid * hid.dwCount);
            if (dataSize <= 0)
                return;

            var bytes = new byte[dataSize];
            Marshal.Copy(IntPtr.Add(buffer, Marshal.SizeOf<RAWINPUTHEADER>() + 8), bytes, 0, dataSize);
            ParseReport(bytes);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private bool IsSegaDevice(IntPtr hDevice)
    {
        if (_segaDevice == hDevice)
            return true;

        uint size = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0)
            return false;

        var buffer = Marshal.AllocHGlobal((int)size * 2);
        try
        {
            var result = GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buffer, ref size);
            if (result == uint.MaxValue)
                return false;

            var name = Marshal.PtrToStringUni(buffer) ?? "";
            var match = name.IndexOf("VID_0CA3&PID_0021", StringComparison.OrdinalIgnoreCase) >= 0;
            if (match)
            {
                _segaDevice = hDevice;
                Log("sega hid: " + name);
            }

            return match;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private async Task UpdateAquaMaiAsync()
    {
        try
        {
            Log("aquamai update: checking latest release");
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MaimaiLauncher");
            http.Timeout = TimeSpan.FromMinutes(5);

            var json = await http.GetStringAsync("https://api.github.com/repos/MuNET-OSS/AquaMai/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "?";

            // 已是最新就不下载
            var pkgDir = Path.GetDirectoryName(_config.AquaMai.Toml) ?? AppContext.BaseDirectory;
            var dllPath = Path.Combine(pkgDir, "AquaMai.dll");
            if (File.Exists(dllPath))
            {
                var current = FileVersionInfo.GetVersionInfo(dllPath).FileVersion ?? "";
                var latest = tag.TrimStart('v', 'V');
                if (current.Length > 0 && latest.StartsWith(current.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Log("aquamai update: already latest " + current);
                    PostJson("{\"type\":\"aquamai-saved\",\"ok\":true,\"message\":\"已是最新 " + tag + "\"}");
                    return;
                }
            }
            var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind == JsonValueKind.Undefined)
            {
                Log("aquamai update: no zip asset in " + tag);
                PostJson("{\"type\":\"aquamai-saved\",\"ok\":false,\"message\":\"最新版没有 zip 资源\"}");
                return;
            }

            var url = asset.GetProperty("browser_download_url").GetString()!;
            var name = asset.GetProperty("name").GetString() ?? "aquamai.zip";
            var tmp = Path.Combine(Path.GetTempPath(), "aquamai-" + Guid.NewGuid().ToString("N") + ".zip");
            await using (var src = await http.GetStreamAsync(url))
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst);

            var dir = Path.Combine(Path.GetTempPath(), "aquamai-" + Guid.NewGuid().ToString("N"));
            ZipFile.ExtractToDirectory(tmp, dir, true);
            var dll = Directory.GetFiles(dir, "AquaMai.dll", SearchOption.AllDirectories).FirstOrDefault();
            if (dll == null)
            {
                Log("aquamai update: AquaMai.dll not found in " + name);
                return;
            }

            if (File.Exists(dllPath))
                File.Copy(dllPath, dllPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
            File.Copy(dll, dllPath, true);
            Log("aquamai update: " + tag + " -> " + dllPath);
            PostJson("{\"type\":\"aquamai-saved\",\"ok\":true,\"message\":\"已更新 AquaMai " + tag + "\"}");
        }
        catch (Exception ex)
        {
            Log("aquamai update failed: " + ex.Message);
            PostJson("{\"type\":\"aquamai-saved\",\"ok\":false,\"message\":\"更新失败: " + ex.Message.Replace("\"", "'") + "\"}");
        }
    }

    private void PickMajPlayPath()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择 MajdataPlay.exe",
            Filter = "MajdataPlay (MajdataPlay.exe)|MajdataPlay.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        _config.MajPlay.Exe = dlg.FileName;
        _config.Save();
        Log("majplay path: " + dlg.FileName);
        PostJson("{\"type\":\"aquamai-saved\",\"ok\":true,\"message\":\"MajPlay 路径已保存\"}");
        PostStatus();
    }

    private string ResolveLedPort()
    {
        try
        {
            var toml = AquaMaiParser.ResolvePath(_config);
            if (toml != null && File.Exists(toml))
            {
                foreach (var line in File.ReadLines(toml))
                {
                    var t = line.Trim();
                    if (!t.StartsWith("PortName_1PLED"))
                        continue;
                    var q1 = t.IndexOf('"');
                    var q2 = t.LastIndexOf('"');
                    if (q1 >= 0 && q2 > q1)
                        return t.Substring(q1 + 1, q2 - q1 - 1);
                }
            }
        }
        catch (Exception ex)
        {
            Log("led port resolve failed: " + ex.Message);
        }

        return "COM21";
    }

    private void AddNewVersion()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择启动脚本 (.bat)",
            Filter = "启动脚本 (*.bat)|*.bat|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        var name = Path.GetFileNameWithoutExtension(dlg.FileName);
        _config.Games.Add(new GameConfig { Name = name, Bat = dlg.FileName });
        if (_config.ActiveGame < 0 || _config.ActiveGame >= _config.Games.Count)
            _config.ActiveGame = _config.Games.Count - 1;
        _config.Save();
        PostGames();
        PostStatus();
    }

    private SerialPort? _led;
    private System.Windows.Forms.Timer? _ledRetry;
    private DateTime _lastRawAttempt = DateTime.MinValue;
    private bool _ledOk;

    private static void KillInjector()
    {
        // inject.exe 是保证 sinmai 运行的关键，游戏在跑时绝不能杀
        if (Process.GetProcessesByName("Sinmai").Length > 0)
        {
            Log("skip kill inject: Sinmai is running");
            return;
        }

        foreach (var name in new[] { "inject", "inject_x64", "injector" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(1500);
                    Log("killed " + name);
                }
                catch { }
            }
        }
    }

    private System.Windows.Forms.Timer? _statusTimer;

    private NotifyIcon? _tray;

    private void SetupTray()
    {
        if (_tray != null)
            return;

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示启动器", null, (_, _) => ShowLauncher());
        menu.Items.Add("隐藏到托盘", null, (_, _) => Hide());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Close());

        Icon trayIcon;
        try { trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { trayIcon = SystemIcons.Application; }

        _tray = new NotifyIcon
        {
            Icon = trayIcon,
            Visible = true,
            Text = "舞萌手台启动器",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowLauncher();
        Log("tray ready");
    }

    private void StartStatusTimer()
    {
        if (_statusTimer != null)
            return;

        _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _statusTimer.Tick += (_, _) => PostStatus();
        _statusTimer.Start();
    }

    private void StartLed()
    {
        try { _led?.Close(); _led?.Dispose(); } catch { }
        _led = null;

        KillInjector();
        try
        {
            _led = new SerialPort(ResolveLedPort(), 115200)
            {
                DtrEnable = true,
                RtsEnable = true,
                WriteTimeout = 200,
            };
            _led.Open();
            for (var i = 0; i < 8; i++)
                LedSet(i, 0, 0, 0);
            LedLatch();
            _ledOk = true;
            EnsureLedTimer();
            Log("led open " + _led.PortName);
            UpdateLed(Array.Empty<int>());

            // 开机灯效：8 颗依次亮一遍，确认输出链路
            Task.Run(() =>
            {
                for (var i = 0; i < 8; i++)
                {
                    var c = LedColors[i];
                    LedSet(i, c.R, c.G, c.B);
                    LedLatch();
                    Thread.Sleep(140);
                }
                UpdateLed(Array.Empty<int>());
            });
        }
        catch (Exception ex)
        {
            _led = null;
            _ledOk = false;
            Log("led open failed: " + ex.Message);

            EnsureLedTimer();
        }
    }

    private void EnsureLedTimer()
    {
        if (_ledRetry != null)
            return;

        // 灯板断开/被占用时每 3 秒重开一次
        _ledRetry = new System.Windows.Forms.Timer { Interval = 3000 };
        _ledRetry.Tick += (_, _) =>
        {
            if (_ledOk)
                return;
            StartLed();
        };
        _ledRetry.Start();
    }

    private void LedSet(int i, byte r, byte g, byte b)
    {
        if (_led?.IsOpen != true)
            return;
        var p = new byte[] { 0xE0, 0x11, 0x01, 0x05, 0x31, (byte)i, r, g, b, 0 };
        byte sum = 0;
        for (var k = 1; k < 9; k++)
            sum += p[k];
        p[9] = sum;
        try { _led.Write(p, 0, 10); } catch { _ledOk = false; }
    }

    private void LedLatch()
    {
        if (_led?.IsOpen != true)
            return;
        try { _led.Write(new byte[] { 0xE0, 0x11, 0x01, 0x01, 0x3C, 0x4F }, 0, 6); } catch { }
    }

    private static readonly (byte R, byte G, byte B)[] LedColors =
    {
        (0, 180, 255),   // A1 青
        (0, 0, 0),       // A2
        (220, 220, 220), // A3 白
        (255, 120, 0),   // A4 橙
        (0, 90, 255),    // A5 蓝
        (220, 220, 220), // A6 白
        (0, 0, 0),       // A7
        (0, 180, 255),   // A8 青
    };

    private void UpdateLed(int[] pressed)
    {
        if (pressed.Length > 0)
            Log("led press " + string.Join(",", pressed));

        for (var i = 0; i < 8; i++)
        {
            var on = Array.IndexOf(pressed, i) >= 0;
            var c = LedColors[i];
            var k = on ? 1.0 : 0.22;
            LedSet(i, (byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
        }
        LedLatch();
    }

    private static void ParseReport(byte[] bytes)
    {
        // Sega 64 字节报告：报告ID + ...，按钮在 offset 29/30（0D F8 = 0xF80D）
        uint raw = 0;
        if (bytes.Length >= 31)
            raw = (uint)(bytes[29] | (bytes[30] << 8));
        else if (bytes.Length >= 3)
            raw = (uint)(bytes[1] | (bytes[2] << 8));
        else if (bytes.Length >= 2)
            raw = (uint)(bytes[0] | (bytes[1] << 8));

        if (raw != _lastRaw)
        {
            _lastRaw = raw;
            _hasRaw = true;
            Log($"raw size={bytes.Length} hex={BitConverter.ToString(bytes)} buttons=0x{raw:X4}");
            _lastReport = (byte[])bytes.Clone();
            return;
        }

        // 触摸等非按键位：整包有变化也记一行，方便定位触摸数据在哪几个字节
        var changed = _lastReport == null || _lastReport.Length != bytes.Length;
        if (!changed)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                if (_lastReport![i] == bytes[i])
                    continue;
                changed = true;
                break;
            }
        }

        if (changed)
        {
            _lastReport = (byte[])bytes.Clone();
            Log($"hid size={bytes.Length} hex={BitConverter.ToString(bytes)}");
        }
    }

    private void StartInput()
    {
        if (_inputTimer != null)
            return;

        Log("input timer start");
        _inputTimer = new System.Windows.Forms.Timer { Interval = 20 };
        _inputTimer.Tick += (_, _) =>
        {
            // io4 没连上时每 3 秒重新注册一次
            if (!_hasRaw && (DateTime.UtcNow - _lastRawAttempt).TotalSeconds > 3)
            {
                _lastRawAttempt = DateTime.UtcNow;
                Log("io4 retry register");
                RegisterRawInput();
            }

            var pressed = ReadButtons();
            var key = string.Join(",", pressed);
            if (key != _lastButtonKey)
            {
                _lastButtonKey = key;
                PostButtons(pressed);
                UpdateLed(pressed);
            }
        };
        _inputTimer.Start();
    }

    private void PostButtons(int[] pressed)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        var json = "{\"buttons\":{\"pressed\":[" + string.Join(",", pressed) + "]}}";
        Log("post " + json);

        try
        {
            _web.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch
        {
            // 页面尚未就绪或正在关闭
        }
    }

    private void StartTouch()
    {
        if (_touch != null)
            return;

        _touch = new TouchReader(_config.Touch.Port, _config.Touch.Baud);
        _touch.Log = Log;
        _touch.ActiveChanged += active =>
        {
            var json = "{\"touch\":{\"active\":[" + string.Join(",", active) + "]}}";
            Log("post " + json);
            PostJson(json);
        };
        _touch.Start();
        Log("touch start " + _config.Touch.Port);
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;

            var msgType = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (msgType == "ready")
            {
                PostStatus();
                PostGames();
                PostAquaMai();
                return;
            }
            if (msgType == "aquamai-set" || msgType == "aquamai-section")
            {
                HandleAquaMaiWrite(root, msgType);
                return;
            }
            if (msgType != "action")
                return;
            if (!root.TryGetProperty("action", out var action))
                return;

            switch (action.GetString())
            {
                case "launch":
                    _ = LaunchGameAsync();
                    break;
                case "majplay":
                    _ = LaunchMajPlayAsync();
                    break;
                case "mcm":
                    _ = LaunchMcmAsync();
                    break;
                case "shutdown":
                    SystemActions.Shutdown();
                    break;
                case "reboot":
                    SystemActions.Reboot();
                    break;
                case "volume-up":
                    SystemActions.VolumeUp();
                    break;
                case "volume-down":
                    SystemActions.VolumeDown();
                    break;
                case "help":
                    PostStatus();
                    break;
                case "aquamai":
                    PostAquaMai();
                    break;
                case "majplay-path":
                    PickMajPlayPath();
                    break;
                case "update-aquamai":
                    _ = UpdateAquaMaiAsync();
                    break;
                case "new-version":
                    AddNewVersion();
                    break;
                case "rename-version":
                    if (root.TryGetProperty("index", out var ri) && ri.TryGetInt32(out var ridx) &&
                        root.TryGetProperty("name", out var rn) && ridx >= 0 && ridx < _config.Games.Count)
                    {
                        _config.Games[ridx].Name = rn.GetString() ?? _config.Games[ridx].Name;
                        _config.Save();
                        PostGames();
                        PostStatus();
                    }
                    break;
                case "version":
                    if (root.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out var idx))
                        SwitchVersion(idx);
                    break;
                case "exit":
                    Close();
                    break;
            }
        }
        catch
        {
            // 页面消息格式异常时忽略
        }
    }

    /// <summary>
    /// 一次“外部程序接管整屏”的会话：释放设备 → 隐藏 → 启动 → 等窗口 → 拉到竖屏 →
    /// 等窗口消失 → 恢复启动器 → 重新占用设备。游戏、MajPlay 共用这一条路径。
    /// </summary>
    private sealed class AppLaunchSpec
    {
        public string File = "";
        public string Args = "";
        public string WorkingDir = "";
        public string WaitProcess = "";
        public string Tag = "app";
        public bool WaitForStale;
    }

    private async Task<bool> RunAppSessionAsync(AppLaunchSpec spec)
    {
        if (_gameRunning)
            return false;

        _gameRunning = true;
        var epoch = ++_deviceEpoch;

        try
        {
            if (spec.WaitForStale)
                await Task.Run(() => WaitForStaleProcesses(new[] { "amdaemon", _config.CurrentGame.WaitProcess }, 150));

            await Task.Run(() => ReleaseDevices());
            await Task.Delay(400); // 给驱动时间释放串口，但不阻塞 UI
            Hide();
            Log($"{spec.Tag} launch: {spec.File}");

            var psi = new ProcessStartInfo(spec.File)
            {
                WorkingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDir)
                    ? (Path.GetDirectoryName(spec.File) ?? AppContext.BaseDirectory)
                    : spec.WorkingDir,
                UseShellExecute = true,
                Arguments = spec.Args ?? "",
            };
            var proc = Process.Start(psi);

            var target = ScreenSelector.Select(_config.Screen).Bounds;

            await Task.Run(() =>
            {
                var app = string.IsNullOrWhiteSpace(spec.WaitProcess)
                    ? proc
                    : WaitForProcess(spec.WaitProcess, 600);

                if (app == null)
                {
                    Log($"{spec.Tag} process not found within 60s");
                    TryShowLauncher(epoch);
                KillInjector();
                    return;
                }

                Log($"{spec.Tag} process found: " + app.Id);
                PinGameWindow(app, target);

                // 窗口消失就先把界面还回来，不等进程完全退出
                var windowGone = false;
                while (!app.HasExited)
                {
                    Thread.Sleep(300);
                    app.Refresh();
                    if (app.MainWindowHandle == IntPtr.Zero)
                    {
                        windowGone = true;
                        break;
                    }
                }

                Log(windowGone ? $"{spec.Tag} window gone" : $"{spec.Tag} process exited");
                TryShowLauncher(epoch);
                KillInjector();

                // 不用 WaitForExit：个别程序退出后句柄状态异常会卡住，改成有上限的轮询
                var deadline = Environment.TickCount64 + 30_000;
                try
                {
                    while (!app.HasExited && Environment.TickCount64 < deadline)
                        Thread.Sleep(200);
                }
                catch
                {
                    // 进程句柄异常时直接往下走
                }

                Log($"{spec.Tag} wait finished");

                // 等程序把 IO / 串口句柄彻底放掉再重新占用
                Thread.Sleep(1000);
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (epoch == _deviceEpoch)
                            ReacquireDevices();
                    }));
                }
                catch
                {
                    // 窗体已销毁
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            Log($"{spec.Tag} error " + ex.Message);
            return false;
        }
        finally
        {
            if (epoch == _deviceEpoch && !Visible)
            {
                ShowLauncher();
                ReacquireDevices();
            }
        }
    }

    private Task<bool> LaunchGameAsync() => RunAppSessionAsync(new AppLaunchSpec
    {
        File = _config.CurrentGame.Bat,
        WorkingDir = _config.CurrentGame.EffectiveWorkingDir,
        WaitProcess = _config.CurrentGame.WaitProcess,
        Tag = "game",
        WaitForStale = true,
    });

    private void ReleaseDevices()
    {
        _inputTimer?.Stop();
        UnregisterRawInput();
        _touch?.Stop();

        // LED 也要放掉，否则游戏打不开 COM21
        _ledRetry?.Stop();
        _ledRetry = null;
        try { _led?.Close(); } catch { }
        try { _led?.Dispose(); } catch { }
        _led = null;

        Log("devices released");
    }

    private void TryShowLauncher(int epoch)
    {
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (epoch == _deviceEpoch)
                    ShowLauncher();
            }));
        }
        catch
        {
            // 窗体已销毁
        }
    }

    private void WaitForStaleProcesses(string[] names, int attempts)
    {
        for (var i = 0; i < attempts; i++)
        {
            bool stale;
            try
            {
                stale = names
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Any(n => Process.GetProcessesByName(n).Any(p => !p.HasExited));
            }
            catch
            {
                stale = false;
            }

            if (!stale)
                return;

            Thread.Sleep(100);
        }

        Log("stale processes still running, launching anyway");
    }

    private Process? WaitForProcess(string name, int attempts)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        for (var i = 0; i < attempts; i++)
        {
            Thread.Sleep(100);
            var proc = Process.GetProcessesByName(name).FirstOrDefault(p => !p.HasExited);
            if (proc != null)
                return proc;
        }

        return null;
    }

    private void ShowLauncher()
    {
        ApplyScreenBounds();
        Show();
        Activate();
        TopMost = Process.GetProcessesByName("Sinmai").Length == 0;
        _web.Invalidate(true);
        _gameRunning = false;
        Log("launcher shown");
    }

    private void ReacquireDevices()
    {
        RegisterRawInput();
        _inputTimer?.Start();
        _touch?.Start();
        StartLed();
        Log("game exit: devices reacquired");
    }

    private static void PinGameWindow(Process game, Rectangle bounds)
    {
        for (var i = 0; i < 600; i++)
        {
            game.Refresh();
            var handle = game.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                Thread.Sleep(100);
                continue;
            }

            WindowMover.MoveTo(handle, bounds);
            Log($"game window pinned to {bounds}");

            // Unity 初始化完成后可能再摆一次窗口，稍后再压一次
            Thread.Sleep(1500);
            game.Refresh();
            handle = game.MainWindowHandle;
            if (handle != IntPtr.Zero)
            {
                WindowMover.MoveTo(handle, bounds);
                WindowMover.BringToFront(handle);
            }

            return;
        }

        Log("game window handle not found for pinning");
    }

    private void ApplyScreenBounds()
    {
        var bounds = ScreenSelector.Select(_config.Screen).Bounds;
        if (Bounds != bounds)
        {
            WindowState = FormWindowState.Normal;
            Bounds = bounds;
        }
    }

    private async Task LaunchMcmAsync()
    {
        if (!_config.Mcm.Enabled)
        {
            PostToast("MCM", "未启用或未找到 MaiChartManager");
            return;
        }

        var ok = await McmController.LaunchAndPositionAsync(_config, ScreenSelector.Select(_config.Screen));
        if (!ok)
            PostToast("MCM", "未启动成功，详见 mcm.log");
    }

    private async Task LaunchMajPlayAsync()
    {
        var exe = _config.MajPlay.Exe;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            PostToast("MajPlay", "未配置 MajdataPlay.exe 路径");
            return;
        }

        if (_gameRunning)
            return;

        // MajPlay 的 Unity 初始化会卡住窗口消息，不再走“隐藏+等窗口+锁定”的会话流程
        try
        {
            await Task.Run(() => ReleaseDevices());
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                UseShellExecute = true,
                Arguments = _config.MajPlay.Args ?? "",
            };
            var proc = Process.Start(psi);
            Log("majplay started: " + exe);
            PostToast("MajPlay", "已启动");
            TopMost = false;
            Hide();

            if (proc != null)
            {
                _gameRunning = true;
                _ = Task.Run(async () =>
                {
                    try { await proc.WaitForExitAsync(); } catch { }
                    _gameRunning = false;
                    Log("majplay exited");
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            ShowLauncher();
                            ReacquireDevices();
                        }));
                    }
                    catch { }
                });
            }
        }
        catch (Exception ex)
        {
            Log("majplay error: " + ex.Message);
            PostToast("MajPlay", "启动失败，详见 input.log");
        }

    }

    private void PostStatus()
    {
        PostJson(JsonSerializer.Serialize(new
        {
            type = "status",
            io4 = _hasRaw,
            touch = _touch?.IsConnected == true,
            port = _touch?.PortName ?? _config.Touch.Port,
            game = _gameRunning,
            mcm = _config.Mcm.Enabled,
            led = _ledOk,
            ledPort = _led?.PortName ?? ResolveLedPort(),
            aquamai = _config.AquaMai.Toml,
            writable = _config.AquaMai.Writable,
            games = _config.Games.Count,
            active = _config.ActiveGame,
            version = Application.ProductVersion,
        }));
    }

    private void PostAquaMai()
    {
        try
        {
            var path = AquaMaiParser.ResolvePath(_config);
            if (path == null)
            {
                PostJson(JsonSerializer.Serialize(new { type = "aquamai", groups = Array.Empty<object>(), error = "AquaMai.toml not found" }));
                Log("aquamai toml not found");
                return;
            }

            var groups = AquaMaiParser.Load(path);
            var json = JsonSerializer.Serialize(new
                {
                    type = "aquamai",
                    path,
                    writable = _config.AquaMai.Writable,
                    groups,
                },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            PostJson(json);
            Log($"aquamai loaded {path} groups={groups.Count} sections={groups.Sum(g => g.Sections.Count)}");
        }
        catch (Exception ex)
        {
            Log("aquamai parse error " + ex.Message);
        }
    }

    private void PostGames()
    {
        var games = _config.Games
            .Select((g, i) => new { index = i, name = g.DisplayName, bat = g.Bat })
            .ToArray();

        PostJson(JsonSerializer.Serialize(new
        {
            type = "games",
            active = _config.ActiveGame,
            games,
        }));
    }

    private void SwitchVersion(int index)
    {
        if (index < 0 || index >= _config.Games.Count || index == _config.ActiveGame)
            return;

        _config.ActiveGame = index;
        _config.Save();
        Log("active game -> " + _config.CurrentGame.DisplayName);

        PostGames();
        PostAquaMai();
        PostToast("版本", "已切换到 " + _config.CurrentGame.DisplayName);
    }

    private void HandleAquaMaiWrite(JsonElement root, string kind)
    {
        try
        {
            if (!_config.AquaMai.Writable)
            {
                PostJson(JsonSerializer.Serialize(new { type = "aquamai-saved", ok = false, message = "当前是只读模式" }));
                return;
            }

            var path = AquaMaiParser.ResolvePath(_config);
            if (path == null)
            {
                PostJson(JsonSerializer.Serialize(new { type = "aquamai-saved", ok = false, message = "找不到 AquaMai.toml" }));
                return;
            }

            var section = root.TryGetProperty("section", out var secEl) ? secEl.GetString() ?? "" : "";
            (bool Ok, string Message) result;

            if (kind == "aquamai-section")
            {
                var enabled = root.TryGetProperty("enabled", out var enEl) && enEl.GetBoolean();
                result = AquaMaiEditor.SetSection(path, section, enabled);
            }
            else
            {
                var key = root.TryGetProperty("key", out var keyEl) ? keyEl.GetString() ?? "" : "";
                var value = root.TryGetProperty("value", out var valEl) ? valEl.GetString() ?? "" : "";
                result = AquaMaiEditor.SetOption(path, section, key, value);
            }

            Log($"aquamai write {kind} {section}: ok={result.Ok} {result.Message}");
            PostJson(JsonSerializer.Serialize(new { type = "aquamai-saved", ok = result.Ok, message = result.Message }));

            if (result.Ok)
                PostAquaMai();
        }
        catch (Exception ex)
        {
            Log("aquamai write error " + ex.Message);
            PostJson(JsonSerializer.Serialize(new { type = "aquamai-saved", ok = false, message = ex.Message }));
        }
    }

    private void PostToast(string title, string desc)
    {
        PostJson(JsonSerializer.Serialize(new { type = "toast", title, desc }));
    }

    private void PostJson(string json)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        // 触摸线程也会调到这里，WebView2 必须在 UI 线程访问
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string>(PostJson), json); } catch { }
            return;
        }

        try { _web.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
    }

    private static int[] ReadButtons()
    {
        if (!_hasRaw)
            return Array.Empty<int>();

        var pressed = new List<int>(8);
        for (var i = 0; i < ButtonBits.Length; i++)
        {
            var bit = 1u << ButtonBits[i];
            if ((_lastRaw & bit) == 0)
                pressed.Add(i);
        }

        return pressed.ToArray();
    }

    private static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "input.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 诊断日志失败不影响启动器
        }
    }

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
    private struct RAWHID
    {
        public uint dwSizeHid;
        public uint dwCount;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr hDevice,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize);
}
