using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using RDR2;
using RDR2.Math;
using RDR2.Native;

namespace EnRAGEPlayerFactionExtension
{
    internal sealed class PlayerFactionRuntime
    {
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const int FounderPromptControl = 1980406895; // INPUT_CONTEXT, same contextual action used by enRAGE.
        private const ulong DisableControlActionHash = 0xFE99B66D079CF6BCUL;
        private const ulong SetAmbientPromptGroupThisFrameHash = 0x315C81D760609108UL;
        private const ulong SetPedCanBeTargettedHash = 0x63F58F7C80513AADUL;
        private const ulong SetPedCanBeTargettedByPlayerHash = 0x66B57B72E0836A76UL;
        private const ulong SetEntityCanBeDamagedHash = 0x0D06D522B90E861FUL;
        private const ulong SetBlipNameHash = 11290983183885202434UL;
        // Generic POI is retained for the founder marker. Player camps use RDR2's native
        // BLIP_STYLE_MP_CAMP style, which is classified as PROPERTY and uses the camp icon.
        private const uint NativeMapBlipStyle = 2339406903U;
        private const string FounderPoiSprite = "blip_poi";
        private const string PlayerCampBlipStyle = "BLIP_STYLE_MP_CAMP";
        private const ulong IsControlJustPressedHash = 0x580417101DDB492FUL;
        private const ulong PromptGetGroupForEntityHash = 0xB796970BD125FCE8UL;
        private const ulong PromptHasHoldModeCompletedHash = 0xE0F65F0640EF0617UL;
        private const ulong PromptHasStandardModeCompletedHash = 0xC92AC953F0A982AEUL;
        private const ulong TaskFollowNavMeshToCoordHash = 0x15D3A79D4E44B913UL;
        // RDR2 native (not GTA V's six-argument native): coordinates + duration + four flags.
        private const ulong TaskUseNearestScenarioToCoordHash = 0x322BFDEA666E2B0EUL;
        private const ulong TaskCombatPedHash = 0xF166E48407BAC484UL;
        private const ulong PlayPedAmbientSpeechNativeHash = 0x8E04FEDD28D42462UL;
        private const ulong IsAmbientSpeechPlayingHash = 0x9072C8B49907BFADUL;
        private const ulong IsAnySpeechPlayingHash = 0x729072355FA39EC9UL;
        // Empty dictionary name asks RDR2 to use the ped's current ambient voice instead of
        // forcing every recruited member to speak with a sheriff's voice bank.
        private const string CampSpeechVoice = "";
        private const int ControlFrontendUp = unchecked((int)0x6319DB71);
        private const int ControlFrontendDown = unchecked((int)0x05CA7C52);
        private const int ControlFrontendAccept = unchecked((int)0xC7B5340A);
        private const int ControlFrontendCancel = unchecked((int)0x156F7119);

        // RDR2 INPUT_* control hashes. These are disabled only in the founder's immediate vicinity.
        private static readonly int[] ProtectedMeleeControls =
        {
            unchecked((int)0xB2F377E8), // INPUT_MELEE_ATTACK
            unchecked((int)0x2277FAE9), // INPUT_MELEE_GRAPPLE
            unchecked((int)0xADEAF48C), // INPUT_MELEE_GRAPPLE_ATTACK
            unchecked((int)0x018C47CF), // INPUT_MELEE_GRAPPLE_CHOKE
            unchecked((int)0x91C9A817), // INPUT_MELEE_GRAPPLE_REVERSAL
            unchecked((int)0xD0C1FEFF)  // INPUT_MELEE_GRAPPLE_BREAKOUT
        };

        private readonly ExtensionConfig _config;
        private readonly EnRAGEBridge _bridge;
        private PlayerFactionData _faction;
        private Ped _founder;
        private int _founderBlip;
        private int _founderPrompt;
        private int _promptGroup;
        private Ped _campManager;
        private int _campManagerPrompt;
        private int _campManagerPromptGroup;
        private int _nextCampManagerSpawnAt;
        private int _nextCampManagerProtectionAt;
        private int _nextCampManagerScenarioCheckAt; // retained for save/schema compatibility within the runtime class
        private CampboundRosterMember _campManagerIdle;
        private readonly Random _campScenarioRandom = new Random();
        private bool _campManagerPromptArmed = true;
        private int _nextAttachAt;
        private int _nextStartupBlipCleanupRetryAt;
        private bool _startupBlipCleanupComplete;
        private int _nextFactionCheckAt;
        private int _nextResourceSyncAt;
        private int _nextSpawnAt;
        private int _nextProtectionAt;
        private int _nextCampBlipAttemptAt;
        private int _nextCampPropsAttemptAt;
        private int _campBlip;
        private bool _campPropsRestoredThisRuntime;
        private bool _campTeleportPending;
        private int _campTeleportStartedAt;
        private Vector3 _pendingCampTeleportPosition;
        private string _pendingCampTeleportLabel = string.Empty;
        private bool _pendingCampTeleportForExistingFaction;
        private int _nextFounderScenarioCheckAt;
        private Type _uiDrawType;
        private MethodInfo _uiDrawRect;
        private MethodInfo _uiDrawText;
        private bool _uiDrawWarningLogged;
        private bool _promptArmed = true;
        private bool _keyboardOpen;
        private KeyboardMode _keyboardMode = KeyboardMode.None;
        private string _pendingFactionName = string.Empty;
        private bool _campSelectionActive;
        private bool _campSelectionForExisting;
        private int _campSelectionIndex;
        private bool _loggedMissingPromptApi;
        private bool _factionRegistered;
        private bool _factionMenuOpen;
        private int _nextCampVisualSyncAt;
        private int _nextCampDefenceScanAt;
        private int _nextCampConversationAt;
        private int _pendingCampInitiatorHandle;
        private int _pendingCampReplyHandle;
        private int _pendingCampReplyAt;
        private bool _speechWarningLogged;
        private FactionMenuPage _factionMenuPage = FactionMenuPage.None;
        private int _factionMenuIndex;
        private int _rankMenuIndex;
        private int _pendingRankIndex = -1;
        private List<string> _rankMenuLabels = new List<string>();
        private int _nextReleasedRosterScanAt;
        private int _nextRosterIntegrationWarningAt;
        private readonly Dictionary<int, CampboundRosterMember> _campboundRosterMembers = new Dictionary<int, CampboundRosterMember>();

        private sealed class CampboundRosterMember
        {
            internal int Handle;
            internal Ped Ped;
            internal string RosterId;
            internal Vector3 Destination;
            internal bool Arrived;
            internal bool AwaitingDismount;
            internal int NextPathCheckAt;
            internal float LastDistance;
            internal int LastProgressCheckAt;
            internal int NextScenarioCheckAt;
            internal int ScenarioAnchorIndex = -1;
            internal Vector3 ScenarioAnchorPosition;
            internal float ScenarioLastProgressDistance = float.MaxValue;
            internal int ScenarioLastProgressAt;
            internal int ScenarioAttemptStartedAt;
            internal bool ScenarioTaskPending;
            internal bool PendingObjectScenario;
            internal bool HasActiveCampScenario;
            internal bool UsingFallbackScenario;
            internal int FallbackScenarioIndex;
            internal int FallbackFailuresInRound;
            internal int DefensiveTargetHandle;
            internal int LastDefensiveContactAt;
            internal readonly HashSet<int> FailedScenarioAnchors = new HashSet<int>();
            internal readonly List<string> FallbackScenarioOrder = new List<string>();
        }

        private enum KeyboardMode
        {
            None,
            CreateFactionName,
            RenameLeader,
            RenameFactionName,
            RenameFactionRank
        }

        private enum FactionMenuPage
        {
            None,
            Main,
            Ranks
        }

        private sealed class CampOption
        {
            internal readonly string Label;
            internal readonly Vector3 Position;
            internal readonly string Description;
            internal CampOption(string label, string description, float x, float y, float z)
            {
                Label = label;
                Description = description;
                Position = new Vector3(x, y, z);
            }
        }

        // Coordinates taken from enRAGE's native Camps.cs locations. The player's
        // current position is deliberately excluded so the founder's Beecher's Hope
        // location can never be chosen accidentally as the new gang camp.
        private static readonly CampOption[] CampOptions =
        {
            new CampOption("Cumberland Forest", "Bosque al norte de Valentine, fuera del perímetro de Fort Wallace.", -250.0f, 1600.0f, 180.0f),
            new CampOption("Big Valley", "Praderas al oeste de Strawberry.", -1118.4f, -220.2f, 91.2f),
            new CampOption("Scarlett Meadows", "Campo abierto de Lemoyne.", 1173.2f, -2180.8f, 55.8f),
            new CampOption("Tall Trees", "Bosque al oeste de Blackwater.", -2014.5f, -1828.5f, 114f),
            new CampOption("Roanoke Ridge", "Bosque rocoso del noreste.", 2357.8f, 836.1f, 79.6f),
            new CampOption("Rio Bravo", "Desierto abierto de New Austin.", -5248.1f, -3851.5f, -2.1f),
            new CampOption("Grizzlies East", "Montanas del este de Ambarino.", 712.7f, 1882.3f, 239.1f)
        };

        internal PlayerFactionRuntime()
        {
            _config = ExtensionConfig.Load();
            _faction = PlayerFactionData.Load();

            // Ped handles survive a ScriptHook script reload in the same game process, but
            // must never be trusted after a complete game restart (RDR2 can reuse old handles).
            int currentProcessId = Process.GetCurrentProcess().Id;
            if (_faction.CampRosterPedProcessId != currentProcessId)
            {
                _faction.CampRosterPedHandles.Clear();
                _faction.CampManagerPedHandle = 0;
                _faction.CampRosterPedProcessId = currentProcessId;
                try { _faction.Save(); }
                catch (Exception ex) { Log.Write("Could not persist process-scoped camp ped handles: " + ex.Message); }
                Log.Write("Cleared stale camp NPC/administrator handles after a full game-process change; roster identities remain saved.");
            }

            _bridge = new EnRAGEBridge();
            FounderPatches.Runtime = this;
            // Native blip handles are removed on the first game tick, not in the constructor.
            // The previous behavior could run before the map/UI native state was ready.
            _startupBlipCleanupComplete = _faction.FounderBlipHandle == 0 && _faction.CampBlipHandle == 0;
            Log.Write("Player faction extension initialized. Founded=" + _faction.Founded + ", id=" + _faction.Id +
                ". Startup blip cleanup pending=" + (!_startupBlipCleanupComplete) + ".");
        }

