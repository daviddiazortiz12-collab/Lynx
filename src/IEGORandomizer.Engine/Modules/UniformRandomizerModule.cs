using System;
using System.Collections.Generic;
using System.Linq;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Protection;
using IEGORandomizer.Engine.Seeding;
using IEGORandomizer.Engine.Validation;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Engine.Modules
{
    /// <summary>
    /// Subsystem E: Randomizes Rival/Route and Pachanga Team Uniforms (<c>TeamParamInfo.Uniform</c>,
    /// encoded in IEGO1 as <c>kitNumber * 1000 + homeAwayFlag</c> where <c>homeAwayFlag</c> is <c>0</c> or <c>2</c>),
    /// synchronizing overworld <c>EncountMapRecord.Uniform</c> and strictly restricting choices to kits
    /// verified by <see cref="VisualCoherenceValidator"/> to possess complete 3D body models (<c>uza/uzbXXXX.xc</c>).
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

            var validKits = visualProfile.ValidKitNumbers.OrderBy(k => k).ToList();
            if (validKits.Count == 0)
                return;

            if (!options.RandomizeRivalUniforms && !options.RandomizePachangaUniforms)
                return;

            DeterministicRng kitRng = seeds.GetSubsystem("uniforms.kits");
            var teamParams = game.GetTeamParams();
            var encountIds = new HashSet<int>(game.GetEncounterTeamsOnly().Select(e => e.TeamConfigID));
            var pachangaKitByTeamId = new Dictionary<int, int>();
            bool modified = false;

            foreach (var tp in teamParams)
            {
                if (tp.TeamParamID == 0)
                    continue;

                bool isPachanga = encountIds.Contains(tp.TeamConfigID);
                if (isPachanga && !options.RandomizePachangaUniforms)
                    continue;
                if (!isPachanga && !options.RandomizeRivalUniforms)
                    continue;

                // Keep scripted main-story match uniforms intact if they rely on story-specific kit cutscenes
                if (protection.ScriptedTeamParamHashes.Contains(tp.TeamParamID) ||
                    protection.ScriptedTeamConfigHashes.Contains(tp.TeamConfigID))
                {
                    continue;
                }

                int currentKitNum = tp.Uniform / 1000;
                int chosenKitNum = ChooseDifferentKit(validKits, currentKitNum, kitRng);
                int variantOffset = kitRng.NextChancePercent(25) ? 2 : 0; // 0 = Home kit, 2 = Away kit
                int encodedUniform = (chosenKitNum * 1000) + variantOffset;

                tp.Uniform = encodedUniform;
                modified = true;
                report.RandomizedTeamUniformsCount++;

                if (isPachanga && tp.TeamConfigID != 0)
                {
                    pachangaKitByTeamId[tp.TeamConfigID] = encodedUniform;
                }
            }

            if (modified)
            {
                game.SaveTeamParams(teamParams);
            }

            // Synchronize overworld 3D pachanga player uniforms with their battle TeamParam uniform
            if (pachangaKitByTeamId.Count > 0)
            {
                var spawns = MapDataService.LoadAllEncounterSpawns(game.IE_A.Directory);
                bool spawnsChanged = false;
                foreach (var sp in spawns)
                {
                    if (pachangaKitByTeamId.TryGetValue(sp.Record.TeamID, out int kitCode))
                    {
                        sp.Record.Uniform = kitCode;
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
