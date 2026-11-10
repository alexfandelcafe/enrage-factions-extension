using System;
using System.Collections.Generic;
using System.Reflection;
using RDR2;
using RDR2.Math;
using RDR2.Native;

namespace EnRAGEPlayerFactionExtension
{
    /// <summary>
    /// Mission-specific narrative beats, contextual audio and safe interaction animation attempts.
    /// Optional native calls are isolated so missing assets do not block mission progression.
    /// </summary>
    internal sealed class MissionStoryDirector
    {
        private const ulong PlaySoundFrontendHash = 0x67C540AA08E4A6F5UL;
        private const ulong PrepareMusicEventHash = 0x1E5185B72EF5158AUL;
        private const ulong TriggerMusicEventHash = 0x706D57B0F50DA710UL;
        private const ulong TurnPedToFaceEntityHash = 0x5AD23D40115353ACUL;
        // RDR2 NativeDB CAM/PAD hashes: these are kept isolated from the mission state machine.
        private const ulong CreateCamHash = 0xE72CDBA7F0A02DD6UL;
        private const ulong RenderScriptCamsHash = 0x33281167E4942E4FUL;
        private const ulong DestroyCamHash = 0x4E67E0B6D7FD5145UL;
        private const ulong SetCamActiveHash = 0x87295BCA613800C8UL;
        private const ulong SetCamCoordHash = 0xF9EE7D419EE49DE6UL;
        private const ulong PointCamAtCoordHash = 0x948B39341C3A40C2UL;
        private const ulong SetCamFovHash = 0x27666E5988D9D429UL;
        private const ulong DisableAllControlActionsHash = 0x5F4B6931816E599BUL;
        private const ulong IsDisabledControlJustPressedHash = 0x91AEF906BCA88877UL;
        private const int CinematicSkipReloadControl = unchecked((int)0xE30CD707);
        private const int CinematicSkipAcceptControl = unchecked((int)0xC7B5340A);
        private const int CinematicSkipCancelControl = unchecked((int)0x156F7119);
        private const string CombatMusicStart = "DUEL_GENERAL_START";
        private const string CombatMusicStop = "DUEL_GENERAL_STOP";
        private const string CombatMusicEndStinger = "DUEL_GENERAL_END_OS";

        private readonly Action<string> _notify;
        private readonly Queue<string> _dialogue = new Queue<string>();
        private int _nextDialogueAt;
        private int _dialogueGapMs = 2600;
        private bool _combatMusicActive;
        private bool _combatMusicRequested;
        private int _nextMusicRetryAt;
        private string _activeMusicStopEvent = CombatMusicStop;
        private readonly HashSet<string> _playedCinematics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _cinematicActive;
        private bool _cinematicScenarioStarted;
        private int _cinematicCamera;
        private int _cinematicStartedAt;
        private int _cinematicDurationMs;
        private int _cinematicPlayerHandle;
        private string _activeCinematicKey = string.Empty;
        private Vector3 _cinematicCameraStart;
        private Vector3 _cinematicCameraEnd;
        private Vector3 _cinematicFocusStart;
        private Vector3 _cinematicFocusEnd;

        internal bool IsCinematicActive { get { return _cinematicActive; } }

        internal bool HasPlayedCinematic(string key)
        {
            return !string.IsNullOrWhiteSpace(key) && _playedCinematics.Contains(key);
        }

        internal MissionStoryDirector(Action<string> notify)
        {
            _notify = notify;
        }

