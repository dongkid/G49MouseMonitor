using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace G49BatteryMonitor;

/// <summary>一次电量查询的结果。</summary>
internal sealed record BatteryReading(int Percent, int MilliVolts, bool Charging, bool Ok, string Note)
{
    public static BatteryReading NoData(string note) => new(0, 0, false, false, note);
}

/// <summary>G49 私有协议：组包 / 解包。</summary>
internal static class G49Protocol
{
    public const byte ReportId = 0x08;
    public const byte Magic = 0x55;
    public const byte CmdBattery = 0x04;

    /// <summary>
    /// 组一个 17 字节报文：report id 0x08 + 16 字节载荷。
    ///
    /// 校验位的算法在真机上做过三路对照实测（2026-10-10）：
    ///   正确算法（含 report id）: 15 次查询 → 15 次真实数据
    ///   不含 report id          : 15 次查询 →  0 次（每次都被接收器回 subCmd=0x01 拒绝）
    ///   故意写错 0xFF           : 15 次查询 →  0 次
    /// 即：校验必须把 report id 0x08 一起算进去。参考项目的 WebHID/Python 实现也是这个算法。
    /// </summary>
    public static byte[] Build(byte cmdId, ushort addr = 0, byte[]? payload = null)
    {
        payload ??= Array.Empty<byte>();
        if (payload.Length > 10) throw new ArgumentException("载荷最多 10 字节");

        var frame = new byte[17];
        frame[0] = ReportId;
        frame[1] = cmdId;
        frame[2] = 0x00;                       // subCmd
        frame[3] = (byte)(addr >> 8);          // addr，高字节在前（与参考项目一致）
        frame[4] = (byte)(addr & 0xFF);
        frame[5] = (byte)payload.Length;
        Array.Copy(payload, 0, frame, 6, payload.Length);

        int sum = 0;
        for (int i = 0; i < 15; i++) sum += frame[i];
        frame[16] = (byte)((Magic - sum) & 0xFF);
        return frame;
    }

    /// <summary>
    /// 解析 17 字节响应（下标含开头的 report id）：
    ///   [0]=0x08  [1]=cmdId  [2]=subCmd(0=有数据, 非0=无数据)  [3..4]=addr
    ///   [5]=len   [6]=电量%  [7]=是否充电(非0)                 [8..9]=毫伏(大端)  [16]=校验
    /// subCmd 这里就是结果码：0x00 才是真实数据，0x01 表示"没有数据"。
    /// 只校验 checksum 是不够的 —— 接收器拒绝请求时也会回一条校验正确的包。
    /// </summary>
    public static BatteryReading Parse(byte[] frame)
    {
        if (frame.Length < 17) return BatteryReading.NoData("报文长度不足 17 字节");
        if (frame[0] != ReportId) return BatteryReading.NoData("report id 不是 0x08");
        if (frame[1] != CmdBattery) return BatteryReading.NoData($"cmdId 不是 0x04（实际 0x{frame[1]:X2}）");

        int sum = 0;
        for (int i = 0; i < 15; i++) sum += frame[i];
        byte expect = (byte)((Magic - sum) & 0xFF);
        if (frame[16] != expect) return BatteryReading.NoData($"校验不通过（收到 0x{frame[16]:X2}，应为 0x{expect:X2}）");

        byte result = frame[2];
        if (result != 0x00) return BatteryReading.NoData($"设备无数据（subCmd=0x{result:X2}）");

        int percent = frame[6];
        int mv = (frame[8] << 8) | frame[9];
        bool charging = frame[7] != 0;

        if (percent < 1 || percent > 100) return BatteryReading.NoData($"电量数值超出范围（{percent}）");
        if (mv < 2000 || mv > 5000) return BatteryReading.NoData($"电压数值超出范围（{mv}mV）");

        return new BatteryReading(percent, mv, charging, true, "");
    }
}

/// <summary>一个打开着的 G49 厂商接口句柄，用 OVERLAPPED + 事件实现带超时的读写。</summary>
internal sealed class G49Device : IDisposable
{
    public string Path { get; }
    public string LinkName { get; }
    public ushort ProductId { get; }

    private readonly SafeFileHandle _handle;
    private readonly IntPtr _event;
    private readonly IntPtr _overlapped;
    private readonly int _inputLength;
    private readonly int _outputLength;
    private bool _disposed;

    /// <summary>句柄是否还活着（设备被拔掉后会失效）。</summary>
    public bool Alive => !_disposed && !_handle.IsInvalid && !_handle.IsClosed;

