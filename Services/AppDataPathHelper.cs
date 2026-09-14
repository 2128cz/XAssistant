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
    }
}
