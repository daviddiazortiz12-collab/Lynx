using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lynx.Core.Games;
using Lynx.Core.RomFs;
using Lynx.Core.Scripts;
using Lynx.InazumaEleven.Games.GO;
using Lynx.InazumaEleven.Logic;
using StudioElevenLib.Level5.Archive.ARC0;
using StudioElevenLib.Level5.Binary;
using StudioElevenLib.Level5.Binary.Collections;
using StudioElevenLib.Level5.Binary.Logic;
using StudioElevenLib.Level5.Binary.Mapper;
using StudioElevenLib.Level5.Text;
using StudioElevenLib.Level5.Text.Logic;
using StudioElevenLib.Tools;
using static Lynx.InazumaEleven.Games.GO.GOSupport;

namespace IEGORandomizer.Tests
{
    /// <summary>
    /// Builds realistic, self-contained Inazuma Eleven GO 1 <c>ie_a.fa</c> ARC0 archives and
    /// decrypted Nintendo 3DS <c>.3ds</c> (NCSD + NCCH + IVFC RomFS) test fixtures using
    /// genuine Level-5 binary structures (<c>ARC0Writer</c>, <c>CfgBin</c>, <c>T2bþ</c>, <c>PTreeNode</c>,
    /// and Squirrel 2.2.3 <c>RIQS</c> <c>.nutb</c>). Never relies on external copyrighted ROMs.
    /// </summary>
    public static class SyntheticGameArchiveBuilder
    {
        public static void CreateRealisticIeAFa(string outputPath)
        {
            var root = new VirtualDirectory("");
            var arc = new ARC0 { Directory = root };

            byte[] emptyTextCfg = CreateT2bTextBinary(new Dictionary<int, string>
            {
                { Level5IdHelper.ComputeCrc32Utf8("nm_cp0008"), "Arion Sherwind" },
                { Level5IdHelper.ComputeCrc32Utf8("nm_cp0010"), "Victor Blade" }
            });
            foreach (string textName in new[]
            {
                "encount_area_text_es.cfg.bin",
                "system_text_es.cfg.bin",
                "chara_text_es.cfg.bin",
                "item_text_es.cfg.bin",
                "skill_text_es.cfg.bin",
                "kiznax_hint_text_es.cfg.bin",
                "team_text_es.cfg.bin",
                "troute_text_es.cfg.bin"
            })
            {
                Arc0SafeArchive.WriteFileAtPath(root, "/data/res/text/" + textName, emptyTextCfg);
            }
            Arc0SafeArchive.WriteFileAtPath(root, "/data/txt/43010000_es.cfg.bin", emptyTextCfg);
            Arc0SafeArchive.WriteFileAtPath(root, "/data/txt/43020000_es.cfg.bin", emptyTextCfg);

            // Seed properly-structured Level-5 CfgBin skeletons with *_BEGIN / *_END sections
            Arc0SafeArchive.WriteFileAtPath(root, "/data/res/character/chara_base.cfg.bin",
                CreateSkeletonCfgBin("CHARA_BASE_INFO_BEGIN"));
            Arc0SafeArchive.WriteFileAtPath(root, "/data/res/character/chara_param.cfg.bin",
                CreateSkeletonCfgBin("CHARA_PARAM_INFO_BEGIN", "TRAINING_UD_BEGIN"));
            Arc0SafeArchive.WriteFileAtPath(root, "/data/res/character/skill_table.cfg.bin",
                CreateSkeletonCfgBin("SKILL_TABLE_BEGIN"));
            Arc0SafeArchive.WriteFileAtPath(root, "/data/res/skill/skill_config.cfg.bin",
                CreateSkeletonCfgBin("SKILL_CONFIG_INFO_BEGIN"));
            Arc0SafeArchive.WriteFileAtPath(root, "/data/res/team/team_config.cfg.bin",
                CreateSkeletonCfgBin("STORY_TEAM_INFO_BEGIN", "ENCOUNT_TEAM_INFO_BEGIN"));
            Arc0SafeArchive.WriteFileAtPath(root, "/data/res/team/team_param.cfg.bin",
                CreateSkeletonCfgBin("TEAM_PARAM_INFO_BEGIN"));
            Arc0SafeArchive.WriteFileAtPath(root, "/data/res/soccer/soccer_config.cfg.bin",
                CreateSkeletonCfgBin("SOCCER_INFO_BEGIN"));
            Arc0SafeArchive.WriteFileAtPath(root, "/data/res/shop/community_config.cfg.bin",
                CreateSkeletonCfgBin("COMMUNITY_INFO_BEGIN"));

            using var game = new SafeGOGame(arc, protectedSourcePath: null, preferredLanguageCode: "es");

            // 1. Characters: Story Raimon IDs + 20 compatible playable scouts/rivals + 1 NPC mannequin (Body=0)
            var charaBases = new List<ICharabase>();
            var charaParams = new List<ICharaparam>();
            var skillTables = new List<ISkillTable>();

            int skillIdx = 0;

            void AddPlayer(int modelNum, int position, int element, int gender, int body, int skin, int avatarHash, bool createVisualAssets = true)
            {
                string cpCode = $"cp{modelNum:D4}";
                string paraCode = $"para_{cpCode}";
                int baseHash = Level5IdHelper.ComputeCrc32Utf8(cpCode);
                int paramHash = Level5IdHelper.ComputeCrc32Utf8(paraCode);

                charaBases.Add(new CharaBase
                {
                    BaseHash = baseHash,
                    ModelNumber = modelNum,
                    NameHash = Level5IdHelper.ComputeCrc32Utf8("nm_" + cpCode),
                    NicknameHash = Level5IdHelper.ComputeCrc32Utf8("nk_" + cpCode),
                    CharaBaseType = 1, // Player ("cp")
                    Gender = gender,
                    Body = body,
                    Skin = skin,
                    Year = (modelNum % 3) + 1,
                    DescriptionHash = 0
                });

                charaParams.Add(new CharaParam
                {
                    ParamHash = paramHash,
                    BaseHash = baseHash,
                    PlayerPosition = position,
                    Element = element,
                    FP = 120 + (modelNum % 30),
                    FPGrow = 2,
                    TP = 110 + (modelNum % 40),
                    TPGrow = 2,
                    Kick = 60 + (modelNum % 40),
                    KickGrow = 3,
                    Dribble = 58 + (modelNum % 40),
                    DribbleGrow = 3,
                    Technique = 62 + (modelNum % 35),
                    TechniqueGrow = 3,
                    Block = 55 + (modelNum % 40),
                    BlockGrow = 2,
                    Speed = 60 + (modelNum % 35),
                    SpeedGrow = 3,
                    Stamina = 59 + (modelNum % 30),
                    StaminaGrow = 2,
                    Catch = position == 1 ? 85 : 40 + (modelNum % 20),
                    CatchGrow = position == 1 ? 4 : 1,
                    Luck = 50 + (modelNum % 30),
                    LuckGrow = 2,
                    Freedom = 40 + (modelNum % 40),
                    FightingSpiritHash = avatarHash,
                    SpecialMoveOffset = skillIdx,
                    SpecialMoveCount = 4,
                    PlayerGroup = (modelNum % 3) + 1,
                    ExperienceGrow = modelNum % 3,
                    TrainingUD = 0,
                    FightingSpiritMatchkHash = avatarHash
                });

                string[] defaultSkills = position switch
                {
                    1 => new[] { "whk0001", "whk0002", "whd0001", "whp0001" },
                    2 => new[] { "who0001", "whs0001", "who0002", "whp0002" },
                    3 => new[] { "whs0001", "whs0002", "who0001", "whp0001" },
                    _ => new[] { "whd0001", "whd0002", "who0001", "whp0002" }
                };

                int[] levels = { 1, 12, 25, 40 };
                for (int s = 0; s < 4; s++)
                {
                    skillTables.Add(new SkillTable
                    {
                        SkillIndex = skillIdx++,
                        LevelLearned = levels[s],
                        SkillHash = Level5IdHelper.ComputeCrc32Utf8(defaultSkills[s]),
                        Level = 1
                    });
                }

                if (createVisualAssets)
                {
                    byte[] dummyAsset = Encoding.ASCII.GetBytes("ASSET_" + cpCode);
                    Arc0SafeArchive.WriteFileAtPath(root, $"/data/bustup/face/{cpCode}a.xi", dummyAsset);
                    Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/rpg/face/{cpCode}m.xc", dummyAsset);
                    Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/waza/face/{cpCode}a.xc", dummyAsset);
                }
            }

            int ck0001 = Level5IdHelper.ComputeCrc32Utf8("ck0001");
            int ck0002 = Level5IdHelper.ComputeCrc32Utf8("ck0002");
            int ck0003 = Level5IdHelper.ComputeCrc32Utf8("ck0003");
            int ck0004 = Level5IdHelper.ComputeCrc32Utf8("ck0004");

            AddPlayer(1, position: 1, element: 4, gender: 1, body: 4, skin: 1, avatarHash: 0);       // cp0001 Samguk (GK)
            AddPlayer(8, position: 2, element: 1, gender: 1, body: 4, skin: 1, avatarHash: ck0001);  // cp0008 Arion (MF)
            AddPlayer(9, position: 2, element: 2, gender: 1, body: 4, skin: 1, avatarHash: ck0002);  // cp0009 Riccardo (MF)
            AddPlayer(10, position: 3, element: 3, gender: 1, body: 4, skin: 1, avatarHash: ck0003); // cp0010 Victor (FW)
            AddPlayer(350, position: 3, element: 1, gender: 1, body: 4, skin: 1, avatarHash: ck0004);// cp0350 Bailong (Light)
            AddPlayer(370, position: 3, element: 2, gender: 1, body: 4, skin: 2, avatarHash: ck0002);// cp0370 Tezcat (Shadow)

            for (int i = 101; i <= 120; i++)
            {
                int pos = ((i - 101) % 4) + 1;
                int elem = ((i - 101) % 4) + 1;
                int gender = (i % 5 == 0) ? 2 : 1;
                int body = gender == 2 ? 7 : 4;
                int avatar = (i % 4 == 0) ? ck0004 : 0;
                AddPlayer(i, pos, elem, gender, body, skin: (i % 6) + 1, avatarHash: avatar);
            }

            // Non-player NPC mannequin (Body=0, missing waza model) -> quarantined by VisualAssetValidator
            int npcBaseHash = Level5IdHelper.ComputeCrc32Utf8("cn0900");
            int npcParamHash = Level5IdHelper.ComputeCrc32Utf8("para_cn0900");
            charaBases.Add(new CharaBase
            {
                BaseHash = npcBaseHash,
                ModelNumber = 900,
                CharaBaseType = 3,
                Gender = 1,
                Body = 0,
                Skin = 0,
                Year = 1
            });
            charaParams.Add(new CharaParam
            {
                ParamHash = npcParamHash,
                BaseHash = npcBaseHash,
                PlayerPosition = 3,
                Element = 1,
                TP = 80
            });

            game.SaveCharaBase(charaBases.ToArray());
            game.SaveCharaparams(charaParams.ToArray());
            game.SaveTrainingUD(new ITrainingUD[] { new TrainingUD() });
            game.SaveSkillTable(skillTables.ToArray());

            // 2. Skills & Avatars
            var skills = new List<ISkillConfig>
            {
                MakeSkill("whs0001", skillPos: 1, elem: 3, tp: 30, power: 90),  // Shoot
                MakeSkill("whs0002", skillPos: 1, elem: 1, tp: 45, power: 130), // Shoot
                MakeSkill("whs0003", skillPos: 1, elem: 2, tp: 55, power: 160), // Shoot
                MakeSkill("whs0004", skillPos: 1, elem: 4, tp: 25, power: 75),  // Shoot (low TP)
                MakeSkill("who0001", skillPos: 2, elem: 1, tp: 24, power: 80),  // Dribble
                MakeSkill("who0002", skillPos: 2, elem: 2, tp: 38, power: 120), // Dribble
                MakeSkill("who0003", skillPos: 2, elem: 4, tp: 48, power: 150), // Dribble
                MakeSkill("whd0001", skillPos: 3, elem: 4, tp: 25, power: 85),  // Block
                MakeSkill("whd0002", skillPos: 3, elem: 2, tp: 40, power: 125), // Block
                MakeSkill("whd0003", skillPos: 3, elem: 3, tp: 50, power: 155), // Block
                MakeSkill("whk0001", skillPos: 4, elem: 4, tp: 26, power: 90),  // Catch
                MakeSkill("whk0002", skillPos: 4, elem: 1, tp: 42, power: 135), // Catch
                MakeSkill("whk0003", skillPos: 4, elem: 3, tp: 54, power: 170), // Catch
                MakeSkill("whp0001", skillPos: 5, elem: 0, tp: 0,  power: 0),   // Talent
                MakeSkill("whp0002", skillPos: 5, elem: 0, tp: 0,  power: 0),   // Talent
                MakeSkill("whp0003", skillPos: 5, elem: 0, tp: 0,  power: 0),   // Talent
                MakeSkill("wks0001", skillPos: 1, elem: 1, tp: 60, power: 180), // Avatar Shoot
                MakeSkill("wko0001", skillPos: 2, elem: 2, tp: 50, power: 165), // Avatar Dribble
                MakeSkill("wkd0001", skillPos: 3, elem: 4, tp: 55, power: 170), // Avatar Block
                MakeSkill("wkk0001", skillPos: 4, elem: 3, tp: 60, power: 185)  // Avatar Catch
            };
            game.SaveSkillConfigs(skills.ToArray());

            var avatars = new List<IAvatar>
            {
                MakeAvatar("ck0001", num: 1, pos: 2, elem: 1, skillCode: "wko0001"),
                MakeAvatar("ck0002", num: 2, pos: 2, elem: 2, skillCode: "wks0001"),
                MakeAvatar("ck0003", num: 3, pos: 3, elem: 3, skillCode: "wks0001"),
                MakeAvatar("ck0004", num: 4, pos: 1, elem: 4, skillCode: "wkk0001")
            };
            foreach (string ck in new[] { "ck0001", "ck0002", "ck0003", "ck0004" })
            {
                Arc0SafeArchive.WriteFileAtPath(root, $"/data/bustup/avatar/{ck}a.xi", Encoding.ASCII.GetBytes("AV_" + ck));
            }

            // 3. Build complete item_config.cfg.bin with all IEGO1 sections + ITEM_REF
            var equipmentItems = new List<ItemConfig>
            {
                new ItemConfig { ItemID = Level5IdHelper.ComputeCrc32Utf8("iie0001"), ItemRef = 10, ItemCategory = 1 }, // Boots
                new ItemConfig { ItemID = Level5IdHelper.ComputeCrc32Utf8("iie0002"), ItemRef = 10, ItemCategory = 1 }, // Boots
                new ItemConfig { ItemID = Level5IdHelper.ComputeCrc32Utf8("iie0003"), ItemRef = 10, ItemCategory = 4 }, // Gloves
                new ItemConfig { ItemID = Level5IdHelper.ComputeCrc32Utf8("iie0004"), ItemRef = 10, ItemCategory = 3 }, // Pendant
                new ItemConfig { ItemID = Level5IdHelper.ComputeCrc32Utf8("iie0005"), ItemRef = 10, ItemCategory = 2 }  // Bracelet
            };

            var consumeItems = new List<ItemConfig>
            {
                new ItemConfig { ItemID = Level5IdHelper.ComputeCrc32Utf8("iic0001"), ItemRef = 50, ItemCategory = 10 },
                new ItemConfig { ItemID = Level5IdHelper.ComputeCrc32Utf8("iic0002"), ItemRef = 50, ItemCategory = 10 },
                new ItemConfig { ItemID = Level5IdHelper.ComputeCrc32Utf8("iif0001"), ItemRef = 50, ItemCategory = 17 }, // Formation 1
                new ItemConfig { ItemID = Level5IdHelper.ComputeCrc32Utf8("iif0002"), ItemRef = 50, ItemCategory = 17 }, // Formation 2
                new ItemConfig { ItemID = Level5IdHelper.ComputeCrc32Utf8("iit0001"), ItemRef = 50, ItemCategory = 18 }, // Tactic 1
                new ItemConfig { ItemID = Level5IdHelper.ComputeCrc32Utf8("iit0002"), ItemRef = 50, ItemCategory = 18 }  // Tactic 2
            };

            var importantItems = new List<ItemConfig>
            {
                // Key Item (e.g., Raimon Club Key) -> MUST ALWAYS BE PROTECTED!
                new ItemConfig { ItemID = Level5IdHelper.ComputeCrc32Utf8("iik0001"), ItemRef = 80, ItemCategory = 14 }
            };

            var uniformItems = new List<ItemConfigUniform>
            {
                new ItemConfigUniform { ItemID = Level5IdHelper.ComputeCrc32Utf8("iiu0001"), ItemNumber = 1, ItemRef = 85, ItemCategory = 16 },
                new ItemConfigUniform { ItemID = Level5IdHelper.ComputeCrc32Utf8("iiu0002"), ItemNumber = 2, ItemRef = 85, ItemCategory = 16 },
                new ItemConfigUniform { ItemID = Level5IdHelper.ComputeCrc32Utf8("iiu0003"), ItemNumber = 3, ItemRef = 85, ItemCategory = 16 },
                new ItemConfigUniform { ItemID = Level5IdHelper.ComputeCrc32Utf8("iiu0004"), ItemNumber = 4, ItemRef = 85, ItemCategory = 16 }
            };

            for (int u = 1; u <= 4; u++)
            {
                foreach (string prefix in new[] { "uza", "uzb", "uzc", "uzd" })
                {
                    Arc0SafeArchive.WriteFileAtPath(
                        root,
                        $"/data/chr/model/rpg/body/{prefix}{u:D4}.xc",
                        Encoding.ASCII.GetBytes($"KIT_{prefix}{u:D4}"));
                }
            }

            var palpackCards = new List<ItemConfigPalpackCard>();
            for (int p = 1; p <= 6; p++)
            {
                int targetModel = 100 + p;
                int prereqPlayerModel = 100 + ((p % 6) + 1);
                palpackCards.Add(new ItemConfigPalpackCard
                {
                    ItemID = Level5IdHelper.ComputeCrc32Utf8($"ika{p:D4}"),
                    ItemRef = 90,
                    ItemCategory = 19,
                    MaxQuantity = 1,
                    SellingPrice = 0,
                    PurchasePrice = 800 + (p * 100),
                    RecrutedCharacterId = Level5IdHelper.ComputeCrc32Utf8($"para_cp{targetModel:D4}"),
                    PrerequisitesType1 = 2,
                    PrerequisitesId1 = Level5IdHelper.ComputeCrc32Utf8($"para_cp{prereqPlayerModel:D4}"),
                    PrerequisitesType2 = 1,
                    PrerequisitesId2 = Level5IdHelper.ComputeCrc32Utf8("iic0001")
                });
            }

            var directorItems = new List<ItemConfigDirector>
            {
                new ItemConfigDirector { ItemID = Level5IdHelper.ComputeCrc32Utf8("iim0001"), ItemNumber = 1, ItemRef = 120, ItemCategory = 21 },
                new ItemConfigDirector { ItemID = Level5IdHelper.ComputeCrc32Utf8("iim0002"), ItemNumber = 2, ItemRef = 120, ItemCategory = 21 },
                new ItemConfigDirector { ItemID = Level5IdHelper.ComputeCrc32Utf8("iim0003"), ItemNumber = 3, ItemRef = 120, ItemCategory = 21 }
            };

            Arc0SafeArchive.WriteFileAtPath(
                root,
                "/data/res/item/item_config.cfg.bin",
                BuildItemConfigBinary(equipmentItems, consumeItems, importantItems, uniformItems, palpackCards, avatars, directorItems));

            // Calling SaveAvatars exercises RebuildItemReferences inside Game.cs!
            game.SaveAvatars(avatars.ToArray());

            // 4. Teams & Matches
            var storyTeams = new IStoryTeamInfo[]
            {
                MakeStoryTeam("ts0001", new[] { 1, 8, 9, 10, 101, 102, 103, 104, 105, 106, 107 }),
                MakeStoryTeam("ts0101", new[] { 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116 }),
                MakeStoryTeam("ts0102", new[] { 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120 })
            };

            var encountTeams = new IEncountTeamInfo[]
            {
                MakeEncountTeam("te0101", new[] { 101, 102, 103, 104, 105 }),
                MakeEncountTeam("te0102", new[] { 106, 107, 108, 109, 110 })
            };

            game.SaveTeamConfig(storyTeams, encountTeams);

            var teamParams = new ITeamParamInfo[]
            {
                MakeTeamParam("para_ts0001", "ts0001", kitNum: 1, level: 5, drop1: "iik0001", drop2: "iic0001"),
                MakeTeamParam("para_ts0101", "ts0101", kitNum: 2, level: 18, drop1: "iie0001", drop2: "iic0001"),
                MakeTeamParam("para_ts0102", "ts0102", kitNum: 3, level: 26, drop1: "iie0002", drop2: "iic0002"),
                MakeTeamParam("para_te0101", "te0101", kitNum: 2, level: 14, drop1: "iic0001", drop2: "iie0003"),
                MakeTeamParam("para_te0102", "te0102", kitNum: 4, level: 22, drop1: "iic0002", drop2: "iie0004")
            };
            game.SaveTeamParams(teamParams);

            var soccers = new ISoccerInfo[]
            {
                new SoccerInfo
                {
                    SoccerID = Level5IdHelper.ComputeCrc32Utf8("btl0001"),
                    TeamParamID = Level5IdHelper.ComputeCrc32Utf8("para_ts0001"),
                    SoccerMode = 1
                },
                new SoccerInfo
                {
                    SoccerID = Level5IdHelper.ComputeCrc32Utf8("btl0101"),
                    TeamParamID = Level5IdHelper.ComputeCrc32Utf8("para_ts0101"),
                    SoccerMode = 2
                },
                new SoccerInfo
                {
                    SoccerID = Level5IdHelper.ComputeCrc32Utf8("btl0102"),
                    TeamParamID = Level5IdHelper.ComputeCrc32Utf8("para_ts0102"),
                    SoccerMode = 2
                }
            };
            game.SaveSoccers(soccers);

            // 5. Competition Route (scr_br01.cfg.bin inside /data/res/soccer/)
            var routeCells = new IRouteConfig[]
            {
                new RouteConfig
                {
                    CellNum = 0,
                    CellType = 1,
                    ContentID = Level5IdHelper.ComputeCrc32Utf8("btl0101"),
                    CellLink1 = 1,
                    CellLink2 = -1,
                    CellLink3 = -1,
                    PhaseAppear = "",
                    Map = "",
                    MatchTextLock = ""
                },
                new RouteConfig
                {
                    CellNum = 1,
                    CellType = 3, // Normal Route Chest
                    ContentID = Level5IdHelper.ComputeCrc32Utf8("iie0001"),
                    CellLink1 = 2,
                    CellLink2 = -1,
                    CellLink3 = -1,
                    PhaseAppear = "",
                    Map = "",
                    MatchTextLock = ""
                },
                new RouteConfig
                {
                    CellNum = 2,
                    CellType = 4, // Locked Route Chest holding Key Item -> Protected!
                    ContentID = Level5IdHelper.ComputeCrc32Utf8("iik0001"),
                    CellLink1 = -1,
                    CellLink2 = -1,
                    CellLink3 = -1,
                    PhaseAppear = "",
                    Map = "",
                    MatchTextLock = "LOCK_KEY"
                }
            };
            Arc0SafeArchive.WriteFileAtPath(
                root,
                "/data/res/soccer/scr_br01.cfg.bin",
                BuildRouteBinary(Level5IdHelper.ComputeCrc32Utf8("route_01"), routeCells));

            // 6. Map Overworld Treasure Chests, Pachanga Spawns, and FUNCPOINT TBoxBitCheck
            Arc0SafeArchive.WriteFileAtPath(root, "/data/map/m001a/m001a_tbox.bin", BuildMapTBoxBinary(new[]
            {
                (Id: 1, LightItem: Level5IdHelper.ComputeCrc32Utf8("iic0001"), Flag: 10, Type: 0, ShadowItem: Level5IdHelper.ComputeCrc32Utf8("iic0002")),
                (Id: 2, LightItem: Level5IdHelper.ComputeCrc32Utf8("iie0001"), Flag: 11, Type: 1, ShadowItem: Level5IdHelper.ComputeCrc32Utf8("iie0002")),
                (Id: 3, LightItem: Level5IdHelper.ComputeCrc32Utf8("iie0003"), Flag: 999, Type: 1, ShadowItem: Level5IdHelper.ComputeCrc32Utf8("iie0003")),
                (Id: 4, LightItem: Level5IdHelper.ComputeCrc32Utf8("iik0001"), Flag: 12, Type: 2, ShadowItem: Level5IdHelper.ComputeCrc32Utf8("iik0001"))
            }));

            Arc0SafeArchive.WriteFileAtPath(root, "/data/map/m001a/m001a_enc.bin", BuildMapEncountBinary(new[]
            {
                (TeamCode: "te0101", Models: new[] { 101, 102, 103, 104, 105 }, Kit: 2000),
                (TeamCode: "te0102", Models: new[] { 106, 107, 108, 109, 110 }, Kit: 4000)
            }));

            Arc0SafeArchive.WriteFileAtPath(root, "/data/map/m001a/m001a_funcpt.bin", BuildFuncpointBinaryWithTBoxCheck(999));

            // 7. Compiled Squirrel 2.2.3 (.nutb) Story Event Script
            Arc0SafeArchive.WriteFileAtPath(
                root,
                "/data/script/event/ev1001.nutb",
                BuildSquirrel223Nutb(new[]
                {
                    "CMND_START_SOCCER",
                    "CMND_JOIN_PARTY",
                    "btl0001",
                    "cp0008",
                    "whs0001",
                    "ck0001",
                    "iik0001"
                }));

            game.SaveCopyTo(outputPath);
        }

