using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MaimaiLauncher;

public static class McmController
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoActivate = 0x0010;

    public static async Task<bool> LaunchAndPositionAsync(LauncherConfig config, Screen screen)
    {
        if (!config.Mcm.Enabled)
            return false;

        var process = Find();
        Log($"launch requested, existing={(process == null ? "none" : process.Id.ToString())}");
        if (process == null)
        {
            TryStart(config.Mcm);

            for (var i = 0; i < 120 && process == null; i++)
            {
                await Task.Delay(100);
                process = Find();
            }
        }

        if (process == null)
        {
            Log("process not found after 12s");
            return false;
        }

        Log($"process id={process.Id}");

        await Task.Run(() =>
        {
            try { process.WaitForInputIdle(5000); } catch { }
        });

        for (var i = 0; i < 100; i++)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
                break;
            await Task.Delay(100);
        }

        var handle = process.MainWindowHandle;
        if (handle == IntPtr.Zero)
        {
            Log("main window handle is zero");
            return false;
        }

        var x = screen.Bounds.Left + config.Mcm.X;
        var y = screen.Bounds.Top + config.Mcm.Y;
        var ok = SetWindowPos(handle, HwndTopmost, x, y, config.Mcm.Width, config.Mcm.Height,
            SwpShowWindow | SwpNoActivate);
        Log($"setwindowpos ok={ok} at {x},{y} {config.Mcm.Width}x{config.Mcm.Height}");
        return true;
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "mcm.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响启动
        }
    }

    private static Process? Find() =>
        Process.GetProcessesByName("MaiChartManager").FirstOrDefault(p => !p.HasExited);

    private static void TryStart(McmConfig mcm)
    {
        // Store 应用用 IApplicationActivationManager 激活最可靠（explorer shell: 在部分场景无反应）
        try
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            var hr = manager.ActivateApplication(mcm.Aumid, null, 0, out var pid);
            Log($"activate aumid hr=0x{hr:X8} pid={pid}");
            if (hr == 0)
                return;
        }
        catch (Exception ex)
        {
            Log("activate failed: " + ex.Message);
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe",
                $"shell:AppsFolder\\{mcm.Aumid}") { UseShellExecute = true });
            Log($"explorer shell:AppsFolder\\{mcm.Aumid}");
            return;
        }
        catch (Exception ex)
        {
            Log("explorer launch failed: " + ex.Message);
        }

        try
        {
            Process.Start(new ProcessStartInfo(mcm.Launch) { UseShellExecute = true });
            Log("fallback launch: " + mcm.Launch);
        }
        catch (Exception ex)
        {
            Log("fallback launch failed: " + ex.Message);
        }
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication(string appUserModelId, string? arguments, int options, out uint processId);
        int ActivateForFile(string appUserModelId, IntPtr itemArray, string verb, out uint processId);
        int ActivateForProtocol(string appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager
    {
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);
}

public sealed class TouchReader : IDisposable
{
    private string _port;
    private readonly int _baud;
    private Thread? _thread;
    private volatile bool _running;
    private SerialPort? _serial;
    private string _lastActiveKey = "";
    private DateTime _lastFrameAt = DateTime.UtcNow;
    private int _rawCount;

    public event Action<int[]>? ActiveChanged;

    public Action<string>? Log { get; set; }

    public bool IsRunning => _running;

    public bool IsConnected { get; private set; }

    public string PortName => _port;

    public TouchReader(string port, int baud)
    {
        _port = port;
        _baud = baud;
    }

