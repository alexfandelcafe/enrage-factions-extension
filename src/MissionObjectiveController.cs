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
        private const int MissionSupplyCache = 2;
        private const int MissionStakeout = 3;
        private const int MissionCourier = 4;
        private const int MissionConvoy = 5;
        private const int MissionEliminateTargets = 6;
        private const int MissionDestroyTarget = 7;
        private const int MissionRescuePrisoner = 8;
        private const int MissionHuntAnimal = 9;
        private const string ConvoyWagonModel = "wagon02x";
        private const string CourierModel = "U_M_M_RhdBartender_01";
        private const string HuntingAnimalModel = "A_C_Wolf";
        private static readonly string[] GuardModels = { "G_M_M_UniCriminals_01", "G_M_M_UniCriminals_02", "G_M_M_UniDuster_01" };
        private readonly PlayerFactionData _faction;
        private int _nextSpawnAttemptAt;
        private int _spawnFailures;
        private int _nextCourierSpawnAttemptAt;
        private int _courierSpawnFailures;
        private int _nextVehicleSpawnAttemptAt;
        private int _vehicleSpawnFailures;

        internal MissionObjectiveController(PlayerFactionData faction) { _faction = faction; }

        internal bool EncounterSpawned
        {
            get { return _faction.MissionEncounterSpawned && _faction.MissionConvoyEnemyHandles != null && _faction.MissionConvoyEnemyHandles.Count > 0; }
        }

        internal bool ConvoySpawned { get { return EncounterSpawned; } }
        internal bool EncounterSpawnFailed { get { return _spawnFailures >= 3; } }
        internal Ped CourierPed { get { return ResolveCourier(MissionTarget); } }
        internal bool CourierAvailable
        {
            get { Ped courier = ResolveCourier(MissionTarget); return courier != null && courier.Exists(); }
        }
        internal bool CourierSpawnFailed { get { return _courierSpawnFailures >= 3; } }
        internal bool TargetVehicleAvailable
        {
            get { Vehicle vehicle = ResolveWagon(MissionTarget, 250f); return vehicle != null && vehicle.Exists(); }
        }
        internal bool TargetVehicleSpawnFailed { get { return _vehicleSpawnFailures >= 3; } }

        internal void Maintain(Ped player)
        {
            if (player == null || !player.Exists() || _faction.MissionStage != 1) return;
            Vector3 target = MissionTarget;
            if (target == Vector3.Zero) return;

            int missionType = _faction.MissionType;
            float distance = player.Position.DistanceTo(target);

            if (missionType == MissionCourier || missionType == MissionRescuePrisoner)
            {
                if (distance <= 85f && Game.GameTime >= _nextCourierSpawnAttemptAt &&
                    !CourierAvailable && (missionType != MissionRescuePrisoner || _courierSpawnFailures < 3))
                {
                    _nextCourierSpawnAttemptAt = Game.GameTime + 5000;
                    if (!EnsureCourier(target, player)) _courierSpawnFailures++;
                    else _courierSpawnFailures = 0;
                }
                if (missionType == MissionCourier) return;
            }

            bool supportedCombatMission = missionType == MissionSupplyCache ||
                missionType == MissionStakeout || missionType == MissionConvoy ||
                missionType == MissionEliminateTargets || missionType == MissionDestroyTarget ||
                missionType == MissionRescuePrisoner || missionType == MissionHuntAnimal;
            if (!supportedCombatMission) return;

            float triggerDistance = missionType == MissionConvoy ? 115f :
                (missionType == MissionDestroyTarget ? 100f :
                (missionType == MissionStakeout ? 40f :
                (missionType == MissionHuntAnimal ? 75f : 65f)));
            if (distance > triggerDistance) return;
            if (missionType == MissionStakeout && _faction.MissionWaitProgressMs < 8000) return;

            int processId = Process.GetCurrentProcess().Id;
            if (_faction.MissionEncounterProcessId != 0 && _faction.MissionEncounterProcessId != processId)
            {
                _faction.MissionConvoyWagonHandle = 0;
                if (_faction.MissionConvoyEnemyHandles == null)
                    _faction.MissionConvoyEnemyHandles = new List<int>();
                else
                    _faction.MissionConvoyEnemyHandles.Clear();
                _faction.MissionEncounterSpawned = false;
                _faction.MissionEncounterProcessId = 0;
                _spawnFailures = 0;
                _vehicleSpawnFailures = 0;
                _courierSpawnFailures = 0;
                SaveFaction();
            }

            // Retry vehicle creation independently from enemy creation.
            if ((missionType == MissionConvoy || missionType == MissionDestroyTarget) &&
                _faction.MissionConvoyWagonHandle == 0 && _vehicleSpawnFailures < 3 &&
                Game.GameTime >= _nextVehicleSpawnAttemptAt)
            {
                _nextVehicleSpawnAttemptAt = Game.GameTime + 5000;
                if (EnsureWagon(target)) _vehicleSpawnFailures = 0;
                else _vehicleSpawnFailures++;
            }

            if (_faction.MissionEncounterSpawned || Game.GameTime < _nextSpawnAttemptAt) return;

            _nextSpawnAttemptAt = Game.GameTime + 5000;
            if (_faction.MissionConvoyEnemyHandles == null)
                _faction.MissionConvoyEnemyHandles = new List<int>();
            SpawnGuards(player, target, missionType);
            if (_faction.MissionConvoyEnemyHandles.Count > 0)
            {
                _faction.MissionEncounterSpawned = true;
                _faction.MissionEncounterProcessId = processId;
                _spawnFailures = 0;
                SaveFaction();
                Log.Write("Mission encounter created. Type=" + missionType + ", targets=" +
                    _faction.MissionConvoyEnemyHandles.Count + ", difficulty=" + _faction.MissionDifficulty + ".");
            }
            else
            {
                _spawnFailures++;
                Log.Write("Mission encounter failed to create targets (attempt " + _spawnFailures +
                    "); retrying near target.");
            }
        }

        internal bool AllGuardsDefeated()
        {
            if (!EncounterSpawned) return false;
            foreach (int handle in _faction.MissionConvoyEnemyHandles)
            {
                Ped guard = ResolveGuard(handle, MissionTarget);
                if (guard != null && guard.Exists() && !guard.IsDead) return false;
            }
            return true;
        }

        internal Vehicle CargoWagon { get { return ResolveWagon(MissionTarget, 250f); } }

        internal bool IsPlayerNearLoot(Ped player, float maxDistance = 8f)
        {
            if (player == null || !player.Exists()) return false;
            Vector3 target = MissionTarget;
            Vehicle wagon = ResolveWagon(target, 250f);
            Vector3 loot = wagon != null && wagon.Exists() ? wagon.Position : target;
            return loot != Vector3.Zero && player.Position.DistanceTo(loot) <= maxDistance;
        }

        internal bool IsWagonDestroyedOrMissing()
        {
            // A convoy must not soft-lock when its cargo wagon is destroyed, despawned, or
            // knocked far away by an explosion. No wagon handle also means the spawn failed;
            // once its guards are defeated, the player can still finish the contract.
            if (_faction.MissionConvoyWagonHandle == 0)
                return _vehicleSpawnFailures >= 3;

            try
            {
                Vehicle wagon = ResolveWagon(MissionTarget, 250f);
                if (wagon == null || !wagon.Exists())
                    return true;

                return wagon.IsDead || wagon.Health <= 0 || wagon.BodyHealth <= 0f || wagon.IsOnFire;
            }
            catch (Exception ex)
            {
                Log.Write("Convoy wagon damage check warning: " + ex.Message);
                return true;
            }
        }

        internal void CleanupConvoy()
        {
            bool changed = _faction.MissionConvoyWagonHandle != 0 ||
                _faction.MissionCourierPedHandle != 0 ||
                (_faction.MissionConvoyEnemyHandles != null && _faction.MissionConvoyEnemyHandles.Count > 0) ||
                _faction.MissionEncounterSpawned || _faction.MissionEncounterProcessId != 0;
            Vector3 target = MissionTarget;
            Vehicle wagon = ResolveWagon(target, 250f);
            Ped courier = ResolveCourier(target);
            try { if (wagon != null && wagon.Exists()) wagon.Delete(); }
            catch (Exception ex) { Log.Write("Convoy wagon cleanup warning: " + ex.Message); }
            try { if (courier != null && courier.Exists()) courier.Delete(); }
            catch (Exception ex) { Log.Write("Courier cleanup warning: " + ex.Message); }
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
            _faction.MissionCourierPedHandle = 0;
            if (_faction.MissionConvoyEnemyHandles == null) _faction.MissionConvoyEnemyHandles = new List<int>();
            else _faction.MissionConvoyEnemyHandles.Clear();
            _faction.MissionEncounterSpawned = false;
            _faction.MissionEncounterProcessId = 0;
            _nextSpawnAttemptAt = 0;
            _nextVehicleSpawnAttemptAt = 0;
            _nextCourierSpawnAttemptAt = 0;
            _courierSpawnFailures = 0;
            _vehicleSpawnFailures = 0;
            _spawnFailures = 0;
            if (changed) SaveFaction();
        }

        private Vector3 MissionTarget { get { return new Vector3(_faction.MissionTargetX, _faction.MissionTargetY, _faction.MissionTargetZ); } }

        private bool EnsureWagon(Vector3 target)
        {
            Vehicle existing = ResolveWagon(target);
            if (existing != null && existing.Exists()) return true;
            try
            {
                Model model = new Model(ConvoyWagonModel);
                if (!model.IsValid || !model.IsVehicle) { Log.Write("Mission wagon model is invalid: " + ConvoyWagonModel + "."); return false; }
                if (!model.IsLoaded && !model.Request(1500)) { Log.Write("Mission wagon model did not load: " + ConvoyWagonModel + "."); return false; }
                double angle = (_faction.MissionsCompleted + 1) * 1.17 + _faction.MissionType * 0.31;
                Vector3 position = new Vector3(target.X + (float)Math.Cos(angle) * 4.5f,
                    target.Y + (float)Math.Sin(angle) * 4.5f, target.Z + 0.25f);
                VehicleHash wagonHash = (VehicleHash)Enum.ToObject(typeof(VehicleHash), MISC.GET_HASH_KEY(ConvoyWagonModel));
                Vehicle wagon = World.CreateVehicle(wagonHash, position,
                    (float)((angle * 180.0 / Math.PI + 90.0) % 360.0));
                if (wagon == null || !wagon.Exists()) { Log.Write("Mission target wagon could not be created at " + position + "."); return false; }
                ENTITY.SET_ENTITY_AS_MISSION_ENTITY(wagon.Handle, true, true);
                _faction.MissionConvoyWagonHandle = wagon.Handle;
                SaveFaction();
                Log.Write("Mission target wagon spawned for contract " + _faction.MissionType + ". Handle=" + wagon.Handle + ".");
                return true;
            }
            catch (Exception ex) { Log.Write("Mission wagon creation warning: " + ex.Message); return false; }
        }

        private void SpawnGuards(Ped player, Vector3 target, int missionType)
        {
            int difficulty = Math.Max(0, Math.Min(2, _faction.MissionDifficulty));
            int desired;
            if (missionType == MissionHuntAnimal) desired = 2 + Math.Min(1, difficulty);
            else if (missionType == MissionEliminateTargets) desired = 3 + difficulty;
            else if (missionType == MissionDestroyTarget || missionType == MissionRescuePrisoner) desired = 2 + difficulty;
            else if (missionType == MissionConvoy || missionType == MissionStakeout) desired = 2 + difficulty;
            else desired = 2 + Math.Min(1, difficulty);

            for (int i = 0; i < desired; i++)
            {
                double angle = Math.PI * 2.0 * i / desired + _faction.MissionsCompleted * 0.43 +
                    _faction.MissionType * 0.29;
                float radius = 9.0f + (i % 2) * 3.5f;
                Vector3 position = new Vector3(target.X + (float)Math.Cos(angle) * radius,
                    target.Y + (float)Math.Sin(angle) * radius, target.Z + 0.25f);
                bool hunting = missionType == MissionHuntAnimal;
                string modelName = hunting ? HuntingAnimalModel : GuardModels[i % GuardModels.Length];
                try
                {
                    uint hash = MISC.GET_HASH_KEY(modelName);
                    PedHash pedHash = (PedHash)Enum.ToObject(typeof(PedHash), hash);
                    Model model = new Model(pedHash);
                    if (!model.IsValid || !model.IsPed) { Log.Write("Mission target model is invalid: " + modelName + "."); continue; }
                    if (!model.IsLoaded && !model.Request(1500)) { Log.Write("Mission target model did not load: " + modelName + "."); continue; }
                    Ped targetPed = World.CreatePed(pedHash, position,
                        (float)((angle * 180.0 / Math.PI + 180.0) % 360.0));
                    if (targetPed == null || !targetPed.Exists()) continue;

                    ENTITY.SET_ENTITY_AS_MISSION_ENTITY(targetPed.Handle, true, true);
                    _faction.MissionConvoyEnemyHandles.Add(targetPed.Handle);
                    if (hunting)
                    {
                        targetPed.Task.Combat(player, -1);
                    }
                    else
                    {
                        targetPed.Accuracy = 20 + difficulty * 12;
                        targetPed.Weapons.Give((uint)eWeapon.RevolverCattleman, 36 + difficulty * 18);
                        targetPed.Task.Combat(player, -1);
                    }
                }
                catch (Exception ex) { Log.Write("Mission target spawn warning for " + modelName + ": " + ex.Message); }
            }
        }

        private bool EnsureCourier(Vector3 target, Ped player)
        {
            Ped existing = ResolveCourier(target);
            if (existing != null && existing.Exists())
            {
                FaceCourier(existing, player);
                return true;
            }

            try
            {
                string modelName = CourierModel;
                uint hash = MISC.GET_HASH_KEY(modelName);
                PedHash pedHash = (PedHash)Enum.ToObject(typeof(PedHash), hash);
                Model model = new Model(pedHash);
                if (!model.IsValid || !model.IsPed)
                {
                    Log.Write("Mission contact model is invalid: " + modelName + ".");
                    return false;
                }
                if (!model.IsLoaded && !model.Request(1500))
                {
                    Log.Write("Mission contact model did not load: " + modelName + ".");
                    return false;
                }

                double angle = (_faction.MissionsCompleted + 1) * 0.91;
                Vector3 position = new Vector3(target.X + (float)Math.Cos(angle) * 2.5f,
                    target.Y + (float)Math.Sin(angle) * 2.5f, target.Z + 0.2f);
                Ped courier = World.CreatePed(pedHash, position, (float)((angle * 180.0 / Math.PI + 180.0) % 360.0));
                if (courier == null || !courier.Exists()) return false;

                ENTITY.SET_ENTITY_AS_MISSION_ENTITY(courier.Handle, true, true);
                courier.BlockPermanentEvents = true;
                _faction.MissionCourierPedHandle = courier.Handle;
                SaveFaction();
                FaceCourier(courier, player);
                Log.Write("Mission courier contact created. Handle=" + courier.Handle + ".");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Courier contact creation warning: " + ex.Message);
                return false;
            }
        }

        private void FaceCourier(Ped courier, Ped player)
        {
            try
            {
                if (courier != null && courier.Exists() && player != null && player.Exists())
                    Function.Call(0x5AD23D40115353ACUL,
                        new InputArgument[] { courier.Handle, player.Handle, 1800 });
            }
            catch (Exception ex) { Log.Write("Courier facing task warning: " + ex.Message); }
        }

        private Ped ResolveCourier(Vector3 target)
        {
            if (_faction.MissionCourierPedHandle == 0 || target == Vector3.Zero) return null;
            try
            {
                Ped courier = new Ped(_faction.MissionCourierPedHandle);
                if (courier.Exists() &&
                    unchecked((uint)courier.Model.Hash) == MISC.GET_HASH_KEY(CourierModel) &&
                    courier.Position.DistanceTo(target) <= 125f)
                    return courier;
            }
            catch { }
            return null;
        }

        private Vehicle ResolveWagon(Vector3 target, float maxDistance = 35f)
        {
            if (_faction.MissionConvoyWagonHandle == 0 || target == Vector3.Zero) return null;
            try
            {
                Vehicle wagon = new Vehicle(_faction.MissionConvoyWagonHandle);
                if (wagon.Exists() && unchecked((uint)wagon.Model.Hash) == MISC.GET_HASH_KEY(ConvoyWagonModel) && wagon.Position.DistanceTo(target) <= maxDistance)
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
                if (!guard.Exists() || guard.Position.DistanceTo(target) > 500f) return null;
                uint hash = unchecked((uint)guard.Model.Hash);
                if (_faction.MissionType == MissionHuntAnimal)
                {
                    if (hash == MISC.GET_HASH_KEY(HuntingAnimalModel)) return guard;
                }
                else
                {
                    foreach (string model in GuardModels)
                        if (hash == MISC.GET_HASH_KEY(model)) return guard;
                }
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
