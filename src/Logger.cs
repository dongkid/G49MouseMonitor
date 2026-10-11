using System.Text;

namespace G49BatteryMonitor;

/// <summary>
/// 极简滚动日志：写在 exe 同目录的 logs\ 下，按天分文件，单文件超过 1MB 就换一个。
/// 记录内容：每次采样、连接状态变化、预警、错误。
/// </summary>
internal sealed class Logger : IDisposable
{
    private readonly string _dir;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentFile = "";
    private long _currentBytes;      // 当前文件已写入的字节数（打开已有文件时按实际大小起算）
    private int _nextPart;           // 当天下一个要用的序号文件编号；0 = 本进程还没算过
    private const long MaxBytes = 1024 * 1024;

    public Logger(string exeDir)
    {
        _dir = Path.Combine(exeDir, "logs");
        try { Directory.CreateDirectory(_dir); } catch { }
    }

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (_lock)
        {
            try
            {
                if (_writer == null || _currentBytes >= MaxBytes) OpenNextFile();
                _writer?.WriteLine(line);
                _currentBytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            }
            catch
            {
                // 日志写不进去就算了，绝不因为日志把监控搞挂
            }
        }
    }

    /// <summary>
    /// 换一个日志文件并开始写。
    /// 当天的第一个文件是 g49-yyyyMMdd.log；写满之后往下排 g49-yyyyMMdd-001.log、-002.log……
    /// "写满"由本进程记的 _currentBytes 判断，不再拿日文件名去和滚动后的文件名比较 ——
    /// 原代码正是因为这个比较永远不成立，写满后每写一行都新开一个文件。
    /// </summary>
    private void OpenNextFile()
    {
        string baseName = $"g49-{DateTime.Now:yyyyMMdd}";
        string daily = Path.Combine(_dir, baseName + ".log");

        // 进程内第一次打开、且当天的日文件还没写满：接着写这个日文件（程序重启后也沿用同一个文件）
        bool useDaily = _currentFile.Length == 0 && !ExistsFullOrOver(daily);
        string full = useDaily ? daily : NextNumberedFile(baseName);

        _writer?.Dispose();
        _writer = new StreamWriter(full, append: true, Encoding.UTF8) { AutoFlush = true };
        _currentFile = full;
        _currentBytes = BytesOnDisk(full);
    }

    /// <summary>
    /// 在 g49-yyyyMMdd-001.log、-002.log…… 里挑第一个还不存在的，序号只增不减。
    /// 本进程内记住用到的编号，避免每滚一次都从 001 开始一个个试（那会变成二次方开销）。
    /// </summary>
    private string NextNumberedFile(string baseName)
    {
        // 序号只在进程内第一次滚动时扫一遍（跳过本次运行之前就存在的文件），之后一路递增，不必反复扫目录
        for (int i = 0; i < 100000; i++)
        {
            string candidate = Path.Combine(_dir, NumberedName(baseName, _nextPart + 1));
            if (!Exists(candidate)) { _nextPart++; return candidate; }
            _nextPart++;
        }
        return Path.Combine(_dir, baseName + ".log");   // 一百万个文件这种极端情况：退回日文件，不无限找下去
    }

    // 位数不设上限：超过 999 个文件时自然变成 -1000、-1001……，不会因为编号用尽而停止滚动
    private static string NumberedName(string baseName, int part) => $"{baseName}-{part:D3}.log";

    /// <summary>文件存在、且已经写满（含"存在但读不到长度"的保守情况）。</summary>
    private static bool ExistsFullOrOver(string file)
    {
        try { return File.Exists(file) && new FileInfo(file).Length >= MaxBytes; }
        catch { return true; }   // 读不到长度的异常情况：当作已满，换新文件，避免往不确定的文件里堆
    }

    private static bool Exists(string file)
    {
        try { return File.Exists(file); }
        catch { return false; }
    }

    private static long BytesOnDisk(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return 0; }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}