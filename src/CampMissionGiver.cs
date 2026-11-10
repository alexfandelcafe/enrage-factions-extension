using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using RDR2;
using RDR2.Math;
using RDR2.Native;

namespace EnRAGEPlayerFactionExtension
{
    /// <summary>
    /// Independent mission giver and rotating set of player-faction contracts. It intentionally
    /// does not subscribe to or consume enRAGE WorldEvent/Encounter events.
    /// </summary>
    internal sealed class CampMissionGiver
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const int ContextControl = 1980406895;
        // RDR2 INPUT_RELOAD: a separate contextual action so an active contract can be abandoned.
        private const int CancelMissionControl = unchecked((int)0xE30CD707);
        private const ulong PromptGetGroupForEntityHash = 0xB796970BD125FCE8UL;
        private const ulong PromptHasHoldModeCompletedHash = 0xE0F65F0640EF0617UL;
        private const ulong PromptHasStandardModeCompletedHash = 0xC92AC953F0A982AEUL;
        private const ulong SetAmbientPromptGroupThisFrameHash = 0x315C81D760609108UL;
        private const ulong SetPedCanBeTargettedHash = 0x63F58F7C80513AADUL;
        private const ulong SetPedCanBeTargettedByPlayerHash = 0x66B57B72E0836A76UL;
        private const ulong SetEntityCanBeDamagedHash = 0x0D06D522B90E861FUL;
        private const ulong SetBlipNameHash = 11290983183885202434UL;
        private const uint NativeMapBlipStyle = 2339406903U;
        private const string PedModel = "U_M_M_RhdBartender_01";
        private const string SupplyCacheModel = "p_crate03x";
        private const string TargetSprite = "blip_objective";
        private const int MissionReconnaissance = 0;
        private const int MissionPatrol = 1;
        private const int MissionSupplyCache = 2;
        private const int MissionStakeout = 3;
        private const int MissionCourier = 4;
        private const int MissionConvoy = 5;
        private const int MissionEliminateTargets = 6;
        private const int MissionDestroyTarget = 7;
        private const int MissionRescuePrisoner = 8;
        private const int MissionHuntAnimal = 9;
        private const int PatrolWaypointCount = 3;
        private const int StakeoutDurationMs = 24000;
        private const float StakeoutRadius = 35f;

        private readonly EnRAGEBridge _bridge;
        private readonly PlayerFactionData _faction;
        private readonly MissionObjectiveController _objectiveController;
        private readonly MissionStoryDirector _story;
        private Ped _ped;
        private Prop _supplyCache;
        private int _nextCacheSpawnRetryAt;
        private int _cacheSpawnFailures;
        private int _nextTargetGroundCheckAt;
        private int _nextEscortReminderAt;
        private int _stakeoutRadiusBlip;
        private int _prompt;
        private int _promptGroup;
        private string _promptLabel = string.Empty;
        private int _cancelPrompt;
        private int _cancelPromptGroup;
        private bool _cancelPromptArmed = true;
        private int _lootPrompt;
        private int _lootPromptGroup;
        private int _lootPromptEntityHandle;
        private bool _lootPromptArmed = true;
        private bool _lootPromptJustCompleted;
        private int _objectivePrompt;
        private int _objectivePromptGroup;
        private int _objectivePromptEntityHandle;
        private string _objectivePromptLabel = string.Empty;
        private bool _objectivePromptArmed = true;
        private bool _objectivePromptJustCompleted;
        private int _targetBlip;
        private int _nextSpawnAt;
        private bool _spawnDelayInitialized;
        private int _nextProtectionAt;
        private int _nextRewardRetryAt;
        private int _lastMissionProgressTickAt;
        private int _nextMissionProgressSaveAt;
        private bool _missionTimerInitialized;
        private int _nextStakeoutStatusAt;
        private bool _promptArmed = true;

        internal CampMissionGiver(EnRAGEBridge bridge, PlayerFactionData faction)
        {
            _bridge = bridge;
            _faction = faction;
            _objectiveController = new MissionObjectiveController(faction);
            _story = new MissionStoryDirector(Notify);
        }

        internal void Maintain(Ped player, bool interactionBlocked)
        {
            if (!_faction.Founded || string.IsNullOrWhiteSpace(_faction.CampSiteId))
            {
                DeleteSupplyCacheProp();
                RemoveLootPrompt();
                RemoveObjectivePrompt();
                _objectiveController.CleanupConvoy();
                _story.OnScriptAborted();
                DeletePed();
                RemoveTargetBlip();
                return;
            }

            if (player == null || !player.Exists() || player.IsDead || !_bridge.IsAvailable)
            {
                SetPromptVisible(false);
                SetLootPromptVisible(false);
                SetObjectivePromptVisible(false);
                return;
            }

            Vector3 campCentre = _bridge.GetPlayerCampCentre(_faction);
            if (campCentre == Vector3.Zero)
                campCentre = new Vector3(_faction.CampX, _faction.CampY, _faction.CampZ);
            if (campCentre == Vector3.Zero) return;

            if (_faction.MissionStage == 1) RefineMissionTargetHeight(player);

            if (_faction.MissionStage == 1 && _faction.MissionType == MissionSupplyCache)
                MaintainSupplyCacheProp(player);
            else if (_faction.MissionCachePropHandle != 0)
            {
                DeleteSupplyCacheProp();
                SaveFaction();
            }
            if (_faction.MissionStage == 1 &&
                (_faction.MissionType == MissionSupplyCache || _faction.MissionType == MissionStakeout ||
                 _faction.MissionType == MissionCourier || _faction.MissionType == MissionConvoy ||
                 _faction.MissionType == MissionEliminateTargets || _faction.MissionType == MissionDestroyTarget ||
                 _faction.MissionType == MissionRescuePrisoner || _faction.MissionType == MissionHuntAnimal))
                _objectiveController.Maintain(player);

            _story.Tick();
            if (_faction.MissionStage == 1 && _faction.MissionType == MissionConvoy)
                MaintainConvoyLootPrompt(player);
            else
                RemoveLootPrompt();

            MaintainObjectivePrompt(player);
            UpdateMissionProgress(player, campCentre);
            if (_faction.MissionStage != 1 || _faction.MissionType != MissionConvoy)
                RemoveLootPrompt();
            if (_faction.MissionStage != 1)
                RemoveObjectivePrompt();
            if (_faction.MissionStage > 0) EnsureTargetBlip();
            else RemoveTargetBlip();

            float distanceToCamp = player.Position.DistanceTo(campCentre);
            ReconnectPed(campCentre);
            if (distanceToCamp > 180f)
            {
                DeletePed();
                return;
            }

            // Do not call enRAGE's SpawnPed twice in the same frame as the camp administrator.
            // That NPC is maintained immediately before this one in PlayerFactionRuntime.Tick().
            if ((_ped == null || !_ped.Exists()) && distanceToCamp <= 85f && !_spawnDelayInitialized)
            {
                _spawnDelayInitialized = true;
                _nextSpawnAt = Game.GameTime + 2000;
                Log.Write("Mission giver spawn delayed briefly to avoid two camp-NPC spawns in the same tick.");
            }

            if ((_ped == null || !_ped.Exists()) && distanceToCamp <= 85f && Game.GameTime >= _nextSpawnAt)
            {
                _nextSpawnAt = Game.GameTime + 3500;
                SpawnPed(campCentre);
            }
            if (_ped == null || !_ped.Exists()) return;

            if (Game.GameTime >= _nextProtectionAt)
            {
                _nextProtectionAt = Game.GameTime + 750;
                ProtectPed(player);
            }

            float distanceToPed = player.Position.DistanceTo(_ped.Position);
            if (interactionBlocked || distanceToPed > 3.2f || player.IsOnMount || player.IsInVehicle)
            {
                SetPromptVisible(false);
                SetCancelPromptVisible(false);
                _promptArmed = true;
                _cancelPromptArmed = true;
                return;
            }
            UpdatePrompt();
        }

        internal void OnScriptAborted()
        {
            SetPromptVisible(false);
            RemovePrompt();
            RemoveCancelPrompt();
            RemoveLootPrompt();
            RemoveObjectivePrompt();
            RemoveTargetBlip();
            _story.OnScriptAborted();
            // Keep the NPC and its handle alive; the next ScriptHook runtime reconnects to it.
        }

        private void SpawnPed(Vector3 campCentre)
        {
            try
            {
                Vector3 position = new Vector3(campCentre.X - 2.5f, campCentre.Y + 2.5f, campCentre.Z + 0.25f);
                STREAMING.REQUEST_COLLISION_AT_COORD(position.X, position.Y, position.Z);
                Ped ped = _bridge.SpawnFounder(PedModel, position, 225f);
                if (ped == null || !ped.Exists())
                {
                    Log.Write("Mission giver NPC spawn failed at " + position + ".");
                    return;
                }

                _ped = ped;
                _bridge.MarkPedAsEnRageOwned(ped);
                _faction.MissionGiverPedHandle = ped.Handle;
                _promptArmed = true;
                SaveFaction();
                ProtectPed(Game.Player.Character);
                Log.Write("Independent mission giver spawned at the player camp. Handle=" + ped.Handle + ", position=" + position + ".");
            }
            catch (Exception ex) { Log.Write("Mission giver spawn exception: " + ex); }
        }

