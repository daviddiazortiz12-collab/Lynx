using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using IEGORandomizer.App;
using IEGORandomizer.Engine;
using IEGORandomizer.Engine.Cia;
using IEGORandomizer.Engine.Configuration;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.Core.RomFs;
using Lynx.Core.RomFs.Cia;
using Lynx.InazumaEleven.Games.GO;
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Tests
{
    /// <summary>
    /// Regression tests for v1.1.2 real Nintendo 2DS first-pachanga freeze fixes &amp; FBI in-place update .cia workflow:
    /// 1. Overworld pachangas with TeamConfigID == 0 / SoccerMode == 1 NEVER receive 11v11 (ifa/Cat.19) formations,
    ///    PalPack (Cat.17) items, Keshin (Cat.18) items, Coaches, or Tactics.
    /// 2. Street-clothes teams (Uniform == 0 in TeamParamInfo &amp; EncountMapRecord) preserve Uniform == 0,
    ///    and GK-less kits (uzaXXXX without uzbXXXX) are never assigned to pachangas.
    /// 3. Overworld *.encount.bin (ENCOUNT_INFO) strictly preserves ParamHash vs BaseHash domain on PlayerID1..5
    ///    with 5 unique BaseHashes and preserves Boots domain (0 stays 0, small shoe model int stays small int).
    /// 4. Retail SKILL_CONFIG_INFO (SkillPosition 1=Shoot, 2=Dribble, 3=Block, 4=Save, 15=Talent, plus non-wh*
    ///    Void/Totem stubs and SkillType==2 Keshin moves) maps roles accurately and never corrupts passive talents.
    /// 5. FBI in-place update .cia workflow bumps TMD TitleVersion (V.0.0.0 -&gt; V.1.1.2 -&gt; V.1.1.3 or custom),
    ///    preserves TitleID 0004000000112F00 &amp; SaveDataSize 512 KB, and supports companion Luma3DS patch export.
    /// </summary>
    public sealed class FirstPachangaAndFbiUpdateTests
    {
        [Fact]
        public void FirstPachanga_OverworldZeroConfigAndStreetClothes_NeverReceives11v11FormationsKeshinTacticsOrKitOverwrite()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_v112_pachanga_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string srcPath = Path.Combine(tempDir, "src", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(srcPath);

                string retailPachangaFa = Path.Combine(tempDir, "retail_pachanga", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(retailPachangaFa)!);

                int ifa0001 = Level5IdHelper.ComputeCrc32Utf8("ifa0001");
                int ifb0001 = Level5IdHelper.ComputeCrc32Utf8("ifb0001");
                int ifb0002 = Level5IdHelper.ComputeCrc32Utf8("ifb0002");
                int iipPalpack17 = Level5IdHelper.ComputeCrc32Utf8("iip0001");
                int iiaKeshin18 = Level5IdHelper.ComputeCrc32Utf8("iia0001");

                int paraEp1StreetPachanga = Level5IdHelper.ComputeCrc32Utf8("para_btl_ep1_street");
                int btlEp1StreetPachanga = Level5IdHelper.ComputeCrc32Utf8("btl_ep1_street");
                int paraOverworldKitPachanga = Level5IdHelper.ComputeCrc32Utf8("para_btl_overworld_kit");
                int btlOverworldKitPachanga = Level5IdHelper.ComputeCrc32Utf8("btl_overworld_kit");

                using (var setup = new SafeGOGame(srcPath, "es"))
                {
                    // Add retail-style ifa0001 (11v11), ifb0001/ifb0002 (5v5), Cat.17 Palpack, and Cat.18 Keshin items into ITEM_CONSUME_BEGIN
                    var itemCfg = new StudioElevenLib.Level5.Binary.CfgBin<StudioElevenLib.Level5.Binary.Collections.CfgTreeNode>();
                    itemCfg.Open(setup.GetFileContent("item_config"));
                    var consumeBegin = itemCfg.Entries.FindByName("ITEM_CONSUME_BEGIN");
                    void AppendConsumeNode(int id, int itemRef, int cat)
                    {
                        consumeBegin.AddChild(new StudioElevenLib.Level5.Binary.Collections.CfgTreeNode(
                            new StudioElevenLib.Level5.Binary.Logic.Entry("ITEM_CONSUME", new List<StudioElevenLib.Level5.Binary.Logic.Variable>
                            {
                                new(StudioElevenLib.Level5.Binary.Logic.CfgValueType.Int, id),
                                new(StudioElevenLib.Level5.Binary.Logic.CfgValueType.Int, 0),
                                new(StudioElevenLib.Level5.Binary.Logic.CfgValueType.Int, 0),
                                new(StudioElevenLib.Level5.Binary.Logic.CfgValueType.Int, itemRef),
                                new(StudioElevenLib.Level5.Binary.Logic.CfgValueType.Int, cat),
                                new(StudioElevenLib.Level5.Binary.Logic.CfgValueType.Int, 99),
                                new(StudioElevenLib.Level5.Binary.Logic.CfgValueType.Int, 100),
                                new(StudioElevenLib.Level5.Binary.Logic.CfgValueType.Int, 200),
                            })));
                    }
                    AppendConsumeNode(ifa0001, 50, 19);
                    AppendConsumeNode(ifb0001, 50, 20);
                    AppendConsumeNode(ifb0002, 50, 20);
                    AppendConsumeNode(iipPalpack17, 90, 17);
                    AppendConsumeNode(iiaKeshin18, 100, 18);
                    consumeBegin.Item.Variables[0].Value = consumeBegin.Children.Count;
                    Arc0SafeArchive.WriteFileAtPath(setup.IE_A.Directory, "/data/res/item/item_config.cfg.bin", itemCfg.Save());

                    // Add GK-less kit 9 (uza0009.xc ONLY, NO uzb0009.xc!) — must NEVER be assigned to any team!
                    Arc0SafeArchive.WriteFileAtPath(
                        setup.IE_A.Directory,
                        "/data/chr/model/rpg/body/uza0009.xc",
                        Encoding.ASCII.GetBytes("GK_LESS_KIT_uza0009"));

                    // Add two retail overworld pachangas with TeamConfigID == 0 (players live in *.encount.bin!):
                    // 1) Episode 1 street-clothes pachanga (Uniform == 0, CoachID == 0, TacticID == 0)
                    // 2) Overworld kit pachanga (Uniform == 2000)
                    var tps = setup.GetTeamParams().ToList();
                    tps.Add(new GOSupport.TeamParamInfo
                    {
                        TeamParamID = paraEp1StreetPachanga,
                        TeamConfigID = 0,
                        Uniform = 0, // Street clothes / school uniform!
                        FormationID = ifb0001,
                        CoachID = 0,
                        TacticID = 0,
                        Level = 3,
                        BootsID = Level5IdHelper.ComputeCrc32Utf8("iie0001")
                    });
                    tps.Add(new GOSupport.TeamParamInfo
                    {
                        TeamParamID = paraOverworldKitPachanga,
                        TeamConfigID = 0,
                        Uniform = 2000,
                        FormationID = ifb0002,
                        CoachID = 0,
                        TacticID = 0,
                        Level = 8,
                        BootsID = Level5IdHelper.ComputeCrc32Utf8("iie0002")
                    });
                    setup.SaveTeamParams(tps.ToArray());

                    var soccers = setup.GetSoccers().ToList();
                    soccers.Add(new GOSupport.SoccerInfo
                    {
                        SoccerID = btlEp1StreetPachanga,
                        TeamParamID = paraEp1StreetPachanga,
                        SoccerMode = 1, // 5v5 Mini-Battle!
                        VictoryCondition = 1
                    });
                    soccers.Add(new GOSupport.SoccerInfo
                    {
                        SoccerID = btlOverworldKitPachanga,
                        TeamParamID = paraOverworldKitPachanga,
                        SoccerMode = 1, // 5v5 Mini-Battle!
                        VictoryCondition = 2
                    });
                    setup.SaveSoccers(soccers.ToArray());

                    // Mutate m001a_enc.bin so:
                    // - Spawn[0]: TeamID = btlEp1StreetPachanga, Uniform = 0 (street clothes), Boots = 3 (small shoe int),
                    //             PlayerID1..5 = retail ParamHash (para_cp0101..para_cp0105)!
                    // - Spawn[1]: TeamID = btlOverworldKitPachanga, Uniform = CRC32("iiu0002"), Boots = 0,
                    //             PlayerID1..5 = BaseHash (cp0106..cp0110)!
                    var spawns = MapDataService.LoadAllEncounterSpawns(setup.IE_A.Directory);
                    Assert.True(spawns.Count >= 2);

                    spawns[0].Record.TeamID = btlEp1StreetPachanga;
                    spawns[0].Record.ChasePlayer = 2;
                    spawns[0].Record.Uniform = 0;
                    spawns[0].Record.Boots = 3;
                    spawns[0].Record.PlayerID1 = Level5IdHelper.ComputeCrc32Utf8("para_cp0101");
                    spawns[0].Record.PlayerID2 = Level5IdHelper.ComputeCrc32Utf8("para_cp0102");
                    spawns[0].Record.PlayerID3 = Level5IdHelper.ComputeCrc32Utf8("para_cp0103");
                    spawns[0].Record.PlayerID4 = Level5IdHelper.ComputeCrc32Utf8("para_cp0104");
                    spawns[0].Record.PlayerID5 = Level5IdHelper.ComputeCrc32Utf8("para_cp0105");

                    spawns[1].Record.TeamID = btlOverworldKitPachanga;
                    spawns[1].Record.ChasePlayer = 1;
                    spawns[1].Record.Uniform = Level5IdHelper.ComputeCrc32Utf8("iiu0002");
                    spawns[1].Record.Boots = 0;
                    spawns[1].Record.PlayerID1 = Level5IdHelper.ComputeCrc32Utf8("cp0106");
                    spawns[1].Record.PlayerID2 = Level5IdHelper.ComputeCrc32Utf8("cp0107");
                    spawns[1].Record.PlayerID3 = Level5IdHelper.ComputeCrc32Utf8("cp0108");
                    spawns[1].Record.PlayerID4 = Level5IdHelper.ComputeCrc32Utf8("cp0109");
                    spawns[1].Record.PlayerID5 = Level5IdHelper.ComputeCrc32Utf8("cp0110");

                    MapDataService.SaveEncounterSpawns(setup.IE_A.Directory, spawns);
                    setup.SaveCopyTo(retailPachangaFa);
                }

                string outDir = Path.Combine(tempDir, "out");
                var coordinator = new RandomizerCoordinator();
                var rep = coordinator.GenerateRun(retailPachangaFa, outDir, new RandomizerOptions
                {
                    Seed = "FIRST-PACHANGA-2DS-FIX",
                    Edition = GameEdition.Light
                });

                Assert.True(rep.OutputIntegrityVerified);
                Assert.True(rep.SourceImmutabilityVerified);

                using var modded = new SafeGOGame(Path.Combine(outDir, "Standalone", "ie_a.fa"), "es");
                var moddedTps = modded.GetTeamParams();
                var paramSet = new HashSet<int>(modded.GetCharaparams().Select(p => p.ParamHash));
                var baseSet = new HashSet<int>(modded.GetCharabase().Select(b => b.BaseHash));
                var paramToBase = modded.GetCharaparams().ToDictionary(p => p.ParamHash, p => p.BaseHash);

                var validMiniFormations = new HashSet<int>
                {
                    Level5IdHelper.ComputeCrc32Utf8("iif0003"),
                    ifb0001,
                    ifb0002
                };

                var streetTp = moddedTps.Single(t => t.TeamParamID == paraEp1StreetPachanga);
                var overworldKitTp = moddedTps.Single(t => t.TeamParamID == paraOverworldKitPachanga);

                // 1. Overworld TeamConfigID==0 pachangas MUST have 5v5 mini-formations, CoachID==0, TacticID==0
                Assert.True(validMiniFormations.Contains(streetTp.FormationID),
                    $"Street pachanga received non-5v5 FormationID 0x{streetTp.FormationID:X8}!");
                Assert.Equal(0, streetTp.CoachID);
                Assert.Equal(0, streetTp.TacticID);

                Assert.True(validMiniFormations.Contains(overworldKitTp.FormationID),
                    $"Overworld pachanga received non-5v5 FormationID 0x{overworldKitTp.FormationID:X8}!");
                Assert.Equal(0, overworldKitTp.CoachID);
                Assert.Equal(0, overworldKitTp.TacticID);

                // 2. Street-clothes Uniform == 0 MUST stay 0; kit pachanga must randomize to a complete GK-capable kit (never 9!)
                Assert.Equal(0, streetTp.Uniform);
                Assert.True(overworldKitTp.Uniform >= 1000);
                foreach (var tp in moddedTps)
                {
                    Assert.NotEqual(9, tp.Uniform / 1000, "GK-less kit 9 (missing uzb0009.xc) must never be assigned!");
                    Assert.NotEqual(iipPalpack17, tp.FormationID, "PalPack Cat.17 must never be used as FormationID!");
                    Assert.NotEqual(iiaKeshin18, tp.TacticID, "Keshin Cat.18 must never be used as TacticID!");
                }

                // 3. Verify *.encount.bin domain preservation & 5 unique players
                var moddedSpawns = MapDataService.LoadAllEncounterSpawns(modded.IE_A.Directory);
                var sp0 = moddedSpawns[0].Record;
                Assert.Equal(2, sp0.ChasePlayer);
                Assert.Equal(0, sp0.Uniform); // Street clothes preserved!
                Assert.Equal(3, sp0.Boots);   // Small shoe model int preserved (not overwritten with negative CRC32)!

                int[] sp0Players = { sp0.PlayerID1, sp0.PlayerID2, sp0.PlayerID3, sp0.PlayerID4, sp0.PlayerID5 };
                foreach (int pid in sp0Players)
                {
                    Assert.True(paramSet.Contains(pid), $"Expected ParamHash in Spawn[0], got 0x{pid:X8}!");
                    Assert.False(baseSet.Contains(pid), "ParamHash slot must never be overwritten with BaseHash!");
                }
                Assert.Equal(5, sp0Players.Select(p => paramToBase[p]).Distinct().Count());
                Assert.NotEqual(Level5IdHelper.ComputeCrc32Utf8("para_cp0101"), sp0.PlayerID1);

                var sp1 = moddedSpawns[1].Record;
                Assert.Equal(1, sp1.ChasePlayer);
                Assert.Equal(0, sp1.Boots); // Boots == 0 preserved!
                int expectedIiu = Level5IdHelper.ComputeCrc32Utf8($"iiu{overworldKitTp.Uniform / 1000:D4}");
                Assert.Equal(expectedIiu, sp1.Uniform);

                int[] sp1Players = { sp1.PlayerID1, sp1.PlayerID2, sp1.PlayerID3, sp1.PlayerID4, sp1.PlayerID5 };
                foreach (int bid in sp1Players)
                {
                    Assert.True(baseSet.Contains(bid), $"Expected BaseHash in Spawn[1], got 0x{bid:X8}!");
                }
                Assert.Equal(5, sp1Players.Distinct().Count());
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void RetailSkillEncoding_SkillPosition15Talents_RetailRoleNumbers_AndNonWhVoidSkills_NeverCorruptMovesets()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_v112_skills_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string srcPath = Path.Combine(tempDir, "src", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(srcPath);

                string retailSkillsFa = Path.Combine(tempDir, "retail_skills", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(retailSkillsFa)!);

                int voidDummyRenard = Level5IdHelper.ComputeCrc32Utf8("Renard");
                int voidTotemStub = unchecked((int)0x0F7FB4C1);
                int keshinType2Stub = Level5IdHelper.ComputeCrc32Utf8("whs0500");

                var whsSet = new HashSet<int>();
                var whoSet = new HashSet<int>();
                var whdSet = new HashSet<int>();
                var whkSet = new HashSet<int>();
                var whpSet = new HashSet<int>();

                for (int i = 1; i <= 4; i++)
                {
                    whsSet.Add(Level5IdHelper.ComputeCrc32Utf8($"whs{i:D4}"));
                    whoSet.Add(Level5IdHelper.ComputeCrc32Utf8($"who{i:D4}"));
                    whdSet.Add(Level5IdHelper.ComputeCrc32Utf8($"whd{i:D4}"));
                    whkSet.Add(Level5IdHelper.ComputeCrc32Utf8($"whk{i:D4}"));
                    whpSet.Add(Level5IdHelper.ComputeCrc32Utf8($"whp{i:D4}"));
                }

                using (var setup = new SafeGOGame(srcPath, "es"))
                {
                    // Rewrite SKILL_CONFIG_INFO to exact retail IEGO1 encoding:
                    // 1 = Shoot (whs), 2 = Dribble (who), 3 = Block (whd), 4 = Save (whk), 15 = Talent (whp), SkillType = 1
                    var skills = setup.GetSkillConfigs(false).ToList();
                    foreach (var sk in skills)
                    {
                        sk.SkillType = 1;
                        if (whsSet.Contains(sk.SkillHash)) sk.SkillPosition = 1;
                        else if (whoSet.Contains(sk.SkillHash)) sk.SkillPosition = 2;
                        else if (whdSet.Contains(sk.SkillHash)) sk.SkillPosition = 3;
                        else if (whkSet.Contains(sk.SkillHash)) sk.SkillPosition = 4;
                        else if (whpSet.Contains(sk.SkillHash)) { sk.SkillPosition = 15; sk.TPCost = 0; sk.Power = 0; }
                    }

                    // Inject dangerous retail traps: non-wh* Void/Totem entries + SkillType==2 Keshin entry
                    skills.Add(new GOSupport.SkillConfig { SkillHash = voidDummyRenard, SkillType = 1, SkillPosition = 1, TPCost = 25, Power = 80, Element = 1 });
                    skills.Add(new GOSupport.SkillConfig { SkillHash = voidTotemStub, SkillType = 0, SkillPosition = 2, TPCost = 20, Power = 60, Element = 2 });
                    skills.Add(new GOSupport.SkillConfig { SkillHash = keshinType2Stub, SkillType = 2, SkillPosition = 1, TPCost = 30, Power = 120, Element = 3 });
                    setup.SaveSkillConfigs(skills.ToArray());
                    setup.SaveCopyTo(retailSkillsFa);
                }

                string outDir = Path.Combine(tempDir, "out");
                var opts = new RandomizerOptions
                {
                    Seed = "RETAIL-SKILL-POS15-FIX",
                    Edition = GameEdition.Light
                };
                opts.Players.RandomizePositions = false; // Keep deterministic positions to verify exact slot-0 role mapping
                opts.Skills.RandomizeSkillParameters = true;
                opts.Skills.EnforcePositionCompatibility = true;

                var rep = new RandomizerCoordinator().GenerateRun(retailSkillsFa, outDir, opts);
                Assert.True(rep.OutputIntegrityVerified);

                using var modded = new SafeGOGame(Path.Combine(outDir, "Standalone", "ie_a.fa"), "es");
                var moddedSkills = modded.GetSkillConfigs(false).ToDictionary(s => s.SkillHash);
                var moddedTable = modded.GetSkillTable();

                // 1. Passive talents (SkillPosition == 15 / whp*) must NEVER get randomized Power > 0 or TPCost > 0
                foreach (int th in whpSet.Where(moddedSkills.ContainsKey))
                {
                    Assert.Equal(0, moddedSkills[th].TPCost);
                    Assert.Equal(0, moddedSkills[th].Power);
                }

                // 2. Non-wh* Void/Totem stubs & SkillType==2 Keshin moves must NEVER appear in SKILL_TABLE
                foreach (var st in moddedTable)
                {
                    Assert.NotEqual(voidDummyRenard, st.SkillHash);
                    Assert.NotEqual(voidTotemStub, st.SkillHash);
                    Assert.NotEqual(keshinType2Stub, st.SkillHash);
                }

                // 3. Slot #3 (originally whp0001/whp0002, SkillPosition == 15) must remain a whp* talent,
                //    and Slot #0 of non-protected players (cp0101..cp0120) must match retail role (GK->whk, FW->whs, MF->who, DF->whd)!
                var protectedCtx = new IEGORandomizer.Engine.Protection.StoryProtectionService()
                    .BuildContext(modded, GameEdition.Light, null);
                foreach (var cp in modded.GetCharaparams())
                {
                    if (cp.SpecialMoveCount < 4)
                        continue;

                    int talentSlotHash = moddedTable[cp.SpecialMoveOffset + 3].SkillHash;
                    Assert.True(whpSet.Contains(talentSlotHash),
                        $"Retail SkillPosition==15 talent slot was overwritten with non-talent 0x{talentSlotHash:X8}!");

                    if (protectedCtx.ProtectedStoryPlayerParamHashes.Contains(cp.ParamHash))
                        continue;

                    int slot0Hash = moddedTable[cp.SpecialMoveOffset].SkillHash;
                    switch (cp.PlayerPosition)
                    {
                        case 1: // Goalkeeper -> whk (Save)
                            Assert.True(whkSet.Contains(slot0Hash), $"GK received non-Save move 0x{slot0Hash:X8} in slot 0!");
                            break;
                        case 2: // Forward -> whs (Shoot)
                            Assert.True(whsSet.Contains(slot0Hash), $"FW received non-Shoot move 0x{slot0Hash:X8} in slot 0!");
                            break;
                        case 3: // Midfielder -> who (Dribble)
                            Assert.True(whoSet.Contains(slot0Hash), $"MF received non-Dribble move 0x{slot0Hash:X8} in slot 0!");
                            break;
                        case 4: // Defender -> whd (Block)
                            Assert.True(whdSet.Contains(slot0Hash), $"DF received non-Block move 0x{slot0Hash:X8} in slot 0!");
                            break;
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void FbiInPlaceUpdateCia_BumpsTmdTitleVersion_PreservesSaveDataSizeAndChainsConsecutiveUpdates()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_v112_fbi_cia_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string sourceCia = Path.Combine(tempDir, "Luz_Source_V000.cia");
                CiaPipelineTests.BuildSyntheticLuzV000MultiContentCia(sourceCia, out _);

                var svc = new CiaWorkflowService();

                // 1. Default FBI Update .cia from V.0.0.0 -> V.1.1.2 (raw 1042) + companion Luma3DS LayeredFS file
                string update1Cia = Path.Combine(tempDir, "Out1", "Luz_Update1.cia");
                var opts1 = new RandomizerOptions { Seed = "FBI-UPDATE-1" };
                opts1.Cia.BumpTitleVersionForFbiUpdate = true;
                opts1.Cia.ExportCompanionLuma3dsPatch = true;

                var rep1 = svc.RandomizeCiaToCia(sourceCia, update1Cia, opts1);
                Assert.Equal((ushort)0, rep1.CiaBuildTelemetry.SourceTitleVersionRaw);
                Assert.Equal("V.0.0.0", rep1.CiaBuildTelemetry.SourceTitleVersionFormatted);
                Assert.Equal(CiaConstants.DefaultFbiUpdateTitleVersionRaw, rep1.CiaBuildTelemetry.TitleVersionRaw);
                Assert.Equal("V.1.1.2", rep1.CiaBuildTelemetry.TitleVersionFormatted);
                Assert.True(rep1.CiaBuildTelemetry.IsFbiInPlaceUpdateReady);
                Assert.Equal("0004000000112F00", rep1.CiaBuildTelemetry.TitleIdHex);
                Assert.Equal(512u * 1024u, rep1.CiaBuildTelemetry.PreservedSaveDataSizeBytes);

                string expectedLumaPatch = Path.Combine(tempDir, "Out1", "Luma3DS", "titles", "0004000000112F00", "romfs", "ie_a.fa");
                Assert.True(File.Exists(expectedLumaPatch), "Expected companion Luma3DS LayeredFS ie_a.fa to be exported.");

                var inspect1 = svc.InspectLocalCia(update1Cia);
                Assert.Equal((ushort)1042, inspect1.TmdTitleVersionRaw);
                Assert.Equal("V.1.1.2", inspect1.TmdTitleVersionFormatted);
                Assert.True(inspect1.TmdInternalHashesValid);

                // 2. Chained second update (feeding V.1.1.2 .cia into RandomizeCiaToCia) -> automatically bumps to V.1.1.3 (1043)
                string update2Cia = Path.Combine(tempDir, "Out2", "Luz_Update2.cia");
                var rep2 = svc.RandomizeCiaToCia(update1Cia, update2Cia, new RandomizerOptions { Seed = "FBI-UPDATE-2" });
                Assert.Equal((ushort)1042, rep2.CiaBuildTelemetry.SourceTitleVersionRaw);
                Assert.Equal((ushort)1043, rep2.CiaBuildTelemetry.TitleVersionRaw);
                Assert.Equal("V.1.1.3", rep2.CiaBuildTelemetry.TitleVersionFormatted);
                Assert.True(rep2.CiaBuildTelemetry.IsFbiInPlaceUpdateReady);

                // 3. CLI --cia-version 2.0.1 explicit override
                string cliCustomCia = Path.Combine(tempDir, "Out3", "Luz_CustomVer.cia");
                int exitCode = CliRunner.Run(new[]
                {
                    "-i", sourceCia,
                    "--output-cia", cliCustomCia,
                    "--seed", "CLI-VER-201",
                    "--cia-version", "2.0.1"
                });
                Assert.Equal(0, exitCode);

                var inspect3 = svc.InspectLocalCia(cliCustomCia);
                Assert.Equal(CiaConstants.EncodeTitleVersion(2, 0, 1), inspect3.TmdTitleVersionRaw);
                Assert.Equal("V.2.0.1", inspect3.TmdTitleVersionFormatted);
                Assert.Equal(512u * 1024u, inspect3.SaveDataSizeBytes);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
