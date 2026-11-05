using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace EnRAGEPlayerFactionExtension
{
    internal enum MissionKind
    {
        SupplyWagon,
        SupplyCache,
        TownStash,
        EnemyTreasury,
        EnemyCamp,
        TrainRobbery,
        Roadblock
    }

    internal sealed class MissionDefinition
    {
        internal string Id;
        internal string Title;
        internal string Description;
        internal string ChronicleTag;
        internal int RequiredEvents;
        internal int Supplies;
        internal int Money;
        internal int Prestige;
        internal MissionKind Kind;
    }

    [System.Runtime.Serialization.DataContract]
    internal sealed class MissionRecord
    {
        [System.Runtime.Serialization.DataMember] public string Id { get; set; }
        [System.Runtime.Serialization.DataMember] public int Progress { get; set; }
        [System.Runtime.Serialization.DataMember] public bool Accepted { get; set; }
        [System.Runtime.Serialization.DataMember] public bool Completed { get; set; }
        [System.Runtime.Serialization.DataMember] public bool RewardClaimed { get; set; }
        [System.Runtime.Serialization.DataMember] public string LastEventKey { get; set; }
    }

    [System.Runtime.Serialization.DataContract]
    internal sealed class MissionSaveData
    {
        [System.Runtime.Serialization.DataMember] public List<MissionRecord> Records { get; set; }
        [System.Runtime.Serialization.DataMember] public int Prestige { get; set; }
        [System.Runtime.Serialization.DataMember] public bool PrestigeInitialized { get; set; }

        internal void Normalize()
        {
            if (Records == null) Records = new List<MissionRecord>();
            Records.RemoveAll(x => x == null || string.IsNullOrWhiteSpace(x.Id));
            Prestige = Math.Max(0, Math.Min(100, Prestige));
        }
    }

    /// <summary>
    /// Mission definitions and event-driven progress. Only native ChronicleDeed events are
    /// accepted as evidence; no mission can complete merely by opening the menu.
    /// </summary>
    internal sealed class MissionManager
    {
        private readonly PlayerFactionData _faction;
        private readonly EnRAGEBridge _bridge;
        private readonly MissionSaveData _save;
        private readonly Action<string> _notify;
        private readonly List<MissionDefinition> _definitions;
        private int _lastAvailabilityCheck;
        private readonly HashSet<string> _available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal MissionManager(PlayerFactionData faction, EnRAGEBridge bridge, MissionSaveData save, Action<string> notify)
        {
            _faction = faction;
            _bridge = bridge;
            _save = save ?? new MissionSaveData();
            _save.Normalize();
            if (_faction.PrestigeInitialized)
            {
                _save.Prestige = _faction.Prestige;
                _save.PrestigeInitialized = true;
            }
            else if (_save.PrestigeInitialized)
            {
                _faction.Prestige = Math.Max(0, Math.Min(100, _save.Prestige));
                _faction.PrestigeInitialized = true;
                _faction.Save();
            }
            _notify = notify;
            _definitions = new List<MissionDefinition>
            {
                new MissionDefinition { Id="supply_wagon", Title="Carreta de suministros", Description="Intervení en una operación de suministros registrada por enRAGE.", ChronicleTag="supplies", RequiredEvents=1, Supplies=18, Money=35, Prestige=4, Kind=MissionKind.SupplyWagon },
                new MissionDefinition { Id="supply_cache", Title="Depósito de suministros", Description="Despejá una operación de suministros y asegurá la carga.", ChronicleTag="supplies", RequiredEvents=1, Supplies=15, Money=20, Prestige=3, Kind=MissionKind.SupplyCache },
                new MissionDefinition { Id="town_stash", Title="Alijo urbano", Description="Recuperá un alijo de cajas registrado por enRAGE.", ChronicleTag="supplies", RequiredEvents=1, Supplies=10, Money=30, Prestige=3, Kind=MissionKind.TownStash },
                new MissionDefinition { Id="enemy_treasury", Title="Tesorería rival", Description="Robá una tesorería enemiga registrada por enRAGE.", ChronicleTag="treasury", RequiredEvents=1, Supplies=0, Money=100, Prestige=5, Kind=MissionKind.EnemyTreasury },
                new MissionDefinition { Id="enemy_camp", Title="Guarida rival", Description="Despejá un campamento enemigo reconocido por enRAGE.", ChronicleTag="camp", RequiredEvents=1, Supplies=8, Money=55, Prestige=6, Kind=MissionKind.EnemyCamp },
                new MissionDefinition { Id="train_robbery", Title="Asalto al tren", Description="Completá un robo de tren reconocido por enRAGE.", ChronicleTag="train", RequiredEvents=1, Supplies=0, Money=125, Prestige=5, Kind=MissionKind.TrainRobbery },
                new MissionDefinition { Id="roadblock", Title="Bloqueo de carretera", Description="Despejá un bloqueo de carretera registrado por enRAGE.", ChronicleTag="roadblock", RequiredEvents=1, Supplies=0, Money=45, Prestige=4, Kind=MissionKind.Roadblock }
            };
            EnsureRecords();
        }

        internal IList<MissionDefinition> Definitions { get { return _definitions.AsReadOnly(); } }
        internal IList<MissionRecord> Records { get { return _save.Records.AsReadOnly(); } }
        internal int Prestige { get { return _save.Prestige; } }

        internal void RefreshAvailability()
        {
            if (!_bridge.IsAvailable) { _available.Clear(); return; }
            // Availability is intentionally conservative: the corresponding native subsystem and
            // ChronicleDeed hook must exist. Event-specific runtime validation is done again when
            // the native event is observed; the extension never fabricates a successful event.
            if (Environment.TickCount - _lastAvailabilityCheck < 3000) return;
            _lastAvailabilityCheck = Environment.TickCount;
            _available.Clear();
            Type mainType = _bridge.MainInstance != null ? _bridge.MainInstance.GetType() : null;
            if (mainType == null) return;
            MethodInfo chronicle = mainType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "ChronicleDeed" && m.GetParameters().Length >= 5);
            if (chronicle == null) return;
            foreach (MissionDefinition definition in _definitions)
            {
                if (NativeSubsystemExists(mainType, definition.Kind))
                    _available.Add(definition.Id);
            }
        }

        private static bool NativeSubsystemExists(Type mainType, MissionKind kind)
        {
            string[] methodNames;
            switch (kind)
            {
                case MissionKind.SupplyWagon: methodNames = new[] { "UpdateSupplyWagons", "SpawnSupplyWagon", "RunSupplyWagon" }; break;
                case MissionKind.SupplyCache: methodNames = new[] { "UpdateSupplyCaches", "UpdateSupplyCamps", "SpawnSupplies" }; break;
                case MissionKind.TownStash: methodNames = new[] { "UpdateTownCrates", "SpawnTownCrates" }; break;
                case MissionKind.EnemyTreasury: methodNames = new[] { "UpdateTreasuries", "UpdateStrongboxes", "SpawnStrongboxes" }; break;
                case MissionKind.EnemyCamp: methodNames = new[] { "UpdateCamps", "SpawnCamp" }; break;
                case MissionKind.TrainRobbery: methodNames = new[] { "UpdateTrainRobbery", "TrainRobbed" }; break;
                case MissionKind.Roadblock: methodNames = new[] { "UpdateRoadblocks", "DropRoadblock" }; break;
                default: return false;
            }
            return methodNames.Any(name => mainType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(method => method.Name == name));
        }

        internal bool CanAccept(string id)
        {
            RefreshAvailability();
            MissionDefinition definition = FindDefinition(id);
            if (definition == null || !_available.Contains(id) || !_faction.Founded) return false;
            if (_save.Records.Any(r => r.Accepted && !r.Completed)) return false;
            MissionRecord record = FindRecord(id);
            return record == null || (!record.Completed && !record.Accepted);
        }

        internal bool Accept(string id)
        {
            if (!CanAccept(id)) return false;
            MissionRecord record = FindRecord(id);
            if (record == null)
            {
                record = new MissionRecord { Id = id };
                _save.Records.Add(record);
            }
            record.Accepted = true;
            record.Progress = 0;
            Save();
            if (_notify != null) _notify("Encargo aceptado: " + FindDefinition(id).Title + ".");
            return true;
        }

        internal void ObserveChronicleEvent(string tag, string title, string place, string gangId, string eventKey)
        {
            if (string.IsNullOrWhiteSpace(tag) || !_faction.Founded) return;
            MissionRecord active = _save.Records.FirstOrDefault(r => r.Accepted && !r.Completed && !r.RewardClaimed);
            if (active == null) return;
            MissionDefinition definition = FindDefinition(active.Id);
            if (definition == null || !TagMatches(definition, tag, title)) return;
            if (!string.IsNullOrWhiteSpace(gangId) && string.Equals(gangId, _faction.Id, StringComparison.OrdinalIgnoreCase))
                return;
            if (!string.IsNullOrWhiteSpace(eventKey) && string.Equals(active.LastEventKey, eventKey, StringComparison.Ordinal))
                return;
            active.LastEventKey = eventKey ?? (tag + "|" + title + "|" + place);
            active.Progress = Math.Min(definition.RequiredEvents, active.Progress + 1);
            if (active.Progress >= definition.RequiredEvents)
            {
                active.Completed = true;
                active.Accepted = false;
                Save();
                if (_notify != null) _notify("Encargo completado: " + definition.Title + ". Volvé al encargado para cobrar.");
            }
            else Save();
        }

        internal void ObserveNativeActivity(MissionKind kind, string eventKey)
        {
            if (!_faction.Founded) return;
            MissionRecord active = _save.Records.FirstOrDefault(r => r.Accepted && !r.Completed && !r.RewardClaimed);
            if (active == null) return;
            MissionDefinition definition = FindDefinition(active.Id);
            if (definition == null || definition.Kind != kind) return;
            if (!string.IsNullOrWhiteSpace(eventKey) && string.Equals(active.LastEventKey, eventKey, StringComparison.Ordinal)) return;
            active.LastEventKey = eventKey ?? (kind.ToString() + "|" + Environment.TickCount);
            active.Progress = Math.Min(definition.RequiredEvents, active.Progress + 1);
            if (active.Progress >= definition.RequiredEvents)
            {
                active.Completed = true;
                active.Accepted = false;
                Save();
                if (_notify != null) _notify("Encargo completado: " + definition.Title + ". Volvé al encargado para cobrar.");
            }
            else Save();
        }

        private static bool TagMatches(MissionDefinition definition, string tag, string title)
        {
            string normalized = (tag + " " + (title ?? string.Empty)).ToLowerInvariant();
            switch (definition.Kind)
            {
                case MissionKind.SupplyWagon:
                    return normalized.Contains("wagon") || normalized.Contains("carreta");
                case MissionKind.SupplyCache:
                    return normalized.Contains("cache") || normalized.Contains("depósito") || normalized.Contains("deposit") ||
                           (string.Equals(tag, "supplies", StringComparison.OrdinalIgnoreCase) &&
                            !normalized.Contains("wagon") && !normalized.Contains("carreta") &&
                            !normalized.Contains("crate") && !normalized.Contains("stash") && !normalized.Contains("caja"));
                case MissionKind.TownStash:
                    return normalized.Contains("crate") || normalized.Contains("stash") || normalized.Contains("caja") || normalized.Contains("alijo");
                case MissionKind.EnemyTreasury:
                    return normalized.Contains("treasury") || normalized.Contains("strongbox") || normalized.Contains("tesorer");
                case MissionKind.EnemyCamp:
                    return normalized.Contains("camp") || normalized.Contains("hideout") || normalized.Contains("guarida");
                case MissionKind.TrainRobbery:
                    return normalized.Contains("train") || normalized.Contains("tren");
                case MissionKind.Roadblock:
                    return normalized.Contains("roadblock") || normalized.Contains("bloqueo");
                default: return string.Equals(tag, definition.ChronicleTag, StringComparison.OrdinalIgnoreCase);
            }
        }

        internal bool Claim(string id)
        {
            MissionRecord record = FindRecord(id);
            MissionDefinition definition = FindDefinition(id);
            if (record == null || definition == null || !record.Completed || record.RewardClaimed) return false;
            // Mark first and persist before applying rewards so a reload cannot pay twice.
            record.RewardClaimed = true;
            Save();
            string error = null;
            bool ok = _bridge.AddFactionReward(_faction, definition.Supplies, definition.Money, out error);
            if (!ok)
            {
                record.RewardClaimed = false;
                Save();
                if (_notify != null) _notify("No se pudo entregar la recompensa: " + (error ?? "enRAGE no disponible") + ".");
                return false;
            }
            _save.Prestige = Math.Max(0, Math.Min(100, _save.Prestige + definition.Prestige));
            _faction.Prestige = _save.Prestige;
            _faction.PrestigeInitialized = true;
            _faction.Save();
            Save();
            if (_notify != null) _notify("Recompensa entregada: +" + definition.Supplies + " suministros, $" + definition.Money + " y +" + definition.Prestige + " prestigio.");
            return true;
        }

        internal void SeedPrestige(int value)
        {
            if (_save.PrestigeInitialized && _faction.PrestigeInitialized &&
                _save.Prestige == _faction.Prestige) return;
            if (!_save.PrestigeInitialized)
            {
                _save.Prestige = Math.Max(0, Math.Min(100, value));
                _save.PrestigeInitialized = true;
            }
            _faction.Prestige = Math.Max(0, Math.Min(100, _save.Prestige));
            _faction.PrestigeInitialized = true;
            Save();
            _faction.Save();
        }

        private void EnsureRecords()
        {
            foreach (MissionDefinition definition in _definitions)
                if (FindRecord(definition.Id) == null) _save.Records.Add(new MissionRecord { Id = definition.Id });
        }

        private MissionDefinition FindDefinition(string id) { return _definitions.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)); }
        private MissionRecord FindRecord(string id) { return _save.Records.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)); }

        internal void Save()
        {
            _save.Normalize();
            MissionPersistence.Save(_save);
        }
    }

    internal static class MissionPersistence
    {
        private static readonly string FilePath = Log.DataPath("Missions.json");
        internal static MissionSaveData Load()
        {
            try
            {
                if (!System.IO.File.Exists(FilePath)) return new MissionSaveData { Records = new List<MissionRecord>() };
                var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(MissionSaveData));
                using (var stream = System.IO.File.OpenRead(FilePath))
                {
                    MissionSaveData save = serializer.ReadObject(stream) as MissionSaveData;
                    if (save == null) save = new MissionSaveData();
                    save.Normalize();
                    return save;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Mission data load failed: " + ex.Message);
                return new MissionSaveData { Records = new List<MissionRecord>() };
            }
        }

        internal static void Save(MissionSaveData save)
        {
            string temp = FilePath + ".tmp";
            try
            {
                string directory = System.IO.Path.GetDirectoryName(FilePath);
                if (!System.IO.Directory.Exists(directory)) System.IO.Directory.CreateDirectory(directory);
                var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(MissionSaveData));
                using (var stream = System.IO.File.Create(temp)) serializer.WriteObject(stream, save);
                if (System.IO.File.Exists(FilePath))
                {
                    try { System.IO.File.Replace(temp, FilePath, FilePath + ".bak", true); }
                    catch { System.IO.File.Delete(FilePath); System.IO.File.Move(temp, FilePath); }
                }
                else System.IO.File.Move(temp, FilePath);
            }
            catch (Exception ex)
            {
                Log.Write("Mission data save failed: " + ex);
                try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); } catch { }
            }
        }
    }

    /// <summary>Harmony callback installed against enRAGE's existing ChronicleDeed method.</summary>
    internal static class MissionChroniclePatch
    {
        internal static MissionManager Manager;
        private static readonly Harmony Harmony = new Harmony("enrage.playerfaction.missions");

        internal static void Install(object main)
        {
            if (main == null) return;
            try
            {
                Type type = main.GetType();
                MethodInfo target = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "ChronicleDeed" && m.GetParameters().Length >= 5 &&
                                         m.GetParameters()[0].ParameterType == typeof(string));
                if (target == null) return;
                MethodInfo postfix = typeof(MissionChroniclePatch).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic);
                if (Harmony.GetPatchInfo(target) != null &&
                    Harmony.GetPatchInfo(target).Owners.Contains("enrage.playerfaction.missions")) return;
                Harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                PatchActivityMethod(type, "TrainRobbed", MissionKind.TrainRobbery);
                PatchActivityMethod(type, "OpenStrongbox", MissionKind.EnemyTreasury);
                Log.Write("Mission ChronicleDeed and native activity hooks installed.");
            }
            catch (Exception ex) { Log.Write("Mission ChronicleDeed hook install failed: " + ex); }
        }

        private static void PatchActivityMethod(Type type, string name, MissionKind kind)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == name))
            {
                try
                {
                    var info = Harmony.GetPatchInfo(method);
                    if (info != null && info.Owners.Contains("enrage.playerfaction.missions")) continue;
                    MethodInfo postfix = typeof(MissionChroniclePatch).GetMethod(nameof(ActivityPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                    Harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                    Log.Write("Mission native activity hook installed: " + name + " (" + kind + ").");
                }
                catch (Exception ex) { Log.Write("Mission activity hook failed for " + name + ": " + ex.Message); }
            }
        }

        private static void ActivityPostfix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                MissionManager manager = Manager;
                if (manager == null || __originalMethod == null) return;
                MissionKind kind;
                if (__originalMethod.Name == "TrainRobbed") kind = MissionKind.TrainRobbery;
                else if (__originalMethod.Name == "OpenStrongbox") kind = MissionKind.EnemyTreasury;
                else return;
                string key = __originalMethod.Name + "|" + Environment.TickCount + "|" + (__args == null ? 0 : __args.Length);
                manager.ObserveNativeActivity(kind, key);
            }
            catch (Exception ex) { Log.Write("Mission native activity callback warning: " + ex.Message); }
        }

        private static void Postfix(object[] __args)
        {
            try
            {
                MissionManager manager = Manager;
                if (manager == null || __args == null || __args.Length < 5) return;
                string tag = Convert.ToString(__args[0]);
                string title = Convert.ToString(__args[1]);
                string place = Convert.ToString(__args[2]);
                string gangId = Convert.ToString(__args[4]);
                string key = tag + "|" + title + "|" + place + "|" + gangId + "|" + Environment.TickCount;
                manager.ObserveChronicleEvent(tag, title, place, gangId, key);
            }
            catch (Exception ex) { Log.Write("Mission ChronicleDeed callback warning: " + ex.Message); }
        }

        internal static void Uninstall()
        {
            try { Harmony.UnpatchAll("enrage.playerfaction.missions"); } catch { }
            Manager = null;
        }
    }
}
