using System;
using System.IO;

namespace HalouSuite.Host
{
    /// <summary>
    /// 烟测/调试用文件日志。写到 %TEMP%\HalouHost.log。
    /// 由于无法在外部看到 acad 命令行输出，所有关键状态都同时写日志。
    /// 生产版本应在 IPayload 真正接管业务后改为环境变量开关。
    /// </summary>
    internal static class DiagLog
    {
        private static readonly object s_lock = new object();
        private static readonly string s_logPath =
            Path.Combine(Path.GetTempPath(), "HalouHost.log");

        public static string LogPath { get { return s_logPath; } }

        public static void Reset()
        {
            try
            {
                lock (s_lock)
                {
                    File.WriteAllText(s_logPath,
                        "=== HalouHost diag log opened " + DateTime.Now.ToString("o") + " ===\r\n");
                }
            }
            catch { }
        }

        public static void Write(string source, string message)
        {
            try
            {
                lock (s_lock)
                {
                    File.AppendAllText(s_logPath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [" + source + "] " + message + "\r\n");
                }
            }
            catch { }
        }
    }
}
