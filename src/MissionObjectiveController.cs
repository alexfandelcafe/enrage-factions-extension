using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        private const string ConvoyDriverModel = "G_M_M_UniCriminals_02";
        private const ulong TaskEnterVehicleHash = 0xC20E50AA46D09CA8UL;
        private const ulong TaskVehicleDriveToCoordHash = 0xE2A2AA2F659D77A7UL;
        private const string CourierModel = "U_M_M_RhdBartender_01";
        // Use a dedicated prisoner model for the rescue contract rather than reusing the courier.
        private const string PrisonerModel = "U_M_M_AsbPrisoner_01";
        private const string HuntingAnimalModel = "A_C_Wolf";
        private const string EnemyBlipStyle = "BLIP_STYLE_ENEMY";
        private static readonly string[] GuardModels = { "G_M_M_UniCriminals_01", "G_M_M_UniCriminals_02", "G_M_M_UniDuster_01" };
        private readonly PlayerFactionData _faction;
        private int _nextSpawnAttemptAt;
        private int _spawnFailures;
        private int _nextCourierSpawnAttemptAt;
        private int _courierSpawnFailures;
        private int _nextVehicleSpawnAttemptAt;
        private int _vehicleSpawnFailures;
        private int _nextConvoyDriverSpawnAt;
        private int _convoyDriverSpawnFailures;
        private int _nextConvoyDriverTaskAt;
        private readonly Dictionary<int, int> _enemyBlips = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _missingEnemySince = new Dictionary<int, int>();
        private readonly HashSet<int> _lostEnemyHandles = new HashSet<int>();
        private bool _stealthAlerted;

        internal MissionObjectiveController(PlayerFactionData faction) { _faction = faction; }

        internal bool EncounterSpawned
        {
            get { return _faction.MissionEncounterSpawned && _faction.MissionConvoyEnemyHandles != null && _faction.MissionConvoyEnemyHandles.Count > 0; }
        }

        internal bool ConvoySpawned { get { return EncounterSpawned; } }
        internal bool EncounterSpawnFailed { get { return _spawnFailures >= 3 && !_faction.MissionEncounterSpawned; } }
        internal bool EncounterHadLostTargets { get { return _lostEnemyHandles.Count > 0; } }
        internal Ped CourierPed { get { return ResolveCourier(MissionTarget, _faction.MissionStage >= 2 ? 600f : 125f); } }
        internal Ped RescuedPrisoner { get { return _faction.MissionType == MissionRescuePrisoner ? ResolveCourier(MissionTarget, 600f) : null; } }
        internal bool CourierAvailable
        {
            get { Ped courier = ResolveCourier(MissionTarget, _faction.MissionStage >= 2 ? 600f : 125f); return courier != null && courier.Exists(); }
        }
        internal bool CourierSpawnFailed { get { return _courierSpawnFailures >= 3; } }
        internal bool TargetVehicleAvailable
        {
            get { Vehicle vehicle = ResolveWagon(MissionTarget, 250f); return vehicle != null && vehicle.Exists(); }
        }
        internal bool TargetVehicleSpawnFailed { get { return _vehicleSpawnFailures >= 3; } }

        internal void ReconcileProcessScopedHandles()
        {
            int processId = Process.GetCurrentProcess().Id;
            if (_faction.MissionEncounterProcessId == processId ||
                (_faction.MissionEncounterProcessId == 0 && !HasMissionRuntimeHandles()))
                return;

            // A different process cannot safely reuse saved game entity handles. Reset only the
            // transient world entities; the mission stage/objective progress itself remains persisted.
            RemoveAllEnemyBlips();
            _faction.MissionConvoyWagonHandle = 0;
            _faction.MissionConvoyDriverPedHandle = 0;
            _faction.MissionCourierPedHandle = 0;
            _faction.MissionCachePropHandle = 0;
            _faction.MissionCluePropHandle = 0;
            if (_faction.MissionScenePropHandles != null) _faction.MissionScenePropHandles.Clear();
            if (_faction.MissionConvoyEnemyHandles == null)
                _faction.MissionConvoyEnemyHandles = new List<int>();
            else
                _faction.MissionConvoyEnemyHandles.Clear();
            _faction.MissionEncounterSpawned = false;
            _faction.MissionEncounterProcessId = 0;
            _spawnFailures = 0;
            _vehicleSpawnFailures = 0;
            _courierSpawnFailures = 0;
            _convoyDriverSpawnFailures = 0;
            _stealthAlerted = false;
            SaveFaction();
        }

        internal void Maintain(Ped player)
        {
            if (player == null || !player.Exists() || _faction.MissionStage != 1) return;
            Vector3 target = MissionTarget;
            if (target == Vector3.Zero) return;

            ReconcileProcessScopedHandles();
            MaintainEnemyBlips(target);
            int missionType = _faction.MissionType;
            float distance = player.Position.DistanceTo(target);
            int processId = Process.GetCurrentProcess().Id;

            if (missionType == MissionEliminateTargets || missionType == MissionDestroyTarget)
                MaintainStealthEncounter(player, target);

            if (missionType == MissionCourier || missionType == MissionRescuePrisoner)
            {
                if (distance <= 85f && Game.GameTime >= _nextCourierSpawnAttemptAt &&
                    !CourierAvailable && _courierSpawnFailures < 3)
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

            // Hunting is a three-stage contract: do not spawn the pack until both clues are found.
            if (missionType == MissionHuntAnimal && _faction.MissionObjectiveIndex < 2) return;

            float triggerDistance = missionType == MissionConvoy ? 115f :
                (missionType == MissionDestroyTarget ? 100f :
                (missionType == MissionStakeout ? 40f :
                (missionType == MissionHuntAnimal ? 75f : 65f)));
            if (distance > triggerDistance) return;
            if (missionType == MissionStakeout && _faction.MissionWaitProgressMs < 8000) return;

            // A stale vehicle handle is not a valid vehicle; clear it so the bounded spawn retry can run.
            if ((missionType == MissionConvoy || missionType == MissionDestroyTarget) &&
                _faction.MissionConvoyWagonHandle != 0 && ResolveWagon(target, 250f) == null)
            {
                _faction.MissionConvoyWagonHandle = 0;
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

            if (missionType == MissionConvoy)
            {
                Vehicle convoyWagon = ResolveWagon(target, 250f);
                if (convoyWagon != null && convoyWagon.Exists())
                    MaintainConvoyDriver(target, convoyWagon);
            }

            if (_faction.MissionEncounterSpawned || _spawnFailures >= 3 || Game.GameTime < _nextSpawnAttemptAt) return;

            _nextSpawnAttemptAt = Game.GameTime + 5000;
            if (_faction.MissionConvoyEnemyHandles == null)
                _faction.MissionConvoyEnemyHandles = new List<int>();

            int desiredTargets = DesiredGuardCount(missionType, _faction.MissionDifficulty);
            int existingTargets = _faction.MissionConvoyEnemyHandles.Count;
            if (existingTargets < desiredTargets)
                SpawnGuards(player, target, missionType, existingTargets, desiredTargets);

            if (_faction.MissionConvoyEnemyHandles.Count >= desiredTargets)
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
                if (_spawnFailures >= 3 && _faction.MissionConvoyEnemyHandles.Count > 0)
                {
                    // Accept a smaller, but real encounter after three partial-spawn attempts.
                    // This prevents soft-locks while avoiding a success state with zero enemies.
                    _faction.MissionEncounterSpawned = true;
                    _faction.MissionEncounterProcessId = processId;
                    SaveFaction();
                    Log.Write("Mission encounter accepted as partial after retries. Type=" + missionType +
                        ", spawned=" + _faction.MissionConvoyEnemyHandles.Count + "/" + desiredTargets + ".");
                }
                else
                {
                    Log.Write("Mission encounter spawn incomplete (attempt " + _spawnFailures +
                        "): spawned=" + _faction.MissionConvoyEnemyHandles.Count + "/" + desiredTargets +
                        "; retrying near target.");
                }
            }
        }

        internal bool AllGuardsDefeated()
        {
            if (!EncounterSpawned || _faction.MissionConvoyEnemyHandles == null ||
                _faction.MissionConvoyEnemyHandles.Count == 0) return false;

            bool unresolvedWithinGrace = false;
            foreach (int handle in _faction.MissionConvoyEnemyHandles)
            {
                Ped guard = ResolveGuard(handle, MissionTarget);
                if (guard != null && guard.Exists())
                {
                    _missingEnemySince.Remove(handle);
                    _lostEnemyHandles.Remove(handle);
                    if (!guard.IsDead) return false;
                    continue;
                }

                // An invalid handle is not instantly treated as a kill. Give streaming/AI a
                // short grace period, then record it as lost so an encounter cannot soft-lock.
                int firstMissingAt;
                if (!_missingEnemySince.TryGetValue(handle, out firstMissingAt))
                {
                    _missingEnemySince[handle] = Game.GameTime;
                    unresolvedWithinGrace = true;
                    continue;
                }
                if (Game.GameTime - firstMissingAt < 10000)
                {
                    unresolvedWithinGrace = true;
                    continue;
                }
                if (_lostEnemyHandles.Add(handle))
                    Log.Write("Mission target handle " + handle + " disappeared and was marked lost after the grace period.");
            }
            return !unresolvedWithinGrace;
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

        internal void CleanupConvoy(bool preserveCourier = false)
        {
            bool changed = _faction.MissionConvoyWagonHandle != 0 ||
                _faction.MissionConvoyDriverPedHandle != 0 ||
                _faction.MissionCourierPedHandle != 0 || _enemyBlips.Count > 0 ||
                (_faction.MissionConvoyEnemyHandles != null && _faction.MissionConvoyEnemyHandles.Count > 0) ||
                _faction.MissionEncounterSpawned || _faction.MissionEncounterProcessId != 0;
            RemoveAllEnemyBlips();
            Vector3 target = MissionTarget;
            Vehicle wagon = ResolveWagon(target, 350f);
            Ped convoyDriver = ResolveConvoyDriver(target, 350f);
            Ped courier = preserveCourier ? null : ResolveCourier(target, 600f);
            try { if (wagon != null && wagon.Exists()) wagon.Delete(); }
            catch (Exception ex) { Log.Write("Convoy wagon cleanup warning: " + ex.Message); }
            try { if (convoyDriver != null && convoyDriver.Exists()) convoyDriver.Delete(); }
            catch (Exception ex) { Log.Write("Convoy driver cleanup warning: " + ex.Message); }
            try { if (courier != null && courier.Exists()) courier.Delete(); }
            catch (Exception ex) { Log.Write("Courier cleanup warning: " + ex.Message); }
            if (_faction.MissionConvoyEnemyHandles != null)
            {
                foreach (int handle in _faction.MissionConvoyEnemyHandles)
                {
                    if (handle == _faction.MissionConvoyDriverPedHandle) continue;
                    Ped guard = ResolveGuard(handle, target);
                    try { if (guard != null && guard.Exists()) guard.Delete(); }
                    catch (Exception ex) { Log.Write("Convoy guard cleanup warning: " + ex.Message); }
                }
            }
            _faction.MissionConvoyWagonHandle = 0;
            _faction.MissionConvoyDriverPedHandle = 0;
            if (!preserveCourier) _faction.MissionCourierPedHandle = 0;
            if (_faction.MissionConvoyEnemyHandles == null) _faction.MissionConvoyEnemyHandles = new List<int>();
            else _faction.MissionConvoyEnemyHandles.Clear();
            _faction.MissionEncounterSpawned = false;
            _faction.MissionEncounterProcessId = preserveCourier && _faction.MissionCourierPedHandle != 0
                ? Process.GetCurrentProcess().Id
                : 0;
            _missingEnemySince.Clear();
            _lostEnemyHandles.Clear();
            _stealthAlerted = false;
            _nextSpawnAttemptAt = 0;
            _nextVehicleSpawnAttemptAt = 0;
            _nextConvoyDriverSpawnAt = 0;
            _nextConvoyDriverSpawnFailures = 0;
            _nextConvoyDriverTaskAt = 0;
            _nextCourierSpawnAttemptAt = 0;
            _courierSpawnFailures = 0;
            _vehicleSpawnFailures = 0;
            _spawnFailures = 0;
            if (changed) SaveFaction();
        }

        private bool HasMissionRuntimeHandles()
        {
            return _faction.MissionConvoyWagonHandle != 0 ||
                _faction.MissionConvoyDriverPedHandle != 0 ||
                _faction.MissionCourierPedHandle != 0 ||
                _faction.MissionCachePropHandle != 0 ||
                _faction.MissionCluePropHandle != 0 ||
                _faction.MissionEncounterSpawned ||
                (_faction.MissionConvoyEnemyHandles != null && _faction.MissionConvoyEnemyHandles.Count > 0) ||
                (_faction.MissionScenePropHandles != null &&
                    _faction.MissionScenePropHandles.Any(handle => handle != 0));
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
                Vector3 position;
                float heading;
                if (_faction.MissionTargetHasRoadNode &&
                    (_faction.MissionType == MissionConvoy || _faction.MissionType == MissionDestroyTarget))
                {
                    double roadAngle = _faction.MissionTargetHeading * Math.PI / 180.0;
                    double sideOffset = roadAngle + Math.PI / 2.0;
                    float longitudinalOffset = _faction.MissionType == MissionConvoy ? -48f : 0f;
                    float lateralOffset = _faction.MissionType == MissionConvoy ? 1.2f : 4.5f;
                    position = new Vector3(
                        target.X + (float)Math.Cos(roadAngle) * longitudinalOffset + (float)Math.Cos(sideOffset) * lateralOffset,
                        target.Y + (float)Math.Sin(roadAngle) * longitudinalOffset + (float)Math.Sin(sideOffset) * lateralOffset,
                        target.Z + 0.25f);
                    heading = (_faction.MissionTargetHeading % 360f + 360f) % 360f;
                }
                else
                {
                    position = new Vector3(target.X + (float)Math.Cos(angle) * 4.5f,
                        target.Y + (float)Math.Sin(angle) * 4.5f, target.Z + 0.25f);
                    heading = (float)((angle * 180.0 / Math.PI + 90.0) % 360.0);
                }
                VehicleHash wagonHash = (VehicleHash)Enum.ToObject(typeof(VehicleHash), MISC.GET_HASH_KEY(ConvoyWagonModel));
                Vehicle wagon = World.CreateVehicle(wagonHash, position, heading);
                if (wagon == null || !wagon.Exists()) { Log.Write("Mission target wagon could not be created at " + position + "."); return false; }
                ENTITY.SET_ENTITY_AS_MISSION_ENTITY(wagon.Handle, true, true);
                try { VEHICLE.SET_VEHICLE_ON_GROUND_PROPERLY(wagon.Handle, false); } catch { }
                _faction.MissionConvoyWagonHandle = wagon.Handle;
                _faction.MissionEncounterProcessId = Process.GetCurrentProcess().Id;
                SaveFaction();
                Log.Write("Mission target wagon spawned for contract " + _faction.MissionType + ". Handle=" + wagon.Handle + ".");
                return true;
            }
            catch (Exception ex) { Log.Write("Mission wagon creation warning: " + ex.Message); return false; }
        }

        private static int DesiredGuardCount(int missionType, int difficulty)
        {
            difficulty = Math.Max(0, Math.Min(2, difficulty));
            if (missionType == MissionHuntAnimal) return 2 + Math.Min(1, difficulty);
            if (missionType == MissionEliminateTargets) return 3 + difficulty;
            if (missionType == MissionDestroyTarget || missionType == MissionRescuePrisoner ||
                missionType == MissionConvoy || missionType == MissionStakeout) return 2 + difficulty;
            return 2 + Math.Min(1, difficulty);
        }

        private Ped ResolveConvoyDriver(Vector3 target, float maxDistance = 250f)
        {
            if (_faction.MissionConvoyDriverPedHandle == 0 || target == Vector3.Zero) return null;
            try
            {
                Ped driver = new Ped(_faction.MissionConvoyDriverPedHandle);
                if (driver.Exists() &&
                    unchecked((uint)driver.Model.Hash) == MISC.GET_HASH_KEY(ConvoyDriverModel) &&
                    driver.Position.DistanceTo(target) <= maxDistance)
                    return driver;
            }
            catch { }
            return null;
        }

        private void MaintainConvoyDriver(Vector3 target, Vehicle wagon)
        {
            if (wagon == null || !wagon.Exists() || !_faction.MissionTargetHasRoadNode) return;
            Ped driver = ResolveConvoyDriver(target, 350f);
            if (driver == null && _faction.MissionConvoyDriverPedHandle != 0)
            {
                _faction.MissionConvoyDriverPedHandle = 0;
                SaveFaction();
            }
            if (driver == null)
            {
                if (_convoyDriverSpawnFailures >= 3 || Game.GameTime < _nextConvoyDriverSpawnAt) return;
                _nextConvoyDriverSpawnAt = Game.GameTime + 3500;
                try
                {
                    uint hash = MISC.GET_HASH_KEY(ConvoyDriverModel);
                    PedHash pedHash = (PedHash)Enum.ToObject(typeof(PedHash), hash);
                    Model model = new Model(pedHash);
                    if (!model.IsValid || !model.IsPed || (!model.IsLoaded && !model.Request(1200)))
                    {
                        _convoyDriverSpawnFailures++;
                        Log.Write("Convoy driver model unavailable (attempt " + _convoyDriverSpawnFailures + ").");
                        return;
                    }
                    Vector3 start = wagon.Position;
                    Ped created = World.CreatePed(pedHash,
                        new Vector3(start.X + 1.8f, start.Y + 1.8f, start.Z + 0.2f),
                        _faction.MissionTargetHeading);
                    if (created == null || !created.Exists())
                    {
                        _convoyDriverSpawnFailures++;
                        Log.Write("Convoy driver ped could not be created (attempt " + _convoyDriverSpawnFailures + ").");
                        return;
                    }
                    ENTITY.SET_ENTITY_AS_MISSION_ENTITY(created.Handle, true, true);
                    created.BlockPermanentEvents = true;
                    created.Accuracy = 15;
                    _faction.MissionConvoyDriverPedHandle = created.Handle;
                    if (_faction.MissionConvoyEnemyHandles == null)
                        _faction.MissionConvoyEnemyHandles = new List<int>();
                    if (!_faction.MissionConvoyEnemyHandles.Contains(created.Handle))
                        _faction.MissionConvoyEnemyHandles.Add(created.Handle);
                    _faction.MissionEncounterProcessId = Process.GetCurrentProcess().Id;
                    driver = created;
                    EnsureEnemyBlip(created);
                    SaveFaction();
                    Log.Write("Moving convoy driver spawned. Handle=" + created.Handle + ", wagon=" + wagon.Handle + ".");
                }
                catch (Exception ex)
                {
                    _convoyDriverSpawnFailures++;
                    Log.Write("Convoy driver spawn warning (attempt " + _convoyDriverSpawnFailures + "): " + ex.Message);
                    return;
                }
            }

            if (driver == null || !driver.Exists() || driver.IsDead ||
                Game.GameTime < _nextConvoyDriverTaskAt) return;
            try
            {
                bool seated = PED.IS_PED_IN_VEHICLE(driver.Handle, wagon.Handle, false);
                if (!seated)
                {
                    _nextConvoyDriverTaskAt = Game.GameTime + 2500;
                    Function.Call(TaskEnterVehicleHash, new InputArgument[]
                    {
                        driver.Handle, wagon.Handle, 10000, -1, 1.0f, 1, 0
                    });
                    return;
                }

                double roadAngle = _faction.MissionTargetHeading * Math.PI / 180.0;
                Vector3 destination = new Vector3(
                    target.X + (float)Math.Cos(roadAngle) * 16f,
                    target.Y + (float)Math.Sin(roadAngle) * 16f,
                    target.Z);
                _nextConvoyDriverTaskAt = Game.GameTime + 8500;
                if (wagon.Position.DistanceTo(destination) > 9f)
                {
                    Function.Call(TaskVehicleDriveToCoordHash, new InputArgument[]
                    {
                        driver.Handle, wagon.Handle, 8.0f,
                        destination.X, destination.Y, destination.Z,
                        3.5f, 0, MISC.GET_HASH_KEY(ConvoyWagonModel), 786603
                    });
                    Log.Write("Convoy driver ordered along roadside route toward mission point.");
                }
            }
            catch (Exception ex)
            {
                Log.Write("Convoy driver task warning: " + ex.Message);
            }
        }

        private void SpawnGuards(Ped player, Vector3 target, int missionType, int startIndex, int desired)
        {
            int difficulty = Math.Max(0, Math.Min(2, _faction.MissionDifficulty));
            for (int i = startIndex; i < desired; i++)
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
                    _faction.MissionEncounterProcessId = Process.GetCurrentProcess().Id;
                    SaveFaction();
                    EnsureEnemyBlip(targetPed);
                    bool stealthContract = (missionType == MissionEliminateTargets || missionType == MissionDestroyTarget) &&
                        _faction.MissionOptionalBonusPercent > 0;
                    if (hunting)
                    {
                        targetPed.Task.Combat(player, -1);
                    }
                    else
                    {
                        targetPed.Accuracy = 20 + difficulty * 12;
                        uint weaponHash;
                        if (difficulty == 0)
                            weaponHash = MISC.GET_HASH_KEY("WEAPON_REVOLVER_CATTLEMAN");
                        else if (difficulty == 1 && i % 2 == 0)
                            weaponHash = MISC.GET_HASH_KEY("WEAPON_REPEATER_CARBINE");
                        else if (difficulty >= 2 && i % 3 == 0)
                            weaponHash = MISC.GET_HASH_KEY("WEAPON_SHOTGUN_DOUBLEBARREL");
                        else if (difficulty >= 2)
                            weaponHash = MISC.GET_HASH_KEY("WEAPON_REPEATER_WINCHESTER");
                        else
                            weaponHash = MISC.GET_HASH_KEY("WEAPON_REVOLVER_CATTLEMAN");
                        targetPed.Weapons.Give(weaponHash, 36 + difficulty * 18);
                        if (stealthContract)
                        {
                            targetPed.BlockPermanentEvents = true;
                            TASK.TASK_STAND_STILL(targetPed.Handle, -1);
                        }
                        else
                        {
                            targetPed.BlockPermanentEvents = false;
                            targetPed.Task.Combat(player, -1);
                        }
                    }
                }
                catch (Exception ex) { Log.Write("Mission target spawn warning for " + modelName + ": " + ex.Message); }
            }
        }

        private void MaintainStealthEncounter(Ped player, Vector3 target)
        {
            if (player == null || !player.Exists() || _faction.MissionStage != 1 ||
                _faction.MissionOptionalBonusPercent <= 0 || _stealthAlerted ||
                _faction.MissionConvoyEnemyHandles == null ||
                _faction.MissionConvoyEnemyHandles.Count == 0)
                return;

            bool alerted = false;
            try { alerted = PED.IS_PED_SHOOTING(player.Handle); }
            catch { }

            if (!alerted)
            {
                foreach (int handle in _faction.MissionConvoyEnemyHandles)
                {
                    Ped guard = ResolveGuard(handle, target);
                    if (guard == null || !guard.Exists() || guard.IsDead) continue;
                    float distance;
                    try { distance = guard.Position.DistanceTo(player.Position); }
                    catch { continue; }
                    if (distance > 32f) continue;

                    try
                    {
                        if (ENTITY.HAS_ENTITY_CLEAR_LOS_TO_ENTITY(guard.Handle, player.Handle, 17))
                        {
                            alerted = true;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (!alerted) return;

            _stealthAlerted = true;
            _faction.MissionOptionalBonusPercent = 0;
            SaveFaction();
            Log.Write("Mission stealth objective failed: target group detected the player or the player fired.");
            foreach (int handle in _faction.MissionConvoyEnemyHandles)
            {
                Ped guard = ResolveGuard(handle, target);
                if (guard == null || !guard.Exists() || guard.IsDead) continue;
                try
                {
                    guard.BlockPermanentEvents = false;
                    guard.Task.Combat(player, -1);
                }
                catch (Exception ex)
                {
                    Log.Write("Stealth encounter alert task warning for ped " + handle + ": " + ex.Message);
                }
            }
        }

        private void MaintainEnemyBlips(Vector3 target)
        {
            List<int> staleHandles = new List<int>();
            foreach (KeyValuePair<int, int> entry in _enemyBlips)
            {
                bool stillTracked = false;
                if (_faction.MissionConvoyEnemyHandles != null)
                {
                    foreach (int handle in _faction.MissionConvoyEnemyHandles)
                    {
                        if (handle == entry.Key) { stillTracked = true; break; }
                    }
                }

                Ped guard = stillTracked ? ResolveGuard(entry.Key, target) : null;
                if (!stillTracked || guard == null || !guard.Exists() || guard.IsDead || !EnemyBlipExists(entry.Value))
                {
                    RemoveEnemyBlip(entry.Value);
                    staleHandles.Add(entry.Key);
                }
            }

            foreach (int staleHandle in staleHandles) _enemyBlips.Remove(staleHandle);
            if (_faction.MissionConvoyEnemyHandles == null) return;

            foreach (int handle in _faction.MissionConvoyEnemyHandles)
            {
                if (_enemyBlips.ContainsKey(handle)) continue;
                Ped guard = ResolveGuard(handle, target);
                if (guard != null && guard.Exists() && !guard.IsDead)
                    EnsureEnemyBlip(guard);
            }
        }

        private void EnsureEnemyBlip(Ped guard)
        {
            if (guard == null || !guard.Exists() || guard.IsDead) return;
            int existing;
            if (_enemyBlips.TryGetValue(guard.Handle, out existing) && EnemyBlipExists(existing)) return;

            try
            {
                uint styleHash = MISC.GET_HASH_KEY(EnemyBlipStyle);
                int blip = MAP.BLIP_ADD_FOR_ENTITY(styleHash, guard.Handle);
                if (blip == 0 || !MAP.DOES_BLIP_EXIST(blip))
                {
                    Log.Write("Mission enemy blip creation returned no handle for ped " + guard.Handle + ".");
                    return;
                }

                _enemyBlips[guard.Handle] = blip;
                Log.Write("Mission enemy blip created. Ped=" + guard.Handle + ", blip=" + blip + ".");
            }
            catch (Exception ex)
            {
                Log.Write("Mission enemy blip creation warning for ped " + guard.Handle + ": " + ex.Message);
            }
        }

        private bool EnemyBlipExists(int handle)
        {
            try { return handle != 0 && MAP.DOES_BLIP_EXIST(handle); }
            catch { return false; }
        }

        private unsafe void RemoveEnemyBlip(int handle)
        {
            if (handle == 0) return;
            try
            {
                if (!MAP.DOES_BLIP_EXIST(handle)) return;
                int nativeHandle = handle;
                MAP.REMOVE_BLIP(&nativeHandle);
            }
            catch (Exception ex) { Log.Write("Mission enemy blip cleanup warning: " + ex.Message); }
        }

        private void RemoveAllEnemyBlips()
        {
            if (_enemyBlips.Count == 0) return;
            List<int> blips = new List<int>(_enemyBlips.Values);
            _enemyBlips.Clear();
            foreach (int blip in blips) RemoveEnemyBlip(blip);
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
                string modelName = GetContactModel();
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
                if (_faction.MissionType == MissionRescuePrisoner)
                {
                    // Keep the rescued person physically distinct from the delivery contact.
                    TASK.TASK_START_SCENARIO_IN_PLACE_HASH(courier.Handle,
                        MISC.GET_HASH_KEY("WORLD_HUMAN_SIT_GROUND"), -1, true, 0U, -1f, false);
                }
                _faction.MissionCourierPedHandle = courier.Handle;
                _faction.MissionEncounterProcessId = Process.GetCurrentProcess().Id;
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

        private string GetContactModel()
        {
            return _faction.MissionType == MissionRescuePrisoner ? PrisonerModel : CourierModel;
        }

        private Ped ResolveCourier(Vector3 target, float maxDistance = 125f)
        {
            if (_faction.MissionCourierPedHandle == 0 || target == Vector3.Zero) return null;
            try
            {
                Ped courier = new Ped(_faction.MissionCourierPedHandle);
                if (courier.Exists() &&
                    unchecked((uint)courier.Model.Hash) == MISC.GET_HASH_KEY(GetContactModel()) &&
                    courier.Position.DistanceTo(target) <= maxDistance)
                    return courier;
            }
            catch { }
            return null;
        }

        internal bool ReleaseRescuedPrisoner(Ped player)
        {
            if (player == null || !player.Exists() || player.IsDead) return false;
            Ped prisoner = ResolveCourier(MissionTarget, 600f);
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead) return false;
            try
            {
                prisoner.BlockPermanentEvents = false;
                TASK.CLEAR_PED_TASKS(prisoner.Handle, true, false);
                // TASK_FOLLOW_TO_OFFSET_OF_ENTITY: escort the freed scout back with the player.
                // RDR2's native signature places four task flags before the follow offset.
                Function.Call(0x304AE42E357B8C7EUL, new InputArgument[]
                {
                    prisoner.Handle, player.Handle, false, false, false, false,
                    -0.8f, -1.3f, 0f, 1.0f
                });
                Log.Write("Rescued scout released to follow player. Handle=" + prisoner.Handle + ".");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Rescued scout follow task failed: " + ex.Message);
                return false;
            }
        }

        internal bool RescuedPrisonerReachedCamp(Vector3 campCentre, float maxDistance = 22f)
        {
            Ped prisoner = ResolveCourier(MissionTarget, 600f);
            return prisoner != null && prisoner.Exists() && !prisoner.IsDead &&
                campCentre != Vector3.Zero && prisoner.Position.DistanceTo(campCentre) <= maxDistance;
        }

        internal bool SettleRescuedPrisonerAtCamp(Vector3 campCentre)
        {
            Ped prisoner = ResolveCourier(MissionTarget, 600f);
            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead ||
                campCentre == Vector3.Zero || prisoner.Position.DistanceTo(campCentre) > 30f)
                return false;
            try
            {
                TASK.CLEAR_PED_TASKS(prisoner.Handle, true, false);
                prisoner.BlockPermanentEvents = false;
                _faction.MissionCourierPedHandle = 0;
                SaveFaction();
                Log.Write("Rescued scout reached camp and was released into ambient behaviour. Handle=" + prisoner.Handle + ".");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Rescued scout camp handoff warning: " + ex.Message);
                return false;
            }
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