    public void Start()
    {
        if (_running)
            return;

        _port = ResolvePort(_port);

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "TouchReader" };
        _thread.Start();
    }

    // 按“总线已报告设备描述”找触屏（STM32 Virtual ComPort），避免 COM 号变化后连错口
    private static string ResolvePort(string configured)
    {
        try
        {
            using var usb = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb != null)
            {
                foreach (var vid in usb.GetSubKeyNames())
                {
                    if (!vid.StartsWith("VID_0483&PID_5740", StringComparison.OrdinalIgnoreCase))
                        continue;

                    using var dev = usb.OpenSubKey(vid);
                    if (dev == null)
                        continue;

                    foreach (var inst in dev.GetSubKeyNames())
                    {
                        using var dp = dev.OpenSubKey(inst + @"\Device Parameters");
                        var port = dp?.GetValue("PortName") as string;
                        if (!string.IsNullOrWhiteSpace(port))
                            return port;
                    }
                }
            }
        }
        catch
        {
            // 取不到就用配置里的端口
        }

        return configured;
    }

    public void Stop()
    {
        if (!_running && _thread == null)
            return;

        Log?.Invoke("touch stop requested");
        _running = false;

        try { _serial?.Close(); } catch { }

        var thread = _thread;
        if (thread != null && !thread.Join(800))
            Log?.Invoke("touch thread did not exit in time");

        try { _serial?.Dispose(); } catch { }
        _serial = null;
        _thread = null;
        Log?.Invoke("touch stopped");
    }

    private void Loop()
    {
        while (_running)
        {
            RunSession();
            if (_running)
            {
                Log?.Invoke("touch retry in 1s");
                Thread.Sleep(1000);
            }
        }
    }

    private void RunSession()
    {
        _port = ResolvePort(_port);
        SerialPort? sp = null;
        try
        {
            sp = new SerialPort(_port, _baud, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 100,
                WriteTimeout = 200,
            };
            _serial = sp;
            sp.Open();
            sp.DtrEnable = true;
            sp.RtsEnable = true;
            Log?.Invoke("touch open " + _port);
            IsConnected = true;

            // 参照 MajdataPlay 的初始化：RSET(重校准 MPR121) → HALT → STAT
            try { sp.Write("{RSET}"); } catch { }
            Thread.Sleep(1500);
            try { sp.ReadExisting(); } catch { }
            try { sp.Write("{HALT}"); } catch { }
            Thread.Sleep(200);
            try { sp.Write("{STAT}"); } catch { }
            Thread.Sleep(300);
            try { sp.DiscardInBuffer(); } catch { }

            var frame = new System.Collections.Generic.List<byte>(9);

            while (_running)
            {
                int value;
                try { value = sp.ReadByte(); }
                catch (TimeoutException)
                {
                    // 2 秒没帧就重发 STAT，防止板子掉出数据模式
                    if ((DateTime.UtcNow - _lastFrameAt).TotalSeconds > 2)
                    {
                        try { sp.Write("{STAT}"); } catch { }
                        _lastFrameAt = DateTime.UtcNow;
                    }
                    continue;
                }
                catch { break; }

                if (value < 0)
                    continue;

                var b = (byte)value;
                if (_rawCount < 80)
                {
                    _rawCount++;
                    Log?.Invoke("touch byte " + b.ToString("X2"));
                }

                if (b == 0x28)
                {
                    frame.Clear();
                    frame.Add(b);
                    continue;
                }

                if (frame.Count == 0)
                    continue;

                frame.Add(b);

                if (frame.Count == 9 && b == 0x29)
                {
                    _lastFrameAt = DateTime.UtcNow;

                    long bits = 0;
                    for (var i = 0; i < 7; i++)
                        bits |= (long)(frame[i + 1] & 0x1F) << (5 * i);

                    // 34 个感应区都上报：A1–A8(0–7)、B1–B8(8–15)、C1/C2(16–17)、
                    // D1–D8(18–25)、E1–E8(26–33)
                    var active = new System.Collections.Generic.List<int>(34);
                    for (var i = 0; i < 34; i++)
                        if ((bits & (1L << i)) != 0)
                            active.Add(i);

                    var key = string.Join(",", active);
                    if (key != _lastActiveKey)
                    {
                        _lastActiveKey = key;
                        Log?.Invoke("touch frame " + BitConverter.ToString(frame.ToArray()) + " active=[" + key + "]");
                        ActiveChanged?.Invoke(active.ToArray());
                    }

                    frame.Clear();
                }
                else if (frame.Count > 9)
                {
                    frame.Clear();
                }
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("touch error: " + ex.Message);
        }
        finally
        {
            try { sp?.Close(); } catch { }
            try { sp?.Dispose(); } catch { }
            if (ReferenceEquals(_serial, sp))
                _serial = null;
            IsConnected = false;
            Log?.Invoke("touch session end");
        }
    }

    public void Dispose() => Stop();
}

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MaimaiLauncher";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null)
                return;

            if (enabled)
                key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
            else
                key.DeleteValue(ValueName, false);
        }
        catch
        {
            // 自启动设置失败不影响启动器
        }
    }
}

public static class WindowMover
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;

    public static void MoveTo(IntPtr hwnd, Rectangle bounds)
    {
        SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            SwpNoZOrder | SwpShowWindow);
    }

    public static void BringToFront(IntPtr hwnd) => SetForegroundWindow(hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

public static class SystemActions
{
    private const byte VkVolumeUp = 0xAF;
    private const byte VkVolumeDown = 0xAE;
    private const uint KeyeventfKeyup = 0x0002;

    public static void VolumeUp(int steps = 5) => Press(VkVolumeUp, steps);

    public static void VolumeDown(int steps = 5) => Press(VkVolumeDown, steps);

    private static void Press(byte key, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, KeyeventfKeyup, UIntPtr.Zero);
            Thread.Sleep(40);
        }
    }

    public static void Shutdown() => Run("shutdown.exe", "/s /t 0");

    public static void Reboot() => Run("shutdown.exe", "/r /t 0");

    private static void Run(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // 关机/重启失败时保持启动器可用
        }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
