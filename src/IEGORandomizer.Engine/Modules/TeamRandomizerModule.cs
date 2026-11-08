using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Protection;
using IEGORandomizer.Engine.Seeding;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Engine.Modules
{
    /// <summary>
    /// Subsystem C: Randomizes Story/Rival/Route Teams and 5v5 Pachanga Teams:
    /// kit numbers, formations, coaches, tactics, team levels, and team equipment
    /// (synchronizing overworld <c>EncountMapRecord.Boots</c> when pachanga boots change).
    /// </summary>
    public static class TeamRandomizerModule
    {
        public static void Execute(
            SafeGOGame game,
            SeedEngine seeds,
            TeamsRandomizerOptions options,
            StoryProtectionContext protection,
            RandomizationExecutionReport report)
        {
            if (options == null || !options.Enabled)
                return;

            var storyTeams = game.GetStoryTeamsOnly();
            var encountTeams = game.GetEncounterTeamsOnly();
            var teamParams = game.GetTeamParams();
            var allItems = game.GetItems("all") ?? Array.Empty<IItemConfig>();
            var directors = game.GetItems("director") ?? Array.Empty<IItemConfig>();

            var storyTeamIds = new HashSet<int>(storyTeams.Select(t => t.TeamConfigID).Where(id => id != 0));
            var encountTeamIds = new HashSet<int>(encountTeams.Select(t => t.TeamConfigID).Where(id => id != 0));

            // ItemTypes.json:
            // 1 = Boots, 2 = Gloves, 3 = Bracelet, 4 = Pendant
            // 19 = Formation (Match), 20 = Formation (Mini-Battle), 21 = Coach, 23 = Tactic
            var bootsPool = allItems.Where(i => i.ItemID != 0 && i.ItemCategory == 1).Select(i => i.ItemID).Distinct().ToList();
            var glovesPool = allItems.Where(i => i.ItemID != 0 && i.ItemCategory == 2).Select(i => i.ItemID).Distinct().ToList();
            var braceletPool = allItems.Where(i => i.ItemID != 0 && i.ItemCategory == 3).Select(i => i.ItemID).Distinct().ToList();
            var pendantPool = allItems.Where(i => i.ItemID != 0 && i.ItemCategory == 4).Select(i => i.ItemID).Distinct().ToList();

            var matchFormations = allItems.Where(i => i.ItemID != 0 && (i.ItemCategory == 19 || i.ItemCategory == 17))
                .Where(i => i.ItemRef != 90) // exclude Palpack (ItemRef 90)
                .Select(i => i.ItemID)
                .Distinct()
                .ToList();
            // Also include any valid formation IDs already present in vanilla TeamParams so pools are never empty
            foreach (var tp in teamParams)
            {
                if (tp.FormationID != 0 && storyTeamIds.Contains(tp.TeamConfigID) && !matchFormations.Contains(tp.FormationID))
                    matchFormations.Add(tp.FormationID);
            }

            var miniFormations = allItems.Where(i => i.ItemID != 0 && i.ItemCategory == 20)
                .Select(i => i.ItemID)
                .Distinct()
                .ToList();
            foreach (var tp in teamParams)
            {
                if (tp.FormationID != 0 && encountTeamIds.Contains(tp.TeamConfigID) && !miniFormations.Contains(tp.FormationID))
                    miniFormations.Add(tp.FormationID);
            }

            var coachPool = directors.Where(d => d.ItemID != 0).Select(d => d.ItemID)
                .Concat(allItems.Where(i => i.ItemID != 0 && i.ItemCategory == 21).Select(i => i.ItemID))
                .Distinct()
                .ToList();

            var tacticPool = allItems.Where(i => i.ItemID != 0 && (i.ItemCategory == 23 || i.ItemCategory == 18))
                .Where(i => i.ItemRef != 100) // exclude Avatar (ItemRef 100)
                .Select(i => i.ItemID)
                .Distinct()
                .ToList();
            foreach (var tp in teamParams)
            {
                if (tp.TacticID != 0 && !tacticPool.Contains(tp.TacticID))
                    tacticPool.Add(tp.TacticID);
            }

            // Build valid Emblem1 pool from existing teams + /data/emblem/embXXXX.xi
            var emblemPool = new HashSet<int>();
            foreach (var st in storyTeams)
            {
                if (st.Emblem1 > 0) emblemPool.Add(st.Emblem1);
            }
            foreach (var et in encountTeams)
            {
                if (et.Emblem1 > 0) emblemPool.Add(et.Emblem1);
            }
            var emblemDir = game.GetDirectory("emblem");
            if (emblemDir != null)
            {
                foreach (string fName in emblemDir.Files.Keys)
                {
                    if (fName.StartsWith("emb", StringComparison.OrdinalIgnoreCase) &&
                        fName.EndsWith(".xi", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(fName.Substring(3, fName.Length - 6), out int embNum) &&
                        embNum > 0)
                    {
                        emblemPool.Add(embNum);
                    }
                }
            }
            var orderedEmblemPool = emblemPool.OrderBy(x => x).ToList();

            // 1. Randomize Shirt / Kit Numbers (1..99, unique per team) and Team Emblems
            if (options.RandomizeKitNumbers || (options.RandomizeEmblems && orderedEmblemPool.Count > 1))
            {
                DeterministicRng kitNumRng = seeds.GetSubsystem("teams.kit_numbers");
                DeterministicRng emblemRng = seeds.GetSubsystem("teams.emblems");

                if (options.RandomizeStoryAndRouteTeams)
                {
                    foreach (var st in storyTeams)
                    {
                        if (protection.ScriptedTeamConfigHashes.Contains(st.TeamConfigID))
                            continue;

                        if (options.RandomizeKitNumbers)
                        {
                            AssignStoryShirtNumbers(st, kitNumRng, keepGoalkeeperNo1: true);
                        }

                        if (options.RandomizeEmblems &&
                            orderedEmblemPool.Count > 1 &&
                            !protection.RaimonCoreTeamConfigHashes.Contains(st.TeamConfigID))
                        {
                            st.Emblem1 = PickDifferentInt(orderedEmblemPool, st.Emblem1, emblemRng);
                        }
                    }
                }

                if (options.RandomizePachangaTeams)
                {
                    foreach (var et in encountTeams)
                    {
                        if (options.RandomizeKitNumbers)
                        {
                            AssignEncountShirtNumbers(et, kitNumRng);
                        }

                        if (options.RandomizeEmblems && orderedEmblemPool.Count > 1)
                        {
                            et.Emblem1 = PickDifferentInt(orderedEmblemPool, et.Emblem1, emblemRng);
                        }
                    }
                }

                game.SaveTeamConfig(storyTeams, encountTeams);
            }

            // 2. Randomize TeamParamInfo (Formations, Coaches, Tactics, Levels, Equipment)
            DeterministicRng formRng = seeds.GetSubsystem("teams.formations");
            DeterministicRng coachRng = seeds.GetSubsystem("teams.coaches");
            DeterministicRng tacticRng = seeds.GetSubsystem("teams.tactics");
            DeterministicRng levelRng = seeds.GetSubsystem("teams.levels");
            DeterministicRng equipRng = seeds.GetSubsystem("teams.equipment");

            var pachangaBootsByTeamConfig = new Dictionary<int, int>();

            foreach (var tp in teamParams)
            {
                if (tp.TeamParamID == 0)
                    continue;

                bool isPachanga = encountTeamIds.Contains(tp.TeamConfigID);
                bool isScriptedStory = protection.RaimonCoreTeamConfigHashes.Contains(tp.TeamConfigID) ||
                                       protection.ScriptedTeamParamHashes.Contains(tp.TeamParamID) ||
                                       protection.ScriptedTeamConfigHashes.Contains(tp.TeamConfigID);

                if (isPachanga && !options.RandomizePachangaTeams)
                    continue;
                if (!isPachanga && !options.RandomizeStoryAndRouteTeams)
                    continue;

                // Protect scripted story matches from broken formations/impossible levels
                if (isScriptedStory)
                    continue;

                if (options.RandomizeFormations)
                {
                    var fPool = isPachanga
                        ? (miniFormations.Count > 0 ? miniFormations : matchFormations)
                        : matchFormations;
                    if (fPool.Count > 0)
                        tp.FormationID = formRng.PickOne(fPool);
                }

                if (options.RandomizeCoaches && !isPachanga && coachPool.Count > 0)
                {
                    tp.CoachID = coachRng.PickOne(coachPool);
                }

                if (options.RandomizeTactics && !isPachanga && tacticPool.Count > 0)
                {
                    tp.TacticID = tacticRng.PickOne(tacticPool);
                }

                if (options.RandomizeLevels && tp.Level > 0)
                {
                    int maxDelta = Math.Clamp(options.MaxLevelDelta, 1, 25);
                    int delta = levelRng.NextIntInclusive(-maxDelta, maxDelta);
                    tp.Level = Math.Clamp(tp.Level + delta, 1, 99);
                }

                if (options.RandomizeEquipment)
                {
                    if (bootsPool.Count > 0) tp.BootsID = equipRng.PickOne(bootsPool);
                    if (glovesPool.Count > 0) tp.GlovesID = equipRng.PickOne(glovesPool);
                    if (braceletPool.Count > 0) tp.BraceletID = equipRng.PickOne(braceletPool);
                    if (pendantPool.Count > 0) tp.PendantID = equipRng.PickOne(pendantPool);

                    if (isPachanga && tp.TeamConfigID != 0 && tp.BootsID != 0)
                    {
                        pachangaBootsByTeamConfig[tp.TeamConfigID] = tp.BootsID;
                    }
                }

                if (isPachanga)
                    report.RandomizedPachangaTeamsCount++;
                else
                    report.RandomizedTeamsCount++;
            }

            game.SaveTeamParams(teamParams);

            // Synchronize overworld pachanga boots if equipment was randomized
            if (options.RandomizeEquipment && pachangaBootsByTeamConfig.Count > 0)
            {
                var spawns = MapDataService.LoadAllEncounterSpawns(game.IE_A.Directory);
                bool modifiedSpawns = false;
                foreach (var sp in spawns)
                {
                    if (pachangaBootsByTeamConfig.TryGetValue(sp.Record.TeamID, out int newBoots))
                    {
                        sp.Record.Boots = newBoots;
                        modifiedSpawns = true;
                    }
                }
                if (modifiedSpawns)
                {
                    MapDataService.SaveEncounterSpawns(game.IE_A.Directory, spawns);
                }
            }
        }

        private static void AssignStoryShirtNumbers(
            IStoryTeamInfo team,
            DeterministicRng rng,
            bool keepGoalkeeperNo1)
        {
            var available = Enumerable.Range(2, 98).ToList();
            rng.Shuffle(available);
            int cursor = 0;

            for (int slot = 1; slot <= 16; slot++)
            {
                if (TeamSlotAccessor.GetStoryPlayer(team, slot) == 0)
                    continue;
                int num = (slot == 1 && keepGoalkeeperNo1)
                    ? 1
                    : available[cursor++ % available.Count];
                TeamSlotAccessor.SetStoryKitNumber(team, slot, num);
            }
        }

        private static void AssignEncountShirtNumbers(IEncountTeamInfo team, DeterministicRng rng)
        {
            var available = Enumerable.Range(1, 99).ToList();
            rng.Shuffle(available);
            int cursor = 0;

            for (int slot = 1; slot <= 5; slot++)
            {
                if (TeamSlotAccessor.GetEncountPlayer(team, slot) == 0)
                    continue;
                TeamSlotAccessor.SetEncountKitNumber(team, slot, available[cursor++ % available.Count]);
            }
        }

        private static int PickDifferentInt(IReadOnlyList<int> pool, int current, DeterministicRng rng)
        {
            if (pool.Count == 1)
                return pool[0];
            for (int i = 0; i < 6; i++)
            {
                int candidate = rng.PickOne(pool);
                if (candidate != current)
                    return candidate;
            }
            return rng.PickOne(pool);
        }
    }
}
