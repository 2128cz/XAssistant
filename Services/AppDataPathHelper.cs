using System;
using System.IO;

namespace XAssistant.Services
{
    public static class AppDataPathHelper
    {
        /// <summary>
        /// 数据目录。设了环境变量 XASSISTANT_DATA_DIR 时优先用它，
        /// 便于冒烟测试起一个完全不碰正式库的实例。
        /// </summary>
        public static string GetAppDataFolder()
        {
            string overrideDir = Environment.GetEnvironmentVariable("XASSISTANT_DATA_DIR") ?? "";
            string path;
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                path = overrideDir;
            }
            else
            {
                string folderName;
#if DEBUG
                folderName = "XAssistant_Dev"; // 开发版专用文件夹
#else
                folderName = "XAssistant"; // 生产版专用文件夹
#endif
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    folderName
                );
            }
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// 电脑使用统计的共享数据目录。写方是独立的 UsageTracker 后台服务，读方是 UsageViewModel，
        /// 两边必须算出同一个路径，所以目录只在这里定一次（服务侧的同一份在
        /// XAssistant.Service/UsagePaths.cs，改一处要同步另一处）。
        ///
        /// 取 CommonApplicationData 而不是写死 C:\ProgramData：盘符与目录重定向都跟着系统走。
        /// 它跟 <see cref="GetAppDataFolder"/> 不同源也是有原因的：服务以 LocalSystem 身份运行，
        /// 它的 %APPDATA% 与登录用户的不是同一个，也不能用相对路径（服务进程的当前目录是 System32）。
        /// 这里只算路径、不顺手建目录：本程序只读这个库，建目录是写方（服务）的事，
        /// 没装服务的机器上多出一个空目录只会让人误以为服务已经就位。
        /// </summary>
        public static string GetUsageTrackerFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "XAssistant",
                "UsageTracker"
            );
        }
    }
}
