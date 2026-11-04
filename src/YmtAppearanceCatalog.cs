using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using RDR2.Native;

namespace EnRAGEPlayerFactionExtension
{
    internal sealed class MetaPedCategory
    {
        internal readonly string Key;
        internal readonly string Label;
        internal readonly uint Tag;
        internal readonly string[] MatchTerms;

        internal MetaPedCategory(string key, string label, uint tag, params string[] matchTerms)
        {
            Key = key;
            Label = label;
            Tag = tag;
            MatchTerms = matchTerms ?? new string[0];
        }

        internal bool Matches(string drawable)
        {
            string value = (drawable ?? string.Empty).ToLowerInvariant();
            return MatchTerms.Any(term => value.Contains(term));
        }
    }

    internal sealed class YmtAppearanceAsset
    {
        internal string CategoryKey;
        internal string CategoryLabel;
        internal uint CategoryTag;
        internal string Drawable;
        internal string Albedo;
        internal string Normal;
        internal string Material;
        internal string Palette;
        internal uint DrawableHash;
        internal uint AlbedoHash;
        internal uint NormalHash;
        internal uint MaterialHash;
        internal uint PaletteHash;
        internal int Tint0;
        internal int Tint1;
        internal int Tint2;
        internal string SourceOutfit;

        internal string DisplayName
        {
            get
            {
                string name = string.IsNullOrWhiteSpace(Drawable) ? "Activo sin nombre" : Drawable.Trim();
                if (name.StartsWith("Unknown [", StringComparison.OrdinalIgnoreCase))
                {
                    int start = name.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
                    if (start >= 0) name = "Activo " + name.Substring(start).TrimEnd(']', ')', ' ');
                }
                if (name.Length > 39) name = name.Substring(0, 36) + "...";
                return name;
            }
        }

        internal MemberAppearanceComponent ToStoredComponent()
        {
            return new MemberAppearanceComponent
            {
                CategoryKey = CategoryKey,
                CategoryLabel = CategoryLabel,
                CategoryTag = CategoryTag,
                Drawable = Drawable,
                Albedo = Albedo,
                Normal = Normal,
                Material = Material,
                Palette = Palette,
                DrawableHash = DrawableHash,
                AlbedoHash = AlbedoHash,
                NormalHash = NormalHash,
                MaterialHash = MaterialHash,
                PaletteHash = PaletteHash,
                Tint0 = Tint0,
                Tint1 = Tint1,
                Tint2 = Tint2,
                SourceOutfit = SourceOutfit
            };
        }
    }

    /// <summary>
    /// Reads the user-exported YMT Trainer XML files. It does not bundle extracted game assets:
    /// the player controls which outfit/component assets are available to the editor.
    /// </summary>
    internal static class YmtAppearanceCatalog
    {
        private static readonly MetaPedCategory[] CategoryItems =
        {
            new MetaPedCategory("scarf", "Pañuelos", 0x7179040D, "_scarf", "scarf"),
            new MetaPedCategory("eyebrows", "Cejas", 0x15AD30B0, "_eyebrow", "eyebrow"),
            new MetaPedCategory("glasses", "Gafas", 0x79BC67B1, "_glasses", "glasses"),
            new MetaPedCategory("suboutfit", "Subconjuntos", 0x9E75200C, "suboutfit"),
            new MetaPedCategory("beard", "Barba", 0x3E5AB611, "_beard", "beard"),
            new MetaPedCategory("hair", "Cabello", 0x86A51CD1, "_hair", "hair"),
            new MetaPedCategory("hat", "Sombreros", 0x99259979, "_hat", "hat"),
            new MetaPedCategory("head", "Cabeza", 0x106561CA, "_head", "head"),
            new MetaPedCategory("eyes", "Ojos", 0x8649E03F, "_eyes", "eyes"),
            new MetaPedCategory("teeth", "Dientes", 0x8037C0C6, "_teeth", "teeth"),
            new MetaPedCategory("shirt", "Camisas", 0x2026C13B, "_shirt", "shirt"),
            new MetaPedCategory("vest", "Chalecos", 0x0B6CB287, "_vest", "vest"),
            new MetaPedCategory("coat", "Abrigos", 0x51E52756, "_coat", "coat"),
            new MetaPedCategory("pants", "Pantalones", 0x31034C74, "_pant", "pants"),
            new MetaPedCategory("boots", "Botas", 0x40954316, "_boot", "boot"),
            new MetaPedCategory("neckties", "Corbatas", 0x7179040D, "_tie", "necktie"),
            new MetaPedCategory("belts", "Cinturones", 0xC362CAC7, "_belt", "belt"),
            new MetaPedCategory("gloves", "Guantes", 0x296C078B, "_glove", "glove"),
            new MetaPedCategory("rings", "Anillos", 0x7A680DA4, "_ring", "ring"),
            new MetaPedCategory("spurs", "Espuelas", 0x18725E12, "spurs", "spur")
        };

        internal static IList<MetaPedCategory> Categories
        {
            get { return CategoryItems; }
        }