        internal void Tick()
        {
            if (!_startupBlipCleanupComplete)
            {
                if (Game.GameTime < _nextStartupBlipCleanupRetryAt) return;
                _nextStartupBlipCleanupRetryAt = Game.GameTime + 250;
                _startupBlipCleanupComplete = CleanupPersistedBlipsOnReload();
                if (!_startupBlipCleanupComplete) return;
            }

            if (Game.GameTime >= _nextAttachAt && !_bridge.IsAvailable)
            {
                _nextAttachAt = Game.GameTime + 1500;
                _bridge.TryAttach();
            }
            if (!_bridge.IsAvailable)
                return;

            // Companion may load after enRAGE/the extension. Retry both hooks and migrate exact cached
            // Companion records to the fixed faction once that assembly becomes available.
            _bridge.TryInstallCompanionIntegrationPatches();
            _bridge.NormalizeCompanionPlayerFactionRoster();

            // Companion's exact handle-to-roster cache is polled for every living player_faction member
            // not currently following. Existing handles are reused; after a game restart, a missing world
            // entity can be re-materialized from its same persistent roster ID and saved outfit.
            if (_faction.Founded && _factionRegistered && Game.GameTime >= _nextReleasedRosterScanAt)
            {
                _nextReleasedRosterScanAt = Game.GameTime + 1000;
                MaintainReleasedRosterMembersReturningToCamp();
            }

            // Keyboard updates are processed before the founded/unfounded branches so
            // renaming the leader works while the faction is already active.
            if (_campTeleportPending)
            {
                UpdateCampTeleport();
                return;
            }
            if (_campSelectionActive)
            {
                UpdateCampLocationSelector();
                return;
            }
            if (_keyboardOpen)
            {
                UpdateKeyboard();
                return;
            }
            if (_factionMenuOpen)
            {
                UpdateFactionMenu();
                return;
            }
            if (_faction.Founded)
            {
                FounderPatches.FounderHandle = 0;
                RemovePrompt();
                RemoveFounderBlip();
                DeleteFounder();
                if (_factionRegistered)
                {
                    bool obsoleteLiveCampRemoved = _bridge.MaintainPlayerFactionNeutrality(_faction);
                    if (obsoleteLiveCampRemoved)
                    {
                        _campPropsRestoredThisRuntime = false;
                        _nextCampPropsAttemptAt = 0;
                        Log.Write("A stale hostile enRAGE camp was removed; requesting a clean rebuild of the player's own CampKit.");
                    }
                }
                // Do not recreate the camp marker from PlayerFaction.json alone. It must wait until
                // FindPlayerCampSite confirms that enRAGE has restored the actual native CampSite.
                if (Game.GameTime >= _nextFactionCheckAt)
                {
                    string previousLeaderName = _faction.LeaderName;
                    string previousCampSiteId = _faction.CampSiteId;
                    string previousCampRegion = _faction.CampRegion;
                    string error;
                    // enRAGE loads camps.json before this extension registers player_faction.
                    // Retry discovery quickly until our saved CampSite has been restored.
                    _factionRegistered = _bridge.EnsurePlayerFaction(_faction, out error);
                    if (!_factionRegistered)
                    {
                        _nextFactionCheckAt = Game.GameTime + 1500;
                        Log.Write("Player faction registration retry failed: " + error);
                    }
                    else
                    {
                        string campError;
                        bool hasNativeCamp = _bridge.FindPlayerCampSite(_faction, out campError);
                        _nextFactionCheckAt = Game.GameTime + (hasNativeCamp ? 10000 : 1500);
                        if (!hasNativeCamp && !string.IsNullOrWhiteSpace(campError))
                            Log.Write("Player faction camp verification failed: " + campError);
                        if (hasNativeCamp &&
                            (!string.Equals(previousCampSiteId, _faction.CampSiteId, StringComparison.OrdinalIgnoreCase) ||
                             !string.Equals(previousCampRegion, _faction.CampRegion, StringComparison.OrdinalIgnoreCase)))
                        {
                            try { _faction.Save(); }
                            catch (Exception saveEx) { Log.Write("Could not persist restored native camp site: " + saveEx.Message); }
                        }
                        if (!string.Equals(previousLeaderName, _faction.LeaderName, StringComparison.Ordinal))
                        {
                            try { _faction.Save(); }
                            catch (Exception saveEx) { Log.Write("Could not persist native player leader name: " + saveEx.Message); }
                        }
                        if (hasNativeCamp) EnsureCampBlip();
                    }
                }

                Ped campPlayer = Game.Player.Character;
                if (_factionRegistered && campPlayer != null && campPlayer.Exists() && Game.GameTime >= _nextCampVisualSyncAt)
                {
                    _nextCampVisualSyncAt = Game.GameTime + 5000;
                    bool physicalKitRebuilt;
                    string visualError;
                    if (_bridge.SyncPlayerCampVisualTier(_faction, campPlayer, out physicalKitRebuilt, out visualError))
                    {
                        if (physicalKitRebuilt)
                        {
                            _campPropsRestoredThisRuntime = false;
                            _nextCampPropsAttemptAt = 0;
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(visualError))
                        Log.Write("Camp prestige visual sync pending: " + visualError);
                }
                if (_factionRegistered && campPlayer != null && campPlayer.Exists() && Game.GameTime >= _nextCampPropsAttemptAt)
                {
                    _nextCampPropsAttemptAt = Game.GameTime + 2500;
                    string propsError;
                    bool campPropsReady = _bridge.EnsurePlayerCampProps(_faction, campPlayer,
                        !_campPropsRestoredThisRuntime, out propsError);
                    if (campPropsReady)
                        _campPropsRestoredThisRuntime = true;
                    if (!string.IsNullOrWhiteSpace(propsError)) Log.Write("Camp props pending/retry: " + propsError);
                }
                if (_factionRegistered && Game.GameTime >= _nextResourceSyncAt)
                {
                    _nextResourceSyncAt = Game.GameTime + 1000;
                    string resourceError;
                    _bridge.SyncFactionResources(_faction, out resourceError);
                    if (!string.IsNullOrEmpty(resourceError)) Log.Write("Resource sync warning: " + resourceError);
                }

                if (_factionRegistered && campPlayer != null && campPlayer.Exists() && !campPlayer.IsDead)
                {
                    MaintainCampManager(campPlayer);
                    MaintainCampDefence(campPlayer);
                    MaintainCampAmbientConversation(campPlayer);
                }
                else
                {
                    SetPromptVisible(_campManagerPrompt, false);
                    RemoveCampManagerPrompt();
                }
                return;
            }

            EnsureFounderBlip();
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return;

            float distanceToSpawn = player.Position.DistanceTo(new Vector3(_config.FounderX, _config.FounderY, _config.FounderZ));
            if (_founder == null || !_founder.Exists())
            {
                FounderPatches.FounderHandle = 0;
                RemovePrompt();
                if (distanceToSpawn <= _config.SpawnDistance && Game.GameTime >= _nextSpawnAt)
                {
                    _nextSpawnAt = Game.GameTime + 3000;
                    SpawnFounder();
                }
                return;
            }

            MaintainFounder(player);
            float distance = player.Position.DistanceTo(_founder.Position);
            if (distance <= _config.InteractionDistance + 1.5f)
                DisableHostileMeleeControls();

            if (distance <= 7.0f && !player.IsOnMount && !player.IsInVehicle && !_keyboardOpen)
                UpdateFounderPrompt(player);
            else
                SetPromptVisible(false);

            if (_keyboardOpen)
                UpdateKeyboard();
        }

        internal void OnScriptAborted()
        {
            // ScriptHook can reload scripts without restarting the game. Native prompts and
            // Harmony patches outlive this managed Script instance unless explicitly released.
            _factionMenuOpen = false;
            _keyboardOpen = false;
            _campSelectionActive = false;
            _campTeleportPending = false;
            try { SetPromptVisible(_founderPrompt, false); } catch { }
            try { SetPromptVisible(_campManagerPrompt, false); } catch { }
            RemoveCampManagerPrompt();
            RemovePrompt();
            bool ownsSharedHooks = object.ReferenceEquals(FounderPatches.Runtime, this);
            if (ownsSharedHooks)
            {
                _bridge.UninstallHarmonyPatches();
                FounderPatches.Runtime = null;
                FounderPatches.FounderHandle = 0;
                FounderPatches.MainInstance = null;
                FounderPatches.PlayerFactionId = null;
                FounderPatches.PlayerRecordId = null;
                FounderPatches.ResetUiContexts();
            }
            _founderPrompt = 0;
            _campManagerPrompt = 0;
            _promptGroup = 0;
            _campManagerPromptGroup = 0;
            Log.Write("Script-aborted cleanup complete: both interaction prompts released; " +
                (ownsSharedHooks ? "extension Harmony hooks uninstalled" : "newer runtime owns shared hooks") +
                "; camp NPC and saved blips left intact.");
        }

        internal void ChangePlayerFactionMemberRank(object record, int delta)
        {
            int oldRank, newRank;
            string error;
            if (!_bridge.ChangePlayerFactionMemberRank(record, delta, out oldRank, out newRank, out error))
            {
                Notify("No se pudo cambiar el rango. " + (string.IsNullOrWhiteSpace(error) ? "Comprueba el registro de la banda." : error));
                Log.Write("Roster rank change failed: " + error);
                return;
            }
            try { _faction.Save(); } catch (Exception ex) { Log.Write("Player faction save after rank change warning: " + ex.Message); }
            string name = Convert.ToString(ReadRecordMember(record, "Name")) ?? "El miembro";
            Notify(name + (newRank > oldRank ? " fue ascendido." : " fue degradado."));
            Log.Write("Player faction member rank changed: " + name + " " + oldRank + " -> " + newRank + ".");
        }

        private static object ReadRecordMember(object record, string name)
        {
            if (record == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = record.GetType().GetField(name, flags);
            if (field != null) return field.GetValue(record);
            PropertyInfo property = record.GetType().GetProperty(name, flags);
            return property != null ? property.GetValue(record, null) : null;
        }

        internal void PreparePlayerFactionForCampDiscovery()
        {
            if (!_faction.Founded) return;
            // Reconcile the stable extcomp_/extvdl_ record IDs before marking their actual handles.
            _bridge.NormalizeCompanionPlayerFactionRoster();
            _bridge.MarkKnownPlayerFactionPedsAsEnRageOwned(_faction);
            _bridge.MaintainPlayerFactionNeutrality(_faction);
        }

        internal bool IsPlayerCampSiteId(string siteId)
        {
            return _faction != null && !string.IsNullOrWhiteSpace(siteId) &&
                   string.Equals(_faction.CampSiteId, siteId, StringComparison.OrdinalIgnoreCase);
        }

        internal bool IsValidCampManagerHandle(Ped ped)
        {
            if (ped == null || !ped.Exists() || ped.IsDead || _faction == null || _bridge == null) return false;
            try
            {
                if (_campManager != null && _campManager.Exists() && _campManager.Handle == ped.Handle) return true;
                Vector3 centre = _bridge.GetPlayerCampCentre(_faction);
                if (centre == Vector3.Zero || ped.Position.DistanceTo(centre) > 28f) return false;
                uint model = unchecked((uint)ped.Model.Hash);
                string[] knownModels =
                {
                    _config.FounderModel,
                    "U_M_M_Rwfrancher_01",
                    "U_M_M_RhdBartender_01",
                    "U_M_M_ValTownfolk_01"
                };
                return knownModels.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(MISC.GET_HASH_KEY).Contains(model);
            }
            catch { return false; }
        }

        internal int GetPlayerFactionRegard(string rosterId, int rank)
        {
            int[] baseline = { -5, 15, 35, 65, 90 };
            int value = baseline[Math.Max(0, Math.Min(baseline.Length - 1, rank - 1))];
            int offset;
            if (_faction != null && _faction.RegardOffsets != null && !string.IsNullOrWhiteSpace(rosterId) &&
                _faction.RegardOffsets.TryGetValue(rosterId, out offset)) value += offset;
            return Math.Max(-100, Math.Min(100, value));
        }

        internal bool IsCampManagerSocialTarget(int handle, Ped player)
        {
            return _campManager != null && _campManager.Exists() && _campManager.Handle == handle &&
                   player != null && player.Exists() && _campManager.Position.DistanceTo(player.Position) <= 3.5f;
        }

        internal bool IsPlayerFactionRosterPed(int handle)
        {
            if (handle == 0 || _faction == null) return false;
            if (_campboundRosterMembers.ContainsKey(handle)) return true;

            // Handles are persisted with the roster records and remain valid across a ScriptHook
            // script reload in the same game process. Use this map before the camp AI dictionary
            // has been rebuilt so Companion's first post-reload maintenance pass cannot disable talk.
            return _faction.CampRosterPedHandles != null &&
                   _faction.CampRosterPedHandles.Values.Contains(handle);
        }

        internal bool IsCampMemberSocialTarget(int handle, Ped player)
        {
            if (handle == 0 || player == null || !player.Exists() ||
                _factionMenuOpen || _keyboardOpen || _campManager != null && _campManager.Exists() && _campManager.Handle == handle)
                return false;
            CampboundRosterMember member;
            if (!_campboundRosterMembers.TryGetValue(handle, out member) || member == null || member.Ped == null ||
                !member.Ped.Exists() || member.Ped.IsDead || !member.Arrived || member.DefensiveTargetHandle != 0)
                return false;
            return member.Ped.Position.DistanceTo(player.Position) <= 3.5f;
        }

        internal bool DrawPlayerFactionPrestigeBand(float y, int value)
        {
            if (!ResolveUiDrawMethods()) return false;
            value = Math.Max(0, Math.Min(100, value));
            string[] levels = { "DESCONOCIDA", "EMERGENTE", "RECONOCIDA", "TEMIDA", "LEGENDARIA" };
            int level = Math.Min(4, value / 20);
            int nextThreshold = Math.Min(100, (level + 1) * 20);
            string progress = level == 4
                ? value + " / 100  -  PRESTIGIO MAXIMO"
                : value + " / 100  -  " + levels[level + 1] + " A LOS " + nextThreshold;
            int[][] colors =
            {
                new[] { 145, 145, 145 },
                new[] { 150, 174, 190 },
                new[] { 186, 170, 120 },
                new[] { 195, 132, 88 },
                new[] { 214, 174, 93 }
            };
            int[] color = colors[level];
            DrawMenuRect(0.07f, y, 0.86f, 0.032f, 23, 22, 20, 235);
            DrawMenuRect(0.07f, y + 0.030f, 0.86f, 0.0015f, 90, 78, 60, 210);
            DrawMenuText("PRESTIGIO", 0.088f, y + 0.008f, 0.19f, 205, 166, 105, 255);
            DrawMenuText(levels[level], 0.205f, y + 0.006f, 0.215f, color[0], color[1], color[2], 255);
            DrawMenuRect(0.355f, y + 0.012f, 0.225f, 0.0065f, 48, 46, 42, 255);
            if (value > 0)
                DrawMenuRect(0.355f, y + 0.012f, 0.225f * value / 100f, 0.0065f, color[0], color[1], color[2], 255);
            DrawMenuText(progress, 0.595f, y + 0.008f, 0.185f, 210, 205, 192, 255);
            return true;
        }

        private void SpawnFounder()
        {
            try
            {
                Vector3 position = new Vector3(_config.FounderX, _config.FounderY, _config.FounderZ);
                Ped ped = _bridge.SpawnFounder(_config.FounderModel, position, _config.FounderHeading);
                if (ped == null || !ped.Exists())
                {
                    Log.Write("Founder spawn returned no Ped after configured and fallback models. ConfiguredModel=" + _config.FounderModel + ", distance=" + Game.Player.Character.Position.DistanceTo(position) + ".");
                    return;
                }

                _founder = ped;
                FounderPatches.FounderHandle = ped.Handle;
                _promptArmed = true;
                StartFounderGuitarScenario(ped);
                ApplyFounderProtection(Game.Player.Character);
                Log.Write("Founder spawned. Handle=" + ped.Handle + " at " + position + ".");
            }
            catch (Exception ex)
            {
                Log.Write("SpawnFounder exception: " + ex);
            }
        }

        private void MaintainFounder(Ped player)
        {
            if (_founder == null || !_founder.Exists())
                return;

            FounderPatches.FounderHandle = _founder.Handle;
            EnsureFounderGuitarScenario();
            if (Game.GameTime < _nextProtectionAt)
                return;
            _nextProtectionAt = Game.GameTime + 500;
            ApplyFounderProtection(player);
        }

        private void ApplyFounderProtection(Ped player)
        {
            ApplyProtectedAmbientPed(_founder, player, "Founder");
        }

        private void ApplyProtectedAmbientPed(Ped protectedPed, Ped player, string role)
        {
            if (protectedPed == null || !protectedPed.Exists())
                return;

            try
            {
                ENTITY.SET_ENTITY_INVINCIBLE(protectedPed.Handle, true);
                Function.Call(SetEntityCanBeDamagedHash, new InputArgument[] { protectedPed.Handle, false });
                Function.Call(SetPedCanBeTargettedHash, new InputArgument[] { protectedPed.Handle, false });
                if (player != null && player.Exists())
                    Function.Call(SetPedCanBeTargettedByPlayerHash, new InputArgument[] { protectedPed.Handle, Game.Player.Handle, false });

                protectedPed.BlockPermanentEvents = true;
                PED.SET_PED_CONFIG_FLAG(protectedPed.Handle, 130, false);
                PED.SET_PED_COMBAT_ATTRIBUTES(protectedPed.Handle, 5, false);
                PED.SET_PED_COMBAT_ATTRIBUTES(protectedPed.Handle, 46, false);
                if (player != null && player.Exists())
                {
                    uint playerGroup = player.RelationshipGroup;
                    protectedPed.RelationshipGroup = playerGroup;
                    World.SetRelationshipBetweenGroups(0, protectedPed.RelationshipGroup, playerGroup);
                    World.SetRelationshipBetweenGroups(0, playerGroup, protectedPed.RelationshipGroup);
                }
            }
            catch (Exception ex)
            {
                Log.Write(role + " protection warning: " + ex.Message);
            }
        }

        private void DisableHostileMeleeControls()
        {
            foreach (int control in ProtectedMeleeControls)
            {
                try
                {
                    Function.Call(DisableControlActionHash, new InputArgument[] { 0, control, true });
                }
                catch (Exception ex)
                {
                    Log.Write("Control suppression warning: " + ex.Message);
                }
            }
        }

        private unsafe bool CleanupPersistedBlipsOnReload()
        {
            bool allRemoved = true;
            int[] handles = { _faction.FounderBlipHandle, _faction.CampBlipHandle };
            foreach (int handle in handles.Distinct())
            {
                if (handle == 0) continue;
                try
                {
                    if (MAP.DOES_BLIP_EXIST(handle))
                    {
                        int nativeHandle = handle;
                        MAP.REMOVE_BLIP(&nativeHandle);
                    }

                    // Keep the persisted handle until the native confirms removal. This lets the
                    // next tick retry instead of silently forgetting an orphaned map marker.
                    if (MAP.DOES_BLIP_EXIST(handle))
                    {
                        allRemoved = false;
                        Log.Write("Startup blip cleanup pending; native handle still exists: " + handle + ".");
                    }
                    else
                    {
                        Log.Write("Startup blip cleanup confirmed for handle " + handle + ".");
                    }
                }
                catch (Exception ex)
                {
                    allRemoved = false;
                    Log.Write("Startup blip cleanup will retry for handle " + handle + ": " + ex.Message);
                }
            }

            if (!allRemoved) return false;

            _founderBlip = 0;
            _campBlip = 0;
            _faction.FounderBlipHandle = 0;
            _faction.CampBlipHandle = 0;
            try { _faction.Save(); }
            catch (Exception ex)
            {
                Log.Write("Startup blip handle clear/save warning: " + ex.Message);
                // Removal already happened. The old handles may remain on disk if the write failed;
                // the next script initialization will attempt this cleanup again.
            }
            Log.Write("Startup blip cleanup complete; camp marker creation remains gated on native CampSite recovery.");
            return true;
        }

        private void EnsureFounderBlip()
        {
            if (_founderBlip != 0 && MAP.DOES_BLIP_EXIST(_founderBlip))
                return;

            // A previous handle is cleaned in the constructor before this marker is recreated.
            if (_faction.FounderBlipHandle != 0)
            {
                if (MAP.DOES_BLIP_EXIST(_faction.FounderBlipHandle))
                {
                    _founderBlip = _faction.FounderBlipHandle;
                    return;
                }
                _faction.FounderBlipHandle = 0;
            }

            try
            {
                _founderBlip = MAP.BLIP_ADD_FOR_COORDS(NativeMapBlipStyle,
                    _config.FounderX, _config.FounderY, _config.FounderZ);
                if (_founderBlip != 0)
                {
                    MAP.SET_BLIP_SPRITE(_founderBlip, MISC.GET_HASH_KEY("blip_poi"), true);
                    TrySetBlipName(_founderBlip, "Fundador - Beecher's Hope");
                    _faction.FounderBlipHandle = _founderBlip;
                    try { _faction.Save(); } catch (Exception saveEx) { Log.Write("Founder blip handle save warning: " + saveEx.Message); }
                    Log.Write("Founder neutral POI blip created: " + _founderBlip + ".");
                }
            }
            catch (Exception ex)
            {
                Log.Write("Founder blip creation failed: " + ex);
            }
        }

        private void TrySetBlipName(int handle, string text)
        {
            try
            {
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetType("NemesisSystem.UiDraw", false) != null);
                if (assembly == null) return;
                Type uiDraw = assembly.GetType("NemesisSystem.UiDraw", false);
                MethodInfo forever = uiDraw.GetMethods(StaticFlags).FirstOrDefault(m => m.Name == "ForeverVarString" &&
                    m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                if (forever == null) return;
                ulong label = Convert.ToUInt64(forever.Invoke(null, new object[] { text }));
                Function.Call(SetBlipNameHash, new InputArgument[] { handle, new InputArgument(label) });
            }
            catch (Exception ex)
            {
                Log.Write("Blip label was not applied: " + ex.Message);
            }
        }

        private unsafe void EnsureCampBlip(bool forceRefresh = false)
        {
            // A saved camp must always get its native camp blip back after a script reload.
            // The constructor removes the stale handle; this method creates a fresh one from
            // the persisted CampSite coordinates as soon as the site has been recovered.
            if (!_faction.Founded || string.IsNullOrWhiteSpace(_faction.CampSiteId)) return;
            if (Game.GameTime < _nextCampBlipAttemptAt && !forceRefresh) return;
            _nextCampBlipAttemptAt = Game.GameTime + 5000;
            try
            {
                if (forceRefresh)
                {
                    int oldHandle = _campBlip != 0 ? _campBlip : _faction.CampBlipHandle;
                    if (oldHandle != 0 && MAP.DOES_BLIP_EXIST(oldHandle))
                    {
                        int removeHandle = oldHandle;
                        MAP.REMOVE_BLIP(&removeHandle);
                    }
                    _campBlip = 0;
                    _faction.CampBlipHandle = 0;
                }
                if (!forceRefresh && _campBlip != 0 && MAP.DOES_BLIP_EXIST(_campBlip)) return;
                int saved = _faction.CampBlipHandle;
                if (!forceRefresh && saved != 0 && MAP.DOES_BLIP_EXIST(saved))
                {
                    _campBlip = saved;
                    return;
                }

                // Don't leave a stale persisted handle if RDR2 invalidated it on a new session.
                _campBlip = 0;
                _faction.CampBlipHandle = 0;
                uint campStyle = MISC.GET_HASH_KEY(PlayerCampBlipStyle);
                int handle = MAP.BLIP_ADD_FOR_COORDS(campStyle, _faction.CampX, _faction.CampY, _faction.CampZ);
                if (handle == 0)
                {
                    Log.Write("Camp POI blip creation returned 0; will retry.");
                    return;
                }
                _campBlip = handle;
                _faction.CampBlipHandle = handle; // Persist only so the next script initialization can remove it.
                try { _faction.Save(); } catch (Exception saveEx) { Log.Write("Camp blip handle save warning: " + saveEx.Message); }
                TrySetBlipName(handle, "Campamento - " + _faction.Name);
                Log.Write("RDR2 BLIP_STYLE_MP_CAMP created (PROPERTY / BLIP_CAMP): " + handle + " at " + _faction.CampX + ", " + _faction.CampY + ", " + _faction.CampZ + ".");
            }
            catch (Exception ex)
            {
                Log.Write("Camp POI blip creation failed; will retry: " + ex);
            }
        }

        private void UpdateFounderPrompt(Ped player)
        {
            if (_founder == null || !_founder.Exists()) return;

            int group;
            if (!TryGetPromptGroup(_founder, out group))
            {
                if (!_loggedMissingPromptApi)
                {
                    _loggedMissingPromptApi = true;
                    Log.Write("enRAGE prompt API unavailable; founder interaction will be retried.");
                }
                return;
            }

            if (_founderPrompt == 0 || _promptGroup != group || !PromptValid(_founderPrompt))
            {
                RemovePrompt();
                if (!TryCreatePrompt(group)) return;
            }

            SetPromptVisible(_founderPrompt, true);
            ActivateAmbientPromptGroup(_founder, _founderPrompt, _promptGroup, "Fundador", "Founder");
            bool completed = PromptHoldCompleted(_founderPrompt);
            if (!completed) { _promptArmed = true; return; }
            if (!_promptArmed) return;

            _promptArmed = false;
            SetPromptVisible(_founderPrompt, false);
            BeginNameKeyboard();
        }

        private void UpdateCampManagerPrompt(Ped player)
        {
            if (_campManager == null || !_campManager.Exists()) return;
            int group;
            if (!TryGetCampManagerPromptGroup(_campManager, out group)) return;

            if (_campManagerPrompt == 0 || _campManagerPromptGroup != group || !PromptValid(_campManagerPrompt))
            {
                RemoveCampManagerPrompt();
                if (!TryCreateCampManagerPrompt(group)) return;
            }

            SetPromptVisible(_campManagerPrompt, true);
            ActivateAmbientPromptGroup(_campManager, _campManagerPrompt, _campManagerPromptGroup,
                "Administrador de la banda", "Camp administrator");
            bool completed = PromptHoldCompleted(_campManagerPrompt);
            bool contextPressed = JustPressed(FounderPromptControl);
            if (!completed && !contextPressed) { _campManagerPromptArmed = true; return; }
            if (!_campManagerPromptArmed && !contextPressed) return;

            _campManagerPromptArmed = false;
            SetPromptVisible(_campManagerPrompt, false);
            Log.Write("Camp manager prompt completed (native=" + completed + ", contextFallback=" + contextPressed + ").");
            OpenFactionMenu();
        }

        private bool TryGetPromptGroup(Ped target, out int group)
        {
            group = 0;
            if (target == null || !target.Exists()) return false;
            try
            {
                // enRAGE and Companion both attach MakePrompt prompts to the entity's real
                // interaction-lock-on group. A synthetic hash makes the prompt visible but can
                // prevent its hold completion from being routed to this ped.
                group = Function.Call<int>(PromptGetGroupForEntityHash, new InputArgument[] { target.Handle });
                if (group == 0)
                    Log.Write("Target prompt group returned 0; using the native default prompt group.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Target prompt group lookup failed: " + ex.Message);
                return false;
            }
        }

        private bool TryGetCampManagerPromptGroup(Ped target, out int group)
        {
            return TryGetPromptGroup(target, out group);
        }

        private void ActivateAmbientPromptGroup(Ped ped, int prompt, int group, string displayName, string role)
        {
            if (ped == null || !ped.Exists() || prompt == 0) return;
            try
            {
                Type uiDraw = _bridge.MainInstance != null
                    ? _bridge.MainInstance.GetType().Assembly.GetType("NemesisSystem.UiDraw", false)
                    : null;
                if (uiDraw == null)
                {
                    Assembly enrage = AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetType("NemesisSystem.UiDraw", false) != null);
                    if (enrage != null) uiDraw = enrage.GetType("NemesisSystem.UiDraw", false);
                }
                MethodInfo foreverVarString = uiDraw != null
                    ? uiDraw.GetMethods(StaticFlags).FirstOrDefault(m => m.Name == "ForeverVarString" &&
                        m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string))
                    : null;
                if (foreverVarString == null) return;

                ulong label = Convert.ToUInt64(foreverVarString.Invoke(null, new object[] { displayName }));
                Function.Call(SetAmbientPromptGroupThisFrameHash, new InputArgument[]
                {
                    ped.Handle,
                    _config.InteractionDistance + 0.5f,
                    2,
                    0,
                    group,
                    new InputArgument(label),
                    0
                });
            }
            catch (Exception ex) { Log.Write(role + " ambient prompt group warning: " + ex.Message); }
        }