        /// <summary>
        /// Starts a short, skippable in-world cinematic. It is deliberately non-blocking:
        /// mission progress continues through the existing state machine while the camera runs.
        /// If the player is mounted, in combat, or camera creation fails, callers can continue
        /// with the ordinary mission flow instead of holding the encounter.
        /// </summary>
        internal bool TryStartCinematic(string key, Ped player, Vector3 focus, string scenario, int durationMs)
        {
            if (string.IsNullOrWhiteSpace(key) || _cinematicActive || _playedCinematics.Contains(key))
                return false;
            if (!CanStartCinematic(player))
                return false;

            _playedCinematics.Add(key);
            if (focus == Vector3.Zero) focus = player.Position;

            _activeCinematicKey = key;
            _cinematicPlayerHandle = player.Handle;
            _cinematicStartedAt = Game.GameTime;
            _cinematicDurationMs = Math.Max(2600, Math.Min(6000, durationMs));

            Vector3 playerPosition = player.Position;
            float directionX = focus.X - playerPosition.X;
            float directionY = focus.Y - playerPosition.Y;
            float directionLength = (float)Math.Sqrt(directionX * directionX + directionY * directionY);
            if (directionLength < 0.05f)
            {
                directionX = 0f;
                directionY = 1f;
                directionLength = 1f;
            }
            directionX /= directionLength;
            directionY /= directionLength;
            float sideX = -directionY;
            float sideY = directionX;

            _cinematicCameraStart = new Vector3(
                playerPosition.X - directionX * 3.6f + sideX * 2.0f,
                playerPosition.Y - directionY * 3.6f + sideY * 2.0f,
                playerPosition.Z + 1.65f);
            _cinematicCameraEnd = new Vector3(
                playerPosition.X - directionX * 2.0f - sideX * 4.1f,
                playerPosition.Y - directionY * 2.0f - sideY * 4.1f,
                playerPosition.Z + 2.05f);
            _cinematicFocusStart = new Vector3(focus.X, focus.Y, focus.Z + 0.45f);
            _cinematicFocusEnd = new Vector3(
                focus.X + directionX * 11f,
                focus.Y + directionY * 11f,
                focus.Z + 1.0f);

            try
            {
                _cinematicCamera = Function.Call<int>(CreateCamHash,
                    new InputArgument[] { "DEFAULT_SCRIPTED_CAMERA", true });
                if (_cinematicCamera == 0)
                    throw new InvalidOperationException("CREATE_CAM returned an invalid camera handle.");

                Function.Call(SetCamCoordHash, new InputArgument[]
                {
                    _cinematicCamera, _cinematicCameraStart.X, _cinematicCameraStart.Y, _cinematicCameraStart.Z
                });
                Function.Call(PointCamAtCoordHash, new InputArgument[]
                {
                    _cinematicCamera, _cinematicFocusStart.X, _cinematicFocusStart.Y, _cinematicFocusStart.Z
                });
                Function.Call(SetCamFovHash, new InputArgument[] { _cinematicCamera, 43f });
                Function.Call(SetCamActiveHash, new InputArgument[] { _cinematicCamera, true });
                Function.Call(RenderScriptCamsHash, new InputArgument[] { true, true, 500, false, true, 0 });

                _cinematicActive = true;
                if (!string.IsNullOrWhiteSpace(scenario))
                {
                    try
                    {
                        TASK.CLEAR_PED_TASKS(player.Handle, true, false);
                        TASK.TASK_START_SCENARIO_IN_PLACE_HASH(player.Handle,
                            MISC.GET_HASH_KEY(scenario), -1, true, 0U, -1f, false);
                        _cinematicScenarioStarted = true;
                    }
                    catch (Exception ex)
                    {
                        // The camera-only version is still valid when a scenario is unavailable.
                        Log.Write("Mission cinematic scenario warning (" + scenario + "): " + ex.Message);
                    }
                }

                Log.Write("Mission cinematic started: " + key + ", scenario=" +
                    (string.IsNullOrWhiteSpace(scenario) ? "(camera only)" : scenario) + ".");
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Mission cinematic could not start (" + key + "): " + ex.Message);
                CleanupCinematic(false);
                return false;
            }
        }

