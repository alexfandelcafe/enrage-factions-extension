using System;
using System.Linq;
using System.Reflection;
using RDR2;
using RDR2.Math;
using RDR2.Native;

namespace EnRAGEPlayerFactionExtension
{
    /// <summary>
    /// Independent mission giver and first end-to-end mission. It intentionally does not
    /// subscribe to or consume enRAGE WorldEvent/Encounter events.
    /// </summary>
    internal sealed class CampMissionGiver
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const int ContextControl = 1980406895;
        private const ulong IsControlJustPressedHash = 0x580417101DDB492FUL;
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
        private const string TargetSprite = "blip_poi";
        private const int MissionSuppliesReward = 5;
        private const int MissionTreasuryReward = 25;
        private const int MissionPrestigeReward = 5;

        private readonly EnRAGEBridge _bridge;
        private readonly PlayerFactionData _faction;
        private Ped _ped;
        private int _prompt;
        private int _promptGroup;
        private int _targetBlip;
        private int _nextSpawnAt;
        private int _nextProtectionAt;
        private int _nextRewardRetryAt;
        private bool _promptArmed = true;

        internal CampMissionGiver(EnRAGEBridge bridge, PlayerFactionData faction)
        {
            _bridge = bridge;
            _faction = faction;
        }

        internal void Maintain(Ped player, bool interactionBlocked)
        {
            if (!_faction.Founded || string.IsNullOrWhiteSpace(_faction.CampSiteId))
            {
                DeletePed();
                RemoveTargetBlip();
                return;
            }

            if (player == null || !player.Exists() || player.IsDead || !_bridge.IsAvailable)
            {
                SetPromptVisible(false);
                return;
            }

            Vector3 campCentre = _bridge.GetPlayerCampCentre(_faction);
            if (campCentre == Vector3.Zero)
                campCentre = new Vector3(_faction.CampX, _faction.CampY, _faction.CampZ);
            if (campCentre == Vector3.Zero) return;

            UpdateMissionProgress(player, campCentre);
            if (_faction.MissionStage > 0) EnsureTargetBlip();
            else RemoveTargetBlip();

            float distanceToCamp = player.Position.DistanceTo(campCentre);
            ReconnectPed(campCentre);
            if (distanceToCamp > 180f)
            {
                DeletePed();
                return;
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
            RemoveTargetBlip();
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
            bool contextPressed = JustPressed(ContextControl);
            if (!completed && !contextPressed)
            {
                _promptArmed = true;
                return;
            }
            if (!_promptArmed && !contextPressed) return;

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

        private bool JustPressed(int control)
        {
            try
            {
                if (Function.Call<bool>(IsControlJustPressedHash, new InputArgument[] { 0, control })) return true;
                return Function.Call<bool>(IsControlJustPressedHash, new InputArgument[] { 2, control });
            }
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

        private void OnInteract()
        {
            if (_faction.MissionStage > 0)
            {
                if (_faction.MissionStage == 1)
                    Notify("MISIÓN ACTIVA: inspecciona el punto marcado en el mapa y vuelve al campamento.");
                else
                    Notify("MISIÓN ACTIVA: vuelve al campamento para entregar el informe.");
                return;
            }

            Vector3 centre = _bridge.GetPlayerCampCentre(_faction);
            double angle = (_faction.MissionsCompleted + 1) * 2.399963229728653;
            Vector3 target = new Vector3(
                centre.X + (float)Math.Cos(angle) * 260f,
                centre.Y + (float)Math.Sin(angle) * 260f,
                centre.Z + 0.35f);
            STREAMING.REQUEST_COLLISION_AT_COORD(target.X, target.Y, target.Z);
            float groundZ = 0f;
            try
            {
                if (MISC.GET_GROUND_Z_FOR_3D_COORD(target.X, target.Y, centre.Z + 35f, &groundZ, false) &&
                    Math.Abs(groundZ - centre.Z) <= 45f)
                    target.Z = groundZ + 0.25f;
            }
            catch { }

            _faction.MissionTargetX = target.X;
            _faction.MissionTargetY = target.Y;
            _faction.MissionTargetZ = target.Z;
            _faction.MissionStage = 1;
            SaveFaction();
            EnsureTargetBlip();
            Notify("Nueva misión: Reconocimiento de ruta. Visita el punto marcado y vuelve. Recompensa: +5 prestigio, +5 suministros y $25 para la tesorería.");
        }

        private void UpdateMissionProgress(Ped player, Vector3 campCentre)
        {
            if (_faction.MissionStage == 1)
            {
                Vector3 target = new Vector3(_faction.MissionTargetX, _faction.MissionTargetY, _faction.MissionTargetZ);
                if (player.Position.DistanceTo(target) <= 24f)
                {
                    _faction.MissionStage = 2;
                    SaveFaction();
                    RemoveTargetBlip();
                    EnsureTargetBlip();
                    Notify("Punto inspeccionado. Vuelve al campamento para entregar el informe.");
                }
            }

            if (_faction.MissionStage == 2 && player.Position.DistanceTo(campCentre) <= 14f)
            {
                _faction.MissionStage = 3; // Persist a claim state before paying to avoid repeated per-tick rewards.
                SaveFaction();
            }

            if (_faction.MissionStage == 3 && Game.GameTime >= _nextRewardRetryAt)
                CompleteMission();
        }

        private void CompleteMission()
        {
            _nextRewardRetryAt = Game.GameTime + 5000;
            string error;
            if (!_bridge.AwardMissionResources(_faction, MissionSuppliesReward, MissionTreasuryReward, out error))
            {
                Log.Write("Mission reward pending; native faction resources unavailable: " + error);
                return;
            }

            _faction.MissionPrestige = Math.Min(100, _faction.MissionPrestige + MissionPrestigeReward);
            _faction.MissionsCompleted++;
            _faction.MissionStage = 0;
            _faction.MissionTargetX = 0f;
            _faction.MissionTargetY = 0f;
            _faction.MissionTargetZ = 0f;
            SaveFaction();
            RemoveTargetBlip();
            Notify("Misión completada: +5 prestigio, +5 suministros y $25 añadidos a la tesorería de la facción.");
            Log.Write("Independent mission completed. Count=" + _faction.MissionsCompleted +
                ", mission prestige=" + _faction.MissionPrestige + ", resources=+" + MissionSuppliesReward +
                " supplies/+$" + MissionTreasuryReward + ".");
        }

        private void EnsureTargetBlip()
        {
            if (_faction.MissionStage <= 0) return;
            if (_targetBlip != 0 && MAP.DOES_BLIP_EXIST(_targetBlip)) return;
            RemoveTargetBlip();

            Vector3 target = _faction.MissionStage == 1
                ? new Vector3(_faction.MissionTargetX, _faction.MissionTargetY, _faction.MissionTargetZ)
                : new Vector3(_faction.CampX, _faction.CampY, _faction.CampZ);
            try
            {
                _targetBlip = MAP.BLIP_ADD_FOR_COORDS(NativeMapBlipStyle, target.X, target.Y, target.Z);
                if (_targetBlip != 0)
                {
                    MAP.SET_BLIP_SPRITE(_targetBlip, MISC.GET_HASH_KEY(TargetSprite), true);
                    SetBlipName(_targetBlip, _faction.MissionStage == 1 ? "Reconocimiento de ruta" : "Volver al campamento");
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

        private void SaveFaction()
        {
            try { _faction.Save(); }
            catch (Exception ex) { Log.Write("Mission data save warning: " + ex.Message); }
        }
    }
}
