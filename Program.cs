using BomDiffWinform.Forms;
using BomDiffWinform.Services;

namespace BomDiffWinform;

static class Program
{
    [STAThread]
    static void Main()
    {
        // 初始化日志系统（最先执行）
        LogService.Initialize();

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        finally
        {
            // 程序退出前关闭日志
            LogService.Shutdown();
        }
    }
}
