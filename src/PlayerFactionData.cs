using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace EnRAGEPlayerFactionExtension
{
    [DataContract]
    internal sealed class PlayerFactionData
    {
        [DataMember(Order = 1)] public bool Founded { get; set; }
        [DataMember(Order = 2)] public string Id { get; set; }
        [DataMember(Order = 3)] public string Name { get; set; }
        [DataMember(Order = 4)] public string LeaderName { get; set; }
        [DataMember(Order = 5)] public int SchemaVersion { get; set; }
        [DataMember(Order = 6)] public int FounderBlipHandle { get; set; }
        [DataMember(Order = 7)] public string LeaderDisplayName { get; set; }
        [DataMember(Order = 8)] public string CampSiteId { get; set; }
        [DataMember(Order = 9)] public string CampRegion { get; set; }
        [DataMember(Order = 10)] public float CampX { get; set; }
        [DataMember(Order = 11)] public float CampY { get; set; }
        [DataMember(Order = 12)] public float CampZ { get; set; }
        // Per-record manpower already applied to enRAGE's Chronicle.Men. This makes
        // synchronization incremental and prevents the same recruit being counted twice.
        [DataMember(Order = 13)] public Dictionary<string, int> RosterContributions { get; set; }
        // Last observed native enRAGE balances; these are mirrors, not a second economy.
        [DataMember(Order = 14)] public int CurrentMen { get; set; }
        [DataMember(Order = 15)] public int CurrentSupplies { get; set; }
        [DataMember(Order = 16)] public int CurrentTreasury { get; set; }
        // First synchronization explicitly seeds all enRAGE resources to zero.
        [DataMember(Order = 17)] public bool ResourcesInitialized { get; set; }
        // RDR2 BLIP_STYLE_MP_CAMP marker handle for the player's actual camp (not the founder marker).
        [DataMember(Order = 18)] public int CampBlipHandle { get; set; }
        [DataMember(Order = 19)] public bool TerritoryInitialized { get; set; }
        // User-customized tier labels are reapplied to enRAGE's native Gang.Tiers on registration.
        // Empty entries preserve enRAGE defaults. GangId and rosterId remain fixed identifiers.
        [DataMember(Order = 20)] public string[] RankLabels { get; set; }
        [DataMember(Order = 21)] public bool SettingsInitialized { get; set; }
        [DataMember(Order = 22)] public bool AllowTerritoryWars { get; set; }
        [DataMember(Order = 23)] public bool AllowFeuds { get; set; }
        [DataMember(Order = 24)] public bool AllowGangLifeEvents { get; set; }
        [DataMember(Order = 25)] public bool AllowAnswersGunfire { get; set; }
        // Runtime handle used to reconnect to the camp administrator after a ScriptHook reload.
        [DataMember(Order = 26)] public int CampManagerPedHandle { get; set; }
        // Exact roster record ID -> living world ped handle. Valid only in the process that created it.
        // This prevents spawning a second physical representative for the same saved roster member.
        [DataMember(Order = 27)] public Dictionary<string, int> CampRosterPedHandles { get; set; }
        [DataMember(Order = 28)] public int CampRosterPedProcessId { get; set; }
        // Existing roster entries at the moment a new band is founded are baseline records, not new recruits.
        // They do not contribute manpower until a new roster record is recruited after the band starts.
        [DataMember(Order = 29)] public List<string> UncountedRosterIds { get; set; }
        // One-time baseline marker. False on v0.3.4 saves so legacy inflated starting manpower is rebased once.
        [DataMember(Order = 30)] public bool ManpowerBaselineInitialized { get; set; }
        // Last camp-kit prestige tier persisted for diagnostics/migration. Visual cache itself belongs to enRAGE.
        [DataMember(Order = 31)] public int CampVisualTier { get; set; }
        // Per-roster member offset applied to the displayed/Companion "Hacia ti" value.
        [DataMember(Order = 32)] public Dictionary<string, int> RegardOffsets { get; set; }
        // Runtime handle for the separate mission-giver NPC; valid only in the process that created it.
        [DataMember(Order = 33)] public int MissionGiverPedHandle { get; set; }
        // 0 = none, 1 = mission objective (visit, interact or timed hold), 2 = return to camp, 3 = reward pending.
        [DataMember(Order = 34)] public int MissionStage { get; set; }
        [DataMember(Order = 35)] public float MissionTargetX { get; set; }
        [DataMember(Order = 36)] public float MissionTargetY { get; set; }
        [DataMember(Order = 37)] public float MissionTargetZ { get; set; }
        // Mission prestige is added by the extension's postfix to enRAGE's NotorietyOf for player_faction.
        [DataMember(Order = 38)] public int MissionPrestige { get; set; }
        [DataMember(Order = 39)] public int MissionsCompleted { get; set; }
        // Stable IDs that Companion's exact ped-to-roster cache has confirmed as actual recruits.
        [DataMember(Order = 40)] public List<string> ConfirmedRecruitIds { get; set; }
        // Stable mission IDs: 0 reconnaissance, 1 patrol, 2 cache, 3 stakeout, 4 courier, 5 convoy.
        [DataMember(Order = 41)] public int MissionType { get; set; }
        [DataMember(Order = 42)] public int MissionObjectiveIndex { get; set; }
        [DataMember(Order = 43)] public bool MissionObjectiveReady { get; set; }
        [DataMember(Order = 44)] public int MissionWaitProgressMs { get; set; }
        // Runtime entity handle for the visible supply cache; verified against model and coordinates on reconnect.
        [DataMember(Order = 45)] public int MissionCachePropHandle { get; set; }
        // Convoy handles are process-scoped and validated against model/location before reuse.
        [DataMember(Order = 46)] public int MissionConvoyWagonHandle { get; set; }
        [DataMember(Order = 47)] public List<int> MissionConvoyEnemyHandles { get; set; }
        [DataMember(Order = 48)] public int MissionDifficulty { get; set; }
        [DataMember(Order = 49)] public int LastMissionType { get; set; }
        [DataMember(Order = 50)] public bool MissionEncounterSpawned { get; set; }
        [DataMember(Order = 51)] public int MissionEncounterProcessId { get; set; }
        // Process-scoped handover contact used by courier/rescue contracts; validated by model and position.
        [DataMember(Order = 52)] public int MissionCourierPedHandle { get; set; }
        // Receipt and small write-ahead journal for idempotent mission resource awards across script reloads.
        [DataMember(Order = 54)] public string LastAppliedMissionRewardId { get; set; }
        [DataMember(Order = 55)] public string PendingMissionRewardId { get; set; }
        [DataMember(Order = 56)] public int PendingMissionRewardSuppliesBefore { get; set; }
        [DataMember(Order = 57)] public int PendingMissionRewardTreasuryBefore { get; set; }
        [DataMember(Order = 58)] public int PendingMissionRewardSuppliesAfter { get; set; }
        [DataMember(Order = 59)] public int PendingMissionRewardTreasuryAfter { get; set; }
        // Heading captured from a validated road node for roadside convoy/sabotage objectives.
        [DataMember(Order = 60)] public float MissionTargetHeading { get; set; }
        [DataMember(Order = 61)] public bool MissionTargetHasRoadNode { get; set; }
        // Visible/interactive map prop for reconnaissance, patrol and wolf-tracking clues.
        [DataMember(Order = 62)] public int MissionCluePropHandle { get; set; }
        // Mission-site set dressing props, stored by slot and validated against model/position on reuse.
        [DataMember(Order = 63)] public List<int> MissionScenePropHandles { get; set; }
        // Optional-objective bonus percent, persisted so the award survives a script reload.
        [DataMember(Order = 64)] public int MissionOptionalBonusPercent { get; set; }

        internal static PlayerFactionData Load()
        {
            string path = Log.DataPath("PlayerFaction.json");
            if (!File.Exists(path))
                return NewEmpty();

            try
            {
                var serializer = new DataContractJsonSerializer(typeof(PlayerFactionData));
                using (FileStream stream = File.OpenRead(path))
                {
                    PlayerFactionData data = serializer.ReadObject(stream) as PlayerFactionData;
                    if (data == null)
                        return NewEmpty();
                    // This extension's companion integration relies on this exact key.
                    // Never expose or accept a different GangId/RosterId in the faction menu.
                    data.Id = "player_faction";
                    if (string.IsNullOrWhiteSpace(data.Name))
                        data.Name = "La Banda";
                    if (string.IsNullOrWhiteSpace(data.LeaderName))
                        data.LeaderName = string.Empty;
                    if (data.LeaderDisplayName == null)
                        data.LeaderDisplayName = string.Empty;
                    if (data.CampSiteId == null)
                        data.CampSiteId = string.Empty;
                    if (data.CampRegion == null)
                        data.CampRegion = string.Empty;
                    if (data.RosterContributions == null)
                        data.RosterContributions = new Dictionary<string, int>();
                    if (data.RankLabels == null)
                        data.RankLabels = new string[0];
                    if (data.CampRosterPedHandles == null)
                        data.CampRosterPedHandles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (data.RegardOffsets == null)
                        data.RegardOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (data.UncountedRosterIds == null)
                        data.UncountedRosterIds = new List<string>();
                    if (data.ConfirmedRecruitIds == null)
                        data.ConfirmedRecruitIds = new List<string>();
                    if (data.MissionConvoyEnemyHandles == null)
                        data.MissionConvoyEnemyHandles = new List<int>();
                    if (data.SchemaVersion < 16)
                        data.LastMissionType = -1;
                    if (data.SchemaVersion < 17)
                        data.MissionCourierPedHandle = 0;
                    if (data.SchemaVersion < 18)
                    {
                        data.LastAppliedMissionRewardId = string.Empty;
                        data.PendingMissionRewardId = string.Empty;
                        data.PendingMissionRewardSuppliesBefore = 0;
                        data.PendingMissionRewardTreasuryBefore = 0;
                        data.PendingMissionRewardSuppliesAfter = 0;
                        data.PendingMissionRewardTreasuryAfter = 0;
                    }
                    if (data.LastAppliedMissionRewardId == null) data.LastAppliedMissionRewardId = string.Empty;
                    if (data.PendingMissionRewardId == null) data.PendingMissionRewardId = string.Empty;
                    if (data.SchemaVersion < 19)
                    {
                        data.MissionTargetHeading = 0f;
                        data.MissionTargetHasRoadNode = false;
                    }
                    if (data.SchemaVersion < 20) data.MissionCluePropHandle = 0;
                    if (data.MissionScenePropHandles == null) data.MissionScenePropHandles = new List<int>();
                    if (data.SchemaVersion < 21) data.MissionScenePropHandles.Clear();
                    if (data.SchemaVersion < 22) data.MissionOptionalBonusPercent = 0;
                    data.MissionOptionalBonusPercent = Math.Max(0, Math.Min(50, data.MissionOptionalBonusPercent));
                    if (!data.SettingsInitialized)
                    {
                        // Migrate v0.2.x saves to the values used by the previous hard-coded setup.
                        data.AllowTerritoryWars = true;
                        data.AllowFeuds = false;
                        data.AllowGangLifeEvents = false;
                        data.AllowAnswersGunfire = false;
                        data.SettingsInitialized = true;
                    }
                    data.SchemaVersion = Math.Max(22, data.SchemaVersion);
                    return data;
                }
            }
            catch (Exception ex)
            {
                Log.Write("LOAD PlayerFaction.json failed: " + ex);
                return NewEmpty();
            }
        }

        internal void Save()
        {
            string path = Log.DataPath("PlayerFaction.json");
            string temp = path + ".tmp";
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(PlayerFactionData));
                using (FileStream stream = File.Create(temp))
                    serializer.WriteObject(stream, this);

                if (File.Exists(path))
                {
                    string backup = path + ".bak";
                    try { File.Replace(temp, path, backup, true); }
                    catch
                    {
                        File.Delete(path);
                        File.Move(temp, path);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            catch (Exception ex)
            {
                Log.Write("SAVE PlayerFaction.json failed: " + ex);
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }

        internal static PlayerFactionData NewEmpty()
        {
            return new PlayerFactionData
            {
                Founded = false,
                Id = "player_faction",
                Name = "La Banda",
                LeaderName = string.Empty,
                FounderBlipHandle = 0,
                LeaderDisplayName = string.Empty,
                CampSiteId = string.Empty,
                CampRegion = string.Empty,
                RosterContributions = new Dictionary<string, int>(),
                SchemaVersion = 22,
                RankLabels = new string[0],
                CampRosterPedHandles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                CampRosterPedProcessId = 0,
                UncountedRosterIds = new List<string>(),
                ManpowerBaselineInitialized = false,
                SettingsInitialized = true,
                AllowTerritoryWars = true,
                AllowFeuds = false,
                AllowGangLifeEvents = false,
                AllowAnswersGunfire = false,
                CampManagerPedHandle = 0,
                MissionGiverPedHandle = 0,
                MissionStage = 0,
                MissionTargetX = 0f,
                MissionTargetY = 0f,
                MissionTargetZ = 0f,
                MissionPrestige = 0,
                MissionsCompleted = 0,
                MissionType = 0,
                MissionObjectiveIndex = 0,
                MissionObjectiveReady = false,
                MissionWaitProgressMs = 0,
                MissionCachePropHandle = 0,
                MissionConvoyWagonHandle = 0,
                MissionConvoyEnemyHandles = new List<int>(),
                MissionDifficulty = 0,
                LastMissionType = -1,
                MissionEncounterSpawned = false,
                MissionEncounterProcessId = 0,
                MissionCourierPedHandle = 0,
                LastAppliedMissionRewardId = string.Empty,
                PendingMissionRewardId = string.Empty,
                PendingMissionRewardSuppliesBefore = 0,
                PendingMissionRewardTreasuryBefore = 0,
                PendingMissionRewardSuppliesAfter = 0,
                PendingMissionRewardTreasuryAfter = 0,
                MissionTargetHeading = 0f,
                MissionTargetHasRoadNode = false,
                MissionCluePropHandle = 0,
                MissionScenePropHandles = new List<int>(),
                MissionOptionalBonusPercent = 0,
                ConfirmedRecruitIds = new List<string>(),
                CampVisualTier = -1,
                RegardOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
