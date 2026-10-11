using System.Text;
using System.Windows.Forms;

namespace G49BatteryMonitor;

internal static class Program
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static MainForm? _form;

    [STAThread]
    private static void Main(string[] args)
    {
        string exeDir = AppContext.BaseDirectory;

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            SelfTest.Run(exeDir);
            return;
        }

        if (args.Any(a => a.Equals("--test-notify", StringComparison.OrdinalIgnoreCase)))
        {
            NotifyTest.Run(exeDir);
            return;
        }

        bool background = args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));

        // 单实例：第二次双击 exe 不会再起一个进程，而是把已经在跑的那个窗口叫出来
        _mutex = new Mutex(true, @"Local\G49BatteryMonitor.SingleInstance", out bool isFirstInstance);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\G49BatteryMonitor.Show");
        if (!isFirstInstance)
        {
            try { _showEvent.Set(); } catch { }
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 给进程一个通知身份，系统通知才会归到这个程序名下
        SystemToast.RegisterForCurrentProcess();

        // 兜底：万一哪里抛异常，也留下线索，不要静默地消失
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash(exeDir, e.ExceptionObject?.ToString() ?? "未知异常");
        Application.ThreadException += (_, e) => WriteCrash(exeDir, e.Exception?.ToString() ?? "未知异常");

        // Logger 要先建好，AppConfig.Load 的"回退到默认值"才有地方记
        using var log = new Logger(exeDir);

        var cfg = AppConfig.Load(exeDir);
        if (cfg.Issues.Count > 0)
        {
            // 整份回退是丢设置的大事，记 WARN；单个字段坏掉只记 INFO
            if (cfg.FileLevelFailure) log.Warn("config.json 读不了，已整份回退到默认设置");
            foreach (var issue in cfg.Issues) log.Info($"config.json：{issue}");
        }

        // 自启项如果已经存在但路径不对（比如文件夹被搬走了），顺手修正；没有就什么都不做
        AutoStart.RepairIfNeeded();

        log.Info("程序启动" + (background ? "（开机自启，静默进后台）" : ""));
        // 把生效的设置完整记一笔 —— 以后再出现"间隔怎么变了"这类疑问，看日志就有答案
        log.Info($"生效设置：间隔 无线={cfg.PollSecondsNormal}s 有线={cfg.PollSecondsWired}s 未连接={cfg.PollSecondsDisconnected}s；" +
                 $"预警阈值 {cfg.WarnNotice}/{cfg.WarnLow}/{cfg.WarnCritical}%；提示音={(cfg.SoundEnabled ? "开" : "关")}；提醒方式={cfg.AlertStyle}");

        _form = new MainForm(cfg, log, background);

        var waiter = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_showEvent.WaitOne()) break;
                }
                catch
                {
                    break;
                }
                _form?.RequestShow();
            }
        })
        { IsBackground = true, Name = "G49-ShowWaiter" };
        waiter.Start();

        Application.Run(_form);

        log.Info("程序退出");
        try { _mutex.ReleaseMutex(); } catch { }
    }

    private static void WriteCrash(string exeDir, string detail)
    {
        try
        {
            File.AppendAllText(Path.Combine(exeDir, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }
}

/// <summary>
/// 命令行提醒自测：弹一条真实提醒然后自动退出。
/// 用法：G49BatteryMonitor.exe --test-notify
/// 用来确认"这台机器上低电量提醒到底能不能弹出来"。
/// </summary>
internal static class NotifyTest
{
    public static void Run(string exeDir)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 给进程一个通知身份，系统通知才会归到这个程序名下
        SystemToast.RegisterForCurrentProcess();

        using var log = new Logger(exeDir);
        log.Info("开始提醒自测");

        var report = new StringBuilder();
        report.AppendLine("G49 鼠标电量监控 —— 系统通知自测报告");
        report.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        report.AppendLine("通知身份：" + SystemToast.AppUserModelId);
        report.AppendLine();

        // 先清掉本程序自己的历史通知，否则同 Tag 的通知会被替换、条数不变，没法当证据用
        SystemToast.ClearHistory();
        Thread.Sleep(400);

        int before = SystemToast.HistoryCount();
        report.AppendLine("发送前，通知中心里本程序的通知条数：" + before + "（已先清空）");

        bool sent = SystemToast.Show("提醒自测",
            "看到这条通知就说明电量提醒能正常弹出来，并且会留在通知中心里。",
            AlertLevel.Warning, log);
        report.AppendLine("发送调用结果：" + (sent ? "成功" : "失败 —— " + SystemToast.LastError));

        Thread.Sleep(2500);
        int after = SystemToast.HistoryCount();
        report.AppendLine("发送后，通知中心里本程序的通知条数：" + after);
        report.AppendLine();

        if (!sent)
            report.AppendLine("结论：系统通知发送失败 ❌ 程序会自动改用自绘小卡片兜底。");
        else if (after > before)
            report.AppendLine("结论：通知已真实送达 Windows 通知中心 ✅（客观证据，不依赖人眼确认）");
        else
            report.AppendLine("结论：发送调用成功，但通知中心里没查到 ⚠ 可能被系统策略（专注助手/勿扰）拦下了。");

        log.Info("提醒自测结束：" + (sent ? "发送成功" : "发送失败") + $"，通知中心 {before} → {after}");

        File.WriteAllText(Path.Combine(exeDir, "notify-test.txt"), report.ToString(), Encoding.UTF8);
    }
}

/// <summary>
/// 命令行自检：不启动界面，直接枚举接口、读一次电量，把结果写到 exe 同目录的 selftest.txt。
/// 用法：G49BatteryMonitor.exe --selftest
/// </summary>
internal static class SelfTest
{
    public static void Run(string exeDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("G49 鼠标电量监控 —— 自检报告");
        sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("程序：" + (Environment.ProcessPath ?? "(未知)"));
        sb.AppendLine("系统：" + Environment.OSVersion.VersionString + " / " + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);
        sb.AppendLine();

        try
        {
            var candidates = G49Locator.Enumerate();
            sb.AppendLine($"G49 相关 HID 接口：{candidates.Count} 个");
            foreach (var c in candidates)
                sb.AppendLine("  [" + c.Name + "] " + c.Path);
            sb.AppendLine();

            var device = G49Locator.OpenFirstWritable(out string trace, out BatteryReading? first);
            sb.AppendLine("接口筛选过程：");
            sb.AppendLine(trace.Replace("\\n", "\n"));
            sb.AppendLine();

            if (device == null)
            {
                sb.AppendLine("结果：没有找到可以通信的接口（接收器可能没插，或者鼠标处于关机/休眠）。");
            }
            else
            {
                sb.AppendLine("使用接口：" + device.Path);
                sb.AppendLine("连接方式：" + device.LinkName + "（PID 0x" + device.ProductId.ToString("X4") + "）");
                sb.AppendLine("首次读数：" + Describe(first));
                for (int i = 0; i < 3; i++)
                    sb.AppendLine($"连续第 {i + 2} 次：" + Describe(device.QueryBattery(200)));
                device.Dispose();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("自检出错：" + ex);
        }

        string path = Path.Combine(exeDir, "selftest.txt");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static string Describe(BatteryReading? r)
    {
        if (r == null) return "没有回包";
        if (!r.Ok) return "无数据（" + r.Note + "）";
        return $"{r.Percent}%  {r.MilliVolts}mV  充电={(r.Charging ? "是" : "否")}";
    }
}
