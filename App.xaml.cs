using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace FileCopyWithMenu
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                WriteCrashLog(args.ExceptionObject as Exception, "AppDomain");
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrashLog(e.Exception, "Dispatcher");
            MessageBox.Show(
                "程序遇到未处理的错误：\n\n" + e.Exception.Message +
                "\n\n详细信息已写入日志：\n" + CrashLogPath,
                "FileCopyWithMenu", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private static string CrashLogPath
        {
            get { return Core.AppPaths.ErrorLog; }
        }

        private static void WriteCrashLog(Exception ex, string source)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(CrashLogPath);
                if (dir != null) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.AppendLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + source + "] ===");
                sb.AppendLine(ex == null ? "(null)" : ex.ToString());
                sb.AppendLine();
                File.AppendAllText(CrashLogPath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 记录失败就算了
            }
        }
    }
}
