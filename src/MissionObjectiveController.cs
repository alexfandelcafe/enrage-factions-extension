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
        private const string ConvoyWagonModel = "wagon02x";
        private const string CourierModel = "U_M_M_RhdBartender_01";
        private static readonly string[] GuardModels = { "G_M_M_UniCriminals_01", "G_M_M_UniCriminals_02", "G_M_M_UniDuster_01" };
        private readonly PlayerFactionData _faction;
        private int _nextSpawnAttemptAt;
        private int _spawnFailures;
        private int _nextCourierSpawnAttemptAt;
        private int _courierSpawnFailures;
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
        internal bool TargetVehicleSpawnFailed { get { return _vehicleSpawnFailures >= 3; } }
        internal Ped MissionTargetPed { get { return ResolveCourier(MissionTarget); } }
        internal bool MissionTargetPedDead
        {
            get
            {
                Ped ped = ResolveCourier(MissionTarget);
                return ped != null && ped.Exists() && ped.IsDead;
            }
        }

        internal void Maintain(Ped player)
        {
            if (player == null || !player.Exists() || _faction.MissionStage != 1) return;
            Vector3 target = MissionTarget;
            if (target == Vector3.Zero) return;

            MissionDefinition definition = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);
            MissionObjectiveType objectiveType = definition.ObjectiveType;
            float distance = player.Position.DistanceTo(target);

            if (objectiveType == MissionObjectiveType.DeliverDispatch ||
                objectiveType == MissionObjectiveType.RescuePrisoner)
            {
                if (distance <= 85f && Game.GameTime >= _nextCourierSpawnAttemptAt &&
                    _faction.MissionCourierPedHandle == 0)
                {
                    _nextCourierSpawnAttemptAt = Game.GameTime + 5000;
                    if (!EnsureCourier(target, player)) _courierSpawnFailures++;
                    else _courierSpawnFailures = 0;
                }
                return;
            }

            if (objectiveType == MissionObjectiveType.DestroyTarget)
            {
                if (distance <= 115f && _faction.MissionConvoyWagonHandle == 0 &&
                    Game.GameTime >= _nextSpawnAttemptAt)
                {
                    _nextSpawnAttemptAt = Game.GameTime + 5000;
                    if (!EnsureWagon(target)) _vehicleSpawnFailures++;
                    else _vehicleSpawnFailures = 0;
                }
                return;
            }

            if (objectiveType == MissionObjectiveType.HuntAnimal)
            {
                if (!definition.SpawnEncounter && _faction.MissionConvoyEnemyHandles.Count == 0) return;
                if (distance <= Math.Max(55f, definition.ObjectiveRadius + 25f) &&
                    Game.GameTime >= _nextSpawnAttemptAt && !_faction.MissionEncounterSpawned)
                {
                    _nextSpawnAttemptAt = Game.GameTime + 5000;
                    SpawnGuards(player, target, objectiveType);
                    if (_faction.MissionConvoyEnemyHandles.Count > 0)
                    {
                        _faction.MissionEncounterSpawned = true;
                        _faction.MissionEncounterProcessId = Process.GetCurrentProcess().Id;
                        _spawnFailures = 0;
                        SaveFaction();
                    }
                    else
                    {
                        _spawnFailures++;
                    }
                }
                return;
            }

            if (objectiveType != MissionObjectiveType.RecoverSupplies &&
                objectiveType != MissionObjectiveType.HoldArea &&
                objectiveType != MissionObjectiveType.InterceptConvoy &&
                objectiveType != MissionObjectiveType.EliminateTargets)
                return;

            if (objectiveType != MissionObjectiveType.EliminateTargets && !definition.SpawnEncounter)
                return;

            float triggerDistance = objectiveType == MissionObjectiveType.InterceptConvoy
                ? 115f
                : (objectiveType == MissionObjectiveType.HoldArea
                    ? definition.AreaRadius + 10f
                    : Math.Max(55f, definition.ObjectiveRadius + 25f));
            if (distance > triggerDistance) return;
            if (objectiveType == MissionObjectiveType.HoldArea &&
                _faction.MissionWaitProgressMs < definition.EncounterAtSeconds * 1000)
                return;

            int processId = Process.GetCurrentProcess().Id;
            if (_faction.MissionEncounterProcessId != 0 && _faction.MissionEncounterProcessId != processId)
            {
                _faction.MissionConvoyWagonHandle = 0;
                _faction.MissionConvoyEnemyHandles.Clear();
                _faction.MissionEncounterSpawned = false;
                _faction.MissionEncounterProcessId = 0;
                _spawnFailures = 0;
                SaveFaction();
            }
            if (_faction.MissionEncounterSpawned || Game.GameTime < _nextSpawnAttemptAt) return;

            _nextSpawnAttemptAt = Game.GameTime + 5000;
            if (objectiveType == MissionObjectiveType.InterceptConvoy) EnsureWagon(target);
            SpawnGuards(player, target, objectiveType);
            if (_faction.MissionConvoyEnemyHandles.Count > 0)
            {
                _faction.MissionEncounterSpawned = true;
                _faction.MissionEncounterProcessId = processId;
                _spawnFailures = 0;
                SaveFaction();
                Log.Write("Mission encounter created. Type=" + definition.Key + ", guards=" +
                    _faction.MissionConvoyEnemyHandles.Count + ", difficulty=" + _faction.MissionDifficulty + ".");
            }
            else
            {
                _spawnFailures++;
                Log.Write("Mission encounter failed to create guards (attempt " + _spawnFailures + "); retrying near target.");
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
        internal Vehicle MissionTargetVehicle { get { return ResolveWagon(MissionTarget, 250f); } }

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
            return IsTargetVehicleDestroyedOrMissing();
        }

        internal bool IsTargetVehicleDestroyedOrMissing()
        {
            if (_faction.MissionConvoyWagonHandle == 0)
                return _vehicleSpawnFailures >= 3;
            try
            {
                Vehicle targetVehicle = ResolveWagon(MissionTarget, 250f);
                if (targetVehicle == null || !targetVehicle.Exists()) return true;
                return targetVehicle.IsDead || targetVehicle.Health <= 0 ||
                    targetVehicle.BodyHealth <= 0f || targetVehicle.IsOnFire;
            }
            catch (Exception ex)
            {
                Log.Write("Mission target vehicle damage check warning: " + ex.Message);
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
                MissionDefinition definition = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);
                string modelName = string.IsNullOrWhiteSpace(definition.TargetVehicleModel)
                    ? ConvoyWagonModel : definition.TargetVehicleModel;
                Model model = new Model(modelName);
                if (!model.IsValid || !model.IsVehicle)
                {
                    Log.Write("Mission target vehicle model is invalid: " + modelName + ".");
                    return false;
                }
                if (!model.IsLoaded && !model.Request(1500))
                {
                    Log.Write("Mission target vehicle model did not load: " + modelName + ".");
                    return false;
                }
                double angle = definition.TargetHeading != 0f
                    ? definition.TargetHeading * Math.PI / 180.0
                    : (_faction.MissionsCompleted + 1) * 1.17;
                Vector3 position = new Vector3(target.X + (float)Math.Cos(angle) * 4.5f,
                    target.Y + (float)Math.Sin(angle) * 4.5f, target.Z + 0.25f);
                VehicleHash wagonHash = (VehicleHash)Enum.ToObject(typeof(VehicleHash), MISC.GET_HASH_KEY(modelName));
                Vehicle wagon = World.CreateVehicle(wagonHash, position,
                    definition.TargetHeading != 0f ? definition.TargetHeading : (float)((angle * 180.0 / Math.PI + 90.0) % 360.0));
                if (wagon == null || !wagon.Exists())
                {
                    Log.Write("Mission target vehicle could not be created at " + position + ".");
                    return false;
                }
                ENTITY.SET_ENTITY_AS_MISSION_ENTITY(wagon.Handle, true, true);
                _faction.MissionConvoyWagonHandle = wagon.Handle;
                _vehicleSpawnFailures = 0;
                SaveFaction();
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Mission target vehicle creation warning: " + ex.Message);
                return false;
            }
        }

        private void SpawnGuards(Ped player, Vector3 target, MissionObjectiveType objectiveType)
        {
            MissionDefinition definition = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);
            bool hunting = objectiveType == MissionObjectiveType.HuntAnimal;
            int defaultCount = hunting ? definition.TargetCount :
                (objectiveType == MissionObjectiveType.InterceptConvoy || objectiveType == MissionObjectiveType.HoldArea
                    ? 2 + Math.Max(0, Math.Min(2, _faction.MissionDifficulty))
                    : 2 + Math.Max(0, Math.Min(1, _faction.MissionDifficulty)));
            int desired = definition.GuardCount > 0 ? definition.GuardCount : defaultCount;
            desired = Math.Max(1, Math.Min(12, desired));

            for (int i = 0; i < desired; i++)
            {
                double angle = Math.PI * 2.0 * i / desired + _faction.MissionsCompleted * 0.43;
                float radius = definition.AreaRadius > 0f
                    ? Math.Min(30f, Math.Max(5f, definition.AreaRadius * 0.25f))
                    : 9f;
                radius += (i % 2) * 3.5f;
                Vector3 position = new Vector3(target.X + (float)Math.Cos(angle) * radius,
                    target.Y + (float)Math.Sin(angle) * radius, target.Z + 0.25f);
                string modelName;
                if (hunting)
                    modelName = string.IsNullOrWhiteSpace(definition.TargetPedModel) ? "A_C_Wolf" : definition.TargetPedModel;
                else if (definition.EnemyModels != null && definition.EnemyModels.Count > 0)
                    modelName = definition.EnemyModels[i % definition.EnemyModels.Count];
                else
                    modelName = GuardModels[i % GuardModels.Length];

                try
                {
                    uint hash = MISC.GET_HASH_KEY(modelName);
                    PedHash pedHash = (PedHash)Enum.ToObject(typeof(PedHash), hash);
                    Model model = new Model(pedHash);
                    if (!model.IsValid || !model.IsPed) { Log.Write("Mission target ped model is invalid: " + modelName + "."); continue; }
                    if (!model.IsLoaded && !model.Request(1500)) { Log.Write("Mission target ped model did not load: " + modelName + "."); continue; }
                    Ped targetPed = World.CreatePed(pedHash, position, (float)((angle * 180.0 / Math.PI + 180.0) % 360.0));
                    if (targetPed == null || !targetPed.Exists()) continue;
                    ENTITY.SET_ENTITY_AS_MISSION_ENTITY(targetPed.Handle, true, true);
                    if (hunting)
                    {
                        targetPed.BlockPermanentEvents = true;
                    }
                    else
                    {
                        targetPed.Accuracy = definition.EnemyAccuracy;
                        uint weaponHash = string.IsNullOrWhiteSpace(definition.EnemyWeaponName)
                            ? (uint)eWeapon.RevolverCattleman
                            : MISC.GET_HASH_KEY(definition.EnemyWeaponName);
                        targetPed.Weapons.Give(weaponHash, definition.EnemyAmmo);
                        targetPed.Task.Combat(player, -1);
                    }
                    _faction.MissionConvoyEnemyHandles.Add(targetPed.Handle);
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
                MissionDefinition definition = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);
                string modelName = definition.ObjectiveType == MissionObjectiveType.RescuePrisoner &&
                    !string.IsNullOrWhiteSpace(definition.TargetPedModel)
                    ? definition.TargetPedModel
                    : CourierModel;
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
                MissionDefinition definition = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);
                string modelName = definition.ObjectiveType == MissionObjectiveType.RescuePrisoner &&
                    !string.IsNullOrWhiteSpace(definition.TargetPedModel)
                    ? definition.TargetPedModel
                    : CourierModel;
                if (courier.Exists() &&
                    unchecked((uint)courier.Model.Hash) == MISC.GET_HASH_KEY(modelName) &&
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
                MissionDefinition definition = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);
                string modelName = string.IsNullOrWhiteSpace(definition.TargetVehicleModel)
                    ? ConvoyWagonModel : definition.TargetVehicleModel;
                if (wagon.Exists() && unchecked((uint)wagon.Model.Hash) == MISC.GET_HASH_KEY(modelName) && wagon.Position.DistanceTo(target) <= maxDistance)
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
                MissionDefinition definition = MissionCatalog.GetStep(_faction.MissionType, _faction.MissionStepIndex);
                if (definition.ObjectiveType == MissionObjectiveType.HuntAnimal)
                {
                    string modelName = string.IsNullOrWhiteSpace(definition.TargetPedModel) ? "A_C_Wolf" : definition.TargetPedModel;
                    if (hash == MISC.GET_HASH_KEY(modelName)) return guard;
                }
                else
                {
                    if (definition.EnemyModels != null)
                        foreach (string model in definition.EnemyModels) if (hash == MISC.GET_HASH_KEY(model)) return guard;
                    foreach (string model in GuardModels) if (hash == MISC.GET_HASH_KEY(model)) return guard;
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
