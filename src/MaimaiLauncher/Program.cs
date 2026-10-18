using System;
using System.Windows.Forms;

namespace MaimaiLauncher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 单实例：避免重复启动抢 COM3 / COM21
        var mutex = new System.Threading.Mutex(true, "MaimaiLauncher.SingleInstance", out var created);
        if (!created)
            return;

        ApplicationConfiguration.Initialize();

        var config = LauncherConfig.Load();
        if (!config.Game.IsValid)
        {
            using var setup = new SetupForm(config);
            if (setup.ShowDialog() != DialogResult.OK)
                return;
            config.Save();
        }

        StartupManager.Apply(config.Startup);
        Application.Run(new MainForm(config));
        GC.KeepAlive(mutex);
    }
}
