using System;
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

        private readonly EnRAGEBridge _bridge;
        private readonly PlayerFactionData _faction;
        private readonly MissionObjectiveController _objectiveController;
        private readonly MissionStoryDirector _story;
        private Ped _ped;
        private Prop _supplyCache;
        private int _nextCacheSpawnRetryAt;
        private int _supplyCacheSpawnFailures;
        private int _prompt;
        private int _promptGroup;
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
            _story = new MissionStoryDirector(_faction);
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

            MissionObjectiveType activeObjectiveType = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex).ObjectiveType;
            bool courierNeedsFallbackDrop = activeObjectiveType == MissionObjectiveType.DeliverDispatch &&
                _objectiveController.CourierSpawnFailed;
            if (_faction.MissionStage == 1 &&
                (activeObjectiveType == MissionObjectiveType.RecoverSupplies ||
                 activeObjectiveType == MissionObjectiveType.InteractTarget || courierNeedsFallbackDrop))
                MaintainSupplyCacheProp(player);
            else if (_faction.MissionCachePropHandle != 0)
            {
                DeleteSupplyCacheProp();
                SaveFaction();
            }
            if (_faction.MissionStage == 1 &&
                (activeObjectiveType == MissionObjectiveType.RecoverSupplies ||
                 activeObjectiveType == MissionObjectiveType.HoldArea ||
                 activeObjectiveType == MissionObjectiveType.DeliverDispatch ||
                 activeObjectiveType == MissionObjectiveType.InterceptConvoy))
                _objectiveController.Maintain(player);

            _story.Tick();
            if (_faction.MissionStage == 1 && activeObjectiveType == MissionObjectiveType.InterceptConvoy)
                MaintainConvoyLootPrompt(player);
            else
                RemoveLootPrompt();
            MaintainObjectivePrompt(player);

            UpdateMissionProgress(player, campCentre);
            if (_faction.MissionStage != 1 ||
                MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex).ObjectiveType != MissionObjectiveType.InterceptConvoy)
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
                _promptArmed = true;
                return;
            }
            UpdatePrompt();
        }

        internal void OnScriptAborted()
        {
            SetPromptVisible(false);
            RemovePrompt();
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
            RemovePrompt();
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

            if (_prompt == 0 || _promptGroup != group || !PromptValid(_prompt))
            {
                RemovePrompt();
                if (!TryCreatePrompt(group)) return;
            }

            SetPromptVisible(true);
            ActivatePromptGroup(group);
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

        private bool TryCreatePrompt(int group)
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
                _prompt = Convert.ToInt32(make.Invoke(null, new object[] { control, "Pedir una misión", true, group }));
                if (_prompt == 0) return false;
                _promptGroup = group;
                _promptArmed = true;
                Log.Write("Mission giver prompt created. Prompt=" + _prompt + ", group=" + group + ".");
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
            _promptArmed = true;
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
            MissionDefinition definition = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);
            bool missionReady = _faction.MissionStage == 1 &&
                definition.ObjectiveType == MissionObjectiveType.InterceptConvoy &&
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
            if (wagon == null || !wagon.Exists() ||
                player.Position.DistanceTo(wagon.Position) > definition.InteractionRadius)
            {
                SetLootPromptVisible(false);
                _lootPromptArmed = true;
                return;
            }

            int group;
            try { group = Function.Call<int>(PromptGetGroupForEntityHash, new InputArgument[] { wagon.Handle }); }
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

                string promptLabel = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex).PromptLabel;
                if (string.IsNullOrWhiteSpace(promptLabel)) promptLabel = "Saquear cargamento";
                _lootPrompt = Convert.ToInt32(make.Invoke(null,
                    new object[] { control, promptLabel, true, group }));
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
                    entityHandle, MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex).InteractionRadius,
                    2, 0, group, new InputArgument(label), 0
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
            MissionDefinition definition = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);

            if (_faction.MissionStage == 1 && _faction.MissionObjectiveReady &&
                player != null && player.Exists() && !player.IsDead &&
                !player.IsOnMount && !player.IsInVehicle)
            {
                if (definition.ObjectiveType == MissionObjectiveType.RecoverSupplies &&
                    _faction.MissionObjectiveIndex >= 2 &&
                    (_objectiveController.AllGuardsDefeated() || _objectiveController.EncounterSpawnFailed) &&
                    _supplyCache != null && _supplyCache.Exists() &&
                    player.Position.DistanceTo(_supplyCache.Position) <= definition.InteractionRadius)
                {
                    entityHandle = _supplyCache.Handle;
                    promptLabel = string.IsNullOrWhiteSpace(definition.PromptLabel)
                        ? "Recoger provisiones" : definition.PromptLabel;
                }
                else if (definition.ObjectiveType == MissionObjectiveType.DeliverDispatch &&
                    _faction.MissionObjectiveIndex >= 1)
                {
                    Ped courier = _objectiveController.CourierPed;
                    if (courier != null && courier.Exists() &&
                        player.Position.DistanceTo(courier.Position) <= definition.InteractionRadius)
                    {
                        entityHandle = courier.Handle;
                        promptLabel = _faction.MissionObjectiveIndex == 1
                            ? (string.IsNullOrWhiteSpace(definition.FirstPromptLabel) ? "Hablar con el contacto" : definition.FirstPromptLabel)
                            : (string.IsNullOrWhiteSpace(definition.SecondPromptLabel) ? "Entregar despacho" : definition.SecondPromptLabel);
                    }
                    else if (_objectiveController.CourierSpawnFailed &&
                        _supplyCache != null && _supplyCache.Exists() &&
                        player.Position.DistanceTo(_supplyCache.Position) <= definition.InteractionRadius)
                    {
                        entityHandle = _supplyCache.Handle;
                        promptLabel = string.IsNullOrWhiteSpace(definition.SecondPromptLabel)
                            ? "Depositar despacho" : definition.SecondPromptLabel;
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
            ActivateEntityPromptGroup(entityHandle, group, definition.Title,
                Math.Max(definition.InteractionRadius, 3.5f));
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
                MissionDefinition active = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);
                string status;
                if (_faction.MissionStage == 2)
                    status = "Vuelve al campamento para entregar el encargo.";
                else if (_faction.MissionStage == 3)
                    status = "La recompensa está pendiente; se entregará cuando los recursos de la facción estén disponibles.";
                else if (active.ObjectiveType == MissionObjectiveType.RecoverSupplies)
                    status = "Encuentra el alijo y despeja la zona. Al estar listo, aparecerá la interacción contextual.";
                else if (active.ObjectiveType == MissionObjectiveType.DeliverDispatch)
                    status = "Localiza al contacto marcado. La conversación y la entrega se activan con sus prompts contextuales.";
                else if (active.ObjectiveType == MissionObjectiveType.InterceptConvoy)
                    status = "Localiza el convoy y elimina a los escoltas. El cargamento tendrá un prompt contextual cuando la zona esté despejada.";
                else if (active.ObjectiveType == MissionObjectiveType.HoldArea)
                    status = "Mantente dentro del perímetro marcado hasta completar la vigilancia.";
                else
                    status = active.StartInstruction;
                _story.PlayReminderCue();
                _story.ShowMissionStatus("MISIÓN ACTIVA: " + status, 3000);
                return;
            }

            Vector3 centre = _bridge.GetPlayerCampCentre(_faction);
            if (centre == Vector3.Zero)
                centre = new Vector3(_faction.CampX, _faction.CampY, _faction.CampZ);
            if (centre == Vector3.Zero)
            {
                _story.ShowMissionStatus("No se pudo localizar el centro del campamento para asignar una misión.");
                return;
            }

            MissionCatalog.Reload();
            RemoveLootPrompt();
            RemoveObjectivePrompt();
            _objectiveController.CleanupConvoy();
            if (_faction.MissionCachePropHandle != 0) DeleteSupplyCacheProp();
            _supplyCacheSpawnFailures = 0;
            _nextCacheSpawnRetryAt = 0;
            _supplyCache = null;
            Random missionRandom = new Random(unchecked(Game.GameTime * 397 ^ _faction.MissionsCompleted * 7919 ^ _faction.MissionPrestige));
            _faction.MissionDifficulty = MissionCatalog.DetermineDifficulty(_faction.MissionsCompleted, missionRandom);
            _faction.MissionType = MissionCatalog.SelectNext(_faction.MissionsCompleted,
                _faction.LastMissionType, _faction.MissionDifficulty, missionRandom);
            _faction.MissionConvoyWagonHandle = 0;
            _faction.MissionConvoyEnemyHandles.Clear();
            _faction.MissionEncounterSpawned = false;
            _faction.MissionEncounterProcessId = 0;
            _faction.MissionStepIndex = 0;
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

        private unsafe void SetMissionTarget(Vector3 campCentre, int pointIndex)
        {
            MissionDefinition definition = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);
            Vector3 origin = campCentre;
            if (definition.UseFixedTarget)
                origin = new Vector3(definition.TargetX, definition.TargetY, definition.TargetZ);

            double baseAngle = (_faction.MissionsCompleted + 1) * 2.399963229728653 +
                _faction.MissionType * 0.731 + _faction.MissionStepIndex * 0.917;
            double angle = baseAngle;
            bool multiplePoints = definition.ObjectiveType == MissionObjectiveType.PatrolRoute ||
                definition.ObjectiveType == MissionObjectiveType.InvestigateArea ||
                (definition.ObjectiveType == MissionObjectiveType.ReachLocation && definition.WaypointCount > 1);
            if (multiplePoints)
                angle += pointIndex * (Math.PI * 2.0 / definition.WaypointCount);

            float radius = definition.UseFixedTarget
                ? definition.WaypointSpacing * Math.Max(1, pointIndex)
                : definition.TargetDistance + ((_faction.MissionsCompleted + pointIndex + _faction.MissionStepIndex) % 3) * 18f +
                    Math.Max(0, Math.Min(2, _faction.MissionDifficulty)) * 22f;

            Vector3 target;
            if (definition.UseFixedTarget && pointIndex == 0)
                target = origin;
            else
                target = new Vector3(
                    origin.X + (float)Math.Cos(angle) * radius,
                    origin.Y + (float)Math.Sin(angle) * radius,
                    origin.Z + 0.35f);

            bool foundGround = false;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double candidateAngle = angle + attempt * (Math.PI / 4.0);
                float candidateRadius = (definition.UseFixedTarget && pointIndex == 0)
                    ? 0f : radius + (attempt % 3) * 14f;
                Vector3 candidate = candidateRadius == 0f
                    ? target
                    : new Vector3(origin.X + (float)Math.Cos(candidateAngle) * candidateRadius,
                        origin.Y + (float)Math.Sin(candidateAngle) * candidateRadius, origin.Z + 0.35f);
                STREAMING.REQUEST_COLLISION_AT_COORD(candidate.X, candidate.Y, candidate.Z);
                float groundZ = 0f;
                try
                {
                    if (MISC.GET_GROUND_Z_FOR_3D_COORD(candidate.X, candidate.Y, origin.Z + 50f, &groundZ, false) &&
                        Math.Abs(groundZ - origin.Z) <= 80f)
                    {
                        candidate.Z = groundZ + 0.25f;
                        target = candidate;
                        foundGround = true;
                        break;
                    }
                }
                catch { }
                if (definition.UseFixedTarget && pointIndex == 0) break;
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

        private static string GetMissionObjectiveBlipName(PlayerFactionData faction)
        {
            if (faction == null) return "Objetivo de misión";
            MissionDefinition definition = MissionCatalog.GetStep(faction.MissionType, faction.MissionStepIndex);
            if (definition.ObjectiveType == MissionObjectiveType.PatrolRoute ||
                definition.ObjectiveType == MissionObjectiveType.InvestigateArea ||
                (definition.ObjectiveType == MissionObjectiveType.ReachLocation && definition.WaypointCount > 1))
                return definition.Title + ": punto " + (faction.MissionObjectiveIndex + 1) + "/" + definition.WaypointCount;
            return definition.Title;
        }

        private void GetMissionReward(int missionType, out int supplies, out int treasury, out int prestige)
        {
            MissionCatalog.GetRewards(missionType, _faction.MissionDifficulty, out supplies, out treasury, out prestige);
        }

        private string GetMissionPropModelName()
        {
            MissionDefinition definition = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);
            return definition.ObjectiveType == MissionObjectiveType.InteractTarget &&
                !string.IsNullOrWhiteSpace(definition.TargetPropModel)
                ? definition.TargetPropModel
                : SupplyCacheModel;
        }

        private void MaintainSupplyCacheProp(Ped player)
        {
            Vector3 target = new Vector3(_faction.MissionTargetX, _faction.MissionTargetY, _faction.MissionTargetZ);
            if (player == null || !player.Exists() || player.Position.DistanceTo(target) > 110f)
                return;

            if (_supplyCache != null && _supplyCache.Exists())
                return;

            string modelName = GetMissionPropModelName();
            uint expectedModel = MISC.GET_HASH_KEY(modelName);
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
                        _supplyCacheSpawnFailures = 0;
                        return;
                    }
                }
                catch { }

                _faction.MissionCachePropHandle = 0;
                SaveFaction();
            }

            if (_supplyCacheSpawnFailures >= 3 || Game.GameTime < _nextCacheSpawnRetryAt)
                return;
            _nextCacheSpawnRetryAt = Game.GameTime + 5000;

            try
            {
                Model model = new Model(modelName);
                if (!model.IsValid)
                {
                    RegisterSupplyCacheSpawnFailure("model is invalid: " + modelName);
                    return;
                }
                if (!model.IsLoaded && !model.Request(1000))
                {
                    RegisterSupplyCacheSpawnFailure("model did not load: " + modelName);
                    return;
                }

                STREAMING.REQUEST_COLLISION_AT_COORD(target.X, target.Y, target.Z);
                Prop created = World.CreateProp(model, target, false, true);
                if (created == null || !created.Exists())
                {
                    RegisterSupplyCacheSpawnFailure("prop creation returned no entity");
                    return;
                }

                _supplyCache = created;
                _faction.MissionCachePropHandle = created.Handle;
                _supplyCacheSpawnFailures = 0;
                SaveFaction();
                Log.Write("Mission objective prop created. Model=" + modelName + ", handle=" + created.Handle +
                    ", position=" + target + ".");
            }
            catch (Exception ex)
            {
                RegisterSupplyCacheSpawnFailure(ex.Message);
            }
        }

        private void RegisterSupplyCacheSpawnFailure(string reason)
        {
            _supplyCacheSpawnFailures++;
            Log.Write("Mission objective prop spawn failed (attempt " + _supplyCacheSpawnFailures +
                "/3): " + reason + ".");
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
                    uint expectedModel = MISC.GET_HASH_KEY(GetMissionPropModelName());
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
            _supplyCacheSpawnFailures = 0;
            _nextCacheSpawnRetryAt = 0;
        }

        private void UpdateMissionProgress(Ped player, Vector3 campCentre)
        {
            if (player == null || !player.Exists()) return;

            if (_faction.MissionStage == 1)
            {
                MissionDefinition definition = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);
                MissionObjectiveType objectiveType = definition.ObjectiveType;
                Vector3 target = new Vector3(_faction.MissionTargetX, _faction.MissionTargetY, _faction.MissionTargetZ);
                float distance = player.Position.DistanceTo(target);

                if (objectiveType == MissionObjectiveType.PatrolRoute)
                {
                    if (distance <= definition.ObjectiveRadius)
                    {
                        if (_faction.MissionObjectiveIndex + 1 < definition.WaypointCount)
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
                            AdvanceMissionToReturn("Has completado " + definition.Title + ".");
                        }
                    }
                }
                else if (objectiveType == MissionObjectiveType.InterceptConvoy)
                {
                    float encounterRadius = Math.Max(60f, definition.ObjectiveRadius + 40f);
                    if (distance <= encounterRadius && _objectiveController.EncounterSpawned &&
                        _faction.MissionObjectiveIndex == 0)
                    {
                        _faction.MissionObjectiveIndex = 1;
                        SaveFaction();
                        _story.OnEncounterStarted(_faction.MissionType);
                    }

                    if (distance <= encounterRadius &&
                        (_objectiveController.AllGuardsDefeated() || _objectiveController.EncounterSpawnFailed))
                    {
                        if (_objectiveController.IsWagonDestroyedOrMissing())
                        {
                            AdvanceMissionToReturn("Los escoltas han sido neutralizados, pero la carreta está destruida o no se encuentra. Regresa al campamento.");
                        }
                        else if (!_faction.MissionObjectiveReady)
                        {
                            _faction.MissionObjectiveReady = true;
                            SaveFaction();
                            _story.OnLootAvailable(_faction.MissionType);
                        }
                        else if (_objectiveController.IsPlayerNearLoot(player, definition.InteractionRadius) &&
                                 _lootPromptJustCompleted)
                        {
                            _story.OnObjectiveInteraction(player, "REVISIÓN: Buscas entre los bultos y rescatas lo que todavía puede servir.");
                            AdvanceMissionToReturn("Has saqueado el convoy y recuperado sus suministros.");
                        }
                    }
                }
                else if (objectiveType == MissionObjectiveType.RecoverSupplies)
                {
                    float encounterRadius = Math.Max(45f, definition.ObjectiveRadius + 20f);
                    if (_objectiveController.EncounterSpawned && distance <= encounterRadius &&
                        _faction.MissionObjectiveIndex == 0)
                    {
                        _faction.MissionObjectiveIndex = 1;
                        SaveFaction();
                        _story.OnEncounterStarted(_faction.MissionType);
                    }

                    if (distance <= definition.InteractionRadius)
                    {
                        bool guardsClear = _objectiveController.AllGuardsDefeated() ||
                            _objectiveController.EncounterSpawnFailed;
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
                        else if (_supplyCacheSpawnFailures >= 3 &&
                            (_supplyCache == null || !_supplyCache.Exists()))
                        {
                            AdvanceMissionToReturn("No se pudo materializar el alijo físico tras varios intentos. El encargo se cierra para evitar que quede bloqueado.");
                        }
                    }
                }
                else if (objectiveType == MissionObjectiveType.DeliverDispatch)
                {
                    if (distance <= definition.ObjectiveRadius && !_faction.MissionObjectiveReady)
                    {
                        _faction.MissionObjectiveReady = true;
                        _faction.MissionObjectiveIndex = 1;
                        SaveFaction();
                        _story.OnCheckpoint(_faction.MissionType, 1);
                    }

                    if (_faction.MissionObjectiveReady && distance <= definition.ObjectiveRadius &&
                        !_objectiveController.CourierAvailable && _objectiveController.CourierSpawnFailed &&
                        _supplyCacheSpawnFailures >= 3)
                    {
                        AdvanceMissionToReturn("El contacto y el punto de entrega alternativo no pudieron aparecer. El encargo se cierra sin dejar la misión bloqueada.");
                    }
                    else if (_faction.MissionObjectiveReady && _objectivePromptJustCompleted)
                    {
                        if (_objectiveController.CourierAvailable)
                        {
                            if (_faction.MissionObjectiveIndex == 1)
                            {
                                _faction.MissionObjectiveIndex = 2;
                                SaveFaction();
                                _story.OnCourierConversation(_faction.MissionType, _objectiveController.CourierPed, player, false);
                            }
                            else if (_faction.MissionObjectiveIndex >= 2)
                            {
                                _story.OnCourierConversation(_faction.MissionType, _objectiveController.CourierPed, player, true);
                                _story.OnObjectiveInteraction(player, "ENTREGA: Le pasas el despacho al contacto y esperas a que lo guarde.");
                                AdvanceMissionToReturn("Has entregado el despacho al contacto.");
                            }
                        }
                        else if (_objectiveController.CourierSpawnFailed &&
                            _supplyCache != null && _supplyCache.Exists())
                        {
                            _story.OnObjectiveInteraction(player, "ENTREGA: Dejas el despacho en el punto alternativo acordado.");
                            AdvanceMissionToReturn("El contacto no apareció. Dejaste el despacho en un punto de entrega alternativo.");
                        }
                    }
                }
                else if (objectiveType == MissionObjectiveType.HoldArea)
                {
                    int now = Game.GameTime;
                    int durationMs = Math.Max(5000, definition.DurationSeconds * 1000);
                    int encounterAtMs = definition.EncounterAtSeconds * 1000;
                    bool insidePerimeter = distance <= definition.AreaRadius;

                    if (!_missionTimerInitialized)
                    {
                        _lastMissionProgressTickAt = now;
                        _missionTimerInitialized = true;
                    }
                    else
                    {
                        int elapsed = now - _lastMissionProgressTickAt;
                        if (insidePerimeter && elapsed > 0)
                            _faction.MissionWaitProgressMs = Math.Min(durationMs,
                                _faction.MissionWaitProgressMs + elapsed);
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
                        if (!insidePerimeter && _faction.MissionWaitProgressMs < durationMs)
                        {
                            _story.ShowMissionStatus(
                                "VIGILANCIA: fuera del perímetro; el reloj está detenido. Regresa al punto marcado.", 2300);
                        }
                        else if (_faction.MissionWaitProgressMs >= durationMs &&
                                 _objectiveController.EncounterSpawned && !_objectiveController.AllGuardsDefeated())
                        {
                            _story.ShowMissionStatus(
                                "VIGILANCIA: el tiempo terminó. Neutraliza a los atacantes para asegurar el cruce.", 2300);
                        }
                        else if (_faction.MissionWaitProgressMs < durationMs)
                        {
                            int secondsRemaining = (durationMs - _faction.MissionWaitProgressMs + 999) / 1000;
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

                    if (definition.SpawnEncounter &&
                        _faction.MissionWaitProgressMs >= encounterAtMs &&
                        _faction.MissionObjectiveIndex == 0 &&
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

                    if (_faction.MissionWaitProgressMs >= durationMs &&
                        (!_objectiveController.EncounterSpawned || _objectiveController.AllGuardsDefeated() ||
                         _objectiveController.EncounterSpawnFailed))
                    {
                        AdvanceMissionToReturn("Has mantenido la posición durante " +
                            definition.DurationSeconds + " segundos y confirmado quién utiliza la ruta.");
                    }
                }
                else // ReachLocation
                {
                    if (distance <= definition.ObjectiveRadius)
                    {
                        if (_faction.MissionObjectiveIndex + 1 < definition.WaypointCount)
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
                            _story.OnCheckpoint(_faction.MissionType, _faction.MissionObjectiveIndex + 1);
                            AdvanceMissionToReturn("Has completado " + definition.Title + ".");
                        }
                    }
                }
            }

            if (_faction.MissionStage == 2 && player.Position.DistanceTo(campCentre) <= 14f)
            {
                _faction.MissionStage = 3;
                SaveFaction();
            }

            if (_faction.MissionStage == 3 && Game.GameTime >= _nextRewardRetryAt)
                CompleteMission();
        }

        private void AdvanceMissionToReturn(string message)
        {
            MissionDefinition definition = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);
            if (definition.ObjectiveType == MissionObjectiveType.RecoverSupplies ||
                (definition.ObjectiveType == MissionObjectiveType.DeliverDispatch && _faction.MissionCachePropHandle != 0))
                DeleteSupplyCacheProp();
            RemoveLootPrompt();
            RemoveObjectivePrompt();
            _story.OnMissionReturning(_faction.MissionType, message);
            _objectiveController.CleanupConvoy();
            _faction.MissionStage = 2;
            _faction.MissionObjectiveReady = false;
            _faction.MissionWaitProgressMs = 0;
            _missionTimerInitialized = false;
            _lastMissionProgressTickAt = 0;
            _nextStakeoutStatusAt = 0;
            SaveFaction();
            RemoveTargetBlip();
            EnsureTargetBlip();
        }

        private void CompleteMission()
        {
            _nextRewardRetryAt = Game.GameTime + 5000;
            int suppliesReward, treasuryReward, prestigeReward;
            GetMissionReward(_faction.MissionType, out suppliesReward, out treasuryReward, out prestigeReward);

            string error;
            if (!_bridge.AwardMissionResources(_faction, suppliesReward, treasuryReward, out error))
            {
                Log.Write("Mission reward pending; native faction resources unavailable: " + error);
                return;
            }

            string completedName = GetMissionTitle(_faction.MissionType);
            MissionDefinition completedDefinition = MissionCatalog.Get(_faction.MissionType);
            if (completedDefinition.ObjectiveType == MissionObjectiveType.RecoverSupplies ||
                _faction.MissionCachePropHandle != 0)
                DeleteSupplyCacheProp();
            if (completedDefinition.ObjectiveType == MissionObjectiveType.InterceptConvoy ||
                completedDefinition.ObjectiveType == MissionObjectiveType.DeliverDispatch)
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
            if (_targetBlip != 0 && MAP.DOES_BLIP_EXIST(_targetBlip)) return;
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

        private void SaveFaction()
        {
            try { _faction.Save(); }
            catch (Exception ex) { Log.Write("Mission data save warning: " + ex.Message); }
        }
    }
}
