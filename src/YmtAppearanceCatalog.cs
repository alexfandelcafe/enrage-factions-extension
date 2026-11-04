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
        internal string DisplayNameOverride;

        internal string DisplayName
        {
            get
            {
                string name = !string.IsNullOrWhiteSpace(DisplayNameOverride)
                    ? DisplayNameOverride.Trim()
                    : (string.IsNullOrWhiteSpace(Drawable) ? "Activo sin nombre" : Drawable.Trim());
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
    /// Reads YMT Trainer outfit XML and the installed Ultimate YMT Helper part library.
    /// Both sources are normalized into the same MetaPed component catalog.
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
            string savedDirectory = Path.GetFullPath(Path.Combine(
                ExtensionPaths.ScriptsDirectory, "..", "YMTTFiles", "SavedYMTs"));
            string libraryPath = Path.GetFullPath(Path.Combine(
                ExtensionPaths.ScriptsDirectory, "..", "UltimateYMTHelper", "part_library.xml"));
            directory = savedDirectory + " | " + libraryPath;
            filesScanned = 0;

            var assets = new List<YmtAppearanceAsset>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Keep the original YMT Trainer import available as a fallback/additional source.
            if (Directory.Exists(savedDirectory))
            {
                string[] files;
                try { files = Directory.GetFiles(savedDirectory, "*.xml", SearchOption.TopDirectoryOnly); }
                catch (Exception ex)
                {
                    Log.Write("Could not enumerate YMT saved outfits: " + ex.Message);
                    files = new string[0];
                }

                foreach (string file in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        XmlDocument document = LoadXmlDocument(file, 5L * 1024L * 1024L);
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

                            AddAsset(assets, seen, asset);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Skipped invalid YMT outfit file '" + Path.GetFileName(file) + "': " + ex.Message);
                    }
                }
            }

            // Ultimate YMT Helper maintains a separate parts library in the game root.
            // Its schema is not the same as YMT Trainer's outfit XML, so inspect each record
            // and accept several common field/attribute spellings instead of assuming one layout.
            if (File.Exists(libraryPath))
            {
                try
                {
                    XmlDocument library = LoadXmlDocument(libraryPath, 64L * 1024L * 1024L);
                    filesScanned++;
                    int elementsSeen;
                    int libraryAdded = ParsePartLibrary(library, assets, seen, out elementsSeen);
                    string rootName = library.DocumentElement == null ? "(sin raíz)" : library.DocumentElement.Name;
                    Log.Write("Ultimate YMT Helper part_library.xml: root=" + rootName +
                        ", elements=" + elementsSeen + ", indexed=" + libraryAdded +
                        ", file=" + libraryPath + ".");
                    if (libraryAdded == 0)
                        Log.Write("part_library.xml schema sample: " + DescribeLibraryStructure(library));
                }
                catch (Exception ex)
                {
                    Log.Write("Could not read Ultimate YMT Helper part_library.xml '" + libraryPath + "': " + ex);
                }
            }
            else
            {
                Log.Write("Ultimate YMT Helper part_library.xml not found at " + libraryPath + ".");
            }

            Log.Write("YMT appearance catalog scanned " + filesScanned + " XML file(s) and indexed " +
                assets.Count + " unique component asset(s) from " + directory + ".");
            return assets;
        }

        private static XmlDocument LoadXmlDocument(string file, long maximumBytes)
        {
            var info = new FileInfo(file);
            if (info.Length <= 0 || info.Length > maximumBytes)
                throw new InvalidDataException("Unsupported XML file size: " + info.Length + " bytes.");

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = maximumBytes
            };
            var document = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(file, settings))
                document.Load(reader);
            return document;
        }

        private static int ParsePartLibrary(
            XmlDocument document,
            List<YmtAppearanceAsset> assets,
            HashSet<string> seen,
            out int elementsSeen)
        {
            elementsSeen = 0;
            int added = 0;
            XmlNodeList nodes = document.SelectNodes("//*");
            if (nodes == null) return 0;

            foreach (XmlNode node in nodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                elementsSeen++;

                // Prefer an actual MetaPed drawable/code. Human-readable catalogue labels are
                // used for display only; they must never be hashed in place of the drawable.
                string drawable = ReadValue(node,
                    "drawable", "drawableName", "assetName", "componentName", "partName",
                    "asset", "component", "metaPedTag");
                string displayName = ReadValue(node, "displayName", "label", "title", "name");
                string rawHash = ReadValue(node,
                    "drawableHash", "assetHash", "componentHash", "tagHash", "hash", "hashValue");

                if (string.IsNullOrWhiteSpace(drawable))
                    drawable = rawHash;

                if (string.IsNullOrWhiteSpace(drawable) && string.IsNullOrWhiteSpace(rawHash))
                    continue;

                string categoryHint = ReadValue(node,
                    "category", "categoryName", "partCategory", "type", "slot", "partType", "componentType");
                string context = BuildNodeContext(node);
                MetaPedCategory category = ResolveCategory(categoryHint + " " + context + " " + drawable + " " + displayName);
                if (category == null)
                    continue;

                uint drawableHash = ParseHash(rawHash);
                if (drawableHash == 0U)
                    drawableHash = ParseHash(drawable);
                if (drawableHash == 0U)
                    continue;

                var asset = new YmtAppearanceAsset
                {
                    CategoryKey = category.Key,
                    CategoryLabel = category.Label,
                    CategoryTag = category.Tag,
                    Drawable = drawable.Trim(),
                    DisplayNameOverride = displayName,
                    Albedo = ReadValue(node, "albedo", "albedoName", "albedoHash"),
                    Normal = ReadValue(node, "normal", "normalName", "normalHash"),
                    Material = ReadValue(node, "material", "materialName", "materialHash"),
                    Palette = ReadValue(node, "palette", "paletteName", "paletteHash"),
                    Tint0 = ReadTint(node, "tint0"),
                    Tint1 = ReadTint(node, "tint1"),
                    Tint2 = ReadTint(node, "tint2"),
                    SourceOutfit = "Ultimate YMT Helper"
                };
                asset.DrawableHash = drawableHash;
                asset.AlbedoHash = ParseHash(asset.Albedo);
                asset.NormalHash = ParseHash(asset.Normal);
                asset.MaterialHash = ParseHash(asset.Material);
                asset.PaletteHash = ParseHash(asset.Palette);

                if (AddAsset(assets, seen, asset))
                    added++;
            }

            return added;
        }

        private static bool AddAsset(
            List<YmtAppearanceAsset> assets,
            HashSet<string> seen,
            YmtAppearanceAsset asset)
        {
            string signature = asset.CategoryKey + "|" + asset.DrawableHash.ToString("X8") + "|" +
                asset.AlbedoHash.ToString("X8") + "|" + asset.NormalHash.ToString("X8") + "|" +
                asset.MaterialHash.ToString("X8") + "|" + asset.PaletteHash.ToString("X8") + "|" +
                asset.Tint0 + "|" + asset.Tint1 + "|" + asset.Tint2;
            if (!seen.Add(signature)) return false;
            assets.Add(asset);
            return true;
        }

        private static string ReadValue(XmlNode node, params string[] names)
        {
            if (node == null || names == null) return string.Empty;

            foreach (string name in names)
            {
                string normalized = NormalizeFieldName(name);
                if (node.Attributes != null)
                {
                    foreach (XmlAttribute attribute in node.Attributes)
                    {
                        if (NormalizeFieldName(attribute.LocalName) == normalized &&
                            !string.IsNullOrWhiteSpace(attribute.Value))
                            return attribute.Value.Trim();
                    }
                }

                foreach (XmlNode child in node.ChildNodes)
                {
                    if (child.NodeType != XmlNodeType.Element ||
                        NormalizeFieldName(child.LocalName) != normalized)
                        continue;

                    string value = child.InnerText == null ? string.Empty : child.InnerText.Trim();
                    if (!string.IsNullOrWhiteSpace(value)) return value;

                    if (child.Attributes != null)
                    {
                        XmlAttribute valueAttribute = child.Attributes["value"];
                        if (valueAttribute == null)
                        {
                            foreach (XmlAttribute attribute in child.Attributes)
                            {
                                if (NormalizeFieldName(attribute.LocalName) == "value")
                                {
                                    valueAttribute = attribute;
                                    break;
                                }
                            }
                        }
                        if (valueAttribute != null) return valueAttribute.Value.Trim();
                    }
                }
            }

            return string.Empty;
        }

        private static string NormalizeFieldName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return new string(value.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant).ToArray());
        }

        private static string BuildNodeContext(XmlNode node)
        {
            var parts = new List<string>();
            XmlNode current = node;
            int depth = 0;
            while (current != null && current.NodeType != XmlNodeType.Document && depth < 6)
            {
                parts.Add(current.LocalName);
                if (current.Attributes != null)
                {
                    foreach (XmlAttribute attribute in current.Attributes)
                    {
                        if (NormalizeFieldName(attribute.LocalName) == "category" ||
                            NormalizeFieldName(attribute.LocalName) == "type" ||
                            NormalizeFieldName(attribute.LocalName) == "slot")
                            parts.Add(attribute.Value);
                    }
                }
                current = current.ParentNode;
                depth++;
            }
            return string.Join(" ", parts.ToArray());
        }

        private static int ReadTint(XmlNode node, string name)
        {
            string value = ReadValue(node, name);
            int parsed;
            if (int.TryParse(value, out parsed)) return parsed;

            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element &&
                    NormalizeFieldName(child.LocalName) == NormalizeFieldName(name) &&
                    child.Attributes != null)
                {
                    foreach (XmlAttribute attribute in child.Attributes)
                    {
                        if (NormalizeFieldName(attribute.LocalName) == "value" &&
                            int.TryParse(attribute.Value, out parsed))
                            return parsed;
                    }
                }
            }
            return 0;
        }

        private static string DescribeLibraryStructure(XmlDocument document)
        {
            if (document.DocumentElement == null) return "(XML vacío)";
            var names = new List<string>();
            foreach (XmlNode child in document.DocumentElement.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element)
                {
                    names.Add(child.LocalName);
                    if (names.Count >= 12) break;
                }
            }
            return "root=" + document.DocumentElement.LocalName +
                "; children=" + string.Join(",", names.ToArray());
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
