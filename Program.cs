using ImeEval.Core;
using ImeEval.Poc;
using ImeEval.UI;

namespace ImeEval;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // --compile <码表> <文章> [句尾延迟ms]：只做切分与按键表生成，打印结果后退出。
        // 不创建窗口、不碰焦点，专门用来在后台调试语料切分问题。
        int ci = Array.FindIndex(args, a => a.Equals("--compile", StringComparison.OrdinalIgnoreCase));
        if (ci >= 0 && args.Length >= ci + 3)
        {
            var code = CodeTable.Load(args[ci + 1]);
            var article = File.ReadAllText(args[ci + 2]);
            int tail = args.Length >= ci + 4 && int.TryParse(args[ci + 3], out int t) ? t : 100;
            var res = Compiler.Build(article, code, new CompileOptions { TailDelayMs = tail });
            Console.WriteLine(res);
            foreach (var u in res.Table.Units)
                Console.WriteLine($"{res.Table.ExpectedOf(u)}\t{string.Concat(u.Tokens.Select(x => x.Display))}");
            return;
        }

        // --poc 打开 M0 技术验证窗口（保留，排查输入法行为差异时用）
        // 例：ImeEval.exe --poc --auto
        if (args.Any(a => a.Equals("--poc", StringComparison.OrdinalIgnoreCase)))
        {
            bool auto = args.Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase));
            Application.Run(new PocForm(auto));
            return;
        }

        // --smoke <码表> <文章> [间隔ms] 无人值守端到端跑一轮
        int si = Array.FindIndex(args, a => a.Equals("--smoke", StringComparison.OrdinalIgnoreCase));
        if (si >= 0 && args.Length >= si + 3)
        {
            string codePath = args[si + 1];
            string articlePath = args[si + 2];
            int? interval = args.Length >= si + 4 && int.TryParse(args[si + 3], out int v) ? v : null;

            var form = new MainForm();
            form.Shown += async (_, _) =>
            {
                int code = await form.RunSmokeAsync(codePath, articlePath, interval);
                Environment.ExitCode = code;
                await Task.Delay(300);
                Application.Exit();
            };
            Application.Run(form);
            return;
        }

        Application.Run(new MainForm());
    }
}
