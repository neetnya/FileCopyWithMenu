using System;
using System.IO;

namespace FileCopyWithMenu.Core
{
    /// <summary>
    /// 程序用到的固定路径。
    /// 注意：本程序**没有任何记忆功能**，这里只用于崩溃日志这类诊断信息的落盘位置，
    /// 不保存、不读取任何用户状态（目录、窗口尺寸、选项等一律不持久化）。
    /// </summary>
    public static class AppPaths
    {
        public static string DataDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FileCopyWithMenu");
            }
        }

        /// <summary>崩溃日志文件路径（仅在程序异常退出时写入）。</summary>
        public static string ErrorLog
        {
            get { return Path.Combine(DataDirectory, "error.log"); }
        }
    }
}
