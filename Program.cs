using ImeBench.Poc;
using ImeBench.UI;

namespace ImeBench;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // --poc 打开 M0 技术验证窗口（保留，排查输入法行为差异时用）
        // 例：ImeBench.exe --poc --auto
        if (args.Any(a => a.Equals("--poc", StringComparison.OrdinalIgnoreCase)))
        {
            bool auto = args.Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase));
            Application.Run(new PocForm(auto));
            return;
        }

        // --smoke <码表> <文章> [间隔ms] 无人值守端到端跑一轮并导出报告
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
