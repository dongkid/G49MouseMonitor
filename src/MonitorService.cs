namespace G49BatteryMonitor;

internal sealed record MonitorSnapshot(
    bool Connected,
    BatteryReading? Reading,
    string LinkName,
    BatteryReading? LastGood,
    DateTime? LastGoodAt,
    string Message,
    DateTime At);

/// <summary>
/// 后台监控主体：定期向鼠标查询电量、判断是否需要预警、在设备插拔时立刻重查。
/// 跑在独立线程上，界面通过 Updated 事件拿快照。
/// </summary>
internal sealed class MonitorService : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly Logger _log;
    private readonly Notifier _notifier;
    private readonly ManualResetEventSlim _wake = new(false);

    private Thread? _thread;
    private volatile bool _stopping;
    private G49Device? _device;
    private BatteryReading? _lastGood;
    private DateTime? _lastGoodAt;

    /// <summary>0=未提醒；1=已提醒第一档；2=已提醒第二档；3=已提醒第三档。充电后归零。</summary>
    private int _stage;
    private bool _fullNotified;
    private volatile bool _forceRescan;

    public event Action<MonitorSnapshot>? Updated;
    public MonitorSnapshot Current { get; private set; }

    public MonitorService(AppConfig cfg, Logger log, Notifier notifier)
    {
        _cfg = cfg;
        _log = log;
        _notifier = notifier;
        Current = new MonitorSnapshot(false, null, "", null, null, "正在启动…", DateTime.Now);
    }

    public void Start()
    {
        _stopping = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "G49-Monitor" };
        _thread.Start();
    }

    /// <summary>让轮询立刻跑一轮（用户点"立即刷新"、电脑唤醒时用）。</summary>
    public void RequestImmediateRefresh() => _wake.Set();

    /// <summary>
    /// 让轮询立刻跑一轮，并且先丢掉当前句柄、从头重新挑接口。
    /// 设备插拔时必须用这个：否则"插上线"时接收器可能还在正常应答，
    /// 程序就会一直用着 2.4G，根本不会去看新出现的那个有线接口。
    /// </summary>
    public void RequestRescan()
    {
        _forceRescan = true;
        _wake.Set();
    }

    private void Loop()
    {
        while (!_stopping)
        {
            MonitorSnapshot snapshot;
            try
            {
                snapshot = Poll();
            }
            catch (Exception ex)
            {
                _log.Error("轮询出错：" + ex);
                snapshot = new MonitorSnapshot(false, null, "", _lastGood, _lastGoodAt, "轮询出错：" + ex.Message, DateTime.Now);
            }

            Current = snapshot;
            try { Updated?.Invoke(snapshot); } catch { }
            LogSample(snapshot);
            EvaluateAlert(snapshot);

            int seconds;
            if (snapshot.Connected)
            {
                bool wired = _device?.ProductId == 0xF55E;
                seconds = wired ? _cfg.PollSecondsWired : _cfg.PollSecondsNormal;
            }
            else
            {
                // 没连上时快速重试，这样插线/拔线/鼠标唤醒能马上就反映出来，而不是等 5 分钟
                seconds = _cfg.PollSecondsDisconnected;
            }
            seconds = Math.Clamp(seconds, 5, 3600);
            _wake.Wait(TimeSpan.FromSeconds(seconds));
            _wake.Reset();
        }
    }

    private MonitorSnapshot Poll()
    {
        // 一个周期内最多尝试两次。第一次如果发现句柄已经成了僵尸（比如线被拔了），
        // 立刻就地重新扫描再试一次，绝不留到下一个周期。
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (_forceRescan)
            {
                _forceRescan = false;
                _device?.Dispose();
                _device = null;
            }

            if (_device == null || !_device.Alive)
            {
                _device?.Dispose();
                _device = null;

                _device = G49Locator.OpenFirstWritable(out string trace, out BatteryReading? first);
                if (_device == null)
                {
                    _log.Info("未找到可用设备。" + trace.Replace("\n", " | "));
                    return NotConnected(_lastGood == null
                        ? "未检测到 G49 鼠标（接收器可能没插）"
                        : "未检测到 G49 鼠标");
                }

                _log.Info("已连接 " + _device.LinkName + "（PID 0x" + _device.ProductId.ToString("X4") + "）");
                if (first is { Ok: true })
                {
                    _lastGood = first;
                    _lastGoodAt = DateTime.Now;
                    return new MonitorSnapshot(true, first, _device.LinkName, _lastGood, _lastGoodAt, "", DateTime.Now);
                }
                return NotConnected(first?.Note ?? "设备没有返回数据");
            }

            var reading = _device.QueryBattery(200);

            if (reading == null)
            {
                _log.Warn("查询没有回包（设备可能已被拔掉），本周期内立刻重新扫描");
                _device.Dispose();
                _device = null;
                continue;
            }

            // 有线接口还在、却读不到数据 —— 基本可以断定线已经拔了，句柄成了僵尸
            if (!reading.Ok && _device.ProductId == 0xF55E)
            {
                _log.Info("有线接口已无数据，判定为线已拔，本周期内立刻重新扫描");
                _device.Dispose();
                _device = null;
                continue;
            }

            if (!reading.Ok) return NotConnected(reading.Note);

            _lastGood = reading;
            _lastGoodAt = DateTime.Now;
            return new MonitorSnapshot(true, reading, _device.LinkName, _lastGood, _lastGoodAt, "", DateTime.Now);
        }

        return NotConnected("设备没有响应");
    }

    private MonitorSnapshot NotConnected(string message)
        => new(false, null, "", _lastGood, _lastGoodAt, message, DateTime.Now);

    private DateTime _lastSampleLoggedAt = DateTime.MinValue;
    private void LogSample(MonitorSnapshot s)
    {
        // 只压掉 10 秒内的重复采样（用户连点"立即刷新"时别刷屏）。
        // 不能按分钟去重 —— 那样会把间隔小于 1 分钟的正常轮询悄悄吃掉，
        // 事后看日志会误以为"漏采样了"。
        if ((s.At - _lastSampleLoggedAt).TotalSeconds < 10) return;
        _lastSampleLoggedAt = s.At;

        if (s.Connected && s.Reading != null)
            _log.Info($"采样：{s.Reading.Percent}%  {s.Reading.MilliVolts}mV  充电={(s.Reading.Charging ? "是" : "否")}  链路={s.LinkName}");
        else
            _log.Info($"未连接：{s.Message}");
    }

    private void EvaluateAlert(MonitorSnapshot s)
    {
        if (!s.Connected || s.Reading == null) return;
        var r = s.Reading;

        if (r.Charging)
        {
            _stage = 0;
            if (r.Percent >= 99)
            {
                if (!_fullNotified)
                {
                    _fullNotified = true;
                    _log.Info("提醒：已充满");
                    _notifier.Notify("鼠标已充满", $"当前 {r.Percent}%，可以拔线了。", AlertLevel.Info);
                }
            }
            else
            {
                _fullNotified = false;
            }
            return;
        }

        _fullNotified = false;

        // 电量明显回升（换了电池 / 补过电）后，把提醒阶梯重置
        if (_stage > 0 && r.Percent > _cfg.WarnNotice + 5) _stage = 0;

        if (r.Percent <= _cfg.WarnCritical && _stage < 3)
        {
            _stage = 3;
            _log.Warn($"预警：电量严重不足 {r.Percent}%");
            _notifier.Notify("鼠标电量严重不足",
                $"仅剩 {r.Percent}%（{r.MilliVolts / 1000.0:F2}V），请尽快充电。", AlertLevel.Critical);
        }
        else if (r.Percent <= _cfg.WarnLow && _stage < 2)
        {
            _stage = 2;
            _log.Warn($"预警：电量偏低 {r.Percent}%");
            _notifier.Notify("鼠标电量偏低",
                $"剩余 {r.Percent}%（{r.MilliVolts / 1000.0:F2}V），建议尽快充电。", AlertLevel.Warning);
        }
        else if (r.Percent <= _cfg.WarnNotice && _stage < 1)
        {
            _stage = 1;
            _log.Info($"提醒：电量 {r.Percent}%");
            _notifier.Notify("鼠标电量提醒",
                $"剩余 {r.Percent}%（{r.MilliVolts / 1000.0:F2}V）。", AlertLevel.Info);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        try { _thread?.Join(1500); } catch { }
        _device?.Dispose();
        _device = null;
        _wake.Dispose();
    }
}