        private bool TryCreatePrompt(int group)
        {
            int prompt;
            if (!TryCreatePromptFor(group, _config.FounderPrompt, out prompt)) return false;
            _founderPrompt = prompt;
            _promptGroup = group;
            return true;
        }

        private bool TryCreateCampManagerPrompt(int group)
        {
            int prompt;
            if (!TryCreatePromptFor(group, "Administrar la facción", out prompt)) return false;
            _campManagerPrompt = prompt;
            _campManagerPromptGroup = group;
            _campManagerPromptArmed = false;
            Log.Write("Camp manager interaction prompt created. Prompt=" + prompt + ", group=" + group + ".");
            return true;
        }

        private bool TryCreatePromptFor(int group, string label, out int prompt)
        {
            prompt = 0;
            try
            {
                Type mainType = _bridge.MainInstance != null ? _bridge.MainInstance.GetType() : null;
                if (mainType == null)
                    mainType = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("NemesisSystem.NemesisMod", false)).FirstOrDefault(t => t != null);
                if (mainType == null) return false;
                MethodInfo make = mainType.GetMethods(StaticFlags).FirstOrDefault(m => m.Name == "MakePrompt" && m.GetParameters().Length == 4);
                if (make == null) return false;
                Type controlType = make.GetParameters()[0].ParameterType;
                object control = controlType.IsEnum ? Enum.ToObject(controlType, FounderPromptControl) : (object)FounderPromptControl;
                prompt = Convert.ToInt32(make.Invoke(null, new object[] { control, label, true, group }));
                return prompt != 0;
            }
            catch (Exception ex)
            {
                Log.Write("Prompt creation failed for '" + label + "': " + ex);
                return false;
            }
        }

