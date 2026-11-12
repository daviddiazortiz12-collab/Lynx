using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using IEGORandomizer.Engine;
using IEGORandomizer.Engine.Cia;
using IEGORandomizer.Engine.Configuration;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.Core.RomFs;
using Lynx.Core.RomFs.Cia;
using Lynx.Core.Scripts;
using Lynx.InazumaEleven.Games.GO;
using Lynx.InazumaEleven.Logic;
using StudioElevenLib.Level5.Binary;
using StudioElevenLib.Level5.Binary.Collections;
using StudioElevenLib.Level5.Binary.Logic;
using StudioElevenLib.Level5.Binary.Mapper;
using StudioElevenLib.Level5.Text;
using StudioElevenLib.Level5.Text.Logic;

namespace IEGORandomizer.Tests
{
    /// <summary>
    /// Regression suite for v1.2.0:
    /// 1. Chapter 1 1v1 Story Duel vs Victor Blade (btl0302 / ifb0302 / Uniform 903000), 5v5 pachanga ifb03XX
    ///    drill-formation exclusion, unregistered iiuXXXX uniform protection, and 1-to-1 Avatar SpecialMoveID skeleton safety.
    /// 2. Starter Technique (LevelLearned == 1) or initial Fighting Spirit (EG) guarantee for every playable character,
    ///    with 100% guaranteed TP-affordable Level-1 active move for Arion (para_cp0008) &amp; JP (para_cp0005) while preserving
    ///    all 3 story awakening slots (LevelLearned == 101).
    /// 3. Dynamic Story-Technique Swap on genuine Squirrel 2.2.4 .nutb bytecode (RunUnlockSkill, CMND_BTL_LEARNING_SKILL,
    ///    temporary CMND_BTL_LEARNING_SKILL injection before CMND_BTL_USE_SKILL, JZ relative jump fixup, shared RunSkillScript
    ///    choreography literal protection, and CMND_ADD_SET_DIRECTOR coach remapping).
    /// 4. Coaches (ItemConfigDirector + Spanish Dir-{N}-1/2 system_text), Managers (cp0036..cp0038), Story NPCs (cn0001..cn0099),
    ///    Overworld Map NPCs (.npc.bin Type 1/3 vs Type 4 animal quarantine), and FBI V.1.1.2 -&gt; V.1.2.0 .cia upgrade.
    /// </summary>
    public sealed class StoryDuelNpcAndDynamicSkillTests
    {
        [Fact]
        public void VictorDuelBtl0302_DrillFormationExclusion_AndAvatarSkeletonSafety_NeverCrashHardware()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_v120_duel_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string baseFa = Path.Combine(tempDir, "base", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(baseFa)!);
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(baseFa);

                string duelFa = Path.Combine(tempDir, "duel_src", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(duelFa)!);

                int btl0302Hash = Level5IdHelper.ComputeCrc32Utf8("btl0302");
                int ifb0302Hash = Level5IdHelper.ComputeCrc32Utf8("ifb0302");
                int ifb0101Hash = Level5IdHelper.ComputeCrc32Utf8("ifb0101");
                int ifb0102Hash = Level5IdHelper.ComputeCrc32Utf8("ifb0102");
                int tsDuelHash = Level5IdHelper.ComputeCrc32Utf8("ts0302");
                int tpDuelHash = Level5IdHelper.ComputeCrc32Utf8("para_ts0302");
                int victorParamHash = Level5IdHelper.ComputeCrc32Utf8("para_cp0010");
                int ck0002Hash = Level5IdHelper.ComputeCrc32Utf8("ck0002");
                int wks0002Hash = Level5IdHelper.ComputeCrc32Utf8("wks0002");