        internal static MetaPedCategory GetCategory(string key)
        {
            return CategoryItems.FirstOrDefault(x =>
                string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        internal static List<YmtAppearanceAsset> LoadSavedAssets(out string directory, out int filesScanned)
        {
            directory = Path.GetFullPath(Path.Combine(ExtensionPaths.ScriptsDirectory, "..", "YMTTFiles", "SavedYMTs"));
            filesScanned = 0;
            var assets = new List<YmtAppearanceAsset>();
            if (!Directory.Exists(directory))
                return assets;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] files;
            try { files = Directory.GetFiles(directory, "*.xml", SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                Log.Write("Could not enumerate YMT saved outfits: " + ex.Message);
                return assets;
            }

            foreach (string file in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length <= 0 || info.Length > 5 * 1024 * 1024)
                    {
                        Log.Write("Skipped unsupported YMT XML size: " + info.Name);
                        continue;
                    }

                    var settings = new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        IgnoreComments = true,
                        IgnoreWhitespace = true,
                        MaxCharactersInDocument = 5 * 1024 * 1024
                    };
                    var document = new XmlDocument { XmlResolver = null };
                    using (XmlReader reader = XmlReader.Create(file, settings))
                        document.Load(reader);

                    XmlNodeList nodes = document.SelectNodes("/MetaPed/explicitAssets/Item/explicitAssets/Item");
                    if (nodes == null || nodes.Count == 0)
                        nodes = document.SelectNodes("//Item[drawable]");

                    filesScanned++;
                    string outfitName = Path.GetFileNameWithoutExtension(file);
                    if (nodes == null) continue;

                    foreach (XmlNode node in nodes)
                    {
                        string drawable = ReadChildText(node, "drawable");
                        if (string.IsNullOrWhiteSpace(drawable)) continue;
                        MetaPedCategory category = ResolveCategory(drawable);
                        if (category == null) continue;

                        var asset = new YmtAppearanceAsset
                        {
                            CategoryKey = category.Key,
                            CategoryLabel = category.Label,
                            CategoryTag = category.Tag,
                            Drawable = drawable.Trim(),
                            Albedo = ReadChildText(node, "albedo"),
                            Normal = ReadChildText(node, "normal"),
                            Material = ReadChildText(node, "material"),
                            Palette = ReadChildText(node, "palette"),
                            Tint0 = ReadIntAttribute(node, "tint0", "value"),
                            Tint1 = ReadIntAttribute(node, "tint1", "value"),
                            Tint2 = ReadIntAttribute(node, "tint2", "value"),
                            SourceOutfit = outfitName
                        };
                        asset.DrawableHash = ParseHash(asset.Drawable);
                        asset.AlbedoHash = ParseHash(asset.Albedo);
                        asset.NormalHash = ParseHash(asset.Normal);
                        asset.MaterialHash = ParseHash(asset.Material);
                        asset.PaletteHash = ParseHash(asset.Palette);
                        if (asset.DrawableHash == 0U) continue;

                        string signature = asset.CategoryKey + "|" + asset.DrawableHash.ToString("X8") + "|" +
                            asset.AlbedoHash.ToString("X8") + "|" + asset.NormalHash.ToString("X8") + "|" +
                            asset.MaterialHash.ToString("X8") + "|" + asset.PaletteHash.ToString("X8") + "|" +
                            asset.Tint0 + "|" + asset.Tint1 + "|" + asset.Tint2;
                        if (seen.Add(signature))
                            assets.Add(asset);
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Skipped invalid YMT outfit file '" + Path.GetFileName(file) + "': " + ex.Message);
                }
            }

            Log.Write("YMT appearance catalog scanned " + filesScanned + " file(s) and indexed " +
                assets.Count + " component asset(s) from " + directory + ".");
            return assets;
        }

        internal static uint ParseHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0U;
            string candidate = value.Trim();
            int marker = candidate.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                int start = marker + 2;
                int end = start;
                while (end < candidate.Length && Uri.IsHexDigit(candidate[end])) end++;
                uint parsed;
                if (end > start && uint.TryParse(candidate.Substring(start, end - start),
                    System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }

            try { return MISC.GET_HASH_KEY(candidate); }
            catch { return 0U; }
        }

        private static MetaPedCategory ResolveCategory(string drawable)
        {
            // Specific terms (scarf/eyebrow/glasses) precede broader terms such as tie/head.
            return CategoryItems.FirstOrDefault(x => x.Matches(drawable));
        }

        private static string ReadChildText(XmlNode node, string name)
        {
            if (node == null) return string.Empty;
            XmlNode child = node.SelectSingleNode(name);
            return child == null ? string.Empty : child.InnerText.Trim();
        }

        private static int ReadIntAttribute(XmlNode node, string elementName, string attributeName)
        {
            if (node == null) return 0;
            XmlNode child = node.SelectSingleNode(elementName);
            if (child == null || child.Attributes == null) return 0;
            XmlAttribute attribute = child.Attributes[attributeName];
            int value;
            return attribute != null && int.TryParse(attribute.Value, out value) ? value : 0;
        }
    }
}
