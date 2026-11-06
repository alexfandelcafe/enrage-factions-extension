using System;
using System.Collections.Generic;
using System.Reflection;
using RDR2;
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
        private int _nextHintFallbackAt;

        internal MissionStoryDirector(Action<string> notify)
        {
            _notify = notify;
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
            if (missionType == 2 || missionType == 3 || missionType == 5)
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
                        "ENCARGADO: Si la carreta termina destruida, recoge lo que puedas de la zona y no te quedes allí.",
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