    private G49Device(string path, string linkName, ushort pid, SafeFileHandle handle, int inputLength, int outputLength)
    {
        Path = path;
        LinkName = linkName;
        ProductId = pid;
        _handle = handle;
        _inputLength = inputLength;
        _outputLength = outputLength;
        _event = Native.CreateEvent(IntPtr.Zero, false, false, null);
        _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Native.OVERLAPPED>());
    }

    /// <summary>
    /// 按路径打开。必须用共享方式打开（FILE_SHARE_READ|WRITE）：
    /// 实测独占打开会被拒（Win32=32），共享打开则和网页版 WebHID 可以并存。
    /// </summary>
    public static G49Device? Open(string path, out int error, out string detail)
    {
        error = 0;
        detail = "";
        var h = Native.CreateFile(path,
            Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            h.Dispose();
            detail = $"打开失败 Win32={error}";
            return null;
        }

        int inputLen = 0, outputLen = 0;
        ushort pid = 0;
        if (Native.HidD_GetPreparsedData(h, out IntPtr pre) && pre != IntPtr.Zero)
        {
            var caps = new Native.HIDP_CAPS { Reserved = new ushort[17] };
            if (Native.HidP_GetCaps(pre, ref caps) == Native.HIDP_STATUS_SUCCESS)
            {
                inputLen = caps.InputReportByteLength;
                outputLen = caps.OutputReportByteLength;
            }
            Native.HidD_FreePreparsedData(pre);
        }
        if (inputLen <= 0) inputLen = 64;
        if (outputLen <= 0) outputLen = 17;

        var attr = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
        if (Native.HidD_GetAttributes(h, ref attr)) pid = attr.ProductID;

        try { Native.HidD_SetNumInputBuffers(h, 32); } catch { /* 非致命 */ }

        string link = pid switch
        {
            0xF55E => "有线",
            0xF55D or 0xF5D5 => "2.4G",
            _ => "USB",
        };
        return new G49Device(path, link, pid, h, inputLen, outputLen);
    }

    /// <summary>把队列里积压的旧报文读掉。内核输入队列会留下永不失效的陈货，必须先清空再查询。</summary>
    public int Drain(int perReadTimeoutMs = 15, int max = 6)
    {
        int n = 0;
        for (int i = 0; i < max; i++)
        {
            if (ReadRaw(perReadTimeoutMs) == null) break;
            n++;
        }
        return n;
    }

    /// <summary>发一次电量查询并等回包。返回 null 表示没收到任何回包。</summary>
    public BatteryReading? QueryBattery(int timeoutMs = 150)
    {
        Drain();
        var frame = G49Protocol.Build(G49Protocol.CmdBattery);
        if (!WriteRaw(frame, 500)) return null;

        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            int remain = (int)Math.Max(1, deadline - Environment.TickCount64);
            var resp = ReadRaw(remain);
            if (resp == null) break;
            if (resp.Length >= 2 && resp[1] == G49Protocol.CmdBattery)
                return G49Protocol.Parse(resp);
        }
        return null;
    }

    private bool WriteRaw(byte[] data, int timeoutMs)
    {
        if (!Alive) return false;
        IntPtr buf = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, buf, data.Length);
            Native.ResetEvent(_event);
            var ov = new Native.OVERLAPPED { hEvent = _event };
            Marshal.StructureToPtr(ov, _overlapped, false);

            if (!Native.WriteFile(_handle, buf, data.Length, out _, _overlapped))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != Native.ERROR_IO_PENDING) return false;
                if (Native.WaitForSingleObject(_event, (uint)timeoutMs) != Native.WAIT_OBJECT_0)
                {
                    Native.CancelIoEx(_handle, _overlapped);
                    Native.WaitForSingleObject(_event, 500);
                    return false;
                }
                if (!Native.GetOverlappedResult(_handle, _overlapped, out _, false)) return false;
            }
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private byte[]? ReadRaw(int timeoutMs)
    {
        if (!Alive) return null;
        IntPtr buf = Marshal.AllocHGlobal(_inputLength);
        try
        {
            Native.ResetEvent(_event);
            var ov = new Native.OVERLAPPED { hEvent = _event };
            Marshal.StructureToPtr(ov, _overlapped, false);

            if (!Native.ReadFile(_handle, buf, _inputLength, out int read, _overlapped))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != Native.ERROR_IO_PENDING) return null;
                if (Native.WaitForSingleObject(_event, (uint)timeoutMs) != Native.WAIT_OBJECT_0)
                {
                    Native.CancelIoEx(_handle, _overlapped);
                    Native.WaitForSingleObject(_event, 500);
                    return null;
                }
                if (!Native.GetOverlappedResult(_handle, _overlapped, out read, false)) return null;
            }
            if (read <= 0) return null;
            int n = Math.Min(read, _inputLength);
            var result = new byte[n];
            Marshal.Copy(buf, result, 0, n);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _handle.Dispose(); } catch { }
        try { if (_event != IntPtr.Zero) Native.CloseHandle(_event); } catch { }
        try { if (_overlapped != IntPtr.Zero) Marshal.FreeHGlobal(_overlapped); } catch { }
    }
}

