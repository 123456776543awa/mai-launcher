using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MaimaiLauncher;

public sealed class AquaMaiOption
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string Type { get; set; } = "raw";
    public string Doc { get; set; } = "";
    public bool Enabled { get; set; }
    public int Line { get; set; }
}

public sealed class AquaMaiSection
{
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Title { get; set; } = "";
    public string Doc { get; set; } = "";
    public bool Enabled { get; set; }
    public List<AquaMaiOption> Keys { get; set; } = new();
}

public sealed class AquaMaiGroup
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public List<AquaMaiSection> Sections { get; set; } = new();
}

/// <summary>
/// 直接解析当前版本的 AquaMai.toml：
/// 段落头 / 注释掉的段落头、活动键 / 注释掉的键、键上方的 ## 注释，都当作 UI 数据。
/// </summary>
public static class AquaMaiParser
{
    private static readonly Regex SecRe = new(@"^\s*\[([^\]]+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex SecCmtRe = new(@"^\s*#\s*\[([^\]]+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex KeyRe = new(@"^\s*([A-Za-z0-9_]+)\s*=\s*(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex KeyCmtRe = new(@"^\s*#\s*([A-Za-z0-9_]+)\s*=\s*(.+?)\s*$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> GroupTitles = new()
    {
        ["General"] = "常规",
        ["GameSystem"] = "游戏系统",
        ["GameSettings"] = "游戏设置",
        ["Tweaks"] = "优化",
        ["UX"] = "界面体验",
        ["Utils"] = "工具",
        ["Fancy"] = "花活",
    };

    public static string? ResolvePath(LauncherConfig config)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(config.AquaMai.Toml))
            candidates.Add(config.AquaMai.Toml);

        var dir = config.CurrentGame.EffectiveWorkingDir;
        if (!string.IsNullOrWhiteSpace(dir))
        {
            candidates.Add(Path.Combine(dir, "AquaMai.toml"));
            candidates.Add(Path.Combine(dir, "Package", "AquaMai.toml"));
        }

        if (!string.IsNullOrWhiteSpace(config.CurrentGame.Bat))
        {
            var batDir = Path.GetDirectoryName(config.CurrentGame.Bat);
            if (!string.IsNullOrWhiteSpace(batDir))
            {
                candidates.Add(Path.Combine(batDir, "AquaMai.toml"));
                candidates.Add(Path.Combine(batDir, "Package", "AquaMai.toml"));
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static List<AquaMaiGroup> Load(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var sections = new List<AquaMaiSection>();
        AquaMaiSection? current = null;
        var doc = new List<string>();

        string FlushDoc()
        {
            var text = string.Join("\n", doc.Where(x => x.Length > 0)).Trim();
            doc.Clear();
            return text;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                if (doc.Count > 0) doc.Add("");
                continue;
            }

            var sec = SecRe.Match(line);
            var secCmt = sec.Success ? null : SecCmtRe.Match(line);
            if (sec.Success || (secCmt?.Success ?? false))
            {
                var name = (sec.Success ? sec : secCmt!).Groups[1].Value.Trim();
                var leaf = name.Split('.').Last();
                current = new AquaMaiSection
                {
                    Name = name,
                    Group = name.Split('.')[0],
                    Title = CamelSplit(leaf),
                    Enabled = !trimmed.StartsWith("#", StringComparison.Ordinal),
                    Doc = FlushDoc(),
                };
                sections.Add(current);
                continue;
            }

            var key = KeyRe.Match(line);
            var keyCmt = key.Success ? null : KeyCmtRe.Match(line);
            if (key.Success || (keyCmt?.Success ?? false))
            {
                var m = key.Success ? key : keyCmt!;
                var raw = m.Groups[2].Value.Trim();
                var enabled = key.Success;

                if (raw.StartsWith("[", StringComparison.Ordinal) &&
                    raw.Count(c => c == '[') > raw.Count(c => c == ']'))
                {
                    var buf = new StringBuilder(raw);
                    var j = i + 1;
                    while (j < lines.Length &&
                           buf.ToString().Count(c => c == '[') > buf.ToString().Count(c => c == ']'))
                    {
                        buf.Append(' ').Append(lines[j].Trim());
                        j++;
                    }
                    raw = buf.ToString();
                    i = j - 1;
                }

                current ??= new AquaMaiSection { Name = "General", Group = "General", Title = "General", Enabled = true };
                current.Keys.Add(new AquaMaiOption
                {
                    Key = m.Groups[1].Value,
                    Value = StripQuotes(raw),
                    Type = TypeOf(raw),
                    Doc = FlushDoc(),
                    Enabled = enabled,
                    Line = i + 1,
                });
                continue;
            }

            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                var text = trimmed.TrimStart('#').Trim();
                if (text.Length > 0) doc.Add(text);
            }
        }

        var groups = new List<AquaMaiGroup>();
        foreach (var g in sections.GroupBy(s => s.Group))
        {
            groups.Add(new AquaMaiGroup
            {
                Name = g.Key,
                Title = GroupTitles.TryGetValue(g.Key, out var t) ? t : g.Key,
                Sections = g.ToList(),
            });
        }

        return groups;
    }

