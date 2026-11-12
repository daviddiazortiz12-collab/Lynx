using System;
using System.Collections.Generic;
using System.Linq;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Protection;
using IEGORandomizer.Engine.Seeding;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.Core.RomFs;
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Engine.Modules
{
    /// <summary>
    /// Subsystem C: Randomizes Story/Rival/Route Teams and 5v5 Pachanga Teams:
    /// kit numbers, formations, coaches, tactics, team levels, and team equipment
    /// (synchronizing overworld <c>EncountMapRecord.Boots</c> safely within its domain).
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
            var soccers = game.GetSoccers() ?? Array.Empty<ISoccerInfo>();
            var allItems = game.GetItems("all") ?? Array.Empty<IItemConfig>();
            var directors = game.GetItems("director") ?? Array.Empty<IItemConfig>();

            var storyTeamIds = new HashSet<int>(storyTeams.Select(t => t.TeamConfigID).Where(id => id != 0));
            var encountTeamIds = new HashSet<int>(encountTeams.Select(t => t.TeamConfigID).Where(id => id != 0));

            // SoccerMode in soccer_config.cfg.bin (verified against SoccersWindow.Designer.cs):
            // 0 = None, 1 = Mini-Battle (5v5 Pachanga), 2 = Match (11v11)
            var miniBattleParamIds = new HashSet<int>(
                soccers.Where(s => s != null && s.SoccerMode == 1 && s.TeamParamID != 0).Select(s => s.TeamParamID));
            var fullMatchParamIds = new HashSet<int>(
                soccers.Where(s => s != null && s.SoccerMode == 2 && s.TeamParamID != 0).Select(s => s.TeamParamID));

            // Prefix CRC32 lookups:
            // - ifa0001..ifa0099 = 11v11 Match formations
            // - ifb0101..ifb0199 = 5v5 Mini-Battle (Pachanga) formations ONLY!
            // - ifb0300..ifb0399 = 1v1 / 2v2 / 3v3 Story Drill formations (NEVER valid for 5v5 Pachangas or 11v11 Matches!)
            var ifaLookup = Level5IdHelper.BuildPrefixLookup("ifa");
            var validMiniIfbHashes = new HashSet<int>(
                Enumerable.Range(101, 99).Select(n => Level5IdHelper.ComputeCrc32Utf8($"ifb{n:D4}")));
            var drillIfbHashes = new HashSet<int>(
                Enumerable.Range(300, 100).Select(n => Level5IdHelper.ComputeCrc32Utf8($"ifb{n:D4}")));
            var iisLookup = Level5IdHelper.BuildPrefixLookup("iis");

            // ItemTypes.json (retail IEGO1):
            // 1 = Boots, 2 = Gloves, 3 = Bracelet, 4 = Pendant
            // 17 = Palpack (NEVER a formation!), 18 = Fighting Spirit (NEVER a tactic!)
            // 19 = Formation (11v11 Match), 20 = Formation (5v5 Mini-Battle), 21 = Coach, 23 = Tactic
            bool hasPopulatedItemNames = allItems.Any(i => i.NameID != 0);
            bool IsNonDummyItem(IItemConfig i) => i.ItemID != 0 && (!hasPopulatedItemNames || i.NameID != 0);

            var allMatchCatIds = new HashSet<int>(
                allItems.Where(i => i.ItemID != 0 && (i.ItemCategory == 19 || ifaLookup.ContainsKey(i.ItemID))).Select(i => i.ItemID));
            var allMiniCatIds = new HashSet<int>(
                allItems.Where(i => i.ItemID != 0 && (i.ItemCategory == 20 || validMiniIfbHashes.Contains(i.ItemID))).Select(i => i.ItemID));

            var bootsPool = allItems.Where(i => IsNonDummyItem(i) && i.ItemCategory == 1).Select(i => i.ItemID).Distinct().ToList();
            var glovesPool = allItems.Where(i => IsNonDummyItem(i) && i.ItemCategory == 2).Select(i => i.ItemID).Distinct().ToList();
            var braceletPool = allItems.Where(i => IsNonDummyItem(i) && i.ItemCategory == 3).Select(i => i.ItemID).Distinct().ToList();
            var pendantPool = allItems.Where(i => IsNonDummyItem(i) && i.ItemCategory == 4).Select(i => i.ItemID).Distinct().ToList();

            var miniFormationSet = new HashSet<int>(
                allItems.Where(i => IsNonDummyItem(i) && (i.ItemCategory == 20 || validMiniIfbHashes.Contains(i.ItemID)))
                        .Where(i => !allMatchCatIds.Contains(i.ItemID) && !ifaLookup.ContainsKey(i.ItemID) && !drillIfbHashes.Contains(i.ItemID))
                        .Select(i => i.ItemID));

            var matchFormationSet = new HashSet<int>(
                allItems.Where(i => IsNonDummyItem(i) && (i.ItemCategory == 19 || ifaLookup.ContainsKey(i.ItemID)))
                        .Where(i => i.ItemRef != 90 && !allMiniCatIds.Contains(i.ItemID) && !validMiniIfbHashes.Contains(i.ItemID) && !drillIfbHashes.Contains(i.ItemID) && !miniFormationSet.Contains(i.ItemID))
                        .Select(i => i.ItemID));

            // Include valid vanilla formations from unambiguously classified TeamParams (strictly excluding ifb03XX drills!)
            foreach (var tp in teamParams)
            {
                if (tp.FormationID == 0 || drillIfbHashes.Contains(tp.FormationID))
                    continue;

                if (validMiniIfbHashes.Contains(tp.FormationID))
                {
                    miniFormationSet.Add(tp.FormationID);
                    matchFormationSet.Remove(tp.FormationID);
                }
                else if (ifaLookup.ContainsKey(tp.FormationID) || allMatchCatIds.Contains(tp.FormationID))
                {
                    matchFormationSet.Add(tp.FormationID);
                    miniFormationSet.Remove(tp.FormationID);
                }
                else if (encountTeamIds.Contains(tp.TeamConfigID) ||
                         (miniBattleParamIds.Contains(tp.TeamParamID) && !fullMatchParamIds.Contains(tp.TeamParamID)))
                {
                    if (!matchFormationSet.Contains(tp.FormationID) && !allMatchCatIds.Contains(tp.FormationID))
                        miniFormationSet.Add(tp.FormationID);
                }
                else if (storyTeamIds.Contains(tp.TeamConfigID) &&
                         !protection.ScriptedTeamConfigHashes.Contains(tp.TeamConfigID) &&
                         !miniFormationSet.Contains(tp.FormationID) &&
                         !allMiniCatIds.Contains(tp.FormationID))
                {
                    matchFormationSet.Add(tp.FormationID);
                }
            }

            var matchFormations = matchFormationSet.OrderBy(x => x).ToList();
            var miniFormations = miniFormationSet.OrderBy(x => x).ToList();

            bool hasPopulatedDirectorNames = directors.Any(d => d.NameID != 0);
            var coachPool = directors
                .Where(d => d.ItemID != 0 && (!hasPopulatedDirectorNames || d.NameID != 0) && (d is not Lynx.InazumaEleven.Games.GO.GOSupport.ItemConfigDirector dir || (dir.ItemNumber > 0 && dir.ItemNumber < 900)))
                .Select(d => d.ItemID)
                .Concat(allItems.Where(i => IsNonDummyItem(i) && i.ItemCategory == 21).Select(i => i.ItemID))
                .Distinct()
                .ToList();

            var tacticSet = new HashSet<int>(
                allItems.Where(i => IsNonDummyItem(i) && (i.ItemCategory == 23 || iisLookup.ContainsKey(i.ItemID)))
                        .Where(i => i.ItemCategory != 18 && i.ItemRef != 100)
                        .Select(i => i.ItemID));
            foreach (var tp in teamParams)
            {
                if (tp.TacticID != 0 && storyTeamIds.Contains(tp.TeamConfigID))
                    tacticSet.Add(tp.TacticID);
            }
            var tacticPool = tacticSet.OrderBy(x => x).ToList();

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
                        if (protection.ScriptedTeamConfigHashes.Contains(et.TeamConfigID))
                            continue;

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

            var pachangaBootsByKey = new Dictionary<int, int>();

            foreach (var tp in teamParams)
            {
                if (tp.TeamParamID == 0)
                    continue;

                bool isPachanga = IsMiniBattleTeamParam(
                    tp,
                    storyTeamIds,
                    encountTeamIds,
                    miniBattleParamIds,
                    fullMatchParamIds,
                    miniFormationSet);

                bool isScriptedStory = protection.RaimonCoreTeamConfigHashes.Contains(tp.TeamConfigID) ||
                                       protection.ScriptedTeamParamHashes.Contains(tp.TeamParamID) ||
                                       (tp.TeamConfigID != 0 && protection.ScriptedTeamConfigHashes.Contains(tp.TeamConfigID));

                if (isPachanga && !options.RandomizePachangaTeams)
                    continue;
                if (!isPachanga && !options.RandomizeStoryAndRouteTeams)
                    continue;

                // Protect scripted core teams from broken formations/impossible levels
                if (isScriptedStory)
                    continue;

                if (options.RandomizeFormations && tp.FormationID != 0)
                {
                    if (isPachanga)
                    {
                        // NEVER assign an 11v11 Match formation (ifaXXXX) to a 5v5 Pachanga: btl_manager freezes immediately!
                        if (miniFormations.Count > 0)
                            tp.FormationID = formRng.PickOne(miniFormations);
                    }
                    else
                    {
                        if (matchFormations.Count > 0)
                            tp.FormationID = formRng.PickOne(matchFormations);
                    }
                }

                if (!isPachanga)
                {
                    if (options.RandomizeCoaches && coachPool.Count > 0)
                        tp.CoachID = coachRng.PickOne(coachPool);

                    if (options.RandomizeTactics && tacticPool.Count > 0)
                        tp.TacticID = tacticRng.PickOne(tacticPool);
                }

                if (options.RandomizeLevels && tp.Level > 0)
                {
                    // Cap level delta proportionally for early-game teams (e.g. Chapter 1 btl0002 at Lv.1-2)
                    // so CalFinalRevision never imposes an unplayable stat penalty before RPG grinding unlocks!
                    int effectiveMaxDelta = tp.Level <= 5
                        ? Math.Min(1, options.MaxLevelDelta)
                        : Math.Min(Math.Max(2, tp.Level / 2), Math.Clamp(options.MaxLevelDelta, 1, 25));
                    int delta = levelRng.NextIntInclusive(-effectiveMaxDelta, effectiveMaxDelta);
                    tp.Level = Math.Clamp(tp.Level + delta, 1, 99);
                }

                if (options.RandomizeEquipment)
                {
                    // Tier equipment for early-game teams (Lv <= 15) so Lv.2 teams never roll +70 endgame Zero/Omega gear,
                    // and NEVER put GK Gloves on 5v5 Pachangas (so street pachanga keepers don't get massive Catch bonuses)!
                    bool isEarlyTier = tp.Level > 0 && tp.Level <= 15;
                    var effBootsPool = isEarlyTier && bootsPool.Count >= 4 ? bootsPool.Take(Math.Max(2, bootsPool.Count / 3)).ToList() : bootsPool;
                    var effGlovesPool = isEarlyTier && glovesPool.Count >= 4 ? glovesPool.Take(Math.Max(2, glovesPool.Count / 3)).ToList() : glovesPool;
                    var effBraceletPool = isEarlyTier && braceletPool.Count >= 4 ? braceletPool.Take(Math.Max(2, braceletPool.Count / 3)).ToList() : braceletPool;
                    var effPendantPool = isEarlyTier && pendantPool.Count >= 4 ? pendantPool.Take(Math.Max(2, pendantPool.Count / 3)).ToList() : pendantPool;

                    if (effBootsPool.Count > 0 && (!isPachanga || tp.BootsID != 0))
                        tp.BootsID = equipRng.PickOne(effBootsPool);
                    if (isPachanga)
                        tp.GlovesID = 0;
                    else if (effGlovesPool.Count > 0)
                        tp.GlovesID = equipRng.PickOne(effGlovesPool);
                    if (effBraceletPool.Count > 0 && (!isPachanga || tp.BraceletID != 0))
                        tp.BraceletID = equipRng.PickOne(effBraceletPool);
                    if (effPendantPool.Count > 0 && (!isPachanga || tp.PendantID != 0))
                        tp.PendantID = equipRng.PickOne(effPendantPool);

                    if (isPachanga && tp.BootsID != 0)
                    {
                        if (tp.TeamConfigID != 0)
                            pachangaBootsByKey[tp.TeamConfigID] = tp.BootsID;
                        pachangaBootsByKey[tp.TeamParamID] = tp.BootsID;
                    }
                }

                if (isPachanga)
                    report.RandomizedPachangaTeamsCount++;
                else
                    report.RandomizedTeamsCount++;
            }

            foreach (var sc in soccers)
            {
                if (sc != null && sc.SoccerID != 0 && sc.TeamParamID != 0 &&
                    pachangaBootsByKey.TryGetValue(sc.TeamParamID, out int bId) &&
                    !pachangaBootsByKey.ContainsKey(sc.SoccerID))
                {
                    pachangaBootsByKey[sc.SoccerID] = bId;
                }
            }

            game.SaveTeamParams(teamParams);

            // Synchronize overworld pachanga boots safely within their existing domain (never overwrite 0 or mix int shoe indices with CRC32)
            if (options.RandomizeEquipment && pachangaBootsByKey.Count > 0)
            {
                var spawns = MapDataService.LoadAllEncounterSpawns(game.IE_A.Directory);
                var smallShoeModelPool = spawns
                    .Select(s => s.Record.Boots)
                    .Where(b => b > 0 && b < 10_000)
                    .Distinct()
                    .OrderBy(b => b)
                    .ToList();

                bool modifiedSpawns = false;
                foreach (var sp in spawns)
                {
                    if (sp.Record.Boots == 0)
                        continue;

                    if (sp.Record.Boots > 0 && sp.Record.Boots < 10_000)
                    {
                        if (smallShoeModelPool.Count > 1)
                        {
                            sp.Record.Boots = equipRng.PickOne(smallShoeModelPool);
                            modifiedSpawns = true;
                        }
                    }
                    else if (pachangaBootsByKey.TryGetValue(sp.Record.TeamID, out int newBoots) && newBoots != 0)
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

        internal static bool IsMiniBattleTeamParam(
            ITeamParamInfo tp,
            HashSet<int> storyTeamIds,
            HashSet<int> encountTeamIds,
            HashSet<int> miniBattleParamIds,
            HashSet<int> fullMatchParamIds,
            HashSet<int> miniFormationSet = null)
        {
            if (tp == null)
                return false;

            // Explicit 5-player ENCOUNT_TEAM_INFO (teXXXX) is always a 5v5 Pachanga
            if (encountTeamIds != null && tp.TeamConfigID != 0 && encountTeamIds.Contains(tp.TeamConfigID))
                return true;

            // Explicit 16-player STORY_TEAM_INFO (tsXXXX) is a Story/Route team, never a 5-player pachanga
            if (storyTeamIds != null && tp.TeamConfigID != 0 && storyTeamIds.Contains(tp.TeamConfigID))
                return false;

            // Overworld random pachangas in IEGO1 have TeamConfigID == 0 (their 5 players come from *.encount.bin)
            // or reference a non-story SoccerMode == 1 / ifbXXXX mini-battle formation!
            if (tp.TeamConfigID == 0)
                return true;

            if (miniBattleParamIds != null && miniBattleParamIds.Contains(tp.TeamParamID) &&
                (fullMatchParamIds == null || !fullMatchParamIds.Contains(tp.TeamParamID)))
            {
                return true;
            }

            if (miniFormationSet != null && tp.FormationID != 0 && miniFormationSet.Contains(tp.FormationID))
                return true;

            return false;
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