/// <summary>扫描 / 选择通信接口。</summary>
internal static class G49Locator
{
    /// <summary>G49 在 USB 上的三种身份：2.4G 接收器、备用接收器、有线。</summary>
    private static readonly (ushort Pid, string Name, int Priority)[] KnownPids =
    {
        (0xF55E, "有线 USB-C", 0),          // 有线优先
        (0xF55D, "2.4G 接收器", 1),
        (0xF5D5, "2.4G 备用接收器", 2),
    };

    internal sealed record Candidate(string Path, ushort Pid, string Name, int Priority);

    /// <summary>
    /// 枚举 VID=0x3554 且 PID 属于 G49 的全部 HID 接口。
    ///
    /// 注意：不要按 usagePage 挑接口。实测这块鼠标的 6 个接口里，可读写 17 字节报文的是
    /// usagePage=0xFF02 的那个；usagePage=0xFF04 的那个只有 feature 报文，往里写会直接失败。
    /// 可靠的做法是"能不能写进去"——见 ProbeAndRank。
    /// </summary>
    public static List<Candidate> Enumerate()
    {
        var found = new List<Candidate>();
        Guid guid = Native.GUID_DEVINTERFACE_HID;
        IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return found;

        try
        {
            var probe = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; ; i++)
            {
                var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = probe.cbSize };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                if (required <= 0) continue;

                IntPtr buf = Marshal.AllocHGlobal(required);
                try
                {
                    // cbSize 在 64 位下是 8、32 位下是 6；字符串紧跟其后从偏移 4 开始
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, buf, required, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(IntPtr.Add(buf, 4)) ?? "";
                    if (path.Length == 0) continue;
                    if (path.IndexOf("vid_3554", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    foreach (var known in KnownPids)
                    {
                        string tag = "pid_" + known.Pid.ToString("x4");
                        if (path.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            found.Add(new Candidate(path, known.Pid, known.Name, known.Priority));
                            break;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }

        return found.OrderBy(c => c.Priority).ToList();
    }

    /// <summary>
    /// 按优先级（有线 → 2.4G → 备用接收器）依次尝试打开，用"发一条真实的电量查询看有没有回包"
    /// 来筛掉那些看着像、其实不能通信的接口。
    /// 优先返回真正拿到数据的接口；如果都拿不到数据（老鼠睡着了），就返回第一个至少能响应的接口，
    /// 这样界面上还能显示"接收器插着，但鼠标没数据"。
    /// </summary>
    public static G49Device? OpenFirstWritable(out string trace, out BatteryReading? firstReading)
    {
        var sb = new StringBuilder();
        firstReading = null;
        var candidates = Enumerate();
        sb.Append($"发现 {candidates.Count} 个 G49 相关接口");

        G49Device? fallback = null;
        BatteryReading? fallbackReading = null;

        foreach (var c in candidates)
        {
            var device = G49Device.Open(c.Path, out int err, out string detail);
            if (device == null)
            {
                sb.Append($"\n  {c.Name}：{detail}");
                continue;
            }

            var reading = device.QueryBattery(200);
            if (reading == null)
            {
                sb.Append($"\n  {c.Name}：写不进去或没有回包，跳过");
                device.Dispose();
                continue;
            }

            if (reading.Ok)
            {
                sb.Append($"\n  {c.Name}：可通信，读数 {reading.Percent}%");
                firstReading = reading;
                trace = sb.ToString();
                fallback?.Dispose();
                return device;
            }

            sb.Append($"\n  {c.Name}：有响应但没有数据（{reading.Note}）");
            if (fallback == null)
            {
                fallback = device;
                fallbackReading = reading;
            }
            else
            {
                device.Dispose();
            }
        }

        trace = sb.ToString();
        if (fallback != null)
        {
            firstReading = fallbackReading;
            return fallback;
        }
        return null;
    }
}