                using (var game = new SafeGOGame(baseFa, "es"))
                {
                    var root = game.IE_A.Directory;

                    // Add unregistered 3D body kit uza0555 + uzb0555 (NOT in item_config ITEM_UNIFORM!)
                    // and special hoodie uza0903 + uzb0903 (>= 900, must be excluded from standard soccer kit pool!)
                    Arc0SafeArchive.WriteFileAtPath(root, "/data/chr/model/rpg/body/uza0555.xc", new byte[] { 1, 2, 3, 4 });
                    Arc0SafeArchive.WriteFileAtPath(root, "/data/chr/model/rpg/body/uzb0555.xc", new byte[] { 1, 2, 3, 4 });
                    Arc0SafeArchive.WriteFileAtPath(root, "/data/chr/model/rpg/body/uza0903.xc", new byte[] { 1, 2, 3, 4 });
                    Arc0SafeArchive.WriteFileAtPath(root, "/data/chr/model/rpg/body/uzb0903.xc", new byte[] { 1, 2, 3, 4 });

                    // Add 1v1 story duel team ts0302 (Victor Blade solo, 1 active slot!) + TeamParam with ifb0302 & Uniform 903000
                    var storyTeams = game.GetStoryTeamsOnly().ToList();
                    storyTeams.Add(new GOSupport.StoryTeamInfo
                    {
                        TeamConfigID = tsDuelHash,
                        NameID = Level5IdHelper.ComputeCrc32Utf8("nm_ts0302"),
                        Player1 = victorParamHash,
                        NumberPlayer1 = 10
                    });
                    var encountTeams = game.GetEncounterTeamsOnly();
                    game.SaveTeamConfig(storyTeams.ToArray(), encountTeams);

                    var teamParams = game.GetTeamParams().ToList();
                    teamParams.Add(new GOSupport.TeamParamInfo
                    {
                        TeamParamID = tpDuelHash,
                        TeamConfigID = tsDuelHash,
                        FormationID = ifb0302Hash,
                        Uniform = 903000, // Black Knights special hoodie (>= 900000!)
                        Level = 2
                    });
                    // Ensure 5v5 pachangas use genuine ifb0101 / ifb0102
                    foreach (var tp in teamParams.Where(t => encountTeams.Any(e => e.TeamConfigID == t.TeamConfigID)))
                    {
                        tp.FormationID = ifb0101Hash;
                    }
                    teamParams.Add(new GOSupport.TeamParamInfo
                    {
                        TeamParamID = Level5IdHelper.ComputeCrc32Utf8("para_te9999"),
                        TeamConfigID = 0,
                        FormationID = ifb0102Hash,
                        Uniform = 3000,
                        Level = 3
                    });
                    game.SaveTeamParams(teamParams.ToArray());

                    var soccers = (game.GetSoccers() ?? Array.Empty<ISoccerInfo>()).ToList();
                    soccers.Add(new GOSupport.SoccerInfo
                    {
                        SoccerID = btl0302Hash,
                        SoccerMode = 1,
                        TeamParamID = tpDuelHash
                    });
                    SaveSoccerInfos(game, soccers);

                    // Give Victor (para_cp0010) Maestro ck0002 and ensure avatars have passive SkillIDs
                    var avatars = game.GetAvatars(false).ToList();
                    int wkp1 = Level5IdHelper.ComputeCrc32Utf8("wkp0001");
                    int wkp2 = Level5IdHelper.ComputeCrc32Utf8("wkp0002");
                    foreach (var av in avatars)
                    {
                        if (av.SkillID == 0)
                            av.SkillID = wkp1;
                    }
                    var existingCk2 = avatars.FirstOrDefault(a => a.AvatarHash == ck0002Hash);
                    if (existingCk2 != null)
                    {
                        existingCk2.SpecialMoveID = wks0002Hash;
                        existingCk2.SkillID = wkp2;
                    }
                    else
                    {
                        avatars.Add(new GOSupport.Avatar
                        {
                            AvatarHash = ck0002Hash,
                            AvatarNumber = 2,
                            Position = 2,
                            Element = 3,
                            SpecialMoveID = wks0002Hash,
                            SkillID = wkp2,
                            FightingSpiritPoint = 140,
                            Attack = 100,
                            EvolutionGrow = 1
                        });
                    }
                    Arc0SafeArchive.WriteFileAtPath(root, "/data/bustup/avatar/ck0002a.xi", new byte[] { 1, 2, 3, 4 });
                    game.SaveAvatars(avatars.ToArray());

                    var charaParams = game.GetCharaparams();
                    var victor = charaParams.First(c => c.ParamHash == victorParamHash);
                    victor.FightingSpiritHash = ck0002Hash;
                    var arion = charaParams.First(c => c.ParamHash == Level5IdHelper.ComputeCrc32Utf8("para_cp0008"));
                    arion.FightingSpiritHash = 0;
                    game.SaveCharaparams(charaParams);

                    game.SaveCopyTo(duelFa);
                }

