using System;
using System.Collections.Generic;

namespace EnRAGEPlayerFactionExtension
{
    internal enum MissionObjectiveType
    {
        ReachLocation, PatrolRoute, RecoverSupplies, HoldArea, DeliverDispatch, InterceptConvoy,
        EliminateTargets, DestroyTarget, RescuePrisoner, HuntAnimal
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
        internal int MinimumCompleted { get; private set; }

        internal MissionDefinition(int id, string title, string instruction, MissionObjectiveType objectiveType,
            int supplies, int treasury, int prestige, int weight, int minimumCompleted = 0)
        {
            Id = id; Title = title; StartInstruction = instruction; ObjectiveType = objectiveType;
            SuppliesReward = supplies; TreasuryReward = treasury; PrestigeReward = prestige;
            Weight = weight; MinimumCompleted = minimumCompleted;
        }
    }

    internal static class MissionCatalog
    {
        private static readonly MissionDefinition[] Definitions =
        {
            new MissionDefinition(0, "Reconocimiento de ruta",
                "Inspecciona dos puntos de la ruta, registra las señales y vuelve al campamento.",
                MissionObjectiveType.ReachLocation, 5, 25, 5, 3),
            new MissionDefinition(1, "Patrulla de frontera",
                "Recorre tres puntos de control en orden. La ruta cambia entre salidas.",
                MissionObjectiveType.PatrolRoute, 8, 35, 8, 3),
            new MissionDefinition(2, "Recuperar provisiones",
                "Encuentra el alijo, despeja la zona y recoge las provisiones antes de regresar.",
                MissionObjectiveType.RecoverSupplies, 12, 20, 9, 3),
            new MissionDefinition(3, "Vigilar el cruce",
                "Observa el camino durante 24 segundos dentro del perímetro. Si aparece una partida, despeja el cruce.",
                MissionObjectiveType.HoldArea, 7, 34, 8, 2),
            new MissionDefinition(4, "Entregar el despacho",
                "Llega al contacto, verifica la información y entrega el despacho con su prompt contextual.",
                MissionObjectiveType.DeliverDispatch, 6, 48, 8, 2),
            new MissionDefinition(5, "Interceptar un convoy",
                "Localiza la caravana, elimina a sus escoltas y saquea la carga antes de regresar.",
                MissionObjectiveType.InterceptConvoy, 13, 65, 11, 2),
            new MissionDefinition(6, "Limpiar el escondite",
                "Localiza el refugio enemigo y elimina a todos los hombres que lo ocupan.",
                MissionObjectiveType.EliminateTargets, 9, 42, 10, 2, 1),
            new MissionDefinition(7, "Sabotear el carro de suministros",
                "Encuentra el carro protegido, elimina a sus escoltas y destruye el vehículo.",
                MissionObjectiveType.DestroyTarget, 7, 68, 11, 1, 3),
            new MissionDefinition(8, "Rescatar al explorador",
                "Localiza al explorador retenido, neutraliza a sus captores y libéralo con el prompt contextual.",
                MissionObjectiveType.RescuePrisoner, 10, 38, 13, 2, 2),
            new MissionDefinition(9, "Cazar al depredador",
                "Rastrea la zona marcada, encuentra a la manada y abate a los lobos que amenazan los caminos.",
                MissionObjectiveType.HuntAnimal, 7, 52, 10, 2, 3)
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
            foreach (MissionDefinition definition in Definitions)
            {
                if (completed < definition.MinimumCompleted) continue;
                if (Definitions.Length > 1 && definition.Id == lastType) continue;
                int weight = WeightFor(definition, completed, difficulty);
                if (weight <= 0) continue;
                candidates.Add(definition);
                totalWeight += weight;
            }

            if (candidates.Count == 0)
            {
                foreach (MissionDefinition definition in Definitions)
                    if (completed >= definition.MinimumCompleted) candidates.Add(definition);
            }
            if (candidates.Count == 0) return Definitions[0].Id;

            int roll = random.Next(Math.Max(1, totalWeight));
            foreach (MissionDefinition definition in candidates)
            {
                roll -= WeightFor(definition, completed, difficulty);
                if (roll < 0) return definition.Id;
            }
            return candidates[candidates.Count - 1].Id;
        }

        private static int WeightFor(MissionDefinition definition, int completed, int difficulty)
        {
            int weight = definition.Weight;
            MissionObjectiveType type = definition.ObjectiveType;
            if (difficulty <= 0 && (type == MissionObjectiveType.InterceptConvoy ||
                type == MissionObjectiveType.DestroyTarget || type == MissionObjectiveType.HuntAnimal))
                weight = Math.Max(1, weight - 1);
            else if (difficulty >= 2 && (type == MissionObjectiveType.EliminateTargets ||
                type == MissionObjectiveType.DestroyTarget || type == MissionObjectiveType.HuntAnimal ||
                type == MissionObjectiveType.InterceptConvoy))
                weight += 2;
            if ((completed + definition.Id * 2) % 5 == 0) weight++;
            return Math.Max(1, weight);
        }

        internal static string DifficultyName(int difficulty)
        {
            switch (difficulty) { case 1: return "Media"; case 2: return "Alta"; default: return "Baja"; }
        }

        internal static void GetRewards(int missionType, int difficulty, out int supplies, out int treasury, out int prestige)
        {
            MissionDefinition definition = Get(missionType);
            double multiplier = 1.0 + Math.Max(0, Math.Min(2, difficulty)) * 0.30;
            supplies = Math.Max(1, (int)Math.Round(definition.SuppliesReward * multiplier));
            treasury = Math.Max(1, (int)Math.Round(definition.TreasuryReward * multiplier));
            prestige = Math.Max(1, (int)Math.Round(definition.PrestigeReward * multiplier));
        }
    }
}
