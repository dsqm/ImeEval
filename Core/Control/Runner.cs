using System.Diagnostics;
using System.Text.Json;
using ImeBench.Win32;

namespace ImeBench.Core;

public enum RunEndReason
{
    Completed,
    StoppedByUser,
    LostFocus,
    SendFailed,
    EmptyTable,
}

public sealed class RunOptions
{
    /// <summary>按键间隔（毫秒）。由标定器得出，用户可覆盖。</summary>
    public int IntervalMs { get; set; } = 2;

    /// <summary>文本连续多少毫秒不变即认为本次上屏完成。</summary>
    public int SettleMs { get; set; } = 40;

    /// <summary>单单元最长等待。</summary>
    public int SettleMaxMs { get; set; } = 1500;

    /// <summary>每多少个单元落一次检查点。</summary>
    public int CheckpointEvery { get; set; } = 20;

    /// <summary>检查点文件路径。为空则不落盘。</summary>
    public string? CheckpointPath { get; set; }

    /// <summary>每个单元跑完清空宿主，保证错误不跨单元传播。</summary>
    public bool ClearEachUnit { get; set; } = true;
}

public sealed class RunProgress
{
    public int Index { get; set; }
    public int Total { get; set; }
    public string CurrentKeys { get; set; } = "";
    public string LastActual { get; set; } = "";
}

public sealed class RunResult
{
    public RunEndReason Reason { get; set; }
    public BenchStats Stats { get; } = new();
    public List<UnitResult> Units { get; } = new();

    /// <summary>下一个待跑单元序号（断点位置）。</summary>
    public int ResumeIndex { get; set; }

    public string ReasonText => Reason switch
    {
        RunEndReason.Completed => "全部完成",
        RunEndReason.StoppedByUser => "用户停止（热键/按钮）",
        RunEndReason.LostFocus => "主窗口失去焦点，已停止",
        RunEndReason.SendFailed => "SendInput 失败",
        _ => "按键表为空",
    };
}

/// <summary>断点文件。恢复时从 NextIndex 对应单元的首键重打（句首重入）。</summary>
public sealed class Checkpoint
{
    public int NextIndex { get; set; }
    public BenchStats Stats { get; set; } = new();
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public string KeyTableJson { get; set; } = "";

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Checkpoint? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }
}

/// <summary>
/// 编排层（数据流的组织者，不含任何一层的核心逻辑）。
///
/// 它把四层串起来，自己不做具体活：
///   控制层 RunController  —— 每键问一次"还能发吗"
///   发送层 KeyPlayer      —— 按节奏把按键序列打出去
///   验证层 Verifier       —— 捕获上屏文本并比对出 S/D/I
///    Corpus 层 KeyTable   —— 提供上屏单元与期望文本
///
/// 线程模型：发送循环跑在后台线程（Task.Run）用 Stopwatch 精确定时，
/// UI 线程保持泵消息，这样 IME 才能消化按键、界面也才能响应停止按钮与热键。
/// </summary>
public sealed class Runner
{
    private readonly Verifier _verify;
    private readonly KeyPlayer _player;
    private readonly RunController _ctrl;

    public Runner(IUpTextSink host, InputSender sender, RunController ctrl)
    {
        _verify = new Verifier(host);
        _player = new KeyPlayer(sender);
        _ctrl = ctrl;
    }

    /// <summary>每完成一个上屏单元触发（后台线程，UI 需自行封送）。</summary>
    public event Action<UnitResult>? UnitDone;

    /// <summary>开始某单元时触发（后台线程）。</summary>
    public event Action<RunProgress>? Progress;

    /// <summary>已累计的统计，供 UI 实时读取。</summary>
    public BenchStats Live { get; } = new();

    public void RequestStop() => _ctrl.RequestStop(StopSource.StopButton);

    public bool StopRequested => _ctrl.StopRequested;

    public async Task<RunResult> RunAsync(KeyTable table, RunOptions opt,
                                          int startIndex = 0, BenchStats? seed = null)
    {
        var result = new RunResult();

        if (table.Units.Count == 0)
        {
            result.Reason = RunEndReason.EmptyTable;
            return result;
        }

        if (seed != null) result.Stats.CopyFrom(seed);

        _ctrl.Reset();
        _player.ResetErrors();
        await Task.Run(() => RunLoop(table, opt, startIndex, result));
        return result;
    }

    /// <summary>本轮发送层的按键串解析错误（无法识别的特殊键/指令等）。</summary>
    public IReadOnlyList<string> SendErrors => _player.Errors;

