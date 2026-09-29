using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImeBench.Core;

/// <summary>
/// 按键 token 的语义类型。
///
/// 关键：SEP（音节分隔符）与 COMMIT（确认上屏）在物理键上可能都是空格，
/// 但语义完全不同 —— 混用会导致"上屏次数"统计错误。所以必须显式区分。
/// </summary>
public enum KeyTokenKind
{
    /// <summary>普通编码按键（如 "zhong"）。</summary>
    Key,

    /// <summary>音节分隔符（双拼/五笔需要，全拼不需要）。不上屏。</summary>
    Sep,

    /// <summary>确认上屏。一次 COMMIT = 一次上屏检查点。</summary>
    Commit,
}

public sealed class KeyToken
{
    public KeyTokenKind Kind { get; set; } = KeyTokenKind.Key;

    /// <summary>实际要发送的按键串。语法见 KeySeq：Commit 通常为 "_"（空格）。</summary>
    public string Keys { get; set; } = "";

    /// <summary>该 token 对应的目标字符（Key 类型才有）。代理对只取高位代理，完整文本看 TargetText。</summary>
    public char? Target { get; set; }

    /// <summary>
    /// 该 token 产出的完整目标文本（Key 类型才有）：
    /// 单字 = 整个文本元素（代理对是完整一对），词组 = 整词，多字标点 = 整串。
    /// 为空时回落到 Target。
    /// </summary>
    public string TargetText { get; set; } = "";

    [JsonIgnore] public bool IsCommit => Kind == KeyTokenKind.Commit;

    /// <summary>是否触发一次上屏检查（读文本 + 比对）。</summary>
    [JsonIgnore] public bool IsCheckpoint => Kind == KeyTokenKind.Commit;

    /// <summary>UI 显示用：空格一律显示 _；确认上屏也是空格，同样显示 _。</summary>
    public string Display => Kind switch
    {
        KeyTokenKind.Commit => "_",
        KeyTokenKind.Sep => "_",
        _ => Keys.Replace(' ', '_'),
    };

    public KeyToken Clone() => new() { Kind = Kind, Keys = Keys, Target = Target, TargetText = TargetText };
}

/// <summary>
/// 一个上屏单元：连续若干 token，以一个 COMMIT 收尾，对应一次上屏。
/// 断点续测的粒度就落在它身上。
/// </summary>
public sealed class KeyUnit
{
    public List<KeyToken> Tokens { get; set; } = new();

    /// <summary>本单元在全文期望文本中的起始下标。</summary>
    public int ExpectedStart { get; set; }

    /// <summary>本单元期望产生的文本长度（字符数）。</summary>
    public int ExpectedLength { get; set; }

    [JsonIgnore]
    public string KeysText => string.Concat(Tokens.Select(t => t.Keys));

    [JsonIgnore]
    public string DisplayText => string.Concat(Tokens.Select(t => t.Display));

    [JsonIgnore]
    public int SendLength => Tokens.Sum(t => KeySeq.CountKeys(t.Keys));
}

/// <summary>整张按键表：测试脚本的完整、可复现描述。</summary>
public sealed class KeyTable
{
    public List<KeyUnit> Units { get; set; } = new();

    /// <summary>全文期望文本（归一化后）。</summary>
    public string ExpectedText { get; set; } = "";

    /// <summary>生成时间，写进报告与断点文件。</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [JsonIgnore] public int UnitCount => Units.Count;

    [JsonIgnore] public int TotalKeys => Units.Sum(u => u.SendLength);

    public string ExpectedOf(KeyUnit u)
        => ExpectedText.Length == 0 ? ""
           : ExpectedText.Substring(Math.Min(u.ExpectedStart, ExpectedText.Length),
                                    Math.Min(u.ExpectedLength, ExpectedText.Length - Math.Min(u.ExpectedStart, ExpectedText.Length)));

    public string ToJson()
        => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public static KeyTable? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<KeyTable>(json); }
        catch { return null; }
    }

    public void Save(string path) => File.WriteAllText(path, ToJson());

    public static KeyTable? Load(string path)
        => File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
}