        private bool PromptValid(int prompt)
        {
            try
            {
                Type mainType = _bridge.MainInstance != null ? _bridge.MainInstance.GetType() : null;
                if (mainType == null)
                    mainType = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("NemesisSystem.NemesisMod", false)).FirstOrDefault(t => t != null);
                MethodInfo method = mainType != null
                    ? mainType.GetMethods(StaticFlags).FirstOrDefault(m => m.Name == "PromptValid" && m.GetParameters().Length == 1)
                    : null;
                return method != null && Convert.ToBoolean(method.Invoke(null, new object[] { prompt }));
            }
            catch { return false; }
        }

        private bool PromptHoldCompleted(int prompt)
        {
            if (prompt == 0 || !PromptValid(prompt)) return false;
            try
            {
                // Companion uses hold-mode prompts. Check that native first, then accept the
                // standard-mode completion too in case enRAGE rebuilt the prompt after a reload
                // with a different mode flag in the current ScriptHook build.
                if (Function.Call<bool>(PromptHasHoldModeCompletedHash, new InputArgument[] { prompt }))
                    return true;
            }
            catch (Exception ex) { Log.Write("Native hold-prompt completion check warning: " + ex.Message); }
            try
            {
                return Function.Call<bool>(PromptHasStandardModeCompletedHash, new InputArgument[] { prompt });
            }
            catch (Exception ex)
            {
                Log.Write("Native standard-prompt completion check warning: " + ex.Message);
                return false;
            }
        }

        private void SetPromptVisible(bool visible)
        {
            SetPromptVisible(_founderPrompt, visible);
        }

        private void SetPromptVisible(int prompt, bool visible)
        {
            if (prompt == 0) return;
            try
            {
                Type hud = typeof(Game).Assembly.GetType("RDR2.Native.HUD", false);
                if (hud == null) return;
                InvokeHud(hud, "_UI_PROMPT_SET_VISIBLE", prompt, visible);
                InvokeHud(hud, "_UI_PROMPT_SET_ENABLED", prompt, visible);
            }
            catch (Exception ex) { Log.Write("Prompt visibility warning: " + ex.Message); }
        }

        private static void InvokeHud(Type hud, string name, params object[] args)
        {
            MethodInfo method = hud.GetMethods(StaticFlags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length);
            if (method != null) method.Invoke(null, args);
        }

        private void RemovePrompt()
        {
            if (_founderPrompt == 0) return;
            try
            {
                Type hud = typeof(Game).Assembly.GetType("RDR2.Native.HUD", false);
                if (hud != null) InvokeHud(hud, "_UI_PROMPT_DELETE", _founderPrompt);
            }
            catch (Exception ex) { Log.Write("Prompt delete warning: " + ex.Message); }
            _founderPrompt = 0;
            _promptGroup = 0;
            _promptArmed = true;
        }

        private void RemoveCampManagerPrompt()
        {
            if (_campManagerPrompt != 0)
            {
                try
                {
                    Type hud = typeof(Game).Assembly.GetType("RDR2.Native.HUD", false);
                    if (hud != null) InvokeHud(hud, "_UI_PROMPT_DELETE", _campManagerPrompt);
                }
                catch (Exception ex) { Log.Write("Camp manager prompt delete warning: " + ex.Message); }
            }
            _campManagerPrompt = 0;
            _campManagerPromptGroup = 0;
            _campManagerPromptArmed = true;
        }

        private void BeginNameKeyboard()
        {
            Notify("FUNDAR BANDA · PASO 1 DE 2: NOMBRE. El nombre aparecerá en el roster.");
            OpenKeyboard(KeyboardMode.CreateFactionName, string.Empty, 28, "Faction name keyboard opened.");
        }

        private void BeginCampLocationKeyboard()
        {
            OpenCampLocationKeyboard(false, "Camp location menu opened.");
        }

        private void BeginExistingCampLocationKeyboard()
        {
            OpenCampLocationKeyboard(true, "Existing faction camp placement menu opened.");
        }

        private void OpenCampLocationKeyboard(bool forExistingFaction, string logMessage)
        {
            _campSelectionForExisting = forExistingFaction;
            _campSelectionIndex = 0;
            _campSelectionActive = true;
            ResolveUiDrawMethods();
            Notify(_campSelectionForExisting
                ? "ELIGE LA NUEVA UBICACIÓN DEL CAMPAMENTO. Usa arriba/abajo y confirma con aceptar."
                : "FUNDAR BANDA · PASO 2 DE 2: ELIGE LA UBICACIÓN DEL CAMPAMENTO.");
            Log.Write(logMessage + " (on-screen camp creation menu)");
        }

        private bool ResolveUiDrawMethods()
        {
            if (_uiDrawRect != null && _uiDrawText != null)
                return true;

            try
            {
                Assembly assembly = _bridge.MainInstance != null
                    ? _bridge.MainInstance.GetType().Assembly
                    : AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetType("NemesisSystem.UiDraw", false) != null);
                _uiDrawType = assembly != null ? assembly.GetType("NemesisSystem.UiDraw", false) : null;
                if (_uiDrawType == null)
                    return false;

                _uiDrawRect = _uiDrawType.GetMethods(StaticFlags).FirstOrDefault(m =>
                {
                    if (m.Name != "Rect" || m.GetParameters().Length != 8) return false;
                    ParameterInfo[] p = m.GetParameters();
                    return p.Take(4).All(x => x.ParameterType == typeof(float)) &&
                           p.Skip(4).All(x => x.ParameterType == typeof(int));
                });
                _uiDrawText = _uiDrawType.GetMethods(StaticFlags).FirstOrDefault(m =>
                {
                    if (m.Name != "Text" || m.GetParameters().Length != 8) return false;
                    ParameterInfo[] p = m.GetParameters();
                    return p[0].ParameterType == typeof(string) &&
                           p.Skip(1).Take(3).All(x => x.ParameterType == typeof(float)) &&
                           p.Skip(4).All(x => x.ParameterType == typeof(int));
                });

                if (_uiDrawRect == null || _uiDrawText == null)
                    throw new MissingMethodException("NemesisSystem.UiDraw.Rect/Text renderer signatures were not found.");
                return true;
            }
            catch (Exception ex)
            {
                if (!_uiDrawWarningLogged)
                {
                    _uiDrawWarningLogged = true;
                    Log.Write("Camp creation menu renderer unavailable; falling back to game notifications: " + ex.Message);
                }
                return false;
            }
        }

        private void DrawMenuRect(float x, float y, float width, float height, int r, int g, int b, int a)
        {
            if (!ResolveUiDrawMethods()) return;
            try { _uiDrawRect.Invoke(null, new object[] { x, y, width, height, r, g, b, a }); }
            catch (Exception ex) { Log.Write("Camp menu rectangle draw warning: " + ex.Message); }
        }

        private void DrawMenuText(string text, float x, float y, float scale, int r, int g, int b, int a)
        {
            if (!ResolveUiDrawMethods()) return;
            try { _uiDrawText.Invoke(null, new object[] { text ?? string.Empty, x, y, scale, r, g, b, a }); }
            catch (Exception ex) { Log.Write("Camp menu text draw warning: " + ex.Message); }
        }

        private void DrawCampLocationMenu()
        {
            if (!ResolveUiDrawMethods()) return;

            CampOption selected = CampOptions[_campSelectionIndex];
            string factionName = _campSelectionForExisting ? _faction.Name : _pendingFactionName;

            // The drawing helpers and normalized coordinates are the same UiDraw renderer
            // used by enRAGE's roster, conversation and search panels.
            DrawMenuRect(0.215f, 0.175f, 0.57f, 0.65f, 14, 13, 12, 242);
            DrawMenuRect(0.215f, 0.175f, 0.57f, 0.004f, 187, 145, 82, 255);
            DrawMenuText(_campSelectionForExisting ? "CAMPAMENTO DE LA BANDA" : "FUNDAR UNA BANDA",
                0.245f, 0.198f, 0.30f, 245, 235, 214, 255);
            DrawMenuText(_campSelectionForExisting ? "ELEGIR UBICACION · CAMPAMENTO" : "PASO 2 DE 2 · ELEGIR CAMPAMENTO",
                0.245f, 0.235f, 0.19f, 190, 174, 145, 255);
            DrawMenuText("BANDA: " + (factionName ?? ""), 0.245f, 0.269f, 0.205f, 232, 226, 211, 255);
            DrawMenuText("UBICACIONES DISPONIBLES", 0.245f, 0.309f, 0.18f, 164, 157, 141, 255);

            for (int i = 0; i < CampOptions.Length; i++)
            {
                float y = 0.337f + i * 0.044f;
                bool active = i == _campSelectionIndex;
                if (active)
                {
                    DrawMenuRect(0.242f, y, 0.285f, 0.037f, 63, 48, 29, 245);
                    DrawMenuRect(0.242f, y, 0.003f, 0.037f, 205, 166, 105, 255);
                }
                else
                {
                    DrawMenuRect(0.242f, y, 0.285f, 0.037f, 30, 28, 25, 220);
                }
                DrawMenuText((active ? "> " : "  ") + CampOptions[i].Label,
                    0.253f, y + 0.007f, 0.19f,
                    active ? 250 : 205, active ? 235 : 199, active ? 208 : 187, 255);
            }

            DrawMenuRect(0.55f, 0.337f, 0.205f, 0.365f, 25, 23, 21, 230);
            DrawMenuText("UBICACION " + (_campSelectionIndex + 1) + " DE " + CampOptions.Length,
                0.565f, 0.354f, 0.18f, 164, 157, 141, 255);
            DrawMenuText(selected.Label, 0.565f, 0.387f, 0.245f, 242, 221, 184, 255);
            DrawMenuText(selected.Description, 0.565f, 0.429f, 0.16f, 210, 205, 192, 255);
            DrawMenuText("CAMPAMENTO EXTERIOR", 0.565f, 0.505f, 0.175f, 190, 174, 145, 255);
            DrawMenuText("El montaje fisico se", 0.565f, 0.536f, 0.175f, 210, 205, 192, 255);
            DrawMenuText("restaura al acercarte.", 0.565f, 0.559f, 0.175f, 210, 205, 192, 255);
            DrawMenuText("TERRITORIO", 0.565f, 0.612f, 0.175f, 190, 174, 145, 255);
            DrawMenuText("La banda obtiene su", 0.565f, 0.641f, 0.175f, 210, 205, 192, 255);
            DrawMenuText("primer punto de apoyo", 0.565f, 0.664f, 0.175f, 210, 205, 192, 255);
            DrawMenuText("en este distrito.", 0.565f, 0.687f, 0.175f, 210, 205, 192, 255);

            DrawMenuRect(0.242f, 0.718f, 0.513f, 0.0015f, 105, 91, 69, 210);
            DrawMenuText("ARRIBA / ABAJO  RECORRER     ACEPTAR  VIAJAR     ATRAS  VOLVER",
                0.253f, 0.746f, 0.165f, 188, 179, 159, 255);
        }

        private void DrawFactionNameStep()
        {
            if (!ResolveUiDrawMethods()) return;
            DrawMenuRect(0.30f, 0.10f, 0.40f, 0.095f, 14, 13, 12, 230);
            DrawMenuRect(0.30f, 0.10f, 0.40f, 0.003f, 187, 145, 82, 255);
            DrawMenuText("FUNDAR UNA BANDA", 0.322f, 0.122f, 0.27f, 245, 235, 214, 255);
            DrawMenuText("PASO 1 DE 2 · NOMBRE DE LA BANDA", 0.322f, 0.158f, 0.19f, 210, 198, 174, 255);
        }

        private bool JustPressed(int control)
        {
            try
            {
                if (Function.Call<bool>(IsControlJustPressedHash, new InputArgument[] { 0, control }))
                    return true;
                // RDR2 routes some frontend/gamepad controls through input group 2.
                return Function.Call<bool>(IsControlJustPressedHash, new InputArgument[] { 2, control });
            }
            catch (Exception ex) { Log.Write("Camp/menu input warning: " + ex.Message); return false; }
        }

        private void UpdateCampLocationSelector()
        {
            DrawCampLocationMenu();
            if (JustPressed(ControlFrontendCancel))
            {
                _campSelectionActive = false;
                if (_campSelectionForExisting)
                    Notify("Selección cancelada. Puedes volver a elegir el campamento desde la ficha del líder.");
                else
                {
                    _promptArmed = true;
                    Notify("Fundación cancelada.");
                }
                return;
            }
            if (JustPressed(ControlFrontendUp))
            {
                _campSelectionIndex = (_campSelectionIndex + CampOptions.Length - 1) % CampOptions.Length;
                return;
            }
            if (JustPressed(ControlFrontendDown))
            {
                _campSelectionIndex = (_campSelectionIndex + 1) % CampOptions.Length;
                return;
            }
            if (JustPressed(ControlFrontendAccept))
            {
                _campSelectionActive = false;
                CampOption option = CampOptions[_campSelectionIndex];
                BeginCampTeleport(option.Position, option.Label, _campSelectionForExisting);
            }
        }

        private void OpenFactionMenu()
        {
            if (!_faction.Founded)
            {
                Notify("La banda todavía no está fundada.");
                Log.Write("Camp administrator interaction ignored: faction data is not founded.");
                return;
            }
            string ensureError = "enRAGE todavía no está listo.";
            if (!_bridge.IsAvailable || !_bridge.EnsurePlayerFaction(_faction, out ensureError))
            {
                Notify("No se pudo abrir la administración: " +
                    (string.IsNullOrWhiteSpace(ensureError) ? "enRAGE todavía no está listo." : ensureError));
                Log.Write("Camp administrator menu open failed: " + ensureError);
                return;
            }
            _factionMenuOpen = true;
            _factionMenuPage = FactionMenuPage.Main;
            _factionMenuIndex = 0;
            _rankMenuIndex = 0;
            _pendingRankIndex = -1;
            _campManagerPromptArmed = false;
            RefreshRankMenuLabels();
            bool rendererReady = ResolveUiDrawMethods();
            Log.Write("Faction management renderer " + (rendererReady ? "ready" : "unavailable; notifications/input fallback active") + ".");
            Notify("ADMINISTRACIÓN DE LA BANDA: usa las flechas para moverte, aceptar para elegir y atrás para salir.");
            Log.Write("Camp administrator faction menu opened. UiRenderer=" + (_uiDrawRect != null && _uiDrawText != null) + ".");
        }

        private void CloseFactionMenu()
        {
            _factionMenuOpen = false;
            _factionMenuPage = FactionMenuPage.None;
            _pendingRankIndex = -1;
            _campManagerPromptArmed = true;
            Notify("Menú de facción cerrado.");
        }

        private void RefreshRankMenuLabels()
        {
            _rankMenuLabels = _bridge.GetPlayerFactionRankLabels(_faction) ?? new List<string>();
            if (_rankMenuIndex >= _rankMenuLabels.Count) _rankMenuIndex = Math.Max(0, _rankMenuLabels.Count - 1);
        }

        private string[] GetFactionMenuLabels()
        {
            return new[]
            {
                "Renombrar banda",
                "Nombre visible del líder",
                "Administrar rangos",
                "Guerras territoriales: " + (_faction.AllowTerritoryWars ? "ACTIVADAS" : "DESACTIVADAS"),
                "Rivalidades entre bandas: " + (_faction.AllowFeuds ? "ACTIVADAS" : "DESACTIVADAS"),
                "Eventos de banda: " + (_faction.AllowGangLifeEvents ? "ACTIVADOS" : "DESACTIVADOS"),
                "Responder a disparos: " + (_faction.AllowAnswersGunfire ? "SÍ" : "NO"),
                "Cerrar menú"
            };
        }

        private void DrawFactionMenu()
        {
            if (!ResolveUiDrawMethods()) return;
            DrawMenuRect(0.18f, 0.105f, 0.64f, 0.79f, 14, 13, 12, 244);
            DrawMenuRect(0.18f, 0.105f, 0.64f, 0.004f, 187, 145, 82, 255);
            DrawMenuText("ADMINISTRACIÓN DE LA BANDA", 0.215f, 0.137f, 0.30f, 245, 235, 214, 255);
            DrawMenuText((_faction.Name ?? "La Banda") + "  ·  ADMINISTRADOR DEL CAMPAMENTO", 0.215f, 0.181f, 0.18f, 190, 174, 145, 255);

            if (_factionMenuPage == FactionMenuPage.Ranks)
            {
                DrawMenuText("ESTRUCTURA DE RANGOS", 0.215f, 0.225f, 0.21f, 232, 226, 211, 255);
                if (_rankMenuLabels == null || _rankMenuLabels.Count == 0)
                    DrawMenuText("enRAGE no expone rangos editables para esta banda.", 0.23f, 0.31f, 0.19f, 210, 205, 192, 255);
                else
                {
                    for (int i = 0; i < _rankMenuLabels.Count; i++)
                    {
                        float y = 0.275f + i * 0.052f;
                        bool active = i == _rankMenuIndex;
                        DrawMenuRect(0.215f, y, 0.315f, 0.043f,
                            active ? 63 : 30, active ? 48 : 28, active ? 29 : 25, active ? 245 : 220);
                        if (active) DrawMenuRect(0.215f, y, 0.003f, 0.043f, 205, 166, 105, 255);
                        DrawMenuText((active ? "> " : "  ") + (string.IsNullOrWhiteSpace(_rankMenuLabels[i]) ? "Rango " + (i + 1) : _rankMenuLabels[i]),
                            0.23f, y + 0.008f, 0.205f,
                            active ? 250 : 205, active ? 235 : 199, active ? 208 : 187, 255);
                    }
                }
                DrawMenuRect(0.55f, 0.275f, 0.23f, 0.38f, 25, 23, 21, 230);
                DrawMenuText("EDITAR RANGO", 0.568f, 0.298f, 0.205f, 190, 174, 145, 255);
                DrawMenuText("Seleccioná un rango y", 0.568f, 0.34f, 0.17f, 210, 205, 192, 255);
                DrawMenuText("presioná Aceptar para", 0.568f, 0.365f, 0.17f, 210, 205, 192, 255);
                DrawMenuText("cambiar su nombre.", 0.568f, 0.39f, 0.17f, 210, 205, 192, 255);
                DrawMenuText("Los umbrales de rango", 0.568f, 0.45f, 0.17f, 210, 205, 192, 255);
                DrawMenuText("y las plazas internas", 0.568f, 0.475f, 0.17f, 210, 205, 192, 255);
                DrawMenuText("de enRAGE se conservan.", 0.568f, 0.50f, 0.17f, 210, 205, 192, 255);
            }
            else
            {
                DrawMenuText("CONFIGURACIÓN", 0.215f, 0.225f, 0.21f, 232, 226, 211, 255);
                string[] options = GetFactionMenuLabels();
                for (int i = 0; i < options.Length; i++)
                {
                    float y = 0.268f + i * 0.052f;
                    bool active = i == _factionMenuIndex;
                    DrawMenuRect(0.215f, y, 0.315f, 0.043f,
                        active ? 63 : 30, active ? 48 : 28, active ? 29 : 25, active ? 245 : 220);
                    if (active) DrawMenuRect(0.215f, y, 0.003f, 0.043f, 205, 166, 105, 255);
                    DrawMenuText((active ? "> " : "  ") + options[i], 0.23f, y + 0.008f, 0.174f,
                        active ? 250 : 205, active ? 235 : 199, active ? 208 : 187, 255);
                }

                DrawMenuRect(0.55f, 0.268f, 0.23f, 0.43f, 25, 23, 21, 230);
                DrawMenuText("ESTADO DE LA BANDA", 0.568f, 0.29f, 0.19f, 190, 174, 145, 255);
                DrawMenuText("HOMBRES: " + _faction.CurrentMen, 0.568f, 0.335f, 0.175f, 210, 205, 192, 255);
                DrawMenuText("SUMINISTROS: " + _faction.CurrentSupplies, 0.568f, 0.365f, 0.175f, 210, 205, 192, 255);
                DrawMenuText("TESORERÍA: " + _faction.CurrentTreasury, 0.568f, 0.395f, 0.175f, 210, 205, 192, 255);
                DrawMenuText("IDENTIFICADORES FIJOS", 0.568f, 0.455f, 0.17f, 190, 174, 145, 255);
                DrawMenuText("GANG: player_faction", 0.568f, 0.49f, 0.16f, 210, 205, 192, 255);
                DrawMenuText("ROSTER: player_faction", 0.568f, 0.518f, 0.16f, 210, 205, 192, 255);
                DrawMenuText(GetFactionMenuDescription(_factionMenuIndex), 0.568f, 0.568f, 0.15f, 205, 199, 187, 255);
            }

            DrawMenuRect(0.215f, 0.755f, 0.565f, 0.0015f, 105, 91, 69, 210);
            DrawMenuText("ARRIBA / ABAJO  MOVER     ACEPTAR  ELEGIR     ATRÁS  VOLVER",
                0.23f, 0.785f, 0.16f, 188, 179, 159, 255);
        }

        private string GetFactionMenuDescription(int index)
        {
            switch (index)
            {
                case 0: return "Cambiar el nombre visible de la facción.";
                case 1: return "Cambiar el alias del líder; Arthur/John conserva el ID nativo.";
                case 2: return "Editar nombres de rango sin alterar sus límites internos.";
                case 3: return "Permite guerras de distrito si la otra facción también las tiene activas y ambas controlan territorio.";
                case 4: return "Permite rivalidades de enRAGE; no declara una guerra instantánea al activarlo.";
                case 5: return "Activa eventos de vida etiquetados para bandas; no fuerza un evento inmediato.";
                case 6: return "Habilita respuestas de patrullas/eventos a disparos cercanos; no obliga a todo NPC del campamento a combatir.";
                default: return "Los IDs de gang y roster no se pueden cambiar.";
            }
        }

        private void UpdateFactionMenu()
        {
            DrawFactionMenu();
            if (JustPressed(ControlFrontendCancel))
            {
                Log.Write("Faction menu cancel input received.");
                if (_factionMenuPage == FactionMenuPage.Ranks)
                {
                    _factionMenuPage = FactionMenuPage.Main;
                    _factionMenuIndex = 2;
                }
                else CloseFactionMenu();
                return;
            }

            int count = _factionMenuPage == FactionMenuPage.Ranks
                ? Math.Max(1, _rankMenuLabels == null ? 0 : _rankMenuLabels.Count)
                : GetFactionMenuLabels().Length;
            if (JustPressed(ControlFrontendUp))
            {
                Log.Write("Faction menu up input received.");
                if (_factionMenuPage == FactionMenuPage.Ranks)
                    _rankMenuIndex = (_rankMenuIndex + count - 1) % count;
                else _factionMenuIndex = (_factionMenuIndex + count - 1) % count;
                return;
            }
            if (JustPressed(ControlFrontendDown))
            {
                Log.Write("Faction menu down input received.");
                if (_factionMenuPage == FactionMenuPage.Ranks)
                    _rankMenuIndex = (_rankMenuIndex + 1) % count;
                else _factionMenuIndex = (_factionMenuIndex + 1) % count;
                return;
            }
            if (!JustPressed(ControlFrontendAccept)) return;
            Log.Write("Faction menu accept input received on page " + _factionMenuPage + ", index=" +
                (_factionMenuPage == FactionMenuPage.Ranks ? _rankMenuIndex : _factionMenuIndex) + ".");

            if (_factionMenuPage == FactionMenuPage.Ranks)
            {
                RefreshRankMenuLabels();
                if (_rankMenuLabels.Count == 0)
                {
                    Notify("No se encontraron rangos nativos para esta facción.");
                    _factionMenuPage = FactionMenuPage.Main;
                    return;
                }
                _pendingRankIndex = Math.Max(0, Math.Min(_rankMenuIndex, _rankMenuLabels.Count - 1));
                Notify("CAMPO: NOMBRE DEL RANGO. Los umbrales internos se mantienen.");
                OpenKeyboard(KeyboardMode.RenameFactionRank, _rankMenuLabels[_pendingRankIndex], 28,
                    "Faction rank label keyboard opened for index " + _pendingRankIndex + ".");
                return;
            }

            switch (_factionMenuIndex)
            {
                case 0:
                    Notify("CAMPO: NOMBRE DE LA BANDA. El ID gang player_faction no cambia.");
                    OpenKeyboard(KeyboardMode.RenameFactionName, _faction.Name, 28, "Faction rename keyboard opened.");
                    break;
                case 1:
                    string currentLeader = !string.IsNullOrWhiteSpace(_faction.LeaderDisplayName)
                        ? _faction.LeaderDisplayName : _faction.LeaderName;
                    Notify("CAMPO: NOMBRE VISIBLE DEL LÍDER. Su ID de historia no cambia.");
                    OpenKeyboard(KeyboardMode.RenameLeader, currentLeader ?? string.Empty, 28,
                        "Leader display-name keyboard opened from camp administrator.");
                    break;
                case 2:
                    RefreshRankMenuLabels();
                    _rankMenuIndex = 0;
                    _factionMenuPage = FactionMenuPage.Ranks;
                    break;
                case 3: ToggleFactionSetting(3); break;
                case 4: ToggleFactionSetting(4); break;
                case 5: ToggleFactionSetting(5); break;
                case 6: ToggleFactionSetting(6); break;
                default: CloseFactionMenu(); break;
            }
        }

        private void ToggleFactionSetting(int menuIndex)
        {
            bool previous;
            switch (menuIndex)
            {
                case 3: previous = _faction.AllowTerritoryWars; _faction.AllowTerritoryWars = !previous; break;
                case 4: previous = _faction.AllowFeuds; _faction.AllowFeuds = !previous; break;
                case 5: previous = _faction.AllowGangLifeEvents; _faction.AllowGangLifeEvents = !previous; break;
                case 6: previous = _faction.AllowAnswersGunfire; _faction.AllowAnswersGunfire = !previous; break;
                default: return;
            }

            string error;
            if (!_bridge.EnsurePlayerFaction(_faction, out error))
            {
                if (menuIndex == 3) _faction.AllowTerritoryWars = previous;
                else if (menuIndex == 4) _faction.AllowFeuds = previous;
                else if (menuIndex == 5) _faction.AllowGangLifeEvents = previous;
                else if (menuIndex == 6) _faction.AllowAnswersGunfire = previous;
                string ignored;
                _bridge.EnsurePlayerFaction(_faction, out ignored);
                Notify("No se pudo aplicar el ajuste de facción: " + error);
                return;
            }

            try { _faction.Save(); }
            catch (Exception ex) { Log.Write("Faction configuration save failed: " + ex.Message); }
            string settingName = menuIndex == 3 ? "Guerras territoriales" : menuIndex == 4 ? "Rivalidades" : menuIndex == 5 ? "Eventos de banda" : "Responder a disparos";
            bool enabled = menuIndex == 3 ? _faction.AllowTerritoryWars : menuIndex == 4 ? _faction.AllowFeuds : menuIndex == 5 ? _faction.AllowGangLifeEvents : _faction.AllowAnswersGunfire;
            Notify(settingName + (enabled ? " activado." : " desactivado."));
        }

        private void SaveRankLabelFromKeyboard(string label)
        {
            if (_pendingRankIndex < 0) return;
            RefreshRankMenuLabels();
            if (_pendingRankIndex >= _rankMenuLabels.Count)
            {
                _pendingRankIndex = -1;
                Notify("El rango seleccionado ya no existe.");
                return;
            }

            string[] oldLabels = _faction.RankLabels == null ? new string[0] : (string[])_faction.RankLabels.Clone();
            string[] nextLabels = oldLabels;
            if (nextLabels.Length < _rankMenuLabels.Count) Array.Resize(ref nextLabels, _rankMenuLabels.Count);
            nextLabels[_pendingRankIndex] = label;
            _faction.RankLabels = nextLabels;

            string error;
            if (!_bridge.EnsurePlayerFaction(_faction, out error))
            {
                _faction.RankLabels = oldLabels;
                string ignored;
                _bridge.EnsurePlayerFaction(_faction, out ignored);
                _pendingRankIndex = -1;
                Notify("No se pudo actualizar el rango: " + error);
                return;
            }

            try { _faction.Save(); }
            catch (Exception ex) { Log.Write("Rank label save failed: " + ex.Message); }
            RefreshRankMenuLabels();
            _rankMenuIndex = _pendingRankIndex;
            _pendingRankIndex = -1;
            Notify("Nombre del rango actualizado: " + label + ".");
        }

        internal bool NeedsCampSelection
        {
            get { return _faction.Founded && string.IsNullOrWhiteSpace(_faction.CampSiteId); }
        }

        internal void BeginCampLocationFromRoster()
        {
            if (_keyboardOpen || !_faction.Founded) return;
            string findError;
            if (_bridge.FindPlayerCampSite(_faction, out findError))
            {
                try { _faction.Save(); } catch (Exception ex) { Log.Write("Could not save discovered native camp id: " + ex.Message); }
                EnsureCampBlip();
                Notify("Esta banda ya tiene un campamento nativo en " + _faction.CampRegion + ".");
                return;
            }
            if (!string.IsNullOrWhiteSpace(findError)) Log.Write("Camp lookup before placement failed: " + findError);
            if (!NeedsCampSelection) return;
            Notify("Esta banda todavía no tiene un campamento nativo. Elige dónde establecerlo.");
            BeginExistingCampLocationKeyboard();
        }

        internal void BeginLeaderRenameFromRoster()
        {
            if (!_faction.Founded || _keyboardOpen) return;
            string current = !string.IsNullOrWhiteSpace(_faction.LeaderDisplayName)
                ? _faction.LeaderDisplayName : _faction.LeaderName;
            Notify("CAMPO: NOMBRE VISIBLE DEL LÍDER. Su identidad interna seguirá siendo Arthur/John.");
            OpenKeyboard(KeyboardMode.RenameLeader, current ?? string.Empty, 28, "Leader display-name keyboard opened.");
        }

        private void OpenKeyboard(KeyboardMode mode, string defaultText, int maxLength, string logMessage)
        {
            try
            {
                string title;
                switch (mode)
                {
                    case KeyboardMode.CreateFactionName: title = "FMMC_KEY_TIP8"; break;
                    case KeyboardMode.RenameLeader: title = "FMMC_KEY_TIP8"; break;
                    case KeyboardMode.RenameFactionName: title = "FMMC_KEY_TIP8"; break;
                    case KeyboardMode.RenameFactionRank: title = "FMMC_KEY_TIP8"; break;
                    default: title = "ENRAGE"; break;
                }
                MISC.DISPLAY_ONSCREEN_KEYBOARD(0, title, "", defaultText ?? "", "", "", "", maxLength);
                _keyboardMode = mode;
                _keyboardOpen = true;
                Log.Write(logMessage);
            }
            catch (Exception ex)
            {
                _keyboardOpen = false;
                _keyboardMode = KeyboardMode.None;
                _promptArmed = true;
                Notify("No se pudo abrir el teclado de texto.");
                Log.Write("Keyboard open failed: " + ex);
            }
        }

        private void UpdateKeyboard()
        {
            int result;
            try { result = MISC.UPDATE_ONSCREEN_KEYBOARD(); }
            catch (Exception ex)
            {
                Log.Write("Keyboard update failed: " + ex);
                _keyboardOpen = false;
                _keyboardMode = KeyboardMode.None;
                _promptArmed = true;
                return;
            }

            if (result == 0 || result == -1)
            {
                if (_keyboardMode == KeyboardMode.CreateFactionName)
                    DrawFactionNameStep();
                return;
            }

            KeyboardMode mode = _keyboardMode;
            _keyboardOpen = false;
            _keyboardMode = KeyboardMode.None;
            if (result != 1)
            {
                if (mode == KeyboardMode.CreateFactionName)
                {
                    _promptArmed = true;
                    Notify("Fundación cancelada.");
                }
                else if (mode == KeyboardMode.RenameLeader)
                {
                    Notify("Cambio de nombre cancelado.");
                }
                else if (mode == KeyboardMode.RenameFactionName)
                {
                    Notify("Cambio de nombre de la banda cancelado.");
                }
                else if (mode == KeyboardMode.RenameFactionRank)
                {
                    _pendingRankIndex = -1;
                    Notify("Cambio de nombre del rango cancelado.");
                }
                return;
            }

            string input = string.Empty;
            try { input = MISC.GET_ONSCREEN_KEYBOARD_RESULT() ?? string.Empty; }
            catch (Exception ex) { Log.Write("Keyboard result failed: " + ex.Message); }

            switch (mode)
            {
                case KeyboardMode.CreateFactionName:
                    _pendingFactionName = SanitizeFactionName(input);
                    if (_pendingFactionName.Length < 2)
                    {
                        _promptArmed = true;
                        Notify("El nombre de la banda debe tener al menos 2 caracteres.");
                        return;
                    }
                    BeginCampLocationKeyboard();
                    return;

                case KeyboardMode.RenameLeader:
                    string displayName = SanitizeFactionName(input);
                    if (displayName.Length < 2)
                    {
                        Notify("El nombre visible debe tener al menos 2 caracteres.");
                        return;
                    }
                    string previous = _faction.LeaderDisplayName;
                    _faction.LeaderDisplayName = displayName;
                    string error;
                    if (!_bridge.EnsurePlayerFaction(_faction, out error))
                    {
                        _faction.LeaderDisplayName = previous;
                        Notify("No se pudo actualizar el líder: " + error);
                        return;
                    }
                    _faction.LeaderName = displayName;
                    try
                    {
                        _faction.Save();
                        Notify("Nombre visible del líder actualizado: " + displayName + ".");
                        Log.Write("Player leader display alias changed to '" + displayName + "'; internal YouId remains unchanged.");
                    }
                    catch (Exception ex)
                    {
                        _faction.LeaderDisplayName = previous;
                        _bridge.EnsurePlayerFaction(_faction, out error);
                        Notify("No se pudo guardar el nombre del líder.");
                        Log.Write("Leader display name persistence failed: " + ex);
                    }
                    return;

                case KeyboardMode.RenameFactionName:
                    string factionName = SanitizeFactionName(input);
                    if (factionName.Length < 2)
                    {
                        Notify("El nombre de la banda debe tener al menos 2 caracteres.");
                        return;
                    }
                    string previousFactionName = _faction.Name;
                    _faction.Name = factionName;
                    string factionError;
                    if (!_bridge.EnsurePlayerFaction(_faction, out factionError))
                    {
                        _faction.Name = previousFactionName;
                        string ignoredNameError;
                        _bridge.EnsurePlayerFaction(_faction, out ignoredNameError);
                        Notify("No se pudo actualizar la banda: " + factionError);
                        return;
                    }
                    try
                    {
                        _faction.Save();
                        if (_campBlip != 0 && MAP.DOES_BLIP_EXIST(_campBlip)) TrySetBlipName(_campBlip, "Campamento - " + factionName);
                        Notify("Nombre de la banda actualizado: " + factionName + ".");
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Faction name persistence failed: " + ex);
                        Notify("El nombre cambió en memoria, pero no se pudo guardar. Revisa extension.log.");
                    }
                    return;

                case KeyboardMode.RenameFactionRank:
                    string rankLabel = SanitizeFactionName(input);
                    if (rankLabel.Length < 2)
                    {
                        Notify("El nombre del rango debe tener al menos 2 caracteres.");
                        return;
                    }
                    SaveRankLabelFromKeyboard(rankLabel);
                    return;
            }
        }

        private void BeginCampTeleport(Vector3 position, string label, bool forExistingFaction)
        {
            _pendingCampTeleportPosition = position;
            _pendingCampTeleportLabel = label ?? "Campamento";
            _pendingCampTeleportForExistingFaction = forExistingFaction;
            _campTeleportPending = true;
            _campTeleportStartedAt = Game.GameTime;
            Notify("Viajando a " + _pendingCampTeleportLabel + "... La pantalla se fundirá a negro.");
            try
            {
                CAM.DO_SCREEN_FADE_OUT(700);
                Log.Write("Camp teleport fade-out started: " + _pendingCampTeleportLabel + " at " + position + ".");
            }
            catch (Exception ex)
            {
                // Keep the transition recoverable even if this build exposes no fade wrapper.
                Log.Write("Camp teleport fade-out native failed: " + ex.Message);
            }
        }

        private void UpdateCampTeleport()
        {
            if (!_campTeleportPending) return;
            try
            {
                bool fadedOut = false;
                try { fadedOut = CAM.IS_SCREEN_FADED_OUT(); }
                catch (Exception ex) { Log.Write("Fade-out state check warning: " + ex.Message); }

                // Don't wait forever if another mod interrupts the fade native.
                if (!fadedOut && Game.GameTime - _campTeleportStartedAt < 3000)
                    return;

                if (!fadedOut)
                    Log.Write("Camp teleport proceeding after fade timeout; screen did not report fully faded out.");

                // Establish the site while the screen is black. If enRAGE rejects this location,
                // the player stays where they are and the selection panel can be reopened.
                if (_pendingCampTeleportForExistingFaction)
                    PlaceCampForExistingFaction(_pendingCampTeleportPosition, _pendingCampTeleportLabel);
                else
                    CompleteFounding(_pendingCampTeleportPosition, _pendingCampTeleportLabel);

                bool campCreated = _faction.Founded && !string.IsNullOrWhiteSpace(_faction.CampSiteId);
                if (!campCreated)
                {
                    _campTeleportPending = false;
                    try { CAM.DO_SCREEN_FADE_IN(650); } catch (Exception fadeEx) { Log.Write("Fade-in after rejected camp warning: " + fadeEx.Message); }
                    return;
                }

                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                {
                    Log.Write("Camp was created, but the player ped was unavailable for the teleport.");
                    _campTeleportPending = false;
                    try { CAM.DO_SCREEN_FADE_IN(650); } catch { }
                    return;
                }

                // Use the actual coordinates enRAGE saved for the CampSite, not an estimated
                // selector coordinate, so the player arrives at the same site as the camp blip.
                float x = _faction.CampX;
                float y = _faction.CampY;
                float z = _faction.CampZ;
                STREAMING.REQUEST_COLLISION_AT_COORD(x, y, z);
                ENTITY.SET_ENTITY_COORDS(player.Handle, x, y, z + 0.3f, false, false, false, false);
                STREAMING.REQUEST_COLLISION_AT_COORD(x, y, z);
                _campPropsRestoredThisRuntime = false;
                _nextCampPropsAttemptAt = Game.GameTime + 1000;
                _nextCampBlipAttemptAt = 0;
                Log.Write("Player teleported to player faction camp " + _faction.CampSiteId + " at " + x + ", " + y + ", " + z + ".");
                Notify("Llegaste al campamento de " + _faction.Name + " en " + _pendingCampTeleportLabel + ".");
                _campTeleportPending = false;
                CAM.DO_SCREEN_FADE_IN(850);
            }
            catch (Exception ex)
            {
                _campTeleportPending = false;
                try { CAM.DO_SCREEN_FADE_IN(650); } catch { }
                Log.Write("Camp teleport failed: " + ex);
                Notify("No se pudo completar el viaje al campamento. Revisa extension.log.");
            }
        }

        private void StartFounderGuitarScenario(Ped ped)
        {
            if (ped == null || !ped.Exists()) return;
            try
            {
                TASK.CLEAR_PED_TASKS(ped.Handle, true, false);
                TASK.TASK_START_SCENARIO_IN_PLACE_HASH(ped.Handle,
                    MISC.GET_HASH_KEY("WORLD_HUMAN_SIT_GUITAR"), -1, true, 0U, -1f, false);
                _nextFounderScenarioCheckAt = Game.GameTime + 5000;
                Log.Write("Founder guitar scenario started: WORLD_HUMAN_SIT_GUITAR (ped=" + ped.Handle + ").");
            }
            catch (Exception ex)
            {
                Log.Write("Founder guitar scenario start failed: " + ex.Message);
            }
        }

        private void EnsureFounderGuitarScenario()
        {
            if (_founder == null || !_founder.Exists() || Game.GameTime < _nextFounderScenarioCheckAt)
                return;
            _nextFounderScenarioCheckAt = Game.GameTime + 5000;
            try
            {
                if (!PED.IS_PED_USING_ANY_SCENARIO(_founder.Handle))
                    StartFounderGuitarScenario(_founder);
            }
            catch (Exception ex)
            {
                Log.Write("Founder guitar scenario maintenance warning: " + ex.Message);
            }
        }

        private void MaintainReleasedRosterMembersReturningToCamp()
        {
            if (!_faction.Founded || string.IsNullOrWhiteSpace(_faction.CampSiteId) ||
                (Math.Abs(_faction.CampX) + Math.Abs(_faction.CampY) + Math.Abs(_faction.CampZ) < 0.01f)) return;

            bool companionIntegrationAvailable;
            Vector3 campCentre = _bridge.GetPlayerCampCentre(_faction);
            Ped player = Game.Player.Character;
            bool campLoaded = player != null && player.Exists() && player.Position.DistanceTo(campCentre) <= 180f;
            // Old roster records are persisted as data, not world entities. Materialize at most two
            // missing records per scan near the loaded camp, reusing a cached/stored handle first.
            List<EnRAGEBridge.ReleasedRosterPed> campRoster = _bridge.GetReleasedPlayerFactionRosterPeds(
                _faction, campCentre, campLoaded ? 3 : 0, out companionIntegrationAvailable);
            if (!companionIntegrationAvailable)
            {
                if (Game.GameTime >= _nextRosterIntegrationWarningAt)
                {
                    _nextRosterIntegrationWarningAt = Game.GameTime + 10000;
                    Log.Write("Camp roster appearance is waiting for Companion's live main instance, roster, active-companion list and exact handle cache. Will retry.");
                }
                return; // Companion may load after this extension.
            }

            HashSet<int> campHandles = new HashSet<int>(campRoster.Select(r => r.Handle));
            foreach (EnRAGEBridge.ReleasedRosterPed rosterPed in campRoster)
            {
                if (rosterPed == null || rosterPed.Ped == null || !rosterPed.Ped.Exists() || rosterPed.Ped.IsDead)
                    continue;

                CampboundRosterMember member;
                if (!_campboundRosterMembers.TryGetValue(rosterPed.Handle, out member))
                {
                    Vector3 destination = GetRosterCampDestination(rosterPed.Handle);
                    member = new CampboundRosterMember
                    {
                        Handle = rosterPed.Handle,
                        Ped = rosterPed.Ped,
                        RosterId = rosterPed.RosterId,
                        Destination = destination,
                        Arrived = false,
                        AwaitingDismount = rosterPed.Ped.IsOnMount,
                        LastDistance = float.MaxValue,
                        LastProgressCheckAt = Game.GameTime,
                        NextPathCheckAt = 0,
                        NextScenarioCheckAt = 0,
                        ScenarioAnchorIndex = -1
                    };
                    _campboundRosterMembers[member.Handle] = member;
                    EnsureNativeCampMemberSocialInteraction(member.Ped);
                    if (member.AwaitingDismount)
                    {
                        member.NextPathCheckAt = Game.GameTime + 2000;
                        Log.Write("Roster ped " + member.RosterId + " is mounted; waiting for Companion's dismount task before returning to camp.");
                    }
                    else StartRosterMemberReturn(member);
                    Log.Write("Non-following player_faction roster identity " + member.RosterId + " (handle=" + member.Handle +
                              ") assigned to camp life using its exact roster ID and tracked entity handle.");
                }
                else
                {
                    member.Ped = rosterPed.Ped; // Refresh wrapper for the same handle; never create an entity.
                    UpdateRosterMemberReturn(member);
                }
            }

            // If Companion lists a handle as actively following again, remove our camp AI state immediately.
            // The next scan can add it again only after it stops following.
            foreach (int handle in _campboundRosterMembers.Keys.ToArray())
            {
                if (!campHandles.Contains(handle))
                    _campboundRosterMembers.Remove(handle);
            }
        }

        private Vector3 GetRosterCampDestination(int handle)
        {
            Vector3 centre = _bridge.GetPlayerCampCentre(_faction);
            // Keep idle NPCs in the middle area while spacing them around the kit instead of stacking
            // every entity at the exact fire/camp centre. Prefer a free radial slot.
            int firstSlot = Math.Abs(handle % 16);
            for (int attempt = 0; attempt < 16; attempt++)
            {
                int slot = (firstSlot + attempt) % 16;
                double angle = slot * (Math.PI * 2.0 / 16.0);
                float radius = 2.8f + (slot % 4) * 0.65f;
                Vector3 candidate = centre + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0.25f);
                bool used = _campManagerIdle != null && _campManagerIdle.Handle != handle &&
                            _campManagerIdle.Destination.DistanceTo(candidate) < 1.7f;
                if (!used)
                {
                    foreach (CampboundRosterMember other in _campboundRosterMembers.Values)
                    {
                        if (other != null && other.Handle != handle && other.Destination.DistanceTo(candidate) < 1.7f)
                        {
                            used = true;
                            break;
                        }
                    }
                }
                if (!used) return candidate;
            }
            return centre + new Vector3(3.0f, 0.0f, 0.25f);
        }

        private void StartRosterMemberReturn(CampboundRosterMember member)
        {
            if (member == null || member.Ped == null || !member.Ped.Exists() || member.Ped.IsDead) return;
            try
            {
                STREAMING.REQUEST_COLLISION_AT_COORD(member.Destination.X, member.Destination.Y, member.Destination.Z);
                TASK.CLEAR_PED_TASKS(member.Ped.Handle, true, false);
                Function.Call(TaskFollowNavMeshToCoordHash, new InputArgument[]
                {
                    member.Ped.Handle,
                    member.Destination.X,
                    member.Destination.Y,
                    member.Destination.Z,
                    1.0f,
                    -1,
                    3.0f,
                    0,
                    -1.0f
                });
                member.Arrived = false;
                member.AwaitingDismount = false;
                member.ScenarioAnchorIndex = -1;
                member.ScenarioAnchorPosition = Vector3.Zero;
                member.ScenarioLastProgressDistance = float.MaxValue;
                member.ScenarioLastProgressAt = 0;
                member.ScenarioTaskPending = false;
                member.PendingObjectScenario = false;
                member.HasActiveCampScenario = false;
                member.UsingFallbackScenario = false;
                member.LastDistance = member.Ped.Position.DistanceTo(member.Destination);
                member.LastProgressCheckAt = Game.GameTime;
                member.NextPathCheckAt = Game.GameTime + 12000;
                member.NextScenarioCheckAt = 0;
            }
            catch (Exception ex)
            {
                member.NextPathCheckAt = Game.GameTime + 8000;
                Log.Write("Could not order roster ped " + member.RosterId + " back to camp: " + ex.Message);
            }
        }

        internal void EnsureNativeCampMemberSocialInteraction(Ped ped)
        {
            if (ped == null || !ped.Exists() || ped.IsDead) return;

            try
            {
                // Companion applies BlockPermanentEvents=true to dismissed recruits. That prevents
                // the ambient social system from answering RDR2's native greet/antagonize controls.
                ped.BlockPermanentEvents = false;
                // Mission/recruited peds can have the ambient social prompt disabled by default.
                // Flag 130 must be false for RDR2's native greet/antagonize prompts to appear.
                PED.SET_PED_CONFIG_FLAG(ped.Handle, 130, false);
                // These flags keep interaction lock-on available while permanent ambient reactions
                // remain blocked for camp AI.
                PED.SET_PED_CONFIG_FLAG(ped.Handle, 297, true);
                PED.SET_PED_CONFIG_FLAG(ped.Handle, 301, false);
            }
            catch (Exception ex)
            {
                Log.Write("Camp member native-interaction config warning (" + ped.Handle + "): " + ex.Message);
            }

            try
            {
                // The native greet/antagonize wheel requires the member to remain focusable by the player.
                Function.Call(SetPedCanBeTargettedHash, new InputArgument[] { ped.Handle, true });
                if (Game.Player != null)
                    Function.Call(SetPedCanBeTargettedByPlayerHash, new InputArgument[] { ped.Handle, Game.Player.Handle, true });
            }
            catch (Exception ex)
            {
                Log.Write("Camp member native-targeting warning (" + ped.Handle + "): " + ex.Message);
            }
        }

        private void UpdateRosterMemberReturn(CampboundRosterMember member)
        {
            if (member == null || member.Ped == null || !member.Ped.Exists() || member.Ped.IsDead) return;

            // Companion/EnRAGE can refresh their recruit profile; reassert native interaction flags.
            EnsureNativeCampMemberSocialInteraction(member.Ped);

            if (member.Arrived)
            {
                // If ambient AI or a later task pulled this same entity away from camp, bring it
                // back instead of letting a non-following roster member settle somewhere else.
                Vector3 campCentre = _bridge.GetPlayerCampCentre(_faction);
                if (member.Ped.Position.DistanceTo(campCentre) > 18.0f)
                {
                    member.Arrived = false;
                    member.ScenarioTaskPending = false;
                    member.PendingObjectScenario = false;
                    member.HasActiveCampScenario = false;
                    member.UsingFallbackScenario = false;
                    member.ScenarioAnchorIndex = -1;
                    member.FailedScenarioAnchors.Clear();
                    if (member.Ped.IsOnMount)
                    {
                        member.AwaitingDismount = true;
                        member.NextPathCheckAt = Game.GameTime + 2000;
                    }
                    else
                    {
                        StartRosterMemberReturn(member);
                    }
                    return;
                }
                MaintainCampIdleScenario(member);
                return;
            }

            if (member.Ped.IsOnMount)
            {
                member.AwaitingDismount = true;
                member.NextPathCheckAt = Game.GameTime + 2000;
                return; // Do not cancel Companion's dismount action or pathfind while mounted.
            }
            if (member.AwaitingDismount)
            {
                member.AwaitingDismount = false;
                StartRosterMemberReturn(member);
                return;
            }

            float distance = member.Ped.Position.DistanceTo(member.Destination);
            // Match the 3 m stop radius passed to TASK_FOLLOW_NAV_MESH_TO_COORD; don't
            // declare an NPC camped while it is still standing on the edge of the site.
            if (distance <= 3.8f)
            {
                member.Arrived = true;
                member.NextScenarioCheckAt = Game.GameTime + 400;
                member.LastDistance = distance;
                Log.Write("Roster ped " + member.RosterId + " reached the camp centre area; selecting an unused object scenario or standing/sitting idle.");
                MaintainCampIdleScenario(member);
                return;
            }

            if (Game.GameTime < member.NextPathCheckAt) return;
            // Only reset a path if it appears stuck. Preserve a valid walk while distance is decreasing.
            if (member.LastDistance - distance >= 2.0f)
            {
                member.LastDistance = distance;
                member.LastProgressCheckAt = Game.GameTime;
                member.NextPathCheckAt = Game.GameTime + 12000;
                return;
            }

            member.LastDistance = distance;
            member.LastProgressCheckAt = Game.GameTime;
            StartRosterMemberReturn(member);
        }

        private void MaintainCampIdleScenario(CampboundRosterMember member)
        {
            if (member == null || member.Ped == null || !member.Ped.Exists() || member.Ped.IsDead ||
                !member.Arrived || Game.GameTime < member.NextScenarioCheckAt) return;
            member.NextScenarioCheckAt = Game.GameTime + 650;

            bool usingScenario = false;
            try { usingScenario = PED.IS_PED_USING_ANY_SCENARIO(member.Ped.Handle); }
            catch (Exception ex) { Log.Write("Camp scenario state check warning for " + member.RosterId + ": " + ex.Message); }

            if (usingScenario)
            {
                if (member.ScenarioTaskPending)
                {
                    bool wasObjectScenario = member.PendingObjectScenario;
                    member.ScenarioTaskPending = false;
                    member.PendingObjectScenario = false;
                    member.ScenarioAttemptStartedAt = 0;
                    member.HasActiveCampScenario = true;
                    member.UsingFallbackScenario = !wasObjectScenario;
                    Log.Write("Camp idle scenario started for " + member.RosterId + ": " +
                              (wasObjectScenario ? "near a CampKit object" : "random standing/sitting idle") + ".");
                }
                else if (!member.HasActiveCampScenario)
                {
                    // Includes the camp manager reconnecting to a ped that was already using a scenario.
                    member.HasActiveCampScenario = true;
                }
                return;
            }

            if (member.HasActiveCampScenario)
            {
                member.HasActiveCampScenario = false;
                member.ScenarioAnchorIndex = -1;
                member.ScenarioAnchorPosition = Vector3.Zero;
                member.ScenarioLastProgressDistance = float.MaxValue;
                member.ScenarioLastProgressAt = 0;
                member.UsingFallbackScenario = false;
                // Occupancy can change while an idle animation runs. Reconsider previously busy
                // prop points once it ends, rather than blacklisting them for the whole session.
                member.FailedScenarioAnchors.Clear();
                member.NextScenarioCheckAt = Game.GameTime + 400;
                return;
            }

            if (member.ScenarioTaskPending)
            {
                int elapsed = Game.GameTime - member.ScenarioAttemptStartedAt;
                if (member.PendingObjectScenario)
                {
                    // The native walks to its selected scenario. Give it time while the ped is
                    // making progress, but do not wait seven seconds at every empty CampKit point.
                    float currentDistance = member.Ped.Position.DistanceTo(member.ScenarioAnchorPosition);
                    if (member.ScenarioLastProgressDistance - currentDistance >= 0.35f)
                    {
                        member.ScenarioLastProgressDistance = currentDistance;
                        member.ScenarioLastProgressAt = Game.GameTime;
                    }
                    bool progressedRecently = Game.GameTime - member.ScenarioLastProgressAt < 3500;
                    if (elapsed < 3500 || (progressedRecently && elapsed < 15000)) return;

                    if (member.ScenarioAnchorIndex >= 0)
                    {
                        member.FailedScenarioAnchors.Add(member.ScenarioAnchorIndex);
                        Log.Write("Camp scenario anchor " + member.ScenarioAnchorIndex + " did not become usable for " + member.RosterId + "; trying another nearby point.");
                    }
                }
                else if (elapsed < 2800)
                {
                    return;
                }
                else
                {
                    member.FallbackFailuresInRound++;
                    Log.Write("Fallback idle scenario did not start for " + member.RosterId + "; trying another standing/sitting scenario.");
                }
                member.ScenarioTaskPending = false;
                member.PendingObjectScenario = false;
                member.ScenarioAttemptStartedAt = 0;
                member.ScenarioLastProgressAt = 0;
                member.ScenarioLastProgressDistance = float.MaxValue;
                member.ScenarioAnchorIndex = -1;
                try { TASK.CLEAR_PED_TASKS(member.Ped.Handle, true, false); } catch { }
            }

            if (!member.UsingFallbackScenario && TryStartNearestUnusedCampObjectScenario(member))
                return;

            // No unoccupied world scenario near the CampKit points became active.
            // Randomly cycle through valid stock standing and sitting scenarios.
            member.UsingFallbackScenario = true;
            StartNextRandomCampIdleScenario(member);
        }

        private bool TryStartNearestUnusedCampObjectScenario(CampboundRosterMember member)
        {
            List<Vector3> anchors = _bridge.GetPlayerCampScenarioAnchors(_faction);
            if (anchors == null || anchors.Count == 0) return false;

            Vector3 centre = _bridge.GetPlayerCampCentre(_faction);
            List<int> order = Enumerable.Range(0, anchors.Count)
                .Where(i => !member.FailedScenarioAnchors.Contains(i) && !IsCampScenarioAnchorReserved(i, anchors[i], member))
                .OrderBy(i => anchors[i].DistanceTo(member.Ped.Position))
                .ToList();
            foreach (int index in order)
            {
                Vector3 anchor = anchors[index];
                if (anchor.DistanceTo(centre) > 15f) continue;
                if (TryInvokeNearestScenarioTask(member.Ped, anchor, 3.25f))
                {
                    member.ScenarioAnchorIndex = index;
                    member.ScenarioAnchorPosition = anchor;
                    member.ScenarioLastProgressDistance = member.Ped.Position.DistanceTo(anchor);
                    member.ScenarioLastProgressAt = Game.GameTime;
                    member.ScenarioTaskPending = true;
                    member.PendingObjectScenario = true;
                    member.ScenarioAttemptStartedAt = Game.GameTime;
                    member.NextScenarioCheckAt = Game.GameTime + 450;
                    Log.Write("Trying nearest unused world scenario near CampKit anchor " + index + " for " + member.RosterId + ".");
                    return true;
                }
                member.FailedScenarioAnchors.Add(index);
            }
            return false;
        }

        private bool IsCampScenarioAnchorReserved(int anchorIndex, Vector3 anchor, CampboundRosterMember requester)
        {
            // Reserve both the chosen CampKit point and the actual location of any controlled ped
            // already entering/using a scenario. Different nearby prop anchors can resolve to the
            // same world scenario node, so index-only reservations are not sufficient.
            if (_campManagerIdle != null && !object.ReferenceEquals(_campManagerIdle, requester) &&
                IsScenarioSpotInUseBy(_campManagerIdle, anchorIndex, anchor)) return true;
            foreach (CampboundRosterMember other in _campboundRosterMembers.Values)
            {
                if (other == null || object.ReferenceEquals(other, requester) || other.Handle == requester.Handle) continue;
                if (IsScenarioSpotInUseBy(other, anchorIndex, anchor)) return true;
            }
            return false;
        }

        private static bool IsScenarioSpotInUseBy(CampboundRosterMember other, int anchorIndex, Vector3 anchor)
        {
            if (other == null || (!other.ScenarioTaskPending && !other.HasActiveCampScenario)) return false;
            if (other.ScenarioAnchorIndex == anchorIndex) return true;
            try
            {
                return other.Ped != null && other.Ped.Exists() && other.Ped.Position.DistanceTo(anchor) < 2.75f;
            }
            catch { return false; }
        }

        private bool TryInvokeNearestScenarioTask(Ped ped, Vector3 point, float radius)
        {
            if (ped == null || !ped.Exists()) return false;
            try
            {
                // RDR2 native 0x322BFDEA666E2B0E (_TASK_USE_NEAREST_SCENARIO_TO_COORD).
                // It walks to a nearby usable world scenario rather than warping the ped. The native
                // has ten parameters in RDR2; GTA V's unrelated six-parameter signature is not used.
                Function.Call(TaskUseNearestScenarioToCoordHash, new InputArgument[]
                {
                    ped.Handle, point.X, point.Y, point.Z, radius, -1, true, true, true, true
                });
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("RDR2 nearest-scenario task failed safely; using standing/sitting idle instead: " + ex.Message);
                return false;
            }
        }

        private void StartNextRandomCampIdleScenario(CampboundRosterMember member)
        {
            if (member == null || member.Ped == null || !member.Ped.Exists()) return;
            if (member.FallbackScenarioOrder.Count == 0 || member.FallbackScenarioIndex >= member.FallbackScenarioOrder.Count)
            {
                member.FallbackScenarioOrder.Clear();
                member.FallbackScenarioOrder.AddRange(new[]
                {
                    "WORLD_HUMAN_STAND_WAITING",
                    "WORLD_HUMAN_STARE_STOIC",
                    "WORLD_HUMAN_WAITING_IMPATIENT",
                    "WORLD_HUMAN_SIT_GROUND",
                    "WORLD_HUMAN_SIT_BACK_EXHAUSTED",
                    "WORLD_HUMAN_SIT_SMOKE",
                    "WORLD_HUMAN_SIT_GUITAR",
                    "WORLD_HUMAN_SIT_GUITAR_DOWNBEAT",
                    "WORLD_HUMAN_SIT_GUITAR_UPBEAT",
                    "WORLD_HUMAN_SIT_GROUND_READING",
                    "WORLD_HUMAN_SIT_GROUND_READING_BOOK",
                    "WORLD_HUMAN_SIT_GROUND_READ_NEWSPAPER",
                    "WORLD_HUMAN_SIT_GROUND_SKETCHING",
                    "WORLD_HUMAN_SIT_GROUND_WHITTLE",
                    "WORLD_HUMAN_SMOKE",
                    "WORLD_HUMAN_SMOKE_CIGAR",
                    "WORLD_HUMAN_COFFEE_DRINK",
                    "WORLD_HUMAN_LEAN_BACK_WALL",
                    "WORLD_HUMAN_LEAN_BACK_WALL_SMOKING",
                    "WORLD_HUMAN_GUARD_LEAN_WALL_MALE_E",
                    "WORLD_HUMAN_BADASS",
                    "WORLD_HUMAN_HANG_OUT_STREET",
                    "WORLD_HUMAN_HAMMERING",
                    "WORLD_HUMAN_DRINKING",
                    "WORLD_HUMAN_CAMP_FIRE_SIT",
                    "WORLD_HUMAN_CAMP_FIRE_STAND",
                    "WORLD_HUMAN_READ_NEWSPAPER",
                    "WORLD_HUMAN_WRITE_NOTEBOOK",
                    "WORLD_HUMAN_SEAT_LEDGE_HARMONICA",
                    "WORLD_HUMAN_SEAT_LEDGE_JAW_HARP",
                    "PROP_HUMAN_SEAT_CHAIR",
                    "PROP_HUMAN_SEAT_BENCH",
                    "WORLD_HUMAN_STAND_FISHING"
                });
                for (int i = member.FallbackScenarioOrder.Count - 1; i > 0; i--)
                {
                    int swap = _campScenarioRandom.Next(i + 1);
                    string temp = member.FallbackScenarioOrder[i];
                    member.FallbackScenarioOrder[i] = member.FallbackScenarioOrder[swap];
                    member.FallbackScenarioOrder[swap] = temp;
                }
                member.FallbackScenarioIndex = 0;
                member.FallbackFailuresInRound = 0;
            }

            string scenario = member.FallbackScenarioOrder[member.FallbackScenarioIndex++];
            try
            {
                TASK.CLEAR_PED_TASKS(member.Ped.Handle, true, false);
                TASK.TASK_START_SCENARIO_IN_PLACE_HASH(member.Ped.Handle,
                    MISC.GET_HASH_KEY(scenario), -1, true, 0U, -1f, false);
                member.ScenarioTaskPending = true;
                member.PendingObjectScenario = false;
                member.ScenarioAttemptStartedAt = Game.GameTime;
                member.ScenarioAnchorIndex = -1;
                member.ScenarioAnchorPosition = Vector3.Zero;
                member.ScenarioLastProgressDistance = float.MaxValue;
                member.ScenarioLastProgressAt = 0;
                member.NextScenarioCheckAt = Game.GameTime + 400;
                Log.Write("Trying random camp idle scenario " + scenario + " for " + member.RosterId + ".");
            }
            catch (Exception ex)
            {
                member.ScenarioTaskPending = false;
                member.FallbackFailuresInRound++;
                member.NextScenarioCheckAt = Game.GameTime + 1000;
                Log.Write("Could not start fallback scenario " + scenario + " for " + member.RosterId + ": " + ex.Message);
            }
        }

        private bool PlayCampSpeech(Ped ped, string speechName)
        {
            if (ped == null || !ped.Exists() || ped.IsDead || string.IsNullOrWhiteSpace(speechName)) return false;
            IntPtr speechPtr = IntPtr.Zero;
            IntPtr voicePtr = IntPtr.Zero;
            IntPtr parametersPtr = IntPtr.Zero;
            try
            {
                if (Function.Call<bool>(IsAmbientSpeechPlayingHash, new InputArgument[] { ped.Handle })) return false;

                // RDR2 0x8E04FEDD28D42462 expects a ScriptedSpeechParams* (0x40 bytes), not
                // GTA-style scalar speech arguments. Keep both UTF-8 strings and the native struct
                // alive only for the synchronous native call.
                speechPtr = AllocUtf8NullTerminated(speechName);
                voicePtr = AllocUtf8NullTerminated(CampSpeechVoice);
                parametersPtr = Marshal.AllocHGlobal(0x40);
                for (int i = 0; i < 0x40; i++) Marshal.WriteByte(parametersPtr, i, 0);
                Marshal.WriteInt64(parametersPtr, 0, speechPtr.ToInt64());
                Marshal.WriteInt64(parametersPtr, 8, voicePtr.ToInt64());
                Marshal.WriteInt32(parametersPtr, 16, 1); // variation
                Marshal.WriteInt32(parametersPtr, 24, unchecked((int)MISC.GET_HASH_KEY("SPEECH_PARAMS_FORCE")));
                Marshal.WriteInt64(parametersPtr, 32, 0L); // no explicit listener ped
                Marshal.WriteInt32(parametersPtr, 40, 1); // syncOverNetwork
                Marshal.WriteInt32(parametersPtr, 48, 1);
                Marshal.WriteInt32(parametersPtr, 56, 0); // Current SHVDN ScriptedSpeechParams uses v8=0.

                return Function.Call<bool>(PlayPedAmbientSpeechNativeHash, new InputArgument[]
                {
                    ped.Handle, unchecked((ulong)parametersPtr.ToInt64())
                });
            }
            catch (Exception ex)
            {
                if (!_speechWarningLogged)
                {
                    _speechWarningLogged = true;
                    Log.Write("RDR2 ambient speech native rejected the scripted speech parameters: " + ex.Message);
                }
                return false;
            }
            finally
            {
                if (parametersPtr != IntPtr.Zero) Marshal.FreeHGlobal(parametersPtr);
                if (voicePtr != IntPtr.Zero) Marshal.FreeHGlobal(voicePtr);
                if (speechPtr != IntPtr.Zero) Marshal.FreeHGlobal(speechPtr);
            }
        }

        private static IntPtr AllocUtf8NullTerminated(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes((value ?? string.Empty) + "\0");
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return pointer;
        }

        private bool IsCampPedSpeaking(Ped ped)
        {
            if (ped == null || !ped.Exists()) return false;
            try { return Function.Call<bool>(IsAmbientSpeechPlayingHash, new InputArgument[] { ped.Handle }); }
            catch { return false; }
        }

        private void MaintainCampAmbientConversation(Ped player)
        {
            if (player == null || !player.Exists() || player.Position.DistanceTo(_bridge.GetPlayerCampCentre(_faction)) > 135f)
                return;

            if (_pendingCampReplyHandle != 0 && Game.GameTime >= _pendingCampReplyAt)
            {
                CampboundRosterMember first = _campboundRosterMembers.Values.FirstOrDefault(m => m != null && m.Handle == _pendingCampInitiatorHandle);
                CampboundRosterMember second = _campboundRosterMembers.Values.FirstOrDefault(m => m != null && m.Handle == _pendingCampReplyHandle);
                if (first != null && second != null && first.Ped != null && first.Ped.Exists() && second.Ped != null && second.Ped.Exists() &&
                    first.Arrived && second.Arrived && first.Ped.Position.DistanceTo(second.Ped.Position) <= 12f &&
                    !IsCampPedSpeaking(first.Ped) && !IsCampPedSpeaking(second.Ped) &&
                    first.DefensiveTargetHandle == 0 && second.DefensiveTargetHandle == 0)
                {
                    string[] speechReplies = { "GENERIC_HI", "GENERIC_THANKS", "GENERIC_BYE" };
                    PlayCampSpeech(second.Ped, speechReplies[_campScenarioRandom.Next(speechReplies.Length)]);
                }
                _pendingCampReplyHandle = 0;
                _pendingCampInitiatorHandle = 0;
            }

            if (Game.GameTime < _nextCampConversationAt) return;
            List<CampboundRosterMember> nearby = _campboundRosterMembers.Values
                .Where(m => m != null && m.Arrived && m.DefensiveTargetHandle == 0 && m.Ped != null && m.Ped.Exists() && !m.Ped.IsDead &&
                            m.Ped.Position.DistanceTo(_bridge.GetPlayerCampCentre(_faction)) <= 18f && !IsCampPedSpeaking(m.Ped))
                .ToList();
            if (nearby.Count < 2)
            {
                _nextCampConversationAt = Game.GameTime + 10000;
                return;
            }

            CampboundRosterMember firstMember = nearby[_campScenarioRandom.Next(nearby.Count)];
            List<CampboundRosterMember> replyCandidates = nearby.Where(m => m.Handle != firstMember.Handle &&
                m.Ped.Position.DistanceTo(firstMember.Ped.Position) <= 12f).ToList();
            if (replyCandidates.Count == 0)
            {
                _nextCampConversationAt = Game.GameTime + 8000;
                return;
            }
            CampboundRosterMember secondMember = replyCandidates[_campScenarioRandom.Next(replyCandidates.Count)];
            string[] openers = { "GENERIC_HOWS_IT_GOING", "GENERIC_HI", "GENERIC_WHATS_UP" };
            string opener = openers[_campScenarioRandom.Next(openers.Length)];
            if (PlayCampSpeech(firstMember.Ped, opener))
            {
                _pendingCampInitiatorHandle = firstMember.Handle;
                _pendingCampReplyHandle = secondMember.Handle;
                _pendingCampReplyAt = Game.GameTime + 1800;
                _nextCampConversationAt = Game.GameTime + _campScenarioRandom.Next(30000, 55000);
                Log.Write("Queued ambient camp exchange between roster members " + firstMember.RosterId + " and " + secondMember.RosterId + ".");
            }
            else _nextCampConversationAt = Game.GameTime + 8000;
        }

        private void MaintainCampDefence(Ped player)
        {
            if (player == null || !player.Exists() || player.IsDead || Game.GameTime < _nextCampDefenceScanAt) return;
            _nextCampDefenceScanAt = Game.GameTime + 450;
            Vector3 centre = _bridge.GetPlayerCampCentre(_faction);
            if (centre == Vector3.Zero || player.Position.DistanceTo(centre) > 150f) return;

            List<CampboundRosterMember> members = _campboundRosterMembers.Values
                .Where(m => m != null && m.Arrived && m.Ped != null && m.Ped.Exists() && !m.Ped.IsDead &&
                            m.Ped.Position.DistanceTo(centre) <= 55f)
                .ToList();
            if (members.Count == 0) return;
            HashSet<int> playerFactionHandles = _bridge.GetKnownPlayerFactionPedHandles(_faction);
            foreach (CampboundRosterMember member in members) playerFactionHandles.Add(member.Handle);

            Ped[] worldPeds;
            try { worldPeds = World.GetAllPeds(); }
            catch { worldPeds = new Ped[0]; }
            foreach (Ped aggressor in worldPeds)
            {
                if (aggressor == null || !aggressor.Exists() || aggressor.IsDead || !aggressor.IsHuman ||
                    aggressor.Handle == player.Handle || playerFactionHandles.Contains(aggressor.Handle) ||
                    aggressor.Position.DistanceTo(centre) > 48f)
                    continue;

                bool directlyAttacking = false;
                try { directlyAttacking = PED.IS_PED_IN_COMBAT(aggressor.Handle, player.Handle); } catch { }
                if (!directlyAttacking)
                {
                    foreach (CampboundRosterMember target in members)
                    {
                        try
                        {
                            if (PED.IS_PED_IN_COMBAT(aggressor.Handle, target.Handle))
                            {
                                directlyAttacking = true;
                                break;
                            }
                        }
                        catch { }
                    }
                }
                if (!directlyAttacking && _campManager != null && _campManager.Exists())
                {
                    try { directlyAttacking = PED.IS_PED_IN_COMBAT(aggressor.Handle, _campManager.Handle); } catch { }
                }
                if (!directlyAttacking) continue; // Walking past the camp is not a hostile act.

                foreach (CampboundRosterMember defender in members)
                {
                    if (defender.Ped.Position.DistanceTo(aggressor.Position) > 50f) continue;
                    if (defender.DefensiveTargetHandle != 0 && defender.DefensiveTargetHandle != aggressor.Handle)
                    {
                        Ped oldTarget = null;
                        try { oldTarget = new Ped(defender.DefensiveTargetHandle); } catch { }
                        bool oldStillThreatening = oldTarget != null && oldTarget.Exists() &&
                            (PedCombatting(oldTarget, player) || PedCombatting(oldTarget, defender.Ped));
                        if (oldStillThreatening) continue;
                        StopCampMemberDefence(defender);
                    }
                    StartCampMemberDefence(defender, aggressor, player);
                }
            }

            // End the defensive task when the assailant is no longer fighting anyone in the camp
            // or the player. Only a previously identified aggressor is ever followed/attacked.
            foreach (CampboundRosterMember defender in members)
            {
                if (defender.DefensiveTargetHandle == 0) continue;
                Ped target = null;
                try { target = new Ped(defender.DefensiveTargetHandle); } catch { }
                bool targetStillCombat = target != null && target.Exists() && !target.IsDead &&
                    (PedCombatting(target, defender.Ped) || PedCombatting(target, player));
                if (targetStillCombat)
                {
                    defender.LastDefensiveContactAt = Game.GameTime;
                    if (!PedCombatting(defender.Ped, target)) StartCampMemberDefence(defender, target, player);
                }
                else if (Game.GameTime - defender.LastDefensiveContactAt > 3000)
                {
                    Log.Write("Camp resident " + defender.RosterId + " returned to camp idle after threat ended.");
                    StopCampMemberDefence(defender);
                }
            }
        }

        private static bool PedCombatting(Ped ped, Ped target)
        {
            if (ped == null || target == null || !ped.Exists() || !target.Exists()) return false;
            try { return PED.IS_PED_IN_COMBAT(ped.Handle, target.Handle); }
            catch { return false; }
        }

        private void StartCampMemberDefence(CampboundRosterMember member, Ped aggressor, Ped player)
        {
            if (member == null || member.Ped == null || !member.Ped.Exists() || aggressor == null || !aggressor.Exists() ||
                member.Handle == aggressor.Handle || aggressor.Handle == (player != null && player.Exists() ? player.Handle : 0)) return;
            bool firstOrder = member.DefensiveTargetHandle != aggressor.Handle;
            member.DefensiveTargetHandle = aggressor.Handle;
            member.LastDefensiveContactAt = Game.GameTime;
            member.HasActiveCampScenario = false;
            member.ScenarioTaskPending = false;
            member.PendingObjectScenario = false;
            member.ScenarioAnchorIndex = -1;
            member.NextScenarioCheckAt = Game.GameTime + 1000;
            try
            {
                member.Ped.BlockPermanentEvents = false;
                PED.SET_PED_COMBAT_ATTRIBUTES(member.Ped.Handle, 5, true);
                PED.SET_PED_COMBAT_ATTRIBUTES(member.Ped.Handle, 46, true);
                if (firstOrder || !PedCombatting(member.Ped, aggressor))
                {
                    TASK.CLEAR_PED_TASKS(member.Ped.Handle, true, false);
                    Function.Call(TaskCombatPedHash, new InputArgument[] { member.Ped.Handle, aggressor.Handle, 0, 0 });
                    Log.Write("Camp resident " + member.RosterId + " defending camp against a detected aggressor (handle=" + aggressor.Handle + ").");
                }
            }
            catch (Exception ex) { Log.Write("Camp defence task failed for " + member.RosterId + ": " + ex.Message); }
        }

        private void StopCampMemberDefence(CampboundRosterMember member)
        {
            if (member == null || member.Ped == null || !member.Ped.Exists()) return;
            member.DefensiveTargetHandle = 0;
            member.LastDefensiveContactAt = 0;
            try
            {
                TASK.CLEAR_PED_TASKS(member.Ped.Handle, true, false);
                member.Ped.BlockPermanentEvents = true;
                PED.SET_PED_COMBAT_ATTRIBUTES(member.Ped.Handle, 5, false);
                PED.SET_PED_COMBAT_ATTRIBUTES(member.Ped.Handle, 46, false);
                member.HasActiveCampScenario = false;
                member.ScenarioTaskPending = false;
                member.PendingObjectScenario = false;
                member.UsingFallbackScenario = false;
                member.ScenarioAnchorIndex = -1;
                member.FailedScenarioAnchors.Clear();
                member.NextScenarioCheckAt = Game.GameTime + 800;
            }
            catch (Exception ex) { Log.Write("Camp defender passive reset warning: " + ex.Message); }
        }

        private void MaintainCampManager(Ped player)
        {
            if (!_faction.Founded || string.IsNullOrWhiteSpace(_faction.CampSiteId))
            {
                DeleteCampManager();
                return;
            }

            Vector3 campCentre = _bridge.GetPlayerCampCentre(_faction);
            ReconnectCampManager(campCentre);
            float distanceToCamp = player.Position.DistanceTo(campCentre);
            if (distanceToCamp > 180f)
            {
                DeleteCampManager();
                return;
            }

            if ((_campManager == null || !_campManager.Exists()) && distanceToCamp <= 85f && Game.GameTime >= _nextCampManagerSpawnAt)
            {
                _nextCampManagerSpawnAt = Game.GameTime + 3500;
                SpawnCampManager(campCentre);
            }

            if (_campManager == null || !_campManager.Exists()) return;
            if (_campManagerIdle == null || _campManagerIdle.Handle != _campManager.Handle)
            {
                bool alreadyInScenario = false;
                try { alreadyInScenario = PED.IS_PED_USING_ANY_SCENARIO(_campManager.Handle); } catch { }
                _campManagerIdle = new CampboundRosterMember
                {
                    Handle = _campManager.Handle,
                    Ped = _campManager,
                    RosterId = "Administrador del campamento",
                    Destination = campCentre,
                    Arrived = alreadyInScenario,
                    HasActiveCampScenario = alreadyInScenario,
                    ScenarioAnchorIndex = -1,
                    NextScenarioCheckAt = Game.GameTime + 500
                };
            }
            else if (!_campManagerIdle.Arrived)
            {
                _campManagerIdle.Destination = campCentre;
            }

            // The administrator uses the same unoccupied-object scenario allocator as other roster peds.
            // Guitar is one of the possible ambience scenarios, not a hardcoded permanent task.
            UpdateRosterMemberReturn(_campManagerIdle);
            if (Game.GameTime >= _nextCampManagerProtectionAt)
            {
                _nextCampManagerProtectionAt = Game.GameTime + 500;
                ApplyProtectedAmbientPed(_campManager, player, "Camp manager");
            }

            float distance = player.Position.DistanceTo(_campManager.Position);
            if (distance <= _config.InteractionDistance + 1.5f) DisableHostileMeleeControls();
            if (distance <= 2.8f && !player.IsOnMount && !player.IsInVehicle && !_factionMenuOpen && !_keyboardOpen)
                UpdateCampManagerPrompt(player);
            else
            {
                SetPromptVisible(_campManagerPrompt, false);
                _campManagerPromptArmed = true;
            }
        }

        private void SpawnCampManager(Vector3 campCentre)
        {
            try
            {
                // Start close to the CampKit's actual centre; the allocator can move the manager a few
                // metres to an available seat/fire/prop scenario without piling him directly on a prop.
                Vector3 position = new Vector3(campCentre.X + 1.7f, campCentre.Y + 1.0f, campCentre.Z + 0.25f);
                STREAMING.REQUEST_COLLISION_AT_COORD(position.X, position.Y, position.Z);
                Ped ped = _bridge.SpawnFounder(_config.FounderModel, position, _config.FounderHeading);
                if (ped == null || !ped.Exists())
                {
                    Log.Write("Camp administrator spawn failed at " + position + ".");
                    return;
                }
                _campManager = ped;
                _bridge.MarkPedAsEnRageOwned(ped);
                _faction.CampManagerPedHandle = ped.Handle;
                try { _faction.Save(); } catch (Exception saveEx) { Log.Write("Camp administrator handle save warning: " + saveEx.Message); }
                _campManagerPromptArmed = true;
                _campManagerIdle = new CampboundRosterMember
                {
                    Handle = ped.Handle,
                    Ped = ped,
                    RosterId = "Administrador del campamento",
                    Destination = campCentre,
                    Arrived = false,
                    ScenarioAnchorIndex = -1,
                    NextScenarioCheckAt = Game.GameTime + 400
                };
                ApplyProtectedAmbientPed(ped, Game.Player.Character, "Camp manager");
                Log.Write("Camp administrator spawned near CampKit centre: handle=" + ped.Handle + " at " + position + ".");
            }
            catch (Exception ex) { Log.Write("Camp administrator spawn exception: " + ex); }
        }

        private void ReconnectCampManager(Vector3 campCentre)
        {
            if (_campManager != null && _campManager.Exists()) return;
            if (_faction.CampManagerPedHandle == 0) return;
            try
            {
                Ped candidate = new Ped(_faction.CampManagerPedHandle);
                uint expectedModel = MISC.GET_HASH_KEY(_config.FounderModel);
                bool belongsToCamp = candidate.Exists() && candidate.Position.DistanceTo(campCentre) <= 25f;
                bool expectedPed = candidate.Exists() && unchecked((uint)candidate.Model.Hash) == expectedModel;
                if (belongsToCamp && expectedPed)
                {
                    _campManager = candidate;
                    _bridge.MarkPedAsEnRageOwned(candidate);
                    bool alreadyInScenario = false;
                    try { alreadyInScenario = PED.IS_PED_USING_ANY_SCENARIO(candidate.Handle); } catch { }
                    _campManagerIdle = new CampboundRosterMember
                    {
                        Handle = candidate.Handle,
                        Ped = candidate,
                        RosterId = "Administrador del campamento",
                        Destination = campCentre,
                        Arrived = alreadyInScenario,
                        HasActiveCampScenario = alreadyInScenario,
                        ScenarioAnchorIndex = -1,
                        NextScenarioCheckAt = Game.GameTime + 500
                    };
                    _campManagerPromptArmed = true;
                    Log.Write("Reconnected to existing camp administrator after script reload: handle=" + candidate.Handle + ".");
                    return;
                }
            }
            catch (Exception ex) { Log.Write("Camp administrator reconnection warning: " + ex.Message); }
            _faction.CampManagerPedHandle = 0;
            _campManagerIdle = null;
            try { _faction.Save(); } catch (Exception saveEx) { Log.Write("Stale camp administrator handle clear warning: " + saveEx.Message); }
        }

        private void DeleteCampManager()
        {
            RemoveCampManagerPrompt();
            try
            {
                if (_campManager != null && _campManager.Exists()) _campManager.Delete();
            }
            catch (Exception ex) { Log.Write("Camp administrator cleanup warning: " + ex.Message); }
            _campManager = null;
            _campManagerIdle = null;
            if (_faction.CampManagerPedHandle != 0)
            {
                _faction.CampManagerPedHandle = 0;
                try { _faction.Save(); } catch (Exception saveEx) { Log.Write("Camp administrator handle cleanup save warning: " + saveEx.Message); }
            }
            _campManagerPromptArmed = true;
        }

        private void PlaceCampForExistingFaction(Vector3 campPosition, string campLabel)
        {
            if (!NeedsCampSelection) return;
            string error;
            if (!_bridge.EnsurePlayerFaction(_faction, out error))
            {
                Notify("No se pudo preparar la banda para crear el campamento: " + error);
                return;
            }

            string siteId, region, campError;
            if (!_bridge.AddPlayerCampSite(_faction, campPosition, "chosen from the player faction leader page", out siteId, out region, out campError))
            {
                Notify("No se pudo colocar el campamento en " + campLabel + ". " + campError + " Elige otra ubicación.");
                BeginExistingCampLocationKeyboard();
                return;
            }

            try
            {
                _faction.Save();
                _campPropsRestoredThisRuntime = false;
                _nextCampPropsAttemptAt = 0;
                EnsureCampBlip(true);
                string resourceError;
                _bridge.SyncFactionResources(_faction, out resourceError);
                if (!string.IsNullOrEmpty(resourceError)) Log.Write("Resource sync after camp placement warning: " + resourceError);
                Notify("Campamento establecido para " + _faction.Name + ": " + campLabel + ".");
                Log.Write("PLAYER FACTION CAMP PLACED: " + siteId + " (" + region + ").");
            }
            catch (Exception ex)
            {
                Log.Write("Existing faction camp data save failed: " + ex);
                Notify("El campamento fue creado por enRAGE, pero no se pudo actualizar PlayerFaction.json. Revisa extension.log.");
            }
        }

        private void CompleteFounding(Vector3 campPosition, string campLabel)
        {
            _faction.Founded = true;
            _faction.Name = _pendingFactionName;
            _faction.LeaderDisplayName = string.Empty;
            _faction.LeaderName = string.Empty;
            _faction.Id = "player_faction";
            _faction.SchemaVersion = 10;
            // Every new founding starts with an empty Chronicle force/economy, even if an older
            // unfounded save still contains stale totals from a previous band. SyncFactionResources
            // will baseline old roster entries so only new recruits add manpower.
            _faction.ResourcesInitialized = false;
            _faction.RosterContributions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _faction.UncountedRosterIds = new List<string>();
            _faction.ManpowerBaselineInitialized = false;
            _faction.CurrentMen = 0;
            _faction.CurrentSupplies = 0;
            _faction.CurrentTreasury = 0;

            try
            {
                // Persist the faction intent before AddCampSite saves enRAGE's own camp file.
                _faction.Save();
            }
            catch (Exception ex)
            {
                _faction.Founded = false;
                Notify("No se pudo guardar la facción. Revisa extension.log.");
                Log.Write("Faction creation initial save failed: " + ex);
                _promptArmed = true;
                return;
            }

            string error;
            if (!_bridge.EnsurePlayerFaction(_faction, out error))
            {
                _faction.Founded = false;
                try { _faction.Save(); } catch { }
                Notify("No se pudo fundar la banda: " + error);
                _promptArmed = true;
                return;
            }

            string siteId, region, campError;
            if (!_bridge.AddPlayerCampSite(_faction, campPosition, "chosen during player-faction founding", out siteId, out region, out campError))
            {
                _faction.Founded = false;
                try { _faction.Save(); } catch { }
                Notify("No se pudo colocar el campamento en " + campLabel + ". " + campError + " Elige otra ubicación.");
                _promptArmed = true;
                BeginCampLocationKeyboard();
                return;
            }

            _factionRegistered = true;
            _campPropsRestoredThisRuntime = false;
            _nextCampPropsAttemptAt = 0;
            try
            {
                _faction.Save();
            }
            catch (Exception ex)
            {
                // enRAGE's own CampsContent was already saved. Leave the faction active;
                // startup recovery will discover that CampSite from Camps.Extra.
                Log.Write("Faction final save failed after native camp creation: " + ex);
                Notify("La banda fue creada, pero no se guardaron todos los datos de extensión. Revisa extension.log.");
            }

            string resourceError;
            _bridge.SyncFactionResources(_faction, out resourceError);
            if (!string.IsNullOrEmpty(resourceError)) Log.Write("Initial faction resource sync warning: " + resourceError);

            Log.Write("PLAYER FACTION FOUNDED: " + _faction.Name + " (" + _faction.Id + "), camp=" + siteId + " (" + region + ").");
            EnsureCampBlip(true);
            Notify("Has fundado " + _faction.Name + ". Eres su líder. Campamento: " + campLabel + ". El campamento físico se monta al acercarte a la ubicación.");
            FounderPatches.FounderHandle = 0;
            RemovePrompt();
            RemoveFounderBlip();
            DeleteFounder();
        }

        private static string SanitizeFactionName(string input)
        {
            string cleaned = new string((input ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (cleaned.Length > 28) cleaned = cleaned.Substring(0, 28).Trim();
            return cleaned;
        }

        private void Notify(string message)
        {
            try
            {
                object instance = _bridge.MainInstance;
                if (instance != null)
                {
                    MethodInfo method = instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(m => m.Name == "ShowNotification" && m.GetParameters().Length == 1 &&
                                             m.GetParameters()[0].ParameterType == typeof(string));
                    if (method != null)
                    {
                        method.Invoke(instance, new object[] { "enRAGE - " + message });
                        return;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Notification warning: " + ex.Message); }
            Log.Write("NOTICE: " + message);
        }

        private void DeleteFounder()
        {
            try
            {
                if (_founder != null && _founder.Exists())
                    _founder.Delete();
            }
            catch (Exception ex) { Log.Write("Founder cleanup warning: " + ex.Message); }
            _founder = null;
        }

        private unsafe void RemoveFounderBlip()
        {
            int handle = _founderBlip != 0 ? _founderBlip : _faction.FounderBlipHandle;
            if (handle == 0) return;

            try
            {
                if (MAP.DOES_BLIP_EXIST(handle))
                {
                    int nativeHandle = handle;
                    MAP.REMOVE_BLIP(&nativeHandle);
                }

                // Keep the handle while it still exists. Tick() calls this again in the
                // founded branch, so a transient native/UI delay cannot leave our marker up.
                if (MAP.DOES_BLIP_EXIST(handle))
                {
                    _founderBlip = handle;
                    _faction.FounderBlipHandle = 0;
                    return;
                }

                _founderBlip = 0;
                _faction.FounderBlipHandle = 0;
                try { _faction.Save(); } catch (Exception saveEx) { Log.Write("Founder blip handle clear warning: " + saveEx.Message); }
                Log.Write("Founder blip removed successfully (handle=" + handle + ").");
            }
            catch (Exception ex)
            {
                _founderBlip = handle;
                _faction.FounderBlipHandle = 0;
                Log.Write("Founder blip cleanup will retry: " + ex.Message);
            }
        }
    }
}
