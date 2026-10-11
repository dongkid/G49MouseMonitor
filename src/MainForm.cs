using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace G49BatteryMonitor;

/// <summary>主窗口。点右上角 × 只是把窗口藏起来，后台继续监控。</summary>
internal sealed class MainForm : Form
{
    private readonly AppConfig _cfg;
    private readonly Logger _log;
    private readonly Notifier _notifier;
    private readonly MonitorService _monitor;

    private readonly BatteryGauge _gauge = new();
    private readonly Label _lblPercent = new();
    private readonly Label _lblVolt = new();
    private readonly Label _lblMode = new();
    private readonly Label _lblUpdated = new();
    private readonly Label _lblLast = new();
    private readonly CheckBox _chkAutoStart = new();
    private readonly ComboBox _cboInterval = new();
    private readonly NumericUpDown _numNotice = new();
    private readonly NumericUpDown _numLow = new();
    private readonly NumericUpDown _numCritical = new();
    private readonly CheckBox _chkSound = new();
    private readonly Button _btnRefresh = new();
    private readonly Button _btnExit = new();
    private readonly Label _lblHint = new();

    private bool _allowVisible;
    private bool _dpiAdjusted;
    private DateTime _lastDeviceEventLog = DateTime.MinValue;
    private bool _reallyExiting;
    private bool _hideHintShown;
    private bool _syncingUi;
    private IntPtr _deviceNotification = IntPtr.Zero;
    private IntPtr _deviceFilter = IntPtr.Zero;

    private static readonly (string Text, int Seconds)[] Intervals =
    {
        ("30 秒", 30),
        ("1 分钟", 60),
        ("5 分钟", 300),
        ("15 分钟", 900),
    };

    public MainForm(AppConfig cfg, Logger log, bool startHidden)
    {
        _cfg = cfg;
        _log = log;
        _notifier = new Notifier(this, cfg, log);
        _monitor = new MonitorService(cfg, log, _notifier);

        Text = "G49 鼠标电量监控";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        // 缩放必须按这个顺序声明：先说"我是在 96 DPI 下设计的"，再开 Dpi 缩放，
        // 最后才设置字号和尺寸。顺序反了会被反向缩掉（150% 缩放下窗口会变成 0.66 倍）。
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9f);
        ClientSize = new Size(404, 366);

        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _allowVisible = !startHidden;
        BuildUi();
        LoadSettingsIntoUi();

