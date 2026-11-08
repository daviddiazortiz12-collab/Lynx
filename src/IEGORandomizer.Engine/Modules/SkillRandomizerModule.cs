using System;
using System.Collections.Generic;
using System.Linq;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Protection;
using IEGORandomizer.Engine.Seeding;
using Lynx.Core.Games;
using Lynx.Core.RomFs;
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Engine.Modules
{
    /// <summary>
    /// Subsystem B: Randomizes initial skills, level-up learnsets (<c>skill_table.cfg.bin</c>),
    /// passive talents, Fighting Spirit skills (<c>IAvatar.SpecialMoveID</c> &amp; <c>SkillID</c>),
    /// and optional skill parameters (<c>TPCost</c>, <c>Power</c>, <c>Element</c>).
    /// Enforces player/avatar skill-domain separation, role compatibility, script-forced skill safety,
    /// and guaranteed Level-1 TP usability.
    /// </summary>
    public static class SkillRandomizerModule
    {
        public static void Execute(
            SafeGOGame game,
            SeedEngine seeds,
            SkillsRandomizerOptions options,
            StoryProtectionContext protection,
            RandomizationExecutionReport report)
        {
            if (options == null || !options.Enabled)
                return;

            var skills = game.GetSkillConfigs(false);
            var skillTable = game.GetSkillTable();
            var charaParams = game.GetCharaparams();
            var avatars = game.GetAvatars(false);

            if (skills.Length == 0)
                return;

            // Identify Fighting Spirit exclusive skills ("wk*") vs standard player skills ("wh*")
            var wkLookup = Level5IdHelper.BuildPrefixLookup("wks", "wko", "wkd", "wkk", "wkp");
            var avatarMoveHashes = new HashSet<int>(wkLookup.Keys);
            foreach (var av in avatars)
            {
                if (av.SpecialMoveID != 0) avatarMoveHashes.Add(av.SpecialMoveID);
                if (av.SkillID != 0) avatarMoveHashes.Add(av.SkillID);
            }

            var skillByHash = skills
                .Where(s => s.SkillHash != 0)
                .GroupBy(s => s.SkillHash)
                .ToDictionary(g => g.Key, g => g.First());

            // Exclude Ultimate Evolution Manual dummy rows (whs9999, who9999, whd9999, whk9999)
            // which appear in retail skill_config.cfg.bin with SkillPosition 1..4, Power = 0, TPCost = 1.
            var dummyManualHashes = new HashSet<int>
            {
                Level5IdHelper.ComputeCrc32Utf8("whs9999"),
                Level5IdHelper.ComputeCrc32Utf8("who9999"),
                Level5IdHelper.ComputeCrc32Utf8("whd9999"),
                Level5IdHelper.ComputeCrc32Utf8("whk9999")
            };

            // 1. Optional Skill Parameter Randomization (TPCost, Power, Element)
            if (options.RandomizeSkillParameters)
            {
                DeterministicRng paramRng = seeds.GetSubsystem("skills.params");
                int variance = Math.Clamp(options.SkillParamVariancePercent, 5, 50);

                foreach (var sk in skills)
                {
                    if (sk.SkillHash == 0 || dummyManualHashes.Contains(sk.SkillHash))
                        continue;

                    // SkillPosition 5 = Passive Skill/Talent (TPCost = 0, Power = 0); skip dummy 0-power active rows
                    if (sk.SkillPosition == 5 || sk.TPCost <= 1 || sk.Power <= 0)
                        continue;

                    if (sk.Element >= 1 && sk.Element <= 4)
                    {
                        sk.Element = paramRng.NextIntInclusive(1, 4);
                    }

                    int tpDelta = paramRng.NextIntInclusive(-variance, variance);
                    sk.TPCost = Math.Clamp((int)Math.Round(sk.TPCost * (100 + tpDelta) / 100.0), 15, 85);

                    if (sk.Power > 0)
                    {
                        int powDelta = paramRng.NextIntInclusive(-variance, variance);
                        sk.Power = Math.Clamp((int)Math.Round(sk.Power * (100 + powDelta) / 100.0), 30, 250);
                    }

                    report.RandomizedSkillParamsCount++;
                }

                game.SaveSkillConfigs(skills);
            }

            // Build categorized player-usable skill pools (MovePositions.json: 1=Save, 2=Shot, 3=Dribble, 4=Block, 5=Talent)
            var playerMoves = skills
                .Where(s => s.SkillHash != 0 &&
                            !avatarMoveHashes.Contains(s.SkillHash) &&
                            !dummyManualHashes.Contains(s.SkillHash))
                .ToList();

            var saveMoves = playerMoves.Where(s => s.SkillPosition == 1 && s.Power > 0 && s.TPCost > 1).ToList();
            var shotMoves = playerMoves.Where(s => s.SkillPosition == 2 && s.Power > 0 && s.TPCost > 1).ToList();
            var dribbleMoves = playerMoves.Where(s => s.SkillPosition == 3 && s.Power > 0 && s.TPCost > 1).ToList();
            var blockMoves = playerMoves.Where(s => s.SkillPosition == 4 && s.Power > 0 && s.TPCost > 1).ToList();
            var activeFieldMoves = playerMoves.Where(s => s.SkillPosition >= 1 && s.SkillPosition <= 4 && s.Power > 0 && s.TPCost > 1).ToList();
            var talentMoves = playerMoves.Where(s => s.SkillPosition == 5).ToList();

            // 2. Player Initial Skills, Level-Up Learnsets, and Talents
            if ((options.RandomizeInitialSkills || options.RandomizeLevelUpLearnsets || options.RandomizeTalents) &&
                activeFieldMoves.Count > 0 &&
                skillTable.Length > 0)
            {
                DeterministicRng learnRng = seeds.GetSubsystem("skills.learnsets");
                bool charaParamsModified = false;

                foreach (var cp in charaParams)
                {
                    if (cp.ParamHash == 0 || cp.SpecialMoveCount <= 0)
                        continue;
                    if (cp.SpecialMoveOffset < 0 || cp.SpecialMoveOffset + cp.SpecialMoveCount > skillTable.Length)
                        continue;

                    bool isProtectedPlayer = protection.ProtectedStoryPlayerParamHashes.Contains(cp.ParamHash);
                    var assignedInPlayer = new HashSet<int>();

                    // Compute usable TP at Level 1 so initial skills can always be executed
                    int usableLv1Tp = EstimateUsableInitialTp(game, cp);

                    for (int slot = 0; slot < cp.SpecialMoveCount; slot++)
                    {
                        ISkillTable entry = skillTable[cp.SpecialMoveOffset + slot];
                        if (entry.SkillHash == 0)
                            continue;

                        // Never overwrite a skill explicitly invoked by a story script on a story character
                        if (isProtectedPlayer && protection.ScriptReferencedSkillHashes.Contains(entry.SkillHash))
                        {
                            assignedInPlayer.Add(entry.SkillHash);
                            continue;
                        }

                        bool isInitialSlot = slot == 0 || entry.LevelLearned <= 5;
                        bool wasTalent = skillByHash.TryGetValue(entry.SkillHash, out var origSkill) &&
                                         origSkill.SkillPosition == 5;

                        if (wasTalent)
                        {
                            if (!options.RandomizeTalents || talentMoves.Count == 0)
                            {
                                assignedInPlayer.Add(entry.SkillHash);
                                continue;
                            }

                            int pickedTalent = PickUniqueSkillHash(talentMoves, assignedInPlayer, null, learnRng);
                            entry.SkillHash = pickedTalent;
                            assignedInPlayer.Add(pickedTalent);
                            report.RandomizedSkillSlotsCount++;
                            continue;
                        }

                        if (isInitialSlot && !options.RandomizeInitialSkills)
                        {
                            assignedInPlayer.Add(entry.SkillHash);
                            continue;
                        }

                        if (!isInitialSlot && !options.RandomizeLevelUpLearnsets)
                        {
                            assignedInPlayer.Add(entry.SkillHash);
                            continue;
                        }

                        IReadOnlyList<ISkillConfig> candidatePool = SelectCompatiblePool(
                            cp.PlayerPosition,
                            slot,
                            options.EnforcePositionCompatibility,
                            saveMoves,
                            shotMoves,
                            dribbleMoves,
                            blockMoves,
                            activeFieldMoves,
                            learnRng);

                        int? maxTpConstraint = (isInitialSlot && options.EnsureUsableInitialTp)
                            ? usableLv1Tp
                            : null;

                        int chosenHash = PickUniqueSkillHash(candidatePool, assignedInPlayer, maxTpConstraint, learnRng);
                        entry.SkillHash = chosenHash;
                        assignedInPlayer.Add(chosenHash);
                        report.RandomizedSkillSlotsCount++;

                        // If even the cheapest move in the pool exceeded the player's Lv.1 TP, bump TP safely
                        if (isInitialSlot &&
                            options.EnsureUsableInitialTp &&
                            skillByHash.TryGetValue(chosenHash, out var chosenCfg) &&
                            chosenCfg.TPCost > usableLv1Tp)
                        {
                            cp.TP = Math.Max(cp.TP, chosenCfg.TPCost + 20);
                            usableLv1Tp = EstimateUsableInitialTp(game, cp);
                            charaParamsModified = true;
                        }
                    }
                }

                game.SaveSkillTable(skillTable);
                if (charaParamsModified)
                {
                    game.SaveCharaparams(charaParams);
                }
            }

            // 3. Fighting Spirit (Avatar) Skills
            if (options.RandomizeFightingSpiritSkills && avatars.Length > 0)
            {
                DeterministicRng avSkillRng = seeds.GetSubsystem("skills.avatars");

                var avatarActivePool = skills
                    .Where(s => avatarMoveHashes.Contains(s.SkillHash) &&
                                !dummyManualHashes.Contains(s.SkillHash) &&
                                s.SkillPosition >= 1 && s.SkillPosition <= 4 &&
                                s.Power > 0)
                    .ToList();

                var avatarPassivePool = avatars
                    .Select(a => a.SkillID)
                    .Where(h => h != 0)
                    .Distinct()
                    .ToList();

                if (avatarActivePool.Count > 0)
                {
                    foreach (var av in avatars)
                    {
                        if (av.AvatarHash == 0)
                            continue;
                        if (protection.ScriptReferencedAvatarHashes.Contains(av.AvatarHash) &&
                            protection.ScriptReferencedSkillHashes.Contains(av.SpecialMoveID))
                        {
                            continue;
                        }

                        // Match avatar position role when possible (PlayerPositions: 1=GK->Save(1), 2=FW->Shot(2), 3=MF->Dribble/Shot, 4=DF->Block(4))
                        var roleMatches = avatarActivePool
                            .Where(s => IsMoveMatchingAvatarPosition(av.Position, s.SkillPosition))
                            .ToList();
                        var pool = roleMatches.Count > 0 ? roleMatches : avatarActivePool;

                        av.SpecialMoveID = avSkillRng.PickOne(pool).SkillHash;
                        if (av.SkillID != 0 && avatarPassivePool.Count > 0)
                        {
                            av.SkillID = avSkillRng.PickOne(avatarPassivePool);
                        }
                        report.RandomizedAvatarSkillsCount++;
                    }

                    game.SaveAvatars(avatars);
                }
            }
        }

        private static IReadOnlyList<ISkillConfig> SelectCompatiblePool(
            int playerPosition,
            int slotIndex,
            bool enforceCompatibility,
            List<ISkillConfig> saveMoves,
            List<ISkillConfig> shotMoves,
            List<ISkillConfig> dribbleMoves,
            List<ISkillConfig> blockMoves,
            List<ISkillConfig> allActiveMoves,
            DeterministicRng rng)
        {
            if (!enforceCompatibility)
                return allActiveMoves;

            // PlayerPositions.json: 1=Goalkeeper, 2=Forward, 3=Midfielder, 4=Defender
            if (slotIndex == 0)
            {
                return playerPosition switch
                {
                    1 when saveMoves.Count > 0 => saveMoves,
                    2 when shotMoves.Count > 0 => shotMoves,
                    3 when dribbleMoves.Count > 0 => dribbleMoves,
                    4 when blockMoves.Count > 0 => blockMoves,
                    _ => allActiveMoves
                };
            }

            // Secondary slots keep GKs out of useless 4-shot builds and field players out of GK-only saves
            if (playerPosition == 1)
            {
                if (slotIndex == 1 && saveMoves.Count > 0)
                    return saveMoves;
                var gkChoices = saveMoves.Concat(blockMoves).Concat(dribbleMoves).ToList();
                return gkChoices.Count > 0 ? gkChoices : allActiveMoves;
            }

            var nonSaveMoves = shotMoves.Concat(dribbleMoves).Concat(blockMoves).ToList();
            if (nonSaveMoves.Count == 0)
                return allActiveMoves;

            // Bias secondary slots toward player role while allowing natural variety
            if (rng.NextChancePercent(60))
            {
                return playerPosition switch
                {
                    2 when shotMoves.Count > 0 => shotMoves,
                    3 when dribbleMoves.Count > 0 => dribbleMoves,
                    4 when blockMoves.Count > 0 => blockMoves,
                    _ => nonSaveMoves
                };
            }

            return nonSaveMoves;
        }

        private static int PickUniqueSkillHash(
            IReadOnlyList<ISkillConfig> pool,
            HashSet<int> alreadyAssigned,
            int? maxTpCost,
            DeterministicRng rng)
        {
            var filtered = pool
                .Where(s => !alreadyAssigned.Contains(s.SkillHash) &&
                            (!maxTpCost.HasValue || s.TPCost <= maxTpCost.Value))
                .ToList();

            if (filtered.Count > 0)
                return rng.PickOne(filtered).SkillHash;

            // Fallback 1: relax uniqueness before relaxing TP ceiling
            if (maxTpCost.HasValue)
            {
                var tpSafe = pool.Where(s => s.TPCost <= maxTpCost.Value).ToList();
                if (tpSafe.Count > 0)
                    return rng.PickOne(tpSafe).SkillHash;

                // Pick lowest-TP skill in pool
                return pool.OrderBy(s => s.TPCost).First().SkillHash;
            }

            var nonDup = pool.Where(s => !alreadyAssigned.Contains(s.SkillHash)).ToList();
            if (nonDup.Count > 0)
                return rng.PickOne(nonDup).SkillHash;

            return rng.PickOne(pool).SkillHash;
        }

        private static int EstimateUsableInitialTp(SafeGOGame game, ICharaparam cp)
        {
            // Uses the exact 0x0033E404 stat calculator ported in Lynx's GOStatCalculator
            try
            {
                if (game.StatCalculator != null)
                {
                    int profile = cp.StatVariance >= 1 && cp.StatVariance <= game.StatCalculator.ProfileCount
                        ? cp.StatVariance
                        : 1;
                    int elem = cp.Element >= 1 && cp.Element <= 4 ? cp.Element : 1;
                    int lv1Tp = game.StatCalculator.GetStat(PlayerStats.TP, cp.TP, cp.TPGrow, 1, elem, profile);
                    if (lv1Tp > 0)
                        return lv1Tp;
                }
            }
            catch
            {
                // Fallback to raw base TP
            }

            return Math.Max(20, cp.TP);
        }

        private static bool IsMoveMatchingAvatarPosition(int avatarPosition, int skillPosition)
        {
            return avatarPosition switch
            {
                1 => skillPosition == 1, // GK -> Save
                2 => skillPosition == 2, // FW -> Shot
                3 => skillPosition == 3 || skillPosition == 2, // MF -> Dribble/Shot
                4 => skillPosition == 4, // DF -> Block
                _ => true
            };
        }
    }
}