        public static void WrapIeAFaInDecrypted3dsContainer(
            string ieaFaPath,
            string destination3dsPath,
            ulong titleId = 0x0004000000112F00UL,
            string productCode = "CTR-P-AE4P")
        {
            byte[] ieaBytes = File.ReadAllBytes(ieaFaPath);

            byte[] dirHash = BitConverter.GetBytes(0xFFFFFFFFu);
            byte[] dirMeta = new byte[0x18];
            BitConverter.GetBytes(0u).CopyTo(dirMeta, 0x00);
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(dirMeta, 0x04);
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(dirMeta, 0x08);
            BitConverter.GetBytes(0u).CopyTo(dirMeta, 0x0C);
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(dirMeta, 0x10);
            BitConverter.GetBytes(0u).CopyTo(dirMeta, 0x14);

            byte[] fileHash = BitConverter.GetBytes(0xFFFFFFFFu);
            byte[] fileNameUtf16 = Encoding.Unicode.GetBytes("ie_a.fa");
            int fileMetaLen = 0x20 + 16;
            byte[] fileMeta = new byte[fileMetaLen];
            BitConverter.GetBytes(0u).CopyTo(fileMeta, 0x00);
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(fileMeta, 0x04);
            BitConverter.GetBytes(0UL).CopyTo(fileMeta, 0x08);
            BitConverter.GetBytes((ulong)ieaBytes.Length).CopyTo(fileMeta, 0x10);
            BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(fileMeta, 0x18);
            BitConverter.GetBytes((uint)fileNameUtf16.Length).CopyTo(fileMeta, 0x1C);
            Array.Copy(fileNameUtf16, 0, fileMeta, 0x20, fileNameUtf16.Length);

            uint l3HeaderLen = 0x28;
            uint dirHashOff = 0x28;
            uint dirMetaOff = dirHashOff + (uint)dirHash.Length;
            uint fileHashOff = dirMetaOff + (uint)dirMeta.Length;
            uint fileMetaOff = fileHashOff + (uint)fileHash.Length;
            uint fileDataOff = (uint)AlignUp(fileMetaOff + fileMeta.Length, 0x10);

            long level3TotalLen = AlignUp(fileDataOff + ieaBytes.Length, 0x200);
            long romFsTotalLen = 0x1000 + level3TotalLen;
            long ncchOffset = 0x4000;
            long romFsRelOffset = 0x1000;
            long ncchTotalLen = romFsRelOffset + romFsTotalLen;
            long total3dsLen = ncchOffset + ncchTotalLen;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination3dsPath))!);
            using var fs = new FileStream(destination3dsPath, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.SetLength(total3dsLen);
            using var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);

            fs.Seek(0x100, SeekOrigin.Begin);
            bw.Write(Nintendo3dsRomReader.NcsdMagic);
            bw.Write((uint)(total3dsLen / 0x200));
            bw.Write(titleId);
            fs.Seek(0x120, SeekOrigin.Begin);
            bw.Write((uint)(ncchOffset / 0x200));
            bw.Write((uint)(ncchTotalLen / 0x200));

            fs.Seek(ncchOffset + 0x100, SeekOrigin.Begin);
            bw.Write(Nintendo3dsRomReader.NcchMagic);
            bw.Write((uint)(ncchTotalLen / 0x200));
            bw.Write(titleId);
            fs.Seek(ncchOffset + 0x118, SeekOrigin.Begin);
            bw.Write(titleId);
            fs.Seek(ncchOffset + 0x150, SeekOrigin.Begin);
            byte[] prodPadded = new byte[16];
            byte[] prodRaw = Encoding.ASCII.GetBytes(productCode);
            Array.Copy(prodRaw, prodPadded, Math.Min(16, prodRaw.Length));
            bw.Write(prodPadded);
            fs.Seek(ncchOffset + 0x18F, SeekOrigin.Begin);
            bw.Write((byte)0x04);
            fs.Seek(ncchOffset + 0x1B0, SeekOrigin.Begin);
            bw.Write((uint)(romFsRelOffset / 0x200));
            bw.Write((uint)(romFsTotalLen / 0x200));

            long romFsAbs = ncchOffset + romFsRelOffset;
            fs.Seek(romFsAbs, SeekOrigin.Begin);
            bw.Write(Nintendo3dsRomReader.IvfcMagic);
            bw.Write(0x00010000u);
            bw.Write(0x20u);

            long l3Abs = romFsAbs + 0x1000;
            fs.Seek(l3Abs, SeekOrigin.Begin);
            bw.Write(l3HeaderLen);
            bw.Write(dirHashOff);
            bw.Write((uint)dirHash.Length);
            bw.Write(dirMetaOff);
            bw.Write((uint)dirMeta.Length);
            bw.Write(fileHashOff);
            bw.Write((uint)fileHash.Length);
            bw.Write(fileMetaOff);
            bw.Write((uint)fileMeta.Length);
            bw.Write(fileDataOff);

            bw.Write(dirHash);
            bw.Write(dirMeta);
            bw.Write(fileHash);
            bw.Write(fileMeta);

            fs.Seek(l3Abs + fileDataOff, SeekOrigin.Begin);
            bw.Write(ieaBytes);
        }

        private static byte[] CreateSkeletonCfgBin(params string[] beginNames)
        {
            var cfg = new CfgBin<CfgTreeNode> { Encoding = Encoding.UTF8 };
            foreach (string beginName in beginNames)
            {
                string endName = beginName.Substring(0, beginName.Length - "_BEGIN".Length) + "_END";
                var beginNode = new CfgTreeNode(new Entry(beginName, new List<Variable>
                {
                    new Variable(CfgValueType.Int, 0)
                }));
                cfg.Entries.AddChild(beginNode);
                cfg.Entries.AddChild(new CfgTreeNode(new Entry(endName)));
            }
            return cfg.Save();
        }

        private static byte[] BuildItemConfigBinary(
            List<ItemConfig> equipment,
            List<ItemConfig> consume,
            List<ItemConfig> important,
            List<ItemConfigUniform> uniform,
            List<ItemConfigPalpackCard> kizunax,
            List<IAvatar> avatars,
            List<ItemConfigDirector> directors)
        {
            var cfg = new CfgBin<CfgTreeNode> { Encoding = Encoding.UTF8 };

            void AddSection<T>(string beginName, string childName, IReadOnlyList<T> list) where T : class
            {
                string endName = beginName.Substring(0, beginName.Length - "_BEGIN".Length) + "_END";
                var begin = new CfgTreeNode(new Entry(beginName, new List<Variable>
                {
                    new Variable(CfgValueType.Int, list.Count)
                }));
                foreach (var item in list)
                {
                    begin.AddChild(new CfgTreeNode(new Entry(childName, CfgBinMapper.GetVariablesFromInstance(item))));
                }
                cfg.Entries.AddChild(begin);
                cfg.Entries.AddChild(new CfgTreeNode(new Entry(endName)));
            }

            AddSection("ITEM_REF_BEGIN", "ITEM_REF", new List<ItemReference>());
            AddSection("ITEM_EQUIPMENT_BEGIN", "ITEM_EQUIPMENT", equipment);
            AddSection("ITEM_CONSUME_BEGIN", "ITEM_CONSUME", consume);
            AddSection("ITEM_IMPORTANT_BEGIN", "ITEM_IMPORTANT", important);
            AddSection("ITEM_UNIFORM_BEGIN", "ITEM_UNIFORM", uniform);
            AddSection("ITEM_KIZUNAX_BEGIN", "ITEM_KIZUNAX", kizunax);
            AddSection("ITEM_AVATAR_BEGIN", "ITEM_AVATAR", avatars);
            AddSection("AVATAR_INDEX_BEGIN", "AVATAR_INDEX", new List<AvatarTimeGrowth>());
            AddSection("ITEM_DIRECTOR_BEGIN", "ITEM_DIRECTOR", directors);

            return cfg.Save();
        }

        private static byte[] BuildRouteBinary(int nameCrc32, IRouteConfig[] routes)
        {
            var cfg = new CfgBin<CfgTreeNode> { Encoding = Encoding.UTF8 };
            var begin = new CfgTreeNode(new Entry("ROUTE_CONFIG_BEGIN", new List<Variable>
            {
                new Variable(CfgValueType.Int, routes.Length),
                new Variable(CfgValueType.Int, nameCrc32)
            }));
            foreach (var r in routes)
            {
                begin.AddChild(new CfgTreeNode(new Entry("ROUTE_CONFIG", CfgBinMapper.GetVariablesFromInstance(r))));
            }
            cfg.Entries.AddChild(begin);
            cfg.Entries.AddChild(new CfgTreeNode(new Entry("ROUTE_CONFIG_END")));
            return cfg.Save();
        }

        private static SkillConfig MakeSkill(string code, int skillPos, int elem, int tp, int power)
        {
            return new SkillConfig
            {
                SkillHash = Level5IdHelper.ComputeCrc32Utf8(code),
                SkillPosition = skillPos,
                Element = elem,
                TPCost = tp,
                Power = power,
                Number = 1,
                EvolutionGrow = 1,
                Fault = 5
            };
        }

        private static Avatar MakeAvatar(string code, int num, int pos, int elem, string skillCode)
        {
            return new Avatar
            {
                AvatarHash = Level5IdHelper.ComputeCrc32Utf8(code),
                AvatarNumber = num,
                Position = pos,
                Element = elem,
                SpecialMoveID = Level5IdHelper.ComputeCrc32Utf8(skillCode),
                FightingSpiritPoint = 120,
                Attack = 90,
                EvolutionGrow = 1
            };
        }

        private static StoryTeamInfo MakeStoryTeam(string teamCode, int[] playerModels)
        {
            var t = new StoryTeamInfo
            {
                TeamConfigID = Level5IdHelper.ComputeCrc32Utf8(teamCode),
                NameID = Level5IdHelper.ComputeCrc32Utf8("nm_" + teamCode)
            };

            for (int i = 0; i < Math.Min(16, playerModels.Length); i++)
            {
                int paramHash = Level5IdHelper.ComputeCrc32Utf8($"para_cp{playerModels[i]:D4}");
                typeof(StoryTeamInfo).GetProperty($"Player{i + 1}")!.SetValue(t, paramHash);
                typeof(StoryTeamInfo).GetProperty($"NumberPlayer{i + 1}")!.SetValue(t, i + 1);
            }
            return t;
        }

        private static EncountTeamInfo MakeEncountTeam(string teamCode, int[] playerModels)
        {
            var t = new EncountTeamInfo
            {
                TeamConfigID = Level5IdHelper.ComputeCrc32Utf8(teamCode),
                NameID = Level5IdHelper.ComputeCrc32Utf8("nm_" + teamCode)
            };

            for (int i = 0; i < Math.Min(5, playerModels.Length); i++)
            {
                int paramHash = Level5IdHelper.ComputeCrc32Utf8($"para_cp{playerModels[i]:D4}");
                typeof(EncountTeamInfo).GetProperty($"Player{i + 1}")!.SetValue(t, paramHash);
                typeof(EncountTeamInfo).GetProperty($"NumberPlayer{i + 1}")!.SetValue(t, i + 1);
            }
            return t;
        }

        private static TeamParamInfo MakeTeamParam(string paramCode, string configCode, int kitNum, int level, string drop1, string drop2)
        {
            return new TeamParamInfo
            {
                TeamParamID = Level5IdHelper.ComputeCrc32Utf8(paramCode),
                TeamConfigID = Level5IdHelper.ComputeCrc32Utf8(configCode),
                Uniform = kitNum * 1000,
                BootsID = Level5IdHelper.ComputeCrc32Utf8("iie0001"),
                GlovesID = Level5IdHelper.ComputeCrc32Utf8("iie0003"),
                BraceletID = Level5IdHelper.ComputeCrc32Utf8("iie0005"),
                PendantID = Level5IdHelper.ComputeCrc32Utf8("iie0004"),
                FormationID = Level5IdHelper.ComputeCrc32Utf8("iif0001"),
                CoachID = Level5IdHelper.ComputeCrc32Utf8("iim0001"),
                TacticID = Level5IdHelper.ComputeCrc32Utf8("iit0001"),
                Level = level,
                AILevel = 2,
                Friendship = level * 25,
                Prestige = level * 40,
                VictoryPoints = 10,
                DropID1 = Level5IdHelper.ComputeCrc32Utf8(drop1),
                DropRate1 = 35,
                DropID2 = Level5IdHelper.ComputeCrc32Utf8(drop2),
                DropRate2 = 25
            };
        }

        private static byte[] CreateT2bTextBinary(Dictionary<int, string> nouns)
        {
            // Minimal valid CfgBin acting as text container when strings aren't mutated
            var cfg = new CfgBin<CfgTreeNode> { Encoding = Encoding.UTF8 };
            var begin = new CfgTreeNode(new Entry("NOUN_INFO_BEGIN", new List<Variable>
            {
                new Variable(CfgValueType.Int, nouns.Count)
            }));
            foreach (var kvp in nouns)
            {
                begin.AddChild(new CfgTreeNode(new Entry("NOUN_INFO", new List<Variable>
                {
                    new Variable(CfgValueType.Int, kvp.Key),
                    new Variable(CfgValueType.Int, 0),
                    new Variable(CfgValueType.String, kvp.Value)
                })));
            }
            cfg.Entries.AddChild(begin);
            cfg.Entries.AddChild(new CfgTreeNode(new Entry("NOUN_INFO_END")));
            return cfg.Save();
        }

        private static byte[] BuildMapTBoxBinary(
            (int Id, int LightItem, int Flag, int Type, int ShadowItem)[] chests)
        {
            var cfg = new CfgBin<CfgTreeNode> { Encoding = Encoding.UTF8 };
            var rootNode = new CfgTreeNode(new Entry("TBOX_CONFIG_BEGIN", new List<Variable>
            {
                new Variable(CfgValueType.Int, chests.Length)
            }));
            foreach (var c in chests)
            {
                var vars = new List<Variable>
                {
                    new Variable(CfgValueType.Int, c.Id),
                    new Variable(CfgValueType.Float, 10.0f * c.Id),
                    new Variable(CfgValueType.Float, 0.0f),
                    new Variable(CfgValueType.Float, 20.0f * c.Id),
                    new Variable(CfgValueType.Float, 0.0f),
                    new Variable(CfgValueType.Int, c.LightItem),
                    new Variable(CfgValueType.Int, c.Flag),
                    new Variable(CfgValueType.Int, c.Type),
                    new Variable(CfgValueType.Int, c.ShadowItem)
                };
                rootNode.AddChild(new CfgTreeNode(new Entry("TBOX_CONFIG", vars)));
            }
            cfg.Entries.AddChild(rootNode);
            cfg.Entries.AddChild(new CfgTreeNode(new Entry("TBOX_CONFIG_END")));
            return cfg.Save();
        }

        private static byte[] BuildMapEncountBinary((string TeamCode, int[] Models, int Kit)[] spawns)
        {
            var cfg = new CfgBin<CfgTreeNode> { Encoding = Encoding.UTF8 };
            var rootNode = new CfgTreeNode(new Entry("ENCOUNT_INFO_BEGIN", new List<Variable>
            {
                new Variable(CfgValueType.Int, spawns.Length)
            }));
            foreach (var s in spawns)
            {
                var vars = new List<Variable>
                {
                    new Variable(CfgValueType.Int, Level5IdHelper.ComputeCrc32Utf8($"cp{s.Models[0]:D4}")),
                    new Variable(CfgValueType.Int, Level5IdHelper.ComputeCrc32Utf8(s.TeamCode)),
                    new Variable(CfgValueType.Int, 0),
                    new Variable(CfgValueType.Float, 5.0f),
                    new Variable(CfgValueType.Float, 0.0f),
                    new Variable(CfgValueType.Float, 5.0f),
                    new Variable(CfgValueType.Int, 0),
                    new Variable(CfgValueType.Int, 0),
                    new Variable(CfgValueType.Float, 0.0f),
                    new Variable(CfgValueType.Int, 0),
                    new Variable(CfgValueType.Int, 0),
                    new Variable(CfgValueType.Int, 0),
                    new Variable(CfgValueType.Int, Level5IdHelper.ComputeCrc32Utf8($"cp{s.Models[0]:D4}")),
                    new Variable(CfgValueType.Int, Level5IdHelper.ComputeCrc32Utf8($"cp{s.Models[1]:D4}")),
                    new Variable(CfgValueType.Int, Level5IdHelper.ComputeCrc32Utf8($"cp{s.Models[2]:D4}")),
                    new Variable(CfgValueType.Int, Level5IdHelper.ComputeCrc32Utf8($"cp{s.Models[3]:D4}")),
                    new Variable(CfgValueType.Int, Level5IdHelper.ComputeCrc32Utf8($"cp{s.Models[4]:D4}")),
                    new Variable(CfgValueType.Int, s.Kit),
                    new Variable(CfgValueType.Int, Level5IdHelper.ComputeCrc32Utf8("iie0001")),
                    new Variable(CfgValueType.Int, 0),
                    new Variable(CfgValueType.Int, 0),
                    new Variable(CfgValueType.Int, 0)
                };
                rootNode.AddChild(new CfgTreeNode(new Entry("ENCOUNT_INFO", vars)));
            }
            cfg.Entries.AddChild(rootNode);
            cfg.Entries.AddChild(new CfgTreeNode(new Entry("ENCOUNT_INFO_END")));
            return cfg.Save();
        }

        private static byte[] BuildFuncpointBinaryWithTBoxCheck(int protectedTBoxFlag)
        {
            var cfg = new CfgBin<PtreeNode> { Encoding = Encoding.UTF8 };
            var ptreeRoot = new PtreeNode(
                new Entry("PTREE", new List<Variable> { new Variable(CfgValueType.String, "FUNCPOINT") }),
                1);
            var eventDef = new PtreeNode(
                new Entry("PTREE", new List<Variable> { new Variable(CfgValueType.String, "EVENT_DEF") }),
                2);
            var tboxCheck = new PtreeNode(
                new Entry("PTVAL", new List<Variable>
                {
                    new Variable(CfgValueType.Int, protectedTBoxFlag),
                    new Variable(CfgValueType.String, "TBoxBitCheck")
                }),
                3);
            eventDef.AddChild(tboxCheck);
            ptreeRoot.AddChild(eventDef);
            cfg.Entries.AddChild(ptreeRoot);
            return cfg.Save();
        }

        private static byte[] BuildSquirrel223Nutb(string[] stringLiterals)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            bw.Write(NutbScriptAuditor.SqBytecodeStreamTag); // 0xFAFA
            bw.Write(NutbScriptAuditor.SqirMagicLe);         // "RIQS"
            bw.Write(1);
            bw.Write(4);
            bw.Write(4);
            bw.Write(0x54524150u); // 'PART'

            foreach (string s in stringLiterals)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(s);
                bw.Write(NutbScriptAuditor.OtStringTag);
                bw.Write(bytes.Length);
                bw.Write(bytes);
            }

            bw.Write(0x4C494154u); // 'TAIL'
            bw.Flush();
            return ms.ToArray();
        }

        private static long AlignUp(long val, long align) => (val + align - 1) & ~(align - 1);
    }
}
