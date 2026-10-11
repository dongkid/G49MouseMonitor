using System.Drawing;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Windows.UI.Notifications;

namespace G49BatteryMonitor;

internal enum AlertLevel { Info, Warning, Critical }

/// <summary>
/// 真正的 Windows 系统通知。
///
/// 为什么不用托盘气泡：实测（2026-10-10）托盘气泡在这台机器上不会进"通知中心"，
/// 用户明确要求通知必须能在控制中心看到，所以改用官方的 Windows.UI.Notifications。
/// 用之前要先用 SetCurrentProcessExplicitAppUserModelID 给进程一个"通知身份"，
/// 否则通知发不出去。
/// </summary>
internal static class SystemToast
{
    public const string AppUserModelId = "G49BatteryMonitor";

    public static string LastError { get; private set; } = "";

    /// <summary>点击通知时要做什么（由主窗口设置成"把窗口叫出来"）。</summary>
    public static Action? OnActivated;

    // 必须保住引用，否则事件回调会被 GC 回收掉
    private static ToastNotification? _lastToast;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>进程启动时调用一次：给进程一个通知身份，通知才会归到这个程序名下。</summary>
    public static void RegisterForCurrentProcess()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch { /* 失败也不致命，后面 Show 会真实暴露问题 */ }
    }

    public static bool Show(string title, string message, AlertLevel level, Logger log)
    {
        try
        {
            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var texts = xml.GetElementsByTagName("text");
            texts[0].AppendChild(xml.CreateTextNode(title));
            texts[1].AppendChild(xml.CreateTextNode(message));

            var toast = new ToastNotification(xml) { Tag = "g49-battery", Group = "g49" };
            toast.Failed += (_, e) => log.Error($"系统通知被系统拒收，错误码 0x{e.ErrorCode:X8}");
            toast.Activated += (_, _) =>
            {
                try { OnActivated?.Invoke(); } catch { }
            };
            _lastToast = toast;

            ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(toast);
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + "：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// "通知中心"里属于本程序的通知条数。
    /// 这是客观证据：条数变多，就说明通知真的送达了，不用靠人眼看屏幕。
    /// </summary>
    public static int HistoryCount()
    {
        try { return ToastNotificationManager.History.GetHistory(AppUserModelId).Count; }
        catch { return -1; }
    }

    public static void ClearHistory()
    {
        try { ToastNotificationManager.History.Clear(AppUserModelId); } catch { }
    }
}

/// <summary>
/// 提醒分发：首选系统通知；万一系统通知发不出去，自动退回到自绘的右下角小卡片，
/// 保证不会出现"该提醒却没提醒"。
/// </summary>
internal sealed class Notifier : IDisposable
{
    private readonly Form _ui;
    private readonly AppConfig _cfg;
    private readonly Logger _log;
    private bool _toastBroken;
    private NoticeCard? _card;

    public Notifier(Form ui, AppConfig cfg, Logger log)
    {
        _ui = ui;
        _cfg = cfg;
        _log = log;
        SystemToast.OnActivated = () =>
        {
            try
            {
                if (_ui.IsDisposed) return;
                _ui.BeginInvoke(new Action(() =>
                {
                    _ui.Show();
                    _ui.WindowState = FormWindowState.Normal;
                    _ui.Activate();
                }));
            }
            catch { }
        };
    }

    /// <summary>可以从任意线程调用。</summary>
    public void Notify(string title, string text, AlertLevel level)
    {
        if (_ui.IsDisposed) return;
        try
        {
            if (_ui.InvokeRequired) _ui.BeginInvoke(new Action(() => ShowCore(title, text, level)));
            else ShowCore(title, text, level);
        }
        catch
        {
            // 提醒失败不能影响监控
        }
    }

    private void ShowCore(string title, string text, AlertLevel level)
    {
        PlaySound(level);

        if (_cfg.AlertStyle is "auto" or "toast" && !_toastBroken)
        {
            if (SystemToast.Show(title, text, level, _log)) return;
            _toastBroken = true;
            _log.Warn("系统通知发送失败，改用自绘提示卡片：" + SystemToast.LastError);
        }

        if (_cfg.AlertStyle == "toast") return; // 用户明确只要系统通知
        ShowCard(title, text, level);
    }

    private void PlaySound(AlertLevel level)
    {
        if (!_cfg.SoundEnabled) return;
        try
        {
            if (level == AlertLevel.Critical) SystemSounds.Hand.Play();
            else SystemSounds.Exclamation.Play();
        }
        catch { }
    }

    private void ShowCard(string title, string text, AlertLevel level)
    {
        try
        {
            _card?.Close();
            _card = new NoticeCard(title, text, level);
            _card.Show();
        }
        catch (Exception ex)
        {
            _log.Error("弹提示卡片失败：" + ex.Message);
        }
    }

    public void Dispose()
    {
        try { _card?.Close(); } catch { }
    }
}

/// <summary>自绘的右下角提示卡片（系统通知发不出去时的兜底）：不抢焦点、8 秒后自动消失。</summary>
internal sealed class NoticeCard : Form
{
    private readonly System.Windows.Forms.Timer _timer = new();

    public NoticeCard(string title, string text, AlertLevel level)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(30, 30, 34);
        Size = new Size(380, 108);

        Color accent = level switch
        {
            AlertLevel.Critical => Color.FromArgb(232, 84, 84),
            AlertLevel.Warning => Color.FromArgb(240, 168, 48),
            _ => Color.FromArgb(72, 160, 240),
        };

        var bar = new Panel { BackColor = accent, Dock = DockStyle.Left, Width = 5 };
        var lblTitle = new Label
        {
            Text = title,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            AutoSize = false,
            Bounds = new Rectangle(20, 16, 340, 26),
        };
        var lblText = new Label
        {
            Text = text,
            ForeColor = Color.FromArgb(205, 205, 212),
            Font = new Font("Microsoft YaHei UI", 9.5f),
            AutoSize = false,
            Bounds = new Rectangle(20, 46, 340, 48),
        };
        Controls.Add(bar);
        Controls.Add(lblTitle);
        Controls.Add(lblText);

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);

        _timer.Interval = 8000;
        _timer.Tick += (_, _) => { _timer.Stop(); Close(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE：不抢焦点
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW：不出现在 Alt+Tab
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
