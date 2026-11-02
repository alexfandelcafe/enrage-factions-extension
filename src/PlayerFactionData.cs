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
                    if (!data.SettingsInitialized)
                    {
                        // Migrate v0.2.x saves to the values used by the previous hard-coded setup.
                        data.AllowTerritoryWars = true;
                        data.AllowFeuds = false;
                        data.AllowGangLifeEvents = false;
                        data.AllowAnswersGunfire = false;
                        data.SettingsInitialized = true;
                    }
                    data.SchemaVersion = Math.Max(11, data.SchemaVersion);
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
                SchemaVersion = 11,
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
                CampVisualTier = -1,
                RegardOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
