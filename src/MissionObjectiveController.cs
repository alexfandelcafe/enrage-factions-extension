using System;
using System.Collections.Generic;
using System.Diagnostics;
using RDR2;
using RDR2.Math;
using RDR2.Native;

namespace EnRAGEPlayerFactionExtension
{
    /// <summary>
    /// Owns physical mission encounter entities; CampMissionGiver remains responsible for prompts and stages.
    /// Handles are process-scoped and are only reused after model/position validation.
    /// </summary>
    internal sealed class MissionObjectiveController
    {
        private const int MissionConvoy = 5;
        private const string ConvoyWagonModel = "wagon02x";
        private static readonly string[] GuardModels = { "G_M_M_UniCriminals_01", "G_M_M_UniCriminals_02", "G_M_M_UniCriminals_03" };
        private readonly PlayerFactionData _faction;
        private int _nextSpawnAttemptAt;

        internal MissionObjectiveController(PlayerFactionData faction) { _faction = faction; }

        internal bool ConvoySpawned
        {
            get { return _faction.MissionEncounterSpawned && _faction.MissionConvoyEnemyHandles != null && _faction.MissionConvoyEnemyHandles.Count > 0; }
        }

        internal void Maintain(Ped player)
        {
            if (player == null || !player.Exists() || _faction.MissionStage != 1 || _faction.MissionType != MissionConvoy) return;
            Vector3 target = MissionTarget;
            if (target == Vector3.Zero || player.Position.DistanceTo(target) > 115f) return;

            int processId = Process.GetCurrentProcess().Id;
            if (_faction.MissionEncounterProcessId != 0 && _faction.MissionEncounterProcessId != processId)
            {
                _faction.MissionConvoyWagonHandle = 0;
                _faction.MissionConvoyEnemyHandles.Clear();
                _faction.MissionEncounterSpawned = false;
                _faction.MissionEncounterProcessId = 0;
                SaveFaction();
            }
            if (_faction.MissionEncounterSpawned || Game.GameTime < _nextSpawnAttemptAt) return;

            _nextSpawnAttemptAt = Game.GameTime + 5000;
            EnsureWagon(target);
            SpawnGuards(player, target);
            if (_faction.MissionConvoyEnemyHandles.Count > 0)
            {
                _faction.MissionEncounterSpawned = true;
                _faction.MissionEncounterProcessId = processId;
                SaveFaction();
                Log.Write("Convoy encounter created. Guards=" + _faction.MissionConvoyEnemyHandles.Count +
                          ", difficulty=" + _faction.MissionDifficulty + ".");
            }
            else Log.Write("Convoy encounter could not create guards; retrying near target.");
        }

        internal bool AllGuardsDefeated()
        {
            if (!ConvoySpawned) return false;
            foreach (int handle in _faction.MissionConvoyEnemyHandles)
            {
                Ped guard = ResolveGuard(handle, MissionTarget);
                if (guard != null && guard.Exists() && !guard.IsDead) return false;
            }
            return true;
        }

        internal bool IsPlayerNearLoot(Ped player)
        {
            if (player == null || !player.Exists()) return false;
            Vector3 target = MissionTarget;
            Vehicle wagon = ResolveWagon(target);
            Vector3 loot = wagon != null && wagon.Exists() ? wagon.Position : target;
            return loot != Vector3.Zero && player.Position.DistanceTo(loot) <= 8f;
        }

        internal void CleanupConvoy()
        {
            bool changed = _faction.MissionConvoyWagonHandle != 0 ||
                (_faction.MissionConvoyEnemyHandles != null && _faction.MissionConvoyEnemyHandles.Count > 0) ||
                _faction.MissionEncounterSpawned || _faction.MissionEncounterProcessId != 0;
            Vector3 target = MissionTarget;
            Vehicle wagon = ResolveWagon(target);
            try { if (wagon != null && wagon.Exists()) wagon.Delete(); }
            catch (Exception ex) { Log.Write("Convoy wagon cleanup warning: " + ex.Message); }
            if (_faction.MissionConvoyEnemyHandles != null)
            {
                foreach (int handle in _faction.MissionConvoyEnemyHandles)
                {
                    Ped guard = ResolveGuard(handle, target);
                    try { if (guard != null && guard.Exists()) guard.Delete(); }
                    catch (Exception ex) { Log.Write("Convoy guard cleanup warning: " + ex.Message); }
                }
            }
            _faction.MissionConvoyWagonHandle = 0;
            if (_faction.MissionConvoyEnemyHandles == null) _faction.MissionConvoyEnemyHandles = new List<int>();
            else _faction.MissionConvoyEnemyHandles.Clear();
            _faction.MissionEncounterSpawned = false;
            _faction.MissionEncounterProcessId = 0;
            _nextSpawnAttemptAt = 0;
            if (changed) SaveFaction();
        }