    /// <summary>本轮检测到高置信度自动上屏而跳过上屏键的句数。</summary>
    public int AutoCommitSkips => _player.AutoCommitSkips;

    private void RunLoop(KeyTable table, RunOptions opt, int startIndex, RunResult result)
    {
        var pacer = new Pacer { IntervalMs = opt.IntervalMs };
        var sw = new Stopwatch();
        int i = Math.Max(0, startIndex);
        var reason = RunEndReason.Completed;

        _verify.Focus();

        for (; i < table.Units.Count; i++)
        {
            if (_ctrl.StopRequested) { reason = Map(_ctrl.Source); break; }

            var unit = table.Units[i];
            Progress?.Invoke(new RunProgress
            {
                Index = i,
                Total = table.Units.Count,
                CurrentKeys = unit.DisplayText,
            });

            _verify.Reset();
            int commitStart = _verify.SnapshotCommitEvents();   // 本句开始时的 IME 提交计数
            pacer.Reset();
            sw.Restart();

            string expected = table.ExpectedOf(unit);

            // 高置信度自动上屏探测：上屏键发出前，若输入法已把整句上屏，则跳过空格
            bool autoCommit = false;
            var status = _player.Play(unit, _ctrl, pacer, commitNeeded: () =>
            {
                autoCommit = _verify.IsAutoCommitted(expected, Math.Min(opt.SettleMs, 60), () => _ctrl.StopRequested);
                return !autoCommit;
            });
            if (status != SendStatus.Sent)
            {
                reason = status switch
                {
                    SendStatus.Stopped => _ctrl.Source == StopSource.LostFocus
                                          ? RunEndReason.LostFocus : RunEndReason.StoppedByUser,
                    SendStatus.LostFocus => RunEndReason.LostFocus,
                    _ => RunEndReason.SendFailed,
                };
                break;
            }

            string actual = _verify.Capture(opt.SettleMs, opt.SettleMaxMs, () => _ctrl.StopRequested);
            sw.Stop();

            if (_ctrl.StopRequested) { reason = Map(_ctrl.Source); break; }

            var ur = _verify.Compare(i, unit.DisplayText, expected, actual, sw.ElapsedMilliseconds);
            ur.AutoCommit = autoCommit;
            ur.CommitEvents = Math.Max(0, _verify.SnapshotCommitEvents() - commitStart);

            result.Units.Add(ur);
            result.Stats.ElapsedMs += ur.Ms;
            result.Stats.Add(ur, Verifier.ClosesSentence(expected));

            // 实时统计（UI 直接读这个）
            Live.DoneUnits = result.Stats.DoneUnits;
            Live.ErrorUnits = result.Stats.ErrorUnits;
            Live.DoneSentences = result.Stats.DoneSentences;
            Live.ErrorSentences = result.Stats.ErrorSentences;
            Live.ExpectedChars = result.Stats.ExpectedChars;
            Live.ActualChars = result.Stats.ActualChars;
            Live.Sub = result.Stats.Sub;
            Live.Del = result.Stats.Del;
            Live.Ins = result.Stats.Ins;
            Live.CommitEvents = result.Stats.CommitEvents;
            Live.ElapsedMs = result.Stats.ElapsedMs;

            UnitDone?.Invoke(ur);

            result.ResumeIndex = i + 1;

            if (opt.CheckpointPath != null && ((i + 1) % Math.Max(1, opt.CheckpointEvery) == 0))
                SaveCheckpoint(table, opt.CheckpointPath, i + 1, result.Stats);
        }

        if (reason == RunEndReason.Completed) result.ResumeIndex = table.Units.Count;
        result.Reason = reason;
        result.Stats.ElapsedMs = result.Stats.ElapsedMs; // 保持

        if (opt.CheckpointPath != null)
            SaveCheckpoint(table, opt.CheckpointPath, result.ResumeIndex, result.Stats);

        _verify.Reset();
    }

    private static RunEndReason Map(StopSource src) => src switch
    {
        StopSource.LostFocus => RunEndReason.LostFocus,
        _ => RunEndReason.StoppedByUser,
    };

    private static void SaveCheckpoint(KeyTable table, string path, int nextIndex, BenchStats stats)
    {
        try
        {
            new Checkpoint
            {
                NextIndex = nextIndex,
                Stats = stats,
                KeyTableJson = table.ToJson(),
            }.Save(path);
        }
        catch
        {
            // 落盘失败不该中断测试
        }
    }
}
