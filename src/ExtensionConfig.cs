using System;
using System.Globalization;
using System.IO;

namespace EnRAGEPlayerFactionExtension
{
    internal sealed class ExtensionConfig
    {
        internal float FounderX = -1641.797f;
        internal float FounderY = -1399.678f;
        internal float FounderZ = 82.804f;
        internal float FounderHeading = 90f;
        internal string FounderModel = "U_M_M_Rwfrancher_01";
        internal float SpawnDistance = 300f;
        internal float InteractionDistance = 3f;
        internal string FounderPrompt = "Fundar una banda";

        internal static ExtensionConfig Load()
        {
            ExtensionConfig config = new ExtensionConfig();
            string path = Path.Combine(ExtensionPaths.ScriptsDirectory, "EnRAGEPlayerFactionExtension.ini");
            if (!File.Exists(path))
            {
                try
                {
                    File.WriteAllText(path,
                        "; EnRAGE Player Faction Extension settings" + Environment.NewLine +
                        "FounderX=-1641.797" + Environment.NewLine +
                        "FounderY=-1399.678" + Environment.NewLine +
                        "FounderZ=82.804" + Environment.NewLine +
                        "FounderHeading=90.0" + Environment.NewLine +
                        "FounderModel=U_M_M_Rwfrancher_01" + Environment.NewLine +
                        "SpawnDistance=300.0" + Environment.NewLine +
                        "InteractionDistance=3.0" + Environment.NewLine +
                        "FounderPrompt=Fundar una banda" + Environment.NewLine);
                }
                catch (Exception ex) { Log.Write("Could not create INI: " + ex.Message); }
            }

            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                        continue;
                    int split = line.IndexOf('=');
                    if (split <= 0) continue;
                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    float number;
                    switch (key.ToLowerInvariant())
                    {
                        case "founderx": if (TryFloat(value, out number)) config.FounderX = number; break;
                        case "foundery": if (TryFloat(value, out number)) config.FounderY = number; break;
                        case "founderz": if (TryFloat(value, out number)) config.FounderZ = number; break;
                        case "founderheading": if (TryFloat(value, out number)) config.FounderHeading = number; break;
                        case "foundermodel": if (!string.IsNullOrWhiteSpace(value)) config.FounderModel = value; break;
                        case "spawndistance": if (TryFloat(value, out number)) config.SpawnDistance = Clamp(number, 300f, 600f); break;
                        case "interactiondistance": if (TryFloat(value, out number)) config.InteractionDistance = Clamp(number, 1.5f, 5f); break;
                        case "founderprompt": if (!string.IsNullOrWhiteSpace(value)) config.FounderPrompt = value; break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("INI read failed: " + ex);
            }
            return config;
        }

        private static bool TryFloat(string value, out float number)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
                   float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number);
        }

        private static float Clamp(float value, float min, float max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
