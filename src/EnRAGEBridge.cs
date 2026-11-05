using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RDR2;
using RDR2.Math;
using RDR2.Native;

namespace EnRAGEPlayerFactionExtension
{
    /// <summary>
    /// Reflection-only bridge to the inspected enRAGE build. It deliberately does not reference
    /// NemesisMod.dll at compile time, so it can be installed beside the existing Companion extension.
    /// </summary>
    internal sealed class EnRAGEBridge
    {
        internal sealed class ReleasedRosterPed
        {
            internal int Handle;
            internal Ped Ped;
            internal string RosterId;
        }

        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const string HarmonyId = "alexfandelcafe.enrage.player-faction.extension";

        private Type _mainType;
        private Type _gangsType;
        private Type _gangType;
        private Type _factionContentType;
        private Type _rosterMenuType;
        private Type _campsType;
        private Type _campSiteType;
        private Type _campKitType;
        private Type _themeType;
        private int _lastDefinedCampVisualTier = -1;
        private int _lastAppliedCampVisualTier = -1;
        private FieldInfo _definedField;
        private FieldInfo _extraCampSitesField;
        private MethodInfo _setAllMethod;
        private MethodInfo _fillNewMethod;
        private MethodInfo _spawnPedMethod;
        private MethodInfo _makeOursMethod;
        private MethodInfo _landMethod;
        private MethodInfo _addCampSiteMethod;
        private MethodInfo _saveCampSitesMethod;
        private Harmony _harmony;
        private bool _patched;
        private bool _companionFriendshipPatched;
        private bool _companionRosterCommitPatched;
        private bool _companionNativeSocialPrefixPatched;
        private bool _companionDismissedProfilePatched;
        private bool _companionReleasedProfilePatched;
        private bool _companionCampManagerRecruitmentGuardPatched;
        private int _nextCompanionRosterNormalizeAt;
        private int _nextPlayerFactionNeutralityAt;

        internal bool IsAvailable { get { return _mainType != null && _gangsType != null; } }
        internal object MainInstance { get { return FounderPatches.MainInstance; } }

        internal bool TryAttach()
        {
            if (IsAvailable)
            {
                // ScriptHook can abort/recreate scripts while this bridge instance is retained by
                // an internal retry path. If its Harmony owner was cleaned during Aborted, reinstall
                // the enRAGE hooks before reporting the bridge ready again.
                if (_harmony == null || !_patched)
                    InstallHarmonyPatches();
                TryInstallCompanionIntegrationPatches();
                return true;
            }

            try
            {
                _mainType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => SafeGetType(a, "NemesisSystem.NemesisMod"))
                    .FirstOrDefault(t => t != null);
                if (_mainType == null)
                    return false;

                Assembly assembly = _mainType.Assembly;
                _gangsType = assembly.GetType("NemesisSystem.Gangs", false);
                _gangType = assembly.GetType("NemesisSystem.Gang", false);
                _factionContentType = assembly.GetType("NemesisSystem.FactionContent", false);
                _rosterMenuType = assembly.GetType("NemesisSystem.RosterMenu", false);
                _themeType = assembly.GetType("NemesisSystem.Theme", false);
                _campsType = assembly.GetType("NemesisSystem.Camps", false);
                _campSiteType = assembly.GetType("NemesisSystem.CampSite", false);
                _campKitType = assembly.GetType("NemesisSystem.CampKit", false);
                if (_gangsType == null || _gangType == null || _factionContentType == null)
                {
                    Log.Write("enRAGE types incomplete; bridge will retry.");
                    _mainType = null;
                    return false;
                }

                _definedField = _gangsType.GetField("_defined", StaticFlags);
                _setAllMethod = _gangsType.GetMethod("SetAll", StaticFlags);
                _fillNewMethod = _factionContentType.GetMethod("FillNew", StaticFlags);
                _spawnPedMethod = _mainType.GetMethod("SpawnPed", StaticFlags, null,
                    new[] { typeof(PedHash), typeof(Vector3), typeof(float) }, null);
                _makeOursMethod = _mainType.GetMethod("MakeOurs", StaticFlags, null, new[] { typeof(int) }, null);
                _landMethod = _mainType.GetMethod("Land", StaticFlags, null, new[] { typeof(Ped), typeof(Vector3) }, null);
                _addCampSiteMethod = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m =>
                    m.Name == "AddCampSite" && m.GetParameters().Length == 3 &&
                    m.GetParameters()[0].ParameterType == _gangType &&
                    m.GetParameters()[1].ParameterType == typeof(Vector3) &&
                    m.GetParameters()[2].ParameterType == typeof(string));
                _extraCampSitesField = _campsType != null ? _campsType.GetField("Extra", StaticFlags) : null;
                _saveCampSitesMethod = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m =>
                    m.Name == "SaveCampSites" && m.GetParameters().Length == 0);

                if (_definedField == null || _setAllMethod == null || _fillNewMethod == null || _spawnPedMethod == null)
                {
                    Log.Write("Required enRAGE reflection members not found; bridge will retry. " +
                              "defined=" + (_definedField != null) + ", SetAll=" + (_setAllMethod != null) +
                              ", FillNew=" + (_fillNewMethod != null) + ", SpawnPed=" + (_spawnPedMethod != null));
                    _mainType = null;
                    return false;
                }