    private static string CamelSplit(string name) =>
        Regex.Replace(name, "(?<!^)(?=[A-Z])", " ").Trim();

    private static string TypeOf(string value)
    {
        var v = value.Trim();
        if (v.StartsWith("\"", StringComparison.Ordinal) || v.StartsWith("'", StringComparison.Ordinal)) return "string";
        if (v is "true" or "false") return "bool";
        if (Regex.IsMatch(v, @"^-?\d+$")) return "int";
        if (Regex.IsMatch(v, @"^-?\d+\.\d+([eE][-+]?\d+)?$")) return "float";
        if (v.StartsWith("[", StringComparison.Ordinal)) return "array";
        return "raw";
    }

    private static string StripQuotes(string value)
    {
        var v = value.Trim();
        if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0])
            return v[1..^1];
        return v;
    }
}

/// <summary>
/// 只改“值”，不重新序列化整份 TOML：保留注释、顺序和缩进。
/// 写入前备份 .bak，写入后重新解析校验，失败自动回滚。
/// </summary>
public static class AquaMaiEditor
{
    public static (bool Ok, string Message) SetOption(string path, string section, string key, string value)
    {
        return Mutate(path, lines =>
        {
            var sec = FindSection(lines, section);
            if (sec < 0) return (false, "没找到段落 " + section);

            EnableSectionAt(lines, sec);

            var keyIdx = FindKey(lines, sec, key);
            var newLine = $"{key} = {value}";
            if (keyIdx >= 0)
                lines[keyIdx] = newLine;
            else
                lines.Insert(sec + 1, newLine);

            return (true, "已写入 " + key);
        }, () => VerifyOption(path, section, key, value));
    }

    public static (bool Ok, string Message) SetSection(string path, string section, bool enabled)
    {
        return Mutate(path, lines =>
        {
            var sec = FindSection(lines, section);
            if (sec < 0) return (false, "没找到段落 " + section);

            var line = lines[sec];
            var name = $"[{section}]";
            lines[sec] = enabled ? name : "#" + name;
            return (true, enabled ? "已启用 " + section : "已停用 " + section);
        }, () => VerifySection(path, section, enabled));
    }

    private static (bool Ok, string Message) Mutate(string path, Func<List<string>, (bool Ok, string Message)> change, Func<bool> verify)
    {
        if (!File.Exists(path))
            return (false, "找不到 AquaMai.toml");

        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var original = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n").Split('\n').ToList();

        var (ok, message) = change(lines);
        if (!ok) return (false, message);

        var text = string.Join("\n", lines).Replace("\n", newline);

        var backup = path + ".launcher.bak";
        try
        {
            File.Copy(path, backup, true);
            File.WriteAllText(path, text, new UTF8Encoding(bom));
        }
        catch (Exception ex)
        {
            return (false, "写入失败: " + ex.Message);
        }

        if (!verify())
        {
            try { File.Copy(backup, path, true); } catch { }
            return (false, "校验失败，已回滚");
        }

        return (true, message);
    }

    private static bool VerifyOption(string path, string section, string key, string value)
    {
        try
        {
            var groups = AquaMaiParser.Load(path);
            var sec = groups.SelectMany(g => g.Sections).FirstOrDefault(s => s.Name == section);
            var opt = sec?.Keys.FirstOrDefault(k => k.Key == key);
            return opt != null && opt.Value == value && opt.Enabled;
        }
        catch
        {
            return false;
        }
    }

    private static bool VerifySection(string path, string section, bool enabled)
    {
        try
        {
            var groups = AquaMaiParser.Load(path);
            var sec = groups.SelectMany(g => g.Sections).FirstOrDefault(s => s.Name == section);
            return sec != null && sec.Enabled == enabled;
        }
        catch
        {
            return false;
        }
    }

    private static int FindSection(List<string> lines, string section)
    {
        var target = $"[{section}]";
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t == target || t == "#" + target) return i;
        }
        return -1;
    }

    private static int FindKey(List<string> lines, int sectionIndex, string key)
    {
        var pattern = new Regex(@"^#?\s*" + Regex.Escape(key) + @"\s*=");
        for (var i = sectionIndex + 1; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("[", StringComparison.Ordinal) || t.StartsWith("#[", StringComparison.Ordinal)) break;
            if (pattern.IsMatch(t)) return i;
        }
        return -1;
    }

    private static void EnableSectionAt(List<string> lines, int index)
    {
        var t = lines[index].TrimStart();
        if (t.StartsWith("#[", StringComparison.Ordinal))
            lines[index] = t[1..];
    }
}
