using System;
using System.Collections.Generic;
using System.Linq;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Protection;
using IEGORandomizer.Engine.Seeding;
using IEGORandomizer.Engine.Validation;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.Core.RomFs;
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Engine.Modules
{
    /// <summary>
    /// Subsystem E: Randomizes Rival/Route and Pachanga Team Uniforms (<c>TeamParamInfo.Uniform</c>,
    /// encoded in IEGO1 as <c>kitNumber * 1000 + homeAwayFlag</c> where <c>homeAwayFlag</c> is <c>0</c> or <c>2</c>),
    /// synchronizing overworld <c>EncountMapRecord.Uniform</c> and strictly restricting choices to kits
    /// verified by <see cref="VisualCoherenceValidator"/> to possess complete 3D body models
    /// (<c>uzaXXXX.xc</c> for 5v5 pachangas, <c>uzaXXXX.xc + uzbXXXX.xc</c> for 11v11 rival teams with GK,
    /// and <c>uzcXXXX.xc</c> / <c>uzdXXXX.xc</c> before selecting Away variant <c>+2</c>).
    /// </summary>
    public static class UniformRandomizerModule
    {
        public static void Execute(
            SafeGOGame game,
            SeedEngine seeds,
            UniformsRandomizerOptions options,
            StoryProtectionContext protection,
            VisualCoherenceProfile visualProfile,
            RandomizationExecutionReport report)
        {
            if (options == null || !options.Enabled)
                return;

            var validBaseKits = visualProfile.ValidKitNumbers.OrderBy(k => k).ToList();
            if (validBaseKits.Count == 0)
                return;

            // Require complete field + goalkeeper 3D models (uzaXXXX.xc + uzbXXXX.xc) for ALL teams whenever available,
            // because 5v5 pachangas with "Score a goal" victory conditions also render goalkeeper meshes!
            var validCompleteKits = visualProfile.ValidGoalkeeperKitNumbers.Count > 0
                ? visualProfile.ValidGoalkeeperKitNumbers.OrderBy(k => k).ToList()
                : validBaseKits;

            // Build set of kit numbers registered as actual ITEM_UNIFORM ("iiuXXXX") items in item_config.cfg.bin:
            // Overworld ENCOUNT_INFO.Uniform stores CRC32("iiuXXXX"), so assigning a body number that lacks an
            // ITEM_UNIFORM entry causes a null item lookup crash when spawning the pachanga on the map!
            var registeredUniformItems = game.GetItems("uniform") ?? Array.Empty<IItemConfig>();
            var iiuLookup = Level5IdHelper.BuildPrefixLookup("iiu");
            var registeredIiuKitNums = new HashSet<int>();
            foreach (var uItem in registeredUniformItems)
            {
                if (uItem.ItemID != 0 && iiuLookup.TryGetValue(uItem.ItemID, out string code) &&
                    int.TryParse(code.AsSpan(3), out int uNum) &&
                    uNum > 0 && uNum < VisualCoherenceValidator.MaxStandardSoccerKitNumberExclusive)
                {
                    registeredIiuKitNums.Add(uNum);
                }
            }

            var validPachangaKits = registeredIiuKitNums.Count > 0
                ? validCompleteKits.Where(k => registeredIiuKitNums.Contains(k)).ToList()
                : validCompleteKits;
            if (validPachangaKits.Count == 0)
                validPachangaKits = validCompleteKits;

            if (!options.RandomizeRivalUniforms && !options.RandomizePachangaUniforms)
                return;

            DeterministicRng kitRng = seeds.GetSubsystem("uniforms.kits");
            var teamParams = game.GetTeamParams();
            var storyIds = new HashSet<int>(game.GetStoryTeamsOnly().Select(s => s.TeamConfigID).Where(id => id != 0));
            var encountIds = new HashSet<int>(game.GetEncounterTeamsOnly().Select(e => e.TeamConfigID).Where(id => id != 0));
            var soccers = game.GetSoccers() ?? Array.Empty<ISoccerInfo>();
            var miniBattleParamIds = new HashSet<int>(
                soccers.Where(s => s != null && s.SoccerMode == 1 && s.TeamParamID != 0).Select(s => s.TeamParamID));
            var fullMatchParamIds = new HashSet<int>(
                soccers.Where(s => s != null && s.SoccerMode == 2 && s.TeamParamID != 0).Select(s => s.TeamParamID));

            var pachangaKitByKey = new Dictionary<int, (int ChosenKitNum, int EncodedUniform)>();
            bool modified = false;

            foreach (var tp in teamParams)
            {
                if (tp.TeamParamID == 0)
                    continue;

                // CRITICAL: Never overwrite Uniform <= 0 (Episode 1 street-clothes / school-uniform pachangas)
                // OR Uniform >= 900000 (special story outfits such as 903000 Black Knights hoodie in btl0302/btl0002)!
                if (tp.Uniform <= 0 || tp.Uniform >= (VisualCoherenceValidator.MaxStandardSoccerKitNumberExclusive * 1000))
                    continue;

                bool isPachanga = TeamRandomizerModule.IsMiniBattleTeamParam(
                    tp,
                    storyIds,
                    encountIds,
                    miniBattleParamIds,
                    fullMatchParamIds);

                if (isPachanga && !options.RandomizePachangaUniforms)
                    continue;
                if (!isPachanga && !options.RandomizeRivalUniforms)
                    continue;

                // Keep Raimon's own preset story rosters in Raimon uniform, plus any manually pinned team
                if (protection.RaimonCoreTeamConfigHashes.Contains(tp.TeamConfigID) ||
                    protection.ScriptedTeamParamHashes.Contains(tp.TeamParamID) ||
                    (tp.TeamConfigID != 0 && protection.ScriptedTeamConfigHashes.Contains(tp.TeamConfigID)))
                {
                    continue;
                }

                int currentKitNum = tp.Uniform / 1000;
                var candidatePool = isPachanga ? validPachangaKits : validCompleteKits;
                int chosenKitNum = ChooseDifferentKit(candidatePool, currentKitNum, kitRng);

                bool canUseAway = visualProfile.ValidAwayGoalkeeperKitNumbers.Contains(chosenKitNum) ||
                                  (visualProfile.ValidGoalkeeperKitNumbers.Count == 0 && visualProfile.ValidAwayKitNumbers.Contains(chosenKitNum));

                int variantOffset = (canUseAway && kitRng.NextChancePercent(25)) ? 2 : 0;
                int encodedUniform = (chosenKitNum * 1000) + variantOffset;

                tp.Uniform = encodedUniform;
                modified = true;
                report.RandomizedTeamUniformsCount++;

                if (isPachanga)
                {
                    if (tp.TeamConfigID != 0)
                        pachangaKitByKey[tp.TeamConfigID] = (chosenKitNum, encodedUniform);
                    pachangaKitByKey[tp.TeamParamID] = (chosenKitNum, encodedUniform);
                }
            }

            foreach (var sc in soccers)
            {
                if (sc != null && sc.SoccerID != 0 && sc.TeamParamID != 0 &&
                    pachangaKitByKey.TryGetValue(sc.TeamParamID, out var choice) &&
                    !pachangaKitByKey.ContainsKey(sc.SoccerID))
                {
                    pachangaKitByKey[sc.SoccerID] = choice;
                }
            }

            if (modified)
            {
                game.SaveTeamParams(teamParams);
            }

            // Synchronize overworld 3D pachanga player uniforms with their battle TeamParam uniform
            if (pachangaKitByKey.Count > 0)
            {
                var spawns = MapDataService.LoadAllEncounterSpawns(game.IE_A.Directory);
                bool spawnsChanged = false;
                foreach (var sp in spawns)
                {
                    // Never overwrite Uniform == 0 (street clothes / default map body model)!
                    if (sp.Record.Uniform == 0)
                        continue;

                    if (pachangaKitByKey.TryGetValue(sp.Record.TeamID, out var kitChoice))
                    {
                        // In Ocelot / retail IEGO1 ENCOUNT_INFO, Uniform is stored as CRC32("iiuXXXX") ItemID
                        // when non-zero hash, or as small encoded kit integer (kitNum * 1000) in custom/test tables.
                        if (sp.Record.Uniform > 0 && sp.Record.Uniform < 2000000)
                        {
                            sp.Record.Uniform = kitChoice.EncodedUniform;
                        }
                        else
                        {
                            sp.Record.Uniform = Level5IdHelper.ComputeCrc32Utf8($"iiu{kitChoice.ChosenKitNum:D4}");
                        }
                        spawnsChanged = true;
                    }
                }
                if (spawnsChanged)
                {
                    MapDataService.SaveEncounterSpawns(game.IE_A.Directory, spawns);
                }
            }
        }

        private static int ChooseDifferentKit(IReadOnlyList<int> validKits, int currentKitNum, DeterministicRng rng)
        {
            if (validKits.Count == 1)
                return validKits[0];

            for (int i = 0; i < 6; i++)
            {
                int candidate = rng.PickOne(validKits);
                if (candidate != currentKitNum)
                    return candidate;
            }
            return rng.PickOne(validKits);
        }
    }
}