        private Vector3 MissionTarget { get { return new Vector3(_faction.MissionTargetX, _faction.MissionTargetY, _faction.MissionTargetZ); } }

        private void EnsureWagon(Vector3 target)
        {
            Vehicle existing = ResolveWagon(target);
            if (existing != null && existing.Exists()) return;
            try
            {
                Model model = new Model(ConvoyWagonModel);
                if (!model.IsValid || !model.IsVehicle) { Log.Write("Convoy wagon model is invalid: " + ConvoyWagonModel + "."); return; }
                if (!model.IsLoaded && !model.Request(1500)) { Log.Write("Convoy wagon model did not load: " + ConvoyWagonModel + "."); return; }
                double angle = (_faction.MissionsCompleted + 1) * 1.17;
                Vector3 position = new Vector3(target.X + (float)Math.Cos(angle) * 4.5f, target.Y + (float)Math.Sin(angle) * 4.5f, target.Z + 0.25f);
                VehicleHash wagonHash = (VehicleHash)Enum.ToObject(typeof(VehicleHash), MISC.GET_HASH_KEY(ConvoyWagonModel));
                Vehicle wagon = World.CreateVehicle(wagonHash, position, (float)((angle * 180.0 / Math.PI + 90.0) % 360.0));
                if (wagon == null || !wagon.Exists()) { Log.Write("Convoy wagon could not be created at " + position + "."); return; }
                ENTITY.SET_ENTITY_AS_MISSION_ENTITY(wagon.Handle, true, true);
                _faction.MissionConvoyWagonHandle = wagon.Handle;
                SaveFaction();
            }
            catch (Exception ex) { Log.Write("Convoy wagon creation warning: " + ex.Message); }
        }

        private void SpawnGuards(Ped player, Vector3 target)
        {
            int desired = 2 + Math.Max(0, Math.Min(2, _faction.MissionDifficulty));
            for (int i = 0; i < desired; i++)
            {
                double angle = Math.PI * 2.0 * i / desired + _faction.MissionsCompleted * 0.43;
                float radius = 9.0f + (i % 2) * 3.5f;
                Vector3 position = new Vector3(target.X + (float)Math.Cos(angle) * radius, target.Y + (float)Math.Sin(angle) * radius, target.Z + 0.25f);
                string modelName = GuardModels[i % GuardModels.Length];
                try
                {
                    uint hash = MISC.GET_HASH_KEY(modelName);
                    PedHash pedHash = (PedHash)Enum.ToObject(typeof(PedHash), hash);
                    Model model = new Model(pedHash);
                    if (!model.IsValid || !model.IsPed) { Log.Write("Convoy guard model is invalid: " + modelName + "."); continue; }
                    if (!model.IsLoaded && !model.Request(1500)) { Log.Write("Convoy guard model did not load: " + modelName + "."); continue; }
                    Ped guard = World.CreatePed(pedHash, position, (float)((angle * 180.0 / Math.PI + 180.0) % 360.0));
                    if (guard == null || !guard.Exists()) continue;
                    ENTITY.SET_ENTITY_AS_MISSION_ENTITY(guard.Handle, true, true);
                    guard.Accuracy = 20 + _faction.MissionDifficulty * 12;
                    guard.Weapons.Give(WeaponHash.RevolverCattleman, 36 + _faction.MissionDifficulty * 18, true, true);
                    guard.Task.FightAgainst(player);
                    _faction.MissionConvoyEnemyHandles.Add(guard.Handle);
                }
                catch (Exception ex) { Log.Write("Convoy guard spawn warning for " + modelName + ": " + ex.Message); }
            }
        }

        private Vehicle ResolveWagon(Vector3 target)
        {
            if (_faction.MissionConvoyWagonHandle == 0 || target == Vector3.Zero) return null;
            try
            {
                Vehicle wagon = new Vehicle(_faction.MissionConvoyWagonHandle);
                if (wagon.Exists() && unchecked((uint)wagon.Model.Hash) == MISC.GET_HASH_KEY(ConvoyWagonModel) && wagon.Position.DistanceTo(target) <= 35f)
                    return wagon;
            }
            catch { }
            return null;
        }

        private Ped ResolveGuard(int handle, Vector3 target)
        {
            if (handle == 0 || target == Vector3.Zero) return null;
            try
            {
                Ped guard = new Ped(handle);
                if (!guard.Exists() || guard.Position.DistanceTo(target) > 80f) return null;
                uint hash = unchecked((uint)guard.Model.Hash);
                foreach (string model in GuardModels) if (hash == MISC.GET_HASH_KEY(model)) return guard;
            }
            catch { }
            return null;
        }

        private void SaveFaction()
        {
            try { _faction.Save(); }
            catch (Exception ex) { Log.Write("Convoy mission state save warning: " + ex.Message); }
        }
    }
}
