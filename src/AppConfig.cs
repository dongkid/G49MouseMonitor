using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace G49BatteryMonitor;

/// <summary>
/// 配置。跟随 exe 放在同一个文件夹里（config.json），整个文件夹拷走就能用，C 盘零残留。
/// </summary>
internal sealed class AppConfig
{
    /// <summary>无线（2.4G）时的查询间隔，秒。</summary>
    public int PollSecondsNormal { get; set; } = 300;

    /// <summary>有线时的查询间隔，秒。有线不耗鼠标电池，可以查勤一点。</summary>
    public int PollSecondsWired { get; set; } = 60;

    /// <summary>查不到设备时的重试间隔，秒。这个要短，"拔线/插线"才能立刻反应过来。</summary>
    public int PollSecondsDisconnected { get; set; } = 15;

    /// <summary>第一档提醒阈值（%）。</summary>
    public int WarnNotice { get; set; } = 20;

    /// <summary>第二档提醒阈值（%）。</summary>
    public int WarnLow { get; set; } = 10;

    /// <summary>第三档提醒阈值（%）。</summary>
    public int WarnCritical { get; set; } = 5;

    /// <summary>低电量提示音。</summary>
    public bool SoundEnabled { get; set; } = true;

    /// <summary>第一次把窗口收进后台时，弹一次提示告诉用户程序还在跑。</summary>
    public bool ShowHideHint { get; set; } = true;

    /// <summary>提醒方式：auto=优先系统通知、失败自动改用自绘小卡片；toast=只用系统通知；card=只用小卡片。</summary>
    public string AlertStyle { get; set; } = "auto";

    [JsonIgnore]
    public string FilePath { get; private set; } = "";

    private readonly List<string> _issues = [];
    private bool _fileLevelFailure;

    /// <summary>这次 Load 遇到的毛病（人话描述）。空 = 一切正常；这些字段不写回文件。</summary>
    [JsonIgnore]
    public ReadOnlyCollection<string> Issues => _issues.AsReadOnly();

    /// <summary>true = 整个文件读不了（语法坏/根不是对象/读不到），这次是整份回退的默认值。</summary>
    [JsonIgnore]
    public bool FileLevelFailure => _fileLevelFailure;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 读配置。容错粒度是"按字段"：只有读不出来的那个字段用默认值，别的字段一律保留。
    /// 只有整个文件读不到 / JSON 语法坏（半截文件、尾逗号）才整份回默认，这时会记一条
    /// <see cref="FileLevelFailure"/>，由 Program.cs 写进日志，绝不静默丢设置。
    /// </summary>
    public static AppConfig Load(string exeDir)
    {
        string path = Path.Combine(exeDir, "config.json");
        AppConfig cfg = new() { FilePath = path };
        if (!File.Exists(path)) { cfg.Normalize(); return cfg; }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            cfg.NoteFileFailure("读取不到文件", ex);
            cfg.Normalize();
            return cfg;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                cfg.ReadFields(doc.RootElement);
            }
            else
            {
                cfg.NoteFileFailure($"JSON 根节点是 {doc.RootElement.ValueKind}，不是对象", null);
            }
        }
        catch (JsonException ex)
        {
            cfg.NoteFileFailure("JSON 语法损坏", ex);
            cfg.Normalize();
            return cfg;
        }

        cfg.Normalize();
        return cfg;
    }

    /// <summary>
    /// 逐字段读。读不出来的字段保持默认值（构造时已填好），并在 <see cref="Issues"/> 里
    /// 留一条人话说明；一个字段坏掉不影响其它字段。
    /// </summary>
    private void ReadFields(JsonElement root)
    {
        foreach (var (name, type, read) in Readers)
        {
            if (!root.TryGetProperty(name, out var el)) continue; // 没写这个键 = 用默认值
            try
            {
                read(this, el);
            }
            catch (Exception ex)
            {
                _issues.Add($"{name} 读不出来（期望 {type}），这一项改用默认值：{ex.Message}");
            }
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (!Readers.Any(r => r.Name == prop.Name))
                _issues.Add($"配置里有未知项 {prop.Name}，已忽略");
        }
    }

    private void NoteFileFailure(string what, Exception? ex)
    {
        _fileLevelFailure = true;
        _issues.Add(ex == null ? $"{what}，已整份回退到默认设置" : $"{what}，已整份回退到默认设置：{ex.Message}");
    }

    private static readonly (string Name, string Type, Action<AppConfig, JsonElement> Read)[] Readers =
    {
        ("PollSecondsNormal", "整数(秒)", static (c, e) => c.PollSecondsNormal = e.GetInt32()),
        ("PollSecondsWired", "整数(秒)", static (c, e) => c.PollSecondsWired = e.GetInt32()),
        ("PollSecondsDisconnected", "整数(秒)", static (c, e) => c.PollSecondsDisconnected = e.GetInt32()),
        ("WarnNotice", "整数(%)", static (c, e) => c.WarnNotice = e.GetInt32()),
        ("WarnLow", "整数(%)", static (c, e) => c.WarnLow = e.GetInt32()),
        ("WarnCritical", "整数(%)", static (c, e) => c.WarnCritical = e.GetInt32()),
        ("SoundEnabled", "true/false", static (c, e) => c.SoundEnabled = e.GetBoolean()),
        ("ShowHideHint", "true/false", static (c, e) => c.ShowHideHint = e.GetBoolean()),
        ("AlertStyle", "文本", static (c, e) => c.AlertStyle = e.GetString() ?? c.AlertStyle),
    };

    public void Normalize()
    {
        PollSecondsNormal = Math.Clamp(PollSecondsNormal, 10, 3600);
        PollSecondsWired = Math.Clamp(PollSecondsWired, 10, 3600);
        PollSecondsDisconnected = Math.Clamp(PollSecondsDisconnected, 5, 3600);
        WarnNotice = Math.Clamp(WarnNotice, 1, 100);
        WarnLow = Math.Clamp(WarnLow, 1, WarnNotice);
        WarnCritical = Math.Clamp(WarnCritical, 1, WarnLow);
        // 旧版本用过的 "balloon"（托盘气泡）已被系统通知取代，读到就自动升级
        if (AlertStyle == "balloon") AlertStyle = "toast";
        if (AlertStyle is not ("auto" or "toast" or "card")) AlertStyle = "auto";
    }

    /// <summary>
    /// 原子写：先写同目录临时文件，落盘后再整体改名覆盖 config.json。
    /// 断电/被杀进程只会留下一个 .tmp 残片，config.json 永远是完整的旧内容或完整的新内容。
    /// </summary>
    public void Save()
    {
        try
        {
            Normalize();
            var dir = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);

            string tmp = Path.Combine(dir, $"config.json.{Guid.NewGuid():N}.tmp");
            try
            {
                // File.WriteAllText 会自己开 FileShare.Read，是我们自己写的，同进程读没问题
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
                // File.Move(overwrite) 在同一个卷上是原子改名替换
                File.Move(tmp, FilePath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        catch
        {
            // 写不进配置不该影响监控
        }
    }
}
