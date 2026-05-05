using System;
using System.IO;

namespace XAssistant.Services
{
    public static class AppDataPathHelper
    {
        public static string GetAppDataFolder()
        {
            string folderName;
#if DEBUG
            folderName = "XAssistant_Dev"; // 开发版专用文件夹
#else
            folderName = "XAssistant"; // 生产版专用文件夹
#endif
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                folderName
            );
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
