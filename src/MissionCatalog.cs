using System;
using System.Collections.Generic;

namespace EnRAGEPlayerFactionExtension
{
    internal enum MissionObjectiveType
    {
        ReachLocation, PatrolRoute, RecoverSupplies, HoldArea, DeliverDispatch, InterceptConvoy
    }

    internal sealed class MissionDefinition
    {
        internal int Id { get; private set; }
        internal string Title { get; private set; }
        internal string StartInstruction { get; private set; }
        internal MissionObjectiveType ObjectiveType { get; private set; }
        internal int SuppliesReward { get; private set; }
        internal int TreasuryReward { get; private set; }
        internal int PrestigeReward { get; private set; }
        internal int Weight { get; private set; }

        internal MissionDefinition(int id, string title, string instruction, MissionObjectiveType objectiveType,
            int supplies, int treasury, int prestige, int weight)
        {
            Id = id; Title = title; StartInstruction = instruction; ObjectiveType = objectiveType;
            SuppliesReward = supplies; TreasuryReward = treasury; PrestigeReward = prestige; Weight = weight;
        }
    }

    /// <summary>Data-driven contract catalogue, weighted selection, and reward scaling.</summary>
    internal static class MissionCatalog
    {
        private static readonly MissionDefinition[] Definitions =
        {
            new MissionDefinition(0, "Reconocimiento de ruta", "Visita el punto marcado y regresa al campamento.", MissionObjectiveType.ReachLocation, 5, 25, 5, 3),
            new MissionDefinition(1, "Patrulla de frontera", "Recorre los tres puntos marcados en orden y regresa al campamento.", MissionObjectiveType.PatrolRoute, 8, 35, 8, 2),
            new MissionDefinition(2, "Recuperar provisiones", "Encuentra el alijo, pulsa la tecla contextual para recogerlo y vuelve al campamento.", MissionObjectiveType.RecoverSupplies, 12, 20, 9, 3),
            new MissionDefinition(3, "Vigilar el cruce", "Llega al cruce y permanece dentro del área marcada durante 18 segundos; después regresa.", MissionObjectiveType.HoldArea, 6, 30, 7, 2),
            new MissionDefinition(4, "Entregar el despacho", "Llega al punto de encuentro, pulsa la tecla contextual y vuelve al campamento.", MissionObjectiveType.DeliverDispatch, 5, 45, 8, 2),
            new MissionDefinition(5, "Interceptar un convoy", "Localiza la caravana, elimina a sus escoltas y saquea el cargamento antes de regresar al campamento.", MissionObjectiveType.InterceptConvoy, 12, 60, 10, 1)
        };

        internal static MissionDefinition Get(int id)
        {
            foreach (MissionDefinition definition in Definitions)
                if (definition.Id == id) return definition;
            return Definitions[0];
        }

        internal static int DetermineDifficulty(int completed, Random random)
        {
            int tier = Math.Max(0, Math.Min(2, completed / 3));
            if (tier < 2 && random.Next(100) < 12) tier++;
            else if (tier > 0 && random.Next(100) < 18) tier--;
            return Math.Max(0, Math.Min(2, tier));
        }

        internal static int SelectNext(int completed, int lastType, int difficulty, Random random)
        {
            List<MissionDefinition> candidates = new List<MissionDefinition>();
            int totalWeight = 0;
            foreach (MissionDefinition d in Definitions)
            {
                if (Definitions.Length > 1 && d.Id == lastType) continue;
                int weight = WeightFor(d, completed, difficulty);
                candidates.Add(d);
                totalWeight += weight;
            }
            if (candidates.Count == 0) return Definitions[0].Id;
            int roll = random.Next(Math.Max(1, totalWeight));
            foreach (MissionDefinition d in candidates)
            {
                roll -= WeightFor(d, completed, difficulty);
                if (roll < 0) return d.Id;
            }
            return candidates[candidates.Count - 1].Id;
        }

        private static int WeightFor(MissionDefinition d, int completed, int difficulty)
        {
            int weight = d.Weight;
            if (difficulty == 0 && d.ObjectiveType == MissionObjectiveType.InterceptConvoy) weight = Math.Max(1, weight - 1);
            else if (difficulty == 2 && d.ObjectiveType == MissionObjectiveType.InterceptConvoy) weight += 3;
            if ((completed + d.Id) % 4 == 0) weight++;
            return weight;
        }

        internal static string DifficultyName(int difficulty)
        {
            switch (difficulty) { case 1: return "Media"; case 2: return "Alta"; default: return "Baja"; }
        }

        internal static void GetRewards(int missionType, int difficulty, out int supplies, out int treasury, out int prestige)
        {
            MissionDefinition d = Get(missionType);
            double multiplier = 1.0 + Math.Max(0, Math.Min(2, difficulty)) * 0.30;
            supplies = Math.Max(1, (int)Math.Round(d.SuppliesReward * multiplier));
            treasury = Math.Max(1, (int)Math.Round(d.TreasuryReward * multiplier));
            prestige = Math.Max(1, (int)Math.Round(d.PrestigeReward * multiplier));
        }
    }
}
