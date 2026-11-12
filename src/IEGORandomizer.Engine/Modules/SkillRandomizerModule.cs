using System;
using System.Collections.Generic;
using System.Linq;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Protection;
using IEGORandomizer.Engine.Seeding;
using IEGORandomizer.Engine.Validation;
using Lynx.Core.Games;
using Lynx.Core.RomFs;
using Lynx.Core.Scripts;
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Engine.Modules
{
    /// <summary>
    /// Subsystem B: Randomizes initial skills, level-up learnsets (<c>skill_table.cfg.bin</c>),
    /// passive talents, Fighting Spirit passives (<c>IAvatar.SkillID</c>, strictly preserving
    /// <c>IAvatar.SpecialMoveID</c> 1-to-1 skeletal motion <c>vkaNNNN_ckNNNN.xc</c>),
    /// optional skill parameters (<c>TPCost</c>, <c>Power</c>, <c>Element</c>),
    /// guaranteed Level-1 starter technique or initial Fighting Spirit for every playable character
    /// (<c>100%</c> guaranteed Level-1 active move for Arion <c>para_cp0008</c> and JP <c>para_cp0005</c>),
    /// and Dynamic Story-Technique Swap mappings for Squirrel <c>.nutb</c> event &amp; battle scripts.
    /// </summary>
    public static class SkillRandomizerModule
    {
        public static void Execute(
            SafeGOGame game,
            SeedEngine seeds,
            SkillsRandomizerOptions options,
            StoryProtectionContext protection,
            VisualCoherenceProfile visualProfile,
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

            // Precompute canonical Level-5 CRC32 lookups for genuine IEGO1 player & Fighting Spirit skills:
            // Player active skills: whsXXXX (Shoot), whoXXXX (Dribble), whdXXXX (Block), whkXXXX (Save) [0001..8999]
            // Player passive talents: whpXXXX [0001..8999] (retail SkillPosition == 15; synthetic test == 5)
            // Fighting Spirit skills: wksXXXX (Shoot), wkoXXXX (Dribble), wkdXXXX (Block), wkkXXXX (Save), wkpXXXX (Passive)
            var whsLookup = BuildRangeCodeLookup("whs", 1, 8999);
            var whoLookup = BuildRangeCodeLookup("who", 1, 8999);
            var whdLookup = BuildRangeCodeLookup("whd", 1, 8999);
            var whkLookup = BuildRangeCodeLookup("whk", 1, 8999);
            var whpLookup = BuildRangeCodeLookup("whp", 1, 8999);

            var whsSet = new HashSet<int>(whsLookup.Keys);
            var whoSet = new HashSet<int>(whoLookup.Keys);
            var whdSet = new HashSet<int>(whdLookup.Keys);
            var whkSet = new HashSet<int>(whkLookup.Keys);
            var whpSet = new HashSet<int>(whpLookup.Keys);

            var skillCodeByHash = new Dictionary<int, string>(
                whsLookup.Count + whoLookup.Count + whdLookup.Count + whkLookup.Count + whpLookup.Count);
            foreach (var kv in whsLookup) skillCodeByHash[kv.Key] = kv.Value;
            foreach (var kv in whoLookup) skillCodeByHash[kv.Key] = kv.Value;
            foreach (var kv in whdLookup) skillCodeByHash[kv.Key] = kv.Value;
            foreach (var kv in whkLookup) skillCodeByHash[kv.Key] = kv.Value;
            foreach (var kv in whpLookup) skillCodeByHash[kv.Key] = kv.Value;

            var paraCpLookup = Level5IdHelper.BuildPrefixLookup("para_cp");
            int arionParamHash = Level5IdHelper.ComputeCrc32Utf8("para_cp0008");
            int jpParamHash = Level5IdHelper.ComputeCrc32Utf8("para_cp0005");

            var wksSet = BuildRangeHashSet("wks", 1, 8999);
            var wkoSet = BuildRangeHashSet("wko", 1, 8999);
            var wkdSet = BuildRangeHashSet("wkd", 1, 8999);
            var wkkSet = BuildRangeHashSet("wkk", 1, 8999);
            var wkpSet = BuildRangeHashSet("wkp", 1, 8999);

            var avatarMoveHashes = new HashSet<int>(
                wksSet.Concat(wkoSet).Concat(wkdSet).Concat(wkkSet).Concat(wkpSet));
            foreach (var av in avatars)
            {
                if (av.SpecialMoveID != 0) avatarMoveHashes.Add(av.SpecialMoveID);
                if (av.SkillID != 0) avatarMoveHashes.Add(av.SkillID);
            }

            var skillByHash = skills
                .Where(s => s.SkillHash != 0)
                .GroupBy(s => s.SkillHash)
                .ToDictionary(g => g.Key, g => g.First());

            // Capture original retail skill_table hashes BEFORE mutating skillTable:
            // on full retail archives (skillTable.Length >= 100), intersecting with vanilla skill_table guarantees
            // that unused/cut wh* stubs lacking .waza camera scripts can never be assigned to any player!
            var vanillaSkillTableHashes = new HashSet<int>(
                skillTable.Where(st => st != null && st.SkillHash != 0).Select(st => st.SkillHash));

            var dummyManualHashes = new HashSet<int>
            {
                Level5IdHelper.ComputeCrc32Utf8("whs9999"),
                Level5IdHelper.ComputeCrc32Utf8("who9999"),
                Level5IdHelper.ComputeCrc32Utf8("whd9999"),
                Level5IdHelper.ComputeCrc32Utf8("whk9999"),
                Level5IdHelper.ComputeCrc32Utf8("whp9999")
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

                    bool isStandardPlayerActive = whsSet.Contains(sk.SkillHash) ||
                                                  whoSet.Contains(sk.SkillHash) ||
                                                  whdSet.Contains(sk.SkillHash) ||
                                                  whkSet.Contains(sk.SkillHash);
                    bool isAvatarActive = wksSet.Contains(sk.SkillHash) ||
                                          wkoSet.Contains(sk.SkillHash) ||
                                          wkdSet.Contains(sk.SkillHash) ||
                                          wkkSet.Contains(sk.SkillHash);

                    if ((!isStandardPlayerActive && !isAvatarActive) ||
                        whpSet.Contains(sk.SkillHash) ||
                        sk.SkillPosition == 15 ||
                        sk.SkillPosition == 5 ||
                        sk.TPCost <= 1 ||
                        sk.Power <= 0)
                    {
                        continue;
                    }

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

            // Build categorized player-usable skill pools strictly from genuine wh[sodkp]0001..8999 entries
            var candidateActiveMoves = skills
                .Where(s => s.SkillHash != 0 &&
                            s.SkillType != 2 &&
                            !avatarMoveHashes.Contains(s.SkillHash) &&
                            !dummyManualHashes.Contains(s.SkillHash) &&
                            s.Power > 0 &&
                            s.TPCost > 1 &&
                            (whsSet.Contains(s.SkillHash) ||
                             whoSet.Contains(s.SkillHash) ||
                             whdSet.Contains(s.SkillHash) ||
                             whkSet.Contains(s.SkillHash)))
                .ToList();

            if (skillTable.Length >= 100)
            {
                var retailVerifiedActives = candidateActiveMoves
                    .Where(s => vanillaSkillTableHashes.Contains(s.SkillHash))
                    .ToList();
                if (retailVerifiedActives.Count >= 40)
                    candidateActiveMoves = retailVerifiedActives;
            }

            var saveMoves = candidateActiveMoves.Where(s => whkSet.Contains(s.SkillHash)).ToList();
            var shotMoves = candidateActiveMoves.Where(s => whsSet.Contains(s.SkillHash)).ToList();
            var dribbleMoves = candidateActiveMoves.Where(s => whoSet.Contains(s.SkillHash)).ToList();
            var blockMoves = candidateActiveMoves.Where(s => whdSet.Contains(s.SkillHash)).ToList();
            var activeFieldMoves = candidateActiveMoves;

            var talentMoves = skills
                .Where(s => s.SkillHash != 0 &&
                            s.SkillType != 2 &&
                            !avatarMoveHashes.Contains(s.SkillHash) &&
                            !dummyManualHashes.Contains(s.SkillHash) &&
                            (whpSet.Contains(s.SkillHash) || s.SkillPosition == 15 || s.SkillPosition == 5) &&
                            s.TPCost == 0)
                .Where(s => whpSet.Contains(s.SkillHash))
                .ToList();

            if (skillTable.Length >= 100)
            {
                var retailVerifiedTalents = talentMoves
                    .Where(s => vanillaSkillTableHashes.Contains(s.SkillHash))
                    .ToList();
                if (retailVerifiedTalents.Count >= 10)
                    talentMoves = retailVerifiedTalents;
            }

            // 2. Player Initial Skills, Level-Up Learnsets, Talents, Starter Guarantee & Dynamic Story Swap
            if ((options.RandomizeInitialSkills ||
                 options.RandomizeLevelUpLearnsets ||
                 options.RandomizeTalents ||
                 options.GuaranteeInitialMoveOrSpiritForAllPlayers ||
                 options.GuaranteeProtagonistInitialSkill) &&
                activeFieldMoves.Count > 0 &&
                skillTable.Length > 0)
            {
                DeterministicRng learnRng = seeds.GetSubsystem("skills.learnsets");
                bool charaParamsModified = false;
                bool skillTableModified = false;

                // Snapshot vanilla skill_table slots before mutation for Dynamic Story-Technique Swap mapping
                var vanillaSlotHashesByPlayer = new Dictionary<int, (int SkillHash, int LevelLearned)[]>(charaParams.Length);
                foreach (var cp in charaParams)
                {
                    if (cp.ParamHash == 0 || cp.SpecialMoveCount <= 0 ||
                        cp.SpecialMoveOffset < 0 || cp.SpecialMoveOffset + cp.SpecialMoveCount > skillTable.Length)
                    {
                        continue;
                    }

                    var snap = new (int SkillHash, int LevelLearned)[cp.SpecialMoveCount];
                    for (int s = 0; s < cp.SpecialMoveCount; s++)
                    {
                        snap[s] = (skillTable[cp.SpecialMoveOffset + s].SkillHash, skillTable[cp.SpecialMoveOffset + s].LevelLearned);
                    }
                    vanillaSlotHashesByPlayer[cp.ParamHash] = snap;
                }

                if (options.RandomizeInitialSkills || options.RandomizeLevelUpLearnsets || options.RandomizeTalents)
                {
                    foreach (var cp in charaParams)
                    {
                        if (cp.ParamHash == 0 || cp.SpecialMoveCount <= 0)
                            continue;
                        if (cp.SpecialMoveOffset < 0 || cp.SpecialMoveOffset + cp.SpecialMoveCount > skillTable.Length)
                            continue;

                        bool isProtectedPlayer = protection.ProtectedStoryPlayerParamHashes.Contains(cp.ParamHash);
                        var assignedInPlayer = new HashSet<int>();
                        int usableLv1Tp = EstimateUsableInitialTp(game, cp);

                        for (int slot = 0; slot < cp.SpecialMoveCount; slot++)
                        {
                            ISkillTable entry = skillTable[cp.SpecialMoveOffset + slot];
                            if (entry.SkillHash == 0)
                                continue;

                            // Only freeze script-referenced skills when Dynamic Story-Technique Swap is explicitly disabled
                            if (!options.EnableDynamicStoryTechniqueSwap &&
                                isProtectedPlayer &&
                                protection.ScriptReferencedSkillHashes.Contains(entry.SkillHash))
                            {
                                assignedInPlayer.Add(entry.SkillHash);
                                continue;
                            }

                            int originalHash = entry.SkillHash;
                            bool isInitialSlot = slot == 0 || entry.LevelLearned <= 5;
                            bool wasTalent = whpSet.Contains(originalHash) ||
                                             (skillByHash.TryGetValue(originalHash, out var origSkill) &&
                                              (origSkill.SkillPosition == 15 || origSkill.SkillPosition == 5));

                            if (wasTalent)
                            {
                                if (!options.RandomizeTalents || talentMoves.Count == 0)
                                {
                                    assignedInPlayer.Add(entry.SkillHash);
                                    continue;
                                }

                                int pickedTalent = PickUniqueSkillHash(talentMoves, assignedInPlayer, null, learnRng, avoidHash: originalHash);
                                entry.SkillHash = pickedTalent;
                                assignedInPlayer.Add(pickedTalent);
                                skillTableModified = true;
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

                            // When replacing a story-scripted skill slot, prefer the same tactical category (Shoot/Dribble/Block/Save)
                            // so scripted shots remain shots, scripted saves remain saves, and scripted dribbles remain dribbles!
                            IReadOnlyList<ISkillConfig> candidatePool;
                            if (isProtectedPlayer && protection.ScriptReferencedSkillHashes.Contains(originalHash))
                            {
                                candidatePool = SelectCategoryMatchedPool(
                                    originalHash,
                                    whsSet,
                                    whoSet,
                                    whdSet,
                                    whkSet,
                                    shotMoves,
                                    dribbleMoves,
                                    blockMoves,
                                    saveMoves,
                                    activeFieldMoves);
                            }
                            else
                            {
                                candidatePool = SelectCompatiblePool(
                                    cp.PlayerPosition,
                                    slot,
                                    options.EnforcePositionCompatibility,
                                    saveMoves,
                                    shotMoves,
                                    dribbleMoves,
                                    blockMoves,
                                    activeFieldMoves,
                                    learnRng);
                            }

                            int? maxTpConstraint = (isInitialSlot && options.EnsureUsableInitialTp)
                                ? usableLv1Tp
                                : null;

                            int chosenHash = PickUniqueSkillHash(
                                candidatePool,
                                assignedInPlayer,
                                maxTpConstraint,
                                learnRng,
                                avoidHash: originalHash);
                            entry.SkillHash = chosenHash;
                            assignedInPlayer.Add(chosenHash);
                            skillTableModified = true;
                            report.RandomizedSkillSlotsCount++;

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
                }

                // 2b. Starter Technique / Initial Fighting Spirit Guarantee (isolated RNG subsystem "skills.starter_guarantee")
                if (options.GuaranteeInitialMoveOrSpiritForAllPlayers || options.GuaranteeProtagonistInitialSkill)
                {
                    DeterministicRng starterRng = seeds.GetSubsystem("skills.starter_guarantee");
                    var validAvatarPool = (visualProfile?.ValidAvatarHashes != null && visualProfile.ValidAvatarHashes.Count > 0)
                        ? visualProfile.ValidAvatarHashes.OrderBy(h => h).ToList()
                        : avatars.Where(a => a.AvatarHash != 0 && a.SpecialMoveID != 0 && a.FightingSpiritPoint > 0)
                                 .Select(a => a.AvatarHash)
                                 .OrderBy(h => h)
                                 .ToList();

                    foreach (var cp in charaParams)
                    {
                        if (cp.ParamHash == 0)
                            continue;

                        bool isArion = cp.ParamHash == arionParamHash;
                        bool isJp = cp.ParamHash == jpParamHash;
                        if (!options.GuaranteeInitialMoveOrSpiritForAllPlayers && !isArion)
                            continue;

                        if (cp.SpecialMoveCount <= 0 ||
                            cp.SpecialMoveOffset < 0 ||
                            cp.SpecialMoveOffset + cp.SpecialMoveCount > skillTable.Length)
                        {
                            // Playable character with 0 move slots and 0 Fighting Spirit: grant an initial playable EG!
                            if (!isArion &&
                                !isJp &&
                                cp.FightingSpiritHash == 0 &&
                                validAvatarPool.Count > 0 &&
                                !protection.ProtectedAvatarBearerParamHashes.Contains(cp.ParamHash) &&
                                (visualProfile == null || visualProfile.CoherentPlayableParamHashes.Contains(cp.ParamHash)))
                            {
                                cp.FightingSpiritHash = starterRng.PickOne(validAvatarPool);
                                charaParamsModified = true;
                                report.GuaranteedInitialStarterCount++;
                            }
                            continue;
                        }

                        // Special story-unlock slot preservation for Arion (para_cp0008) & JP (para_cp0005):
                        // Give Slot 0 LevelLearned = 1 (100% guaranteed initial active move from Minute 0!)
                        // while shifting story-locked LevelLearned == 101 markers onto Slots 1..3 so story cutscenes
                        // (ev42033100, btl0006, btl0308) STILL awaken brand-new randomized techniques!
                        if ((isArion || isJp) &&
                            options.EnableDynamicStoryTechniqueSwap &&
                            vanillaSlotHashesByPlayer.TryGetValue(cp.ParamHash, out var vanillaSlots))
                        {
                            var slot0 = skillTable[cp.SpecialMoveOffset];
                            if (slot0.LevelLearned > 1)
                            {
                                if (isArion && cp.SpecialMoveCount >= 4 &&
                                    vanillaSlots[0].LevelLearned >= 100 &&
                                    vanillaSlots[1].LevelLearned >= 100 &&
                                    vanillaSlots[2].LevelLearned >= 100)
                                {
                                    slot0.LevelLearned = 1;
                                    skillTable[cp.SpecialMoveOffset + 1].LevelLearned = 101;
                                    skillTable[cp.SpecialMoveOffset + 2].LevelLearned = 101;
                                    skillTable[cp.SpecialMoveOffset + 3].LevelLearned = 101;
                                    skillTableModified = true;
                                }
                                else if (isJp && cp.SpecialMoveCount >= 2 && vanillaSlots[0].LevelLearned >= 100)
                                {
                                    slot0.LevelLearned = 1;
                                    skillTable[cp.SpecialMoveOffset + 1].LevelLearned = 101;
                                    skillTableModified = true;
                                }
                                else
                                {
                                    slot0.LevelLearned = 1;
                                    skillTableModified = true;
                                }
                            }
                        }

                        // Check whether player already has a Level-1 active move OR an initial Fighting Spirit (EG)
                        bool hasLv1ActiveMove = false;
                        int firstActiveCandidateSlot = -1;
                        var assignedHashes = new HashSet<int>();

                        for (int s = 0; s < cp.SpecialMoveCount; s++)
                        {
                            var st = skillTable[cp.SpecialMoveOffset + s];
                            if (st.SkillHash != 0)
                                assignedHashes.Add(st.SkillHash);

                            bool isActive = st.SkillHash != 0 &&
                                            !whpSet.Contains(st.SkillHash) &&
                                            (whsSet.Contains(st.SkillHash) ||
                                             whoSet.Contains(st.SkillHash) ||
                                             whdSet.Contains(st.SkillHash) ||
                                             whkSet.Contains(st.SkillHash));

                            if (isActive)
                            {
                                if (st.LevelLearned == 1)
                                    hasLv1ActiveMove = true;
                                if (firstActiveCandidateSlot < 0 && st.LevelLearned < 100)
                                    firstActiveCandidateSlot = s;
                            }
                        }

                        bool hasInitialSpirit = cp.FightingSpiritHash != 0;
                        bool mustForceInitialMove = isArion || isJp || (!hasLv1ActiveMove && !hasInitialSpirit);

                        if (mustForceInitialMove && !hasLv1ActiveMove)
                        {
                            int targetSlot = firstActiveCandidateSlot >= 0 ? firstActiveCandidateSlot : 0;
                            var targetEntry = skillTable[cp.SpecialMoveOffset + targetSlot];
                            int usableLv1Tp = EstimateUsableInitialTp(game, cp);

                            bool targetIsActive = targetEntry.SkillHash != 0 &&
                                                  !whpSet.Contains(targetEntry.SkillHash) &&
                                                  (whsSet.Contains(targetEntry.SkillHash) ||
                                                   whoSet.Contains(targetEntry.SkillHash) ||
                                                   whdSet.Contains(targetEntry.SkillHash) ||
                                                   whkSet.Contains(targetEntry.SkillHash));

                            if (!targetIsActive)
                            {
                                var rolePool = SelectCompatiblePool(
                                    cp.PlayerPosition,
                                    0,
                                    options.EnforcePositionCompatibility,
                                    saveMoves,
                                    shotMoves,
                                    dribbleMoves,
                                    blockMoves,
                                    activeFieldMoves,
                                    starterRng);
                                assignedHashes.Remove(targetEntry.SkillHash);
                                targetEntry.SkillHash = PickUniqueSkillHash(rolePool, assignedHashes, usableLv1Tp, starterRng);
                            }
                            else if (options.EnsureUsableInitialTp &&
                                     skillByHash.TryGetValue(targetEntry.SkillHash, out var curCfg) &&
                                     curCfg.TPCost > usableLv1Tp)
                            {
                                var rolePool = SelectCompatiblePool(
                                    cp.PlayerPosition,
                                    0,
                                    options.EnforcePositionCompatibility,
                                    saveMoves,
                                    shotMoves,
                                    dribbleMoves,
                                    blockMoves,
                                    activeFieldMoves,
                                    starterRng);
                                assignedHashes.Remove(targetEntry.SkillHash);
                                targetEntry.SkillHash = PickUniqueSkillHash(rolePool, assignedHashes, usableLv1Tp, starterRng);
                            }

                            if (options.EnsureUsableInitialTp &&
                                skillByHash.TryGetValue(targetEntry.SkillHash, out var finalCfg) &&
                                finalCfg.TPCost > usableLv1Tp)
                            {
                                cp.TP = Math.Max(cp.TP, finalCfg.TPCost + 20);
                                charaParamsModified = true;
                            }

                            targetEntry.LevelLearned = 1;
                            skillTableModified = true;
                            report.GuaranteedInitialStarterCount++;
                        }
                    }
                }

                // 2c. Build Dynamic Story-Technique Swap mappings for Squirrel .nutb patching
                if (options.EnableDynamicStoryTechniqueSwap)
                {
                    DeterministicRng storySwapRng = seeds.GetSubsystem("skills.story_swap");

                    foreach (var cp in charaParams)
                    {
                        if (cp.ParamHash == 0 || !paraCpLookup.TryGetValue(cp.ParamHash, out string paraCode))
                            continue;
                        if (!vanillaSlotHashesByPlayer.TryGetValue(cp.ParamHash, out var vanillaSlots))
                            continue;

                        // Special shifted story-unlock mapping for Arion (Slots 0..2 -> Slots 1..3) and JP (Slot 0 -> Slot 1)
                        if (cp.ParamHash == arionParamHash &&
                            cp.SpecialMoveCount >= 4 &&
                            vanillaSlots[0].LevelLearned >= 100 &&
                            skillTable[cp.SpecialMoveOffset].LevelLearned == 1 &&
                            skillTable[cp.SpecialMoveOffset + 1].LevelLearned >= 100)
                        {
                            for (int vIdx = 0; vIdx < 3; vIdx++)
                            {
                                if (skillCodeByHash.TryGetValue(vanillaSlots[vIdx].SkillHash, out string vCode) &&
                                    skillCodeByHash.TryGetValue(skillTable[cp.SpecialMoveOffset + vIdx + 1].SkillHash, out string nCode))
                                {
                                    protection.StorySkillSlotRemaps[(paraCode, vCode)] = nCode;
                                }
                            }
                            continue;
                        }

                        if (cp.ParamHash == jpParamHash &&
                            cp.SpecialMoveCount >= 2 &&
                            vanillaSlots[0].LevelLearned >= 100 &&
                            skillTable[cp.SpecialMoveOffset].LevelLearned == 1 &&
                            skillTable[cp.SpecialMoveOffset + 1].LevelLearned >= 100)
                        {
                            if (skillCodeByHash.TryGetValue(vanillaSlots[0].SkillHash, out string vCode) &&
                                skillCodeByHash.TryGetValue(skillTable[cp.SpecialMoveOffset + 1].SkillHash, out string nCode))
                            {
                                protection.StorySkillSlotRemaps[(paraCode, vCode)] = nCode;
                            }
                            continue;
                        }

                        for (int s = 0; s < cp.SpecialMoveCount; s++)
                        {
                            int vHash = vanillaSlots[s].SkillHash;
                            int nHash = skillTable[cp.SpecialMoveOffset + s].SkillHash;
                            if (vHash != 0 && nHash != 0 &&
                                skillCodeByHash.TryGetValue(vHash, out string vCode) &&
                                skillCodeByHash.TryGetValue(nHash, out string nCode))
                            {
                                protection.StorySkillSlotRemaps[(paraCode, vCode)] = nCode;
                            }
                        }
                    }

                    // Also map any battle-only / event-referenced (para_cpXXXX, wh*YYYY) call-site whose vanilla skill
                    // was not in the player's 4 base skill_table rows (e.g. temporary match skills in btl0002 / btl0008!)
                    foreach (var callSite in protection.NutbAudit.SkillCallSites
                                 .Where(cs => !string.IsNullOrEmpty(cs.CharaId) &&
                                              !string.IsNullOrEmpty(cs.SkillId) &&
                                              cs.SkillId.StartsWith("wh", StringComparison.Ordinal))
                                 .OrderBy(cs => cs.VirtualPath, StringComparer.Ordinal)
                                 .ThenBy(cs => cs.FunctionName, StringComparer.Ordinal))
                    {
                        var key = (callSite.CharaId, callSite.SkillId);
                        if (protection.StorySkillSlotRemaps.ContainsKey(key))
                            continue;

                        int vHash = Level5IdHelper.ComputeCrc32Utf8(callSite.SkillId);
                        var catPool = SelectCategoryMatchedPool(
                            vHash,
                            whsSet,
                            whoSet,
                            whdSet,
                            whkSet,
                            shotMoves,
                            dribbleMoves,
                            blockMoves,
                            saveMoves,
                            activeFieldMoves);

                        int pickedHash = PickUniqueSkillHash(catPool, new HashSet<int>(), null, storySwapRng, avoidHash: vHash);
                        if (skillCodeByHash.TryGetValue(pickedHash, out string pickedCode))
                        {
                            protection.StorySkillSlotRemaps[key] = pickedCode;
                        }
                    }

                    report.DynamicStorySkillMappingsCount = protection.StorySkillSlotRemaps.Count;
                }

                if (skillTableModified)
                {
                    game.SaveSkillTable(skillTable);
                }
                if (charaParamsModified)
                {
                    game.SaveCharaparams(charaParams);
                }
            }

            // 3. Fighting Spirit (Avatar) Skills & Stats
            // CRITICAL: NEVER swap av.SpecialMoveID across spirits! In IEGO1, each spirit ckNNNN loads a dedicated
            // skeletal animation /data/chr/motion/waza/vkaNNNN/vkaNNNN_ckNNNN.xc rigged 1-to-1 to that spirit's skeleton.
            // Swapping SpecialMoveID across spirits crashes btl_manager when Victor summons Maestro (ck0002) after btl0302!
            // Instead, safely randomize the spirit's passive talent (av.SkillID) and FightingSpiritPoint/Attack variance.
            if (options.RandomizeFightingSpiritSkills && avatars.Length > 0)
            {
                DeterministicRng avSkillRng = seeds.GetSubsystem("skills.avatars");

                var avatarPassivePool = avatars
                    .Select(a => a.SkillID)
                    .Where(h => h != 0)
                    .Concat(skills.Where(s => wkpSet.Contains(s.SkillHash)).Select(s => s.SkillHash))
                    .Distinct()
                    .OrderBy(h => h)
                    .ToList();

                bool avatarsChanged = false;
                foreach (var av in avatars)
                {
                    if (av.AvatarHash == 0)
                        continue;

                    bool modifiedThisAvatar = false;
                    if (avatarPassivePool.Count > 1 && av.SkillID != 0)
                    {
                        int nextPassive = avSkillRng.PickOne(avatarPassivePool);
                        if (nextPassive == av.SkillID)
                            nextPassive = avatarPassivePool[(avatarPassivePool.IndexOf(av.SkillID) + 1) % avatarPassivePool.Count];
                        av.SkillID = nextPassive;
                        modifiedThisAvatar = true;
                    }

                    if (av.FightingSpiritPoint > 0)
                    {
                        int fspDelta = avSkillRng.NextIntInclusive(-15, 15);
                        av.FightingSpiritPoint = Math.Clamp((int)Math.Round(av.FightingSpiritPoint * (100 + fspDelta) / 100.0), 60, 260);
                        modifiedThisAvatar = true;
                    }

                    if (av.Attack > 0)
                    {
                        int atkDelta = avSkillRng.NextIntInclusive(-15, 15);
                        av.Attack = Math.Clamp((int)Math.Round(av.Attack * (100 + atkDelta) / 100.0), 40, 240);
                        modifiedThisAvatar = true;
                    }

                    if (modifiedThisAvatar)
                    {
                        avatarsChanged = true;
                        report.RandomizedAvatarSkillsCount++;
                    }
                }

                if (avatarsChanged)
                {
                    game.SaveAvatars(avatars);
                }
            }
        }

        private static IReadOnlyList<ISkillConfig> SelectCategoryMatchedPool(
            int vanillaSkillHash,
            HashSet<int> whsSet,
            HashSet<int> whoSet,
            HashSet<int> whdSet,
            HashSet<int> whkSet,
            List<ISkillConfig> shotMoves,
            List<ISkillConfig> dribbleMoves,
            List<ISkillConfig> blockMoves,
            List<ISkillConfig> saveMoves,
            List<ISkillConfig> fallbackMoves)
        {
            if (whsSet.Contains(vanillaSkillHash) && shotMoves.Count > 0)
                return shotMoves;
            if (whoSet.Contains(vanillaSkillHash) && dribbleMoves.Count > 0)
                return dribbleMoves;
            if (whdSet.Contains(vanillaSkillHash) && blockMoves.Count > 0)
                return blockMoves;
            if (whkSet.Contains(vanillaSkillHash) && saveMoves.Count > 0)
                return saveMoves;
            return fallbackMoves;
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
            DeterministicRng rng,
            int avoidHash = 0)
        {
            var strictFiltered = pool
                .Where(s => s.SkillHash != avoidHash &&
                            !alreadyAssigned.Contains(s.SkillHash) &&
                            (!maxTpCost.HasValue || s.TPCost <= maxTpCost.Value))
                .ToList();

            if (strictFiltered.Count > 0)
                return rng.PickOne(strictFiltered).SkillHash;

            var filtered = pool
                .Where(s => !alreadyAssigned.Contains(s.SkillHash) &&
                            (!maxTpCost.HasValue || s.TPCost <= maxTpCost.Value))
                .ToList();

            if (filtered.Count > 0)
                return rng.PickOne(filtered).SkillHash;

            if (maxTpCost.HasValue)
            {
                var tpSafe = pool.Where(s => s.TPCost <= maxTpCost.Value && s.SkillHash != avoidHash).ToList();
                if (tpSafe.Count > 0)
                    return rng.PickOne(tpSafe).SkillHash;

                var tpSafeAny = pool.Where(s => s.TPCost <= maxTpCost.Value).ToList();
                if (tpSafeAny.Count > 0)
                    return rng.PickOne(tpSafeAny).SkillHash;

                return pool.OrderBy(s => s.TPCost).First().SkillHash;
            }

            var nonDup = pool.Where(s => !alreadyAssigned.Contains(s.SkillHash)).ToList();
            if (nonDup.Count > 0)
                return rng.PickOne(nonDup).SkillHash;

            return rng.PickOne(pool).SkillHash;
        }

        private static int EstimateUsableInitialTp(SafeGOGame game, ICharaparam cp)
        {
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

        private static HashSet<int> BuildRangeHashSet(string prefix, int minInclusive, int maxInclusive)
        {
            var set = new HashSet<int>(maxInclusive - minInclusive + 1);
            for (int i = minInclusive; i <= maxInclusive; i++)
            {
                set.Add(Level5IdHelper.ComputeCrc32Utf8($"{prefix}{i:D4}"));
            }
            return set;
        }

        private static Dictionary<int, string> BuildRangeCodeLookup(string prefix, int minInclusive, int maxInclusive)
        {
            var dict = new Dictionary<int, string>(maxInclusive - minInclusive + 1);
            for (int i = minInclusive; i <= maxInclusive; i++)
            {
                string code = $"{prefix}{i:D4}";
                dict[Level5IdHelper.ComputeCrc32Utf8(code)] = code;
            }
            return dict;
        }
    }
}