                // Test across TITAN-C45C-614C (the exact seed reported by the user!) + 3 extra seeds
                string[] seeds = { "TITAN-C45C-614C", "SEED-VICTOR-01", "SEED-VICTOR-02", "SEED-VICTOR-03" };
                foreach (string seed in seeds)
                {
                    string outDir = Path.Combine(tempDir, "out_" + seed);
                    var opts = new RandomizerOptions { Seed = seed };
                    opts.Teams.RandomizeLevels = true;

                    var coordinator = new RandomizerCoordinator();
                    var report = coordinator.GenerateRun(duelFa, outDir, opts);
                    Assert.True(report.OutputIntegrityVerified);

                    string exportedFa = Path.Combine(outDir, "Standalone", "ie_a.fa");
                    using var check = new SafeGOGame(exportedFa, "es");

                    // 1. btl0302 1v1 duel team & TeamParam remained 100% protected (Victor solo, ifb0302, Uniform 903000)
                    var duelTeam = check.GetStoryTeamsOnly().First(t => t.TeamConfigID == tsDuelHash);
                    Assert.Equal(victorParamHash, duelTeam.Player1);
                    Assert.Equal(0, duelTeam.Player2);

                    var duelTp = check.GetTeamParams().First(t => t.TeamParamID == tpDuelHash);
                    Assert.Equal(ifb0302Hash, duelTp.FormationID);
                    Assert.Equal(903000, duelTp.Uniform);

                    // 2. ZERO 5v5 pachanga teams received ifb0302 (1v1 drill formation!) or kit >= 900 or unregistered kit 555!
                    var encountIds = new HashSet<int>(check.GetEncounterTeamsOnly().Select(e => e.TeamConfigID));
                    foreach (var tp in check.GetTeamParams())
                    {
                        if (tp.TeamParamID == tpDuelHash)
                            continue;

                        bool isPachanga = tp.TeamConfigID == 0 || encountIds.Contains(tp.TeamConfigID);
                        if (isPachanga)
                        {
                            Assert.NotEqual(ifb0302Hash, tp.FormationID);
                            Assert.NotEqual(555, tp.Uniform / 1000);
                        }
                        Assert.True(tp.Uniform < 900000, $"Unexpected >=900000 uniform {tp.Uniform} on 0x{tp.TeamParamID:X8}");
                    }

                    // 3. Maestro ck0002 kept its exact SpecialMoveID (wks0002) AND Victor kept ck0002, while Arion & JP stayed 0!
                    var ck2After = check.GetAvatars(false).First(a => a.AvatarHash == ck0002Hash);
                    Assert.Equal(wks0002Hash, ck2After.SpecialMoveID);

                    var cpAfter = check.GetCharaparams().ToDictionary(c => c.ParamHash);
                    Assert.Equal(ck0002Hash, cpAfter[victorParamHash].FightingSpiritHash);
                    Assert.Equal(0, cpAfter[Level5IdHelper.ComputeCrc32Utf8("para_cp0008")].FightingSpiritHash);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void StarterGuarantee_EveryPlayerGetsLv1ActiveMoveOrSpirit_AndArionAlwaysGetsGuaranteedInitialSkill()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_v120_starter_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string baseFa = Path.Combine(tempDir, "base", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(baseFa)!);
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(baseFa);

                string retailLearnsetFa = Path.Combine(tempDir, "learnset_src", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(retailLearnsetFa)!);

                int arionHash = Level5IdHelper.ComputeCrc32Utf8("para_cp0008");
                int who0001Hash = Level5IdHelper.ComputeCrc32Utf8("who0001");
                int whs0001Hash = Level5IdHelper.ComputeCrc32Utf8("whs0001");
                int whd0001Hash = Level5IdHelper.ComputeCrc32Utf8("whd0001");
                int whs0002Hash = Level5IdHelper.ComputeCrc32Utf8("whs0002");

                using (var game = new SafeGOGame(baseFa, "es"))
                {
                    var skillTable = game.GetSkillTable();
                    var charaParams = game.GetCharaparams();

                    // Replicate retail Arion (para_cp0008): Slots 0..2 locked at LevelLearned = 101, Slot 3 at LevelLearned = 62!
                    var arion = charaParams.First(c => c.ParamHash == arionHash);
                    arion.FightingSpiritHash = 0;
                    skillTable[arion.SpecialMoveOffset + 0].SkillHash = who0001Hash;
                    skillTable[arion.SpecialMoveOffset + 0].LevelLearned = 101;
                    skillTable[arion.SpecialMoveOffset + 1].SkillHash = whs0001Hash;
                    skillTable[arion.SpecialMoveOffset + 1].LevelLearned = 101;
                    skillTable[arion.SpecialMoveOffset + 2].SkillHash = whd0001Hash;
                    skillTable[arion.SpecialMoveOffset + 2].LevelLearned = 101;
                    skillTable[arion.SpecialMoveOffset + 3].SkillHash = whs0002Hash;
                    skillTable[arion.SpecialMoveOffset + 3].LevelLearned = 62;

                    // Also give several scouts late LevelLearned = 18 / 30 and no Fighting Spirit
                    foreach (var cp in charaParams.Where(c => c.ParamHash != arionHash).Take(8))
                    {
                        cp.FightingSpiritHash = 0;
                        for (int s = 0; s < cp.SpecialMoveCount; s++)
                        {
                            skillTable[cp.SpecialMoveOffset + s].LevelLearned = 15 + (s * 10);
                        }
                    }

                    game.SaveSkillTable(skillTable);
                    game.SaveCharaparams(charaParams);
                    game.SaveCopyTo(retailLearnsetFa);
                }

                var coordinator = new RandomizerCoordinator();
                var report = coordinator.GenerateRun(
                    retailLearnsetFa,
                    Path.Combine(tempDir, "out"),
                    new RandomizerOptions { Seed = "TITAN-C45C-614C" });

                Assert.True(report.GuaranteedInitialStarterCount > 0);

                using var reopened = new SafeGOGame(Path.Combine(tempDir, "out", "Standalone", "ie_a.fa"), "es");
                var outSkills = reopened.GetSkillConfigs(false).ToDictionary(s => s.SkillHash);
                var outTable = reopened.GetSkillTable();
                var whpSet = new HashSet<int>(Enumerable.Range(1, 50).Select(i => Level5IdHelper.ComputeCrc32Utf8($"whp{i:D4}")));

                foreach (var cp in reopened.GetCharaparams())
                {
                    if (cp.ParamHash == 0 || cp.SpecialMoveCount <= 0)
                        continue;

                    bool hasLv1Active = false;
                    for (int s = 0; s < cp.SpecialMoveCount; s++)
                    {
                        var st = outTable[cp.SpecialMoveOffset + s];
                        if (st.LevelLearned == 1 && st.SkillHash != 0 && !whpSet.Contains(st.SkillHash))
                        {
                            hasLv1Active = true;
                        }
                    }

                    if (cp.ParamHash == arionHash)
                    {
                        Assert.True(hasLv1Active, "Arion (para_cp0008) MUST ALWAYS start with a Level-1 active move!");
                        Assert.Equal(1, outTable[cp.SpecialMoveOffset + 0].LevelLearned);
                        // All 3 story awakening slots remain preserved at 101 on Slots 1..3!
                        Assert.Equal(101, outTable[cp.SpecialMoveOffset + 1].LevelLearned);
                        Assert.Equal(101, outTable[cp.SpecialMoveOffset + 2].LevelLearned);
                        Assert.Equal(101, outTable[cp.SpecialMoveOffset + 3].LevelLearned);
                        // And Arion's randomized moveset changed from vanilla!
                        Assert.NotEqual(who0001Hash, outTable[cp.SpecialMoveOffset + 1].SkillHash);
                    }
                    else
                    {
                        Assert.True(
                            hasLv1Active || cp.FightingSpiritHash != 0,
                            $"Player 0x{cp.ParamHash:X8} lacked both a Level-1 active move and an initial Fighting Spirit!");
                    }
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void DynamicStorySkillSwap_AndNpcCoachManagerRandomizer_PatchNutbBytecodeAndRespectAllRigs()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_v120_nutb_npc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string baseFa = Path.Combine(tempDir, "base", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(baseFa)!);
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(baseFa);

                string fullFa = Path.Combine(tempDir, "src", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(fullFa)!);

                using (var game = new SafeGOGame(baseFa, "es"))
                {
                    var root = game.IE_A.Directory;

                    // 1. Add 3 Coaches (ItemConfigDirector iim0001..iim0003) + Spanish system_text_es.cfg.bin Dir-{N}-1/2
                    AddDirectorsToItemConfig(game, new[]
                    {
                        (Code: "iim0001", Num: 1, Kick: 20, Speed: 0, Desc1: "Tiro +20"),
                        (Code: "iim0002", Num: 2, Kick: 0, Speed: 25, Desc1: "Velocidad +25"),
                        (Code: "iim0003", Num: 3, Kick: 15, Speed: 15, Desc1: "Equilibrio +15")
                    });

                    // 2. Add Managers (cp0036, cp0037, Body=7, Gender=2) + Female Scout Donor (cp0201, Body=7, Gender=2)
                    //    + Story Stand-Event NPCs (cn0001, cn0002, Body=6, Gender=1) + Special Rig Quarantine (cn0013, Body=0)
                    //    + Overworld Town NPCs (cn1001, cn1002, Body=1, Gender=1) + Animal (ca0001, Type=4)
                    var charaBases = game.GetCharabase().ToList();
                    AddCpCharacterAssets(root, charaBases, modelNum: 36, body: 7, gender: 2, nameSeed: 3600);
                    AddCpCharacterAssets(root, charaBases, modelNum: 37, body: 7, gender: 2, nameSeed: 3700);
                    AddCpCharacterAssets(root, charaBases, modelNum: 201, body: 7, gender: 2, nameSeed: 2010);

                    var charaParams = game.GetCharaparams().ToList();
                    charaParams.Add(new GOSupport.CharaParam
                    {
                        ParamHash = Level5IdHelper.ComputeCrc32Utf8("para_cp0201"),
                        BaseHash = Level5IdHelper.ComputeCrc32Utf8("cp0201"),
                        PlayerPosition = 3,
                        Element = 1,
                        TP = 120,
                        Kick = 60,
                        Dribble = 60,
                        Technique = 60,
                        Block = 60,
                        Speed = 60,
                        Stamina = 60,
                        Catch = 40,
                        Luck = 60,
                        SpecialMoveOffset = 0,
                        SpecialMoveCount = 2
                    });
                    game.SaveCharaparams(charaParams.ToArray());

                    AddCnStandEventAssets(root, charaBases, modelNum: 1, body: 6, gender: 1, nameSeed: 5001);
                    AddCnStandEventAssets(root, charaBases, modelNum: 2, body: 6, gender: 1, nameSeed: 5002);
                    AddCnStandEventAssets(root, charaBases, modelNum: 13, body: 0, gender: 1, nameSeed: 9913); // Special rig quarantine!
                    AddCnOverworldAssets(root, charaBases, modelNum: 1001, body: 1, gender: 1);
                    AddCnOverworldAssets(root, charaBases, modelNum: 1002, body: 1, gender: 1);
                    charaBases.Add(new GOSupport.CharaBase
                    {
                        BaseHash = Level5IdHelper.ComputeCrc32Utf8("ca0001"),
                        ModelNumber = 1,
                        CharaBaseType = 4, // Animal/Dog -> MUST NEVER BE RANDOMIZED!
                        Body = 0,
                        Gender = 0,
                        NameHash = 7777
                    });
                    game.SaveCharaBase(charaBases.ToArray());

                    // 3. Write map m001a.npc.bin with Type 3 (HeadId 1001) + Type 4 Dog (HeadId 1)
                    Arc0SafeArchive.WriteFileAtPath(root, "/data/map/m001a/m001a.npc.bin", BuildSampleNpcBin());

                    // 4. Write genuine Squirrel 2.2.4 bytecode script /data/script/btl/btl0002_test.nutb
                    byte[] realNutb = BuildRealSquirrel224StoryBattleNutb();
                    Arc0SafeArchive.WriteFileAtPath(root, "/data/script/btl/btl0002_test.nutb", realNutb);

                    game.SaveCopyTo(fullFa);
                }

                // Also verify V.1.1.2 (1042) -> V.1.2.0 (1056) FBI Update .cia upgrade!
                var coordinator = new RandomizerCoordinator();
                var report = coordinator.GenerateRun(
                    fullFa,
                    Path.Combine(tempDir, "out"),
                    new RandomizerOptions { Seed = "TITAN-C45C-614C" });

                Assert.True(report.RandomizedCoachesCount >= 3);
                Assert.True(report.RandomizedManagersCount >= 1);
                Assert.Equal(2, report.RandomizedStoryNpcsCount); // cn0001 <-> cn0002 swapped; cn0013 untouched!
                Assert.Equal(1, report.RandomizedOverworldMapNpcsCount); // cn1001 -> cn1002; Type 4 dog untouched!
                Assert.True(report.PatchedNutbScriptsCount >= 1);
                Assert.True(report.InjectedTemporaryBattleSkillNodesCount >= 1);

                using var outGame = new SafeGOGame(Path.Combine(tempDir, "out", "Standalone", "ie_a.fa"), "es");
                var outRoot = outGame.IE_A.Directory;

                // Verify Story NPC swap + cn0013 quarantine
                var outCb = outGame.GetCharabase().Where(c => c.CharaBaseType == 3).ToDictionary(c => c.ModelNumber);
                Assert.Equal(5002, outCb[1].NameHash);
                Assert.Equal(5001, outCb[2].NameHash);
                Assert.Equal(9913, outCb[13].NameHash); // Quarantined special rig untouched!

                // Verify Overworld Map NPC swap + Type 4 Dog quarantine
                var outMapNpcs = MapDataService.LoadAllMapNpcBases(outRoot);
                var humanNpc = outMapNpcs.First(n => n.Record.Type == 3);
                var dogNpc = outMapNpcs.First(n => n.Record.Type == 4);
                Assert.Equal(1002, humanNpc.Record.HeadId);
                Assert.Equal(1, dogNpc.Record.HeadId);

                // Verify Coach stat profile + Spanish Dir-{N}-1 text synchronized in lockstep!
                var outDirs = outGame.GetItems("director").OfType<GOSupport.ItemConfigDirector>().OrderBy(d => d.ItemNumber).ToList();
                var outSysText = new T2bþ(Arc0SafeArchive.TryReadFileFromPath(outRoot, "/data/res/text/system_text_es.cfg.bin"));
                foreach (var dir in outDirs)
                {
                    int crc1 = Level5IdHelper.ComputeCrc32Utf8($"Dir-{dir.ItemNumber}-1");
                    string txt = outSysText.Texts[crc1].Strings[0].Text;
                    if (dir.KickCompatible == 20) Assert.Equal("Tiro +20", txt);
                    else if (dir.SpeedCompatible == 25) Assert.Equal("Velocidad +25", txt);
                    else if (dir.KickCompatible == 15) Assert.Equal("Equilibrio +15", txt);
                    else throw new InvalidOperationException("Unexpected director profile.");
                }

                // Re-parse the patched Squirrel 2.2.4 .nutb script and verify:
                // - RunUnlockSkill("para_cp0008", ...) was patched away from "who0001"
                // - CMND_BTL_USE_SKILL(0, "para_cp0010", ...) had CMND_BTL_LEARNING_SKILL injected right before it
                // - RunSkillScript("whs0002") shared literal stayed "whs0002" (NOT corrupted!)
                // - CMND_ADD_SET_DIRECTOR("iim0001") was remapped to another coach ("iim0002" or "iim0003")
                // - Relative JZ jump across the injected block now points past the injected instructions cleanly!
                byte[] patchedNutbBytes = Arc0SafeArchive.TryReadFileFromPath(outRoot, "/data/script/btl/btl0002_test.nutb");
                Assert.True(NutbBytecodeCodec.TryParse(patchedNutbBytes, out var parsedNutb));

                var postAudit = new NutbAuditReport();
                NutbBytecodeCodec.AnalyzeFunctionTree(parsedNutb.MainFunction, "/data/script/btl/btl0002_test.nutb", postAudit);

                var unlockCall = postAudit.SkillCallSites.First(c => c.Kind == NutbSkillCallKind.Unlock);
                Assert.NotEqual("who0001", unlockCall.SkillId);

                var learnCall = postAudit.SkillCallSites.First(c => c.Kind == NutbSkillCallKind.BattleLearn && c.CharaId == "para_cp0010");
                var useCall = postAudit.SkillCallSites.First(c => c.Kind == NutbSkillCallKind.BattleUse && c.CharaId == "para_cp0010");
                Assert.Equal(learnCall.SkillId, useCall.SkillId);
                Assert.NotEqual("whs0002", useCall.SkillId);

                // Shared literal "whs0002" is still present for RunSkillScript("whs0002")!
                Assert.True(
                    parsedNutb.MainFunction.Literals.Any(
                        lit => lit.TypeTag == NutbValue.OtString && lit.DecodeString(Encoding.UTF8) == "whs0002"));

                // Director remapped away from iim0001
                Assert.False(postAudit.StoryDirectorItemIds.Contains("iim0001"));
                Assert.True(postAudit.StoryDirectorItemIds.Count > 0);

                // Verify JZ jump target lands cleanly on the Return instruction at the end of the function!
                var jzIns = parsedNutb.MainFunction.Instructions.First(i => i.Op == NutbOpcode.Jz);
                int jzIp = parsedNutb.MainFunction.Instructions.IndexOf(jzIns);
                int targetIp = (jzIp + 1) + jzIns.Arg1;
                Assert.Equal(parsedNutb.MainFunction.Instructions.Count - 1, targetIp);
                Assert.Equal(NutbOpcode.Return, parsedNutb.MainFunction.Instructions[targetIp].Op);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        private static void SaveSoccerInfos(SafeGOGame game, List<ISoccerInfo> soccers)
        {
            var cfg = new CfgBin<CfgTreeNode>();
            cfg.Open(game.GetFileContent("soccer_config"));
            var begin = cfg.Entries.FindByName("SOCCER_INFO_BEGIN");
            if (begin == null)
            {
                begin = new CfgTreeNode(new Entry("SOCCER_INFO_BEGIN", new List<Variable> { new Variable(CfgValueType.Int, 0) }));
                cfg.Entries.AddChild(begin);
                cfg.Entries.AddChild(new CfgTreeNode(new Entry("SOCCER_INFO_END")));
            }
            begin.Children.Clear();
            begin.Item.Variables[0].Value = soccers.Count;
            foreach (var s in soccers)
            {
                begin.AddChild(new CfgTreeNode(new Entry("SOCCER_INFO", CfgBinMapper.GetVariablesFromInstance(s))));
            }
            Arc0SafeArchive.WriteFileAtPath(game.IE_A.Directory, "/data/res/soccer/soccer_config.cfg.bin", cfg.Save());
        }

        private static void AddDirectorsToItemConfig(
            SafeGOGame game,
            (string Code, int Num, int Kick, int Speed, string Desc1)[] defs)
        {
            var directors = defs.Select(d => new GOSupport.ItemConfigDirector
            {
                ItemID = Level5IdHelper.ComputeCrc32Utf8(d.Code),
                ItemNumber = d.Num,
                NameID = Level5IdHelper.ComputeCrc32Utf8("nm_" + d.Code),
                DescriptionID = Level5IdHelper.ComputeCrc32Utf8("ds_" + d.Code),
                ItemCategory = 21,
                KickCompatible = d.Kick,
                SpeedCompatible = d.Speed
            }).ToArray();

            var itemCfg = new CfgBin<CfgTreeNode>();
            itemCfg.Open(game.GetFileContent("item_config"));
            if (itemCfg.Entries.FindByName("ITEM_DIRECTOR_BEGIN") == null)
            {
                var begin = new CfgTreeNode(new Entry("ITEM_DIRECTOR_BEGIN", new List<Variable> { new Variable(CfgValueType.Int, 0) }));
                itemCfg.Entries.AddChild(begin);
                itemCfg.Entries.AddChild(new CfgTreeNode(new Entry("ITEM_DIRECTOR_END")));
                Arc0SafeArchive.WriteFileAtPath(game.IE_A.Directory, "/data/res/item/item_config.cfg.bin", itemCfg.Save());
            }
            game.SaveItems(directors);

            var textCfg = new CfgBin<CfgTreeNode> { Encoding = Encoding.UTF8 };
            var textBegin = new CfgTreeNode(new Entry("TEXT_INFO_BEGIN", new List<Variable> { new Variable(CfgValueType.Int, defs.Length * 2) }));
            foreach (var d in defs)
            {
                textBegin.AddChild(new CfgTreeNode(new Entry("TEXT_INFO", new List<Variable>
                {
                    new Variable(CfgValueType.Int, Level5IdHelper.ComputeCrc32Utf8($"Dir-{d.Num}-1")),
                    new Variable(CfgValueType.Int, 0),
                    new Variable(CfgValueType.String, d.Desc1)
                })));
                textBegin.AddChild(new CfgTreeNode(new Entry("TEXT_INFO", new List<Variable>
                {
                    new Variable(CfgValueType.Int, Level5IdHelper.ComputeCrc32Utf8($"Dir-{d.Num}-2")),
                    new Variable(CfgValueType.Int, 0),
                    new Variable(CfgValueType.String, "Sin penalización")
                })));
            }
            textCfg.Entries.AddChild(textBegin);
            textCfg.Entries.AddChild(new CfgTreeNode(new Entry("TEXT_INFO_END")));
            Arc0SafeArchive.WriteFileAtPath(game.IE_A.Directory, "/data/res/text/system_text_es.cfg.bin", textCfg.Save());
        }

        private static void AddCpCharacterAssets(
            StudioElevenLib.Tools.VirtualDirectory root,
            List<ICharabase> charaBases,
            int modelNum,
            int body,
            int gender,
            int nameSeed)
        {
            string code = $"cp{modelNum:D4}";
            charaBases.RemoveAll(c => c.CharaBaseType == 1 && c.ModelNumber == modelNum);
            charaBases.Add(new GOSupport.CharaBase
            {
                BaseHash = Level5IdHelper.ComputeCrc32Utf8(code),
                ModelNumber = modelNum,
                CharaBaseType = 1,
                Body = body,
                Gender = gender,
                Skin = 1,
                Year = 1,
                NameHash = nameSeed,
                NicknameHash = nameSeed + 1,
                DescriptionHash = nameSeed + 2
            });
            byte tag = (byte)(modelNum & 0xFF);
            Arc0SafeArchive.WriteFileAtPath(root, $"/data/bustup/face/{code}a.xi", new byte[] { 0xB0, tag });
            Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/rpg/face/{code}m.xc", new byte[] { 0xC0, tag });
            Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/waza/face/{code}a.xc", new byte[] { 0xD0, tag });
        }

        private static void AddCnStandEventAssets(
            StudioElevenLib.Tools.VirtualDirectory root,
            List<ICharabase> charaBases,
            int modelNum,
            int body,
            int gender,
            int nameSeed)
        {
            string code = $"cn{modelNum:D4}";
            charaBases.Add(new GOSupport.CharaBase
            {
                BaseHash = Level5IdHelper.ComputeCrc32Utf8(code),
                ModelNumber = modelNum,
                CharaBaseType = 3,
                Body = body,
                Gender = gender,
                Skin = 1,
                Year = 3,
                NameHash = nameSeed,
                NicknameHash = nameSeed + 1,
                DescriptionHash = nameSeed + 2
            });
            byte tag = (byte)(modelNum & 0xFF);
            Arc0SafeArchive.WriteFileAtPath(root, $"/data/bustup/face/{code}a.xi", new byte[] { 0xE0, tag });
            Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/rpg/face/{code}m.xc", new byte[] { 0xF0, tag });
        }

        private static void AddCnOverworldAssets(
            StudioElevenLib.Tools.VirtualDirectory root,
            List<ICharabase> charaBases,
            int modelNum,
            int body,
            int gender)
        {
            string code = $"cn{modelNum:D4}";
            charaBases.Add(new GOSupport.CharaBase
            {
                BaseHash = Level5IdHelper.ComputeCrc32Utf8(code),
                ModelNumber = modelNum,
                CharaBaseType = 3,
                Body = body,
                Gender = gender,
                Skin = 1,
                NameHash = modelNum
            });
            Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/rpg/face/{code}m.xc", new byte[] { 0x99, (byte)(modelNum & 0xFF) });
        }

        private static byte[] BuildSampleNpcBin()
        {
            var cfg = new CfgBin<CfgTreeNode> { Encoding = Encoding.UTF8 };
            var begin = new CfgTreeNode(new Entry("NPC_BASE_BEGIN", new List<Variable> { new Variable(CfgValueType.Int, 2) }));
            begin.AddChild(new CfgTreeNode(new Entry("NPC_BASE", new List<Variable>
            {
                new Variable(CfgValueType.Int, 101),  // NpcId
                new Variable(CfgValueType.Int, 1001), // HeadId -> cn1001
                new Variable(CfgValueType.Int, 3),    // Type == 3 (Human NPC)
                new Variable(CfgValueType.Int, 0),
                new Variable(CfgValueType.Int, 0),
                new Variable(CfgValueType.Int, 0),
                new Variable(CfgValueType.Int, 0)
            })));
            begin.AddChild(new CfgTreeNode(new Entry("NPC_BASE", new List<Variable>
            {
                new Variable(CfgValueType.Int, 102),  // NpcId
                new Variable(CfgValueType.Int, 1),    // HeadId -> ca0001
                new Variable(CfgValueType.Int, 4),    // Type == 4 (Animal/Dog -> MUST STAY 1!)
                new Variable(CfgValueType.Int, 0),
                new Variable(CfgValueType.Int, 0),
                new Variable(CfgValueType.Int, 0),
                new Variable(CfgValueType.Int, 0)
            })));
            cfg.Entries.AddChild(begin);
            cfg.Entries.AddChild(new CfgTreeNode(new Entry("NPC_BASE_END")));
            return cfg.Save();
        }

        private static byte[] BuildRealSquirrel224StoryBattleNutb()
        {
            // Literals:
            // 0: "RunUnlockSkill"
            // 1: "para_cp0008"
            // 2: "who0001"
            // 3: "CMND_BTL_USE_SKILL"
            // 4: "para_cp0010"
            // 5: "whs0002"   (shared between CMND_BTL_USE_SKILL and RunSkillScript!)
            // 6: "RunSkillScript"
            // 7: "CMND_ADD_SET_DIRECTOR"
            // 8: "iim0001"
            var fn = new NutbFunctionProto
            {
                SourceName = NutbValue.FromAsciiString("btl0002_test.nut"),
                Name = NutbValue.FromAsciiString("main"),
                HasDefaultParamsSection = false, // Exact Squirrel 2.2.3/2.2.4 layout!
                VarParamsByteSize = 4,
                StackSize = 12
            };
            fn.Parameters.Add(NutbValue.FromAsciiString("this"));

            string[] lits =
            {
                "RunUnlockSkill",
                "para_cp0008",
                "who0001",
                "CMND_BTL_USE_SKILL",
                "para_cp0010",
                "whs0002",
                "RunSkillScript",
                "CMND_ADD_SET_DIRECTOR",
                "iim0001"
            };
            foreach (string s in lits)
                fn.Literals.Add(NutbValue.FromAsciiString(s));

            // 1) JZ slot 1 -> jump to final Return (tests relative jump fixup across injected block!)
            //    Before injection, total instructions will be 19; JZ is at index 0, Return is at index 18 => rel = 18 - 1 = 17.
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.Jz, 1, 17, 0, 0));

            // 2) RunUnlockSkill("para_cp0008", "who0001"): [0..3] -> 4 instructions
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.PrepCallK, 2, 0, 0, 3));
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.Load, 4, 1, 0, 0));
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.Load, 5, 2, 0, 0));
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.Call, 255, 2, 3, 3));

            // 3) CMND_BTL_USE_SKILL(0, "para_cp0010", "whs0002", -1, 1): 7 instructions
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.PrepCallK, 2, 3, 0, 3));
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.LoadInt, 4, 0, 0, 0));
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.Load, 5, 4, 0, 0));
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.Load, 6, 5, 0, 0));
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.LoadInt, 7, -1, 0, 0));
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.LoadInt, 8, 1, 0, 0));
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.Call, 255, 2, 3, 6));

            // 4) RunSkillScript("whs0002"): 3 instructions (uses SAME literal #5 "whs0002"!)
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.PrepCallK, 2, 6, 0, 3));
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.Load, 4, 5, 0, 0));
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.Call, 255, 2, 3, 2));

            // 5) CMND_ADD_SET_DIRECTOR("iim0001"): 3 instructions
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.PrepCallK, 2, 7, 0, 3));
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.Load, 4, 8, 0, 0));
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.Call, 255, 2, 3, 2));

            // 6) Final Return (index 18 before injection)
            fn.Instructions.Add(new NutbInstruction(NutbOpcode.Return, 255, 0, 0, 0));

            var script = new NutbStream
            {
                CharSize = 1,
                IntSize = 4,
                FloatSize = 4,
                MainFunction = fn
            };
            return NutbBytecodeCodec.Serialize(script);
        }

        [Fact]
        public void PachangaKeeperAndEarlyRaimonShootSoftlock_NeverCreatesUnbeatableKeeperOrShootlessEarlyParty()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_v121_pachanga_balance_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string sourceFa = Path.Combine(tempDir, "ie_a.fa");
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(sourceFa);

                // Simulate realistic retail Level-1 Base Stats (Catch=14..28, Kick=18..30) + Arion (para_cp0008, MF)
                // + early Raimon field starters (para_cp0002, para_cp0006, para_cp0007, para_cp0009)
                int arionHash = Level5IdHelper.ComputeCrc32Utf8("para_cp0008");
                int riccardoHash = Level5IdHelper.ComputeCrc32Utf8("para_cp0009");
                int subaruHash = Level5IdHelper.ComputeCrc32Utf8("para_cp0002");
                int adeHash = Level5IdHelper.ComputeCrc32Utf8("para_cp0006");
                int eugeneHash = Level5IdHelper.ComputeCrc32Utf8("para_cp0007");
                var whsLookup = Level5IdHelper.BuildPrefixLookup("whs");
                var whkLookup = Level5IdHelper.BuildPrefixLookup("whk");

                using (var game = new SafeGOGame(sourceFa))
                {
                    var bases = game.GetCharabase().ToList();
                    var cpList = game.GetCharaparams().ToList();
                    var stList = game.GetSkillTable().ToList();

                    // Convert baseline synthetic Catch/Kick to retail Level-1 Base Stat scale (~14..32)
                    foreach (var p in cpList)
                    {
                        p.Catch = p.PlayerPosition == 1 ? 26 : 15;
                        p.Kick = p.PlayerPosition == 2 ? 28 : 20;
                    }

                    (string Code, int Num, int Pos, int LvLearned)[] earlyDefs =
                    {
                        ("cp0008", 8, 3, 101), // Arion (MF, all 3 vanilla slots 101)
                        ("cp0009", 9, 3, 1),   // Riccardo (MF)
                        ("cp0002", 2, 4, 5),   // Subaru (DF)
                        ("cp0006", 6, 3, 5),   // Adé (MF)
                        ("cp0007", 7, 3, 5)    // Eugene (MF)
                    };

                    foreach (var ed in earlyDefs)
                    {
                        int bHash = Level5IdHelper.ComputeCrc32Utf8(ed.Code);
                        int pHash = Level5IdHelper.ComputeCrc32Utf8("para_" + ed.Code);
                        if (!bases.Any(b => b.BaseHash == bHash))
                        {
                            AddCpCharacterAssets(game.IE_A.Directory, bases, modelNum: ed.Num, body: 1, gender: 1, nameSeed: 9000 + ed.Num);
                        }

                        if (!cpList.Any(p => p.ParamHash == pHash))
                        {
                            int off = stList.Count;
                            int count = ed.Code == "cp0008" ? 4 : 2;
                            for (int s = 0; s < count; s++)
                            {
                                stList.Add(new GOSupport.SkillTable
                                {
                                    SkillIndex = s,
                                    SkillHash = Level5IdHelper.ComputeCrc32Utf8("who0001"), // Dribble in vanilla
                                    LevelLearned = ed.Code == "cp0008" && s < 3 ? 101 : (s == 0 ? ed.LvLearned : 20)
                                });
                            }
                            cpList.Add(new GOSupport.CharaParam
                            {
                                ParamHash = pHash,
                                BaseHash = bHash,
                                PlayerPosition = ed.Pos,
                                Element = 1,
                                FP = 80,
                                TP = 85,
                                Kick = 22,
                                Dribble = 24,
                                Technique = 22,
                                Block = 20,
                                Speed = 22,
                                Stamina = 20,
                                Catch = 16,
                                Luck = 18,
                                Freedom = 40,
                                SpecialMoveOffset = off,
                                SpecialMoveCount = count
                            });
                        }
                    }

                    game.SaveCharaBase(bases.ToArray());
                    game.SaveCharaparams(cpList.ToArray());
                    game.SaveSkillTable(stList.ToArray());
                    string updated = Path.Combine(tempDir, "ie_a_v121.fa");
                    game.SaveCopyTo(updated);
                    sourceFa = updated;
                }

                string[] seedsToTest = { "TITAN-C45C-614C", "SOFTLOCK-SEED-1", "SOFTLOCK-SEED-2", "SOFTLOCK-SEED-3" };
                var coordinator = new RandomizerCoordinator();

                foreach (string seed in seedsToTest)
                {
                    string outDir = Path.Combine(tempDir, "out_" + seed);
                    coordinator.GenerateRun(sourceFa, outDir, new RandomizerOptions { Seed = seed });

                    using var modded = new SafeGOGame(Path.Combine(outDir, "Standalone", "ie_a.fa"));
                    var paramByHash = modded.GetCharaparams().ToDictionary(p => p.ParamHash);
                    var skillByHash = modded.GetSkillConfigs(false).ToDictionary(s => s.SkillHash);
                    var st = modded.GetSkillTable();

                    // 1. Arion (para_cp0008) MUST ALWAYS have a Level-1 solo Shoot technique (whsXXXX) & enough Lv.1 TP for >=2 shots!
                    var arion = paramByHash[arionHash];
                    var arionSlot0 = st[arion.SpecialMoveOffset];
                    Assert.Equal(1, arionSlot0.LevelLearned);
                    Assert.True(whsLookup.ContainsKey(arionSlot0.SkillHash),
                        $"Seed {seed}: Arion Slot 0 MUST be a Shoot technique (whsXXXX), got {arionSlot0.SkillHash:X8}!");
                    var arionShotCfg = skillByHash[arionSlot0.SkillHash];
                    Assert.True(arionShotCfg.PartnerNumber <= 1, "Arion Lv.1 starter shot must be a solo shot (PartnerNumber <= 1)!");

                    // 2. At least 3 early Raimon members (Arion + Riccardo + >=2 of Subaru/Adé/Eugene) MUST have Level-1 Shoot moves!
                    int[] earlySquad = { arionHash, riccardoHash, subaruHash, adeHash, eugeneHash };
                    int earlyShootersCount = earlySquad.Count(ph =>
                    {
                        var p = paramByHash[ph];
                        for (int s = 0; s < p.SpecialMoveCount; s++)
                        {
                            var sl = st[p.SpecialMoveOffset + s];
                            if (sl.LevelLearned == 1 && whsLookup.ContainsKey(sl.SkillHash))
                                return true;
                        }
                        return false;
                    });
                    Assert.True(earlyShootersCount >= 4,
                        $"Seed {seed}: Expected at least 4 early Raimon shooters with Lv.1 Shoot techniques, got {earlyShootersCount}!");

                    // 3. ALL playable characters (100% — including EVERY 5v5 Pachanga player!) MUST have >=1 Lv.1 active move OR >=1 EG,
                    //    while 5v5 Pachanga keepers are still never turned into dedicated GK Catch walls!
                    var activeSkillLookup = Level5IdHelper.BuildPrefixLookup("whs", "who", "whd", "whk");
                    var visProfile = IEGORandomizer.Engine.Validation.VisualCoherenceValidator.AnalyzeArchive(modded);
                    foreach (var p in modded.GetCharaparams().Where(c => visProfile.CoherentPlayableParamHashes.Contains(c.ParamHash)))
                    {
                        bool hasLv1Move = false;
                        for (int s = 0; s < p.SpecialMoveCount; s++)
                        {
                            var sl = st[p.SpecialMoveOffset + s];
                            if (sl.LevelLearned == 1 && activeSkillLookup.ContainsKey(sl.SkillHash))
                            {
                                hasLv1Move = true;
                                break;
                            }
                        }
                        bool hasEg = p.FightingSpiritHash != 0;
                        Assert.True(hasLv1Move || hasEg,
                            $"Seed {seed}: Player 0x{p.ParamHash:X8} has NEITHER a Lv.1 active move NOR an EG! ALL players must have at least one!");
                    }

                    var fullMatchIds = new HashSet<int>(
                        modded.GetStoryTeamsOnly()
                              .Where(t => t.Player6 != 0 && t.Player11 != 0)
                              .SelectMany(t => new[] { t.Player1, t.Player2, t.Player3, t.Player4, t.Player5, t.Player6, t.Player7, t.Player8, t.Player9, t.Player10, t.Player11 }));

                    foreach (var et in modded.GetEncounterTeamsOnly())
                    {
                        if (et.Player1 == 0 || fullMatchIds.Contains(et.Player1))
                            continue;

                        var pk = paramByHash[et.Player1];
                        Assert.NotEqual(1, pk.PlayerPosition, $"Seed {seed}: 5v5 Pachanga keeper must NOT be forced to GK (Position=1)!");
                        Assert.True(pk.Catch <= 24, $"Seed {seed}: 5v5 Pachanga keeper Lv.1 Base Catch ({pk.Catch}) must not be inflated!");

                        for (int s = 0; s < pk.SpecialMoveCount; s++)
                        {
                            var sl = st[pk.SpecialMoveOffset + s];
                            if (sl.LevelLearned <= 5)
                            {
                                Assert.False(whkLookup.ContainsKey(sl.SkillHash),
                                    $"Seed {seed}: 5v5 Pachanga keeper must NOT have an early GK Save supertechnique (whkXXXX)!");
                            }
                        }
                    }

                    // 4. Pachanga TeamParam must NEVER wear GK Gloves (GlovesID == 0)
                    var encIds = new HashSet<int>(modded.GetEncounterTeamsOnly().Select(e => e.TeamConfigID));
                    foreach (var tp in modded.GetTeamParams().Where(t => encIds.Contains(t.TeamConfigID)))
                    {
                        Assert.Equal(0, tp.GlovesID);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
