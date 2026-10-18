using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MaimaiLauncher;

public sealed class SetupForm : Form
{
    private readonly LauncherConfig _config;
    private readonly TextBox _bat = new();
    private readonly CheckBox _startup = new();

    public SetupForm(LauncherConfig config)
    {
        _config = config;

        Text = "舞萌手台启动器 - 首次设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(660, 270);
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "app.ico")); } catch { }

        var credit = new Label
        {
            Text = "by 123456776543",
            AutoSize = true,
            ForeColor = Color.Gray,
            Location = new Point(24, 228),
        };
        Controls.Add(credit);

        var title = new Label
        {
            Text = "选择游戏启动脚本",
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 20),
        };
        Controls.Add(title);

        var desc = new Label
        {
            Text = "请选择 sinmai 的 启动.bat（选 Package 里的 start.bat 也可以）。\n启动器会记住路径，换机器或换目录只改这里。",
            AutoSize = true,
            Location = new Point(24, 58),
        };
        Controls.Add(desc);

        _bat.Location = new Point(24, 104);
        _bat.Size = new Size(500, 28);
        Controls.Add(_bat);

        var browse = new Button
        {
            Text = "浏览...",
            Location = new Point(536, 103),
            Size = new Size(96, 30),
        };
        browse.Click += (_, _) => Browse();
        Controls.Add(browse);

        var note = new Label
        {
            Text = "找不到？直接选游戏根目录下的 启动.bat。",
            AutoSize = true,
            ForeColor = Color.Gray,
            Location = new Point(24, 142),
        };
        Controls.Add(note);

        var ok = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Location = new Point(440, 220),
            Size = new Size(88, 32),
        };
        ok.Click += (_, _) => Confirm();
        Controls.Add(ok);

        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(544, 220),
            Size = new Size(88, 32),
        };
        Controls.Add(cancel);

        _startup.Text = "开机自动启动启动器";
        _startup.AutoSize = true;
        _startup.Location = new Point(24, 176);
        _startup.Checked = _config.Startup;
        Controls.Add(_startup);

        AcceptButton = ok;
        CancelButton = cancel;

        var detected = AutoDetect();
        if (detected != null)
            _bat.Text = detected;
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择 启动.bat",
            Filter = "启动脚本 (*.bat)|*.bat|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog(this) == DialogResult.OK)
            _bat.Text = dlg.FileName;
    }

    private void Confirm()
    {
        var path = _bat.Text.Trim().Trim('"');
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "启动.bat 不存在，请重新选择。", "路径无效",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _config.Game.Bat = Path.GetFullPath(path);
        _config.Game.WorkingDir = Path.GetDirectoryName(_config.Game.Bat) ?? "";
        _config.Startup = _startup.Checked;

        var mcm = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "mcm.exe");
        _config.Mcm.Enabled = File.Exists(mcm);

        DialogResult = DialogResult.OK;
        Close();
    }

    private static string? AutoDetect()
    {
        var roots = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetParent(AppContext.BaseDirectory)?.FullName,
            Directory.GetCurrentDirectory(),
        };

        string[] names = { "启动.bat", "启动游戏.bat", "start.bat" };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            foreach (var name in names)
            {
                var direct = Path.Combine(root, name);
                if (File.Exists(direct))
                    return direct;

                var package = Path.Combine(root, "Package", name);
                if (File.Exists(package))
                    return package;
            }
        }

        return null;
    }
}
