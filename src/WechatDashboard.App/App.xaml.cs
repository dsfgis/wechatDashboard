using System.IO;
using System.Windows;
using System.Windows.Threading;
using WechatDashboard.Infrastructure.Capture;

namespace WechatDashboard.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 界面线程上的未处理异常默认会直接终止进程，这里改为记录日志并提示后继续运行
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogException(args.ExceptionObject as Exception, "appdomain");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException(args.Exception, "task");
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception, "dispatcher");
        e.Handled = true;
        MessageBox.Show(
            $"本次操作未完成：{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}详细信息已写入：{LogDirectory}",
            "操作未完成",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static string LogDirectory => Path.Combine(ProjectToolPaths.UserDataRoot, "logs");

    private static void LogException(Exception? exception, string source)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var file = Path.Combine(LogDirectory, $"crash-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(
                file,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({source}) {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 写日志失败不应再次影响主流程
        }
    }
}
