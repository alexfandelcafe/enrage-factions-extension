using System;
using System.IO;

namespace EnRAGEPlayerFactionExtension
{
    internal static class ExtensionPaths
    {
        internal static string ScriptsDirectory
        {
            get
            {
                string baseDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                DirectoryInfo info = new DirectoryInfo(baseDirectory);
                if (info.Name.Equals("scripts", StringComparison.OrdinalIgnoreCase))
                    return info.FullName;
                return Path.Combine(baseDirectory, "scripts");
            }
        }

        internal static string DataDirectory
        {
            get { return Path.Combine(ScriptsDirectory, "EnRAGEPlayerFactionExtension"); }
        }
    }
}
