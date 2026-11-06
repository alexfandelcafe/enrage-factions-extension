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
        private const ulong TriggerMusicEventHash = 0x706D57B0F50DA710UL;
        private const ulong RequestAnimDictHash = 0xA862A2AD321F94B4UL;
        private const ulong HasAnimDictLoadedHash = 0xD031A9162D01088CUL;
        private const ulong TaskPlayAnimHash = 0xEA47FE3719165B94UL;
        private const ulong TurnPedToFaceEntityHash = 0x5AD23D40115353ACUL;
        private const string CombatMusicStart = "DUEL_GENERAL_START";
        private const string CombatMusicStop = "DUEL_GENERAL_STOP";
        private const string CombatMusicEndStinger = "DUEL_GENERAL_END_OS";
        private const string InspectionAnimDictionary = "MECH_DYNAMIC@WORLD_PLAYER_DYNAMIC_KNEEL_GROUND@GENERIC@MALE_A";
        private const string InspectionAnimation = "base";

        private readonly Action<string> _notify;
        private readonly Queue<string> _dialogue = new Queue<string>();
        private int _nextDialogueAt;
        private int _dialogueGapMs = 2600;
        private bool _combatMusicActive;
        private bool _inspectionAnimLoaded;
        private int _nextAnimationRequestAt;

        internal MissionStoryDirector(Action<string> notify)
        {
            _notify = notify;
        }

        internal void Tick()
        {
            MaintainInspectionDictionary();
            if (_dialogue.Count == 0 || Game.GameTime < _nextDialogueAt) return;
            string line = _dialogue.Dequeue();
            try
            {
                if (!TryDisplaySubtitle(line))
                    _notify(line);
            }
            catch (Exception ex)
            {
                Log.Write("Mission dialogue display warning: " + ex.Message);
                try { _notify(line); } catch { }
            }
            _nextDialogueAt = Game.GameTime + _dialogueGapMs;
        }

        internal void BeginMission(int missionType, int difficulty, Ped missionGiver, Ped player)
        {
            RequestInspectionDictionary();
            PlayFrontendCue("SELECT");
            FaceEachOther(missionGiver, player);
            QueueDialogue(GetOpeningLines(missionType, difficulty));
        }

        internal void OnCheckpoint(int missionType, int objectiveIndex)
        {
            PlayFrontendCue("NAV_UP_DOWN");
            QueueDialogue(GetCheckpointLines(missionType, objectiveIndex));
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
            PlayInspectionAnimation(player);
            PlayFrontendCue("SELECT");
            if (!string.IsNullOrWhiteSpace(context))
                QueueDialogue(new[] { context }, true);
        }

        internal void PlayReminderCue()
        {
            PlayFrontendCue("NAV_UP_DOWN");
        }

        internal void OnMissionReturning(int missionType)
        {
            StopCombatMusic(true);
            PlayFrontendCue("SELECT");
            QueueDialogue(GetReturnLines(missionType), true);
        }

        internal void OnMissionCompleted(int missionType)
        {
            StopCombatMusic(true);
            PlayFrontendCue("SELECT");
            QueueDialogue(new[] { "ENCARGADO: Buen trabajo. La banda recordará que has cumplido." }, true);
        }

        internal void OnScriptAborted()
        {
            _dialogue.Clear();
            StopCombatMusic(false);
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
            try
            {
                // A built-in RDR2 music event is used only for hostile encounters.
                _combatMusicActive = Function.Call<bool>(TriggerMusicEventHash,
                    new InputArgument[] { CombatMusicStart });
                if (!_combatMusicActive)
                    Log.Write("Mission combat music event was not accepted by the game audio engine.");
            }
            catch (Exception ex) { Log.Write("Mission combat music warning: " + ex.Message); }
        }

        private void StopCombatMusic(bool playStinger)
        {
            if (!_combatMusicActive) return;
            try
            {
                if (playStinger)
                    Function.Call(TriggerMusicEventHash, new InputArgument[] { CombatMusicEndStinger });
                Function.Call(TriggerMusicEventHash, new InputArgument[] { CombatMusicStop });
            }
            catch (Exception ex) { Log.Write("Mission combat music stop warning: " + ex.Message); }
            _combatMusicActive = false;
        }

        private bool TryDisplaySubtitle(string line)
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
                        method.Invoke(null, new object[] { line, 4200 });
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

        private void RequestInspectionDictionary()
        {
            if (_inspectionAnimLoaded || Game.GameTime < _nextAnimationRequestAt) return;
            _nextAnimationRequestAt = Game.GameTime + 900;
            try
            {
                Function.Call(RequestAnimDictHash, new InputArgument[] { InspectionAnimDictionary });
                _inspectionAnimLoaded = Function.Call<bool>(HasAnimDictLoadedHash,
                    new InputArgument[] { InspectionAnimDictionary });
            }
            catch (Exception ex)
            {
                Log.Write("Inspection animation dictionary request warning: " + ex.Message);
            }
        }

        private void MaintainInspectionDictionary()
        {
            if (_inspectionAnimLoaded) return;
            RequestInspectionDictionary();
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

        private bool PlayInspectionAnimation(Ped player)
        {
            if (player == null || !player.Exists() || player.IsOnMount || player.IsInVehicle) return false;
            try
            {
                MaintainInspectionDictionary();
                if (!_inspectionAnimLoaded) return false;

                Function.Call(TaskPlayAnimHash, new InputArgument[]
                {
                    player.Handle, InspectionAnimDictionary, true, "base", false,
                    InspectionAnimation, 1.0f, 1.0f, 1700, 0
                });
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Mission inspection animation unavailable; interaction continues without it: " + ex.Message);
                return false;
            }
        }
    }
}
