using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace EnRAGEPlayerFactionExtension
{
    /// <summary>
    /// Stores appearance overrides by stable enRAGE roster ID, never by a transient Ped handle.
    /// Each component is persisted separately so a member can change one part without replacing
    /// the rest of the NPC's native outfit.
    /// </summary>
    [DataContract]
    internal sealed class MemberAppearanceStoreDocument
    {
        [DataMember(Order = 1)] public int SchemaVersion { get; set; }
        [DataMember(Order = 2)] public List<MemberAppearanceProfile> Profiles { get; set; }
    }

    [DataContract]
    internal sealed class MemberAppearanceProfile
    {
        [DataMember(Order = 1)] public string MemberId { get; set; }
        [DataMember(Order = 2)] public string DisplayName { get; set; }
        [DataMember(Order = 3)] public uint ModelHash { get; set; }
        [DataMember(Order = 4)] public List<MemberAppearanceComponent> Components { get; set; }
        [DataMember(Order = 5)] public string UpdatedUtc { get; set; }
    }

    [DataContract]
    internal sealed class MemberAppearanceComponent
    {
        [DataMember(Order = 1)] public string CategoryKey { get; set; }
        [DataMember(Order = 2)] public string CategoryLabel { get; set; }
        [DataMember(Order = 3)] public uint CategoryTag { get; set; }
        [DataMember(Order = 4)] public string Drawable { get; set; }
        [DataMember(Order = 5)] public string Albedo { get; set; }
        [DataMember(Order = 6)] public string Normal { get; set; }
        [DataMember(Order = 7)] public string Material { get; set; }
        [DataMember(Order = 8)] public string Palette { get; set; }
        [DataMember(Order = 9)] public uint DrawableHash { get; set; }
        [DataMember(Order = 10)] public uint AlbedoHash { get; set; }
        [DataMember(Order = 11)] public uint NormalHash { get; set; }
        [DataMember(Order = 12)] public uint MaterialHash { get; set; }
        [DataMember(Order = 13)] public uint PaletteHash { get; set; }
        [DataMember(Order = 14)] public int Tint0 { get; set; }
        [DataMember(Order = 15)] public int Tint1 { get; set; }
        [DataMember(Order = 16)] public int Tint2 { get; set; }
        [DataMember(Order = 17)] public string SourceOutfit { get; set; }
    }

    internal sealed class MemberAppearanceStore
    {
        private const int CurrentSchemaVersion = 1;
        private readonly string _path;
        private readonly MemberAppearanceStoreDocument _document;
        private readonly Dictionary<string, MemberAppearanceProfile> _byMember =
            new Dictionary<string, MemberAppearanceProfile>(StringComparer.OrdinalIgnoreCase);

        private MemberAppearanceStore(string path, MemberAppearanceStoreDocument document)
        {
            _path = path;
            _document = document ?? new MemberAppearanceStoreDocument();
            if (_document.Profiles == null)
                _document.Profiles = new List<MemberAppearanceProfile>();

            foreach (MemberAppearanceProfile profile in _document.Profiles)
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.MemberId))
                    continue;
                if (profile.Components == null)
                    profile.Components = new List<MemberAppearanceComponent>();
                _byMember[profile.MemberId] = profile;
            }

            _document.SchemaVersion = CurrentSchemaVersion;
        }

        internal static MemberAppearanceStore Load()
        {
            string path = Log.DataPath("MemberAppearances.json");
            MemberAppearanceStoreDocument document = null;

            if (File.Exists(path))
            {
                try
                {
                    var serializer = new DataContractJsonSerializer(typeof(MemberAppearanceStoreDocument));
                    using (FileStream stream = File.OpenRead(path))
                        document = serializer.ReadObject(stream) as MemberAppearanceStoreDocument;
                }
                catch (Exception ex)
                {
                    Log.Write("Member appearance store load failed; starting with an empty in-memory store: " + ex.Message);
                }
            }

            var store = new MemberAppearanceStore(path, document);
            Log.Write("Member appearance profiles loaded: " + store._byMember.Count + ".");
            return store;
        }

        internal IEnumerable<MemberAppearanceProfile> Profiles
        {
            get { return _byMember.Values; }
        }

        internal bool TryGetProfile(string memberId, out MemberAppearanceProfile profile)
        {
            profile = null;
            return !string.IsNullOrWhiteSpace(memberId) && _byMember.TryGetValue(memberId, out profile);
        }

        internal int ComponentCount(string memberId)
        {
            MemberAppearanceProfile profile;
            return TryGetProfile(memberId, out profile) && profile.Components != null
                ? profile.Components.Count
                : 0;
        }

        internal void SaveComponent(
            string memberId,
            string displayName,
            uint modelHash,
            MemberAppearanceComponent component)
        {
            if (string.IsNullOrWhiteSpace(memberId))
                throw new ArgumentException("Roster member ID is required.", nameof(memberId));
            if (component == null || string.IsNullOrWhiteSpace(component.CategoryKey))
                throw new ArgumentException("A categorized appearance component is required.", nameof(component));

            MemberAppearanceProfile profile;
            if (!_byMember.TryGetValue(memberId, out profile))
            {
                profile = new MemberAppearanceProfile
                {
                    MemberId = memberId,
                    DisplayName = displayName ?? memberId,
                    ModelHash = modelHash,
                    Components = new List<MemberAppearanceComponent>()
                };
                _byMember.Add(memberId, profile);
                _document.Profiles.Add(profile);
            }
            else if (profile.ModelHash != 0U && modelHash != 0U && profile.ModelHash != modelHash)
            {
                // Components are model-specific. Never carry a previous model's overrides onto a
                // different body; start a clean profile when the underlying model actually changes.
                profile.Components.Clear();
                Log.Write("Cleared model-specific appearance overrides for roster member " + memberId +
                    " because its model changed.");
            }

            profile.DisplayName = string.IsNullOrWhiteSpace(displayName) ? profile.DisplayName : displayName;
            if (modelHash != 0U)
                profile.ModelHash = modelHash;
            if (profile.Components == null)
                profile.Components = new List<MemberAppearanceComponent>();

            int existingIndex = profile.Components.FindIndex(x =>
                x != null && string.Equals(x.CategoryKey, component.CategoryKey, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                profile.Components[existingIndex] = component;
            else
                profile.Components.Add(component);

            profile.Components = profile.Components
                .Where(x => x != null)
                .OrderBy(x => x.CategoryKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
            profile.UpdatedUtc = DateTime.UtcNow.ToString("o");
            Save();
        }

        internal void Save()
        {
            string temporaryPath = _path + ".tmp";
            try
            {
                _document.SchemaVersion = CurrentSchemaVersion;
                _document.Profiles = _byMember.Values
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.MemberId))
                    .OrderBy(x => x.MemberId, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var serializer = new DataContractJsonSerializer(typeof(MemberAppearanceStoreDocument));
                using (FileStream stream = File.Create(temporaryPath))
                    serializer.WriteObject(stream, _document);

                if (File.Exists(_path))
                {
                    string backupPath = _path + ".bak";
                    try { File.Replace(temporaryPath, _path, backupPath, true); }
                    catch
                    {
                        File.Delete(_path);
                        File.Move(temporaryPath, _path);
                    }
                }
                else
                {
                    File.Move(temporaryPath, _path);
                }
            }
            catch
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                throw;
            }
        }
    }
}