        private void ReconnectPed(Vector3 campCentre)
        {
            if (_ped != null && _ped.Exists()) return;
            if (_faction.MissionGiverPedHandle == 0) return;

            try
            {
                Ped candidate = new Ped(_faction.MissionGiverPedHandle);
                uint modelHash = MISC.GET_HASH_KEY(PedModel);
                bool belongsToCamp = candidate.Exists() && candidate.Position.DistanceTo(campCentre) <= 25f;
                bool expectedModel = candidate.Exists() && unchecked((uint)candidate.Model.Hash) == modelHash;
                if (belongsToCamp && expectedModel)
                {
                    _ped = candidate;
                    _bridge.MarkPedAsEnRageOwned(candidate);
                    _promptArmed = true;
                    Log.Write("Reconnected to the mission giver after script reload. Handle=" + candidate.Handle + ".");
                    return;
                }
            }
            catch (Exception ex) { Log.Write("Mission giver reconnect warning: " + ex.Message); }

            _faction.MissionGiverPedHandle = 0;
            SaveFaction();
        }

        private void DeletePed()
        {
            SetPromptVisible(false);
            SetCancelPromptVisible(false);
            RemovePrompt();
            RemoveCancelPrompt();
            try { if (_ped != null && _ped.Exists()) _ped.Delete(); }
            catch (Exception ex) { Log.Write("Mission giver cleanup warning: " + ex.Message); }
            _ped = null;
            if (_faction.MissionGiverPedHandle != 0)
            {
                _faction.MissionGiverPedHandle = 0;
                SaveFaction();
            }
            _promptArmed = true;
            _spawnDelayInitialized = false;
            _nextSpawnAt = 0;
        }

        private void ProtectPed(Ped player)
        {
            if (_ped == null || !_ped.Exists()) return;
            try
            {
                ENTITY.SET_ENTITY_INVINCIBLE(_ped.Handle, true);
                Function.Call(SetEntityCanBeDamagedHash, new InputArgument[] { _ped.Handle, false });
                Function.Call(SetPedCanBeTargettedHash, new InputArgument[] { _ped.Handle, false });
                if (player != null && player.Exists())
                    Function.Call(SetPedCanBeTargettedByPlayerHash, new InputArgument[] { _ped.Handle, Game.Player.Handle, false });
                _ped.BlockPermanentEvents = true;
                PED.SET_PED_CONFIG_FLAG(_ped.Handle, 130, false);
            }
            catch (Exception ex) { Log.Write("Mission giver protection warning: " + ex.Message); }
        }

        private void UpdatePrompt()
        {
            if (_ped == null || !_ped.Exists()) return;
            int group;
            try { group = Function.Call<int>(PromptGetGroupForEntityHash, new InputArgument[] { _ped.Handle }); }
            catch (Exception ex) { Log.Write("Mission giver prompt group lookup warning: " + ex.Message); return; }

            string desiredLabel = _faction.MissionStage > 0 ? "Consultar el encargo" : "Pedir una misión";
            if (_prompt == 0 || _promptGroup != group ||
                !string.Equals(_promptLabel, desiredLabel, StringComparison.Ordinal) || !PromptValid(_prompt))
            {
                RemovePrompt();
                if (!TryCreatePrompt(group, desiredLabel)) return;
            }

            SetPromptVisible(true);
            ActivatePromptGroup(group);
            if (UpdateCancelMissionPrompt(group)) return;
            bool completed = PromptCompleted(_prompt);
            if (!completed)
            {
                _promptArmed = true;
                return;
            }
            if (!_promptArmed) return;

            _promptArmed = false;
            SetPromptVisible(false);
            OnInteract();
        }