                InstallHarmonyPatches();
                Log.Write("Attached to " + _mainType.Assembly.GetName().Name + ".");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("TryAttach failed: " + Unwrap(ex));
                _mainType = null;
                return false;
            }
        }

        internal Ped SpawnFounder(string modelName, Vector3 position, float heading)
        {
            if (!IsAvailable || _spawnPedMethod == null)
                return null;

            // Older INI files may still contain the former, misspelled Rhodes model name.
            // Try the configured value first, then known stock ranch/town peds.
            string[] candidates = new[]
            {
                modelName,
                "U_M_M_Rwfrancher_01",
                "U_M_M_RhdBartender_01",
                "U_M_M_ValTownfolk_01"
            }.Where(x => !string.IsNullOrWhiteSpace(x))
             .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            foreach (string candidate in candidates)
            {
                try
                {
                    uint hash = MISC.GET_HASH_KEY(candidate);
                    PedHash pedHash = (PedHash)Enum.ToObject(typeof(PedHash), hash);
                    Model model = new Model(pedHash);
                    if (!model.IsValid || !model.IsPed)
                    {
                        Log.Write("Founder model is invalid or not a Ped: " + candidate + ".");
                        continue;
                    }
                    if (!model.IsLoaded && !model.Request(10000))
                    {
                        Log.Write("Founder model did not load within 10 seconds: " + candidate + ".");
                        continue;
                    }

                    Log.Write("Founder model loaded: " + candidate + " (0x" + hash.ToString("X8") + ").");
                    Ped ped = null;
                    try
                    {
                        // Prefer enRAGE's own spawner so it performs its normal registration,
                        // voice and ground/collision setup.
                        ped = _spawnPedMethod.Invoke(null, new object[] { pedHash, position, heading }) as Ped;
                    }
                    catch (Exception ex)
                    {
                        Log.Write("enRAGE SpawnPed failed for " + candidate + ": " + Unwrap(ex));
                    }

                    if (ped != null && ped.Exists())
                        return ped;

                    // enRAGE intentionally refuses to spawn while a mission/fade flag is active.
                    // Use the same SHVDN world spawner as a fallback and register it as enRAGE-owned.
                    try
                    {
                        STREAMING.REQUEST_COLLISION_AT_COORD(position.X, position.Y, position.Z);
                        ped = World.CreatePed(pedHash, position, heading);
                        if (ped != null && ped.Exists())
                        {
                            if (_makeOursMethod != null)
                                _makeOursMethod.Invoke(null, new object[] { ped.Handle });
                            if (_landMethod != null)
                                _landMethod.Invoke(null, new object[] { ped, position });
                            Log.Write("Founder spawned through SHVDN fallback: " + candidate + ", handle=" + ped.Handle + ".");
                            return ped;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("SHVDN fallback spawn failed for " + candidate + ": " + Unwrap(ex));
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Founder model attempt failed for " + candidate + ": " + Unwrap(ex));
                }
            }

            Log.Write("Founder spawn failed: none of the configured/fallback ped models could be created.");
            return null;
        }

        internal bool EnsurePlayerFaction(PlayerFactionData data, out string error)
        {
            error = null;
            if (!IsAvailable || data == null || !data.Founded)
            {
                error = "enRAGE todavía no está disponible o no hay facción fundada.";
                return false;
            }

            try
            {
                Array current = _definedField.GetValue(null) as Array;
                List<object> gangs = new List<object>();
                if (current != null)
                {
                    foreach (object gang in current)
                        if (gang != null) gangs.Add(gang);
                }

                object playerGang = gangs.FirstOrDefault(g =>
                    string.Equals(GetField(g, "Id") as string, data.Id, StringComparison.OrdinalIgnoreCase));
                bool isNew = playerGang == null;
                bool nameChanged = isNew || !string.Equals(GetField(playerGang, "Name") as string,
                    data.Name, StringComparison.Ordinal);

                if (isNew)
                {
                    playerGang = Activator.CreateInstance(_gangType, true);
                    SetField(playerGang, "Id", data.Id);
                    gangs.Add(playerGang);
                }

                SetField(playerGang, "Id", data.Id);
                SetField(playerGang, "Name", data.Name);
                SetField(playerGang, "ModelTokens", new string[0]);

                // Player-owned factions should not randomly ambush or feud with their founder.
                SetField(playerGang, "HasCamps", true);
                SetField(playerGang, "EncounterRate", 0.0);
                SetField(playerGang, "AmbushBase", 0.0);
                SetField(playerGang, "FixedHate", 0);
                SetField(playerGang, "StoryOnly", false);
                SetField(playerGang, "Era", 0);

                Array tiers = GetField(playerGang, "Tiers") as Array;
                if (isNew || tiers == null || tiers.Length == 0)
                    _fillNewMethod.Invoke(null, new[] { playerGang });

                // Apply persisted player-configurable faction flags after FillNew, since
                // FillNew may overwrite defaults on first registration.
                SetField(playerGang, "HasCamps", true);
                ApplyGangBooleanSetting(playerGang, "TurfWars", data.AllowTerritoryWars);
                ApplyGangBooleanSetting(playerGang, "Feuds", data.AllowFeuds);
                ApplyGangBooleanSetting(playerGang, "GangLifeEvents", data.AllowGangLifeEvents);
                ApplyGangBooleanSetting(playerGang, "AnswersGunfire", data.AllowAnswersGunfire);
                bool rankLabelsChanged = ApplySavedTierLabels(playerGang, data);

                // FillNew may initialize faction defaults, so apply the real player leader
                // after it and keep enRAGE's native leader card/cache synchronized.
                string playerName = ApplyNativePlayerLeader(playerGang, data);
                data.LeaderName = playerName;
                SetField(playerGang, "LeaderName", playerName);
                SetField(playerGang, "LoreLeader", playerName);
                string leaderBlurb = playerName + " dirige " + data.Name + ".";
                SetField(playerGang, "LeaderBlurb", leaderBlurb);
                SetField(playerGang, "LoreBlurb", leaderBlurb);

                // SetAll is needed for a newly inserted faction or a renamed one. Leader
                // metadata and the _leaderPages cache are updated in place on normal retries.
                if (nameChanged || rankLabelsChanged)
                {
                    Array newArray = Array.CreateInstance(_gangType, gangs.Count);
                    for (int i = 0; i < gangs.Count; i++)
                        newArray.SetValue(gangs[i], i);
                    _setAllMethod.Invoke(null, new object[] { newArray });
                }

                Log.Write("Registered player faction '" + data.Name + "' (" + data.Id + ") with native leader '" + playerName + "'. Defined factions=" + gangs.Count);
                return true;
            }
            catch (Exception ex)
            {
                error = Unwrap(ex);
                Log.Write("Faction registration failed: " + error);
                return false;
            }
        }

        private static void ApplyGangBooleanSetting(object gang, string fieldName, bool desired)
        {
            object previous = GetField(gang, fieldName);
            if (!(previous is bool))
                throw new MissingFieldException(gang == null ? "NemesisSystem.Gang" : gang.GetType().FullName, fieldName);
            SetField(gang, fieldName, desired);
            object actual = GetField(gang, fieldName);
            if (!(actual is bool) || (bool)actual != desired)
                throw new InvalidOperationException("enRAGE no confirmó la configuración Gang." + fieldName + "=" + desired + ".");
            if ((bool)previous != desired)
                Log.Write("Verified live enRAGE flag " + fieldName + "=" + desired + " for player_faction.");
        }

        private string ApplyNativePlayerLeader(object playerGang, PlayerFactionData data)
        {
            string playerId = null;
            try
            {
                PropertyInfo youId = _mainType.GetProperty("YouId", StaticFlags);
                if (youId != null)
                    playerId = youId.GetValue(null, null) as string;
            }
            catch (Exception ex)
            {
                Log.Write("Could not read enRAGE YouId: " + Unwrap(ex));
            }

            string fallbackName = string.Equals(playerId, "vdl_john", StringComparison.OrdinalIgnoreCase)
                ? "John Marston" : "Arthur Morgan";
            object main = FounderPatches.MainInstance;
            object playerRecord = null;
            if (main != null && !string.IsNullOrWhiteSpace(playerId))
            {
                IEnumerable storyRoster = GetField(main, "StoryRoster") as IEnumerable;
                if (storyRoster != null)
                {
                    foreach (object record in storyRoster)
                    {
                        if (string.Equals(GetField(record, "Id") as string, playerId, StringComparison.OrdinalIgnoreCase))
                        {
                            playerRecord = record;
                            break;
                        }
                    }
                }
            }

            string playerName = playerRecord != null ? (GetField(playerRecord, "Name") as string) : null;
            if (string.IsNullOrWhiteSpace(playerName))
                playerName = fallbackName;

            string displayName = !string.IsNullOrWhiteSpace(data.LeaderDisplayName)
                ? data.LeaderDisplayName.Trim() : playerName;
            SetField(playerGang, "LeaderName", displayName);
            SetField(playerGang, "LoreLeader", displayName);
            FounderPatches.PlayerFactionId = data.Id;
            FounderPatches.PlayerRecordId = playerId;

            if (playerRecord == null || main == null)
            {
                Log.Write("Native player leader page is waiting for enRAGE StoryRoster. YouId=" + (playerId ?? "<unavailable>") + ".");
                return displayName;
            }

            try
            {
                FieldInfo cacheField = _mainType.GetField("_leaderPages", InstanceFlags);
                IDictionary leaderPages = cacheField != null ? cacheField.GetValue(main) as IDictionary : null;
                if (leaderPages == null)
                {
                    Log.Write("enRAGE _leaderPages cache was not available; leader name is set but native page is pending.");
                    return displayName;
                }

                // Reuse the existing presentation object when possible. RosterMenu may
                // currently be displaying that exact object in _detail; mutating it in place
                // makes the new visual name visible immediately after the keyboard closes.
                object leaderPage = leaderPages.Contains(data.Id) ? leaderPages[data.Id] : null;
                if (leaderPage == null || !string.Equals(GetField(leaderPage, "Id") as string, playerId, StringComparison.OrdinalIgnoreCase))
                {
                    MethodInfo cloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (cloneMethod == null)
                        throw new MissingMethodException("object.MemberwiseClone");
                    leaderPage = cloneMethod.Invoke(playerRecord, null);
                }

                // This is a presentation/leader-seat copy of the native StoryRoster record.
                // Keep YouId and the character's real stats/name, but do not mutate the
                // Van der Linde StoryRoster entry or place the player in the NPC roster.
                SetField(leaderPage, "Gang", data.Id);
                SetField(leaderPage, "Story", false);
                SetField(leaderPage, "LeaderPage", true);
                SetField(leaderPage, "Person", false);
                SetField(leaderPage, "Rank", 5);
                SetField(leaderPage, "IsAlive", true);
                SetField(leaderPage, "PresumedDead", false);
                SetField(leaderPage, "Title", "(tú)");
                // The visual alias belongs only to this presentation clone. The original
                // StoryRoster record and YouId remain vdl_arthur / vdl_john.
                SetField(leaderPage, "Name", displayName);

                try
                {
                    Ped controlledPed = Game.Player.Character;
                    if (controlledPed != null && controlledPed.Exists())
                        SetField(leaderPage, "ModelHash", unchecked((uint)controlledPed.Model.Hash));
                }
                catch (Exception ex)
                {
                    Log.Write("Could not refresh player leader model hash: " + ex.Message);
                }

                leaderPages[data.Id] = leaderPage;
                Log.Write("Native enRAGE leader page assigned: " + displayName + " [" + playerId + "] -> " + data.Id + ".");
            }
            catch (Exception ex)
            {
                Log.Write("Native player leader page assignment failed: " + Unwrap(ex));
            }

            return displayName;
        }

        internal bool AddPlayerCampSite(PlayerFactionData data, Vector3 position, string how, out string siteId, out string region, out string error)
        {
            siteId = null;
            region = null;
            error = null;
            if (!IsAvailable || data == null || _addCampSiteMethod == null || _extraCampSitesField == null)
            {
                error = "La versión cargada de enRAGE no expone AddCampSite/Camps.Extra.";
                return false;
            }

            try
            {
                object gang = FindPlayerGang(data.Id);
                if (gang == null)
                {
                    error = "La facción todavía no está registrada en enRAGE.";
                    return false;
                }

                IList extra = _extraCampSitesField.GetValue(null) as IList;
                if (extra == null)
                {
                    error = "No se pudo acceder a la lista nativa Camps.Extra.";
                    return false;
                }

                HashSet<string> before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object site in extra)
                {
                    string id = GetField(site, "Id") as string;
                    if (!string.IsNullOrWhiteSpace(id)) before.Add(id);
                }

                object result = _addCampSiteMethod.Invoke(MainInstance, new object[] { gang, position, how ?? "founded by you" });
                if (!(result is bool) || !(bool)result)
                {
                    error = DiagnoseCampSiteRejection(gang, data.Id, position, extra);
                    Log.Write("Camp placement rejected by enRAGE at " + position.X + ", " + position.Y + ", " + position.Z + ": " + error);
                    return false;
                }

                object created = null;
                float best = float.MaxValue;
                foreach (object site in extra)
                {
                    if (!string.Equals(GetField(site, "Gang") as string, data.Id, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string id = GetField(site, "Id") as string;
                    float x = Convert.ToSingle(GetField(site, "X"));
                    float y = Convert.ToSingle(GetField(site, "Y"));
                    float z = Convert.ToSingle(GetField(site, "Z"));
                    float dx = x - position.X, dy = y - position.Y, dz = z - position.Z;
                    float distance = dx * dx + dy * dy + dz * dz;
                    if (!string.IsNullOrWhiteSpace(id) && !before.Contains(id) && distance < best)
                    {
                        created = site;
                        best = distance;
                    }
                }

                if (created == null)
                {
                    error = "enRAGE confirmó el campamento, pero no se pudo recuperar su identificador.";
                    return false;
                }

                // AddCampSite marks the first site as Hideout=true. That mode represents an
                // existing building and CampKit.Spawn deliberately skips it. A player-selected
                // campsite is an outdoor camp, so replace that first-site shell with Hideout=false.
                created = NormalizePlayerCampSite(extra, created, data);
                siteId = GetField(created, "Id") as string;
                region = GetField(created, "Region") as string;
                data.CampSiteId = siteId;
                data.CampRegion = region ?? string.Empty;
                data.CampX = Convert.ToSingle(GetField(created, "X"));
                data.CampY = Convert.ToSingle(GetField(created, "Y"));
                data.CampZ = Convert.ToSingle(GetField(created, "Z"));
                EstablishCampTerritoryFoothold(gang, position, data);
                Log.Write("Native camp site created: " + siteId + " in " + region + " for " + data.Id + ".");
                return true;
            }
            catch (Exception ex)
            {
                error = Unwrap(ex);
                Log.Write("AddPlayerCampSite failed: " + error);
                return false;
            }
        }

        private string DiagnoseCampSiteRejection(object gang, string gangId, Vector3 position, IList extra)
        {
            try
            {
                MethodInfo atFort = _mainType.GetMethods(StaticFlags).FirstOrDefault(m =>
                    m.Name == "AtTheFort" && m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(Vector3));
                if (atFort != null && Convert.ToBoolean(atFort.Invoke(null, new object[] { position })))
                    return "La ubicación está dentro del perímetro prohibido de Fort Wallace (enRAGE bloquea un radio de 450 m). Elegí un punto más alejado.";

                if (extra != null && extra.Cast<object>().Count(site =>
                    string.Equals(GetField(site, "Gang") as string, gangId, StringComparison.OrdinalIgnoreCase)) >= 6)
                    return "Esta facción ya tiene el máximo de seis campamentos nativos.";

                MethodInfo forGang = _campsType != null ? _campsType.GetMethods(StaticFlags).FirstOrDefault(m =>
                    m.Name == "ForGang" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string)) : null;
                IEnumerable gangSites = forGang != null ? forGang.Invoke(null, new object[] { gangId }) as IEnumerable : null;
                if (gangSites != null)
                {
                    foreach (object site in gangSites)
                    {
                        if (site == null) continue;
                        float x = Convert.ToSingle(GetField(site, "X"));
                        float y = Convert.ToSingle(GetField(site, "Y"));
                        float dx = x - position.X;
                        float dy = y - position.Y;
                        if ((dx * dx) + (dy * dy) < 400f * 400f)
                            return "Hay otro campamento de esta misma facción a menos de 400 m. Elegí otra ubicación.";
                    }
                }
            }
            catch (Exception ex) { Log.Write("Camp rejection diagnostics warning: " + Unwrap(ex)); }
            return "enRAGE rechazó el sitio por una condición nativa adicional. Probá otra ubicación y revisá extension.log para ver las coordenadas.";
        }

        private void EstablishCampTerritoryFoothold(object gang, Vector3 position, PlayerFactionData data)
        {
            if (data == null || data.TerritoryInitialized) return;
            try
            {
                MethodInfo districtAt = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m =>
                    m.Name == "DistrictAt" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Vector3));
                MethodInfo shiftControl = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m =>
                    m.Name == "ShiftControl" && m.GetParameters().Length == 4);
                if (districtAt == null || shiftControl == null)
                {
                    Log.Write("Territory foothold skipped: enRAGE DistrictAt/ShiftControl unavailable.");
                    return;
                }
                object district = districtAt.Invoke(MainInstance, new object[] { position });
                if (district == null)
                {
                    Log.Write("Territory foothold skipped: no enRAGE district at the selected camp coordinates.");
                    return;
                }
                // Give the player faction a modest 10-point foothold in the district containing
                // its camp. enRAGE's native ShiftControl handles the opposing shares and saves
                // the same Chronicle.Control map used by its territory screen and wars.
                shiftControl.Invoke(MainInstance, new object[] { district, gang, 10, "player faction camp founded" });
                data.TerritoryInitialized = true;
                try { data.Save(); } catch (Exception saveEx) { Log.Write("Territory initialization flag save warning: " + saveEx.Message); }
                MethodInfo saveWorld = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m => m.Name == "SaveWorld" && m.GetParameters().Length == 0);
                if (saveWorld != null) saveWorld.Invoke(MainInstance, null);
                string districtName = GetField(district, "Name") as string ?? "unknown district";
                Log.Write("Player faction received 10 native territory points in " + districtName + " (" + data.Id + ").");
            }
            catch (Exception ex) { Log.Write("Camp territory foothold failed: " + Unwrap(ex)); }
        }

        internal bool FindPlayerCampSite(PlayerFactionData data, out string error)
        {
            error = null;
            if (!IsAvailable || data == null || _campsType == null || _extraCampSitesField == null)
                return false;
            try
            {
                IList extra = _extraCampSitesField.GetValue(null) as IList;
                if (extra == null) return false;

                object existing = null;
                foreach (object site in extra)
                {
                    if (!string.Equals(GetField(site, "Gang") as string, data.Id, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrWhiteSpace(data.CampSiteId) &&
                        string.Equals(GetField(site, "Id") as string, data.CampSiteId, StringComparison.OrdinalIgnoreCase))
                    {
                        existing = site;
                        break;
                    }
                    if (existing == null) existing = site;
                }

                // enRAGE loads camps.json during its own startup and filters rows whose Gang
                // is not registered yet. The extension registers player_faction after that
                // load, so a perfectly valid saved player camp can be absent from Camps.Extra.
                // Rehydrate that row from PlayerFaction.json once the gang is registered.
                if (existing == null)
                    existing = RestorePlayerCampSiteFromExtensionData(extra, data);

                if (existing != null)
                {
                    existing = NormalizePlayerCampSite(extra, existing, data);
                    data.CampSiteId = GetField(existing, "Id") as string;
                    data.CampRegion = GetField(existing, "Region") as string ?? string.Empty;
                    data.CampX = Convert.ToSingle(GetField(existing, "X"));
                    data.CampY = Convert.ToSingle(GetField(existing, "Y"));
                    data.CampZ = Convert.ToSingle(GetField(existing, "Z"));
                    object playerGang = FindPlayerGang(data.Id);
                    if (playerGang != null)
                        EstablishCampTerritoryFoothold(playerGang, new Vector3(data.CampX, data.CampY, data.CampZ), data);
                    return true;
                }

                // No native or extension-saved camp exists yet. The leader can create the first
                // one from the roster sheet; never silently place it at an arbitrary fallback.
                return false;
            }
            catch (Exception ex)
            {
                error = Unwrap(ex);
                Log.Write("FindPlayerCampSite failed: " + error);
                return false;
            }
        }

        private object RestorePlayerCampSiteFromExtensionData(IList extra, PlayerFactionData data)
        {
            if (extra == null || data == null)
                return null;

            bool hasSavedCampId = !string.IsNullOrWhiteSpace(data.CampSiteId);
            bool hasSavedPosition = Math.Abs(data.CampX) + Math.Abs(data.CampY) + Math.Abs(data.CampZ) > 0.01f;
            if (!hasSavedCampId && !hasSavedPosition)
                return null;

            // Do not manufacture a camp from a stale ID without coordinates. A native CampSite
            // needs an actual world position to rebuild its physical CampKit.
            if (!hasSavedPosition)
            {
                Log.Write("Saved player camp id exists but its coordinates are missing; cannot rehydrate CampSite.");
                return null;
            }

            if (_campSiteType == null)
                return null;

            string id = hasSavedCampId ? data.CampSiteId : data.Id + "_restored_camp";
            string region = string.IsNullOrWhiteSpace(data.CampRegion) ? "the wilds" : data.CampRegion;
            string name = "Campamento de " + (string.IsNullOrWhiteSpace(data.Name) ? "la banda" : data.Name) + " - " + region;
            try
            {
                ConstructorInfo ctor = _campSiteType.GetConstructors(InstanceFlags)
                    .FirstOrDefault(c => c.GetParameters().Length == 9);
                if (ctor == null)
                {
                    Log.Write("Saved player camp could not be rehydrated: CampSite constructor with 9 parameters is unavailable.");
                    return null;
                }

                object restored = ctor.Invoke(new object[]
                {
                    id, 0U, data.CampX, data.CampY, data.CampZ, region, false, name, data.Id
                });
                extra.Add(restored);
                data.CampSiteId = id;
                data.CampRegion = region;

                if (_saveCampSitesMethod != null && MainInstance != null)
                    _saveCampSitesMethod.Invoke(MainInstance, null);
                try { data.Save(); }
                catch (Exception saveEx) { Log.Write("Rehydrated camp data save warning: " + saveEx.Message); }

                Log.Write("Rehydrated player CampSite " + id + " from PlayerFaction.json at " +
                          data.CampX + ", " + data.CampY + ", " + data.CampZ +
                          ". This repairs enRAGE's startup filter for an unregistered player_faction.");
                return restored;
            }
            catch (Exception ex)
            {
                Log.Write("RestorePlayerCampSiteFromExtensionData failed: " + Unwrap(ex));
                return null;
            }
        }

        private object NormalizePlayerCampSite(IList extra, object site, PlayerFactionData data)
        {
            if (extra == null || site == null) return site;
            bool isHideout = false;
            try { isHideout = Convert.ToBoolean(GetField(site, "Hideout")); } catch { }
            if (!isHideout) return site;

            string id = GetField(site, "Id") as string;
            string region = GetField(site, "Region") as string ?? string.Empty;
            string gangId = GetField(site, "Gang") as string ?? data.Id;
            uint imap = Convert.ToUInt32(GetField(site, "Imap"));
            float x = Convert.ToSingle(GetField(site, "X"));
            float y = Convert.ToSingle(GetField(site, "Y"));
            float z = Convert.ToSingle(GetField(site, "Z"));
            string campName = "Campamento de " + (data.Name ?? "la banda") + " - " + region;

            if (_campSiteType == null)
                _campSiteType = site.GetType();
            ConstructorInfo ctor = _campSiteType.GetConstructors(InstanceFlags).FirstOrDefault(c => c.GetParameters().Length == 9);
            if (ctor == null)
            {
                Log.Write("CampSite constructor with 9 parameters unavailable; trying writable Hideout field.");
                FieldInfo hideoutField = site.GetType().GetField("Hideout", InstanceFlags);
                if (hideoutField != null && !hideoutField.IsInitOnly)
                    hideoutField.SetValue(site, false);
                if (_saveCampSitesMethod != null) _saveCampSitesMethod.Invoke(MainInstance, null);
                return site;
            }

            object replacement = ctor.Invoke(new object[] { id, imap, x, y, z, region, false, campName, gangId });
            int index = extra.IndexOf(site);
            if (index >= 0) extra[index] = replacement;
            if (_saveCampSitesMethod != null)
                _saveCampSitesMethod.Invoke(MainInstance, null);
            Log.Write("Converted first player CampSite to a pitched camp (Hideout=false): " + id + ".");
            return replacement;
        }

        internal Vector3 GetPlayerCampCentre(PlayerFactionData data)
        {
            Vector3 fallback = data == null
                ? Vector3.Zero
                : new Vector3(data.CampX, data.CampY, data.CampZ);
            if (!IsAvailable || data == null || string.IsNullOrWhiteSpace(data.CampSiteId))
                return fallback;

            try
            {
                object site = FindCampSite(data.CampSiteId);
                object kit = GetField(MainInstance, "Kit");
                if (site == null || kit == null) return fallback;
                MethodInfo centreMethod = kit.GetType().GetMethods(InstanceFlags)
                    .FirstOrDefault(m => m.Name == "Centre" && m.GetParameters().Length == 1 &&
                                         m.GetParameters()[0].ParameterType.IsInstanceOfType(site));
                if (centreMethod == null) return fallback;
                object value = centreMethod.Invoke(kit, new[] { site });
                if (value is Vector3)
                {
                    Vector3 centre = (Vector3)value;
                    if (centre != Vector3.Zero) return centre;
                }
            }
            catch (Exception ex)
            {
                Log.Write("CampKit centre lookup warning; saved CampSite coordinate will be used: " + Unwrap(ex));
            }
            return fallback;
        }

        /// <summary>
        /// Reads CampKit's own placement plan. Those points sit at the fire, bedrolls, seats,
        /// tents and other camp props, so ambient roster peds can try nearby world scenario nodes.
        /// If CampKit has not produced its layout yet, return a compact ring around the centre.
        /// </summary>
        internal List<Vector3> GetPlayerCampScenarioAnchors(PlayerFactionData data)
        {
            List<Vector3> anchors = new List<Vector3>();
            if (data == null) return anchors;
            Vector3 centre = GetPlayerCampCentre(data);
            try
            {
                object kit = GetField(MainInstance, "Kit");
                IDictionary spotsBySite = kit != null ? GetField(kit, "_spots") as IDictionary : null;
                IEnumerable spots = spotsBySite != null && spotsBySite.Contains(data.CampSiteId)
                    ? spotsBySite[data.CampSiteId] as IEnumerable : null;
                if (spots != null)
                {
                    foreach (object spot in spots)
                    {
                        object position = GetField(spot, "Item1"); // ValueTuple<Vector3,float>
                        if (!(position is Vector3)) continue;
                        Vector3 point = (Vector3)position;
                        if (point.DistanceTo(centre) > 15f) continue;
                        if (anchors.All(existing => existing.DistanceTo(point) >= 1.25f))
                            anchors.Add(point);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("CampKit scenario-anchor lookup warning: " + Unwrap(ex));
            }

            if (anchors.Count == 0)
            {
                anchors.Add(centre);
                for (int i = 0; i < 12; i++)
                {
                    double angle = i * Math.PI / 6.0;
                    float radius = (i % 2 == 0) ? 3.0f : 5.5f;
                    Vector3 point = centre + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0f);
                    if (anchors.All(existing => existing.DistanceTo(point) >= 1.25f)) anchors.Add(point);
                }
            }

            return anchors.OrderBy(point => point.DistanceTo(centre)).ToList();
        }

        /// <summary>
        /// enRAGE only draws physical camp props when its CampKit is asked to spawn them.
        /// Do this when the player is near the site, so terrain/collision is loaded.
        /// </summary>
        internal bool EnsurePlayerCampProps(PlayerFactionData data, Ped player, bool forceRefresh, out string error)
        {
            error = null;
            if (!IsAvailable || data == null || player == null || !player.Exists() || string.IsNullOrWhiteSpace(data.CampSiteId))
                return false;
            try
            {
                object site = FindCampSite(data.CampSiteId);
                if (site == null)
                {
                    error = "El CampSite guardado no existe en Camps.Extra.";
                    return false;
                }

                site = NormalizePlayerCampSite(_extraCampSitesField.GetValue(null) as IList, site, data);
                if (Convert.ToBoolean(GetField(site, "Hideout")))
                {
                    error = "enRAGE sigue clasificando la ubicación como Hideout; no puede montar un campamento exterior.";
                    return false;
                }

                float x = Convert.ToSingle(GetField(site, "X"));
                float y = Convert.ToSingle(GetField(site, "Y"));
                float z = Convert.ToSingle(GetField(site, "Z"));
                Vector3 position = new Vector3(x, y, z);
                if (player.Position.DistanceTo(position) > 140f)
                    return false; // Deferred until the player streams the location in.

                STREAMING.REQUEST_COLLISION_AT_COORD(x, y, z);
                float groundZ = 0f;
                if (!TryGetGroundZ(x, y, z + 3f, out groundZ))
                    return false; // Wait for collision to stream before CampKit caches an empty layout.

                object kit = GetField(MainInstance, "Kit"); // internal lazy property on NemesisMod
                if (kit == null)
                {
                    error = "No se pudo obtener NemesisMod.Kit.";
                    return false;
                }

                MethodInfo has = kit.GetType().GetMethods(InstanceFlags).FirstOrDefault(m =>
                    m.Name == "Has" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                bool kitAlreadyHasCamp = has != null && Convert.ToBoolean(has.Invoke(kit, new object[] { data.CampSiteId }));
                if (kitAlreadyHasCamp && !forceRefresh)
                    return true;

                // ScriptHook script reloads can invalidate the physical props while enRAGE's
                // CampKit still remembers the site key. On the first nearby visit in each
                // extension runtime, clear that stale cache entry and rebuild it once.
                if (forceRefresh)
                {
                    MethodInfo despawn = kit.GetType().GetMethods(InstanceFlags).FirstOrDefault(m =>
                        m.Name == "Despawn" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                    MethodInfo forgetCentre = kit.GetType().GetMethods(InstanceFlags).FirstOrDefault(m =>
                        m.Name == "ForgetCentre" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                    if (despawn != null)
                        despawn.Invoke(kit, new object[] { data.CampSiteId });
                    if (forgetCentre != null)
                        forgetCentre.Invoke(kit, new object[] { data.CampSiteId });
                    Log.Write("Refreshing cached CampKit entry before restoring player camp " + data.CampSiteId + ".");
                }

                MethodInfo spawn = kit.GetType().GetMethods(InstanceFlags).FirstOrDefault(m =>
                    m.Name == "Spawn" && m.GetParameters().Length == 3 &&
                    m.GetParameters()[0].ParameterType.IsInstanceOfType(site) &&
                    m.GetParameters()[1].ParameterType == typeof(string) && m.GetParameters()[2].ParameterType == typeof(bool));
                if (spawn == null)
                {
                    error = "NemesisSystem.CampKit.Spawn(CampSite,string,bool) no está disponible.";
                    return false;
                }

                spawn.Invoke(kit, new object[] { site, data.Id, false });
                bool spawned = has == null || Convert.ToBoolean(has.Invoke(kit, new object[] { data.CampSiteId }));
                if (!spawned)
                {
                    error = "CampKit no registró el montaje del campamento; se volverá a intentar.";
                    return false;
                }
                Log.Write("Native CampKit spawned physical camp props for " + data.CampSiteId + " near the player.");
                return true;
            }
            catch (Exception ex)
            {
                error = Unwrap(ex);
                Log.Write("EnsurePlayerCampProps failed: " + error);
                return false;
            }
        }


        /// <summary>
        /// Replaces the player's native CampKit look according to the currently displayed
        /// enRAGE notoriety value (PRESTIGIO). The physical kit is rebuilt only when the
        /// tier changes, or once after a ScriptHook runtime reload, and only when the site
        /// is close enough for collision to be available.
        /// </summary>
        internal bool SyncPlayerCampVisualTier(PlayerFactionData data, Ped player, out bool physicalKitRebuilt, out string error)
        {
            physicalKitRebuilt = false;
            error = null;
            if (!IsAvailable || data == null || player == null || !player.Exists() ||
                string.IsNullOrWhiteSpace(data.CampSiteId) || MainInstance == null)
                return false;

            try
            {
                Type campKitType = _campKitType ?? _mainType.Assembly.GetType("NemesisSystem.CampKit", false);
                FieldInfo looksField = campKitType != null ? campKitType.GetField("CampLooks", StaticFlags) : null;
                IDictionary looks = looksField != null ? looksField.GetValue(null) as IDictionary : null;
                if (campKitType == null || looks == null)
                {
                    error = "NemesisSystem.CampKit.CampLooks no está disponible.";
                    return false;
                }

                object gang = FindPlayerGang(data.Id);
                MethodInfo notoriety = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m =>
                    m.Name == "NotorietyOf" && m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.IsInstanceOfType(gang));
                if (gang == null || notoriety == null)
                {
                    error = "No se pudo calcular el prestigio nativo de player_faction.";
                    return false;
                }

                int prestige = Math.Max(0, Math.Min(100, Convert.ToInt32(notoriety.Invoke(MainInstance, new[] { gang }))));
                int tier = Math.Min(4, prestige / 20);
                bool newLookRequired = _lastDefinedCampVisualTier != tier || !looks.Contains(data.Id);
                if (newLookRequired)
                {
                    object look = CreatePlayerCampLook(campKitType, tier);
                    if (look == null)
                    {
                        error = "No se pudo construir la configuración visual del CampKit.";
                        return false;
                    }
                    looks[data.Id] = look;
                    _lastDefinedCampVisualTier = tier;
                    Log.Write("Installed CampKit visual tier " + tier + " for player_faction (prestige=" + prestige + ").");
                }

                Vector3 centre = GetPlayerCampCentre(data);
                if (centre == Vector3.Zero || player.Position.DistanceTo(centre) > 140f)
                    return true; // Save the look for the next nearby CampKit.Spawn; don't delete distant props.

                STREAMING.REQUEST_COLLISION_AT_COORD(centre.X, centre.Y, centre.Z);
                float groundZ;
                if (!TryGetGroundZ(centre.X, centre.Y, centre.Z + 3f, out groundZ))
                    return true; // Wait for streamed collision before changing the live kit.

                if (_lastAppliedCampVisualTier != tier)
                {
                    object kit = GetField(MainInstance, "Kit");
                    bool hasKit = false;
                    if (kit != null)
                    {
                        MethodInfo has = kit.GetType().GetMethods(InstanceFlags).FirstOrDefault(m =>
                            m.Name == "Has" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                        hasKit = has != null && Convert.ToBoolean(has.Invoke(kit, new object[] { data.CampSiteId }));
                        if (hasKit)
                        {
                            MethodInfo despawn = kit.GetType().GetMethods(InstanceFlags).FirstOrDefault(m =>
                                m.Name == "Despawn" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                            if (despawn != null)
                            {
                                despawn.Invoke(kit, new object[] { data.CampSiteId });
                                physicalKitRebuilt = true;
                                Log.Write("Despawned live CampKit to refresh player_faction prestige tier " + tier + ".");
                            }
                        }
                    }
                    _lastAppliedCampVisualTier = tier;
                    data.CampVisualTier = tier;
                    try { data.Save(); } catch (Exception saveEx) { Log.Write("Camp visual tier save warning: " + saveEx.Message); }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = Unwrap(ex);
                Log.Write("Camp prestige visual synchronization failed: " + error);
                return false;
            }
        }

        private object CreatePlayerCampLook(Type campKitType, int tier)
        {
            try
            {
                Type lookType = campKitType.GetNestedType("CampLook", BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo makePiece = campKitType.GetMethods(StaticFlags).FirstOrDefault(m => m.Name == "P" && m.GetParameters().Length == 7);
                if (lookType == null || makePiece == null) return null;

                Type zoneType = makePiece.GetParameters()[3].ParameterType;
                object MakePiece(string key, string[] models, int count, string zone, float size, bool faces)
                {
                    object zoneValue = Enum.Parse(zoneType, zone, true);
                    return makePiece.Invoke(null, new object[] { key, models, count, zoneValue, size, faces, 100 });
                }

                // Every prop model below is already part of enRAGE's own CampKit fallback lists.
                string[] tents = { "p_ambtentscrub01b", "p_ambtentgrass01b", "p_ambtentmulch01b", "p_ambtentdirt01x", "p_tentarmy01x", "s_tentcaravan01x", "p_tent_leento01x" };
                string[] beds = { "p_bedrollopen01x", "p_bedrollopen02x", "p_bedroll01x", "p_bedrollclosed01x" };
                string[] seats = { "p_stool01x", "p_woodenchair01x", "p_chair05x", "p_bench_log01x", "p_log02x" };
                string[] lanterns = { "p_lantern05x", "p_lantern09x", "p_lantern01x", "p_lantern02x" };
                string[] wood = { "p_woodpile01x", "p_woodpile02x", "p_firewood01x", "p_logpile01x" };
                string[] crates = { "p_cratestack01x", "p_crate03x", "p_crate14x" };
                string[] barrels = { "p_barrel02x", "p_barrel01x", "p_barrel_wood01x" };
                string[] chests = { "p_chest01x", "p_chest02x", "p_trunk01x" };
                string[] whiskey = { "p_bottlewhiskey01x", "p_whiskeybottle01x", "p_bottle008x", "p_bottle01x" };
                var pieces = new List<object>();
                string shape;
                if (tier == 0)
                {
                    shape = "Scatter";
                    pieces.Add(MakePiece("bedroll", beds, 1, "Inner", 0.9f, true));
                    pieces.Add(MakePiece("seat", seats, 1, "Inner", 0.5f, true));
                    pieces.Add(MakePiece("lantern", lanterns, 1, "Mid", 0.3f, false));
                    pieces.Add(MakePiece("wood", wood, 1, "Mid", 0.9f, false));
                }
                else if (tier == 1)
                {
                    shape = "Cluster";
                    pieces.Add(MakePiece("tent", tents, 1, "Outer", 2.5f, true));
                    pieces.Add(MakePiece("bedroll", beds, 2, "Inner", 0.9f, true));
                    pieces.Add(MakePiece("seat", seats, 2, "Inner", 0.5f, true));
                    pieces.Add(MakePiece("lantern", lanterns, 1, "Mid", 0.3f, false));
                    pieces.Add(MakePiece("wood", wood, 1, "Mid", 0.9f, false));
                }
                else if (tier == 2)
                {
                    shape = "Ring";
                    pieces.Add(MakePiece("tent", tents, 2, "Outer", 2.5f, true));
                    pieces.Add(MakePiece("bedroll", beds, 3, "Inner", 0.9f, true));
                    pieces.Add(MakePiece("seat", seats, 3, "Inner", 0.5f, true));
                    pieces.Add(MakePiece("lantern", lanterns, 2, "Mid", 0.3f, false));
                    pieces.Add(MakePiece("wood", wood, 1, "Mid", 0.9f, false));
                    pieces.Add(MakePiece("crates", crates, 1, "Outer", 1.0f, false));
                    pieces.Add(MakePiece("barrel", barrels, 1, "Mid", 0.5f, false));
                }
                else if (tier == 3)
                {
                    shape = "Row";
                    pieces.Add(MakePiece("tent", tents, 3, "Outer", 2.5f, true));
                    pieces.Add(MakePiece("bedroll", beds, 4, "Inner", 0.9f, true));
                    pieces.Add(MakePiece("seat", seats, 4, "Inner", 0.5f, true));
                    pieces.Add(MakePiece("lantern", lanterns, 3, "Mid", 0.3f, false));
                    pieces.Add(MakePiece("wood", wood, 1, "Mid", 0.9f, false));
                    pieces.Add(MakePiece("crate stack", crates, 2, "Outer", 1.2f, false));
                    pieces.Add(MakePiece("barrel", barrels, 3, "Line", 0.5f, false));
                    pieces.Add(MakePiece("chest", chests, 1, "Mid", 0.8f, false));
                }
                else
                {
                    shape = "Cluster";
                    pieces.Add(MakePiece("tent", tents, 4, "Outer", 2.5f, true));
                    pieces.Add(MakePiece("bedroll", beds, 5, "Inner", 0.9f, true));
                    pieces.Add(MakePiece("seat", seats, 5, "Inner", 0.5f, true));
                    pieces.Add(MakePiece("lantern", lanterns, 4, "Mid", 0.3f, false));
                    pieces.Add(MakePiece("wood", wood, 2, "Mid", 0.9f, false));
                    pieces.Add(MakePiece("crate stack", crates, 3, "Outer", 1.2f, false));
                    pieces.Add(MakePiece("barrel", barrels, 4, "Line", 0.5f, false));
                    pieces.Add(MakePiece("chest", chests, 2, "Mid", 0.8f, false));
                    pieces.Add(MakePiece("whiskey", whiskey, 2, "Inner", 0.3f, false));
                }

                object look = Activator.CreateInstance(lookType, true);
                SetField(look, "Fire", true);
                FieldInfo shapeField = lookType.GetField("Shape", InstanceFlags);
                if (shapeField != null) shapeField.SetValue(look, Enum.Parse(shapeField.FieldType, shape, true));
                FieldInfo piecesField = lookType.GetField("Pieces", InstanceFlags);
                if (piecesField != null)
                {
                    Array pieceArray = Array.CreateInstance(makePiece.ReturnType, pieces.Count);
                    for (int i = 0; i < pieces.Count; i++) pieceArray.SetValue(pieces[i], i);
                    piecesField.SetValue(look, pieceArray);
                }
                return look;
            }
            catch (Exception ex)
            {
                Log.Write("Player CampKit look creation warning: " + Unwrap(ex));
                return null;
            }
        }

        internal void MarkPedAsEnRageOwned(Ped ped)
        {
            if (ped == null || !ped.Exists() || _makeOursMethod == null) return;
            try { _makeOursMethod.Invoke(null, new object[] { ped.Handle }); }
            catch (Exception ex) { Log.Write("Could not mark player-faction ped as enRAGE-owned: " + Unwrap(ex)); }
        }

        /// <summary>
        /// Marks only the exact existing player_faction peds known through the Companion cache or
        /// persisted roster-handle map as enRAGE-owned before LearnCampSites scans ambient gangs.
        /// This prevents a recruited NPC's model from being misidentified as an enemy-gang member.
        /// </summary>
        internal void MarkKnownPlayerFactionPedsAsEnRageOwned(PlayerFactionData data)
        {
            if (data == null) return;
            try
            {
                foreach (int handle in GetKnownPlayerFactionPedHandles(data))
                {
                    if (handle == 0) continue;
                    Ped ped = null;
                    try { ped = new Ped(handle); } catch { }
                    if (ped != null && ped.Exists() && !ped.IsDead)
                        MarkPedAsEnRageOwned(ped);
                }
            }
            catch (Exception ex) { Log.Write("Player-faction pre-scan ownership pass warning: " + Unwrap(ex)); }
        }

        /// <summary>
        /// Prevents enRAGE from assigning the player's own members to enemy camps or treating its
        /// own site as a hostile camp. It preserves legitimate player_faction relations/wars with
        /// other gangs so the native Feuds/TurfWars options can still function.
        /// </summary>
        internal bool MaintainPlayerFactionNeutrality(PlayerFactionData data)
        {
            if (!IsAvailable || data == null || !data.Founded || MainInstance == null) return false;
            int now = Game.GameTime;
            if (now >= 0 && now < _nextPlayerFactionNeutralityAt) return false;
            _nextPlayerFactionNeutralityAt = now + 5000;
            bool ownCampLiveKitWasRemoved = false;
            bool worldChanged = false;
            bool campsChanged = false;
            try
            {
                MarkKnownPlayerFactionPedsAsEnRageOwned(data);
                // Earlier versions forced every relationship involving player_faction to +100 and
                // deleted all wars. That prevented this faction from being its own enemy, but also
                // disabled the administrator's legitimate Feuds/TurfWars options against OTHER gangs.
                // Undo only those legacy +100 overrides; preserve any deliberate non-100 relation.
                Array gangs = _definedField != null ? _definedField.GetValue(null) as Array : null;
                if (gangs != null)
                {
                    foreach (object gang in gangs)
                    {
                        if (gang == null) continue;
                        string gangId = GetField(gang, "Id") as string;
                        if (string.IsNullOrWhiteSpace(gangId)) continue;
                        IDictionary relations = GetField(gang, "Relations") as IDictionary;
                        if (relations == null) continue;

                        List<object> removeKeys = new List<object>();
                        foreach (object keyObject in relations.Keys.Cast<object>().ToArray())
                        {
                            string relationId = keyObject as string;
                            if (string.IsNullOrWhiteSpace(relationId)) continue;
                            bool legacyPlayerPair = string.Equals(gangId, data.Id, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(relationId, data.Id, StringComparison.OrdinalIgnoreCase);
                            if (!legacyPlayerPair) continue;
                            object relation = relations[keyObject];
                            int relationValue;
                            if (relation != null && int.TryParse(Convert.ToString(relation), out relationValue) && relationValue == 100)
                                removeKeys.Add(keyObject);
                        }
                        foreach (object key in removeKeys)
                        {
                            relations.Remove(key);
                            worldChanged = true;
                        }
                    }
                }

                object chronicle = GetField(MainInstance, "Chronicle");
                if (chronicle != null)
                {
                    // Only remove impossible self-pairs. Relation shifts and wars with other
                    // factions remain native, so the player gang can still feud/fight when enabled.
                    worldChanged |= RemovePlayerFactionSelfPairEntries(GetField(chronicle, "Relations") as IDictionary, data.Id);
                    worldChanged |= RemovePlayerFactionSelfPairEntries(GetField(chronicle, "RelationWhy") as IDictionary, data.Id);
                    IList wars = GetField(chronicle, "Wars") as IList;
                    if (wars != null)
                    {
                        for (int i = wars.Count - 1; i >= 0; i--)
                        {
                            object war = wars[i];
                            string a = GetField(war, "A") as string;
                            string b = GetField(war, "B") as string;
                            if (string.Equals(a, data.Id, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(b, data.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                wars.RemoveAt(i);
                                worldChanged = true;
                                Log.Write("Removed impossible player_faction-versus-itself war record.");
                            }
                        }
                    }
                    IList warZones = GetField(chronicle, "WarZones") as IList;
                    if (warZones != null)
                    {
                        for (int i = warZones.Count - 1; i >= 0; i--)
                        {
                            string item = warZones[i] as string;
                            if (string.IsNullOrWhiteSpace(item) || !item.Contains("|")) continue;
                            int colon = item.IndexOf(':');
                            if (colon < 0 || colon >= item.Length - 1) continue;
                            string[] pair = item.Substring(colon + 1).Split('|');
                            if (pair.Length == 2 &&
                                string.Equals(pair[0], data.Id, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(pair[1], data.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                warZones.RemoveAt(i);
                                worldChanged = true;
                            }
                        }
                    }
                }

                // Clean the bogus enemy camp candidate/sites left by earlier scans. A real enRAGE
                // camp can't be within 400 m of another campsite, so only remove wrongly learned
                // objects/candidates close to the actual player camp (never the persisted site ID).
                Vector3 playerCamp = !string.IsNullOrWhiteSpace(data.CampSiteId)
                    ? new Vector3(data.CampX, data.CampY, data.CampZ)
                    : Vector3.Zero;
                IList candidates = GetField(MainInstance, "_campCandidates") as IList;
                if (candidates != null && playerCamp != Vector3.Zero)
                {
                    for (int i = candidates.Count - 1; i >= 0; i--)
                    {
                        object candidate = candidates[i];
                        string owner = GetField(candidate, "Gang") as string;
                        float x = Convert.ToSingle(GetField(candidate, "X") ?? 0f);
                        float y = Convert.ToSingle(GetField(candidate, "Y") ?? 0f);
                        bool nearOwnCamp = new Vector3(x, y, 0f).DistanceTo2D(playerCamp) < 100f;
                        if (string.Equals(owner, data.Id, StringComparison.OrdinalIgnoreCase) || nearOwnCamp)
                        {
                            candidates.RemoveAt(i);
                            campsChanged = true;
                        }
                    }
                }
                if (_extraCampSitesField != null && playerCamp != Vector3.Zero)
                {
                    IList extra = _extraCampSitesField.GetValue(null) as IList;
                    if (extra != null)
                    {
                        for (int i = extra.Count - 1; i >= 0; i--)
                        {
                            object site = extra[i];
                            string siteId = GetField(site, "Id") as string;
                            if (string.Equals(siteId, data.CampSiteId, StringComparison.OrdinalIgnoreCase)) continue;
                            string owner = GetField(site, "Gang") as string;
                            float x = Convert.ToSingle(GetField(site, "X") ?? 0f);
                            float y = Convert.ToSingle(GetField(site, "Y") ?? 0f);
                            bool incorrectlyOwnedByPlayerGang = string.Equals(owner, data.Id, StringComparison.OrdinalIgnoreCase);
                            bool tooCloseToOwnCamp = new Vector3(x, y, 0f).DistanceTo2D(playerCamp) < 85f;
                            if (incorrectlyOwnedByPlayerGang || tooCloseToOwnCamp)
                            {
                                Log.Write("Removed stale/false secondary CampSite (owner=" + (owner ?? "none") + ") around/assigned to the player's own camp: " + siteId + ".");
                                extra.RemoveAt(i);
                                campsChanged = true;
                            }
                        }
                    }
                }

                // Older releases may already have cached our residents or the player's own site as
                // a native hostile LiveCamp. Use enRAGE's own DespawnCamp path to dispose of the
                // erroneous group and its blips/props safely, then let the extension rebuild CampKit.
                IDictionary liveCamps = GetField(MainInstance, "_liveCamps") as IDictionary;
                if (liveCamps != null)
                {
                    MethodInfo despawn = MainInstance.GetType().GetMethods(InstanceFlags).FirstOrDefault(m =>
                        m.Name == "DespawnCamp" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(bool));
                    if (despawn != null)
                    {
                        foreach (DictionaryEntry entry in liveCamps.Cast<DictionaryEntry>().ToArray())
                        {
                            object live = entry.Value;
                            object site = GetField(live, "Site");
                            object boss = GetField(live, "Boss");
                            string siteId = GetField(site, "Id") as string;
                            string owner = GetField(site, "Gang") as string;
                            string bossGang = GetField(boss, "Gang") as string;
                            float siteX = Convert.ToSingle(GetField(site, "X") ?? 0f);
                            float siteY = Convert.ToSingle(GetField(site, "Y") ?? 0f);
                            bool nearOwnCamp = playerCamp != Vector3.Zero &&
                                new Vector3(siteX, siteY, 0f).DistanceTo2D(playerCamp) < 150f;
                            bool ours = string.Equals(siteId, data.CampSiteId, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(owner, data.Id, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(bossGang, data.Id, StringComparison.OrdinalIgnoreCase) ||
                                nearOwnCamp;
                            if (!ours) continue;
                            try
                            {
                                despawn.Invoke(MainInstance, new[] { live, (object)true });
                                ownCampLiveKitWasRemoved = true;
                                Log.Write("Despawned a stale LiveCamp assigned to player_faction/its own camp area: " + (siteId ?? "unknown") +
                                    (nearOwnCamp ? " (near player's own camp)" : "") + ".");
                            }
                            catch (Exception ex) { Log.Write("Could not despawn obsolete player-faction LiveCamp: " + Unwrap(ex)); }
                        }
                    }
                }

                if (campsChanged)
                {
                    MethodInfo saveCamps = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m => m.Name == "SaveCampSites" && m.GetParameters().Length == 0);
                    if (saveCamps != null) saveCamps.Invoke(MainInstance, null);
                }
                if (worldChanged)
                {
                    MethodInfo saveWorld = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m => m.Name == "SaveWorld" && m.GetParameters().Length == 0);
                    if (saveWorld != null) saveWorld.Invoke(MainInstance, null);
                }
                if (worldChanged || campsChanged)
                    Log.Write("Maintained player_faction camp safety: self-relations, stale camp candidates/sites and invalid live camps checked (world=" + worldChanged + ", camps=" + campsChanged + ").");
            }
            catch (Exception ex) { Log.Write("Player-faction neutrality maintenance warning: " + Unwrap(ex)); }
            return ownCampLiveKitWasRemoved;
        }

        private static bool RemovePlayerFactionSelfPairEntries(IDictionary map, string factionId)
        {
            if (map == null || string.IsNullOrWhiteSpace(factionId)) return false;
            bool changed = false;
            foreach (object keyObject in map.Keys.Cast<object>().ToArray())
            {
                string key = keyObject as string;
                if (string.IsNullOrWhiteSpace(key)) continue;
                string[] pair = key.Split('|');
                if (pair.Length == 2 &&
                    string.Equals(pair[0], factionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(pair[1], factionId, StringComparison.OrdinalIgnoreCase))
                {
                    map.Remove(keyObject);
                    changed = true;
                }
            }
            return changed;
        }

        internal bool IsPlayerCampSiteId(string siteId)
        {
            return !string.IsNullOrWhiteSpace(siteId) && _factionSiteIdMatches(siteId);
        }

        private bool _factionSiteIdMatches(string siteId)
        {
            return FounderPatches.Runtime != null && FounderPatches.Runtime.IsPlayerCampSiteId(siteId);
        }

        private unsafe bool TryGetGroundZ(float x, float y, float z, out float groundZ)
        {
            float sampledGround = 0f;
            try
            {
                bool found = MISC.GET_GROUND_Z_FOR_3D_COORD(x, y, z, &sampledGround, false);
                groundZ = sampledGround;
                return found;
            }
            catch (Exception ex)
            {
                groundZ = 0f;
                Log.Write("Camp ground probe failed: " + ex.Message);
                return false;
            }
        }

        private object FindCampSite(string id)
        {
            if (_extraCampSitesField == null || string.IsNullOrWhiteSpace(id)) return null;
            IList extra = _extraCampSitesField.GetValue(null) as IList;
            if (extra == null) return null;
            foreach (object site in extra)
                if (string.Equals(GetField(site, "Id") as string, id, StringComparison.OrdinalIgnoreCase)) return site;
            return null;
        }

        internal bool AwardMissionResources(PlayerFactionData data, int suppliesReward, int treasuryReward, out string error)
        {
            error = null;
            if (!IsAvailable || data == null || !data.Founded || MainInstance == null)
            {
                error = "enRAGE o la facción del jugador todavía no están disponibles.";
                return false;
            }

            try
            {
                IDictionary supplies = GetField(MainInstance, "SupplyMap") as IDictionary;
                IDictionary treasury = GetField(MainInstance, "Treasury") as IDictionary;
                if (supplies == null || treasury == null)
                {
                    error = "No se pudieron resolver los mapas nativos de suministros y tesorería.";
                    return false;
                }

                int currentSupplies = supplies.Contains(data.Id) ? Convert.ToInt32(supplies[data.Id]) : 0;
                int currentTreasury = treasury.Contains(data.Id) ? Convert.ToInt32(treasury[data.Id]) : 0;
                int newSupplies = Math.Max(0, currentSupplies + Math.Max(0, suppliesReward));
                int newTreasury = Math.Max(0, currentTreasury + Math.Max(0, treasuryReward));
                supplies[data.Id] = newSupplies;
                treasury[data.Id] = newTreasury;

                try
                {
                    MethodInfo saveWorld = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m =>
                        m.Name == "SaveWorld" && m.GetParameters().Length == 0);
                    if (saveWorld != null) saveWorld.Invoke(MainInstance, null);
                }
                catch (Exception ex) { Log.Write("SaveWorld after mission reward warning: " + Unwrap(ex)); }

                data.CurrentSupplies = newSupplies;
                data.CurrentTreasury = newTreasury;
                data.ResourcesInitialized = true;
                try { data.Save(); }
                catch (Exception ex) { Log.Write("Mission reward mirror save warning: " + ex.Message); }
                Log.Write("Mission reward applied to native faction resources: supplies +" + Math.Max(0, suppliesReward) +
                    ", treasury +$" + Math.Max(0, treasuryReward) + ".");
                return true;
            }
            catch (Exception ex)
            {
                error = Unwrap(ex);
                Log.Write("Mission reward failed: " + error);
                return false;
            }
        }

        internal bool SyncFactionResources(PlayerFactionData data, out string error)
        {
            error = null;
            if (!IsAvailable || data == null || !data.Founded || MainInstance == null)
                return false;
            try
            {
                object gang = FindPlayerGang(data.Id);
                if (gang == null) return false;
                object main = MainInstance;

                // Chronicle is the only source of faction resources. Seed a newly founded band at zero.
                IDictionary supplies = GetField(main, "SupplyMap") as IDictionary;
                IDictionary treasury = GetField(main, "Treasury") as IDictionary;
                IDictionary men = GetField(main, "MenMap") as IDictionary;
                if (supplies == null || treasury == null || men == null)
                {
                    error = "No se pudieron resolver los mapas nativos de suministros, tesorería y hombres.";
                    return false;
                }

                bool initializingResources = !data.ResourcesInitialized;
                // v0.3.4 did not baseline pre-existing roster IDs and could count them as starting manpower.
                // This independent marker also repairs an already-founded old save once, without resetting
                // its existing supplies or treasury.
                bool needsManpowerBaseline = initializingResources || !data.ManpowerBaselineInitialized;
                bool worldChanged = false;
                bool dataChanged = false;
                if (initializingResources)
                {
                    // MenOf(gang) otherwise inserts enRAGE's default starting manpower (normally 60)
                    // when this dictionary key is first read. Write all three keys before any reads.
                    men[data.Id] = 0;
                    supplies[data.Id] = 0;
                    treasury[data.Id] = 0;
                    data.RosterContributions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (data.UncountedRosterIds == null)
                        data.UncountedRosterIds = new List<string>();
                    data.CurrentMen = 0;
                    data.CurrentSupplies = 0;
                    data.CurrentTreasury = 0;
                    data.ResourcesInitialized = true;
                    worldChanged = true;
                    dataChanged = true;
                    Log.Write("Initialized player faction resources at zero (men=0, supplies=0, treasury=$0).");
                }
                else
                {
                    if (!men.Contains(data.Id)) { men[data.Id] = 0; worldChanged = true; }
                    if (!supplies.Contains(data.Id)) { supplies[data.Id] = 0; worldChanged = true; }
                    if (!treasury.Contains(data.Id)) { treasury[data.Id] = 0; worldChanged = true; }
                }

                if (needsManpowerBaseline)
                {
                    men[data.Id] = 0;
                    data.RosterContributions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    data.UncountedRosterIds = new List<string>();
                    data.CurrentMen = 0;
                    data.ManpowerBaselineInitialized = false; // Becomes true only after the roster can be inspected.
                    worldChanged = true;
                    dataChanged = true;
                    Log.Write("Reset player_faction manpower to zero and started a one-time roster baseline; existing roster identities will not count as newly recruited men.");
                }

                if (data.RosterContributions == null)
                    data.RosterContributions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (data.UncountedRosterIds == null)
                    data.UncountedRosterIds = new List<string>();

                HashSet<string> uncountedRosterIds = new HashSet<string>(data.UncountedRosterIds, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, int> current = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, object> allRecords = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                IEnumerable roster = GetField(main, "Roster") as IEnumerable;
                if (roster != null)
                {
                    foreach (object record in roster)
                    {
                        if (record == null) continue;
                        string recordId = GetField(record, "Id") as string;
                        if (string.IsNullOrWhiteSpace(recordId)) continue;
                        allRecords[recordId] = record;

                        if (!string.Equals(GetField(record, "Gang") as string, data.Id, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!Convert.ToBoolean(GetField(record, "IsAlive")) || Convert.ToBoolean(GetField(record, "PresumedDead")))
                            continue;
                        int rank = Convert.ToInt32(GetField(record, "Rank"));
                        current[recordId] = NativeNamedWorth(rank);
                    }

                    // Extension records left in the old StoryRoster are also part of the pre-founding
                    // baseline even before NormalizeCompanionPlayerFactionRoster moves them.
                    IEnumerable storyRecords = GetField(main, "StoryRoster") as IEnumerable;
                    if (storyRecords != null)
                    {
                        foreach (object record in storyRecords)
                        {
                            if (record == null) continue;
                            string recordId = GetField(record, "Id") as string;
                            if (!string.IsNullOrWhiteSpace(recordId) && !allRecords.ContainsKey(recordId))
                                allRecords[recordId] = record;
                        }
                    }

                    // A new band must be empty even if the saved roster contains recruits from a previous
                    // run/configuration. Baseline those old records without adding manpower. Only roster IDs
                    // first seen after founding contribute to the force as new recruits.
                    if (needsManpowerBaseline)
                    {
                        // Baseline every already-saved member ID before Companion has a chance to
                        // normalize legacy extcomp_/extvdl_ records into player_faction. Otherwise
                        // the next sync mistakes those pre-existing members for new recruits and
                        // a freshly founded band can start with dozens of men.
                        foreach (string recordId in current.Keys)
                            uncountedRosterIds.Add(recordId);
                        foreach (KeyValuePair<string, object> recordEntry in allRecords)
                        {
                            string recordId = recordEntry.Key;
                            if (IsCompanionRosterRecordId(recordId))
                                uncountedRosterIds.Add(recordId);
                        }
                        AddCachedCompanionRosterIds(uncountedRosterIds);
                    }

                    int delta = 0;
                    Dictionary<string, int> updatedContributions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, int> pair in current)
                    {
                        if (uncountedRosterIds.Contains(pair.Key))
                        {
                            // Existing-at-founding roster entries are not part of the new band's starting force.
                            // Keep their current native worth in the ledger but do not add/subtract it.
                            updatedContributions[pair.Key] = pair.Value;
                            continue;
                        }

                        int previousWorth;
                        if (data.RosterContributions.TryGetValue(pair.Key, out previousWorth))
                            delta += pair.Value - previousWorth; // Reflect a promotion or demotion.
                        else
                            delta += pair.Value; // A roster record recruited after this band was founded.
                        updatedContributions[pair.Key] = pair.Value;
                    }

                    foreach (KeyValuePair<string, int> previous in data.RosterContributions)
                    {
                        if (current.ContainsKey(previous.Key)) continue;
                        if (uncountedRosterIds.Remove(previous.Key))
                            continue; // Baseline entries were never counted; removing one must not charge manpower.

                        object oldRecord;
                        bool nativeDeath = allRecords.TryGetValue(previous.Key, out oldRecord) &&
                            (!Convert.ToBoolean(GetField(oldRecord, "IsAlive")) || Convert.ToBoolean(GetField(oldRecord, "PresumedDead")));
                        if (!nativeDeath)
                            delta -= previous.Value; // Alive member was expelled/transferred or removed externally.
                    }

                    int oldMen = Convert.ToInt32(men[data.Id]);
                    if (delta != 0)
                    {
                        men[data.Id] = Math.Max(0, oldMen + delta);
                        worldChanged = true;
                        Log.Write("Faction manpower synchronized: " + oldMen + " -> " + men[data.Id] + " (recruitment/roster delta " + delta + ").");
                    }

                    if (!DictionaryEquals(data.RosterContributions, updatedContributions))
                    {
                        data.RosterContributions = updatedContributions;
                        dataChanged = true;
                    }

                    List<string> normalizedUncounted = uncountedRosterIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    List<string> previousUncounted = (data.UncountedRosterIds ?? new List<string>()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    if (normalizedUncounted.Count != previousUncounted.Count ||
                        !normalizedUncounted.SequenceEqual(previousUncounted, StringComparer.OrdinalIgnoreCase))
                    {
                        data.UncountedRosterIds = normalizedUncounted;
                        dataChanged = true;
                    }
                    else
                    {
                        data.UncountedRosterIds = normalizedUncounted;
                    }
                    if (needsManpowerBaseline && !data.ManpowerBaselineInitialized)
                    {
                        data.ManpowerBaselineInitialized = true;
                        dataChanged = true;
                        Log.Write("Player faction manpower baseline complete; future new roster IDs will contribute manpower.");
                    }
                }
                else
                {
                    Log.Write("Roster is not available yet; preserving previous manpower contribution ledger.");
                    // Do not finalize zero-initialization until the real native roster can be inspected.
                    if (initializingResources)
                    {
                        data.ResourcesInitialized = false;
                        dataChanged = true;
                    }
                }

                int liveMen = Convert.ToInt32(men[data.Id]);
                int liveSupplies = Convert.ToInt32(supplies[data.Id]);
                int liveTreasury = Convert.ToInt32(treasury[data.Id]);
                if (data.CurrentMen != liveMen) { data.CurrentMen = liveMen; dataChanged = true; }
                if (data.CurrentSupplies != liveSupplies) { data.CurrentSupplies = liveSupplies; dataChanged = true; }
                if (data.CurrentTreasury != liveTreasury) { data.CurrentTreasury = liveTreasury; dataChanged = true; }

                if (worldChanged)
                {
                    MethodInfo saveWorld = _mainType.GetMethod("SaveWorld", InstanceFlags);
                    if (saveWorld != null) saveWorld.Invoke(main, null);
                }
                if (dataChanged) data.Save();
                return worldChanged || dataChanged;
            }
            catch (Exception ex)
            {
                error = Unwrap(ex);
                Log.Write("Faction resource synchronization failed: " + error);
                return false;
            }
        }

        private static bool IsCompanionRosterRecordId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) &&
                (id.StartsWith("extcomp_", StringComparison.OrdinalIgnoreCase) ||
                 id.StartsWith("extvdl_", StringComparison.OrdinalIgnoreCase));
        }

        private void AddCachedCompanionRosterIds(HashSet<string> baselineIds)
        {
            if (baselineIds == null) return;
            try
            {
                Type hooks = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => SafeGetType(a, "EnRAGECompanionsExtension.EnRAGEHooks"))
                    .FirstOrDefault(t => t != null);
                FieldInfo cacheField = hooks != null ? hooks.GetField("RosterRecordsByPedHandle", StaticFlags) : null;
                IDictionary cache = cacheField != null ? cacheField.GetValue(null) as IDictionary : null;
                if (cache == null) return;
                foreach (DictionaryEntry entry in cache)
                {
                    object cached = entry.Value;
                    object record = cached != null ? GetField(cached, "Record") : null;
                    string id = record != null ? GetField(record, "Id") as string : null;
                    if (IsCompanionRosterRecordId(id)) baselineIds.Add(id);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Could not baseline existing Companion roster IDs: " + Unwrap(ex));
            }
        }

        private int NativeNamedWorth(int rank)
        {
            MethodInfo method = _mainType.GetMethod("NamedWorth", StaticFlags, null, new[] { typeof(int) }, null);
            if (method == null) return rank > 0 ? Math.Min(10, 5 + (int)Math.Round((rank - 1) * 1.25)) : 1;
            return Convert.ToInt32(method.Invoke(null, new object[] { rank }));
        }

        private object InvokeGangMethod(object main, string name, object gang)
        {
            MethodInfo method = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m => m.Name == name &&
                m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == _gangType);
            return method != null ? method.Invoke(main, new[] { gang }) : null;
        }

        internal bool ChangePlayerFactionMemberRank(object record, int delta, out int oldRank, out int newRank, out string error)
        {
            oldRank = 0;
            newRank = 0;
            error = null;
            if (!IsAvailable || record == null || delta == 0)
            {
                error = "El registro de roster o la conexión con enRAGE no están disponibles.";
                return false;
            }

            try
            {
                string gangId = GetField(record, "Gang") as string;
                if (!string.Equals(gangId, "player_faction", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Este personaje no pertenece a player_faction.";
                    return false;
                }
                if (GetField(record, "IsAlive") is bool alive && !alive)
                {
                    error = "No se puede cambiar el rango de un miembro muerto.";
                    return false;
                }
                if (GetField(record, "PresumedDead") is bool presumedDead && presumedDead)
                {
                    error = "No se puede cambiar el rango de un miembro dado por muerto.";
                    return false;
                }
                oldRank = Convert.ToInt32(GetField(record, "Rank") ?? 1);
                newRank = Math.Max(1, Math.Min(5, oldRank + Math.Sign(delta)));
                if (newRank == oldRank)
                {
                    error = oldRank >= 5 ? "El miembro ya tiene el rango máximo." : "El miembro ya tiene el rango mínimo.";
                    return false;
                }

                object main = MainInstance;
                if (main == null)
                {
                    error = "La instancia principal de enRAGE todavía no está lista para guardar el roster.";
                    return false;
                }
                bool changedByNativePromotion = false;
                if (delta > 0 && main != null)
                {
                    MethodInfo promote = main.GetType().GetMethods(InstanceFlags)
                        .FirstOrDefault(m => m.Name == "Promote" && m.GetParameters().Length == 1 &&
                            m.GetParameters()[0].ParameterType.IsInstanceOfType(record));
                    if (promote != null)
                    {
                        promote.Invoke(main, new[] { record });
                        changedByNativePromotion = Convert.ToInt32(GetField(record, "Rank") ?? oldRank) == newRank;
                    }
                }

                if (!changedByNativePromotion)
                {
                    SetField(record, "Rank", newRank);
                    if (delta < 0)
                        SetField(record, "RankXp", 0);
                    MethodInfo saveRoster = main.GetType().GetMethods(InstanceFlags).FirstOrDefault(m => m.Name == "SaveRoster" && m.GetParameters().Length == 0);
                    if (saveRoster == null)
                    {
                        error = "No se encontró NemesisMod.SaveRoster().";
                        return false;
                    }
                    saveRoster.Invoke(main, null);
                }

                newRank = Convert.ToInt32(GetField(record, "Rank") ?? newRank);
                ApplyCompanionRankAffinity(record);
                MethodInfo persistRoster = main.GetType().GetMethods(InstanceFlags).FirstOrDefault(m => m.Name == "SaveRoster" && m.GetParameters().Length == 0);
                if (persistRoster != null) persistRoster.Invoke(main, null);
                if (newRank == oldRank)
                {
                    error = "enRAGE no confirmó el cambio de rango.";
                    return false;
                }
                Log.Write("Native roster rank changed for " + (GetField(record, "Name") as string ?? "member") +
                          ": " + oldRank + " -> " + newRank + ".");
                return true;
            }
            catch (Exception ex)
            {
                error = Unwrap(ex);
                Log.Write("Player faction roster rank update failed: " + error);
                return false;
            }
        }

        internal void TryInstallCompanionIntegrationPatches()
        {
            if (_harmony == null || (_companionFriendshipPatched && _companionRosterCommitPatched &&
                _companionNativeSocialPrefixPatched && _companionDismissedProfilePatched &&
                _companionReleasedProfilePatched && _companionCampManagerRecruitmentGuardPatched)) return;
            try
            {
                Type hooks = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => SafeGetType(a, "EnRAGECompanionsExtension.EnRAGEHooks"))
                    .FirstOrDefault(t => t != null);
                if (hooks == null) return;

                ForceCompanionRecruitmentIds(hooks);

                if (!_companionFriendshipPatched)
                {
                    MethodInfo friendship = hooks.GetMethods(StaticFlags).FirstOrDefault(m =>
                        m.Name == "ApplyMaximumRecruitFriendship" && m.IsStatic && m.GetParameters().Length == 1);
                    if (friendship != null)
                    {
                        PatchIfFound(friendship, nameof(FounderPatches.CompanionRankFriendshipPostfix), false);
                        _companionFriendshipPatched = true;
                        Log.Write("Companion rank-based relationship postfix installed.");
                    }
                }

                if (!_companionRosterCommitPatched)
                {
                    MethodInfo commit = hooks.GetMethods(StaticFlags).FirstOrDefault(m =>
                        m.Name == "CommitRosterRecord" && m.IsStatic && m.GetParameters().Length == 4);
                    if (commit != null)
                    {
                        PatchIfFound(commit, nameof(FounderPatches.CompanionEnsurePlayerFactionRosterPostfix), false);
                        _companionRosterCommitPatched = true;
                        Log.Write("Companion player_faction roster-normalization postfix installed.");
                    }
                }

                if (!_companionNativeSocialPrefixPatched)
                {
                    MethodInfo socialPrefix = hooks.GetMethods(StaticFlags).FirstOrDefault(m =>
                        m.Name == "UpdateSocialPrefix" && m.IsStatic &&
                        m.GetParameters().Length == 2 &&
                        m.GetParameters()[0].ParameterType == typeof(object) &&
                        m.GetParameters()[1].ParameterType == typeof(object[]));
                    if (socialPrefix != null)
                    {
                        PatchFirstIfFound(socialPrefix, nameof(FounderPatches.AllowNativeSocialForPlayerFactionPrefix));
                        _companionNativeSocialPrefixPatched = true;
                        Log.Write("Companion social-prefix bypass installed for player-faction roster members.");
                    }
                }

                if (!_companionCampManagerRecruitmentGuardPatched)
                {
                    MethodInfo genericEligibility = hooks.GetMethods(StaticFlags).FirstOrDefault(m =>
                        m.Name == "IsEligibleGenericCandidate" && m.IsStatic &&
                        m.GetParameters().Length == 4 &&
                        m.GetParameters()[0].ParameterType == typeof(object) &&
                        m.GetParameters()[1].ParameterType == typeof(Ped) &&
                        m.GetParameters()[2].ParameterType == typeof(Ped) &&
                        m.GetParameters()[3].ParameterType == typeof(bool));
                    if (genericEligibility != null)
                    {
                        PatchFirstIfFound(genericEligibility, nameof(FounderPatches.BlockCampManagerGenericRecruitmentPrefix));
                        _companionCampManagerRecruitmentGuardPatched = true;
                        Log.Write("Companion generic recruitment guard installed for the camp administrator.");
                    }
                }

                if (!_companionDismissedProfilePatched)
                {
                    MethodInfo dismissedProfile = hooks.GetMethods(StaticFlags).FirstOrDefault(m =>
                        m.Name == "ApplyDismissedCombatProfile" && m.IsStatic &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(Ped));
                    if (dismissedProfile != null)
                    {
                        PatchIfFound(dismissedProfile, nameof(FounderPatches.RestoreNativeCampMemberInteractionPostfix), false);
                        _companionDismissedProfilePatched = true;
                        Log.Write("Companion dismissed-profile native-interaction restore installed.");
                    }
                }

                if (!_companionReleasedProfilePatched)
                {
                    MethodInfo releasedProfile = hooks.GetMethods(StaticFlags).FirstOrDefault(m =>
                        m.Name == "ApplyAmbientReleasedCombatProfile" && m.IsStatic &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(Ped));
                    if (releasedProfile != null)
                    {
                        PatchIfFound(releasedProfile, nameof(FounderPatches.RestoreNativeCampMemberInteractionPostfix), false);
                        _companionReleasedProfilePatched = true;
                        Log.Write("Companion released-profile native-interaction restore installed.");
                    }
                }

            }
            catch (Exception ex) { Log.Write("Companion integration patch retry warning: " + Unwrap(ex)); }
        }

        private bool ApplyCompanionRankAffinity(object record)
        {
            if (record == null) return false;
            object oldHate = GetField(record, "Hate");
            object oldFear = GetField(record, "Fear");
            object oldDisposition = GetField(record, "Disposition");
            object oldLike = GetField(record, "Like");
            try
            {
                Type hooks = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => SafeGetType(a, "EnRAGECompanionsExtension.EnRAGEHooks"))
                    .FirstOrDefault(t => t != null);
                MethodInfo apply = hooks != null ? hooks.GetMethods(StaticFlags).FirstOrDefault(m =>
                    m.Name == "ApplyMaximumRecruitFriendship" && m.IsStatic && m.GetParameters().Length == 1) : null;
                if (apply != null) apply.Invoke(null, new[] { record });
                // Apply once more locally so rank-based disposition remains correct even if the
                // Companion method was not patched yet during a late assembly load.
                FounderPatches.CompanionRankFriendshipPostfix(new object[] { record });
            }
            catch (Exception ex) { Log.Write("Applying Companion relationship after rank change warning: " + Unwrap(ex)); }
            return !object.Equals(oldHate, GetField(record, "Hate")) ||
                   !object.Equals(oldFear, GetField(record, "Fear")) ||
                   !object.Equals(oldDisposition, GetField(record, "Disposition")) ||
                   !object.Equals(oldLike, GetField(record, "Like"));
        }

        internal void NormalizeCompanionPlayerFactionRoster()
        {
            int now = Game.GameTime;
            if (now >= 0 && now < _nextCompanionRosterNormalizeAt) return;
            _nextCompanionRosterNormalizeAt = now + 5000;
            try
            {
                Type hooks = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => SafeGetType(a, "EnRAGECompanionsExtension.EnRAGEHooks"))
                    .FirstOrDefault(t => t != null);
                if (hooks == null) return;

                string gangSetting = ReadStaticString(hooks, "_recruitGangId");
                string rosterSetting = ReadStaticString(hooks, "_recruitRosterId");
                if (!string.Equals(gangSetting, "player_faction", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(rosterSetting, "player_faction", StringComparison.OrdinalIgnoreCase))
                    return;

                FieldInfo cacheField = hooks.GetField("RosterRecordsByPedHandle", StaticFlags);
                object main = FounderPatches.MainInstance;
                IList roster = main != null ? GetField(main, "Roster") as IList : null;
                IList storyRoster = main != null ? GetField(main, "StoryRoster") as IList : null;
                IDictionary cache = cacheField != null ? cacheField.GetValue(null) as IDictionary : null;
                if (roster == null) return;

                // On a fresh game/script load Companion's handle cache may still be empty even
                // though the saved NemesisRecord is already in enRAGE.Roster. Reconcile those
                // persisted extcomp_/extvdl_ records first, then merge any live cached records.
                // The ID prefix is Companion's own stable record namespace; no model matching or
                // Ped creation is involved in this migration.
                List<object> candidates = roster.Cast<object>().Where(r => r != null).ToList();
                // Old Companion configurations could persist extension-created records in StoryRoster.
                // Recover those records by their stable extension IDs even when no live cache survives.
                if (storyRoster != null)
                {
                    foreach (object record in storyRoster)
                        if (record != null && !candidates.Contains(record)) candidates.Add(record);
                }
                if (cache != null)
                {
                    foreach (DictionaryEntry entry in cache)
                    {
                        object cached = entry.Value;
                        object record = cached != null ? GetField(cached, "Record") : null;
                        if (record != null && !candidates.Contains(record)) candidates.Add(record);
                        if (record != null && string.Equals(GetField(record, "Gang") as string, "player_faction", StringComparison.OrdinalIgnoreCase))
                        {
                            Ped ownedPed = GetField(cached, "Ped") as Ped;
                            if (ownedPed == null || !ownedPed.Exists())
                            {
                                try { ownedPed = new Ped(Convert.ToInt32(entry.Key)); } catch { }
                            }
                            MarkPedAsEnRageOwned(ownedPed);
                        }
                    }
                }

                bool changed = false;
                foreach (object record in candidates)
                {
                    string id = GetField(record, "Id") as string;
                    if (string.IsNullOrWhiteSpace(id) ||
                        !(id.StartsWith("extcomp_", StringComparison.OrdinalIgnoreCase) ||
                          id.StartsWith("extvdl_", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (GetField(record, "IsAlive") is bool alive && !alive) continue;
                    if (GetField(record, "PresumedDead") is bool presumedDead && presumedDead) continue;

                    string oldGang = GetField(record, "Gang") as string;
                    if (!string.Equals(oldGang, "player_faction", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(oldGang)) SetField(record, "FormerGang", oldGang);
                        SetField(record, "Gang", "player_faction");
                        changed = true;
                    }
                    if (GetField(record, "Story") is bool story && story)
                    {
                        SetField(record, "Story", false);
                        changed = true;
                    }
                    if (!roster.Contains(record))
                    {
                        roster.Add(record);
                        changed = true;
                    }
                    if (storyRoster != null && storyRoster.Contains(record))
                    {
                        storyRoster.Remove(record);
                        changed = true;
                    }
                    changed |= ApplyCompanionRankAffinity(record);
                }

                if (cache != null)
                {
                    foreach (DictionaryEntry entry in cache)
                    {
                        object cached = entry.Value;
                        object record = cached != null ? GetField(cached, "Record") : null;
                        if (!string.Equals(GetField(record, "Gang") as string, "player_faction", StringComparison.OrdinalIgnoreCase)) continue;
                        Ped ownedPed = GetField(cached, "Ped") as Ped;
                        if (ownedPed == null || !ownedPed.Exists())
                        {
                            try { ownedPed = new Ped(Convert.ToInt32(entry.Key)); } catch { }
                        }
                        MarkPedAsEnRageOwned(ownedPed);
                    }
                }

                if (changed)
                {
                    MethodInfo saveRoster = main.GetType().GetMethods(InstanceFlags).FirstOrDefault(m =>
                        m.Name == "SaveRoster" && m.GetParameters().Length == 0);
                    if (saveRoster != null) saveRoster.Invoke(main, null);
                    Log.Write("Companion-managed extcomp/extvdl records reconciled into player_faction roster.");
                }
            }
            catch (Exception ex) { Log.Write("Companion roster normalization warning: " + Unwrap(ex)); }
        }

        private static void ForceCompanionRecruitmentIds(Type hooks)
        {
            try
            {
                foreach (string fieldName in new[] { "_recruitGangId", "_recruitRosterId" })
                {
                    FieldInfo field = hooks.GetField(fieldName, StaticFlags);
                    if (field == null || field.IsInitOnly) continue;
                    string current = Convert.ToString(field.GetValue(null));
                    if (string.Equals(current, "player_faction", StringComparison.OrdinalIgnoreCase)) continue;
                    field.SetValue(null, "player_faction");
                    Log.Write("Pinned Companion " + fieldName + " to fixed ID player_faction (was '" + current + "').");
                }
            }
            catch (Exception ex) { Log.Write("Could not pin Companion gang/roster IDs: " + Unwrap(ex)); }
        }

        private static string ReadStaticString(Type type, string name)
        {
            FieldInfo field = type != null ? type.GetField(name, StaticFlags) : null;
            return field != null ? Convert.ToString(field.GetValue(null)) : null;
        }

        internal void UninstallHarmonyPatches()
        {
            try
            {
                if (_harmony != null)
                    _harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception ex) { Log.Write("Harmony unpatch during script reload warning: " + Unwrap(ex)); }
            _harmony = null;
            _patched = false;
            _companionFriendshipPatched = false;
            _companionRosterCommitPatched = false;
            _companionNativeSocialPrefixPatched = false;
            _companionDismissedProfilePatched = false;
            _companionReleasedProfilePatched = false;
            _nextCompanionRosterNormalizeAt = 0;
        }

        internal List<ReleasedRosterPed> GetReleasedPlayerFactionRosterPeds(
            PlayerFactionData data, Vector3 campCentre, int spawnBudget, out bool integrationAvailable)
        {
            integrationAvailable = false;
            List<ReleasedRosterPed> result = new List<ReleasedRosterPed>();
            if (data == null) return result;

            try
            {
                Type hooks = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => SafeGetType(a, "EnRAGECompanionsExtension.EnRAGEHooks"))
                    .FirstOrDefault(t => t != null);
                if (hooks == null) return result;

                FieldInfo cacheField = hooks.GetField("RosterRecordsByPedHandle", StaticFlags);
                FieldInfo mainField = hooks.GetField("_latestMainInstance", StaticFlags);
                // _latestMainInstance remains null until Companion runs its first cycle after a script
                // reload. Fall back to the already attached enRAGE singleton instead of treating that
                // temporary null as a missing integration.
                object main = mainField != null ? mainField.GetValue(null) : null;
                if (main == null) main = MainInstance;
                if (main == null) main = FounderPatches.MainInstance;
                IList roster = main != null ? GetField(main, "Roster") as IList : null;
                IList storyRoster = main != null ? GetField(main, "StoryRoster") as IList : null;
                IDictionary cache = cacheField != null ? cacheField.GetValue(null) as IDictionary : null;
                IList companions = main != null ? GetField(main, "_comps") as IList : null;
                if (cache == null || main == null || companions == null || roster == null)
                    return result; // Do not guess about following state if Companion's live lists are unavailable.

                integrationAvailable = true;
                if (data.CampRosterPedHandles == null)
                    data.CampRosterPedHandles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                int processId = Process.GetCurrentProcess().Id;
                bool dataChanged = false;
                if (data.CampRosterPedProcessId != processId)
                {
                    // Entity handles are valid through a ScriptHook script reload, but not a game restart.
                    data.CampRosterPedHandles.Clear();
                    data.CampManagerPedHandle = 0;
                    data.CampRosterPedProcessId = processId;
                    dataChanged = true;
                    Log.Write("Camp roster and administrator entity handles reset for a new game process; saved roster records will be materialized on demand.");
                }

                Dictionary<string, object> recordsById = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (IList source in new[] { roster, storyRoster })
                {
                    if (source == null) continue;
                    foreach (object record in source)
                    {
                        if (record == null) continue;
                        string id = GetField(record, "Id") as string;
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        if (string.Equals(GetField(record, "Gang") as string, data.Id, StringComparison.OrdinalIgnoreCase))
                            recordsById[id] = record;
                    }
                }

                HashSet<string> activeRosterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object companion in companions)
                {
                    if (companion == null) continue;
                    Ped activePed = GetField(companion, "Ped") as Ped;
                    Ped realPed = GetField(companion, "Real") as Ped;
                    object record = GetField(companion, "Rec");
                    string id = GetField(record, "Id") as string;
                    if (!string.IsNullOrWhiteSpace(id)) activeRosterIds.Add(id);
                }

                // Index Companion's exact cache by roster ID. Never match entities by model alone;
                // two roster records can share a model but must remain separate characters.
                Dictionary<string, ReleasedRosterPed> cachedById = new Dictionary<string, ReleasedRosterPed>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in cache)
                {
                    int handle;
                    try { handle = Convert.ToInt32(entry.Key); } catch { continue; }
                    if (handle == 0) continue;
                    object cached = entry.Value;
                    if (cached == null) continue;
                    object record = GetField(cached, "Record");
                    string id = GetField(record, "Id") as string;
                    if (record == null || string.IsNullOrWhiteSpace(id) || !recordsById.ContainsKey(id)) continue;
                    Ped ped = GetField(cached, "Ped") as Ped;
                    if (ped == null || !ped.Exists() || ped.Handle != handle) ped = new Ped(handle);
                    if (ped == null || !ped.Exists() || ped.Handle != handle) continue;

                    int preferredHandle;
                    bool savedHandleMatches = data.CampRosterPedHandles.TryGetValue(id, out preferredHandle) && preferredHandle == handle;
                    if (!cachedById.ContainsKey(id) || savedHandleMatches)
                        cachedById[id] = new ReleasedRosterPed { Handle = handle, Ped = ped, RosterId = id };

                    // Keep the real entity associated with its persisted record over script reloads,
                    // including while it is currently following the player.
                    if (!data.CampRosterPedHandles.TryGetValue(id, out preferredHandle) || preferredHandle != handle)
                    {
                        data.CampRosterPedHandles[id] = handle;
                        dataChanged = true;
                    }
                }

                HashSet<string> emittedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int remainingSpawns = Math.Max(0, spawnBudget);
                int rosterIndex = 0;
                foreach (KeyValuePair<string, object> entry in recordsById)
                {
                    string id = entry.Key;
                    object record = entry.Value;
                    rosterIndex++;
                    if (string.IsNullOrWhiteSpace(id) || record == null) continue;
                    if (GetField(record, "IsAlive") is bool alive && !alive) continue;
                    if (GetField(record, "PresumedDead") is bool presumedDead && presumedDead) continue;
                    if (GetField(record, "Story") is bool story && story) continue;

                    // A currently-following record stays with the player, not at camp.
                    if (activeRosterIds.Contains(id)) continue;

                    ReleasedRosterPed cachedPed;
                    if (cachedById.TryGetValue(id, out cachedPed) && cachedPed != null &&
                        cachedPed.Ped != null && cachedPed.Ped.Exists())
                    {
                        if (cachedPed.Ped.IsDead) continue;
                        if (emittedIds.Add(id)) result.Add(cachedPed);
                        continue;
                    }

                    uint modelHash = ToUInt32Safe(GetField(record, "ModelHash"));
                    int savedHandle;
                    if (data.CampRosterPedHandles.TryGetValue(id, out savedHandle) && savedHandle != 0)
                    {
                        Ped savedPed = null;
                        try { savedPed = new Ped(savedHandle); } catch { }
                        if (savedPed != null && savedPed.Exists())
                        {
                            uint liveHash = ToUInt32Safe(savedPed.Model.Hash);
                            if (savedPed.IsDead)
                            {
                                // Companion has not necessarily persisted the death flag yet. Do not replace a dead NPC.
                                continue;
                            }
                            if (modelHash != 0U && liveHash == modelHash)
                            {
                                RegisterCampRosterPedWithCompanion(hooks, record, savedPed, modelHash);
                                if (emittedIds.Add(id))
                                    result.Add(new ReleasedRosterPed { Handle = savedPed.Handle, Ped = savedPed, RosterId = id });
                                Log.Write("Reconnected camp roster member " + id + " to existing ped handle " + savedPed.Handle + ".");
                                continue;
                            }
                        }

                        data.CampRosterPedHandles.Remove(id);
                        dataChanged = true;
                    }

                    if (remainingSpawns <= 0 || modelHash == 0U)
                        continue;

                    Ped spawned = SpawnRosterPedForCamp(modelHash, campCentre, rosterIndex, id);
                    if (spawned == null || !spawned.Exists())
                    {
                        Log.Write("Camp roster member remains pending; model could not be materialized for record " + id + ".");
                        continue;
                    }

                    RegisterCampRosterPedWithCompanion(hooks, record, spawned, modelHash);
                    data.CampRosterPedHandles[id] = spawned.Handle;
                    dataChanged = true;
                    remainingSpawns--;
                    if (emittedIds.Add(id))
                        result.Add(new ReleasedRosterPed { Handle = spawned.Handle, Ped = spawned, RosterId = id });
                    Log.Write("Spawned one physical camp representative for saved roster record " + id +
                              " (" + (GetField(record, "Name") as string ?? "unnamed") + "), handle=" + spawned.Handle +
                              ". No second entity was found for this exact roster ID.");
                }

                foreach (ReleasedRosterPed campPed in result)
                    if (campPed != null && campPed.Ped != null && campPed.Ped.Exists()) MarkPedAsEnRageOwned(campPed.Ped);

                if (dataChanged)
                {
                    try { data.Save(); }
                    catch (Exception ex) { Log.Write("Could not persist camp roster entity handles: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Companion camp roster bridge warning: " + Unwrap(ex));
                integrationAvailable = false;
                result.Clear();
            }
            return result;
        }

        private Ped SpawnRosterPedForCamp(uint modelHash, Vector3 centre, int rosterIndex, string rosterId)
        {
            try
            {
                PedHash pedHash = (PedHash)Enum.ToObject(typeof(PedHash), modelHash);
                Model model = new Model(pedHash);
                if (!model.IsValid || !model.IsPed)
                {
                    Log.Write("Cannot materialize camp roster record " + rosterId + ": invalid Ped model hash 0x" + modelHash.ToString("X8") + ".");
                    return null;
                }
                if (!model.IsLoaded && !model.Request(10000))
                {
                    Log.Write("Camp roster model did not load for record " + rosterId + " (0x" + modelHash.ToString("X8") + ").");
                    return null;
                }

                int stable = 17;
                foreach (char c in rosterId ?? string.Empty) stable = unchecked(stable * 31 + c);
                uint stableAngle = unchecked((uint)stable) % 360U;
                double angle = stableAngle * Math.PI / 180.0 + (rosterIndex * 2.399963229728653);
                float radius = 9.0f + (rosterIndex % 4) * 1.25f;
                Vector3 position = new Vector3(
                    centre.X + (float)Math.Cos(angle) * radius,
                    centre.Y + (float)Math.Sin(angle) * radius,
                    centre.Z + 0.35f);
                float groundZ;
                if (TryGetGroundZ(position.X, position.Y, centre.Z + 20f, out groundZ) &&
                    Math.Abs(groundZ - centre.Z) <= 8f)
                    position.Z = groundZ + 0.15f;
                float heading = (float)((angle * 180.0 / Math.PI + 180.0) % 360.0);
                STREAMING.REQUEST_COLLISION_AT_COORD(position.X, position.Y, position.Z);

                Ped ped = null;
                try { ped = _spawnPedMethod.Invoke(null, new object[] { pedHash, position, heading }) as Ped; }
                catch (Exception ex) { Log.Write("enRAGE SpawnPed for roster record " + rosterId + " failed: " + Unwrap(ex)); }
                if (ped == null || !ped.Exists())
                {
                    try
                    {
                        ped = World.CreatePed(pedHash, position, heading);
                        if (ped != null && ped.Exists())
                        {
                            if (_makeOursMethod != null) _makeOursMethod.Invoke(null, new object[] { ped.Handle });
                            if (_landMethod != null) _landMethod.Invoke(null, new object[] { ped, position });
                        }
                    }
                    catch (Exception ex) { Log.Write("SHVDN camp roster fallback spawn failed for " + rosterId + ": " + Unwrap(ex)); }
                }
                if (ped != null && ped.Exists())
                {
                    try { ENTITY.SET_ENTITY_HEADING(ped.Handle, heading); } catch { }
                    try { ENTITY.SET_ENTITY_AS_MISSION_ENTITY(ped.Handle, true, true); } catch { }
                    try { PED.SET_PED_CONFIG_FLAG(ped.Handle, 297, true); } catch { }
                    try { PED.SET_PED_CONFIG_FLAG(ped.Handle, 301, false); } catch { }
                    try { ped.BlockPermanentEvents = true; } catch { }
                    return ped;
                }
            }
            catch (Exception ex) { Log.Write("Camp roster materialization failed for " + rosterId + ": " + Unwrap(ex)); }
            return null;
        }

        private void RegisterCampRosterPedWithCompanion(Type hooks, object record, Ped ped, uint modelHash)
        {
            if (hooks == null || record == null || ped == null || !ped.Exists() || ped.IsDead) return;
            try
            {
                MethodInfo cacheRecord = hooks.GetMethods(StaticFlags).FirstOrDefault(m =>
                    m.Name == "CacheRosterRecord" && m.IsStatic && m.GetParameters().Length == 4 &&
                    m.GetParameters()[0].ParameterType == typeof(int) && m.GetParameters()[1].ParameterType == typeof(uint));
                if (cacheRecord != null)
                    cacheRecord.Invoke(null, new object[] { ped.Handle, modelHash, record, ped });
                else
                    Log.Write("Companion CacheRosterRecord unavailable; this physical camp NPC will be retried on the next roster scan.");

                MethodInfo preserveOutfit = hooks.GetMethods(StaticFlags).FirstOrDefault(m =>
                    m.Name == "PreserveRecruitOutfit" && m.IsStatic && m.GetParameters().Length == 2);
                if (preserveOutfit != null)
                    preserveOutfit.Invoke(null, new object[] { record, ped });

                MethodInfo rankAffinity = hooks.GetMethods(StaticFlags).FirstOrDefault(m =>
                    m.Name == "ApplyMaximumRecruitFriendship" && m.IsStatic && m.GetParameters().Length == 1);
                if (rankAffinity != null) rankAffinity.Invoke(null, new[] { record });
                ApplyCompanionRankAffinity(record);

                FieldInfo wanderField = hooks.GetField("AmbientReleasedWanderAt", StaticFlags);
                IDictionary wander = wanderField != null ? wanderField.GetValue(null) as IDictionary : null;
                if (wander != null && wander.Contains(ped.Handle)) wander.Remove(ped.Handle);
                FieldInfo releasedField = hooks.GetField("AmbientReleasedRecruitPeds", StaticFlags);
                object released = releasedField != null ? releasedField.GetValue(null) : null;
                MethodInfo addReleased = released != null ? released.GetType().GetMethod("Add", new[] { typeof(int) }) : null;
                if (addReleased != null) addReleased.Invoke(released, new object[] { ped.Handle });
            }
            catch (Exception ex) { Log.Write("Could not associate camp ped with exact Companion roster record: " + Unwrap(ex)); }
        }

        private static uint ToUInt32Safe(object value)
        {
            if (value == null) return 0U;
            try { return unchecked((uint)Convert.ToInt64(value)); }
            catch
            {
                try { return Convert.ToUInt32(value); }
                catch { return 0U; }
            }
        }

        internal object FindPlayerFactionRecord(string rosterId)
        {
            if (!IsAvailable || string.IsNullOrWhiteSpace(rosterId)) return null;
            object main = MainInstance;
            if (main == null) return null;
            foreach (string collection in new[] { "Roster", "StoryRoster" })
            {
                IEnumerable records = GetField(main, collection) as IEnumerable;
                if (records == null) continue;
                foreach (object record in records)
                {
                    if (record == null) continue;
                    if (string.Equals(GetField(record, "Id") as string, rosterId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(GetField(record, "Gang") as string, "player_faction", StringComparison.OrdinalIgnoreCase))
                        return record;
                }
            }
            return null;
        }

        internal bool SaveRoster()
        {
            object main = MainInstance;
            if (main == null) return false;
            try
            {
                MethodInfo save = main.GetType().GetMethods(InstanceFlags).FirstOrDefault(m => m.Name == "SaveRoster" && m.GetParameters().Length == 0);
                if (save == null) return false;
                save.Invoke(main, null);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Native roster save failed: " + Unwrap(ex));
                return false;
            }
        }

        internal HashSet<int> GetKnownPlayerFactionPedHandles(PlayerFactionData data)
        {
            HashSet<int> handles = new HashSet<int>();
            try
            {
                // Persisted handles are safe only within the process that created them. On a full
                // game restart their numeric values can be reused by unrelated NPCs, so never mark
                // those new entities as owned by the player faction.
                int currentProcessId = Process.GetCurrentProcess().Id;
                bool persistedHandlesAreCurrent = data != null && data.CampRosterPedProcessId == currentProcessId;
                if (persistedHandlesAreCurrent && data.CampRosterPedHandles != null)
                {
                    foreach (KeyValuePair<string, int> saved in data.CampRosterPedHandles)
                    {
                        if (saved.Value == 0 || string.IsNullOrWhiteSpace(saved.Key)) continue;
                        object record = FindPlayerFactionRecord(saved.Key);
                        if (record == null) continue;
                        uint expectedModel = ToUInt32Safe(GetField(record, "ModelHash"));
                        Ped savedPed = null;
                        try { savedPed = new Ped(saved.Value); } catch { }
                        if (savedPed == null || !savedPed.Exists() || savedPed.IsDead) continue;
                        uint liveModel = ToUInt32Safe(savedPed.Model.Hash);
                        if (expectedModel != 0U && liveModel != expectedModel) continue;
                        handles.Add(saved.Value);
                    }
                }
                if (persistedHandlesAreCurrent && data.CampManagerPedHandle != 0)
                {
                    Ped managerPed = null;
                    try { managerPed = new Ped(data.CampManagerPedHandle); } catch { }
                    if (managerPed != null && managerPed.Exists() && !managerPed.IsDead &&
                        FounderPatches.Runtime != null && FounderPatches.Runtime.IsValidCampManagerHandle(managerPed))
                        handles.Add(data.CampManagerPedHandle);
                }

                Type hooks = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => SafeGetType(a, "EnRAGECompanionsExtension.EnRAGEHooks"))
                    .FirstOrDefault(t => t != null);
                FieldInfo cacheField = hooks != null ? hooks.GetField("RosterRecordsByPedHandle", StaticFlags) : null;
                IDictionary cache = cacheField != null ? cacheField.GetValue(null) as IDictionary : null;
                if (cache != null)
                {
                    foreach (DictionaryEntry entry in cache)
                    {
                        object cached = entry.Value;
                        object record = cached != null ? GetField(cached, "Record") : null;
                        if (!string.Equals(GetField(record, "Gang") as string, "player_faction", StringComparison.OrdinalIgnoreCase)) continue;
                        int handle;
                        try { handle = Convert.ToInt32(entry.Key); } catch { continue; }
                        if (handle != 0) handles.Add(handle);
                    }
                }
            }
            catch (Exception ex) { Log.Write("Player faction handle snapshot warning: " + Unwrap(ex)); }
            return handles;
        }

        internal List<string> GetPlayerFactionRankLabels(PlayerFactionData data)
        {
            List<string> labels = new List<string>();
            if (!IsAvailable || data == null) return labels;
            try
            {
                object gang = FindPlayerGang(data.Id);
                Array tiers = gang != null ? GetField(gang, "Tiers") as Array : null;
                if (tiers == null) return labels;
                foreach (object tier in tiers)
                {
                    object label = GetField(tier, "Item1");
                    labels.Add(label == null ? string.Empty : Convert.ToString(label));
                }
            }
            catch (Exception ex) { Log.Write("Could not read faction rank labels: " + Unwrap(ex)); }
            return labels;
        }

        private bool ApplySavedTierLabels(object playerGang, PlayerFactionData data)
        {
            if (playerGang == null || data == null || data.RankLabels == null || data.RankLabels.Length == 0)
                return false;
            try
            {
                Array tiers = GetField(playerGang, "Tiers") as Array;
                if (tiers == null || tiers.Length == 0) return false;
                Type elementType = tiers.GetType().GetElementType();
                ConstructorInfo tupleCtor = elementType != null ? elementType.GetConstructor(new[] { typeof(string), typeof(int), typeof(int) }) : null;
                if (tupleCtor == null)
                {
                    Log.Write("Faction rank labels not applied: native tier tuple constructor was not found.");
                    return false;
                }

                bool changed = false;
                int count = Math.Min(tiers.Length, data.RankLabels.Length);
                for (int i = 0; i < count; i++)
                {
                    string desired = data.RankLabels[i];
                    if (string.IsNullOrWhiteSpace(desired)) continue;
                    object tier = tiers.GetValue(i);
                    string oldLabel = Convert.ToString(GetField(tier, "Item1")) ?? string.Empty;
                    if (string.Equals(oldLabel, desired, StringComparison.Ordinal)) continue;
                    int minRank = Convert.ToInt32(GetField(tier, "Item2"));
                    int seats = Convert.ToInt32(GetField(tier, "Item3"));
                    tiers.SetValue(tupleCtor.Invoke(new object[] { desired, minRank, seats }), i);
                    changed = true;
                }
                if (changed)
                    SetField(playerGang, "Tiers", tiers);
                return changed;
            }
            catch (Exception ex)
            {
                Log.Write("Could not apply persisted faction rank labels: " + Unwrap(ex));
                return false;
            }
        }

        private object FindPlayerGang(string id)
        {
            Array current = _definedField != null ? _definedField.GetValue(null) as Array : null;
            if (current == null) return null;
            foreach (object gang in current)
                if (string.Equals(GetField(gang, "Id") as string, id, StringComparison.OrdinalIgnoreCase)) return gang;
            return null;
        }

        private static bool DictionaryEquals(IDictionary<string, int> a, IDictionary<string, int> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            foreach (KeyValuePair<string, int> pair in a)
            {
                int value;
                if (!b.TryGetValue(pair.Key, out value) || value != pair.Value) return false;
            }
            return true;
        }

        private void InstallHarmonyPatches()
        {
            if (_patched)
            {
                TryInstallCompanionIntegrationPatches();
                return;
            }

            // A previous Script instance may have been aborted before its cleanup callback ran.
            // Remove only this extension's Harmony owner before reinstalling to avoid stale prompts/hooks.
            try { new Harmony(HarmonyId).UnpatchAll(HarmonyId); }
            catch (Exception ex) { Log.Write("Stale Harmony owner cleanup warning: " + Unwrap(ex)); }
            _harmony = new Harmony(HarmonyId);
            PatchIfFound(FindMethod(_mainType, "OnTick", 2), nameof(FounderPatches.MainTickPrefix), true);
            MethodInfo updateSocial = FindPedArgumentMethod(_mainType, "UpdateSocial", 1);
            PatchFirstIfFound(updateSocial, nameof(FounderPatches.UpdateSocialPrefix));
            PatchIfFound(updateSocial, nameof(FounderPatches.UpdateSocialPostfix), false);
            PatchFirstIfFound(FindPedArgumentMethod(_mainType, "LearnCampSites", 1), nameof(FounderPatches.PreparePlayerFactionCampDiscoveryPrefix));
            PatchIfFound(FindMethod(_mainType, "AssignCamps", 0), nameof(FounderPatches.PreventPlayerFactionCampAssignmentPostfix), false);

            MethodInfo relation = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m =>
                m.Name == "Relation" && m.GetParameters().Length == 2 &&
                m.GetParameters().All(p => p.ParameterType == typeof(string)) && m.ReturnType == typeof(int));
            PatchIfFound(relation, nameof(FounderPatches.PlayerFactionRelationPrefix), true);
            MethodInfo loreRelation = _mainType.GetMethods(StaticFlags).FirstOrDefault(m =>
                m.Name == "LoreRelation" && m.IsStatic && m.GetParameters().Length == 2 &&
                m.GetParameters().All(p => p.ParameterType == typeof(string)) && m.ReturnType == typeof(int));
            PatchIfFound(loreRelation, nameof(FounderPatches.PlayerFactionRelationPrefix), true);
            MethodInfo atWarWith = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m =>
                m.Name == "AtWarWith" && m.GetParameters().Length == 2 &&
                m.GetParameters().All(p => p.ParameterType == typeof(string)) && m.ReturnType == typeof(bool));
            PatchIfFound(atWarWith, nameof(FounderPatches.PlayerFactionAtWarWithPrefix), true);

            MethodInfo spawnCamp = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m =>
                m.Name == "SpawnCamp" && m.GetParameters().Length == 3 &&
                m.GetParameters()[0].ParameterType.FullName == "NemesisSystem.CampSite" &&
                m.GetParameters()[1].ParameterType.FullName == "NemesisSystem.NemesisRecord" &&
                m.GetParameters()[2].ParameterType == typeof(Ped));
            PatchIfFound(spawnCamp, nameof(FounderPatches.BlockPlayerFactionSpawnCampPrefix), true);
            MethodInfo revealCamp = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m =>
                m.Name == "RevealCamp" && m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType.FullName == "NemesisSystem.NemesisRecord" &&
                m.GetParameters()[1].ParameterType == typeof(bool));
            PatchIfFound(revealCamp, nameof(FounderPatches.BlockPlayerFactionCampRevealPrefix), true);
            PatchIfFound(FindMethod(_mainType, "Ask", 3), nameof(FounderPatches.BlockFounderPedPrefix), true);
            PatchIfFound(FindMethod(_mainType, "Interrogable", 3), nameof(FounderPatches.BlockInterrogationPrefix), true);
            PatchIfFound(FindMethod(_mainType, "SuccessorId", 1), nameof(FounderPatches.PlayerLeaderSuccessorPrefix), true);

            MethodInfo regard = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m =>
                m.Name == "RegardOfRecord" && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.FullName == "NemesisSystem.NemesisRecord" &&
                m.ReturnType == typeof(int));
            PatchIfFound(regard, nameof(FounderPatches.PlayerFactionRegardOfRecordPrefix), true);
            if (regard == null)
                Log.Write("WARNING: NemesisMod.RegardOfRecord(NemesisRecord) not found; rank-based Hacia ti cannot be drawn.");

            MethodInfo notorietyOf = _mainType.GetMethods(InstanceFlags).FirstOrDefault(m =>
                m.Name == "NotorietyOf" && m.GetParameters().Length == 1 && m.ReturnType == typeof(int) &&
                m.GetParameters()[0].ParameterType == _gangType);
            PatchIfFound(notorietyOf, nameof(FounderPatches.PlayerFactionMissionPrestigePostfix), false);
            if (notorietyOf == null)
                Log.Write("Mission prestige postfix unavailable: NotorietyOf(Gang) was not found.");

            if (_rosterMenuType != null)
            {
                PatchIfFound(FindMethod(_rosterMenuType, "DetailActions", 0), nameof(FounderPatches.PlayerLeaderDetailActionsPostfix), false);
                PatchIfFound(FindMethod(_rosterMenuType, "ActivateDetail", 0), nameof(FounderPatches.PlayerLeaderActivateDetailPrefix), true);
                PatchIfFound(FindMethod(_rosterMenuType, "DetailActions", 0), nameof(FounderPatches.PlayerFactionMemberDetailActionsPostfix), false);
                PatchIfFound(FindMethod(_rosterMenuType, "ActivateDetail", 0), nameof(FounderPatches.PlayerFactionMemberActivateDetailPrefix), true);
                MethodInfo drawDetail = FindMethod(_rosterMenuType, "DrawDetail", 0);
                PatchIfFound(drawDetail, nameof(FounderPatches.PlayerFactionDrawDetailPrefix), true);
                PatchIfFound(drawDetail, nameof(FounderPatches.PlayerFactionDrawDetailPostfix), false);
                if (drawDetail == null)
                    Log.Write("WARNING: RosterMenu.DrawDetail() not found; player faction dossier cannot switch to gang-member presentation.");
                MethodInfo drawBar = FindMethod(_rosterMenuType, "DrawFactionBar", 1);
                PatchIfFound(drawBar, nameof(FounderPatches.PlayerFactionDrawBarPrefix), true);
                PatchIfFound(drawBar, nameof(FounderPatches.PlayerFactionDrawBarPostfix), false);
            }
            else
            {
                Log.Write("RosterMenu type unavailable; player roster actions will not be installed.");
            }

            if (_themeType != null)
            {
                MethodInfo notorietyBand = _themeType.GetMethods(StaticFlags).FirstOrDefault(m =>
                    m.Name == "NotorietyBand" && m.IsStatic && m.GetParameters().Length == 6);
                PatchIfFound(notorietyBand, nameof(FounderPatches.PlayerFactionPrestigeBandPrefix), true);
            }
            else Log.Write("Theme type unavailable; player faction prestige label will keep enRAGE's default UI.");

            _patched = true;
            TryInstallCompanionIntegrationPatches();
        }

        private void PatchFirstIfFound(MethodInfo target, string patchName)
        {
            if (target == null)
            {
                Log.Write("Harmony target unavailable; skipped high-priority " + patchName + ".");
                return;
            }
            MethodInfo patch = typeof(FounderPatches).GetMethod(patchName, StaticFlags);
            if (patch == null)
            {
                Log.Write("Harmony patch method unavailable; skipped " + patchName + ".");
                return;
            }
            HarmonyMethod method = new HarmonyMethod(patch);
            method.priority = Priority.First;
            _harmony.Patch(target, prefix: method);
            Log.Write("Harmony high-priority prefix installed: " + target.DeclaringType.Name + "." + target.Name + " -> " + patchName);
        }

        private void PatchIfFound(MethodInfo target, string patchName, bool prefix)
        {
            if (target == null)
            {
                Log.Write("Harmony target unavailable; skipped " + patchName + ".");
                return;
            }
            MethodInfo patch = typeof(FounderPatches).GetMethod(patchName, StaticFlags);
            if (patch == null)
            {
                Log.Write("Harmony patch method unavailable; skipped " + patchName + ".");
                return;
            }
            if (prefix)
                _harmony.Patch(target, prefix: new HarmonyMethod(patch));
            else
                _harmony.Patch(target, postfix: new HarmonyMethod(patch));
            Log.Write("Harmony " + (prefix ? "prefix" : "postfix") + " installed: " + target.DeclaringType.Name + "." + target.Name + " -> " + patchName);
        }

        private static MethodInfo FindPedArgumentMethod(Type type, string name, int count)
        {
            return type.GetMethods(InstanceFlags).FirstOrDefault(m => m.Name == name &&
                m.GetParameters().Length == count && m.GetParameters()[0].ParameterType == typeof(Ped));
        }

        private static MethodInfo FindMethod(Type type, string name, int count)
        {
            return type.GetMethods(InstanceFlags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == count);
        }

        private static MethodInfo FindAnyMethod(Type type, string name, int count)
        {
            return type == null ? null : type.GetMethods(InstanceFlags | StaticFlags)
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == count);
        }

        private static Type SafeGetType(Assembly assembly, string name)
        {
            try { return assembly.GetType(name, false); }
            catch { return null; }
        }

        private static object GetField(object instance, string name)
        {
            if (instance == null) return null;
            FieldInfo field = instance.GetType().GetField(name, InstanceFlags | StaticFlags);
            if (field != null) return field.GetValue(field.IsStatic ? null : instance);
            PropertyInfo property = instance.GetType().GetProperty(name, InstanceFlags | StaticFlags);
            if (property != null) return property.GetValue(property.GetGetMethod(true).IsStatic ? null : instance, null);
            return null;
        }

        private static void SetField(object instance, string name, object value)
        {
            if (instance == null) return;
            FieldInfo field = instance.GetType().GetField(name, InstanceFlags | StaticFlags);
            if (field != null)
            {
                object converted = ConvertValue(value, field.FieldType);
                field.SetValue(field.IsStatic ? null : instance, converted);
                return;
            }
            PropertyInfo property = instance.GetType().GetProperty(name, InstanceFlags | StaticFlags);
            if (property != null && property.GetSetMethod(true) != null)
                property.SetValue(property.GetSetMethod(true).IsStatic ? null : instance,
                    ConvertValue(value, property.PropertyType), null);
        }

        private static object ConvertValue(object value, Type target)
        {
            if (value == null || target.IsInstanceOfType(value)) return value;
            if (target.IsEnum) return Enum.ToObject(target, value);
            return Convert.ChangeType(value, target);
        }

        private static string Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null)
                ex = ex.InnerException;
            return ex.ToString();
        }
    }

    internal static class FounderPatches
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // FounderPatches is a sibling of EnRAGEBridge, not a nested type. Keep its
        // reflection/error helpers local so Harmony callbacks do not depend on private
        // members of the bridge class.
        private static Type SafeGetType(Assembly assembly, string name)
        {
            try { return assembly == null ? null : assembly.GetType(name, false); }
            catch { return null; }
        }

        private static string Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null)
                ex = ex.InnerException;
            return ex == null ? "Unknown error" : ex.ToString();
        }
        internal static int FounderHandle;
        internal static object MainInstance;
        internal static string PlayerFactionId;
        internal static string PlayerRecordId;
        internal static PlayerFactionRuntime Runtime;
        private static bool PlayerFactionPrestigeContext;
        private static string LastPlayerFactionDossierId;
        private static string LastSuppressedPlayerCampSpawnKey;

        internal sealed class PlayerFactionMemberDrawState
        {
            internal object Record;
            internal bool OriginalStory;
            internal bool Applied;
        }

        internal static void ResetUiContexts()
        {
            PlayerFactionPrestigeContext = false;
            LastPlayerFactionDossierId = null;
        }

        private static bool IsPlayerFactionRecord(object record)
        {
            return record != null && string.Equals(ReadMember(record, "Gang") as string,
                string.IsNullOrWhiteSpace(PlayerFactionId) ? "player_faction" : PlayerFactionId,
                StringComparison.OrdinalIgnoreCase);
        }

        private static int RegardForRank(int rank)
        {
            // This extension maps gang hierarchy to Companion/enRAGE's existing "hacia ti"
            // scale. Rank 1 remains cautious/neutral; senior members become progressively loyal.
            int[] values = { -5, 15, 35, 65, 90 };
            return values[Math.Max(0, Math.Min(values.Length - 1, rank - 1))];
        }

        internal static bool PlayerFactionRegardOfRecordPrefix(object[] __args, ref int __result)
        {
            try
            {
                object record = __args != null && __args.Length > 0 ? __args[0] : null;
                if (!IsPlayerFactionRecord(record)) return true;
                int rank = Convert.ToInt32(ReadMember(record, "Rank") ?? 1);
                string recordId = ReadMember(record, "Id") as string;
                __result = Runtime != null ? Runtime.GetPlayerFactionRegard(recordId, rank) : RegardForRank(rank);
                return false;
            }
            catch (Exception ex)
            {
                Log.Write("Player faction regard override warning: " + ex.Message);
                return true;
            }
        }

        internal static void CompanionRankFriendshipPostfix(object[] __args)
        {
            try
            {
                object record = __args != null && __args.Length > 0 ? __args[0] : null;
                if (!IsPlayerFactionRecord(record)) return;
                int rank = Convert.ToInt32(ReadMember(record, "Rank") ?? 1);
                string recordId = ReadMember(record, "Id") as string;
                int regard = Runtime != null ? Runtime.GetPlayerFactionRegard(recordId, rank) : RegardForRank(rank);
                // Companion's native relationship model consists of Hate, Fear and Disposition
                // (Like is optional and absent on this enRAGE NemesisRecord build). Keep every
                // recruit non-hostile, but let seniority alter the actual Companion disposition.
                WriteMember(record, "Hate", 0);
                WriteMember(record, "Fear", 0);
                WriteMember(record, "Like", regard); // Optional on other Companion builds.
                // Provocation lowers "Hacia ti" without making this NPC hostile to the player.
                WriteEnumMember(record, "Disposition", regard <= 0 ? "Neutral" : "Friendly");
            }
            catch (Exception ex) { Log.Write("Rank-based Companion friendship postfix warning: " + ex.Message); }
        }

        internal static void CompanionEnsurePlayerFactionRosterPostfix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2) return;
                object main = __args[0];
                object record = __args[1];
                if (main == null || record == null) return;

                // Only normalize this path when Companion itself is configured for the two fixed
                // player_faction IDs. Do not steal recruits from a deliberately different faction.
                Type hooks = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("EnRAGECompanionsExtension.EnRAGEHooks", false))
                    .FirstOrDefault(t => t != null);
                if (hooks == null) return;
                FieldInfo gangSetting = hooks.GetField("_recruitGangId", Flags);
                FieldInfo rosterSetting = hooks.GetField("_recruitRosterId", Flags);
                string configuredGang = gangSetting != null ? Convert.ToString(gangSetting.GetValue(null)) : null;
                string configuredRoster = rosterSetting != null ? Convert.ToString(rosterSetting.GetValue(null)) : null;
                string fixedId = string.IsNullOrWhiteSpace(PlayerFactionId) ? "player_faction" : PlayerFactionId;
                if (!string.Equals(configuredGang, fixedId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(configuredRoster, fixedId, StringComparison.OrdinalIgnoreCase))
                    return;

                string id = ReadMember(record, "Id") as string;
                bool story = ReadMember(record, "Story") is bool isStory && isStory;
                // Never rewrite the original Van der Linde story cast.
                if (story && !string.IsNullOrWhiteSpace(id) && id.StartsWith("vdl_", StringComparison.OrdinalIgnoreCase))
                    return;

                bool changed = false;
                string oldGang = ReadMember(record, "Gang") as string;
                if (!string.Equals(oldGang, fixedId, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(oldGang)) WriteMember(record, "FormerGang", oldGang);
                    WriteMember(record, "Gang", fixedId);
                    changed = true;
                }
                if (story)
                {
                    WriteMember(record, "Story", false);
                    changed = true;
                }

                IList roster = ReadMember(main, "Roster") as IList;
                if (roster != null && !roster.Contains(record))
                {
                    roster.Add(record);
                    changed = true;
                }

                if (changed)
                {
                    MethodInfo save = main.GetType().GetMethods(Flags).FirstOrDefault(m =>
                        m.Name == "SaveRoster" && m.GetParameters().Length == 0);
                    if (save != null) save.Invoke(main, null);
                    Log.Write("Companion roster commit normalized to player_faction: " + (ReadMember(record, "Name") as string ?? id ?? "member") + ".");
                }
                CompanionRankFriendshipPostfix(new[] { record });
                MethodInfo persistRelationship = main.GetType().GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "SaveRoster" && m.GetParameters().Length == 0);
                if (persistRelationship != null) persistRelationship.Invoke(main, null);
            }
            catch (Exception ex) { Log.Write("Companion roster membership postfix warning: " + ex.Message); }
        }

        internal static void PlayerFactionMemberDetailActionsPostfix(object __instance, ref List<string> __result)
        {
            try
            {
                if (__instance == null || __result == null) return;
                object record = ReadMember(__instance, "_detail");
                if (!IsPlayerFactionRecord(record)) return;
                if (ReadMember(record, "LeaderPage") is bool leaderPage && leaderPage) return;
                if (ReadMember(record, "Person") is bool person && person) return;
                if (!(ReadMember(record, "IsAlive") is bool alive) || !alive) return;
                if (ReadMember(record, "PresumedDead") is bool presumedDead && presumedDead) return;

                int rank = Convert.ToInt32(ReadMember(record, "Rank") ?? 1);
                int back = __result.IndexOf("Atrás");
                if (rank < 5 && !__result.Contains("Ascender rango"))
                {
                    if (back >= 0) __result.Insert(back++, "Ascender rango");
                    else __result.Add("Ascender rango");
                }
                if (rank > 1 && !__result.Contains("Degradar rango"))
                {
                    if (back >= 0) __result.Insert(back, "Degradar rango");
                    else __result.Add("Degradar rango");
                }
            }
            catch (Exception ex) { Log.Write("Player faction member rank actions warning: " + ex.Message); }
        }

        internal static bool PlayerFactionMemberActivateDetailPrefix(object __instance)
        {
            try
            {
                if (__instance == null || Runtime == null) return true;
                MethodInfo method = __instance.GetType().GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "DetailActions" && m.GetParameters().Length == 0);
                List<string> actions = method != null ? method.Invoke(__instance, null) as List<string> : null;
                if (actions == null) return true;
                object selected = ReadMember(__instance, "_action");
                int index = selected == null ? -1 : Convert.ToInt32(selected);
                if (index < 0 || index >= actions.Count) return true;
                string action = actions[index];
                if (action != "Ascender rango" && action != "Degradar rango") return true;
                object record = ReadMember(__instance, "_detail");
                if (!IsPlayerFactionRecord(record)) return true;
                Runtime.ChangePlayerFactionMemberRank(record, action == "Ascender rango" ? 1 : -1);
                return false;
            }
            catch (Exception ex)
            {
                Log.Write("Player faction member rank action failed: " + ex.Message);
                return true;
            }
        }

        internal static void PlayerFactionDrawDetailPrefix(object __instance, out PlayerFactionMemberDrawState __state)
        {
            __state = null;
            try
            {
                if (__instance == null) return;
                object record = ReadMember(__instance, "_detail");
                if (!IsPlayerFactionRecord(record)) return;
                if (ReadMember(record, "LeaderPage") is bool leaderPage && leaderPage) return;
                if (ReadMember(record, "Person") is bool person && person) return;

                bool originalStory = ReadMember(record, "Story") is bool story && story;
                if (originalStory) return;
                WriteMember(record, "Story", true);
                __state = new PlayerFactionMemberDrawState { Record = record, OriginalStory = originalStory, Applied = true };
                string id = ReadMember(record, "Id") as string;
                if (!string.Equals(LastPlayerFactionDossierId, id, StringComparison.OrdinalIgnoreCase))
                {
                    LastPlayerFactionDossierId = id;
                    Log.Write("Player faction roster dossier using enRAGE gang-member presentation with rank-based HACIA TI: " +
                        (ReadMember(record, "Name") as string ?? id ?? "member") + ".");
                }
            }
            catch (Exception ex) { Log.Write("Player faction detail presentation prefix warning: " + ex.Message); }
        }

        internal static void PlayerFactionDrawDetailPostfix(PlayerFactionMemberDrawState __state)
        {
            if (__state == null || !__state.Applied || __state.Record == null) return;
            try { WriteMember(__state.Record, "Story", __state.OriginalStory); }
            catch (Exception ex) { Log.Write("Player faction detail presentation restore warning: " + ex.Message); }
        }

        internal static void PlayerFactionDrawBarPrefix(object __instance, out bool __state)
        {
            __state = false;
            try
            {
                object gang = ReadMember(__instance, "Faction");
                string gangId = ReadMember(gang, "Id") as string;
                __state = string.Equals(gangId,
                    string.IsNullOrWhiteSpace(PlayerFactionId) ? "player_faction" : PlayerFactionId,
                    StringComparison.OrdinalIgnoreCase);
                PlayerFactionPrestigeContext = __state;
            }
            catch (Exception ex) { PlayerFactionPrestigeContext = false; Log.Write("Faction prestige context warning: " + ex.Message); }
        }

        internal static void PlayerFactionDrawBarPostfix(bool __state)
        {
            PlayerFactionPrestigeContext = false;
        }

        internal static void PlayerFactionMissionPrestigePostfix(object __0, ref int __result)
        {
            try
            {
                if (Runtime == null || __0 == null) return;
                string gangId = Convert.ToString(ReadMember(__0, "Id"));
                string playerFactionId = string.IsNullOrWhiteSpace(PlayerFactionId) ? "player_faction" : PlayerFactionId;
                if (!string.Equals(gangId, playerFactionId, StringComparison.OrdinalIgnoreCase)) return;
                __result = Math.Max(0, Math.Min(100, __result + Runtime.MissionPrestigeBonus));
            }
            catch (Exception ex) { Log.Write("Mission prestige postfix warning: " + ex.Message); }
        }

        internal static bool PlayerFactionPrestigeBandPrefix(object[] __args)
        {
            if (!PlayerFactionPrestigeContext) return true;
            try
            {
                if (Runtime == null || __args == null || __args.Length < 2) return true;
                float y = Convert.ToSingle(__args[0]);
                int value = Convert.ToInt32(__args[1]);
                return !Runtime.DrawPlayerFactionPrestigeBand(y, value);
            }
            catch (Exception ex)
            {
                Log.Write("Player faction prestige band render warning: " + ex.Message);
                return true;
            }
        }

        internal static void PlayerLeaderDetailActionsPostfix(object __instance, ref List<string> __result)
        {
            try
            {
                if (__instance == null || __result == null || string.IsNullOrWhiteSpace(PlayerFactionId) || string.IsNullOrWhiteSpace(PlayerRecordId))
                    return;
                object detail = ReadMember(__instance, "_detail");
                if (detail == null) return;
                string id = ReadMember(detail, "Id") as string;
                string gang = ReadMember(detail, "Gang") as string;
                bool leaderPage = Convert.ToBoolean(ReadMember(detail, "LeaderPage"));
                if (!leaderPage || !string.Equals(id, PlayerRecordId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(gang, PlayerFactionId, StringComparison.OrdinalIgnoreCase))
                    return;
                if (!__result.Contains("Cambiar nombre"))
                {
                    int back = __result.IndexOf("Atrás");
                    if (back >= 0) __result.Insert(back, "Cambiar nombre");
                    else __result.Add("Cambiar nombre");
                }
                if (Runtime.NeedsCampSelection && !__result.Contains("Elegir campamento"))
                {
                    int back = __result.IndexOf("Atrás");
                    if (back >= 0) __result.Insert(back, "Elegir campamento");
                    else __result.Add("Elegir campamento");
                }
            }
            catch (Exception ex) { Log.Write("Leader detail action postfix failed: " + ex.Message); }
        }

        internal static bool PlayerLeaderActivateDetailPrefix(object __instance)
        {
            try
            {
                if (__instance == null || Runtime == null) return true;
                MethodInfo method = __instance.GetType().GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "DetailActions" && m.GetParameters().Length == 0);
                List<string> actions = method != null ? method.Invoke(__instance, null) as List<string> : null;
                if (actions == null) return true;
                object selected = ReadMember(__instance, "_action");
                int index = selected == null ? -1 : Convert.ToInt32(selected);
                if (index >= 0 && index < actions.Count)
                {
                    if (actions[index] == "Cambiar nombre")
                    {
                        Runtime.BeginLeaderRenameFromRoster();
                        return false;
                    }
                    if (actions[index] == "Elegir campamento")
                    {
                        Runtime.BeginCampLocationFromRoster();
                        return false;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Leader rename action failed: " + ex.Message); }
            return true;
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null) return null;
            FieldInfo field = instance.GetType().GetField(name, Flags);
            if (field != null) return field.GetValue(field.IsStatic ? null : instance);
            PropertyInfo property = instance.GetType().GetProperty(name, Flags);
            return property != null ? property.GetValue(property.GetGetMethod(true).IsStatic ? null : instance, null) : null;
        }

        private static void WriteMember(object instance, string name, object value)
        {
            if (instance == null) return;
            FieldInfo field = instance.GetType().GetField(name, Flags);
            if (field != null)
            {
                object converted = value == null || field.FieldType.IsInstanceOfType(value)
                    ? value : Convert.ChangeType(value, field.FieldType);
                field.SetValue(field.IsStatic ? null : instance, converted);
                return;
            }
            PropertyInfo property = instance.GetType().GetProperty(name, Flags);
            if (property != null && property.GetSetMethod(true) != null)
            {
                object converted = value == null || property.PropertyType.IsInstanceOfType(value)
                    ? value : Convert.ChangeType(value, property.PropertyType);
                property.SetValue(property.GetSetMethod(true).IsStatic ? null : instance, converted, null);
            }
        }

        private static void WriteEnumMember(object instance, string name, string desiredName)
        {
            if (instance == null) return;
            PropertyInfo property = instance.GetType().GetProperty(name, Flags);
            FieldInfo field = property == null ? instance.GetType().GetField(name, Flags) : null;
            Type memberType = property != null ? property.PropertyType : (field != null ? field.FieldType : null);
            if (memberType == null || !memberType.IsEnum) return;
            string memberName = Enum.GetNames(memberType).FirstOrDefault(n =>
                string.Equals(n, desiredName, StringComparison.OrdinalIgnoreCase));
            if (memberName == null) return;
            object value = Enum.Parse(memberType, memberName, true);
            if (property != null && property.GetSetMethod(true) != null)
                property.SetValue(property.GetSetMethod(true).IsStatic ? null : instance, value, null);
            else if (field != null)
                field.SetValue(field.IsStatic ? null : instance, value);
        }

        internal static void MainTickPrefix(object __instance)
        {
            MainInstance = __instance;
        }


        internal static bool PlayerLeaderSuccessorPrefix(object[] __args, ref string __result)
        {
            if (string.IsNullOrWhiteSpace(PlayerFactionId) || string.IsNullOrWhiteSpace(PlayerRecordId) ||
                __args == null || __args.Length == 0 || __args[0] == null)
                return true;

            try
            {
                object gang = __args[0];
                Type type = gang.GetType();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                object gangId = null;
                FieldInfo field = type.GetField("Id", flags);
                if (field != null)
                    gangId = field.GetValue(field.IsStatic ? null : gang);
                else
                {
                    PropertyInfo property = type.GetProperty("Id", flags);
                    if (property != null)
                        gangId = property.GetValue(property.GetGetMethod(true).IsStatic ? null : gang, null);
                }

                if (string.Equals(gangId as string, PlayerFactionId, StringComparison.OrdinalIgnoreCase))
                {
                    __result = PlayerRecordId;
                    return false;
                }
            }
            catch
            {
                // Leave enRAGE's normal succession result intact if reflection ever fails.
            }
            return true;
        }

        internal static bool BlockFounderPedPrefix(object[] __args)
        {
            if (FounderHandle == 0 || __args == null)
                return true;
            foreach (object arg in __args)
            {
                Ped ped = arg as Ped;
                if (ped != null && ped.Handle == FounderHandle)
                    return false;
            }
            return true;
        }

        internal static bool BlockInterrogationPrefix(object[] __args, ref bool __result)
        {
            if (FounderHandle == 0 || __args == null)
                return true;
            foreach (object arg in __args)
            {
                Ped ped = arg as Ped;
                if (ped != null && ped.Handle == FounderHandle)
                {
                    __result = false;
                    return false;
                }
            }
            return true;
        }

        internal static bool PreparePlayerFactionCampDiscoveryPrefix(object __instance, object[] __args)
        {
            try
            {
                if (Runtime != null) Runtime.PreparePlayerFactionForCampDiscovery();
            }
            catch (Exception ex) { Log.Write("Pre-LearnCampSites player-faction guard warning: " + ex.Message); }
            return true;
        }

        internal static bool PlayerFactionRelationPrefix(object[] __args, ref int __result)
        {
            string fixedId = string.IsNullOrWhiteSpace(PlayerFactionId) ? "player_faction" : PlayerFactionId;
            if (__args != null && __args.Length >= 2 &&
                string.Equals(__args[0] as string, fixedId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(__args[1] as string, fixedId, StringComparison.OrdinalIgnoreCase))
            {
                __result = 100; // Self-relations are always friendly; relations with outside gangs remain native.
                return false;
            }
            return true;
        }

        internal static bool PlayerFactionAtWarWithPrefix(object[] __args, ref bool __result)
        {
            string fixedId = string.IsNullOrWhiteSpace(PlayerFactionId) ? "player_faction" : PlayerFactionId;
            if (__args != null && __args.Length >= 2 &&
                string.Equals(__args[0] as string, fixedId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(__args[1] as string, fixedId, StringComparison.OrdinalIgnoreCase))
            {
                __result = false;
                return false;
            }
            return true;
        }

        internal static bool UpdateSocialPrefix(object __instance, object[] __args)
        {
            if (__instance == null || __args == null || __args.Length == 0 || !(__args[0] is Ped player))
                return true;

            try
            {
                MethodInfo socialTarget = __instance.GetType().GetMethods(Flags)
                    .FirstOrDefault(m => m.Name == "SocialTarget" && m.GetParameters().Length == 1 &&
                                         m.GetParameters()[0].ParameterType == typeof(Ped));
                if (socialTarget == null) return true;
                object result = socialTarget.Invoke(__instance, new object[] { player });
                if (result == null) return true;
                FieldInfo item1 = result.GetType().GetField("Item1");
                Ped target = item1 != null ? item1.GetValue(result) as Ped : null;
                if (target == null) return true;

                if (FounderHandle != 0 && target.Handle == FounderHandle)
                {
                    MethodInfo hideFounder = __instance.GetType().GetMethods(Flags)
                        .FirstOrDefault(m => m.Name == "HideSocialPrompt" && m.GetParameters().Length == 0);
                    if (hideFounder != null) hideFounder.Invoke(__instance, null);
                    return false; // founder NPC only exposes our own prompt.
                }

                // Player-camp residents should use RDR2's native greet/antagonize interaction.
                // Suppress only enRAGE's built-in social prompt; leave Companion's separate
                // call/follow prompt untouched.
                if (Runtime != null && Runtime.IsCampManagerSocialTarget(target.Handle, player))
                {
                    MethodInfo hideManager = __instance.GetType().GetMethods(Flags)
                        .FirstOrDefault(m => m.Name == "HideSocialPrompt" && m.GetParameters().Length == 0);
                    if (hideManager != null) hideManager.Invoke(__instance, null);
                    return false; // manager only exposes its dedicated administration prompt.
                }

                if (Runtime != null && Runtime.IsPlayerFactionRosterPed(target.Handle))
                {
                    // Let enRAGE's original social cycle run so the game's native greet/antagonize
                    // interaction can operate. The postfix below removes only enRAGE's extra prompt.
                    return true;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("UpdateSocial target guard warning: " + ex.Message);
                return true;
            }
        }

        private static Ped GetSocialTarget(object main, Ped player)
        {
            if (main == null || player == null || !player.Exists()) return null;
            try
            {
                MethodInfo socialTarget = main.GetType().GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "SocialTarget" && m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(Ped));
                if (socialTarget == null) return null;
                object result = socialTarget.Invoke(main, new object[] { player });
                if (result == null) return null;
                FieldInfo item1 = result.GetType().GetField("Item1");
                return item1 != null ? item1.GetValue(result) as Ped : null;
            }
            catch (Exception ex)
            {
                Log.Write("Player-faction native social target lookup warning: " + Unwrap(ex));
                return null;
            }
        }

        internal static void UpdateSocialPostfix(object __instance, object[] __args)
        {
            try
            {
                if (Runtime == null || __instance == null || __args == null ||
                    __args.Length == 0 || !(__args[0] is Ped player))
                    return;
                Ped target = GetSocialTarget(__instance, player);
                if (target == null || !target.Exists() ||
                    !Runtime.IsPlayerFactionRosterPed(target.Handle))
                    return;

                // Remove only enRAGE's additional social prompt after its update. The native
                // RDR2 greet/antagonize controls and Companion's separate call prompt remain.
                MethodInfo hide = __instance.GetType().GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "HideSocialPrompt" && m.GetParameters().Length == 0);
                if (hide != null) hide.Invoke(__instance, null);
            }
            catch (Exception ex)
            {
                Log.Write("Player-faction social prompt cleanup warning: " + Unwrap(ex));
            }
        }

        internal static bool AllowNativeSocialForPlayerFactionPrefix(object[] __args, ref bool __result)
        {
            try
            {
                // This patch targets Companion's own UpdateSocialPrefix method. Its arguments are
                // (NemesisMod instance, original UpdateSocial arguments[]).
                if (Runtime == null || __args == null || __args.Length < 2)
                    return true;
                object main = __args[0];
                object[] socialArgs = __args[1] as object[];
                Ped player = socialArgs != null && socialArgs.Length > 0 ? socialArgs[0] as Ped : null;
                Ped target = GetSocialTarget(main, player);
                if (target == null || !target.Exists() ||
                    !Runtime.IsPlayerFactionRosterPed(target.Handle))
                    return true;

                // Preserve Companion's normal generic prompt target for its independent follow/call
                // action, but skip its replacement of native greet/antagonize.
                Type hooks = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => SafeGetType(a, "EnRAGECompanionsExtension.EnRAGEHooks"))
                    .FirstOrDefault(t => t != null);
                FieldInfo preferred = hooks != null ? hooks.GetField("_preferredGenericPed", Flags) : null;
                if (preferred != null) preferred.SetValue(null, target);

                __result = true; // Continue into NemesisMod.UpdateSocial.
                return false;    // Skip only Companion's social override.
            }
            catch (Exception ex)
            {
                Log.Write("Companion native-social bypass warning: " + Unwrap(ex));
                return true;
            }
        }

        internal static bool BlockCampManagerGenericRecruitmentPrefix(object[] __args, ref bool __result)
        {
            try
            {
                Ped candidate = __args != null && __args.Length > 2 ? __args[2] as Ped : null;
                if (Runtime == null || candidate == null || !candidate.Exists() || !Runtime.IsCampManagerPed(candidate))
                    return true;

                // The administrator is a permanent camp service NPC, never a Companion recruit.
                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                Log.Write("Camp administrator recruitment guard warning: " + Unwrap(ex));
                return true;
            }
        }

        internal static void RestoreNativeCampMemberInteractionPostfix(Ped __0)
        {
            try
            {
                if (Runtime == null || __0 == null || !__0.Exists() ||
                    !Runtime.IsPlayerFactionRosterPed(__0.Handle))
                    return;
                Runtime.EnsureNativeCampMemberSocialInteraction(__0);
            }
            catch (Exception ex)
            {
                Log.Write("Companion camp-member native-interaction restore warning: " + Unwrap(ex));
            }
        }

        internal static void PreventPlayerFactionCampAssignmentPostfix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                bool changed = false;
                foreach (string collectionName in new[] { "Roster", "StoryRoster" })
                {
                    IList roster = ReadMember(__instance, collectionName) as IList;
                    if (roster == null) continue;
                    foreach (object record in roster)
                    {
                        if (!IsPlayerFactionRecord(record)) continue;
                        if (ReadMember(record, "CampId") != null) { WriteMember(record, "CampId", null); changed = true; }
                        if (ReadMember(record, "BossId") != null) { WriteMember(record, "BossId", null); changed = true; }
                        if (ReadMember(record, "CampKnown") is bool known && known) { WriteMember(record, "CampKnown", false); changed = true; }
                        if (ReadMember(record, "GaveCamp") is bool gave && gave) { WriteMember(record, "GaveCamp", false); changed = true; }
                    }
                }
                if (changed)
                {
                    MethodInfo save = __instance.GetType().GetMethods(Flags).FirstOrDefault(m => m.Name == "SaveRoster" && m.GetParameters().Length == 0);
                    if (save != null) save.Invoke(__instance, null);
                    Log.Write("Cleared enemy-camp assignments from player_faction roster records.");
                }
            }
            catch (Exception ex) { Log.Write("Player faction camp assignment guard warning: " + ex.Message); }
        }

        internal static bool BlockPlayerFactionCampRevealPrefix(object[] __args)
        {
            try
            {
                object record = __args != null && __args.Length > 0 ? __args[0] : null;
                if (record == null) return true;
                string gangId = ReadMember(record, "Gang") as string;
                string campId = ReadMember(record, "CampId") as string;
                bool playerFactionMember = IsPlayerFactionRecord(record);
                bool playerCampAssignment = Runtime != null && Runtime.IsPlayerCampSiteId(campId);
                if (!playerFactionMember && !playerCampAssignment) return true;

                // Native RevealCamp emits the exact “you've found ... camp” notice and marks the
                // boss as camp-known. A member of our own faction must never produce that message
                // or be treated as the boss of an enemy camp when returning to our own site.
                WriteMember(record, "CampKnown", false);
                WriteMember(record, "GaveCamp", false);
                WriteMember(record, "CampId", null);
                WriteMember(record, "BossId", null);
                if (string.IsNullOrWhiteSpace(LastSuppressedPlayerCampSpawnKey) ||
                    !LastSuppressedPlayerCampSpawnKey.StartsWith("reveal|", StringComparison.Ordinal))
                {
                    LastSuppressedPlayerCampSpawnKey = "reveal|" + (ReadMember(record, "Id") as string ?? gangId ?? campId ?? "member");
                    Log.Write("Blocked enRAGE RevealCamp for a player_faction member/own camp assignment.");
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Write("Player faction camp-reveal guard warning: " + ex.Message);
                return true;
            }
        }

        internal static bool BlockPlayerFactionSpawnCampPrefix(object[] __args)
        {
            try
            {
                string fixedId = string.IsNullOrWhiteSpace(PlayerFactionId) ? "player_faction" : PlayerFactionId;
                if (__args == null) return true;
                foreach (object arg in __args)
                {
                    if (arg == null) continue;
                    string gangId = ReadMember(arg, "Gang") as string;
                    string siteId = ReadMember(arg, "Id") as string;
                    bool isSite = arg.GetType().FullName == "NemesisSystem.CampSite";
                    bool playerGangRecord = IsPlayerFactionRecord(arg);
                    if (string.Equals(gangId, fixedId, StringComparison.OrdinalIgnoreCase) && (isSite || playerGangRecord))
                    {
                        string key = (siteId ?? gangId) + "|" + (ReadMember(arg, "Name") as string ?? string.Empty);
                        if (!string.Equals(LastSuppressedPlayerCampSpawnKey, key, StringComparison.Ordinal))
                        {
                            LastSuppressedPlayerCampSpawnKey = key;
                            Log.Write("Blocked enRAGE enemy live-camp spawn involving player_faction: " + key + ".");
                        }
                        return false;
                    }
                    if (isSite && Runtime != null && Runtime.IsPlayerCampSiteId(siteId))
                    {
                        string key = siteId ?? "player camp site";
                        if (!string.Equals(LastSuppressedPlayerCampSpawnKey, key, StringComparison.Ordinal))
                        {
                            LastSuppressedPlayerCampSpawnKey = key;
                            Log.Write("Blocked enRAGE live enemy camp at the player's own CampSite: " + key + ".");
                        }
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex) { Log.Write("Player faction enemy-camp spawn guard warning: " + ex.Message); return true; }
        }
    }
}
