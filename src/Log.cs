using System;
using System.IO;

namespace EnRAGEPlayerFactionExtension
{
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static readonly string DirectoryPath = ExtensionPaths.DataDirectory;
        private static readonly string FilePath = Path.Combine(DirectoryPath, "extension.log");

        internal static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(DirectoryPath);
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " | " + (message ?? string.Empty) + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never be allowed to break a game tick.
            }
        }

        internal static string DataPath(string fileName)
        {
            Directory.CreateDirectory(DirectoryPath);
            return Path.Combine(DirectoryPath, fileName);
        }
    }
}
