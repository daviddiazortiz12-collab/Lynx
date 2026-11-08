using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IEGORandomizer.Engine;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Seeding;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.Core.RomFs;
using Lynx.InazumaEleven.Games.GO;
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Tests
{
    public sealed class RandomizerEngineTests
    {
        [Fact]
        public void SeedEngine_SameSeedProducesIdenticalStreams_AndIsolatesSubsystems()
        {
            var engineA = new SeedEngine("PEGASO-1234-ABCD");
            var engineB = new SeedEngine("PEGASO-1234-ABCD");
            var engineDiff = new SeedEngine("LANCELOT-9999-FFFF");

            // Consume thousands of draws on unrelated subsystems in engineB BEFORE touching players.swap
            var skillsB = engineB.GetSubsystem("skills.learnsets");
            for (int i = 0; i < 5000; i++)
            {
                _ = skillsB.NextUInt64();
            }

            var playersA = engineA.GetSubsystem("players.swap");
            var playersB = engineB.GetSubsystem("players.swap");
            var playersDiff = engineDiff.GetSubsystem("players.swap");

            var seqA = Enumerable.Range(0, 64).Select(_ => playersA.NextUInt64()).ToList();
            var seqB = Enumerable.Range(0, 64).Select(_ => playersB.NextUInt64()).ToList();
            var seqDiff = Enumerable.Range(0, 64).Select(_ => playersDiff.NextUInt64()).ToList();

            Assert.SequenceEqual(seqA, seqB, "Identical seed must yield identical subsystem draws regardless of other subsystems.");
            Assert.False(seqA.SequenceEqual(seqDiff), "Different seeds must yield distinct sequences.");
        }

        [Fact]
        public void DeterministicRng_CreateDerangement_HasZeroFixedPoints()
        {
            var seedEngine = new SeedEngine("DERANGEMENT-VERIFY-2026");
            var rng = seedEngine.GetSubsystem("test.derange");

            for (int size = 2; size <= 60; size++)
            {
                var input = Enumerable.Range(1000, size).ToList();
                var deranged = rng.CreateDerangement(input);
                Assert.Equal(size, deranged.Count);

                for (int i = 0; i < size; i++)
                {
                    Assert.NotEqual(input[i], deranged[i], $"Fixed point found at index {i} for size {size}.");
                }
            }
        }

        [Fact]
        public void FullCoordinator_SameSeedProducesIdenticalGameData_AndProtectsOriginalSource()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_test_det_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string sourceIea = Path.Combine(tempDir, "source", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(sourceIea)!);
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(sourceIea);
                string sourceShaBefore = GameWorkspace.ComputeSha256Hex(sourceIea);

                string outRun1 = Path.Combine(tempDir, "run1");
                string outRun2 = Path.Combine(tempDir, "run2");
                string outRunDiff = Path.Combine(tempDir, "run_diff");

                var coordinator = new RandomizerCoordinator();

                var opts1 = new RandomizerOptions { Seed = "INAZUMA-SEED-777", Edition = GameEdition.Light, BlindPlayMode = true };
                opts1.Players.SwapStoryPlayerVisualIdentities = true;
                opts1.Skills.RandomizeSkillParameters = true;
                opts1.Teams.RandomizeLevels = true;

                var opts2 = new RandomizerOptions { Seed = "INAZUMA-SEED-777", Edition = GameEdition.Light, BlindPlayMode = true };
                opts2.Players.SwapStoryPlayerVisualIdentities = true;
                opts2.Skills.RandomizeSkillParameters = true;
                opts2.Teams.RandomizeLevels = true;

                var optsDiff = new RandomizerOptions { Seed = "INAZUMA-SEED-888", Edition = GameEdition.Light, BlindPlayMode = true };
                optsDiff.Players.SwapStoryPlayerVisualIdentities = true;
                optsDiff.Skills.RandomizeSkillParameters = true;
                optsDiff.Teams.RandomizeLevels = true;

                var rep1 = coordinator.GenerateRun(sourceIea, outRun1, opts1);
                var rep2 = coordinator.GenerateRun(sourceIea, outRun2, opts2);
                var repDiff = coordinator.GenerateRun(sourceIea, outRunDiff, optsDiff);

                Assert.True(rep1.SourceImmutabilityVerified);
                Assert.True(rep1.OutputIntegrityVerified);
                Assert.Equal(sourceShaBefore, GameWorkspace.ComputeSha256Hex(sourceIea), "Source ie_a.fa must never change.");

                // Blind Play mode must never write spoiler_log.txt or populate SpoilerLogEntries
                Assert.Equal(0, rep1.SpoilerLogEntries.Count);
                Assert.False(File.Exists(Path.Combine(outRun1, "spoiler_log.txt")), "Blind play must not create spoiler_log.txt.");

                // Compare exported Standalone/ie_a.fa tables between Run 1 and Run 2
                using var g1 = new SafeGOGame(Path.Combine(outRun1, "Standalone", "ie_a.fa"));
                using var g2 = new SafeGOGame(Path.Combine(outRun2, "Standalone", "ie_a.fa"));
                using var gDiff = new SafeGOGame(Path.Combine(outRunDiff, "Standalone", "ie_a.fa"));

                Assert.SequenceEqual(
                    g1.GetCharaparams().Select(p => (p.ParamHash, p.Kick, p.Catch, p.Element, p.PlayerPosition, p.FightingSpiritHash)),
                    g2.GetCharaparams().Select(p => (p.ParamHash, p.Kick, p.Catch, p.Element, p.PlayerPosition, p.FightingSpiritHash)));

                Assert.SequenceEqual(
                    g1.GetSkillTable().Select(s => s.SkillHash),
                    g2.GetSkillTable().Select(s => s.SkillHash));

                Assert.SequenceEqual(
                    g1.GetStoryTeamsOnly().Select(t => (t.Player1, t.Player2, t.Player3, t.Player4)),
                    g2.GetStoryTeamsOnly().Select(t => (t.Player1, t.Player2, t.Player3, t.Player4)));

                Assert.SequenceEqual(
                    MapDataService.LoadAllTreasureChests(g1.IE_A.Directory).Select(c => (c.Record.ItemIDLight, c.Record.ItemIDShadow)),
                    MapDataService.LoadAllTreasureChests(g2.IE_A.Directory).Select(c => (c.Record.ItemIDLight, c.Record.ItemIDShadow)));

                // Verify different seed produces distinct output
                bool skillsDiffer = !g1.GetSkillTable().Select(s => s.SkillHash)
                    .SequenceEqual(gDiff.GetSkillTable().Select(s => s.SkillHash));
                Assert.True(skillsDiffer, "Different seeds must produce different learnsets.");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void AllSubsystems_RespectStoryProtectionVisualCoherenceTpSafetyAndLayeredFsExport()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_test_all_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string rawIea = Path.Combine(tempDir, "raw_ie_a.fa");
                string rom3ds = Path.Combine(tempDir, "InazumaElevenGO_Luz.3ds");
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(rawIea);
                SyntheticGameArchiveBuilder.WrapIeAFaInDecrypted3dsContainer(
                    rawIea,
                    rom3ds,
                    titleId: 0x0004000000112F00UL,
                    productCode: "CTR-P-AE4P");

                string romShaBefore = GameWorkspace.ComputeSha256Hex(rom3ds);
                string outDir = Path.Combine(tempDir, "export_pack");

                var coordinator = new RandomizerCoordinator();
                var options = new RandomizerOptions
                {
                    Seed = "PEGASO-LUZ-2026",
                    Edition = GameEdition.Auto,
                    BlindPlayMode = true
                };

                var report = coordinator.GenerateRun(rom3ds, outDir, options);

                Assert.Equal(GameEdition.Light, report.EffectiveEdition);
                Assert.Equal("0004000000112F00", report.ResolvedTitleId);
                Assert.Equal(romShaBefore, GameWorkspace.ComputeSha256Hex(rom3ds), "Source .3ds must remain byte-for-byte unchanged.");

                // Verify Citra & Luma3DS LayeredFS folder layouts were created cleanly
                string citraFa = Path.Combine(outDir, "Citra", "load", "mods", "0004000000112F00", "romfs", "ie_a.fa");
                string lumaFa = Path.Combine(outDir, "Luma3DS", "luma", "titles", "0004000000112F00", "romfs", "ie_a.fa");
                Assert.True(File.Exists(citraFa), "Citra mod ie_a.fa must exist.");
                Assert.True(File.Exists(lumaFa), "Luma3DS LayeredFS ie_a.fa must exist.");

                using var vanilla = new SafeGOGame(rawIea);
                using var modded = new SafeGOGame(citraFa);

                int npcMannequinHash = Level5IdHelper.ComputeCrc32Utf8("para_cn0900");
                int scriptedStoryTeamId = Level5IdHelper.ComputeCrc32Utf8("ts0001");
                int rivalTeamId = Level5IdHelper.ComputeCrc32Utf8("ts0101");
                int keyItemId = Level5IdHelper.ComputeCrc32Utf8("iik0001");

                // 1. Scripted story team ts0001 (referenced by btl0001 in ev1001.nutb) must stay intact,
                // while rival team ts0101 & pachangas & PalPack cards must be strictly deranged!
                var vanillaStory = vanilla.GetStoryTeamsOnly().ToDictionary(t => t.TeamConfigID);
                var moddedStory = modded.GetStoryTeamsOnly().ToDictionary(t => t.TeamConfigID);

                Assert.Equal(vanillaStory[scriptedStoryTeamId].Player1, moddedStory[scriptedStoryTeamId].Player1);
                Assert.Equal(vanillaStory[scriptedStoryTeamId].Player2, moddedStory[scriptedStoryTeamId].Player2);
                Assert.NotEqual(vanillaStory[rivalTeamId].Player1, moddedStory[rivalTeamId].Player1, "Rival team slot 1 should be deranged.");
                Assert.NotEqual(vanillaStory[rivalTeamId].Player2, moddedStory[rivalTeamId].Player2, "Rival team slot 2 should be deranged.");

                // 2. Quarantined NPC mannequin cn0900 (Body=0) must NEVER appear on any team or PalPack card
                foreach (var st in moddedStory.Values)
                {
                    Assert.NotEqual(npcMannequinHash, st.Player1);
                    Assert.NotEqual(npcMannequinHash, st.Player2);
                }

                var vanillaPalpacks = vanilla.GetItems("kizunax").OfType<GOSupport.ItemConfigPalpackCard>().ToList();
                var moddedPalpacks = modded.GetItems("kizunax").OfType<GOSupport.ItemConfigPalpackCard>().ToList();
                for (int i = 0; i < vanillaPalpacks.Count; i++)
                {
                    Assert.NotEqual(vanillaPalpacks[i].RecrutedCharacterId, moddedPalpacks[i].RecrutedCharacterId,
                        $"PalPack card #{i} must be strictly deranged.");
                    Assert.NotEqual(npcMannequinHash, moddedPalpacks[i].RecrutedCharacterId);
                    Assert.NotEqual(moddedPalpacks[i].RecrutedCharacterId, moddedPalpacks[i].PrerequisitesId1,
                        "PalPack card cannot require recruiting itself.");
                }

                // 3. Initial skills must be TP-usable at Level 1 and never contain wk* avatar skills
                var wkLookup = Level5IdHelper.BuildPrefixLookup("wks", "wko", "wkd", "wkk", "wkp");
                var moddedSkills = modded.GetSkillConfigs(false).ToDictionary(s => s.SkillHash);
                var moddedSkillTable = modded.GetSkillTable();
                foreach (var cp in modded.GetCharaparams().Where(p => p.SpecialMoveCount > 0))
                {
                    var slot0 = moddedSkillTable[cp.SpecialMoveOffset];
                    Assert.False(wkLookup.ContainsKey(slot0.SkillHash), "Player learnset must never contain Avatar wk* skill.");
                    if (moddedSkills.TryGetValue(slot0.SkillHash, out var sk) && sk.TPCost > 0)
                    {
                        int lv1Tp = modded.StatCalculator.GetStat(PlayerStats.TP, cp.TP, cp.TPGrow, 1, Math.Max(1, cp.Element), 1);
                        Assert.True(sk.TPCost <= lv1Tp, $"Initial skill TP ({sk.TPCost}) exceeds player Lv.1 TP ({lv1Tp}).");
                    }
                }

                // 4. Key Item (iik0001) & FUNCPOINT TBoxFlag=999 chests must remain untouched while normal chests randomize
                var moddedChests = MapDataService.LoadAllTreasureChests(modded.IE_A.Directory);
                var funcptChest = moddedChests.Single(c => c.Record.TBoxFlag == 999);
                var keyChest = moddedChests.Single(c => c.Record.TBoxID == 4);
                Assert.Equal(Level5IdHelper.ComputeCrc32Utf8("iie0003"), funcptChest.Record.ItemIDLight);
                Assert.Equal(keyItemId, keyChest.Record.ItemIDLight);
                Assert.Equal(keyItemId, keyChest.Record.ItemIDShadow);
                Assert.True(report.RandomizedOverworldChestsCount >= 2, "Unlocked non-key chests must randomize.");

                // 5. Uniforms must map to valid kit numbers (1..4 -> 1000..4002) and sync with overworld Pachanga spawns
                var moddedSpawns = MapDataService.LoadAllEncounterSpawns(modded.IE_A.Directory);
                var moddedTeamParams = modded.GetTeamParams();
                foreach (var sp in moddedSpawns)
                {
                    var matchingTp = moddedTeamParams.Single(tp => tp.TeamConfigID == sp.Record.TeamID);
                    Assert.Equal(matchingTp.Uniform, sp.Record.Uniform, "Overworld pachanga uniform must match battle TeamParam.");
                    int kitNum = sp.Record.Uniform / 1000;
                    Assert.True(kitNum >= 1 && kitNum <= 4, $"Kit number {kitNum} must exist in verified 3D body assets.");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void SubsystemIsolation_TogglingSkillsAndRewards_LeavesPlayerDerangementsIdentical()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_test_iso_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string sourceIea = Path.Combine(tempDir, "ie_a.fa");
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(sourceIea);

                var coordinator = new RandomizerCoordinator();

                var optsFull = new RandomizerOptions { Seed = "ISOLATION-CHECK-42" };
                optsFull.Skills.Enabled = true;
                optsFull.Rewards.Enabled = true;
                optsFull.Uniforms.Enabled = true;

                var optsPlayersOnly = new RandomizerOptions { Seed = "ISOLATION-CHECK-42" };
                optsPlayersOnly.Skills.Enabled = false;
                optsPlayersOnly.Rewards.Enabled = false;
                optsPlayersOnly.Uniforms.Enabled = false;
                optsPlayersOnly.Teams.Enabled = false;

                coordinator.GenerateRun(sourceIea, Path.Combine(tempDir, "full"), optsFull);
                coordinator.GenerateRun(sourceIea, Path.Combine(tempDir, "players_only"), optsPlayersOnly);

                using var gFull = new SafeGOGame(Path.Combine(tempDir, "full", "Standalone", "ie_a.fa"));
                using var gPlayers = new SafeGOGame(Path.Combine(tempDir, "players_only", "Standalone", "ie_a.fa"));

                int rivalId = Level5IdHelper.ComputeCrc32Utf8("ts0101");
                var tFull = gFull.GetStoryTeamsOnly().Single(t => t.TeamConfigID == rivalId);
                var tPlayers = gPlayers.GetStoryTeamsOnly().Single(t => t.TeamConfigID == rivalId);

                Assert.Equal(tFull.Player1, tPlayers.Player1);
                Assert.Equal(tFull.Player2, tPlayers.Player2);
                Assert.Equal(tFull.Player3, tPlayers.Player3);
                Assert.Equal(tFull.Player4, tPlayers.Player4);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Exporter_RefusesToOverwriteOriginalRomFsInputDirectory()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_test_safe_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string romFsDir = Path.Combine(tempDir, "MyExtractedRomFs");
                Directory.CreateDirectory(romFsDir);
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(Path.Combine(romFsDir, "ie_a.fa"));

                var coordinator = new RandomizerCoordinator();
                Assert.Throws<InvalidOperationException>(() =>
                    coordinator.GenerateRun(romFsDir, romFsDir, new RandomizerOptions { Seed = "SAFE-1" }));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
