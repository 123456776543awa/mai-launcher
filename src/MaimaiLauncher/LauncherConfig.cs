using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaimaiLauncher;

public sealed class GameConfig
{
    public string Name { get; set; } = "";
    public string Bat { get; set; } = "";
    public string WorkingDir { get; set; } = "";
    public string WaitProcess { get; set; } = "Sinmai";

    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(Bat) && File.Exists(Bat);

    [JsonIgnore]
    public string EffectiveWorkingDir =>
        !string.IsNullOrWhiteSpace(WorkingDir)
            ? WorkingDir
            : (IsValid ? Path.GetDirectoryName(Path.GetFullPath(Bat)) ?? "" : "");

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name;
            if (!string.IsNullOrWhiteSpace(Bat))
            {
                var dir = Path.GetDirectoryName(Bat);
                var leaf = string.IsNullOrWhiteSpace(dir) ? "" : Path.GetFileName(dir.TrimEnd('\\', '/'));
                if (!string.IsNullOrWhiteSpace(leaf)) return leaf;
                return Path.GetFileNameWithoutExtension(Bat);
            }
            return "未命名版本";
        }
    }
}

public sealed class McmConfig
{
    public bool Enabled { get; set; } = true;
    public string Launch { get; set; } = "mcm.exe";
    public string Aumid { get; set; } = "22369479C5405.MaiChartManager_wm9y4rc078y4p!App";
    public int X { get; set; } = 91;
    public int Y { get; set; } = 1081;
    public int Width { get; set; } = 898;
    public int Height { get; set; } = 599;
}

public sealed class TouchConfig
{
    public string Port { get; set; } = "auto";
    public int Baud { get; set; } = 9600;
}

public sealed class MajPlayConfig
{
    public bool Enabled { get; set; } = true;
    public string Exe { get; set; } = "";
    public string Args { get; set; } = "";
    public string WaitProcess { get; set; } = "MajdataPlay";
}

public sealed class AquaMaiConfig
{
    public string Toml { get; set; } = "";
    public bool Writable { get; set; } = false;
}

public sealed class Io4Config
{
    public string Vid { get; set; } = "0CA3";
    public string Pid { get; set; } = "0021";
}

public sealed class ScreenConfig
{
    public string Target { get; set; } = "portrait";
    public string DeviceName { get; set; } = "";
    public int Width { get; set; } = 1080;
    public int Height { get; set; } = 1920;
    public int TopHeight { get; set; } = 469;
    public int CircleCx { get; set; } = 540;
    public int CircleCy { get; set; } = 1380;
    public int CircleR { get; set; } = 540;
}

public sealed class LauncherConfig
{
    public GameConfig Game { get; set; } = new();
    public List<GameConfig> Games { get; set; } = new();
    public int ActiveGame { get; set; }
    public McmConfig Mcm { get; set; } = new();
    public MajPlayConfig MajPlay { get; set; } = new();
    public AquaMaiConfig AquaMai { get; set; } = new();
    public TouchConfig Touch { get; set; } = new();
    public Io4Config Io4 { get; set; } = new();
    public ScreenConfig Screen { get; set; } = new();
    public bool Startup { get; set; } = true;
    public string Ui { get; set; } = "v2";

    [JsonIgnore]
    public GameConfig CurrentGame => Games.Count > 0
        ? Games[Math.Clamp(ActiveGame, 0, Games.Count - 1)]
        : Game;

    /// <summary>旧配置（只有 Game）自动迁移成 Games[0]。</summary>
    public void Normalize()
    {
        if (Games.Count == 0)
            Games.Add(Game);
        ActiveGame = Math.Clamp(ActiveGame, 0, Games.Count - 1);
    }

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static LauncherConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<LauncherConfig>(json);
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏时回退到默认值，由首次设置重新生成
        }

        var fallback = new LauncherConfig();
        fallback.Normalize();
        return fallback;
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(ConfigPath, json);
    }
}
