using System;
using System.IO;
using System.Text;

namespace NppMermaidViewer.Utils
{
    internal static class Logger
    {
        private static readonly object SyncRoot = new object();

        internal static string LogFilePath
        {
            get
            {
                string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localApplicationData, "NppMermaidViewer", "logs", "NppMermaidViewer.log");
            }
        }

        internal static void LogException(string context, Exception exception)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("[").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
                .Append(context).AppendLine();
            builder.AppendLine(exception.ToString());
            Write(builder.ToString());
        }

        internal static void LogInfo(string context, string message)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("[").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ")
                .Append(context).Append(": ").Append(message).AppendLine();
            Write(builder.ToString());
        }

        private static void Write(string text)
        {
#if !DEBUG
            return;
#else
            try
            {
                string logFilePath = LogFilePath;
                string directory = Path.GetDirectoryName(logFilePath);
                lock (SyncRoot)
                {
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);
                    File.AppendAllText(logFilePath, text);
                }
            }
            catch (Exception)
            {
                // Ignore logging failures so they do not affect user operations.
            }
#endif
        }
    }
}
