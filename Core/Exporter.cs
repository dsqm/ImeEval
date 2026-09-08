using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ImeBench.Core;

public sealed class ReportMeta
{
    public string ImeName { get; set; } = "";
    public int IntervalMs { get; set; }
    public string CodeTablePath { get; set; } = "";
    public DateTime FinishedAt { get; set; } = DateTime.Now;
}

/// <summary>把结果导出成 JSON / CSV / HTML。</summary>
public static class Exporter
{
    public static void ToJson(string path, RunResult result, ReportMeta meta)
    {
        var obj = new
        {
            meta,
            summary = new
            {
                units = result.Stats.DoneUnits,
                errorUnits = result.Stats.ErrorUnits,
                ser = result.Stats.Ser,
                sentences = result.Stats.DoneSentences,
                errorSentences = result.Stats.ErrorSentences,
                sentenceSer = result.Stats.SentenceSer,
                expectedChars = result.Stats.ExpectedChars,
                errorChars = result.Stats.ErrorChars,
                cer = result.Stats.Cer,
                accuracy = result.Stats.Accuracy,
                sub = result.Stats.Sub,
                del = result.Stats.Del,
                ins = result.Stats.Ins,
                elapsedMs = result.Stats.ElapsedMs,
            },
            reason = result.ReasonText,
            units = result.Units,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    public static void ToCsv(string path, RunResult result)
    {
        var sb = new StringBuilder();
        sb.Append('\uFEFF'); // BOM，Excel 直接双击不乱码
        sb.AppendLine("序号,按键串,期望,实际,正确,替换,删除,插入,错字数,耗时ms");
        foreach (var u in result.Units)
        {
            sb.Append(u.Index).Append(',');
            sb.Append(Q(u.Keys)).Append(',');
            sb.Append(Q(u.Expected)).Append(',');
            sb.Append(Q(u.Actual)).Append(',');
            sb.Append(u.Correct ? "是" : "否").Append(',');
            sb.Append(u.Sub).Append(',').Append(u.Del).Append(',').Append(u.Ins).Append(',');
            sb.Append(u.Errors).Append(',').Append(u.Ms);
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("CER," + result.Stats.Cer.ToString("P4", CultureInfo.InvariantCulture));
        sb.AppendLine("SER," + result.Stats.Ser.ToString("P4", CultureInfo.InvariantCulture));
        sb.AppendLine("错字数," + result.Stats.ErrorChars);
        sb.AppendLine("期望字数," + result.Stats.ExpectedChars);
        sb.AppendLine("替换," + result.Stats.Sub);
        sb.AppendLine("删除," + result.Stats.Del);
        sb.AppendLine("插入," + result.Stats.Ins);

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Q(string s)
        => "\"" + s.Replace("\"", "\"\"") + "\"";

    public static void ToHtml(string path, RunResult result, ReportMeta meta)
    {
        var s = result.Stats;
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><meta charset=\"utf-8\"><title>ImeBench 评测报告</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:'Microsoft YaHei',system-ui,sans-serif;margin:32px;color:#1a1a1a;background:#fff}");
        sb.AppendLine("h1{font-size:20px;margin:0 0 4px}h2{font-size:15px;margin:24px 0 8px}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;font-size:13px}");
        sb.AppendLine("th,td{border:1px solid #d8d8d8;padding:5px 8px;text-align:left;vertical-align:top}");
        sb.AppendLine("th{background:#f3f4f6;font-weight:600}");
        sb.AppendLine(".cards{display:flex;gap:12px;flex-wrap:wrap;margin:16px 0}");
        sb.AppendLine(".card{border:1px solid #e2e2e2;border-radius:8px;padding:12px 18px;min-width:120px}");
        sb.AppendLine(".card .k{font-size:12px;color:#666}.card .v{font-size:22px;font-weight:600}");
        sb.AppendLine(".ok{color:#1a7f37}.bad{color:#c92a2a}.del{background:#ffe3e3}.ins{background:#fff3bf}");
        sb.AppendLine(".meta{color:#666;font-size:12px;margin-bottom:8px}");
        sb.AppendLine("</style>");

        sb.AppendLine("<h1>ImeBench 输入法评测报告</h1>");
        sb.AppendLine($"<div class=\"meta\">输入法：{H(meta.ImeName)} ｜ 按键间隔：{meta.IntervalMs}ms ｜ " +
                      $"码表：{H(Path.GetFileName(meta.CodeTablePath))} ｜ 完成时间：{meta.FinishedAt:yyyy-MM-dd HH:mm:ss}</div>");

        sb.AppendLine("<div class=\"cards\">");
        Card(sb, "CER 错字率", s.Cer.ToString("P2"), s.Cer < 0.02);
        Card(sb, "SER 错句率", s.Ser.ToString("P2"), s.Ser < 0.1);
        Card(sb, "准确率", s.Accuracy.ToString("P2"), s.Accuracy > 0.98);
        Card(sb, "上屏次数", s.DoneUnits.ToString(), true);
        Card(sb, "错字数", s.ErrorChars.ToString(), s.ErrorChars == 0);
        Card(sb, "替换/删除/插入", $"{s.Sub}/{s.Del}/{s.Ins}", s.ErrorChars == 0);
        sb.AppendLine("</div>");

        sb.AppendLine($"<div class=\"meta\">期望 {s.ExpectedChars} 字，实际 {s.ActualChars} 字，耗时 {s.ElapsedMs / 1000.0:F1}s" +
                      $"（{s.UnitsPerSec:F1} 次上屏/秒）｜ 结束原因：{result.ReasonText}</div>");

        sb.AppendLine("<h2>逐句对照（仅列出错误项）</h2>");
        sb.AppendLine("<table><tr><th>#</th><th>按键串</th><th>期望</th><th>实际</th><th>S</th><th>D</th><th>I</th></tr>");
        foreach (var u in result.Units.Where(x => !x.Correct))
        {
            sb.AppendLine($"<tr><td>{u.Index}</td><td>{H(u.Keys)}</td><td>{H(u.Expected)}</td>" +
                          $"<td class=\"bad\">{H(u.Actual)}</td><td>{u.Sub}</td><td>{u.Del}</td><td>{u.Ins}</td></tr>");
        }
        sb.AppendLine("</table>");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void Card(StringBuilder sb, string k, string v, bool good)
        => sb.AppendLine($"<div class=\"card\"><div class=\"k\">{k}</div><div class=\"v {(good ? "ok" : "bad")}\">{v}</div></div>");

    private static string H(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