        _monitor.Updated += OnMonitorUpdated;
        _monitor.Start();
    }

    private void BuildUi()
    {
        _gauge.Bounds = new Rectangle(16, 18, 92, 46);

        _lblPercent.Bounds = new Rectangle(122, 8, 250, 46);
        _lblPercent.Font = new Font("Microsoft YaHei UI", 26f, FontStyle.Bold);
        _lblPercent.ForeColor = Color.FromArgb(32, 32, 36);
        _lblPercent.Text = "--";
        _lblPercent.TextAlign = ContentAlignment.MiddleLeft;

        _lblVolt.Bounds = new Rectangle(126, 56, 250, 22);
        _lblVolt.Font = new Font("Microsoft YaHei UI", 10.5f);
        _lblVolt.ForeColor = Color.FromArgb(90, 90, 100);
        _lblVolt.Text = "--";

        _lblMode.Bounds = new Rectangle(16, 76, 370, 22);
        _lblMode.ForeColor = Color.FromArgb(60, 60, 70);
        _lblMode.Text = "正在检测…";

        _lblUpdated.Bounds = new Rectangle(16, 98, 200, 20);
        _lblUpdated.ForeColor = Color.FromArgb(140, 140, 150);
        _lblUpdated.Text = "";

        _lblLast.Bounds = new Rectangle(16, 118, 372, 20);
        _lblLast.ForeColor = Color.FromArgb(150, 150, 160);
        _lblLast.Text = "";

        var sep = new Label
        {
            Bounds = new Rectangle(16, 144, 372, 1),
            BackColor = Color.FromArgb(220, 220, 226),
            Text = "",
        };

        _chkAutoStart.Text = "开机自动启动";
        _chkAutoStart.Bounds = new Rectangle(16, 156, 200, 24);
        _chkAutoStart.CheckedChanged += OnAutoStartChanged;

        var lblInterval = new Label
        {
            Text = "查询间隔",
            Bounds = new Rectangle(16, 190, 80, 24),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _cboInterval.Bounds = new Rectangle(100, 188, 120, 24);
        _cboInterval.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var (text, _) in Intervals) _cboInterval.Items.Add(text);
        _cboInterval.SelectedIndexChanged += OnIntervalChanged;

        var lblWarn = new Label
        {
            Text = "预警阈值 ≤",
            Bounds = new Rectangle(16, 224, 84, 24),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        foreach (var num in new[] { _numNotice, _numLow, _numCritical })
        {
            num.Minimum = 1;
            num.Maximum = 100;
            num.Width = 48;
            num.Height = 24;
            num.TextAlign = HorizontalAlignment.Center;
            num.ValueChanged += OnThresholdChanged;
        }
        _numNotice.Location = new Point(100, 222);
        var lblSlash1 = new Label { Text = "% /", Bounds = new Rectangle(150, 224, 30, 24), TextAlign = ContentAlignment.MiddleLeft };
        _numLow.Location = new Point(180, 222);
        var lblSlash2 = new Label { Text = "% /", Bounds = new Rectangle(230, 224, 30, 24), TextAlign = ContentAlignment.MiddleLeft };
        _numCritical.Location = new Point(260, 222);
        var lblPercentSign = new Label { Text = "%", Bounds = new Rectangle(310, 224, 20, 24), TextAlign = ContentAlignment.MiddleLeft };

        _chkSound.Text = "低电量提示音";
        _chkSound.Bounds = new Rectangle(16, 256, 200, 24);
        _chkSound.CheckedChanged += (_, _) =>
        {
            SaveSettings();
            if (!_syncingUi) _log.Info($"设置变更：提示音 {(_chkSound.Checked ? "开" : "关")}");
        };

        _btnRefresh.Text = "立即刷新";
        _btnRefresh.Bounds = new Rectangle(158, 292, 100, 32);
        _btnRefresh.Click += (_, _) => _monitor.RequestImmediateRefresh();

        _btnExit.Text = "退出";
        _btnExit.Bounds = new Rectangle(272, 292, 100, 32);
        _btnExit.Click += OnExitClicked;

        _lblHint.Bounds = new Rectangle(16, 332, 372, 22);
        _lblHint.ForeColor = Color.FromArgb(150, 150, 160);
        _lblHint.Text = "";

        Controls.AddRange(new Control[]
        {
            _gauge, _lblPercent, _lblVolt, _lblMode, _lblUpdated, _lblLast, sep,
            _chkAutoStart, lblInterval, _cboInterval, lblWarn,
            _numNotice, lblSlash1, _numLow, lblSlash2, _numCritical, lblPercentSign,
            _chkSound, _btnRefresh, _btnExit, _lblHint,
        });
    }

    private void LoadSettingsIntoUi()
    {
        _syncingUi = true;
        try
        {
            _chkAutoStart.Checked = AutoStart.IsEnabled();
            int index = Array.FindIndex(Intervals, i => i.Seconds == _cfg.PollSecondsNormal);
            if (index < 0)
            {
                _cboInterval.Items.Add($"{_cfg.PollSecondsNormal} 秒");
                index = _cboInterval.Items.Count - 1;
            }
            _cboInterval.SelectedIndex = index;
            _numNotice.Value = _cfg.WarnNotice;
            _numLow.Value = _cfg.WarnLow;
            _numCritical.Value = _cfg.WarnCritical;
            _chkSound.Checked = _cfg.SoundEnabled;
        }
        finally
        {
            _syncingUi = false;
        }
    }

    private void OnAutoStartChanged(object? sender, EventArgs e)
    {
        if (_syncingUi) return;
        if (_chkAutoStart.Checked)
        {
            if (!AutoStart.Enable(out string error))
            {
                _lblHint.Text = "设置开机自启失败：" + error;
                _syncingUi = true;
                _chkAutoStart.Checked = false;
                _syncingUi = false;
                return;
            }
            _lblHint.Text = "已设置为开机自动启动";
            _log.Info("已开启开机自启");
        }
        else
        {
            if (!AutoStart.Disable(out string error))
            {
                _lblHint.Text = "取消开机自启失败：" + error;
                _syncingUi = true;
                _chkAutoStart.Checked = true;
                _syncingUi = false;
                return;
            }
            _lblHint.Text = "已取消开机自动启动";
            _log.Info("已关闭开机自启");
        }
    }

    private void OnIntervalChanged(object? sender, EventArgs e)
    {
        if (_syncingUi) return;
        int index = _cboInterval.SelectedIndex;
        if (index >= 0 && index < Intervals.Length)
        {
            _cfg.PollSecondsNormal = Intervals[index].Seconds;
            _cfg.PollSecondsWired = Math.Min(60, Intervals[index].Seconds);
            SaveSettings();
            _log.Info($"设置变更：查询间隔改为 {_cfg.PollSecondsNormal} 秒（有线时 {_cfg.PollSecondsWired} 秒）");
            _monitor.RequestImmediateRefresh();
        }
    }

    private void OnThresholdChanged(object? sender, EventArgs e)
    {
        if (_syncingUi) return;
        if (_numCritical.Value > _numLow.Value) _numLow.Value = _numCritical.Value;
        if (_numLow.Value > _numNotice.Value) _numNotice.Value = _numLow.Value;
        _cfg.WarnNotice = (int)_numNotice.Value;
        _cfg.WarnLow = (int)_numLow.Value;
        _cfg.WarnCritical = (int)_numCritical.Value;
        SaveSettings();
        _log.Info($"设置变更：预警阈值改为 {_cfg.WarnNotice}% / {_cfg.WarnLow}% / {_cfg.WarnCritical}%");
    }

    private void SaveSettings()
    {
        if (_syncingUi) return;
        _cfg.SoundEnabled = _chkSound.Checked;
        _cfg.Save();
    }

    private void OnMonitorUpdated(MonitorSnapshot s)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(new Action(() => ApplySnapshot(s)));
            else ApplySnapshot(s);
        }
        catch { }
    }

    private void ApplySnapshot(MonitorSnapshot s)
    {
        if (IsDisposed) return;

        if (s.Connected && s.Reading != null)
        {
            var r = s.Reading;
            _gauge.SetState(r.Percent, r.Charging, true);
            _lblPercent.Text = r.Percent + "%";
            _lblPercent.ForeColor = r.Percent <= _cfg.WarnCritical
                ? Color.FromArgb(210, 60, 60)
                : r.Percent <= _cfg.WarnNotice ? Color.FromArgb(200, 130, 20) : Color.FromArgb(32, 32, 36);
            _lblVolt.Text = $"{r.MilliVolts / 1000.0:F2}V · {(r.Charging ? "充电中" : "未充电")}";
            _lblMode.Text = $"已连接 · {s.LinkName}";
            _lblMode.ForeColor = Color.FromArgb(50, 120, 60);
            _lblLast.Text = "";
            _lblHint.Text = "";
        }
        else
        {
            _gauge.SetState(0, false, false);
            _lblPercent.Text = "--";
            _lblPercent.ForeColor = Color.FromArgb(150, 150, 160);
            _lblVolt.Text = "--";
            _lblMode.Text = "未连接";
            _lblMode.ForeColor = Color.FromArgb(160, 60, 60);
            _lblHint.Text = s.Message;
            _lblLast.Text = s.LastGood != null && s.LastGoodAt != null
                ? $"最后读数 {s.LastGood.Percent}%（{s.LastGoodAt:HH:mm}）"
                : "";
        }

        _lblUpdated.Text = "最后更新 " + s.At.ToString("HH:mm:ss");
    }

    public void RequestShow()
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _allowVisible = true;
                Show();
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
            }));
        }
        catch { }
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!_allowVisible)
        {
            // 开机自启时静默进后台：窗口对象建好、消息循环跑起来，但不显示
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // 高分屏适配。
        // 说明：WinForms 的自动缩放（AutoScaleMode）在这套手写布局上不生效 —— 实测在 150% 缩放下
        // DeviceDpi=144，但 AutoScaleDimensions 也被改成了 144，比例算下来是 1.0，等于没缩放；
        // 而字号按 pt 计算，在 144 DPI 下本来就会画大 1.5 倍，于是字把布局撑爆、互相重叠。
        // 所以这里直接按 实际DPI/96 手动把整窗和所有子控件等比放大一次。
        if (!_dpiAdjusted)
        {
            _dpiAdjusted = true;
            float factor = DeviceDpi / 96f;
            if (Math.Abs(factor - 1f) > 0.02f)
            {
                Scale(new SizeF(factor, factor));
                _log.Info($"界面按 {DeviceDpi} DPI 缩放 ×{factor:F2}，缩放后 ClientSize={ClientSize.Width}x{ClientSize.Height}");
            }
        }

        _log.Info($"窗口创建：DeviceDpi={DeviceDpi} ClientSize={ClientSize.Width}x{ClientSize.Height}");

        // 注册设备插拔通知，插拔接收器/有线时能立刻反应，而不是等下一个查询周期
        try
        {
            _deviceFilter = Native.BuildDeviceInterfaceFilter();
            _deviceNotification = Native.RegisterDeviceNotification(Handle, _deviceFilter, Native.DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DEVICECHANGE)
        {
            int evt = m.WParam.ToInt32();
            if (evt == Native.DBT_DEVICEARRIVAL || evt == Native.DBT_DEVICEREMOVECOMPLETE)
            {
                // 一次插拔系统会连发好几条消息，日志里只记一条，别刷屏
                if ((DateTime.Now - _lastDeviceEventLog).TotalSeconds >= 2)
                {
                    _lastDeviceEventLog = DateTime.Now;
                    _log.Info(evt == Native.DBT_DEVICEARRIVAL ? "检测到设备接入，立即重新选择接口" : "检测到设备移除，立即重新选择接口");
                }
                // 关键：必须强制重扫，不能只是"再查一次"。插线时接收器可能还在正常应答，
                // 不重扫就会一直用着 2.4G，看不见新插上的有线接口。
                _monitor.RequestRescan();
            }
        }
        else if (m.Msg == Native.WM_POWERBROADCAST)
        {
            // 电脑从睡眠中醒来，设备状态可能已经变了
            _monitor.RequestImmediateRefresh();
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 点右上角 × 时不要退出，只把窗口藏起来。
        // 注意：真点 × 走的是 WM_SYSCOMMAND/SC_CLOSE（原因=UserClosing），
        // 而外部直接发 WM_CLOSE 时原因会是 None，两种都要拦住，否则会"静默退出"。
        if (!_reallyExiting && (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None))
        {
            _log.Info($"窗口关闭请求（原因={e.CloseReason}）→ 藏到后台继续监控");
            e.Cancel = true;
            HideToBackground();
            return;
        }
        _log.Info($"窗口真正关闭（原因={e.CloseReason}）");
        base.OnFormClosing(e);
    }

    private void HideToBackground()
    {
        Hide();
        if (_cfg.ShowHideHint && !_hideHintShown)
        {
            _hideHintShown = true;
            _notifier.Notify("仍在后台监控", "窗口已隐藏，程序继续在后台监控电量；托盘不会留下图标。再双击程序即可重新打开。", AlertLevel.Info);
        }
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        var answer = MessageBox.Show(this,
            "退出后将停止电量监控，直到下次开机或你再次运行程序。\n\n确定要退出吗？",
            "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        _reallyExiting = true;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { _monitor.Dispose(); } catch { }
        try { _notifier.Dispose(); } catch { }
        try
        {
            if (_deviceNotification != IntPtr.Zero) Native.UnregisterDeviceNotification(_deviceNotification);
        }
        catch { }
        try { if (_deviceFilter != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(_deviceFilter); } catch { }
        base.OnFormClosed(e);
    }
}

/// <summary>电池图形。按电量填充，颜色随电量变化；未连接时画成灰色空电池。</summary>
internal sealed class BatteryGauge : Control
{
    private int _percent;
    private bool _charging;
    private bool _connected;

    public BatteryGauge()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        // 普通控件不支持透明背景色，直接用窗口的默认底色
        BackColor = SystemColors.Control;
    }

    public void SetState(int percent, bool charging, bool connected)
    {
        if (_percent == percent && _charging == charging && _connected == connected) return;
        _percent = percent;
        _charging = charging;
        _connected = connected;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int nubWidth = 6;
        int nubHeight = Height / 3;
        var body = new Rectangle(0, 0, Width - nubWidth - 3, Height);

        using (var path = RoundedRect(body, 6))
        using (var back = new SolidBrush(Color.FromArgb(238, 238, 242)))
        using (var border = new Pen(_connected ? Color.FromArgb(120, 125, 135) : Color.FromArgb(180, 180, 188), 2f))
        {
            g.FillPath(back, path);
            g.DrawPath(border, path);
        }

        using (var nub = new SolidBrush(_connected ? Color.FromArgb(120, 125, 135) : Color.FromArgb(180, 180, 188)))
        {
            g.FillRectangle(nub, Width - nubWidth, (Height - nubHeight) / 2, nubWidth, nubHeight);
        }

        if (_connected && _percent > 0)
        {
            var fillArea = new Rectangle(body.X + 3, body.Y + 3, body.Width - 6, body.Height - 6);
            int fillWidth = (int)Math.Round(fillArea.Width * Math.Min(100, _percent) / 100.0);
            if (fillWidth > 0)
            {
                var fillRect = new Rectangle(fillArea.X, fillArea.Y, fillWidth, fillArea.Height);
                Color color = _charging
                    ? Color.FromArgb(60, 150, 230)
                    : _percent <= 10 ? Color.FromArgb(220, 60, 60)
                    : _percent <= 20 ? Color.FromArgb(235, 150, 40)
                    : Color.FromArgb(70, 185, 100);
                using var fillPath = RoundedRect(fillRect, 4);
                using var brush = new SolidBrush(color);
                g.FillPath(brush, fillPath);
            }
        }

        if (_charging && _connected)
        {
            using var pen = new Pen(Color.White, 2.5f);
            int cx = body.X + body.Width / 2;
            int cy = body.Y + body.Height / 2;
            g.DrawLine(pen, cx + 1, cy - 9, cx - 4, cy + 1);
            g.DrawLine(pen, cx - 4, cy + 1, cx + 2, cy + 1);
            g.DrawLine(pen, cx + 2, cy + 1, cx - 3, cy + 11);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