        internal void MaintainCinematic(Ped player)
        {
            if (!_cinematicActive) return;
            try
            {
                if (player == null || !player.Exists() || player.Handle != _cinematicPlayerHandle || player.IsDead)
                {
                    CleanupCinematic(true);
                    return;
                }

                // Controls are disabled for this frame only; the player can skip with Reload,
                // Accept, or Cancel. A script reload/death also has a dedicated cleanup path.
                Function.Call(DisableAllControlActionsHash, new InputArgument[] { 0 });
                bool skip = Function.Call<bool>(IsDisabledControlJustPressedHash,
                    new InputArgument[] { 0, CinematicSkipReloadControl }) ||
                    Function.Call<bool>(IsDisabledControlJustPressedHash,
                    new InputArgument[] { 0, CinematicSkipAcceptControl }) ||
                    Function.Call<bool>(IsDisabledControlJustPressedHash,
                    new InputArgument[] { 0, CinematicSkipCancelControl });

                int elapsed = Game.GameTime - _cinematicStartedAt;
                if (skip || elapsed >= _cinematicDurationMs)
                {
                    CleanupCinematic(true);
                    return;
                }

                float progress = Math.Max(0f, Math.Min(1f, elapsed / (float)_cinematicDurationMs));
                // Smoothstep avoids abrupt changes when the camera begins and ends its pan.
                float eased = progress * progress * (3f - 2f * progress);
                Vector3 camera = Lerp(_cinematicCameraStart, _cinematicCameraEnd, eased);
                Vector3 focus = Lerp(_cinematicFocusStart, _cinematicFocusEnd, eased);
                if (_cinematicCamera != 0)
                {
                    Function.Call(SetCamCoordHash, new InputArgument[]
                    {
                        _cinematicCamera, camera.X, camera.Y, camera.Z
                    });
                    Function.Call(PointCamAtCoordHash, new InputArgument[]
                    {
                        _cinematicCamera, focus.X, focus.Y, focus.Z
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Write("Mission cinematic tick warning; restoring gameplay: " + ex.Message);
                CleanupCinematic(true);
            }
        }

        private static bool CanStartCinematic(Ped player)
        {
            try
            {
                return player != null && player.Exists() && !player.IsDead &&
                    !player.IsOnMount && !player.IsInVehicle && !player.IsInCombat;
            }
            catch
            {
                return false;
            }
        }

        private static Vector3 Lerp(Vector3 from, Vector3 to, float amount)
        {
            return new Vector3(
                from.X + (to.X - from.X) * amount,
                from.Y + (to.Y - from.Y) * amount,
                from.Z + (to.Z - from.Z) * amount);
        }

        private void CleanupCinematic(bool clearPlayerTask)
        {
            int camera = _cinematicCamera;
            _cinematicCamera = 0;
            bool clearScenario = _cinematicScenarioStarted;
            _cinematicScenarioStarted = false;
            int playerHandle = _cinematicPlayerHandle;
            _cinematicPlayerHandle = 0;
            _cinematicActive = false;
            _cinematicStartedAt = 0;
            _cinematicDurationMs = 0;
            _activeCinematicKey = string.Empty;

            if (camera != 0)
            {
                try { Function.Call(RenderScriptCamsHash, new InputArgument[] { false, true, 500, false, false, 0 }); }
                catch (Exception ex) { Log.Write("Mission cinematic camera blend-out warning: " + ex.Message); }
                try { Function.Call(SetCamActiveHash, new InputArgument[] { camera, false }); } catch { }
                try { Function.Call(DestroyCamHash, new InputArgument[] { camera, false }); } catch { }
            }

            if (clearPlayerTask && clearScenario && playerHandle != 0)
            {
                try
                {
                    Ped player = Game.Player != null ? Game.Player.Character : null;
                    if (player != null && player.Exists() && player.Handle == playerHandle)
                        TASK.CLEAR_PED_TASKS(player.Handle, true, false);
                }
                catch (Exception ex) { Log.Write("Mission cinematic task cleanup warning: " + ex.Message); }
            }
        }

        internal void Tick()
        {
            MaintainCombatMusic();
            if (_dialogue.Count == 0 || Game.GameTime < _nextDialogueAt) return;
            string line = _dialogue.Dequeue();
            try
            {
                if (!TryDisplaySubtitle(line))
                    Log.Write("Mission subtitle API unavailable; suppressed enRAGE notification fallback for: " + line);
            }
            catch (Exception ex)
            {
                Log.Write("Mission subtitle display warning; suppressed duplicate notification: " + ex.Message);
            }
            _nextDialogueAt = Game.GameTime + _dialogueGapMs;
        }

        internal void BeginMission(int missionType, int difficulty, Ped missionGiver, Ped player)
        {
            PlayFrontendCue("SELECT");
            FaceEachOther(missionGiver, player);
            QueueDialogue(GetOpeningLines(missionType, difficulty));
        }

        internal void OnCheckpoint(int missionType, int objectiveIndex)
        {
            PlayFrontendCue("NAV_UP_DOWN");
            QueueDialogue(GetCheckpointLines(missionType, objectiveIndex));
        }

        internal void OnLootAvailable(int missionType)
        {
            PlayFrontendCue("SELECT");
            if (missionType == 5)
                QueueDialogue(new[] { "ENCARGADO: La carreta está despejada. Registra el cargamento y trae lo que se pueda salvar." }, true);
            else
                QueueDialogue(new[] { "ENCARGADO: La zona está despejada. Recupera las provisiones y vuelve sin dejar rastro." }, true);
        }

        internal void OnEncounterStarted(int missionType)
        {
            PlayFrontendCue("NAV_UP_DOWN");
            if (missionType == 2 || missionType == 3 || missionType == 5 ||
                missionType == 6 || missionType == 7 || missionType == 8 || missionType == 9)
                StartCombatMusic();
            QueueDialogue(GetEncounterLines(missionType));
        }

        internal void OnCourierConversation(Ped courier, Ped player, bool finalExchange)
        {
            FaceEachOther(courier, player);
            PlayFrontendCue("SELECT");
            QueueDialogue(finalExchange
                ? new[] { "CONTACTO: Bien. Que nadie sepa que nos hemos visto. Lleva el resto de vuelta a tu gente." }
                : new[]
                {
                    "CONTACTO: Creía que mandarían a otro. ¿Traes el despacho?",
                    "Tú: Primero dime si la ruta sigue limpia. No pienso llevar a mi banda a una trampa."
                });
        }

        internal void OnObjectiveInteraction(Ped player, string context)
        {
            // Avoid invoking TASK_PLAY_ANIM from the loot path until its runtime behavior
            // can be validated on the target ScriptHook build. Mission progress is independent.
            PlayFrontendCue("SELECT");
            if (!string.IsNullOrWhiteSpace(context))
                QueueDialogue(new[] { context }, true);
        }

        internal void PlayReminderCue()
        {
            PlayFrontendCue("NAV_UP_DOWN");
        }

        internal void ShowMissionStatus(string line, int duration = 2200)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            try
            {
                if (!TryDisplaySubtitle(line, Math.Max(1200, duration)))
                    Log.Write("Mission status subtitle API unavailable; status line suppressed: " + line);
            }
            catch (Exception ex)
            {
                Log.Write("Mission status subtitle warning: " + ex.Message);
            }
        }


        internal void OnMissionReturning(int missionType, string outcomeMessage)
        {
            StopCombatMusic(true);
            PlayFrontendCue("SELECT");
            if (!string.IsNullOrWhiteSpace(outcomeMessage))
                QueueDialogue(new[] { "ENCARGADO: " + outcomeMessage }, true);
            QueueDialogue(GetReturnLines(missionType), true);
        }

        internal void OnMissionCompleted(int missionType, string completionSummary)
        {
            StopCombatMusic(true);
            PlayFrontendCue("SELECT");
            if (!string.IsNullOrWhiteSpace(completionSummary))
                QueueDialogue(new[] { "ENCARGADO: " + completionSummary }, true);
            QueueDialogue(new[] { "ENCARGADO: Buen trabajo. La banda recordará que has cumplido." }, true);
        }

        internal void OnScriptAborted()
        {
            _dialogue.Clear();
            CleanupCinematic(true);
            StopCombatMusic(false);
            _combatMusicRequested = false;
        }

        private void QueueDialogue(IEnumerable<string> lines, bool append = false)
        {
            bool pendingBeforeAppend = append && _dialogue.Count > 0;
            if (!append) _dialogue.Clear();
            if (lines != null)
                foreach (string line in lines)
                    if (!string.IsNullOrWhiteSpace(line)) _dialogue.Enqueue(line);
            if (!pendingBeforeAppend && _dialogue.Count > 0)
                _nextDialogueAt = Game.GameTime + 1400;
            _dialogueGapMs = 2600;
        }

        private static string[] GetOpeningLines(int missionType, int difficulty)
        {
            string qualifier = difficulty >= 2 ? "No habrá margen para errores." :
                (difficulty == 1 ? "Mantén los ojos abiertos." : "No te precipites.");
            switch (missionType)
            {
                case 1:
                    return new[] {
                        "ENCARGADO: Han visto gente extraña cerca de nuestros caminos. Revisa la frontera y no pierdas de vista las salidas.",
                        "ENCARGADO: Sigue los puntos en orden. Si encuentras señales de una banda, memoriza por dónde se mueve.",
                        "ENCARGADO: " + qualifier
                    };
                case 2:
                    return new[] {
                        "ENCARGADO: Un alijo nuestro quedó atrás y alguien lo está vigilando. Recupera las provisiones, pero no te acerques sin mirar.",
                        "ENCARGADO: No basta con encontrar las cajas. Asegúrate de que nadie pueda seguirte hasta el campamento.",
                        "ENCARGADO: " + qualifier
                    };
                case 3:
                    return new[] {
                        "ENCARGADO: Necesito que vigiles el cruce. Quédate en posición y averigua quién utiliza el camino.",
                        "ENCARGADO: Objetivo opcional: no salgas del perímetro durante la vigilancia para conseguir una bonificación del 15%.",
                        "ENCARGADO: Si te descubren, no abandones el terreno sin resolver la amenaza.",
                        "ENCARGADO: " + qualifier
                    };
                case 4:
                    return new[] {
                        "ENCARGADO: Lleva este despacho al contacto. No lo abras y no se lo entregues al primero que pregunte.",
                        "ENCARGADO: Escucha lo que te diga. Una ruta segura vale más que un pago rápido.",
                        "ENCARGADO: " + qualifier
                    };
                case 5:
                    return new[] {
                        "ENCARGADO: Un convoy lleva suministros que necesitamos. Localízalo y despeja la ruta antes de acercarte a la carga.",
                        "ENCARGADO: Objetivo opcional: conserva la carreta y el cargamento en buen estado para obtener una bonificación del 15%.",
                        "ENCARGADO: Si la carreta termina destruida, recoge lo que puedas de la zona y no te quedes allí.",
                        "ENCARGADO: " + qualifier
                    };
                case 6:
                    return new[] {
                        "ENCARGADO: Un grupo se ha instalado demasiado cerca de nuestros caminos. Entra en su escondite y no dejes que vuelva a organizarse.",
                        "ENCARGADO: Objetivo opcional: acércate sin abrir fuego. Si la guardia no te detecta, recibirás una bonificación del 15%.",
                        "ENCARGADO: Cuenta a los hombres antes de retirarte; no des por despejado el lugar mientras quede uno en pie.",
                        "ENCARGADO: " + qualifier
                    };
                case 7:
                    return new[] {
                        "ENCARGADO: Un carro de suministros abastece a una partida rival. Localízalo, neutraliza a su escolta y deja el vehículo fuera de servicio.",
                        "ENCARGADO: Objetivo opcional: evita que la guardia te vea y conserva una bonificación del 15% por sabotaje discreto.",
                        "ENCARGADO: No basta con espantar a los guardias. El carro no debe llegar a su destino.",
                        "ENCARGADO: " + qualifier
                    };
                case 8:
                    return new[] {
                        "ENCARGADO: Un explorador de los nuestros ha sido retenido. Encuentra el lugar, elimina a sus captores y sácalo de allí.",
                        "ENCARGADO: Comprueba que siga con vida antes de dar el rescate por terminado.",
                        "ENCARGADO: " + qualifier
                    };
                case 9:
                    return new[] {
                        "ENCARGADO: Hay lobos atacando a viajeros en esa zona. Sigue sus rastros y reduce la manada antes de que vuelva a acercarse a nuestros caminos.",
                        "ENCARGADO: No confundas las huellas de paso con las de una guarida. Revisa el área marcada.",
                        "ENCARGADO: " + qualifier
                    };
                default:
                    return new[] {
                        "ENCARGADO: Quiero información fiable sobre ese camino. Examina el terreno antes de volver.",
                        "ENCARGADO: Busca señales, no problemas. Una pista concreta puede ahorrarnos una pelea.",
                        "ENCARGADO: " + qualifier
                    };
            }
        }

        private static string[] GetCheckpointLines(int missionType, int objectiveIndex)
        {
            switch (missionType)
            {
                case 0:
                    return objectiveIndex <= 1
                        ? new[] { "NOTA DE CAMPO: Has encontrado señales recientes. Comprueba el segundo punto antes de sacar conclusiones." }
                        : new[] { "NOTA DE CAMPO: La ruta está revisada. Ya puedes volver con información útil." };
                case 1:
                    return new[] { "PATRULLA: Punto registrado. No abandones el recorrido; falta terreno por cubrir." };
                case 2:
                    return new[] { "ALIJO: Has localizado el escondite. Antes de tocar las provisiones, comprueba quién las está vigilando." };
                case 3:
                    return new[] { "VIGILANCIA: Mantén la posición. El movimiento en el camino podría no ser casual." };
                case 4:
                    return new[] { "DESPACHO: El punto de encuentro está cerca. Confirma la identidad del contacto antes de entregar nada." };
                case 6:
                    return new[] { "ESCONDITE: Has localizado el refugio. Los hombres están cerca; entra con cuidado y no dejes ninguno detrás." };
                case 7:
                    return new[] { "SABOTAJE: El carro está en la zona. Primero despeja a los escoltas; después acaba con el vehículo." };
                case 8:
                    return new[] { "RESCATE: Has encontrado el lugar de retención. Neutraliza a los captores y busca al explorador." };
                case 9:
                    if (objectiveIndex < 2)
                        return new[] { "RASTREO: Has encontrado una señal. Sigue el siguiente rastro; las huellas empiezan a converger." };
                    return new[] { "CAZA: Los rastros convergen. La manada debe estar cerca; prepara el arma y revisa los alrededores." };
                default:
                    return new[] { "CONVOY: Has llegado a la zona. Observa a los escoltas y busca el momento para acercarte." };
            }
        }

        private static string[] GetEncounterLines(int missionType)
        {
            switch (missionType)
            {
                case 2: return new[] { "ALIJO: Te han descubierto. Despeja la zona antes de recoger las provisiones." };
                case 3: return new[] { "VIGILANCIA: ¡Una partida se aproxima al cruce! Aguanta la posición y neutraliza a los atacantes." };
                case 6: return new[] { "ESCONDITE: Ya te han visto. Cierra las salidas y elimina a los hombres que ocupan el refugio." };
                case 7: return new[] { "SABOTAJE: Los escoltas protegen el carro. Rompe su defensa y destruye el vehículo." };
                case 8: return new[] { "RESCATE: Los captores han reaccionado. Acaba con ellos sin poner en peligro al explorador." };
                case 9: return new[] { "CAZA: La manada está cerca. Abate a los lobos antes de que puedan rodearte." };
                default: return new[] { "CONVOY: Los escoltas están alerta. Busca cobertura y neutraliza la amenaza antes de registrar la carga." };
            }
        }

        private static string[] GetReturnLines(int missionType)
        {
            switch (missionType)
            {
                case 0: return new[] { "ENCARGADO: Dos puntos revisados. Con eso podremos planear la siguiente salida." };
                case 1: return new[] { "ENCARGADO: La patrulla ha cubierto la ruta. Volvamos a organizar a la banda." };
                case 2: return new[] { "ENCARGADO: Las provisiones valen poco si llevas enemigos hasta el campamento. Bien hecho al despejar la zona." };
                case 3: return new[] { "ENCARGADO: Ya sabemos quién ronda el cruce. Regresa y contaremos lo que has visto." };
                case 4: return new[] { "ENCARGADO: Un despacho entregado sin levantar sospechas. Regresa con las manos libres." };
                case 6: return new[] { "ENCARGADO: El escondite ha quedado vacío. Tendrán que buscar otro lugar para reunirse." };
                case 7: return new[] { "ENCARGADO: Ese carro ya no llevará suministros a nuestros rivales." };
                case 8: return new[] { "ENCARGADO: El explorador está a salvo. Recuperar a uno de los nuestros siempre merece el riesgo." };
                case 9: return new[] { "ENCARGADO: Los caminos vuelven a ser transitables. La manada no molestará a nuestros viajeros por un tiempo." };
                default: return new[] { "ENCARGADO: El convoy ha quedado neutralizado. Trae el informe y deja que la banda se ocupe del resto." };
            }
        }

        private void StartCombatMusic()
        {
            if (_combatMusicActive) return;
            _combatMusicRequested = true;
            _nextMusicRetryAt = 0;
            MaintainCombatMusic();
        }

        private void MaintainCombatMusic()
        {
            if (!_combatMusicRequested || _combatMusicActive || Game.GameTime < _nextMusicRetryAt)
                return;

            _nextMusicRetryAt = Game.GameTime + 2500;
            try
            {
                bool prepared = Function.Call<bool>(PrepareMusicEventHash,
                    new InputArgument[] { CombatMusicStart });
                bool triggered = Function.Call<bool>(TriggerMusicEventHash,
                    new InputArgument[] { CombatMusicStart });

                Log.Write("Mission music start event " + CombatMusicStart +
                    ": prepared=" + prepared + ", triggered=" + triggered + ".");

                if (triggered)
                {
                    _combatMusicActive = true;
                    _combatMusicRequested = false;
                    _activeMusicStopEvent = CombatMusicStop;
                    return;
                }

                bool fallbackPrepared = Function.Call<bool>(PrepareMusicEventHash,
                    new InputArgument[] { "BRT1_FIGHT_START" });
                bool fallbackTriggered = Function.Call<bool>(TriggerMusicEventHash,
                    new InputArgument[] { "BRT1_FIGHT_START" });
                Log.Write("Mission music fallback BRT1_FIGHT_START: prepared=" +
                    fallbackPrepared + ", triggered=" + fallbackTriggered + ".");

                if (fallbackTriggered)
                {
                    _combatMusicActive = true;
                    _combatMusicRequested = false;
                    _activeMusicStopEvent = "BRT1_STOP";
                }
            }
            catch (Exception ex)
            {
                Log.Write("Mission combat music start warning: " + ex.Message);
            }
        }

        private void StopCombatMusic(bool playStinger)
        {
            _combatMusicRequested = false;
            if (!_combatMusicActive) return;
            try
            {
                if (playStinger && _activeMusicStopEvent == CombatMusicStop)
                {
                    Function.Call(PrepareMusicEventHash, new InputArgument[] { CombatMusicEndStinger });
                    Function.Call(TriggerMusicEventHash, new InputArgument[] { CombatMusicEndStinger });
                }
                Function.Call(PrepareMusicEventHash, new InputArgument[] { _activeMusicStopEvent });
                Function.Call(TriggerMusicEventHash, new InputArgument[] { _activeMusicStopEvent });
            }
            catch (Exception ex) { Log.Write("Mission combat music stop warning: " + ex.Message); }
            _combatMusicActive = false;
            _activeMusicStopEvent = CombatMusicStop;
        }

        private bool TryDisplaySubtitle(string line, int duration = 4200)
        {
            try
            {
                Type screenType = typeof(Game).Assembly.GetType("RDR2.UI.Screen", false);
                if (screenType == null) return false;

                MethodInfo[] methods = screenType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                foreach (MethodInfo method in methods)
                {
                    if (method.Name != "DisplaySubtitle") continue;
                    ParameterInfo[] parameters = method.GetParameters();

                    if (parameters.Length == 2 &&
                        parameters[0].ParameterType == typeof(string) &&
                        parameters[1].ParameterType == typeof(int))
                    {
                        method.Invoke(null, new object[] { line, duration });
                        return true;
                    }

                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    {
                        method.Invoke(null, new object[] { line });
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Native subtitle display unavailable; falling back to notification: " + ex.Message);
            }
            return false;
        }

        private static void PlayFrontendCue(string cue)
        {
            try
            {
                Function.Call(PlaySoundFrontendHash, new InputArgument[]
                {
                    cue, "HUD_FRONTEND_DEFAULT_SOUNDSET", false, 0
                });
            }
            catch (Exception ex) { Log.Write("Mission audio cue warning: " + ex.Message); }
        }

        private static void FaceEachOther(Ped missionGiver, Ped player)
        {
            try
            {
                if (missionGiver != null && missionGiver.Exists() && player != null && player.Exists())
                    Function.Call(TurnPedToFaceEntityHash,
                        new InputArgument[] { missionGiver.Handle, player.Handle, 1800 });
            }
            catch (Exception ex) { Log.Write("Mission dialogue facing task warning: " + ex.Message); }
        }

    }
}
