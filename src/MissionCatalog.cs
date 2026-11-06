using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace EnRAGEPlayerFactionExtension
{
    internal enum MissionObjectiveType
    {
        ReachLocation, PatrolRoute, RecoverSupplies, HoldArea, DeliverDispatch, InterceptConvoy
    }

    [DataContract]
    internal sealed class MissionDefinition
    {
        private int _runtimeId;

        [DataMember(Name = "id", Order = 1)] public string Key { get; set; }
        [DataMember(Name = "title", Order = 2)] public string Title { get; set; }
        [DataMember(Name = "startInstruction", Order = 3)] public string StartInstruction { get; set; }
        [DataMember(Name = "objectiveType", Order = 4)] public string ObjectiveTypeName { get; set; }
        [DataMember(Name = "suppliesReward", Order = 5)] public int SuppliesReward { get; set; }
        [DataMember(Name = "treasuryReward", Order = 6)] public int TreasuryReward { get; set; }
        [DataMember(Name = "prestigeReward", Order = 7)] public int PrestigeReward { get; set; }
        [DataMember(Name = "weight", Order = 8)] public int Weight { get; set; }
        [DataMember(Name = "waypointCount", Order = 9)] public int WaypointCount { get; set; }
        [DataMember(Name = "targetDistance", Order = 10)] public float TargetDistance { get; set; }
        [DataMember(Name = "objectiveRadius", Order = 11)] public float ObjectiveRadius { get; set; }
        [DataMember(Name = "interactionRadius", Order = 12)] public float InteractionRadius { get; set; }
        [DataMember(Name = "areaRadius", Order = 13)] public float AreaRadius { get; set; }
        [DataMember(Name = "durationSeconds", Order = 14)] public int DurationSeconds { get; set; }
        [DataMember(Name = "encounterAtSeconds", Order = 15)] public int EncounterAtSeconds { get; set; }
        [DataMember(Name = "spawnEncounter", Order = 16)] public bool SpawnEncounter { get; set; }
        [DataMember(Name = "promptLabel", Order = 17)] public string PromptLabel { get; set; }
        [DataMember(Name = "firstPromptLabel", Order = 18)] public string FirstPromptLabel { get; set; }
        [DataMember(Name = "secondPromptLabel", Order = 19)] public string SecondPromptLabel { get; set; }
        [DataMember(Name = "briefing", Order = 20)] public List<string> Briefing { get; set; }
        [DataMember(Name = "checkpointLines", Order = 21)] public List<string> CheckpointLines { get; set; }
        [DataMember(Name = "encounterLines", Order = 22)] public List<string> EncounterLines { get; set; }
        [DataMember(Name = "returnLines", Order = 23)] public List<string> ReturnLines { get; set; }
        [DataMember(Name = "completionLine", Order = 24)] public string CompletionLine { get; set; }
        [DataMember(Name = "lootLines", Order = 25)] public List<string> LootLines { get; set; }
        [DataMember(Name = "contactLines", Order = 26)] public List<string> ContactLines { get; set; }
        [DataMember(Name = "handoverLines", Order = 27)] public List<string> HandoverLines { get; set; }

        [IgnoreDataMember]
        internal int Id { get { return _runtimeId; } set { _runtimeId = value; } }

        [IgnoreDataMember]
        internal MissionObjectiveType ObjectiveType
        {
            get
            {
                MissionObjectiveType parsed;
                return TryParseObjectiveType(ObjectiveTypeName, out parsed) ? parsed : MissionObjectiveType.ReachLocation;
            }
        }

        internal bool Normalize(out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(Key))
            {
                error = "missing required id";
                return false;
            }
            if (string.IsNullOrWhiteSpace(Title))
            {
                error = "missing required title";
                return false;
            }

            MissionObjectiveType parsed;
            if (!TryParseObjectiveType(ObjectiveTypeName, out parsed))
            {
                error = "unknown objectiveType '" + (ObjectiveTypeName ?? string.Empty) + "'";
                return false;
            }

            Key = Key.Trim();
            Title = Title.Trim();
            ObjectiveTypeName = parsed.ToString();
            StartInstruction = string.IsNullOrWhiteSpace(StartInstruction) ? Title : StartInstruction.Trim();
            CompletionLine = CompletionLine == null ? string.Empty : CompletionLine.Trim();
            PromptLabel = PromptLabel == null ? string.Empty : PromptLabel.Trim();
            FirstPromptLabel = FirstPromptLabel == null ? string.Empty : FirstPromptLabel.Trim();
            SecondPromptLabel = SecondPromptLabel == null ? string.Empty : SecondPromptLabel.Trim();
            SuppliesReward = Math.Max(0, SuppliesReward);
            TreasuryReward = Math.Max(0, TreasuryReward);
            PrestigeReward = Math.Max(0, PrestigeReward);
            Weight = Math.Max(0, Math.Min(1000, Weight));
            WaypointCount = Math.Max(1, Math.Min(12, WaypointCount <= 0 ? 1 : WaypointCount));
            TargetDistance = Clamp(TargetDistance <= 0f ? DefaultTargetDistance(parsed) : TargetDistance, 80f, 650f);
            ObjectiveRadius = Clamp(ObjectiveRadius <= 0f ? 24f : ObjectiveRadius, 5f, 100f);
            InteractionRadius = Clamp(InteractionRadius <= 0f ? 5f : InteractionRadius, 2f, 12f);
            AreaRadius = Clamp(AreaRadius <= 0f ? 35f : AreaRadius, 8f, 100f);
            DurationSeconds = parsed == MissionObjectiveType.HoldArea
                ? Math.Max(5, Math.Min(600, DurationSeconds <= 0 ? 18 : DurationSeconds))
                : Math.Max(0, Math.Min(600, DurationSeconds));
            EncounterAtSeconds = parsed == MissionObjectiveType.HoldArea
                ? Math.Max(0, Math.Min(DurationSeconds, EncounterAtSeconds))
                : Math.Max(0, Math.Min(600, EncounterAtSeconds));
            Briefing = CleanLines(Briefing);
            CheckpointLines = CleanLines(CheckpointLines);
            EncounterLines = CleanLines(EncounterLines);
            ReturnLines = CleanLines(ReturnLines);
            LootLines = CleanLines(LootLines);
            ContactLines = CleanLines(ContactLines);
            HandoverLines = CleanLines(HandoverLines);
            return true;
        }

        internal static bool TryParseObjectiveType(string value, out MissionObjectiveType objectiveType)
        {
            objectiveType = MissionObjectiveType.ReachLocation;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string normalized = value.Trim().Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
            return Enum.TryParse(normalized, true, out objectiveType) &&
                Enum.IsDefined(typeof(MissionObjectiveType), objectiveType);
        }

        private static List<string> CleanLines(List<string> lines)
        {
            return lines == null
                ? new List<string>()
                : lines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line.Trim()).ToList();
        }

        private static float Clamp(float value, float min, float max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static float DefaultTargetDistance(MissionObjectiveType type)
        {
            switch (type)
            {
                case MissionObjectiveType.PatrolRoute: return 285f;
                case MissionObjectiveType.HoldArea: return 235f;
                case MissionObjectiveType.InterceptConvoy: return 315f;
                default: return 260f;
            }
        }
    }

    [DataContract]
    internal sealed class MissionCatalogDocument
    {
        [DataMember(Name = "missions", Order = 1)]
        public List<MissionDefinition> Missions { get; set; }
    }

    /// <summary>
    /// Loads mission definitions from the player's Missions directory. Runtime mission IDs are
    /// stable hashes of the user-editable string IDs, except the six legacy IDs kept for saves.
    /// </summary>
    internal static class MissionCatalog
    {
        private const string DefaultResourceName = "EnRAGEPlayerFactionExtension.Missions.defaults.json";
        private static readonly object Sync = new object();
        private static bool _loaded;
        private static List<MissionDefinition> _definitions = new List<MissionDefinition>();
        private static Dictionary<int, MissionDefinition> _byId = new Dictionary<int, MissionDefinition>();

        internal static IList<MissionDefinition> GetDefinitions()
        {
            EnsureLoaded();
            lock (Sync) return _definitions.ToList().AsReadOnly();
        }

        internal static bool TryGet(int id, out MissionDefinition definition)
        {
            EnsureLoaded();
            lock (Sync) return _byId.TryGetValue(id, out definition);
        }

        internal static MissionDefinition Get(int id)
        {
            MissionDefinition definition;
            if (TryGet(id, out definition)) return definition;

            Log.Write("Mission definition ID " + id + " is not present in the loaded catalogue.");
            lock (Sync)
                return _definitions.Count > 0 ? _definitions[0] : CreateEmergencyFallback();
        }

        internal static int DetermineDifficulty(int completed, Random random)
        {
            int tier = Math.Max(0, Math.Min(2, completed / 3));
            if (tier < 2 && random.Next(100) < 12) tier++;
            else if (tier > 0 && random.Next(100) < 18) tier--;
            return Math.Max(0, Math.Min(2, tier));
        }

        internal static int SelectNext(int completed, int lastType, int difficulty, Random random)
        {
            EnsureLoaded();
            List<MissionDefinition> candidates;
            lock (Sync)
            {
                candidates = _definitions.Where(d => d.Weight > 0 &&
                    (_definitions.Count(d2 => d2.Weight > 0) <= 1 || d.Id != lastType)).ToList();
                if (candidates.Count == 0)
                    candidates = _definitions.Where(d => d.Weight > 0).ToList();
            }

            if (candidates.Count == 0)
            {
                Log.Write("Mission selection found no definitions with weight > 0; using the first available definition.");
                lock (Sync) return _definitions.Count > 0 ? _definitions[0].Id : 0;
            }

            int totalWeight = candidates.Sum(d => WeightFor(d, completed, difficulty));
            int roll = random.Next(Math.Max(1, totalWeight));
            foreach (MissionDefinition definition in candidates)
            {
                roll -= WeightFor(definition, completed, difficulty);
                if (roll < 0) return definition.Id;
            }
            return candidates[candidates.Count - 1].Id;
        }

        private static int WeightFor(MissionDefinition definition, int completed, int difficulty)
        {
            int weight = definition.Weight;
            if (definition.ObjectiveType == MissionObjectiveType.InterceptConvoy)
            {
                if (difficulty == 0) weight = Math.Max(1, weight - 1);
                else if (difficulty == 2) weight += 3;
            }
            if ((completed + definition.Id) % 4 == 0) weight++;
            return Math.Max(1, weight);
        }

        internal static string DifficultyName(int difficulty)
        {
            switch (difficulty) { case 1: return "Media"; case 2: return "Alta"; default: return "Baja"; }
        }

        internal static void GetRewards(int missionType, int difficulty, out int supplies, out int treasury, out int prestige)
        {
            MissionDefinition definition = Get(missionType);
            double multiplier = 1.0 + Math.Max(0, Math.Min(2, difficulty)) * 0.30;
            supplies = (int)Math.Round(definition.SuppliesReward * multiplier);
            treasury = (int)Math.Round(definition.TreasuryReward * multiplier);
            prestige = (int)Math.Round(definition.PrestigeReward * multiplier);
            if (definition.SuppliesReward > 0) supplies = Math.Max(1, supplies);
            if (definition.TreasuryReward > 0) treasury = Math.Max(1, treasury);
            if (definition.PrestigeReward > 0) prestige = Math.Max(1, prestige);
        }

        internal static void Reload()
        {
            lock (Sync) _loaded = false;
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (_loaded) return;
                _loaded = true;
                LoadFromDisk();
            }
        }

        private static void LoadFromDisk()
        {
            string directory = Log.DataPath("Missions");
            try { Directory.CreateDirectory(directory); }
            catch (Exception ex)
            {
                Log.Write("Could not create missions directory: " + ex.Message);
                return;
            }

            EnsureDefaultFile(directory);
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => string.Equals(Path.GetFileName(path), "defaults.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Log.Write("Could not enumerate mission files: " + ex.Message);
                return;
            }

            var byId = new Dictionary<int, MissionDefinition>();
            var byKey = new Dictionary<string, MissionDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in files)
            {
                List<MissionDefinition> entries;
                try { entries = ReadMissionFile(path); }
                catch (Exception ex)
                {
                    Log.Write("Mission file skipped (" + Path.GetFileName(path) + "): " + ex.Message);
                    continue;
                }

                foreach (MissionDefinition definition in entries)
                {
                    string validationError;
                    if (definition == null || !definition.Normalize(out validationError))
                    {
                        Log.Write("Invalid mission in " + Path.GetFileName(path) + ": " +
                            (definition == null ? "empty entry" : validationError) + ".");
                        continue;
                    }

                    definition.Id = GetRuntimeId(definition.Key);
                    MissionDefinition keyMatch;
                    if (byKey.TryGetValue(definition.Key, out keyMatch))
                    {
                        // A user file can intentionally override a built-in mission by using its ID.
                        byId.Remove(keyMatch.Id);
                        Log.Write("Mission ID '" + definition.Key + "' overridden by " + Path.GetFileName(path) + ".");
                    }
                    else
                    {
                        MissionDefinition idMatch;
                        if (byId.TryGetValue(definition.Id, out idMatch))
                        {
                            Log.Write("Mission skipped because its ID hash collides with '" + idMatch.Key + "': " + definition.Key + ".");
                            continue;
                        }
                    }

                    byKey[definition.Key] = definition;
                    byId[definition.Id] = definition;
                }
            }

            lock (Sync)
            {
                _byId = byId;
                _definitions = byId.Values.OrderBy(d => d.Id).ToList();
            }
            Log.Write("Loaded " + _definitions.Count + " editable mission definitions from " + directory + ".");
        }

        private static List<MissionDefinition> ReadMissionFile(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("file is empty");

            MissionCatalogDocument document = TryDeserialize<MissionCatalogDocument>(json);
            if (document != null && document.Missions != null)
                return document.Missions;

            MissionDefinition single = TryDeserialize<MissionDefinition>(json);
            if (single != null && !string.IsNullOrWhiteSpace(single.Key))
                return new List<MissionDefinition> { single };

            List<MissionDefinition> list = TryDeserialize<List<MissionDefinition>>(json);
            if (list != null) return list;
            throw new InvalidDataException("expected a mission object, a JSON array, or an object with a missions array");
        }

        private static T TryDeserialize<T>(string json) where T : class
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    return serializer.ReadObject(stream) as T;
            }
            catch { return null; }
        }

        private static void EnsureDefaultFile(string directory)
        {
            string target = Path.Combine(directory, "defaults.json");
            if (File.Exists(target)) return;

            try
            {
                using (var resource = typeof(MissionCatalog).Assembly.GetManifestResourceStream(DefaultResourceName))
                {
                    if (resource == null)
                    {
                        Log.Write("Embedded default mission catalogue not found: " + DefaultResourceName + ".");
                        return;
                    }
                    using (var destination = File.Create(target))
                        resource.CopyTo(destination);
                }
                Log.Write("Installed default mission catalogue at " + target + ".");
            }
            catch (Exception ex) { Log.Write("Could not install default mission catalogue: " + ex.Message); }
        }

        private static int GetRuntimeId(string key)
        {
            switch (key.Trim().ToLowerInvariant())
            {
                case "recon":
                case "reconnaissance": return 0;
                case "patrol": return 1;
                case "cache":
                case "supplies": return 2;
                case "stakeout":
                case "hold-area": return 3;
                case "courier":
                case "dispatch": return 4;
                case "convoy": return 5;
            }

            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in key.Trim().ToLowerInvariant())
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return 1000 + (int)(hash % 1000000000U);
            }
        }

        private static MissionDefinition CreateEmergencyFallback()
        {
            var definition = new MissionDefinition
            {
                Key = "emergency-recon",
                Title = "Reconocimiento de ruta",
                StartInstruction = "Visita el punto marcado y regresa al campamento.",
                ObjectiveTypeName = "ReachLocation",
                SuppliesReward = 1,
                TreasuryReward = 1,
                PrestigeReward = 1,
                Weight = 1,
                WaypointCount = 1,
                TargetDistance = 260f,
                ObjectiveRadius = 24f,
                InteractionRadius = 5f,
                AreaRadius = 35f,
                Briefing = new List<string> { "ENCARGADO: Revisa el camino y vuelve con un informe." },
                CheckpointLines = new List<string>(),
                EncounterLines = new List<string>(),
                ReturnLines = new List<string>(),
                CompletionLine = "El informe está recibido."
            };
            definition.Normalize(out _);
            definition.Id = 0;
            return definition;
        }
    }
}