        private bool TryCreatePrompt(int group, string label)
        {
            try
            {
                Type mainType = _bridge.MainInstance != null ? _bridge.MainInstance.GetType() : null;
                if (mainType == null) return false;
                MethodInfo make = mainType.GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "MakePrompt" && m.GetParameters().Length == 4);
                if (make == null) return false;
                Type controlType = make.GetParameters()[0].ParameterType;
                object control = controlType.IsEnum ? Enum.ToObject(controlType, ContextControl) : (object)ContextControl;
                _prompt = Convert.ToInt32(make.Invoke(null, new object[] { control, label, true, group }));
                if (_prompt == 0) return false;
                _promptGroup = group;
                _promptLabel = label;
                _promptArmed = true;
                Log.Write("Mission giver prompt created. Prompt=" + _prompt + ", group=" + group + ", label=" + label + ".");
                return true;
            }
            catch (Exception ex) { Log.Write("Mission giver prompt creation failed: " + ex.Message); return false; }
        }

        private bool PromptValid(int prompt)
        {
            try
            {
                Type mainType = _bridge.MainInstance != null ? _bridge.MainInstance.GetType() : null;
                MethodInfo method = mainType != null ? mainType.GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "PromptValid" && m.GetParameters().Length == 1) : null;
                return method != null && Convert.ToBoolean(method.Invoke(null, new object[] { prompt }));
            }
            catch { return false; }
        }

        private bool PromptCompleted(int prompt)
        {
            if (prompt == 0 || !PromptValid(prompt)) return false;
            try
            {
                if (Function.Call<bool>(PromptHasHoldModeCompletedHash, new InputArgument[] { prompt })) return true;
            }
            catch { }
            try { return Function.Call<bool>(PromptHasStandardModeCompletedHash, new InputArgument[] { prompt }); }
            catch { return false; }
        }


        private void ActivatePromptGroup(int group)
        {
            if (_ped == null || !_ped.Exists() || _prompt == 0) return;
            try
            {
                Assembly assembly = _bridge.MainInstance != null
                    ? _bridge.MainInstance.GetType().Assembly
                    : AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetType("NemesisSystem.UiDraw", false) != null);
                Type uiDraw = assembly != null ? assembly.GetType("NemesisSystem.UiDraw", false) : null;
                MethodInfo forever = uiDraw != null ? uiDraw.GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "ForeverVarString" && m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(string)) : null;
                if (forever == null) return;
                ulong label = Convert.ToUInt64(forever.Invoke(null, new object[] { "Encargado de misiones" }));
                Function.Call(SetAmbientPromptGroupThisFrameHash, new InputArgument[]
                {
                    _ped.Handle, 3.5f, 2, 0, group, new InputArgument(label), 0
                });
            }
            catch (Exception ex) { Log.Write("Mission giver ambient prompt warning: " + ex.Message); }
        }

        private void SetPromptVisible(bool visible)
        {
            if (_prompt == 0) return;
            try
            {
                Type hud = typeof(Game).Assembly.GetType("RDR2.Native.HUD", false);
                if (hud == null) return;
                InvokeHud(hud, "_UI_PROMPT_SET_VISIBLE", _prompt, visible);
                InvokeHud(hud, "_UI_PROMPT_SET_ENABLED", _prompt, visible);
            }
            catch (Exception ex) { Log.Write("Mission giver prompt visibility warning: " + ex.Message); }
        }

        private void RemovePrompt()
        {
            if (_prompt != 0)
            {
                try
                {
                    Type hud = typeof(Game).Assembly.GetType("RDR2.Native.HUD", false);
                    if (hud != null) InvokeHud(hud, "_UI_PROMPT_DELETE", _prompt);
                }
                catch (Exception ex) { Log.Write("Mission giver prompt delete warning: " + ex.Message); }
            }
            _prompt = 0;
            _promptGroup = 0;
            _promptLabel = string.Empty;
            _promptArmed = true;
        }

        private bool UpdateCancelMissionPrompt(int group)
        {
            if (_faction.MissionStage <= 0)
            {
                RemoveCancelPrompt();
                return false;
            }
            if (_cancelPrompt == 0 || _cancelPromptGroup != group || !PromptValid(_cancelPrompt))
            {
                RemoveCancelPrompt();
                if (!TryCreateCancelPrompt(group)) return false;
            }
            SetCancelPromptVisible(true);
            bool completed = PromptCompleted(_cancelPrompt);
            if (!completed)
            {
                _cancelPromptArmed = true;
                return false;
            }
            if (!_cancelPromptArmed) return false;

            _cancelPromptArmed = false;
            SetCancelPromptVisible(false);
            RemoveCancelPrompt();
            FailActiveMission("Has abandonado el encargo voluntariamente. No se entregará recompensa.");
            return true;
        }

        private bool TryCreateCancelPrompt(int group)
        {
            try
            {
                Type mainType = _bridge.MainInstance != null ? _bridge.MainInstance.GetType() : null;
                if (mainType == null) return false;
                MethodInfo make = mainType.GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "MakePrompt" && m.GetParameters().Length == 4);
                if (make == null) return false;
                Type controlType = make.GetParameters()[0].ParameterType;
                object control = controlType.IsEnum
                    ? Enum.ToObject(controlType, CancelMissionControl)
                    : (object)CancelMissionControl;
                _cancelPrompt = Convert.ToInt32(make.Invoke(null,
                    new object[] { control, "Abandonar encargo", true, group }));
                if (_cancelPrompt == 0) return false;
                _cancelPromptGroup = group;
                _cancelPromptArmed = true;
                Log.Write("Mission cancellation prompt created. Prompt=" + _cancelPrompt + ", group=" + group + ".");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Mission cancellation prompt creation warning: " + ex.Message);
                RemoveCancelPrompt();
                return false;
            }
        }

        private void SetCancelPromptVisible(bool visible)
        {
            if (_cancelPrompt == 0) return;
            try
            {
                Type hud = typeof(Game).Assembly.GetType("RDR2.Native.HUD", false);
                if (hud == null) return;
                InvokeHud(hud, "_UI_PROMPT_SET_VISIBLE", _cancelPrompt, visible);
                InvokeHud(hud, "_UI_PROMPT_SET_ENABLED", _cancelPrompt, visible);
            }
            catch (Exception ex) { Log.Write("Mission cancellation prompt visibility warning: " + ex.Message); }
        }

        private void RemoveCancelPrompt()
        {
            SetCancelPromptVisible(false);
            if (_cancelPrompt != 0)
            {
                try
                {
                    Type hud = typeof(Game).Assembly.GetType("RDR2.Native.HUD", false);
                    if (hud != null) InvokeHud(hud, "_UI_PROMPT_DELETE", _cancelPrompt);
                }
                catch (Exception ex) { Log.Write("Mission cancellation prompt deletion warning: " + ex.Message); }
            }
            _cancelPrompt = 0;
            _cancelPromptGroup = 0;
            _cancelPromptArmed = true;
        }

        private static void InvokeHud(Type hud, string name, params object[] args)
        {
            MethodInfo method = hud.GetMethods(Flags).FirstOrDefault(m =>
            {
                if (m.Name != name || m.GetParameters().Length != args.Length) return false;
                ParameterInfo[] p = m.GetParameters();
                for (int i = 0; i < p.Length; i++)
                {
                    if (args[i] == null) continue;
                    Type argType = args[i].GetType();
                    if (!p[i].ParameterType.IsAssignableFrom(argType) && !(p[i].ParameterType.IsEnum && argType == typeof(int)))
                    {
                        try { Convert.ChangeType(args[i], p[i].ParameterType); }
                        catch { return false; }
                    }
                }
                return true;
            });
            if (method == null) return;
            object[] converted = args.ToArray();
            ParameterInfo[] parameters = method.GetParameters();
            for (int i = 0; i < converted.Length; i++)
                if (converted[i] != null && !parameters[i].ParameterType.IsInstanceOfType(converted[i]))
                    converted[i] = parameters[i].ParameterType.IsEnum
                        ? Enum.ToObject(parameters[i].ParameterType, converted[i])
                        : Convert.ChangeType(converted[i], parameters[i].ParameterType);
            method.Invoke(null, converted);
        }

        private void MaintainConvoyLootPrompt(Ped player)
        {
            _lootPromptJustCompleted = false;
            bool missionReady = _faction.MissionStage == 1 &&
                _faction.MissionType == MissionConvoy &&
                _faction.MissionObjectiveReady &&
                (_objectiveController.AllGuardsDefeated() || _objectiveController.EncounterSpawnFailed) &&
                !_objectiveController.IsWagonDestroyedOrMissing();

            if (!missionReady || player == null || !player.Exists() || player.IsDead ||
                player.IsOnMount || player.IsInVehicle)
            {
                SetLootPromptVisible(false);
                _lootPromptArmed = true;
                return;
            }

            Vehicle wagon = _objectiveController.CargoWagon;
            if (wagon == null || !wagon.Exists())
            {
                SetLootPromptVisible(false);
                _lootPromptArmed = true;
                return;
            }

            if (player.Position.DistanceTo(wagon.Position) > 7.5f)
            {
                SetLootPromptVisible(false);
                _lootPromptArmed = true;
                return;
            }

            int group;
            try
            {
                group = Function.Call<int>(PromptGetGroupForEntityHash, new InputArgument[] { wagon.Handle });
            }
            catch (Exception ex)
            {
                Log.Write("Cargo prompt group lookup warning: " + ex.Message);
                SetLootPromptVisible(false);
                return;
            }

            if (group == 0)
            {
                SetLootPromptVisible(false);
                return;
            }

            if (_lootPrompt == 0 || _lootPromptGroup != group ||
                _lootPromptEntityHandle != wagon.Handle || !PromptValid(_lootPrompt))
            {
                RemoveLootPrompt();
                if (!TryCreateLootPrompt(group, wagon.Handle))
                    return;
            }

            SetLootPromptVisible(true);
            ActivateLootPromptGroup(wagon.Handle, group);

            bool completed = PromptCompleted(_lootPrompt);
            if (completed && _lootPromptArmed)
            {
                _lootPromptJustCompleted = true;
                _lootPromptArmed = false;
                SetLootPromptVisible(false);
            }
            else if (!completed)
            {
                _lootPromptArmed = true;
            }
        }

        private bool TryCreateLootPrompt(int group, int entityHandle)
        {
            try
            {
                Type mainType = _bridge.MainInstance != null ? _bridge.MainInstance.GetType() : null;
                if (mainType == null) return false;
                MethodInfo make = mainType.GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "MakePrompt" && m.GetParameters().Length == 4);
                if (make == null)
                {
                    Log.Write("Cargo prompt creation failed: enRAGE MakePrompt helper not found.");
                    return false;
                }

                Type controlType = make.GetParameters()[0].ParameterType;
                object control = controlType.IsEnum
                    ? Enum.ToObject(controlType, ContextControl)
                    : (object)ContextControl;

                _lootPrompt = Convert.ToInt32(make.Invoke(null,
                    new object[] { control, "Saquear cargamento", true, group }));
                if (_lootPrompt == 0) return false;
                _lootPromptGroup = group;
                _lootPromptEntityHandle = entityHandle;
                _lootPromptArmed = true;
                Log.Write("Cargo interaction prompt created. Prompt=" + _lootPrompt +
                    ", group=" + group + ", wagon=" + entityHandle + ".");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Cargo prompt creation warning: " + ex.Message);
                RemoveLootPrompt();
                return false;
            }
        }

        private void ActivateLootPromptGroup(int entityHandle, int group)
        {
            try
            {
                Assembly assembly = _bridge.MainInstance != null
                    ? _bridge.MainInstance.GetType().Assembly
                    : AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetType("NemesisSystem.UiDraw", false) != null);
                Type uiDraw = assembly != null ? assembly.GetType("NemesisSystem.UiDraw", false) : null;
                MethodInfo forever = uiDraw != null ? uiDraw.GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "ForeverVarString" && m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(string)) : null;
                if (forever == null) return;

                ulong label = Convert.ToUInt64(forever.Invoke(null, new object[] { "Cargamento del convoy" }));
                Function.Call(SetAmbientPromptGroupThisFrameHash, new InputArgument[]
                {
                    entityHandle, 7.5f, 2, 0, group, new InputArgument(label), 0
                });
            }
            catch (Exception ex)
            {
                Log.Write("Cargo prompt activation warning: " + ex.Message);
            }
        }

        private void SetLootPromptVisible(bool visible)
        {
            if (_lootPrompt == 0) return;
            try
            {
                Type hud = typeof(Game).Assembly.GetType("RDR2.Native.HUD", false);
                if (hud == null) return;
                InvokeHud(hud, "_UI_PROMPT_SET_VISIBLE", _lootPrompt, visible);
                InvokeHud(hud, "_UI_PROMPT_SET_ENABLED", _lootPrompt, visible);
            }
            catch (Exception ex)
            {
                Log.Write("Cargo prompt visibility warning: " + ex.Message);
            }
        }

        private void RemoveLootPrompt()
        {
            SetLootPromptVisible(false);
            if (_lootPrompt != 0)
            {
                try
                {
                    Type hud = typeof(Game).Assembly.GetType("RDR2.Native.HUD", false);
                    if (hud != null) InvokeHud(hud, "_UI_PROMPT_DELETE", _lootPrompt);
                }
                catch (Exception ex)
                {
                    Log.Write("Cargo prompt delete warning: " + ex.Message);
                }
            }
            _lootPrompt = 0;
            _lootPromptGroup = 0;
            _lootPromptEntityHandle = 0;
            _lootPromptArmed = true;
            _lootPromptJustCompleted = false;
        }

        private void MaintainObjectivePrompt(Ped player)
        {
            _objectivePromptJustCompleted = false;
            int entityHandle = 0;
            string promptLabel = string.Empty;

            if (_faction.MissionStage == 1 && _faction.MissionObjectiveReady &&
                player != null && player.Exists() && !player.IsDead &&
                !player.IsOnMount && !player.IsInVehicle)
            {
                if (_faction.MissionType == MissionSupplyCache &&
                    _faction.MissionObjectiveIndex >= 2 &&
                    (_objectiveController.AllGuardsDefeated() || _objectiveController.EncounterSpawnFailed) &&
                    _supplyCache != null && _supplyCache.Exists() &&
                    player.Position.DistanceTo(_supplyCache.Position) <= 5f)
                {
                    entityHandle = _supplyCache.Handle;
                    promptLabel = "Recoger provisiones";
                }
                else if (_faction.MissionType == MissionRescuePrisoner &&
                    _faction.MissionObjectiveIndex >= 1)
                {
                    Ped prisoner = _objectiveController.CourierPed;
                    if (prisoner != null && prisoner.Exists() && !prisoner.IsDead &&
                        player.Position.DistanceTo(prisoner.Position) <= 5f)
                    {
                        entityHandle = prisoner.Handle;
                        promptLabel = "Liberar al explorador";
                    }
                }
                else if (_faction.MissionType == MissionCourier &&
                    _faction.MissionObjectiveIndex >= 1)
                {
                    Ped courier = _objectiveController.CourierPed;
                    if (courier != null && courier.Exists() &&
                        player.Position.DistanceTo(courier.Position) <= 5f)
                    {
                        entityHandle = courier.Handle;
                        promptLabel = _faction.MissionObjectiveIndex == 1
                            ? "Hablar con el contacto"
                            : "Entregar despacho";
                    }
                }
            }

            if (entityHandle == 0 || string.IsNullOrWhiteSpace(promptLabel))
            {
                SetObjectivePromptVisible(false);
                _objectivePromptArmed = true;
                return;
            }

            int group;
            try { group = Function.Call<int>(PromptGetGroupForEntityHash, new InputArgument[] { entityHandle }); }
            catch (Exception ex)
            {
                Log.Write("Objective prompt group lookup warning: " + ex.Message);
                SetObjectivePromptVisible(false);
                return;
            }
            if (group == 0)
            {
                SetObjectivePromptVisible(false);
                return;
            }

            if (_objectivePrompt == 0 || _objectivePromptGroup != group ||
                _objectivePromptEntityHandle != entityHandle ||
                !string.Equals(_objectivePromptLabel, promptLabel, StringComparison.Ordinal) ||
                !PromptValid(_objectivePrompt))
            {
                RemoveObjectivePrompt();
                if (!TryCreateObjectivePrompt(group, entityHandle, promptLabel)) return;
            }

            SetObjectivePromptVisible(true);
            ActivateEntityPromptGroup(entityHandle, group, "Objetivo de misión", 3.5f);
            bool completed = PromptCompleted(_objectivePrompt);
            if (completed && _objectivePromptArmed)
            {
                _objectivePromptJustCompleted = true;
                _objectivePromptArmed = false;
                SetObjectivePromptVisible(false);
            }
            else if (!completed)
            {
                _objectivePromptArmed = true;
            }
        }

        private bool TryCreateObjectivePrompt(int group, int entityHandle, string label)
        {
            try
            {
                Type mainType = _bridge.MainInstance != null ? _bridge.MainInstance.GetType() : null;
                if (mainType == null) return false;
                MethodInfo make = mainType.GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "MakePrompt" && m.GetParameters().Length == 4);
                if (make == null)
                {
                    Log.Write("Objective prompt creation failed: enRAGE MakePrompt helper not found.");
                    return false;
                }

                Type controlType = make.GetParameters()[0].ParameterType;
                object control = controlType.IsEnum
                    ? Enum.ToObject(controlType, ContextControl)
                    : (object)ContextControl;
                _objectivePrompt = Convert.ToInt32(make.Invoke(null,
                    new object[] { control, label, true, group }));
                if (_objectivePrompt == 0) return false;
                _objectivePromptGroup = group;
                _objectivePromptEntityHandle = entityHandle;
                _objectivePromptLabel = label;
                _objectivePromptArmed = true;
                Log.Write("Objective prompt created. Label=" + label + ", prompt=" +
                    _objectivePrompt + ", entity=" + entityHandle + ".");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Objective prompt creation warning: " + ex.Message);
                RemoveObjectivePrompt();
                return false;
            }
        }

        private void ActivateEntityPromptGroup(int entityHandle, int group, string groupName, float radius)
        {
            try
            {
                Assembly assembly = _bridge.MainInstance != null
                    ? _bridge.MainInstance.GetType().Assembly
                    : AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => a.GetType("NemesisSystem.UiDraw", false) != null);
                Type uiDraw = assembly != null ? assembly.GetType("NemesisSystem.UiDraw", false) : null;
                MethodInfo forever = uiDraw != null ? uiDraw.GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "ForeverVarString" && m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(string)) : null;
                if (forever == null) return;

                string safeGroupName = string.IsNullOrWhiteSpace(groupName) ? "Objetivo de misión" : groupName;
                ulong label = Convert.ToUInt64(forever.Invoke(null, new object[] { safeGroupName }));
                Function.Call(SetAmbientPromptGroupThisFrameHash, new InputArgument[]
                {
                    entityHandle, radius, 2, 0, group, new InputArgument(label), 0
                });
            }
            catch (Exception ex) { Log.Write("Objective prompt activation warning: " + ex.Message); }
        }

        private void SetObjectivePromptVisible(bool visible)
        {
            if (_objectivePrompt == 0) return;
            try
            {
                Type hud = typeof(Game).Assembly.GetType("RDR2.Native.HUD", false);
                if (hud == null) return;
                InvokeHud(hud, "_UI_PROMPT_SET_VISIBLE", _objectivePrompt, visible);
                InvokeHud(hud, "_UI_PROMPT_SET_ENABLED", _objectivePrompt, visible);
            }
            catch (Exception ex) { Log.Write("Objective prompt visibility warning: " + ex.Message); }
        }

        private void RemoveObjectivePrompt()
        {
            SetObjectivePromptVisible(false);
            if (_objectivePrompt != 0)
            {
                try
                {
                    Type hud = typeof(Game).Assembly.GetType("RDR2.Native.HUD", false);
                    if (hud != null) InvokeHud(hud, "_UI_PROMPT_DELETE", _objectivePrompt);
                }
                catch (Exception ex) { Log.Write("Objective prompt delete warning: " + ex.Message); }
            }
            _objectivePrompt = 0;
            _objectivePromptGroup = 0;
            _objectivePromptEntityHandle = 0;
            _objectivePromptLabel = string.Empty;
            _objectivePromptArmed = true;
            _objectivePromptJustCompleted = false;
        }

        private void OnInteract()
        {
            if (_faction.MissionStage > 0)
            {
                string status;
                if (_faction.MissionStage == 2)
                    status = "Vuelve al campamento para entregar el encargo.";
                else if (_faction.MissionStage == 3)
                    status = "La recompensa está pendiente; se entregará cuando los recursos de la facción estén disponibles.";
                else if (_faction.MissionObjectiveReady &&
                    (_faction.MissionType == MissionSupplyCache || _faction.MissionType == MissionCourier ||
                     _faction.MissionType == MissionRescuePrisoner))
                {
                    if (_faction.MissionType == MissionSupplyCache)
                        status = "El alijo está listo. Acércate al cajón para recoger las provisiones mediante su prompt contextual.";
                    else if (_faction.MissionType == MissionRescuePrisoner)
                        status = "Los captores han caído. Acércate al explorador y usa el prompt contextual para liberarlo.";
                    else
                        status = "Has llegado al contacto. Usa su prompt contextual para hablar y entregar el despacho.";
                }
                else if (_faction.MissionType == MissionConvoy)
                    status = _faction.MissionObjectiveReady
                        ? "El convoy está despejado. Acércate a la carreta para saquear el cargamento mediante su prompt contextual."
                        : "Intercepta el convoy y elimina a todos los escoltas antes de saquear el cargamento.";
                else if (_faction.MissionType == MissionStakeout)
                    status = "Vigila el cruce durante 24 segundos dentro del perímetro marcado. El reloj se pausa si te alejas; puede aparecer una partida.";
                else if (_faction.MissionType == MissionEliminateTargets)
                    status = "Llega al escondite y elimina a todos los enemigos.";
                else if (_faction.MissionType == MissionDestroyTarget)
                    status = "Neutraliza a los escoltas y destruye el carro de suministros.";
                else if (_faction.MissionType == MissionRescuePrisoner)
                    status = "Elimina a los captores y libera al explorador que mantienen retenido.";
                else if (_faction.MissionType == MissionHuntAnimal)
                    status = "Sigue las señales hasta la zona de caza y abate a los lobos.";
                else if (_faction.MissionType == MissionPatrol)
                    status = "Patrulla activa: visita el punto " + (_faction.MissionObjectiveIndex + 1) + " de " + PatrolWaypointCount + " y continúa con los siguientes.";
                else
                    status = "Inspecciona el punto marcado en el mapa y vuelve al campamento.";
                _story.PlayReminderCue();
                Notify("MISIÓN ACTIVA: " + status);
                return;
            }

            Vector3 centre = _bridge.GetPlayerCampCentre(_faction);
            if (centre == Vector3.Zero)
                centre = new Vector3(_faction.CampX, _faction.CampY, _faction.CampZ);
            if (centre == Vector3.Zero)
            {
                Notify("No se pudo localizar el centro del campamento para asignar una misión.");
                return;
            }

            // Weighted selection varies the contract and avoids repeating the previous completion.
            RemoveLootPrompt();
            RemoveObjectivePrompt();
            _objectiveController.CleanupConvoy();
            if (_faction.MissionCachePropHandle != 0) DeleteSupplyCacheProp();
            Random missionRandom = new Random(unchecked(Game.GameTime * 397 ^ _faction.MissionsCompleted * 7919 ^ _faction.MissionPrestige));
            _faction.MissionDifficulty = MissionCatalog.DetermineDifficulty(_faction.MissionsCompleted, missionRandom);
            _faction.MissionType = MissionCatalog.SelectNext(_faction.MissionsCompleted,
                _faction.LastMissionType, _faction.MissionDifficulty, missionRandom);
            _faction.MissionConvoyWagonHandle = 0;
            _faction.MissionConvoyEnemyHandles.Clear();
            _cacheSpawnFailures = 0;
            _nextCacheSpawnRetryAt = 0;
            _nextTargetGroundCheckAt = 0;
            _nextEscortReminderAt = 0;
            _faction.MissionEncounterSpawned = false;
            _faction.MissionEncounterProcessId = 0;
            _faction.MissionObjectiveIndex = 0;
            _faction.MissionObjectiveReady = false;
            _faction.MissionWaitProgressMs = 0;
            _missionTimerInitialized = false;
            _lastMissionProgressTickAt = 0;
            _nextMissionProgressSaveAt = 0;
            _nextStakeoutStatusAt = 0;
            SetMissionTarget(centre, 0);
            _faction.MissionStage = 1;
            SaveFaction();
            EnsureTargetBlip();

            _story.BeginMission(_faction.MissionType, _faction.MissionDifficulty, _ped, Game.Player.Character);
        }

        private unsafe void SetMissionTarget(Vector3 centre, int pointIndex)
        {
            double baseAngle = (_faction.MissionsCompleted + 1) * 2.399963229728653 +
                _faction.MissionType * 0.731;
            double angle = baseAngle;
            if (_faction.MissionType == MissionPatrol || _faction.MissionType == MissionReconnaissance)
                angle += pointIndex * (Math.PI * 2.0 / PatrolWaypointCount);
            else if (_faction.MissionType == MissionHuntAnimal)
                // Hunting clues form a compact arc around the search area before the pack spawns.
                angle += pointIndex * (Math.PI / 12.0);

            float radius = _faction.MissionType == MissionStakeout ? 235f :
                (_faction.MissionType == MissionPatrol ? 285f :
                (_faction.MissionType == MissionConvoy || _faction.MissionType == MissionDestroyTarget ? 315f :
                (_faction.MissionType == MissionHuntAnimal ? 300f :
                (_faction.MissionType == MissionRescuePrisoner ? 225f : 260f))));
            radius += ((_faction.MissionsCompleted + pointIndex) % 3) * 18f;
            radius += Math.Max(0, Math.Min(2, _faction.MissionDifficulty)) * 22f;

            Vector3 target = new Vector3(
                centre.X + (float)Math.Cos(angle) * radius,
                centre.Y + (float)Math.Sin(angle) * radius,
                centre.Z + 0.35f);

            // Try several nearby bearings and radii instead of blindly accepting one arbitrary point.
            // Only accept a candidate for which the ground native returns a plausible height.
            bool foundGround = false;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double candidateAngle = angle + attempt * (Math.PI / 4.0);
                float candidateRadius = radius + (attempt % 3) * 14f;
                Vector3 candidate = new Vector3(
                    centre.X + (float)Math.Cos(candidateAngle) * candidateRadius,
                    centre.Y + (float)Math.Sin(candidateAngle) * candidateRadius,
                    centre.Z + 0.35f);
                STREAMING.REQUEST_COLLISION_AT_COORD(candidate.X, candidate.Y, candidate.Z);
                float groundZ = 0f;
                try
                {
                    if (MISC.GET_GROUND_Z_FOR_3D_COORD(candidate.X, candidate.Y, centre.Z + 50f, &groundZ, false) &&
                        Math.Abs(groundZ - centre.Z) <= 55f)
                    {
                        candidate.Z = groundZ + 0.25f;
                        target = candidate;
                        foundGround = true;
                        break;
                    }
                }
                catch { }
            }
            _faction.MissionTargetHeading = 0f;
            _faction.MissionTargetHasRoadNode = false;
            bool roadMission = _faction.MissionType == MissionReconnaissance ||
                _faction.MissionType == MissionPatrol || _faction.MissionType == MissionStakeout ||
                _faction.MissionType == MissionCourier || _faction.MissionType == MissionConvoy ||
                _faction.MissionType == MissionDestroyTarget;
            if (roadMission)
            {
                try
                {
                    Vector3 roadNode = Vector3.Zero;
                    float roadHeading = 0f;
                    // Project route-based missions onto a nearby usable vehicle/path node where possible.
                    bool hasRoadNode = PATHFIND.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING(
                        target.X, target.Y, target.Z, &roadNode, &roadHeading, 1, 3f, 0);
                    if (hasRoadNode && roadNode != Vector3.Zero)
                    {
                        float dx = roadNode.X - target.X;
                        float dy = roadNode.Y - target.Y;
                        float horizontalDistance = (float)Math.Sqrt(dx * dx + dy * dy);
                        float maxVerticalDifference = foundGround ? 22f : 45f;
                        if (horizontalDistance <= 75f && Math.Abs(roadNode.Z - target.Z) <= maxVerticalDifference)
                        {
                            target = new Vector3(roadNode.X, roadNode.Y, roadNode.Z + 0.25f);
                            _faction.MissionTargetHeading = roadHeading;
                            _faction.MissionTargetHasRoadNode = true;
                            foundGround = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Mission road-node projection warning; retaining grounded candidate: " + ex.Message);
                }
            }

            if (!foundGround)
                STREAMING.REQUEST_COLLISION_AT_COORD(target.X, target.Y, target.Z);

            _faction.MissionTargetX = target.X;
            _faction.MissionTargetY = target.Y;
            _faction.MissionTargetZ = target.Z;
        }

        private static string GetMissionTitle(int missionType)
        {
            return MissionCatalog.Get(missionType).Title;
        }

        private static string GetMissionStartInstruction(int missionType)
        {
            return MissionCatalog.Get(missionType).StartInstruction;
        }

        private static string GetMissionObjectiveBlipName(PlayerFactionData faction)
        {
            if (faction == null) return "Objetivo de misión";
            switch (faction.MissionType)
            {
                case MissionPatrol: return "Patrulla: punto " + (faction.MissionObjectiveIndex + 1) + "/" + PatrolWaypointCount;
                case MissionReconnaissance: return "Reconocimiento: punto " + (faction.MissionObjectiveIndex + 1) + "/2";
                case MissionSupplyCache: return "Alijo de provisiones";
                case MissionStakeout: return "Cruce de vigilancia";
                case MissionCourier: return "Punto de entrega";
                case MissionConvoy: return "Convoy enemigo";
                case MissionEliminateTargets: return "Escondite enemigo";
                case MissionDestroyTarget: return "Carro de suministros";
                case MissionRescuePrisoner: return "Explorador retenido";
                case MissionHuntAnimal: return faction.MissionObjectiveIndex < 2
                    ? "Rastros de lobos: señal " + (faction.MissionObjectiveIndex + 1) + "/2"
                    : "Manada de lobos";
                default: return "Reconocimiento de ruta";
            }
        }

        private void GetMissionReward(int missionType, out int supplies, out int treasury, out int prestige)
        {
            MissionCatalog.GetRewards(missionType, _faction.MissionDifficulty, out supplies, out treasury, out prestige);
        }

        private string BuildMissionRewardId()
        {
            // Stable for this active mission even if the script reloads between payout and cleanup.
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2:R}:{3:R}:{4:R}",
                _faction.MissionsCompleted + 1, _faction.MissionType,
                _faction.MissionTargetX, _faction.MissionTargetY, _faction.MissionTargetZ);
        }

        private void MaintainSupplyCacheProp(Ped player)
        {
            Vector3 target = new Vector3(_faction.MissionTargetX, _faction.MissionTargetY, _faction.MissionTargetZ);
            if (player == null || !player.Exists() || player.Position.DistanceTo(target) > 110f)
                return;

            if (_supplyCache != null && _supplyCache.Exists())
                return;

            uint expectedModel = MISC.GET_HASH_KEY(SupplyCacheModel);
            if (_faction.MissionCachePropHandle != 0)
            {
                try
                {
                    Prop candidate = new Prop(_faction.MissionCachePropHandle);
                    bool valid = candidate.Exists() &&
                        unchecked((uint)candidate.Model.Hash) == expectedModel &&
                        candidate.Position.DistanceTo(target) <= 12f;
                    if (valid)
                    {
                        _supplyCache = candidate;
                        return;
                    }
                }
                catch { }

                _faction.MissionCachePropHandle = 0;
                SaveFaction();
            }

            if (Game.GameTime < _nextCacheSpawnRetryAt)
                return;
            _nextCacheSpawnRetryAt = Game.GameTime + 5000;
            _cacheSpawnFailures++;

            try
            {
                Model model = new Model(SupplyCacheModel);
                if (!model.IsValid)
                {
                    Log.Write("Supply-cache prop model is invalid: " + SupplyCacheModel + ".");
                    return;
                }
                if (!model.IsLoaded && !model.Request(1000))
                {
                    Log.Write("Supply-cache prop model did not load: " + SupplyCacheModel + ".");
                    return;
                }

                STREAMING.REQUEST_COLLISION_AT_COORD(target.X, target.Y, target.Z);
                Prop created = World.CreateProp(model, target, false, true);
                if (created == null || !created.Exists())
                {
                    Log.Write("Supply-cache prop could not be created at " + target + ".");
                    return;
                }

                _supplyCache = created;
                _cacheSpawnFailures = 0;
                _faction.MissionCachePropHandle = created.Handle;
                SaveFaction();
                Log.Write("Independent mission supply cache created. Handle=" + created.Handle + ", position=" + target + ".");
            }
            catch (Exception ex)
            {
                Log.Write("Supply-cache prop spawn warning: " + ex.Message);
            }
        }

        private void DeleteSupplyCacheProp()
        {
            int handle = _faction.MissionCachePropHandle;
            Prop candidate = _supplyCache;
            if ((candidate == null || !candidate.Exists()) && handle != 0)
            {
                try { candidate = new Prop(handle); }
                catch { candidate = null; }
            }

            if (candidate != null && candidate.Exists())
            {
                try
                {
                    uint expectedModel = MISC.GET_HASH_KEY(SupplyCacheModel);
                    Vector3 target = new Vector3(_faction.MissionTargetX, _faction.MissionTargetY, _faction.MissionTargetZ);
                    bool belongsToMission = target != Vector3.Zero &&
                        unchecked((uint)candidate.Model.Hash) == expectedModel &&
                        candidate.Position.DistanceTo(target) <= 12f;
                    if (belongsToMission) candidate.Delete();
                }
                catch (Exception ex) { Log.Write("Supply-cache prop cleanup warning: " + ex.Message); }
            }

            _supplyCache = null;
            _faction.MissionCachePropHandle = 0;
        }

        private void UpdateMissionProgress(Ped player, Vector3 campCentre)
        {
            if (_faction.MissionStage == 1)
            {
                Vector3 target = new Vector3(_faction.MissionTargetX, _faction.MissionTargetY, _faction.MissionTargetZ);
                float distance = player.Position.DistanceTo(target);

                if (_faction.MissionType == MissionSupplyCache &&
                    _cacheSpawnFailures >= 3 && (_supplyCache == null || !_supplyCache.Exists()))
                {
                    FailActiveMission("No se pudo colocar el cajón de provisiones después de varios intentos. El encargo queda cancelado.");
                    return;
                }
                if ((_faction.MissionType == MissionCourier || _faction.MissionType == MissionRescuePrisoner) &&
                    _objectiveController.CourierSpawnFailed && !_objectiveController.CourierAvailable)
                {
                    FailActiveMission(_faction.MissionType == MissionRescuePrisoner
                        ? "No se pudo localizar al explorador retenido después de varios intentos. El encargo queda cancelado."
                        : "El contacto no pudo aparecer en el punto de entrega. El encargo queda cancelado.");
                    return;
                }
                if ((_faction.MissionType == MissionConvoy || _faction.MissionType == MissionDestroyTarget) &&
                    _objectiveController.TargetVehicleSpawnFailed && !_objectiveController.TargetVehicleAvailable)
                {
                    FailActiveMission(_faction.MissionType == MissionConvoy
                        ? "No se pudo colocar la carreta del convoy. El encargo queda cancelado sin recompensa."
                        : "No se pudo colocar el carro que había que sabotear. El encargo queda cancelado sin recompensa.");
                    return;
                }
                bool requiresEnemyGroup = _faction.MissionType == MissionSupplyCache ||
                    _faction.MissionType == MissionConvoy || _faction.MissionType == MissionEliminateTargets ||
                    _faction.MissionType == MissionDestroyTarget || _faction.MissionType == MissionRescuePrisoner ||
                    _faction.MissionType == MissionHuntAnimal;
                if (requiresEnemyGroup && _objectiveController.EncounterSpawnFailed)
                {
                    FailActiveMission("No se pudo generar un grupo enemigo válido después de varios intentos. El encargo queda cancelado sin recompensa.");
                    return;
                }

                if (_faction.MissionType == MissionPatrol)
                {
                    if (distance <= 24f)
                    {
                        if (_faction.MissionObjectiveIndex + 1 < PatrolWaypointCount)
                        {
                            _faction.MissionObjectiveIndex++;
                            SetMissionTarget(campCentre, _faction.MissionObjectiveIndex);
                            SaveFaction();
                            RemoveTargetBlip();
                            EnsureTargetBlip();
                            _story.OnCheckpoint(_faction.MissionType, _faction.MissionObjectiveIndex);
                        }
                        else
                        {
                            AdvanceMissionToReturn("Has completado la patrulla de frontera.");
                        }
                    }
                }
                else if (_faction.MissionType == MissionConvoy)
                {
                    if (distance <= 65f && _objectiveController.EncounterSpawned && _faction.MissionObjectiveIndex == 0)
                    {
                        _faction.MissionObjectiveIndex = 1;
                        SaveFaction();
                        _story.OnEncounterStarted(_faction.MissionType);
                    }

                    // Requiring proximity prevents a distant firefight from silently advancing the contract.
                    if (distance <= 65f && (_objectiveController.AllGuardsDefeated() || _objectiveController.EncounterSpawnFailed))
                    {
                        if (_objectiveController.IsWagonDestroyedOrMissing())
                        {
                            AdvanceMissionToReturn(_objectiveController.EncounterHadLostTargets
                                ? "La carreta del convoy ha quedado fuera de servicio. Algunos escoltas se perdieron de vista; regresa al campamento."
                                : "Los escoltas han sido neutralizados y la carreta está destruida o no se encuentra. El convoy queda fuera de servicio; regresa al campamento.");
                        }
                        else if (!_faction.MissionObjectiveReady)
                        {
                            _faction.MissionObjectiveReady = true;
                            SaveFaction();
                            _story.OnLootAvailable(_faction.MissionType);
                        }
                        else if (_objectiveController.IsPlayerNearLoot(player) && _lootPromptJustCompleted)
                        {
                            _story.OnObjectiveInteraction(player, "REVISIÓN: Buscas entre los bultos y rescatas lo que todavía puede servir.");
                            AdvanceMissionToReturn("Has saqueado el convoy y recuperado sus suministros.");
                        }
                    }
                }
                else if (_faction.MissionType == MissionSupplyCache)
                {
                    if (_objectiveController.EncounterSpawned && distance <= 45f && _faction.MissionObjectiveIndex == 0)
                    {
                        _faction.MissionObjectiveIndex = 1;
                        SaveFaction();
                        _story.OnEncounterStarted(_faction.MissionType);
                    }

                    if (distance <= 5f)
                    {
                        bool guardsClear = _objectiveController.AllGuardsDefeated() || _objectiveController.EncounterSpawnFailed;
                        if (!guardsClear)
                        {
                            if (_faction.MissionObjectiveIndex == 0)
                            {
                                _faction.MissionObjectiveIndex = 1;
                                SaveFaction();
                                _story.OnEncounterStarted(_faction.MissionType);
                            }
                        }
                        else if (_faction.MissionObjectiveIndex < 2)
                        {
                            _faction.MissionObjectiveIndex = 2;
                            _faction.MissionObjectiveReady = true;
                            SaveFaction();
                            _story.OnLootAvailable(_faction.MissionType);
                        }
                        else if (_objectivePromptJustCompleted)
                        {
                            _story.OnObjectiveInteraction(player, "RECUPERACIÓN: Revisas las cajas y preparas las provisiones para el regreso.");
                            AdvanceMissionToReturn("Has recuperado las provisiones del alijo.");
                        }
                    }
                }
                else if (_faction.MissionType == MissionCourier)
                {
                    if (distance <= 5f)
                    {
                        if (!_faction.MissionObjectiveReady)
                        {
                            _faction.MissionObjectiveReady = true;
                            _faction.MissionObjectiveIndex = 1;
                            SaveFaction();
                            _story.OnCheckpoint(_faction.MissionType, 1);
                        }
                        else if (_objectivePromptJustCompleted && _faction.MissionObjectiveIndex == 1)
                        {
                            _faction.MissionObjectiveIndex = 2;
                            SaveFaction();
                            _story.OnCourierConversation(_objectiveController.CourierPed, player, false);
                        }
                        else if (_objectivePromptJustCompleted && _faction.MissionObjectiveIndex >= 2)
                        {
                            _story.OnCourierConversation(_objectiveController.CourierPed, player, true);
                            _story.OnObjectiveInteraction(player, "ENTREGA: Le pasas el despacho al contacto y esperas a que lo guarde.");
                            AdvanceMissionToReturn("Has entregado el despacho al contacto.");
                        }
                    }
                }
                else if (_faction.MissionType == MissionEliminateTargets)
                {
                    if (distance <= 65f && _objectiveController.EncounterSpawned &&
                        _faction.MissionObjectiveIndex == 0)
                    {
                        _faction.MissionObjectiveIndex = 1;
                        SaveFaction();
                        _story.OnEncounterStarted(_faction.MissionType);
                    }
                    if (distance <= 65f && _objectiveController.AllGuardsDefeated())
                        AdvanceMissionToReturn(_objectiveController.EncounterHadLostTargets
                            ? "Has despejado el escondite; algunos de sus ocupantes desaparecieron de la zona."
                            : "Has despejado el escondite y eliminado a los hombres que lo ocupaban.");
                    else if (distance <= 65f && _objectiveController.EncounterSpawnFailed &&
                        !_objectiveController.EncounterSpawned)
                        FailActiveMission("No se pudo establecer contacto con los ocupantes del escondite. El encargo queda cancelado.");
                }
                else if (_faction.MissionType == MissionDestroyTarget)
                {
                    if (distance <= 70f && _objectiveController.EncounterSpawned &&
                        _faction.MissionObjectiveIndex == 0)
                    {
                        _faction.MissionObjectiveIndex = 1;
                        SaveFaction();
                        _story.OnEncounterStarted(_faction.MissionType);
                    }

                    bool guardsClear = _objectiveController.AllGuardsDefeated() ||
                        _objectiveController.EncounterSpawnFailed;
                    if (distance <= 70f && guardsClear &&
                        _objectiveController.EncounterSpawnFailed && !_objectiveController.EncounterSpawned &&
                        _objectiveController.TargetVehicleSpawnFailed)
                    {
                        FailActiveMission("No apareció el carro ni se pudo localizar a sus escoltas. El encargo queda cancelado.");
                    }
                    else if (distance <= 70f && guardsClear)
                    {
                        if (_faction.MissionConvoyWagonHandle != 0 && _objectiveController.IsWagonDestroyedOrMissing())
                            AdvanceMissionToReturn(_objectiveController.EncounterHadLostTargets
                                ? "El carro de suministros está fuera de servicio. Algunos escoltas se perdieron de vista."
                                : "Los escoltas han caído y el carro de suministros ha quedado destruido o ha desaparecido.");
                        else if (_objectiveController.TargetVehicleSpawnFailed)
                            AdvanceMissionToReturn("El carro no pudo llegar a la zona. Has neutralizado a sus escoltas y evitado que entreguen los suministros.");
                        else if (_objectiveController.TargetVehicleAvailable && !_faction.MissionObjectiveReady)
                        {
                            _faction.MissionObjectiveReady = true;
                            SaveFaction();
                            _story.ShowMissionStatus("El carro está expuesto. Destrúyelo para completar el sabotaje.", 3000);
                        }
                    }
                }
                else if (_faction.MissionType == MissionRescuePrisoner)
                {
                    if (distance <= 65f && _objectiveController.EncounterSpawned &&
                        _faction.MissionObjectiveIndex == 0)
                    {
                        _faction.MissionObjectiveIndex = 1;
                        SaveFaction();
                        _story.OnEncounterStarted(_faction.MissionType);
                    }

                    if (_objectiveController.CourierSpawnFailed && !_objectiveController.CourierAvailable)
                    {
                        FailActiveMission("El explorador no pudo aparecer en el punto de rescate. El encargo queda cancelado.");
                    }
                    else if (distance <= 65f &&
                        (_objectiveController.AllGuardsDefeated() || _objectiveController.EncounterSpawnFailed))
                    {
                        Ped prisoner = _objectiveController.CourierPed;
                        if (prisoner != null && prisoner.Exists() && prisoner.IsDead)
                        {
                            FailActiveMission("El explorador no sobrevivió al enfrentamiento. El encargo queda cancelado.");
                        }
                        else if (prisoner != null && prisoner.Exists() && !_faction.MissionObjectiveReady)
                        {
                            _faction.MissionObjectiveReady = true;
                            _faction.MissionObjectiveIndex = 1;
                            SaveFaction();
                            _story.OnCheckpoint(_faction.MissionType, 1);
                        }
                        else if (_objectivePromptJustCompleted && prisoner != null && prisoner.Exists())
                        {
                            _story.OnObjectiveInteraction(player, "RESCATE: Cortas las ataduras del explorador y le indicas la ruta de vuelta.");
                            AdvanceMissionToReturn("Has liberado al explorador y asegurado su regreso.");
                        }
                    }
                }
                else if (_faction.MissionType == MissionHuntAnimal)
                {
                    if (_faction.MissionObjectiveIndex < 2 && distance <= 24f)
                    {
                        _faction.MissionObjectiveIndex++;
                        SetMissionTarget(campCentre, _faction.MissionObjectiveIndex);
                        SaveFaction();
                        RemoveTargetBlip();
                        EnsureTargetBlip();
                        _story.OnCheckpoint(_faction.MissionType, _faction.MissionObjectiveIndex);
                    }
                    else if (_faction.MissionObjectiveIndex == 2 && distance <= 65f &&
                        _objectiveController.EncounterSpawned)
                    {
                        _faction.MissionObjectiveIndex = 3;
                        SaveFaction();
                        _story.OnEncounterStarted(_faction.MissionType);
                    }

                    if (_faction.MissionObjectiveIndex >= 2 && distance <= 70f &&
                        _objectiveController.AllGuardsDefeated())
                    {
                        string result = _objectiveController.EncounterHadLostTargets
                            ? "Has seguido los rastros y despejado la zona; algunos lobos se perdieron de vista."
                            : "Has seguido los rastros, abatido a los lobos y despejado la zona de caza.";
                        AdvanceMissionToReturn(result);
                    }
                    else if (_faction.MissionObjectiveIndex >= 2 && distance <= 70f &&
                        _objectiveController.EncounterSpawnFailed && !_objectiveController.EncounterSpawned)
                    {
                        FailActiveMission("No se pudo localizar a la manada después de varios intentos. El encargo queda cancelado.");
                    }
                }
                else if (_faction.MissionType == MissionStakeout)
                {
                    int now = Game.GameTime;
                    bool insidePerimeter = distance <= StakeoutRadius;

                    // Credit elapsed time while inside. The former < 1000 ms gate discarded progress
                    // during script stalls and streaming hitches, causing the objective to stall.
                    if (!_missionTimerInitialized)
                    {
                        _lastMissionProgressTickAt = now;
                        _missionTimerInitialized = true;
                    }
                    else
                    {
                        int elapsed = now - _lastMissionProgressTickAt;
                        if (insidePerimeter && elapsed > 0)
                        {
                            int creditedElapsed = Math.Min(elapsed, 2000);
                            _faction.MissionWaitProgressMs = Math.Min(
                                StakeoutDurationMs, _faction.MissionWaitProgressMs + creditedElapsed);
                        }
                        _lastMissionProgressTickAt = now;
                    }

                    if (insidePerimeter && !_faction.MissionObjectiveReady)
                    {
                        _faction.MissionObjectiveReady = true;
                        _nextStakeoutStatusAt = now + 1000;
                        SaveFaction();
                        _story.OnCheckpoint(_faction.MissionType, 0);
                    }

                    if (_faction.MissionObjectiveReady && now >= _nextStakeoutStatusAt)
                    {
                        _nextStakeoutStatusAt = now + 5000;
                        if (!insidePerimeter && _faction.MissionWaitProgressMs < StakeoutDurationMs)
                        {
                            _story.ShowMissionStatus(
                                "VIGILANCIA: fuera del perímetro; el reloj está detenido. Regresa al punto marcado.", 2300);
                        }
                        else if (_faction.MissionWaitProgressMs >= StakeoutDurationMs &&
                                 _objectiveController.EncounterSpawned && !_objectiveController.AllGuardsDefeated())
                        {
                            _story.ShowMissionStatus(
                                "VIGILANCIA: el tiempo terminó. Neutraliza a los atacantes para asegurar el cruce.", 2300);
                        }
                        else if (_faction.MissionWaitProgressMs < StakeoutDurationMs)
                        {
                            int secondsRemaining = (StakeoutDurationMs - _faction.MissionWaitProgressMs + 999) / 1000;
                            _story.ShowMissionStatus(
                                "VIGILANCIA: quedan " + secondsRemaining +
                                " segundos. Mantente dentro del perímetro marcado.", 2200);
                        }
                    }

                    if (now >= _nextMissionProgressSaveAt)
                    {
                        _nextMissionProgressSaveAt = now + 3000;
                        SaveFaction();
                    }

                    if (_faction.MissionWaitProgressMs >= 8000 && _faction.MissionObjectiveIndex == 0 &&
                        (_objectiveController.EncounterSpawned || _objectiveController.EncounterSpawnFailed))
                    {
                        _faction.MissionObjectiveIndex = 1;
                        SaveFaction();
                        if (_objectiveController.EncounterSpawned)
                            _story.OnEncounterStarted(_faction.MissionType);
                        else
                            _story.ShowMissionStatus(
                                "VIGILANCIA: no se ha acercado ninguna partida. Termina de observar el cruce.", 2400);
                    }

                    // Complete after 18 seconds if no encounter could be created, or after
                    // the spawned attackers have been eliminated.
                    if (_faction.MissionWaitProgressMs >= StakeoutDurationMs &&
                        (_objectiveController.EncounterSpawnFailed ||
                         (_objectiveController.EncounterSpawned && _objectiveController.AllGuardsDefeated())))
                        AdvanceMissionToReturn(
                            "Has vigilado el cruce durante " + (StakeoutDurationMs / 1000) +
                            " segundos y confirmado quién utiliza la ruta.");
                }
                
                else if (_faction.MissionType == MissionReconnaissance && distance <= 24f)
                {
                    if (_faction.MissionObjectiveIndex == 0)
                    {
                        _faction.MissionObjectiveIndex = 1;
                        SetMissionTarget(campCentre, _faction.MissionObjectiveIndex);
                        SaveFaction();
                        RemoveTargetBlip();
                        EnsureTargetBlip();
                        _story.OnCheckpoint(_faction.MissionType, _faction.MissionObjectiveIndex);
                    }
                    else
                    {
                        _story.OnCheckpoint(_faction.MissionType, 2);
                        AdvanceMissionToReturn("Has inspeccionado los dos puntos y reconstruido el rastro.");
                    }
                }
                else if (distance <= 24f)
                {
                    AdvanceMissionToReturn("Punto de reconocimiento inspeccionado.");
                }
            }

            if (_faction.MissionStage == 2 && player.Position.DistanceTo(campCentre) <= 14f)
            {
                if (_faction.MissionType == MissionRescuePrisoner)
                {
                    Ped prisoner = _objectiveController.RescuedPrisoner;
                    if (prisoner == null || !prisoner.Exists() || prisoner.IsDead)
                    {
                        FailActiveMission("El explorador se perdió o murió durante el regreso. El encargo queda cancelado sin recompensa.");
                        return;
                    }
                    if (!_objectiveController.RescuedPrisonerReachedCamp(campCentre))
                    {
                        if (Game.GameTime >= _nextEscortReminderAt)
                        {
                            _nextEscortReminderAt = Game.GameTime + 6000;
                            _story.ShowMissionStatus("El explorador todavía te sigue. Espera a que llegue al campamento para cerrar el rescate.", 3200);
                        }
                    }
                    else if (_objectiveController.SettleRescuedPrisonerAtCamp(campCentre))
                    {
                        _faction.MissionStage = 3;
                        SaveFaction();
                    }
                }
                else
                {
                    _faction.MissionStage = 3; // Persist a claim state before paying to avoid repeated per-tick rewards.
                    SaveFaction();
                }
            }

            if (_faction.MissionStage == 3 && Game.GameTime >= _nextRewardRetryAt)
                CompleteMission();
        }

        private void FailActiveMission(string reason)
        {
            RemoveObjectivePrompt();
            RemoveLootPrompt();
            _objectiveController.CleanupConvoy();
            DeleteSupplyCacheProp();
            _faction.MissionStage = 0;
            _faction.MissionType = MissionReconnaissance;
            _faction.MissionObjectiveIndex = 0;
            _faction.MissionObjectiveReady = false;
            _faction.MissionWaitProgressMs = 0;
            _faction.MissionTargetX = 0f;
            _faction.MissionTargetY = 0f;
            _faction.MissionTargetZ = 0f;
            _missionTimerInitialized = false;
            _lastMissionProgressTickAt = 0;
            _cacheSpawnFailures = 0;
            SaveFaction();
            RemoveTargetBlip();
            _story.ShowMissionStatus("ENCARGO FALLIDO: " + reason, 4500);
            Log.Write("Mission failed without rewards: " + reason);
        }

        private void AdvanceMissionToReturn(string message)
        {
            RemoveObjectivePrompt();
            if (_faction.MissionType == MissionSupplyCache)
                DeleteSupplyCacheProp();
            bool preserveRescuedPrisoner = _faction.MissionType == MissionRescuePrisoner;
            if (preserveRescuedPrisoner &&
                !_objectiveController.ReleaseRescuedPrisoner(Game.Player != null ? Game.Player.Character : null))
            {
                FailActiveMission("No se pudo liberar al explorador de forma segura. El encargo queda cancelado sin recompensa.");
                return;
            }
            _story.OnMissionReturning(_faction.MissionType, message);
            _objectiveController.CleanupConvoy(preserveRescuedPrisoner);
            _faction.MissionStage = 2;
            _faction.MissionObjectiveReady = false;
            _faction.MissionWaitProgressMs = 0;
            _missionTimerInitialized = false;
            _lastMissionProgressTickAt = 0;
            _nextStakeoutStatusAt = 0;
            SaveFaction();
            RemoveTargetBlip();
            EnsureTargetBlip();
            // The return explanation is queued as a subtitle dialogue by MissionStoryDirector.
        }

        private void CompleteMission()
        {
            _nextRewardRetryAt = Game.GameTime + 5000;
            int suppliesReward, treasuryReward, prestigeReward;
            GetMissionReward(_faction.MissionType, out suppliesReward, out treasuryReward, out prestigeReward);

            string error;
            string rewardId = BuildMissionRewardId();
            if (!_bridge.AwardMissionResources(_faction, suppliesReward, treasuryReward, rewardId, out error))
            {
                Log.Write("Mission reward pending; native faction resources unavailable: " + error);
                return;
            }

            string completedName = GetMissionTitle(_faction.MissionType);
            RemoveObjectivePrompt();
            if (_faction.MissionType == MissionSupplyCache)
                DeleteSupplyCacheProp();
            if (_faction.MissionType == MissionConvoy)
                _objectiveController.CleanupConvoy();
            _faction.LastMissionType = _faction.MissionType;
            _faction.MissionPrestige = Math.Min(100, _faction.MissionPrestige + prestigeReward);
            _faction.MissionsCompleted++;
            _faction.MissionStage = 0;
            _faction.MissionType = MissionReconnaissance;
            _faction.MissionObjectiveIndex = 0;
            _faction.MissionObjectiveReady = false;
            _faction.MissionWaitProgressMs = 0;
            _faction.MissionTargetX = 0f;
            _faction.MissionTargetY = 0f;
            _faction.MissionTargetZ = 0f;
            _cacheSpawnFailures = 0;
            SaveFaction();
            RemoveTargetBlip();
            string completionSummary = "El trabajo está hecho. La banda recibe " + suppliesReward +
                " suministros, $" + treasuryReward + " para la tesorería y " + prestigeReward + " de prestigio.";
            _story.OnMissionCompleted(_faction.LastMissionType, completionSummary);
            Log.Write("Independent mission completed: " + completedName + ". Count=" + _faction.MissionsCompleted +
                ", mission prestige=" + _faction.MissionPrestige + ", resources=+" + suppliesReward +
                " supplies/+$" + treasuryReward + ".");
        }

        private void EnsureTargetBlip()
        {
            if (_faction.MissionStage <= 0) return;
            if (_targetBlip != 0 && MAP.DOES_BLIP_EXIST(_targetBlip))
            {
                EnsureStakeoutRadiusBlip();
                return;
            }
            RemoveTargetBlip();

            Vector3 campCentre = _bridge.GetPlayerCampCentre(_faction);
            if (campCentre == Vector3.Zero)
                campCentre = new Vector3(_faction.CampX, _faction.CampY, _faction.CampZ);
            Vector3 target = _faction.MissionStage == 1
                ? new Vector3(_faction.MissionTargetX, _faction.MissionTargetY, _faction.MissionTargetZ)
                : campCentre;
            try
            {
                _targetBlip = MAP.BLIP_ADD_FOR_COORDS(NativeMapBlipStyle, target.X, target.Y, target.Z);
                if (_targetBlip != 0)
                {
                    MAP.SET_BLIP_SPRITE(_targetBlip, MISC.GET_HASH_KEY(TargetSprite), true);
                    SetBlipName(_targetBlip, _faction.MissionStage == 1
                        ? GetMissionObjectiveBlipName(_faction)
                        : "Volver al campamento");
                }
            }
            catch (Exception ex) { Log.Write("Mission objective blip creation failed: " + ex.Message); }
            EnsureStakeoutRadiusBlip();
        }

        private void EnsureStakeoutRadiusBlip()
        {
            if (_faction.MissionStage != 1 || _faction.MissionType != MissionStakeout)
            {
                RemoveStakeoutRadiusBlip();
                return;
            }
            if (_stakeoutRadiusBlip != 0 && MAP.DOES_BLIP_EXIST(_stakeoutRadiusBlip)) return;
            Vector3 target = new Vector3(_faction.MissionTargetX, _faction.MissionTargetY, _faction.MissionTargetZ);
            try
            {
                // Native BLIP_ADD_FOR_RADIUS draws the actual timed-hold perimeter on the map.
                _stakeoutRadiusBlip = MAP.BLIP_ADD_FOR_RADIUS(NativeMapBlipStyle,
                    target.X, target.Y, target.Z, StakeoutRadius);
                if (_stakeoutRadiusBlip == 0 || !MAP.DOES_BLIP_EXIST(_stakeoutRadiusBlip))
                    _stakeoutRadiusBlip = 0;
            }
            catch (Exception ex)
            {
                _stakeoutRadiusBlip = 0;
                Log.Write("Stakeout perimeter blip creation warning: " + ex.Message);
            }
        }

        private unsafe void RemoveStakeoutRadiusBlip()
        {
            int handle = _stakeoutRadiusBlip;
            _stakeoutRadiusBlip = 0;
            if (handle == 0) return;
            try
            {
                if (MAP.DOES_BLIP_EXIST(handle)) MAP.REMOVE_BLIP(&handle);
            }
            catch (Exception ex) { Log.Write("Stakeout perimeter blip cleanup warning: " + ex.Message); }
        }

        private void SetBlipName(int handle, string name)
        {
            try
            {
                Assembly assembly = _bridge.MainInstance != null
                    ? _bridge.MainInstance.GetType().Assembly
                    : AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetType("NemesisSystem.UiDraw", false) != null);
                Type uiDraw = assembly != null ? assembly.GetType("NemesisSystem.UiDraw", false) : null;
                MethodInfo forever = uiDraw != null ? uiDraw.GetMethods(Flags).FirstOrDefault(m =>
                    m.Name == "ForeverVarString" && m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(string)) : null;
                if (forever == null) return;
                ulong label = Convert.ToUInt64(forever.Invoke(null, new object[] { name }));
                Function.Call(SetBlipNameHash, new InputArgument[] { handle, new InputArgument(label) });
            }
            catch (Exception ex) { Log.Write("Mission objective blip label warning: " + ex.Message); }
        }

        private unsafe void RemoveTargetBlip()
        {
            RemoveStakeoutRadiusBlip();
            if (_targetBlip == 0) return;
            try
            {
                if (MAP.DOES_BLIP_EXIST(_targetBlip))
                {
                    int handle = _targetBlip;
                    MAP.REMOVE_BLIP(&handle);
                }
                if (!MAP.DOES_BLIP_EXIST(_targetBlip)) _targetBlip = 0;
            }
            catch (Exception ex) { Log.Write("Mission objective blip removal warning: " + ex.Message); }
        }

        private void Notify(string message)
        {
            try
            {
                object instance = _bridge.MainInstance;
                if (instance != null)
                {
                    MethodInfo method = instance.GetType().GetMethods(Flags).FirstOrDefault(m =>
                        m.Name == "ShowNotification" && m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(string));
                    if (method != null)
                    {
                        method.Invoke(instance, new object[] { "enRAGE - " + message });
                        return;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Mission notification warning: " + ex.Message); }
            Log.Write("MISSION: " + message);
        }

        private unsafe void RefineMissionTargetHeight(Ped player)
        {
            if (player == null || !player.Exists() || _faction.MissionStage != 1 ||
                Game.GameTime < _nextTargetGroundCheckAt) return;
            Vector3 target = new Vector3(_faction.MissionTargetX, _faction.MissionTargetY, _faction.MissionTargetZ);
            if (target == Vector3.Zero || player.Position.DistanceTo(target) > 180f) return;
            _nextTargetGroundCheckAt = Game.GameTime + 2000;
            try
            {
                STREAMING.REQUEST_COLLISION_AT_COORD(target.X, target.Y, target.Z);
                float groundZ = 0f;
                if (!MISC.GET_GROUND_Z_FOR_3D_COORD(target.X, target.Y, target.Z + 50f, &groundZ, false))
                    return;
                if (Math.Abs(groundZ - target.Z) > 55f) return;
                float correctedZ = groundZ + 0.25f;
                if (Math.Abs(correctedZ - target.Z) < 0.75f) return;
                _faction.MissionTargetZ = correctedZ;
                SaveFaction();
                RemoveTargetBlip();
                EnsureTargetBlip();
                Log.Write("Mission target height corrected after local terrain collision loaded: " + correctedZ + ".");
            }
            catch (Exception ex) { Log.Write("Mission target ground refinement warning: " + ex.Message); }
        }

        private void SaveFaction()
        {
            try { _faction.Save(); }
            catch (Exception ex) { Log.Write("Mission data save warning: " + ex.Message); }
        }
    }
}
