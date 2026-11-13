using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using IEGORandomizer.Engine.Protection;
using IEGORandomizer.Engine.Validation;
using Lynx.Core.Binary;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.Core.RomFs;
using Lynx.Core.RomFs.Cia;
using Lynx.InazumaEleven.Logic;
using StudioElevenLib.Level5.Archive.ARC0;
using StudioElevenLib.Level5.Binary;
using StudioElevenLib.Level5.Binary.Collections;
using StudioElevenLib.Level5.Binary.Logic;
using StudioElevenLib.Level5.Compression;
using StudioElevenLib.Tools;
using static Lynx.InazumaEleven.Games.GO.GOSupport;

namespace IEGORandomizer.Engine.OnConsole
{
    public sealed class OnConsoleSeedRecipeResult
    {
        public string OutputRecipePath { get; init; } = string.Empty;
        public long CompressedSizeBytes { get; init; }
        public long UncompressedSizeBytes { get; init; }
        public int TotalArc0Entries { get; init; }
        public int ReusedInstalledSlotsCount { get; init; }
        public int EmbeddedFallbackBlobsCount { get; init; }
        public int MutableTemplatesCount { get; init; }
        public int VisualSwapMembersCount { get; init; }
        public int PlayablePlayersCount { get; init; }
        public int StoryTeamsCount { get; init; }
        public int EncountTeamsCount { get; init; }
        public int MapSpawnsCount { get; init; }
        public int PalpackCardsCount { get; init; }
        public int TreasureChestsCount { get; init; }
    }

    /// <summary>
    /// Compiles a compact, pre-validated <c>IE4RV100</c> (<c>seed_recipe.ie4r</c>) binary recipe from
    /// the user's installed <c>_Randomizado.cia</c> and clean retail <c>ie_a.fa</c> so the Nintendo 2DS
    /// ARM11 C99 engine (<c>ie4p_seed.c</c>) can generate unlimited manual or random "Todo Activado"
    /// seeds directly on the console in seconds without needing a PC.
    /// </summary>
    public static class OnConsoleSeedRecipeBuilder
    {
        public static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("IE4RV100");

        public const byte SrcInstalledSlot = 0;
        public const byte SrcEmbeddedBlob = 1;
        public const byte SrcMutableTemplate = 2;

        public const uint PlayerFlagSwappable = 1u << 0;
        public const uint PlayerFlagProtectedStory = 1u << 1;
        public const uint PlayerFlagProtectedAvatar = 1u << 2;
        public const uint PlayerFlagIsArion = 1u << 3;
        public const uint PlayerFlagIsJp = 1u << 4;

        private static readonly HashSet<int> ReservedManagerModelNumbers = new() { 36, 37, 38, 44 };

        private sealed class ArcSlotInfo
        {
            public int FileIndex { get; init; }
            public string VirtualPath { get; init; } = string.Empty;
            public uint NameCrc32 { get; init; }
            public uint NameOffsetInFolder { get; init; }
            public uint FileOffsetInData { get; init; }
            public uint FileSize { get; init; }
            public long AbsoluteOffset { get; init; }
        }

        private sealed class ParsedArcLayout
        {
            public ARC0Support.Header Header { get; init; }
            public byte[] DirEntriesComp { get; init; } = Array.Empty<byte>();
            public byte[] DirHashesComp { get; init; } = Array.Empty<byte>();
            public byte[] NameTableComp { get; init; } = Array.Empty<byte>();
            public int DecompressedNameTableLength { get; init; }
            public int DetectedAlignment { get; init; }
            public ArcSlotInfo[] Slots { get; init; } = Array.Empty<ArcSlotInfo>();
            public Dictionary<string, int> SlotByPath { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class MutableTemplateEntry
        {
            public int TemplateIndex { get; init; }
            public int ArcFileIndex { get; init; }
            public string VirtualPath { get; init; } = string.Empty;
            public byte[] CleanNormalizedBytes { get; init; } = Array.Empty<byte>();
        }

        public static OnConsoleSeedRecipeResult BuildRecipe(
            string installedInputPath,
            string cleanReferenceInputPath,
            string outputRecipePath,
            string aesKeysPath = null)
        {
            if (string.IsNullOrWhiteSpace(installedInputPath) || !File.Exists(installedInputPath))
                throw new FileNotFoundException("Installed input not found.", installedInputPath);
            if (string.IsNullOrWhiteSpace(cleanReferenceInputPath) || !File.Exists(cleanReferenceInputPath))
                throw new FileNotFoundException("Clean reference input not found.", cleanReferenceInputPath);
            if (string.IsNullOrWhiteSpace(outputRecipePath))
                throw new ArgumentException("Output recipe path cannot be empty.", nameof(outputRecipePath));

            using var installedWs = GameWorkspace.Open(installedInputPath, GameEdition.Auto, "es", computeSourceSha256: false, aesKeysPath: aesKeysPath);
            using var cleanWs = GameWorkspace.Open(cleanReferenceInputPath, GameEdition.Auto, "es", computeSourceSha256: false, aesKeysPath: aesKeysPath);
            {
                string installedIeA = installedWs.ActiveIeAFaPath;
                string cleanIeA = cleanWs.ActiveIeAFaPath;

                using var instFs = new FileStream(installedIeA, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                using var cleanFs = new FileStream(cleanIeA, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);

                ParsedArcLayout instArc = ParseArcLayout(instFs);
                ParsedArcLayout cleanArc = ParseArcLayout(cleanFs);

                if (instArc.Slots.Length != cleanArc.Slots.Length)
                {
                    throw new InvalidDataException(
                        $"ARC0 entry count mismatch: installed={instArc.Slots.Length}, clean={cleanArc.Slots.Length}.");
                }

                // Index every slot of installed_randomizado by (FileSize, SHA256Hex) AND exact same-slot compare
                var instSlotByDigest = new Dictionary<(uint Size, string ShaHex), int>(instArc.Slots.Length);
                var instSlotSha = new string[instArc.Slots.Length];
                for (int i = 0; i < instArc.Slots.Length; i++)
                {
                    var s = instArc.Slots[i];
                    byte[] bytes = ReadBytes(instFs, s.AbsoluteOffset, checked((int)s.FileSize));
                    string sha = Convert.ToHexString(SHA256.HashData(bytes));
                    instSlotSha[i] = sha;
                    instSlotByDigest.TryAdd((s.FileSize, sha), i);
                }

                SafeGOGame cleanGame = cleanWs.Game;
                var protectionSvc = new StoryProtectionService();
                StoryProtectionContext protection = protectionSvc.BuildContext(
                    cleanGame,
                    cleanWs.EffectiveEdition,
                    new Configuration.RandomizerOptions().Protection);
                VisualCoherenceProfile visualProfile = VisualCoherenceValidator.AnalyzeArchive(cleanGame);

                var templates = new List<MutableTemplateEntry>();
                var templateByPath = new Dictionary<string, MutableTemplateEntry>(StringComparer.OrdinalIgnoreCase);

                MutableTemplateEntry RegisterTemplate(string virtualPath, byte[] normalizedBytes)
                {
                    string normPath = NormalizeVirtPath(virtualPath);
                    if (!cleanArc.SlotByPath.TryGetValue(normPath, out int arcIdx))
                        throw new InvalidOperationException($"Clean ARC0 does not contain template '{normPath}'.");

                    var entry = new MutableTemplateEntry
                    {
                        TemplateIndex = templates.Count,
                        ArcFileIndex = arcIdx,
                        VirtualPath = normPath,
                        CleanNormalizedBytes = normalizedBytes
                    };
                    templates.Add(entry);
                    templateByPath[normPath] = entry;
                    return entry;
                }

                // ------------------------------------------------------------------
                // 1. CHARA_BASE + CHARACTER VISUAL SWAP GROUPS (partitioned by Body, Gender)
                // ------------------------------------------------------------------
                var cbCfg = OpenCfgFromClean(cleanGame.IE_A.Directory, "/data/res/character/chara_base.cfg.bin");
                var cbNodes = CollectChildNodes(cbCfg.Entries, "CHARA_BASE_INFO_BEGIN", "CHARA_BASE_INFO");
                var charaBases = Level5CfgBinAdapter.ReadEntries<CharaBase>(cbCfg.Entries, "CHARA_BASE_INFO", "chara_base.cfg.bin");

                var cbVarsToLocate = new List<Variable>();
                var cbVarIdxMap = new Dictionary<(int Row, int Col), int>();
                int[] cbMutableCols = { 2, 3, 7, 8, 9, 10, 11 }; // NameHash, NicknameHash, Body, Skin, Gender, Year, DescriptionHash
                for (int r = 0; r < cbNodes.Count; r++)
                {
                    var vars = cbNodes[r].Item.Variables;
                    foreach (int col in cbMutableCols)
                    {
                        if (col < vars.Count)
                        {
                            cbVarIdxMap[(r, col)] = cbVarsToLocate.Count;
                            cbVarsToLocate.Add(vars[col]);
                        }
                    }
                }
                int[] cbLocatedOffsets = LocateIntVariablesTwoPass(cbCfg, cbVarsToLocate, out byte[] cbCleanBytes);
                MutableTemplateEntry cbTpl = RegisterTemplate("/data/res/character/chara_base.cfg.bin", cbCleanBytes);

                var visualGroups = new List<List<VisualMemberSpec>>();
                var coherentBasesWithRow = charaBases
                    .Select((cb, rowIdx) => (Cb: cb, RowIdx: rowIdx))
                    .Where(x => visualProfile.CoherentPlayableBaseHashes.Contains(x.Cb.BaseHash) &&
                                x.Cb.ModelNumber > 0 &&
                                x.Cb.ModelNumber < VisualCoherenceValidator.MaxEngineModelNumberExclusive &&
                                x.Cb.Body > 0 &&
                                x.Cb.Gender > 0 &&
                                !ReservedManagerModelNumbers.Contains(x.Cb.ModelNumber))
                    .OrderBy(x => x.Cb.ModelNumber)
                    .ToList();

                int FindInstalledSlotForCleanAsset(int cleanArcSlotIdx)
                {
                    var cSlot = cleanArc.Slots[cleanArcSlotIdx];
                    byte[] cBytes = ReadBytes(cleanFs, cSlot.AbsoluteOffset, checked((int)cSlot.FileSize));
                    string cSha = Convert.ToHexString(SHA256.HashData(cBytes));
                    if (instArc.Slots[cleanArcSlotIdx].FileSize == cSlot.FileSize && instSlotSha[cleanArcSlotIdx] == cSha)
                        return cleanArcSlotIdx;
                    if (instSlotByDigest.TryGetValue((cSlot.FileSize, cSha), out int foundSlot))
                        return foundSlot;
                    return -1;
                }

                foreach (var rigGroup in coherentBasesWithRow
                             .GroupBy(x => (x.Cb.Body, x.Cb.Gender))
                             .OrderBy(g => g.Key.Body)
                             .ThenBy(g => g.Key.Gender))
                {
                    var groupMembers = new List<VisualMemberSpec>();
                    foreach (var (cb, rowIdx) in rigGroup)
                    {
                        string cpCode = $"cp{cb.ModelNumber:D4}";
                        string pBust = $"data/bustup/face/{cpCode}a.xi";
                        string pRpg = $"data/chr/model/rpg/face/{cpCode}m.xc";
                        string pWaza = $"data/chr/model/waza/face/{cpCode}a.xc";

                        if (!cleanArc.SlotByPath.TryGetValue(pBust, out int bustArcIdx) ||
                            !cleanArc.SlotByPath.TryGetValue(pRpg, out int rpgArcIdx) ||
                            !cleanArc.SlotByPath.TryGetValue(pWaza, out int wazaArcIdx))
                        {
                            continue;
                        }

                        int instBust = FindInstalledSlotForCleanAsset(bustArcIdx);
                        int instRpg = FindInstalledSlotForCleanAsset(rpgArcIdx);
                        int instWaza = FindInstalledSlotForCleanAsset(wazaArcIdx);
                        if (instBust < 0 || instRpg < 0 || instWaza < 0)
                            continue;

                        if (!cbVarIdxMap.ContainsKey((rowIdx, 2)) || !cbVarIdxMap.ContainsKey((rowIdx, 11)))
                            continue;

                        byte[] bBytes = ReadBytes(cleanFs, cleanArc.Slots[bustArcIdx].AbsoluteOffset, checked((int)cleanArc.Slots[bustArcIdx].FileSize));
                        byte[] rBytes = ReadBytes(cleanFs, cleanArc.Slots[rpgArcIdx].AbsoluteOffset, checked((int)cleanArc.Slots[rpgArcIdx].FileSize));
                        byte[] wBytes = ReadBytes(cleanFs, cleanArc.Slots[wazaArcIdx].AbsoluteOffset, checked((int)cleanArc.Slots[wazaArcIdx].FileSize));

                        groupMembers.Add(new VisualMemberSpec
                        {
                            BustupArcIdx = (uint)bustArcIdx,
                            RpgArcIdx = (uint)rpgArcIdx,
                            WazaArcIdx = (uint)wazaArcIdx,
                            DonorBustupInstSlot = (uint)instBust,
                            BustupSize = (uint)bBytes.Length,
                            BustupCrc = Arc0DeltaPatchPackBuilder.ComputeCrc32(bBytes),
                            DonorRpgInstSlot = (uint)instRpg,
                            RpgSize = (uint)rBytes.Length,
                            RpgCrc = Arc0DeltaPatchPackBuilder.ComputeCrc32(rBytes),
                            DonorWazaInstSlot = (uint)instWaza,
                            WazaSize = (uint)wBytes.Length,
                            WazaCrc = Arc0DeltaPatchPackBuilder.ComputeCrc32(wBytes),
                            NameHash = cb.NameHash,
                            NicknameHash = cb.NicknameHash,
                            DescHash = cb.DescriptionHash,
                            Body = cb.Body,
                            Skin = cb.Skin,
                            Gender = cb.Gender,
                            Year = cb.Year,
                            CbTemplateIdx = (uint)cbTpl.TemplateIndex,
                            OffName = (uint)cbLocatedOffsets[cbVarIdxMap[(rowIdx, 2)]],
                            OffNick = (uint)cbLocatedOffsets[cbVarIdxMap[(rowIdx, 3)]],
                            OffBody = (uint)cbLocatedOffsets[cbVarIdxMap[(rowIdx, 7)]],
                            OffSkin = (uint)cbLocatedOffsets[cbVarIdxMap[(rowIdx, 8)]],
                            OffGender = (uint)cbLocatedOffsets[cbVarIdxMap[(rowIdx, 9)]],
                            OffYear = (uint)cbLocatedOffsets[cbVarIdxMap[(rowIdx, 10)]],
                            OffDesc = (uint)cbLocatedOffsets[cbVarIdxMap[(rowIdx, 11)]]
                        });
                    }

                    if (groupMembers.Count >= 2)
                        visualGroups.Add(groupMembers);
                }

                // ------------------------------------------------------------------
                // 2. CHARA_PARAM (Playable Players: Stats, Position, Element, Avatar)
                // ------------------------------------------------------------------
                var baseByHash = charaBases
                    .Where(b => b.BaseHash != 0)
                    .GroupBy(b => b.BaseHash)
                    .ToDictionary(g => g.Key, g => g.First());

                var cpCfg = OpenCfgFromClean(cleanGame.IE_A.Directory, "/data/res/character/chara_param.cfg.bin");
                var cpNodes = CollectChildNodes(cpCfg.Entries, "CHARA_PARAM_INFO_BEGIN", "CHARA_PARAM_INFO");
                var charaParams = Level5CfgBinAdapter.ReadEntries<CharaParam>(cpCfg.Entries, "CHARA_PARAM_INFO", "chara_param.cfg.bin");

                // Columns in CharaParam:
                // 2=FP, 3=TP, 4=Kick, 5=Dribble, 6=Technique, 7=Block, 8=Speed, 9=Stamina, 10=Catch, 11=Luck,
                // 12=Element, 13=PlayerPosition, 15=Freedom, 16=FightingSpiritHash
                int[] cpMutableCols = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16 };
                var cpVars = new List<Variable>();
                var cpVarMap = new Dictionary<(int Row, int Col), int>();
                for (int r = 0; r < cpNodes.Count; r++)
                {
                    var vars = cpNodes[r].Item.Variables;
                    if (vars.Count <= 18)
                        continue;
                    if (!visualProfile.CoherentPlayableParamHashes.Contains(charaParams[r].ParamHash))
                        continue;
                    foreach (int col in cpMutableCols)
                    {
                        cpVarMap[(r, col)] = cpVars.Count;
                        cpVars.Add(vars[col]);
                    }
                }
                int[] cpOffsets = LocateIntVariablesTwoPass(cpCfg, cpVars, out byte[] cpCleanBytes);
                MutableTemplateEntry cpTpl = RegisterTemplate("/data/res/character/chara_param.cfg.bin", cpCleanBytes);

                int arionHash = Level5IdHelper.ComputeCrc32Utf8("para_cp0008");
                int jpHash = Level5IdHelper.ComputeCrc32Utf8("para_cp0005");

                var playerSpecs = new List<PlayerSpec>();
                for (int r = 0; r < charaParams.Count; r++)
                {
                    var cp = charaParams[r];
                    if (cp.ParamHash == 0 || !visualProfile.CoherentPlayableParamHashes.Contains(cp.ParamHash))
                        continue;
                    if (!cpVarMap.ContainsKey((r, 2)))
                        continue;

                    baseByHash.TryGetValue(cp.BaseHash, out var cbRef);
                    bool isProtStory = protection.ProtectedStoryPlayerParamHashes.Contains(cp.ParamHash);
                    bool isProtAvatar = protection.ProtectedAvatarBearerParamHashes.Contains(cp.ParamHash);

                    uint flags = 0;
                    if (!isProtStory) flags |= PlayerFlagSwappable;
                    if (isProtStory) flags |= PlayerFlagProtectedStory;
                    if (isProtAvatar) flags |= PlayerFlagProtectedAvatar;
                    if (cp.ParamHash == arionHash) flags |= PlayerFlagIsArion;
                    if (cp.ParamHash == jpHash) flags |= PlayerFlagIsJp;

                    var offs = new uint[14];
                    for (int c = 0; c < cpMutableCols.Length; c++)
                        offs[c] = (uint)cpOffsets[cpVarMap[(r, cpMutableCols[c])]];

                    playerSpecs.Add(new PlayerSpec
                    {
                        ParamHash = cp.ParamHash,
                        BaseHash = cp.BaseHash,
                        Body = cbRef?.Body ?? 0,
                        Gender = cbRef?.Gender ?? 0,
                        OrigPosition = cp.PlayerPosition,
                        Flags = flags,
                        MoveOffset = cp.SpecialMoveOffset,
                        MoveCount = cp.SpecialMoveCount,
                        CleanFp = cp.FP,
                        CleanTp = cp.TP,
                        CleanKick = cp.Kick,
                        CleanDribble = cp.Dribble,
                        CleanTechnique = cp.Technique,
                        CleanBlock = cp.Block,
                        CleanSpeed = cp.Speed,
                        CleanStamina = cp.Stamina,
                        CleanCatch = cp.Catch,
                        CleanLuck = cp.Luck,
                        CleanFreedom = cp.Freedom,
                        CleanAvatarHash = cp.FightingSpiritHash,
                        CpTemplateIdx = (uint)cpTpl.TemplateIndex,
                        FieldOffsets = offs
                    });
                }

                // ------------------------------------------------------------------
                // 3. TEAM_CONFIG (Story 11v11 Teams + Encounter 5v5 Teams + Shirt Numbers + Emblems)
                // ------------------------------------------------------------------
                var tcCfg = OpenCfgFromClean(cleanGame.IE_A.Directory, "/data/res/team/team_config.cfg.bin");
                var stNodes = CollectChildNodes(tcCfg.Entries, "STORY_TEAM_INFO_BEGIN", "STORY_TEAM_INFO");
                var etNodes = CollectChildNodes(tcCfg.Entries, "ENCOUNT_TEAM_INFO_BEGIN", "ENCOUNT_TEAM_INFO");
                var storyTeams = Level5CfgBinAdapter.ReadEntries<StoryTeamInfo>(tcCfg.Entries, "STORY_TEAM_INFO", "team_config.cfg.bin");
                var encountTeams = Level5CfgBinAdapter.ReadEntries<EncountTeamInfo>(tcCfg.Entries, "ENCOUNT_TEAM_INFO", "team_config.cfg.bin");

                var tcVars = new List<Variable>();
                var stSlotVarMap = new Dictionary<(int Row, int Slot), int>();
                var stShirtVarMap = new Dictionary<(int Row, int Slot), int>();
                var stEmblemVarMap = new Dictionary<int, int>();
                for (int r = 0; r < stNodes.Count && r < storyTeams.Count; r++)
                {
                    var vars = stNodes[r].Item.Variables;
                    if (vars.Count < 35) continue;
                    for (int s = 0; s < 16; s++)
                    {
                        stSlotVarMap[(r, s)] = tcVars.Count;
                        tcVars.Add(vars[2 + s * 2]);
                        stShirtVarMap[(r, s)] = tcVars.Count;
                        tcVars.Add(vars[3 + s * 2]);
                    }
                    stEmblemVarMap[r] = tcVars.Count;
                    tcVars.Add(vars[34]);
                }

                var etSlotVarMap = new Dictionary<(int Row, int Slot), int>();
                var etShirtVarMap = new Dictionary<(int Row, int Slot), int>();
                var etEmblemVarMap = new Dictionary<int, int>();
                for (int r = 0; r < etNodes.Count && r < encountTeams.Count; r++)
                {
                    var vars = etNodes[r].Item.Variables;
                    if (vars.Count < 13) continue;
                    for (int s = 0; s < 5; s++)
                    {
                        etSlotVarMap[(r, s)] = tcVars.Count;
                        tcVars.Add(vars[2 + s * 2]);
                        etShirtVarMap[(r, s)] = tcVars.Count;
                        tcVars.Add(vars[3 + s * 2]);
                    }
                    etEmblemVarMap[r] = tcVars.Count;
                    tcVars.Add(vars[12]);
                }

                int[] tcOffsets = LocateIntVariablesTwoPass(tcCfg, tcVars, out byte[] tcCleanBytes);
                MutableTemplateEntry tcTpl = RegisterTemplate("/data/res/team/team_config.cfg.bin", tcCleanBytes);

                var storyTeamSpecs = new List<StoryTeamSpec>();
                for (int r = 0; r < storyTeams.Count; r++)
                {
                    if (!stSlotVarMap.ContainsKey((r, 0))) continue;
                    var st = storyTeams[r];
                    int[] cleanPlayers =
                    {
                        st.Player1, st.Player2, st.Player3, st.Player4, st.Player5, st.Player6, st.Player7, st.Player8,
                        st.Player9, st.Player10, st.Player11, st.Player12, st.Player13, st.Player14, st.Player15, st.Player16
                    };
                    var pOffs = new uint[16];
                    var numOffs = new uint[16];
                    for (int s = 0; s < 16; s++)
                    {
                        pOffs[s] = (uint)tcOffsets[stSlotVarMap[(r, s)]];
                        numOffs[s] = (uint)tcOffsets[stShirtVarMap[(r, s)]];
                    }

                    uint flags = 0;
                    if (protection.ScriptedTeamConfigHashes.Contains(st.TeamConfigID)) flags |= 1u;
                    if (st.Player6 != 0 && st.Player11 != 0) flags |= 2u; // Full 11v11
                    if (protection.RaimonCoreTeamConfigHashes.Contains(st.TeamConfigID)) flags |= 4u;

                    storyTeamSpecs.Add(new StoryTeamSpec
                    {
                        TeamConfigId = st.TeamConfigID,
                        Flags = flags,
                        CleanEmblem1 = st.Emblem1,
                        EmblemOffset = (uint)tcOffsets[stEmblemVarMap[r]],
                        CleanPlayers = cleanPlayers,
                        PlayerOffsets = pOffs,
                        ShirtOffsets = numOffs
                    });
                }

                var teamParams = cleanGame.GetTeamParams();
                var soccers = cleanGame.GetSoccers() ?? Array.Empty<ISoccerInfo>();

                var encountTeamSpecs = new List<EncountTeamSpec>();
                var encountIdxByKey = new Dictionary<int, int>();
                for (int r = 0; r < encountTeams.Count; r++)
                {
                    if (!etSlotVarMap.ContainsKey((r, 0))) continue;
                    var et = encountTeams[r];
                    int[] cleanPlayers = { et.Player1, et.Player2, et.Player3, et.Player4, et.Player5 };
                    var pOffs = new uint[5];
                    var numOffs = new uint[5];
                    for (int s = 0; s < 5; s++)
                    {
                        pOffs[s] = (uint)tcOffsets[etSlotVarMap[(r, s)]];
                        numOffs[s] = (uint)tcOffsets[etShirtVarMap[(r, s)]];
                    }

                    int idx = encountTeamSpecs.Count;
                    if (et.TeamConfigID != 0)
                        encountIdxByKey[et.TeamConfigID] = idx;

                    encountTeamSpecs.Add(new EncountTeamSpec
                    {
                        TeamConfigId = et.TeamConfigID,
                        Flags = protection.ScriptedTeamConfigHashes.Contains(et.TeamConfigID) ? 1u : 0u,
                        CleanEmblem1 = et.Emblem1,
                        EmblemOffset = (uint)tcOffsets[etEmblemVarMap[r]],
                        CleanPlayers = cleanPlayers,
                        PlayerOffsets = pOffs,
                        ShirtOffsets = numOffs
                    });
                }

                foreach (var tp in teamParams)
                {
                    if (tp.TeamParamID != 0 && tp.TeamConfigID != 0 &&
                        encountIdxByKey.TryGetValue(tp.TeamConfigID, out int eIdx))
                    {
                        encountIdxByKey.TryAdd(tp.TeamParamID, eIdx);
                    }
                }
                foreach (var sc in soccers)
                {
                    if (sc != null && sc.SoccerID != 0 && sc.TeamParamID != 0 &&
                        encountIdxByKey.TryGetValue(sc.TeamParamID, out int eIdx))
                    {
                        encountIdxByKey.TryAdd(sc.SoccerID, eIdx);
                    }
                }

                // ------------------------------------------------------------------
                // 4. ITEM_CONFIG (PalPack kizunax cards + Valid Avatar/Item Pools)
                // ------------------------------------------------------------------
                var itemCfg = OpenCfgFromClean(cleanGame.IE_A.Directory, "/data/res/item/item_config.cfg.bin");
                var kizNodes = CollectChildNodes(itemCfg.Entries, "ITEM_KIZUNAX_BEGIN", "ITEM_KIZUNAX");
                var palpackCards = Level5CfgBinAdapter.ReadEntries<ItemConfigPalpackCard>(itemCfg.Entries, "ITEM_KIZUNAX", "item_config.cfg.bin");

                var itemVars = new List<Variable>();
                var kizRecruitVarMap = new Dictionary<int, int>();
                var kizPrereqVarMap = new Dictionary<(int Row, int Slot), int>();
                for (int r = 0; r < kizNodes.Count && r < palpackCards.Count; r++)
                {
                    var vars = kizNodes[r].Item.Variables;
                    if (vars.Count <= 21) continue;
                    kizRecruitVarMap[r] = itemVars.Count;
                    itemVars.Add(vars[21]); // RecrutedCharacterId
                    for (int p = 0; p < 4; p++)
                    {
                        kizPrereqVarMap[(r, p)] = itemVars.Count;
                        itemVars.Add(vars[11 + p * 2]); // PrerequisitesId1..4
                    }
                }
                int[] itemOffsets = LocateIntVariablesTwoPass(itemCfg, itemVars, out byte[] itemCleanBytes);
                MutableTemplateEntry itemTpl = RegisterTemplate("/data/res/item/item_config.cfg.bin", itemCleanBytes);

                var palpackSpecs = new List<PalpackSpec>();
                for (int r = 0; r < palpackCards.Count; r++)
                {
                    if (!kizRecruitVarMap.ContainsKey(r)) continue;
                    var card = palpackCards[r];
                    bool eligible = card.RecrutedCharacterId != 0 &&
                                    !protection.ProtectedStoryPlayerParamHashes.Contains(card.RecrutedCharacterId) &&
                                    visualProfile.CoherentPlayableParamHashes.Contains(card.RecrutedCharacterId);

                    palpackSpecs.Add(new PalpackSpec
                    {
                        Eligible = eligible ? 1u : 0u,
                        CleanRecruitedHash = card.RecrutedCharacterId,
                        RecruitedOffset = (uint)itemOffsets[kizRecruitVarMap[r]],
                        PrereqTypes = new[] { card.PrerequisitesType1, card.PrerequisitesType2, card.PrerequisitesType3, card.PrerequisitesType4 },
                        CleanPrereqIds = new[] { card.PrerequisitesId1, card.PrerequisitesId2, card.PrerequisitesId3, card.PrerequisitesId4 },
                        PrereqIdOffsets = new[]
                        {
                            (uint)itemOffsets[kizPrereqVarMap[(r, 0)]],
                            (uint)itemOffsets[kizPrereqVarMap[(r, 1)]],
                            (uint)itemOffsets[kizPrereqVarMap[(r, 2)]],
                            (uint)itemOffsets[kizPrereqVarMap[(r, 3)]]
                        }
                    });
                }

                // ------------------------------------------------------------------
                // 5. SKILL_CONFIG + SKILL_TABLE
                // ------------------------------------------------------------------
                var skCfg = OpenCfgFromClean(cleanGame.IE_A.Directory, "/data/res/skill/skill_config.cfg.bin");
                var skNodes = CollectChildNodes(skCfg.Entries, "SKILL_CONFIG_INFO_BEGIN", "SKILL_CONFIG_INFO");
                var skills = Level5CfgBinAdapter.ReadEntries<SkillConfig>(skCfg.Entries, "SKILL_CONFIG_INFO", "skill_config.cfg.bin");

                var stblCfg = OpenCfgFromClean(cleanGame.IE_A.Directory, "/data/res/character/skill_table.cfg.bin");
                var stblNodes = CollectChildNodes(stblCfg.Entries, "SKILL_TABLE_BEGIN", "SKILL_TABLE");
                var skillTable = Level5CfgBinAdapter.ReadEntries<SkillTable>(stblCfg.Entries, "SKILL_TABLE", "skill_table.cfg.bin");

                var whsSet = BuildRangeHashSet("whs", 1, 8999);
                var whoSet = BuildRangeHashSet("who", 1, 8999);
                var whdSet = BuildRangeHashSet("whd", 1, 8999);
                var whkSet = BuildRangeHashSet("whk", 1, 8999);
                var whpSet = BuildRangeHashSet("whp", 1, 8999);
                var wksSet = BuildRangeHashSet("wks", 1, 8999);
                var wkoSet = BuildRangeHashSet("wko", 1, 8999);
                var wkdSet = BuildRangeHashSet("wkd", 1, 8999);
                var wkkSet = BuildRangeHashSet("wkk", 1, 8999);
                var wkpSet = BuildRangeHashSet("wkp", 1, 8999);

                var avatars = cleanGame.GetAvatars(false) ?? Array.Empty<IAvatar>();
                var avatarMoveHashes = new HashSet<int>(wksSet.Concat(wkoSet).Concat(wkdSet).Concat(wkkSet).Concat(wkpSet));
                foreach (var av in avatars)
                {
                    if (av.SpecialMoveID != 0) avatarMoveHashes.Add(av.SpecialMoveID);
                    if (av.SkillID != 0) avatarMoveHashes.Add(av.SkillID);
                }

                var vanillaSkillTableHashes = new HashSet<int>(
                    skillTable.Where(x => x.SkillHash != 0).Select(x => x.SkillHash));

                var skVars = new List<Variable>();
                var skVarMap = new Dictionary<(int Row, int Col), int>(); // 3=Element, 7=TPCost, 16=Power
                for (int r = 0; r < skNodes.Count && r < skills.Count; r++)
                {
                    var sk = skills[r];
                    var vars = skNodes[r].Item.Variables;
                    if (vars.Count <= 16 || sk.SkillHash == 0) continue;

                    bool isActive = whsSet.Contains(sk.SkillHash) || whoSet.Contains(sk.SkillHash) ||
                                    whdSet.Contains(sk.SkillHash) || whkSet.Contains(sk.SkillHash) ||
                                    wksSet.Contains(sk.SkillHash) || wkoSet.Contains(sk.SkillHash) ||
                                    wkdSet.Contains(sk.SkillHash) || wkkSet.Contains(sk.SkillHash);
                    if (!isActive || sk.TPCost <= 0 || sk.Power <= 0) continue;

                    skVarMap[(r, 3)] = skVars.Count; skVars.Add(vars[3]);
                    skVarMap[(r, 7)] = skVars.Count; skVars.Add(vars[7]);
                    skVarMap[(r, 16)] = skVars.Count; skVars.Add(vars[16]);
                }
                int[] skOffsets = LocateIntVariablesTwoPass(skCfg, skVars, out byte[] skCleanBytes);
                MutableTemplateEntry skTpl = RegisterTemplate("/data/res/skill/skill_config.cfg.bin", skCleanBytes);

                var skillParamSpecs = new List<SkillParamSpec>();
                var shotPool = new List<int>();
                var soloStarterShotPool = new List<int>();
                var dribblePool = new List<int>();
                var blockPool = new List<int>();
                var savePool = new List<int>();
                var talentPool = new List<int>();

                for (int r = 0; r < skills.Count; r++)
                {
                    var sk = skills[r];
                    if (sk.SkillHash == 0) continue;

                    if (skVarMap.ContainsKey((r, 3)))
                    {
                        skillParamSpecs.Add(new SkillParamSpec
                        {
                            CleanElement = sk.Element,
                            CleanTp = sk.TPCost,
                            CleanPower = sk.Power,
                            ElemOffset = (uint)skOffsets[skVarMap[(r, 3)]],
                            TpOffset = (uint)skOffsets[skVarMap[(r, 7)]],
                            PowerOffset = (uint)skOffsets[skVarMap[(r, 16)]]
                        });
                    }

                    bool inVanillaTable = skillTable.Count < 100 || vanillaSkillTableHashes.Contains(sk.SkillHash);
                    if (!inVanillaTable || avatarMoveHashes.Contains(sk.SkillHash) || sk.SkillType == 2)
                        continue;

                    if (whpSet.Contains(sk.SkillHash) && sk.TPCost == 0)
                    {
                        talentPool.Add(sk.SkillHash);
                    }
                    else if (sk.TPCost > 0)
                    {
                        if (whsSet.Contains(sk.SkillHash))
                        {
                            shotPool.Add(sk.SkillHash);
                            if (sk.PartnerNumber <= 1 && sk.Power >= 35 && sk.TPCost <= 45)
                                soloStarterShotPool.Add(sk.SkillHash);
                        }
                        else if (whoSet.Contains(sk.SkillHash)) dribblePool.Add(sk.SkillHash);
                        else if (whdSet.Contains(sk.SkillHash)) blockPool.Add(sk.SkillHash);
                        else if (whkSet.Contains(sk.SkillHash)) savePool.Add(sk.SkillHash);
                    }
                }
                if (soloStarterShotPool.Count == 0) soloStarterShotPool.AddRange(shotPool);

                var stblVars = new List<Variable>();
                var stblLevelVarMap = new Dictionary<int, int>();
                var stblHashVarMap = new Dictionary<int, int>();
                for (int r = 0; r < stblNodes.Count && r < skillTable.Count; r++)
                {
                    var vars = stblNodes[r].Item.Variables;
                    if (vars.Count < 3) continue;
                    stblLevelVarMap[r] = stblVars.Count; stblVars.Add(vars[1]);
                    stblHashVarMap[r] = stblVars.Count; stblVars.Add(vars[2]);
                }
                int[] stblOffsets = LocateIntVariablesTwoPass(stblCfg, stblVars, out byte[] stblCleanBytes);
                MutableTemplateEntry stblTpl = RegisterTemplate("/data/res/character/skill_table.cfg.bin", stblCleanBytes);

                // Mark story-scripted skill slots as frozen so 0 .nutb edits are ever needed on 2DS!
                var frozenSkillSlots = new HashSet<int>();
                foreach (var cp in charaParams)
                {
                    if (!protection.ProtectedStoryPlayerParamHashes.Contains(cp.ParamHash))
                        continue;
                    if (cp.SpecialMoveOffset < 0 || cp.SpecialMoveOffset + cp.SpecialMoveCount > skillTable.Count)
                        continue;
                    for (int s = 0; s < cp.SpecialMoveCount; s++)
                    {
                        int idx = cp.SpecialMoveOffset + s;
                        if (protection.ScriptReferencedSkillHashes.Contains(skillTable[idx].SkillHash))
                            frozenSkillSlots.Add(idx);
                    }
                }

                var skillSlotSpecs = new List<SkillSlotSpec>(skillTable.Count);
                for (int r = 0; r < skillTable.Count; r++)
                {
                    var stEntry = skillTable[r];
                    uint flags = 0;
                    if (frozenSkillSlots.Contains(r) || !stblHashVarMap.ContainsKey(r)) flags |= 1u; // Frozen
                    if (whpSet.Contains(stEntry.SkillHash)) flags |= 2u; // WasTalent

                    skillSlotSpecs.Add(new SkillSlotSpec
                    {
                        CleanSkillHash = stEntry.SkillHash,
                        CleanLevel = stEntry.LevelLearned,
                        Flags = flags,
                        LevelOffset = stblLevelVarMap.ContainsKey(r) ? (uint)stblOffsets[stblLevelVarMap[r]] : 0xFFFFFFFFu,
                        HashOffset = stblHashVarMap.ContainsKey(r) ? (uint)stblOffsets[stblHashVarMap[r]] : 0xFFFFFFFFu
                    });
                }

                // ------------------------------------------------------------------
                // 6. TEAM_PARAM (Formations, Coaches, Tactics, Uniforms, Drops)
                // ------------------------------------------------------------------
                var tpCfg = OpenCfgFromClean(cleanGame.IE_A.Directory, "/data/res/team/team_param.cfg.bin");
                var tpNodes = CollectChildNodes(tpCfg.Entries, "TEAM_PARAM_INFO_BEGIN", "TEAM_PARAM_INFO");
                var tpEntries = Level5CfgBinAdapter.ReadEntries<TeamParamInfo>(tpCfg.Entries, "TEAM_PARAM_INFO", "team_param.cfg.bin");

                var storyIdSet = new HashSet<int>(storyTeams.Select(t => t.TeamConfigID).Where(x => x != 0));
                var encountIdSet = new HashSet<int>(encountTeams.Select(t => t.TeamConfigID).Where(x => x != 0));
                var miniParamIdSet = new HashSet<int>(soccers.Where(s => s != null && s.SoccerMode == 1 && s.TeamParamID != 0).Select(s => s.TeamParamID));
                var fullParamIdSet = new HashSet<int>(soccers.Where(s => s != null && s.SoccerMode == 2 && s.TeamParamID != 0).Select(s => s.TeamParamID));

                var drillIfbSet = BuildRangeHashSet("ifb", 300, 399);
                var validMiniIfbSet = BuildRangeHashSet("ifb", 101, 199);
                var ifaLookup = Level5IdHelper.BuildPrefixLookup("ifa");
                var iisLookup = Level5IdHelper.BuildPrefixLookup("iis");

                var allItems = cleanGame.GetItems("all") ?? Array.Empty<IItemConfig>();
                var directors = cleanGame.GetItems("director") ?? Array.Empty<IItemConfig>();
                bool hasNames = allItems.Any(i => i.NameID != 0);
                bool IsRealItem(IItemConfig i) => i.ItemID != 0 && (!hasNames || i.NameID != 0);

                var matchFormations = allItems
                    .Where(i => IsRealItem(i) && (i.ItemCategory == 19 || ifaLookup.ContainsKey(i.ItemID)) && i.ItemRef != 90 && !validMiniIfbSet.Contains(i.ItemID) && !drillIfbSet.Contains(i.ItemID))
                    .Select(i => i.ItemID)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                var miniFormations = allItems
                    .Where(i => IsRealItem(i) && (i.ItemCategory == 20 || validMiniIfbSet.Contains(i.ItemID)) && !ifaLookup.ContainsKey(i.ItemID) && !drillIfbSet.Contains(i.ItemID))
                    .Select(i => i.ItemID)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                var coachPool = directors
                    .Where(d => d.ItemID != 0 && (!hasNames || d.NameID != 0) && (d is not ItemConfigDirector dir || (dir.ItemNumber > 0 && dir.ItemNumber < 900)))
                    .Select(d => d.ItemID)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                var tacticPool = allItems
                    .Where(i => IsRealItem(i) && (i.ItemCategory == 23 || iisLookup.ContainsKey(i.ItemID)) && i.ItemCategory != 18 && i.ItemRef != 100)
                    .Select(i => i.ItemID)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                var validRivalUniforms = (visualProfile.ValidGoalkeeperKitNumbers.Count > 0
                        ? visualProfile.ValidGoalkeeperKitNumbers
                        : visualProfile.ValidKitNumbers)
                    .Where(k => k > 0 && k < VisualCoherenceValidator.MaxStandardSoccerKitNumberExclusive)
                    .OrderBy(k => k)
                    .Select(k => k * 1000)
                    .ToList();

                // TeamParam columns: 14=Uniform, 20=FormationID, 25=CoachID, 26=TacticID
                var tpVars = new List<Variable>();
                var tpVarMap = new Dictionary<(int Row, int Col), int>();
                for (int r = 0; r < tpNodes.Count && r < tpEntries.Count; r++)
                {
                    var vars = tpNodes[r].Item.Variables;
                    if (vars.Count <= 26) continue;
                    foreach (int col in new[] { 14, 20, 25, 26 })
                    {
                        tpVarMap[(r, col)] = tpVars.Count;
                        tpVars.Add(vars[col]);
                    }
                }
                int[] tpOffsets = LocateIntVariablesTwoPass(tpCfg, tpVars, out byte[] tpCleanBytes);
                MutableTemplateEntry tpTpl = RegisterTemplate("/data/res/team/team_param.cfg.bin", tpCleanBytes);

                var teamParamSpecs = new List<TeamParamSpec>();
                for (int r = 0; r < tpEntries.Count; r++)
                {
                    if (!tpVarMap.ContainsKey((r, 20))) continue;
                    var tp = tpEntries[r];
                    if (tp.TeamParamID == 0 || drillIfbSet.Contains(tp.FormationID))
                        continue;
                    if (protection.ScriptedTeamParamHashes.Contains(tp.TeamParamID) ||
                        (tp.TeamConfigID != 0 && protection.ScriptedTeamConfigHashes.Contains(tp.TeamConfigID)))
                        continue;

                    bool isMini = encountIdSet.Contains(tp.TeamConfigID) ||
                                  (miniParamIdSet.Contains(tp.TeamParamID) && !fullParamIdSet.Contains(tp.TeamParamID)) ||
                                  validMiniIfbSet.Contains(tp.FormationID);
                    bool isRaimon = protection.RaimonCoreTeamConfigHashes.Contains(tp.TeamConfigID);
                    bool canSwapUniform = !isMini && !isRaimon && tp.Uniform > 0 && tp.Uniform < (VisualCoherenceValidator.MaxStandardSoccerKitNumberExclusive * 1000);

                    uint modeFlags = isMini ? 2u : 1u;
                    if (canSwapUniform) modeFlags |= 4u;
                    if (!isMini && tp.CoachID != 0 && !isRaimon) modeFlags |= 8u;
                    if (!isMini && tp.TacticID != 0 && !isRaimon) modeFlags |= 16u;

                    teamParamSpecs.Add(new TeamParamSpec
                    {
                        ModeFlags = modeFlags,
                        CleanUniform = tp.Uniform,
                        UniformOffset = (uint)tpOffsets[tpVarMap[(r, 14)]],
                        FormationOffset = (uint)tpOffsets[tpVarMap[(r, 20)]],
                        CoachOffset = (uint)tpOffsets[tpVarMap[(r, 25)]],
                        TacticOffset = (uint)tpOffsets[tpVarMap[(r, 26)]]
                    });
                }

                // ------------------------------------------------------------------
                // 7. MAP ENCOUNTER SPAWNS (.enc.bin / map cfg) & OVERWORLD TREASURE CHESTS
                // ------------------------------------------------------------------
                var mapSpawns = MapDataService.LoadAllEncounterSpawns(cleanGame.IE_A.Directory);
                var mapChests = MapDataService.LoadAllTreasureChests(cleanGame.IE_A.Directory);

                var mapFilesWithEdits = mapSpawns.Select(s => NormalizeVirtPath(s.VirtualFilePath))
                    .Concat(mapChests.Select(c => NormalizeVirtPath(c.VirtualFilePath)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();

                var spawnsByPath = mapSpawns.GroupBy(s => NormalizeVirtPath(s.VirtualFilePath), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                var chestsByPath = mapChests.GroupBy(c => NormalizeVirtPath(c.VirtualFilePath), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                var paramHashSet = new HashSet<int>(charaParams.Where(p => p.ParamHash != 0).Select(p => p.ParamHash));
                var baseHashSet = new HashSet<int>(charaBases.Where(b => b.BaseHash != 0).Select(b => b.BaseHash));

                var mapSpawnSpecs = new List<MapSpawnSpec>();
                var chestSpecs = new List<ChestSpec>();

                foreach (string mapVirtPath in mapFilesWithEdits)
                {
                    if (!cleanArc.SlotByPath.ContainsKey(mapVirtPath))
                        continue;

                    var mCfg = OpenCfgFromClean(cleanGame.IE_A.Directory, "/" + mapVirtPath);
                    var mVars = new List<Variable>();

                    var encNodes = FindNodesByPrefix(mCfg.Entries, "ENCOUNT_INFO");
                    var tboxNodes = FindNodesByPrefix(mCfg.Entries, "TBOX_CONFIG");

                    var localSpawnMappings = new List<(MapEncounterSpawnEntry Entry, int[] VarIndices, byte[] Domains)>();
                    if (spawnsByPath.TryGetValue(mapVirtPath, out var fileSpawns))
                    {
                        foreach (var sp in fileSpawns)
                        {
                            if (sp.NodeIndex < 0 || sp.NodeIndex >= encNodes.Count) continue;
                            int tid = sp.Record.TeamID;
                            if (tid != 0 && (protection.ScriptedMatchHashes.Contains(tid) ||
                                             protection.ScriptedTeamConfigHashes.Contains(tid) ||
                                             protection.ScriptedTeamParamHashes.Contains(tid)))
                            {
                                continue;
                            }

                            var vars = encNodes[sp.NodeIndex].Item.Variables;
                            if (vars.Count < 8) continue; // ENCOUNT_INFO players are vars[3..7] (let's verify exact indices via MapDataService!)
                            int[] playerCols = FindEncountPlayerVarColumns(vars, sp.Record);
                            if (playerCols == null) continue;

                            var vIdxs = new int[5];
                            var domains = new byte[5];
                            int[] origSlots = { sp.Record.PlayerID1, sp.Record.PlayerID2, sp.Record.PlayerID3, sp.Record.PlayerID4, sp.Record.PlayerID5 };
                            for (int s = 0; s < 5; s++)
                            {
                                vIdxs[s] = mVars.Count;
                                mVars.Add(vars[playerCols[s]]);
                                if (origSlots[s] == 0) domains[s] = 0;
                                else if (paramHashSet.Contains(origSlots[s]))
                                    domains[s] = protection.ProtectedStoryPlayerParamHashes.Contains(origSlots[s]) ? (byte)3 : (byte)1;
                                else if (baseHashSet.Contains(origSlots[s]))
                                    domains[s] = 2;
                                else
                                    domains[s] = 0;
                            }
                            localSpawnMappings.Add((sp, vIdxs, domains));
                        }
                    }

                    var localChestMappings = new List<(int LightVarIdx, int ShadowVarIdx)>();
                    if (chestsByPath.TryGetValue(mapVirtPath, out var fileChests))
                    {
                        foreach (var ch in fileChests)
                        {
                            if (ch.NodeIndex < 0 || ch.NodeIndex >= tboxNodes.Count) continue;
                            if (protection.ProtectedTBoxFlags.Contains(ch.Record.TBoxFlag) ||
                                protection.IsProtectedItem(ch.Record.ItemIDLight) ||
                                protection.IsProtectedItem(ch.Record.ItemIDShadow))
                            {
                                continue;
                            }

                            var vars = tboxNodes[ch.NodeIndex].Item.Variables;
                            if (vars.Count < 9) continue;
                            // Column 6 is TBoxFlag, NOT the Shadow item (column 8).
                            // Keep absent rewards empty rather than creating new ones.
                            int lIdx = -1;
                            int sIdx = -1;
                            if (ch.Record.ItemIDLight != 0) { lIdx = mVars.Count; mVars.Add(vars[5]); }
                            if (ch.Record.ItemIDShadow != 0) { sIdx = mVars.Count; mVars.Add(vars[8]); }
                            if (lIdx < 0 && sIdx < 0) continue;
                            localChestMappings.Add((lIdx, sIdx));
                        }
                    }

                    if (mVars.Count == 0)
                        continue;

                    int[] mOffsets = LocateIntVariablesTwoPass(mCfg, mVars, out byte[] mCleanBytes);
                    MutableTemplateEntry mTpl = RegisterTemplate("/" + mapVirtPath, mCleanBytes);

                    foreach (var (spEntry, vIdxs, domains) in localSpawnMappings)
                    {
                        int linkedEnc = (spEntry.Record.TeamID != 0 && encountIdxByKey.TryGetValue(spEntry.Record.TeamID, out int eIdx))
                            ? eIdx
                            : -1;
                        mapSpawnSpecs.Add(new MapSpawnSpec
                        {
                            TemplateIdx = (uint)mTpl.TemplateIndex,
                            LinkedEncountIdx = linkedEnc,
                            Uniform = spEntry.Record.Uniform,
                            SlotDomains = domains,
                            CleanSlots = new[] { spEntry.Record.PlayerID1, spEntry.Record.PlayerID2, spEntry.Record.PlayerID3, spEntry.Record.PlayerID4, spEntry.Record.PlayerID5 },
                            SlotOffsets = vIdxs.Select(vi => (uint)mOffsets[vi]).ToArray()
                        });
                    }

                    foreach (var (lVi, sVi) in localChestMappings)
                    {
                        chestSpecs.Add(new ChestSpec
                        {
                            TemplateIdx = (uint)mTpl.TemplateIndex,
                            LightItemOffset = lVi < 0 ? 0xFFFFFFFFu : (uint)mOffsets[lVi],
                            ShadowItemOffset = sVi < 0 ? 0xFFFFFFFFu : (uint)mOffsets[sVi]
                        });
                    }
                }

                // ------------------------------------------------------------------
                // 8. POOLS (Avatars, Emblems, Chest Rewards)
                // ------------------------------------------------------------------
                var validAvatars = visualProfile.ValidAvatarHashes.OrderBy(x => x).ToList();
                var saveAvatarSet = new HashSet<int>(
                    avatars.Where(a => a.AvatarHash != 0 && wkkSet.Contains(a.SpecialMoveID)).Select(a => a.AvatarHash));
                var nonSaveAvatars = validAvatars.Where(h => !saveAvatarSet.Contains(h)).ToList();
                if (nonSaveAvatars.Count == 0) nonSaveAvatars = validAvatars;

                var emblemPool = new HashSet<int>();
                foreach (var st in storyTeams) if (st.Emblem1 > 0) emblemPool.Add(st.Emblem1);
                foreach (var et in encountTeams) if (et.Emblem1 > 0) emblemPool.Add(et.Emblem1);
                var orderedEmblems = emblemPool.OrderBy(x => x).ToList();

                var equipmentRewards = (cleanGame.GetItems("equipment") ?? Array.Empty<IItemConfig>())
                    .Where(i => IsRealItem(i) && !protection.IsProtectedItem(i.ItemID))
                    .Select(i => i.ItemID);
                var consumableRewards = (cleanGame.GetItems("consumable") ?? Array.Empty<IItemConfig>())
                    .Where(i => IsRealItem(i) && !protection.IsProtectedItem(i.ItemID) && i.ItemCategory != 14 && i.ItemCategory != 15)
                    .Select(i => i.ItemID);
                var safeChestRewardPool = equipmentRewards.Concat(consumableRewards).Distinct().OrderBy(x => x).ToList();

                // ------------------------------------------------------------------
                // 9. RESOLVE ALL 29,969 CLEAN ARC0 SLOTS (InstalledSlot / Template / FallbackBlob)
                // ------------------------------------------------------------------
                var templateByArcIdx = templates.ToDictionary(t => t.ArcFileIndex);
                var fallbackBlobs = new List<byte[]>();
                var slotSpecs = new CleanArcSlotSpec[cleanArc.Slots.Length];
                int reusedInstCount = 0;

                // Physical ordering inside clean ARC0
                var physOrder = cleanArc.Slots
                    .OrderBy(s => s.FileOffsetInData)
                    .ThenBy(s => s.FileIndex)
                    .Select(s => (uint)s.FileIndex)
                    .ToArray();

                for (int i = 0; i < cleanArc.Slots.Length; i++)
                {
                    var cs = cleanArc.Slots[i];
                    if (templateByArcIdx.TryGetValue(i, out var tpl))
                    {
                        slotSpecs[i] = new CleanArcSlotSpec
                        {
                            NameCrc32 = cs.NameCrc32,
                            NameOffsetInFolder = cs.NameOffsetInFolder,
                            CleanSize = (uint)tpl.CleanNormalizedBytes.Length,
                            CleanDataCrc32 = Arc0DeltaPatchPackBuilder.ComputeCrc32(tpl.CleanNormalizedBytes),
                            SourceKind = SrcMutableTemplate,
                            SourceIndex = (uint)tpl.TemplateIndex
                        };
                        continue;
                    }

                    byte[] cBytes = ReadBytes(cleanFs, cs.AbsoluteOffset, checked((int)cs.FileSize));
                    uint cCrc = Arc0DeltaPatchPackBuilder.ComputeCrc32(cBytes);
                    int instDonor = -1;

                    if (instArc.Slots[i].FileSize == cs.FileSize)
                    {
                        string cSha = Convert.ToHexString(SHA256.HashData(cBytes));
                        if (instSlotSha[i] == cSha)
                            instDonor = i;
                        else if (instSlotByDigest.TryGetValue((cs.FileSize, cSha), out int crossSlot))
                            instDonor = crossSlot;
                    }
                    else
                    {
                        string cSha = Convert.ToHexString(SHA256.HashData(cBytes));
                        if (instSlotByDigest.TryGetValue((cs.FileSize, cSha), out int crossSlot))
                            instDonor = crossSlot;
                    }

                    if (instDonor >= 0)
                    {
                        reusedInstCount++;
                        slotSpecs[i] = new CleanArcSlotSpec
                        {
                            NameCrc32 = cs.NameCrc32,
                            NameOffsetInFolder = cs.NameOffsetInFolder,
                            CleanSize = cs.FileSize,
                            CleanDataCrc32 = cCrc,
                            SourceKind = SrcInstalledSlot,
                            SourceIndex = (uint)instDonor
                        };
                    }
                    else
                    {
                        int blobIdx = fallbackBlobs.Count;
                        fallbackBlobs.Add(cBytes);
                        slotSpecs[i] = new CleanArcSlotSpec
                        {
                            NameCrc32 = cs.NameCrc32,
                            NameOffsetInFolder = cs.NameOffsetInFolder,
                            CleanSize = cs.FileSize,
                            CleanDataCrc32 = cCrc,
                            SourceKind = SrcEmbeddedBlob,
                            SourceIndex = (uint)blobIdx
                        };
                    }
                }

                // ------------------------------------------------------------------
                // 10. SERIALIZE UNCOMPRESSED IE4RV100 PAYLOAD & LZ4-WRAP TO DISK
                // ------------------------------------------------------------------
                byte[] rawPayload = SerializeRecipePayload(
                    cleanArc,
                    physOrder,
                    slotSpecs,
                    fallbackBlobs,
                    templates,
                    visualGroups,
                    playerSpecs,
                    storyTeamSpecs,
                    encountTeamSpecs,
                    mapSpawnSpecs,
                    palpackSpecs,
                    skillParamSpecs,
                    skillSlotSpecs,
                    teamParamSpecs,
                    chestSpecs,
                    shotPool,
                    soloStarterShotPool,
                    dribblePool,
                    blockPool,
                    savePool,
                    talentPool,
                    validAvatars,
                    nonSaveAvatars,
                    orderedEmblems,
                    matchFormations,
                    miniFormations,
                    coachPool,
                    tacticPool,
                    validRivalUniforms,
                    safeChestRewardPool);

                uint payloadCrc = Arc0DeltaPatchPackBuilder.ComputeCrc32(rawPayload);
                byte[] lz4Payload = Arc0DeltaPatchPackBuilder.CompressLz4Block(rawPayload);
                bool useLz4 = lz4Payload.Length < rawPayload.Length;
                byte[] storedPayload = useLz4 ? lz4Payload : rawPayload;

                string parentDir = Path.GetDirectoryName(Path.GetFullPath(outputRecipePath));
                if (!string.IsNullOrEmpty(parentDir))
                    Directory.CreateDirectory(parentDir);

                using (var outFs = new FileStream(outputRecipePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                using (var bw = new BinaryWriter(outFs, Encoding.UTF8, leaveOpen: true))
                {
                    // Outer IE4RV100 Container (20 bytes):
                    // [0x00..0x08] "IE4RV100"
                    // [0x08..0x0C] uint32 UncompressedPayloadSize
                    // [0x0C..0x10] uint32 StoredPayloadSize (if == UncompressedPayloadSize && codec == 1 -> raw, else LZ4)
                    // [0x10..0x14] uint32 PayloadCrc32
                    // [0x14]       byte   Codec (1 = Raw, 2 = LZ4)
                    // [0x15..0x18] reserved (0)
                    // Total outer header = 24 bytes
                    bw.Write(MagicBytes);
                    bw.Write((uint)rawPayload.Length);
                    bw.Write((uint)storedPayload.Length);
                    bw.Write(payloadCrc);
                    bw.Write(useLz4 ? (byte)2 : (byte)1);
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write(storedPayload);
                    bw.Flush();
                }

                return new OnConsoleSeedRecipeResult
                {
                    OutputRecipePath = Path.GetFullPath(outputRecipePath),
                    CompressedSizeBytes = new FileInfo(outputRecipePath).Length,
                    UncompressedSizeBytes = rawPayload.Length,
                    TotalArc0Entries = slotSpecs.Length,
                    ReusedInstalledSlotsCount = reusedInstCount,
                    EmbeddedFallbackBlobsCount = fallbackBlobs.Count,
                    MutableTemplatesCount = templates.Count,
                    VisualSwapMembersCount = visualGroups.Sum(g => g.Count),
                    PlayablePlayersCount = playerSpecs.Count,
                    StoryTeamsCount = storyTeamSpecs.Count,
                    EncountTeamsCount = encountTeamSpecs.Count,
                    MapSpawnsCount = mapSpawnSpecs.Count,
                    PalpackCardsCount = palpackSpecs.Count,
                    TreasureChestsCount = chestSpecs.Count
                };
            }
        }

        private static byte[] SerializeRecipePayload(
            ParsedArcLayout cleanArc,
            uint[] physOrder,
            CleanArcSlotSpec[] slotSpecs,
            List<byte[]> fallbackBlobs,
            List<MutableTemplateEntry> templates,
            List<List<VisualMemberSpec>> visualGroups,
            List<PlayerSpec> players,
            List<StoryTeamSpec> storyTeams,
            List<EncountTeamSpec> encountTeams,
            List<MapSpawnSpec> mapSpawns,
            List<PalpackSpec> palpacks,
            List<SkillParamSpec> skillParams,
            List<SkillSlotSpec> skillSlots,
            List<TeamParamSpec> teamParams,
            List<ChestSpec> chests,
            List<int> shotPool,
            List<int> soloStarterShotPool,
            List<int> dribblePool,
            List<int> blockPool,
            List<int> savePool,
            List<int> talentPool,
            List<int> validAvatars,
            List<int> nonSaveAvatars,
            List<int> emblems,
            List<int> matchFormations,
            List<int> miniFormations,
            List<int> coaches,
            List<int> tactics,
            List<int> rivalUniforms,
            List<int> chestRewards)
        {
            using var ms = new MemoryStream(3 * 1024 * 1024);
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            // Section 1: Clean ARC0 Base Header & Compressed Tables
            ARC0Support.Header hdr = cleanArc.Header;
            int align = cleanArc.DetectedAlignment;
            int fileEntriesSectionLen = 4 + (slotSpecs.Length * 16);
            static int AlignUpInt(int v, int a) => (v + a - 1) & ~(a - 1);

            hdr.DirectoryEntriesOffset = 0x48;
            hdr.DirectoryHashOffset = AlignUpInt(hdr.DirectoryEntriesOffset + cleanArc.DirEntriesComp.Length, 4);
            hdr.FileEntriesOffset = AlignUpInt(hdr.DirectoryHashOffset + cleanArc.DirHashesComp.Length, 4);
            hdr.NameOffset = AlignUpInt(hdr.FileEntriesOffset + fileEntriesSectionLen, 4);
            hdr.DataOffset = AlignUpInt(hdr.NameOffset + cleanArc.NameTableComp.Length, align);

            int decompressedTableChunkSize = ((hdr.DirectoryEntriesCount * 20 +
                                               hdr.DirectoryHashCount * 4 +
                                               slotSpecs.Length * 16 +
                                               cleanArc.DecompressedNameTableLength +
                                               0x20 + 3) & ~3);
            hdr.TableChunkSize = Math.Max(hdr.TableChunkSize, decompressedTableChunkSize);

            bw.Write((uint)slotSpecs.Length);
            bw.Write((uint)align);
            bw.Write((uint)hdr.DataOffset);

            // Serialize exact 72-byte ARC0Support.Header with final pre-calculated section offsets!
            byte[] hdr72 = SerializeArc0Header(hdr);
            bw.Write(hdr72);

            WriteByteBlob(bw, cleanArc.DirEntriesComp);
            WriteByteBlob(bw, cleanArc.DirHashesComp);
            WriteByteBlob(bw, cleanArc.NameTableComp);

            // Physical slot order (N * uint32)
            for (int i = 0; i < physOrder.Length; i++)
                bw.Write(physOrder[i]);

            // Slot descriptors (N * 24 bytes, 4-byte aligned!)
            for (int i = 0; i < slotSpecs.Length; i++)
            {
                var s = slotSpecs[i];
                bw.Write(s.NameCrc32);
                bw.Write(s.NameOffsetInFolder);
                bw.Write(s.CleanSize);
                bw.Write(s.CleanDataCrc32);
                bw.Write((uint)s.SourceKind);
                bw.Write(s.SourceIndex);
            }

            // Embedded fallback clean blobs
            bw.Write((uint)fallbackBlobs.Count);
            foreach (byte[] blob in fallbackBlobs)
                WriteByteBlob(bw, blob);

            // Mutable .cfg.bin / map templates
            bw.Write((uint)templates.Count);
            foreach (var tpl in templates)
            {
                bw.Write((uint)tpl.ArcFileIndex);
                WriteByteBlob(bw, tpl.CleanNormalizedBytes);
            }

            // Section 2: Visual Swap Groups
            bw.Write((uint)visualGroups.Count);
            foreach (var grp in visualGroups)
            {
                bw.Write((uint)grp.Count);
                foreach (var m in grp)
                {
                    bw.Write(m.BustupArcIdx);
                    bw.Write(m.RpgArcIdx);
                    bw.Write(m.WazaArcIdx);
                    bw.Write(m.DonorBustupInstSlot);
                    bw.Write(m.BustupSize);
                    bw.Write(m.BustupCrc);
                    bw.Write(m.DonorRpgInstSlot);
                    bw.Write(m.RpgSize);
                    bw.Write(m.RpgCrc);
                    bw.Write(m.DonorWazaInstSlot);
                    bw.Write(m.WazaSize);
                    bw.Write(m.WazaCrc);
                    bw.Write(m.NameHash);
                    bw.Write(m.NicknameHash);
                    bw.Write(m.DescHash);
                    bw.Write(m.Body);
                    bw.Write(m.Skin);
                    bw.Write(m.Gender);
                    bw.Write(m.Year);
                    bw.Write(m.CbTemplateIdx);
                    bw.Write(m.OffName);
                    bw.Write(m.OffNick);
                    bw.Write(m.OffBody);
                    bw.Write(m.OffSkin);
                    bw.Write(m.OffGender);
                    bw.Write(m.OffYear);
                    bw.Write(m.OffDesc);
                }
            }

            // Section 3: Playable Players
            bw.Write((uint)players.Count);
            bw.Write(players.Count > 0 ? players[0].CpTemplateIdx : 0u);
            foreach (var p in players)
            {
                bw.Write(p.ParamHash);
                bw.Write(p.BaseHash);
                bw.Write(p.Body);
                bw.Write(p.Gender);
                bw.Write(p.OrigPosition);
                bw.Write(p.Flags);
                bw.Write(p.MoveOffset);
                bw.Write(p.MoveCount);
                bw.Write(p.CleanFp);
                bw.Write(p.CleanTp);
                bw.Write(p.CleanKick);
                bw.Write(p.CleanDribble);
                bw.Write(p.CleanTechnique);
                bw.Write(p.CleanBlock);
                bw.Write(p.CleanSpeed);
                bw.Write(p.CleanStamina);
                bw.Write(p.CleanCatch);
                bw.Write(p.CleanLuck);
                bw.Write(p.CleanFreedom);
                bw.Write(p.CleanAvatarHash);
                for (int c = 0; c < 14; c++)
                    bw.Write(p.FieldOffsets[c]);
            }

            // Section 4: Story Teams, Encounter Teams, Map Spawns & PalPack
            uint tcTplIdx = (uint)(templates.FirstOrDefault(t => t.VirtualPath.EndsWith("team_config.cfg.bin", StringComparison.OrdinalIgnoreCase))?.TemplateIndex ?? 0);
            uint itemTplIdx = (uint)(templates.FirstOrDefault(t => t.VirtualPath.EndsWith("item_config.cfg.bin", StringComparison.OrdinalIgnoreCase))?.TemplateIndex ?? 0);
            uint skTplIdx = (uint)(templates.FirstOrDefault(t => t.VirtualPath.EndsWith("skill_config.cfg.bin", StringComparison.OrdinalIgnoreCase))?.TemplateIndex ?? 0);
            uint stblTplIdx = (uint)(templates.FirstOrDefault(t => t.VirtualPath.EndsWith("skill_table.cfg.bin", StringComparison.OrdinalIgnoreCase))?.TemplateIndex ?? 0);
            uint tpTplIdx = (uint)(templates.FirstOrDefault(t => t.VirtualPath.EndsWith("team_param.cfg.bin", StringComparison.OrdinalIgnoreCase))?.TemplateIndex ?? 0);

            bw.Write(tcTplIdx);
            bw.Write((uint)storyTeams.Count);
            foreach (var st in storyTeams)
            {
                bw.Write(st.TeamConfigId);
                bw.Write(st.Flags);
                bw.Write(st.CleanEmblem1);
                bw.Write(st.EmblemOffset);
                for (int s = 0; s < 16; s++) bw.Write(st.CleanPlayers[s]);
                for (int s = 0; s < 16; s++) bw.Write(st.PlayerOffsets[s]);
                for (int s = 0; s < 16; s++) bw.Write(st.ShirtOffsets[s]);
            }

            bw.Write((uint)encountTeams.Count);
            foreach (var et in encountTeams)
            {
                bw.Write(et.TeamConfigId);
                bw.Write(et.Flags);
                bw.Write(et.CleanEmblem1);
                bw.Write(et.EmblemOffset);
                for (int s = 0; s < 5; s++) bw.Write(et.CleanPlayers[s]);
                for (int s = 0; s < 5; s++) bw.Write(et.PlayerOffsets[s]);
                for (int s = 0; s < 5; s++) bw.Write(et.ShirtOffsets[s]);
            }

            bw.Write((uint)mapSpawns.Count);
            foreach (var sp in mapSpawns)
            {
                bw.Write(sp.TemplateIdx);
                bw.Write(sp.LinkedEncountIdx);
                bw.Write(sp.Uniform);
                for (int s = 0; s < 5; s++) bw.Write((uint)sp.SlotDomains[s]);
                for (int s = 0; s < 5; s++) bw.Write(sp.CleanSlots[s]);
                for (int s = 0; s < 5; s++) bw.Write(sp.SlotOffsets[s]);
            }

            bw.Write(itemTplIdx);
            bw.Write((uint)palpacks.Count);
            foreach (var pk in palpacks)
            {
                bw.Write(pk.Eligible);
                bw.Write(pk.CleanRecruitedHash);
                bw.Write(pk.RecruitedOffset);
                for (int s = 0; s < 4; s++) bw.Write(pk.PrereqTypes[s]);
                for (int s = 0; s < 4; s++) bw.Write(pk.CleanPrereqIds[s]);
                for (int s = 0; s < 4; s++) bw.Write(pk.PrereqIdOffsets[s]);
            }

            // Section 5: Skill Params & Skill Table
            bw.Write(skTplIdx);
            bw.Write((uint)skillParams.Count);
            foreach (var sk in skillParams)
            {
                bw.Write(sk.CleanElement);
                bw.Write(sk.CleanTp);
                bw.Write(sk.CleanPower);
                bw.Write(sk.ElemOffset);
                bw.Write(sk.TpOffset);
                bw.Write(sk.PowerOffset);
            }

            bw.Write(stblTplIdx);
            bw.Write((uint)skillSlots.Count);
            foreach (var sl in skillSlots)
            {
                bw.Write(sl.CleanSkillHash);
                bw.Write(sl.CleanLevel);
                bw.Write(sl.Flags);
                bw.Write(sl.LevelOffset);
                bw.Write(sl.HashOffset);
            }

            // Section 6: TeamParams & Chests
            bw.Write(tpTplIdx);
            bw.Write((uint)teamParams.Count);
            foreach (var tp in teamParams)
            {
                bw.Write(tp.ModeFlags);
                bw.Write(tp.CleanUniform);
                bw.Write(tp.UniformOffset);
                bw.Write(tp.FormationOffset);
                bw.Write(tp.CoachOffset);
                bw.Write(tp.TacticOffset);
            }

            bw.Write((uint)chests.Count);
            foreach (var ch in chests)
            {
                bw.Write(ch.TemplateIdx);
                bw.Write(ch.LightItemOffset);
                bw.Write(ch.ShadowItemOffset);
            }

            // Section 7: Candidate Int32 Pools
            WriteIntPool(bw, shotPool);
            WriteIntPool(bw, soloStarterShotPool);
            WriteIntPool(bw, dribblePool);
            WriteIntPool(bw, blockPool);
            WriteIntPool(bw, savePool);
            WriteIntPool(bw, talentPool);
            WriteIntPool(bw, validAvatars);
            WriteIntPool(bw, nonSaveAvatars);
            WriteIntPool(bw, emblems);
            WriteIntPool(bw, matchFormations);
            WriteIntPool(bw, miniFormations);
            WriteIntPool(bw, coaches);
            WriteIntPool(bw, tactics);
            WriteIntPool(bw, rivalUniforms);
            WriteIntPool(bw, chestRewards);

            bw.Flush();
            return ms.ToArray();
        }

        private static void WriteByteBlob(BinaryWriter bw, byte[] data)
        {
            bw.Write((uint)data.Length);
            if (data.Length > 0)
                bw.Write(data);
            int mod = data.Length & 3;
            if (mod != 0)
            {
                for (int p = mod; p < 4; p++)
                    bw.Write((byte)0);
            }
        }

        private static void WriteIntPool(BinaryWriter bw, List<int> items)
        {
            bw.Write((uint)items.Count);
            for (int i = 0; i < items.Count; i++)
                bw.Write(items[i]);
        }

        private static byte[] SerializeArc0Header(ARC0Support.Header hdr)
        {
            using var ms = new MemoryStream(72);
            using var bw = new BinaryDataWriter(ms);
            bw.WriteStruct(hdr);
            byte[] res = ms.ToArray();
            if (res.Length != 72)
                throw new InvalidDataException($"ARC0 header serialized to {res.Length} bytes, expected 72.");
            return res;
        }

        private static int[] LocateIntVariablesTwoPass(
            CfgBin<CfgTreeNode> cfg,
            List<Variable> variables,
            out byte[] cleanNormalizedBytes)
        {
            int count = variables.Count;
            var origValues = new int[count];
            for (int i = 0; i < count; i++)
            {
                int cur = Level5CfgValueParser.TryReadHashOrId(variables[i], out int hv, out _) ? hv : 0;
                origValues[i] = cur;
                Level5CfgValueParser.UpdateHashOrIntVariableInPlace(variables[i], cur);
            }

            cleanNormalizedBytes = cfg.Save();
            if (count == 0)
                return Array.Empty<int>();

            const uint MaskA = 0x5EB10000u;
            const uint MaskB = 0xA14E0000u;

            for (int i = 0; i < count; i++)
                variables[i].Value = unchecked((int)(MaskA ^ (uint)i));
            byte[] bytesA = cfg.Save();

            for (int i = 0; i < count; i++)
                variables[i].Value = unchecked((int)(MaskB ^ (uint)i));
            byte[] bytesB = cfg.Save();

            for (int i = 0; i < count; i++)
                variables[i].Value = origValues[i];

            if (bytesA.Length != cleanNormalizedBytes.Length || bytesB.Length != cleanNormalizedBytes.Length)
            {
                throw new InvalidDataException(
                    $"CfgBin length changed during sentinel pass ({cleanNormalizedBytes.Length} vs {bytesA.Length}/{bytesB.Length}).");
            }

            var offsets = new int[count];
            var hitCounts = new int[count];
            Array.Fill(offsets, -1);

            for (int pos = 0; pos <= cleanNormalizedBytes.Length - 4; pos += 4)
            {
                uint uA = BinaryPrimitives.ReadUInt32LittleEndian(bytesA.AsSpan(pos, 4));
                uint uB = BinaryPrimitives.ReadUInt32LittleEndian(bytesB.AsSpan(pos, 4));
                if (uA == uB)
                    continue;

                uint idxA = uA ^ MaskA;
                uint idxB = uB ^ MaskB;
                if (idxA == idxB && idxA < (uint)count)
                {
                    offsets[idxA] = pos;
                    hitCounts[idxA]++;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (hitCounts[i] != 1 || offsets[i] < 0)
                {
                    throw new InvalidDataException(
                        $"Failed to uniquely locate variable #{i} in CfgBin (hits={hitCounts[i]}).");
                }

                int verifyClean = BinaryPrimitives.ReadInt32LittleEndian(cleanNormalizedBytes.AsSpan(offsets[i], 4));
                if (verifyClean != origValues[i])
                {
                    throw new InvalidDataException(
                        $"Clean value verification failed at offset 0x{offsets[i]:X}: {verifyClean} != {origValues[i]}.");
                }
            }

            return offsets;
        }

        private static int[] FindEncountPlayerVarColumns(List<Variable> vars, EncountMapRecord rec)
        {
            // Verify against MapDataService's ENCOUNT_INFO layout:
            // vars: [0]=EncountID, [1]=TeamID, [2]=ChasePlayer, [3..7]=PlayerID1..5 or check exact match!
            // Let's match against MapDataService: check candidate start columns (vars.Count >= 11 -> PlayerID1..5 at 5..9 or 3..7)
            int[] expected = { rec.PlayerID1, rec.PlayerID2, rec.PlayerID3, rec.PlayerID4, rec.PlayerID5 };
            int[] candidateStarts = { 5, 4, 3, 2, 6 };
            foreach (int start in candidateStarts)
            {
                if (start + 5 > vars.Count) continue;
                bool allMatch = true;
                for (int s = 0; s < 5; s++)
                {
                    int val = Level5CfgValueParser.TryReadHashOrId(vars[start + s], out int hv, out _) ? hv : int.MinValue;
                    if (val != expected[s])
                    {
                        allMatch = false;
                        break;
                    }
                }
                if (allMatch)
                    return new[] { start, start + 1, start + 2, start + 3, start + 4 };
            }
            return null;
        }

        private static CfgBin<CfgTreeNode> OpenCfgFromClean(VirtualDirectory root, string virtualPath)
        {
            byte[] bytes = Arc0SafeArchive.TryReadFileFromPath(root, virtualPath)
                ?? throw new FileNotFoundException($"Virtual file '{virtualPath}' not found in clean ie_a.fa.");
            var cfg = new CfgBin<CfgTreeNode>();
            cfg.Open(bytes);
            return cfg;
        }

        private static List<CfgTreeNode> CollectChildNodes(CfgTreeNode root, string beginName, string childName)
        {
            CfgTreeNode begin = root?.FindByName(beginName);
            if (begin == null)
                return new List<CfgTreeNode>();
            return begin.Children.OfType<CfgTreeNode>().Where(c => c.Item?.Name == childName).ToList();
        }

        private static List<CfgTreeNode> FindNodesByPrefix(CfgTreeNode root, string prefix)
        {
            var results = new List<CfgTreeNode>();
            void Walk(CfgTreeNode n)
            {
                if (n == null) return;
                if (n.Item?.Name != null &&
                    n.Item.Name.StartsWith(prefix, StringComparison.Ordinal) &&
                    !n.Item.Name.EndsWith("_BEGIN", StringComparison.Ordinal) &&
                    !n.Item.Name.EndsWith("_END", StringComparison.Ordinal))
                {
                    results.Add(n);
                }
                foreach (var ch in n.Children.OfType<CfgTreeNode>())
                    Walk(ch);
            }
            Walk(root);
            return results;
        }

        private static HashSet<int> BuildRangeHashSet(string prefix, int startInclusive, int endInclusive)
        {
            var set = new HashSet<int>(endInclusive - startInclusive + 1);
            for (int n = startInclusive; n <= endInclusive; n++)
                set.Add(Level5IdHelper.ComputeCrc32Utf8($"{prefix}{n:D4}"));
            return set;
        }

        private static ParsedArcLayout ParseArcLayout(FileStream stream)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding shiftJis = Encoding.GetEncoding("Shift-JIS");

            stream.Seek(0, SeekOrigin.Begin);
            var reader = new BinaryDataReader(stream);
            var header = reader.ReadStruct<ARC0Support.Header>();

            reader.Seek((uint)header.DirectoryEntriesOffset);
            byte[] dirComp = TrimTrailingZeroPadForLz(reader.GetSection(header.DirectoryHashOffset - header.DirectoryEntriesOffset));
            byte[] dirRaw = Compressor.Decompress(dirComp);
            var dirEntries = new BinaryDataReader(dirRaw).ReadMultipleStruct<ARC0Support.DirectoryEntry>(header.DirectoryEntriesCount);

            reader.Seek((uint)header.DirectoryHashOffset);
            byte[] hashComp = TrimTrailingZeroPadForLz(reader.GetSection(header.FileEntriesOffset - header.DirectoryHashOffset));

            reader.Seek((uint)header.FileEntriesOffset);
            byte[] fileComp = reader.GetSection(header.NameOffset - header.FileEntriesOffset);
            byte[] fileRaw = Compressor.Decompress(fileComp);
            var fileEntries = new BinaryDataReader(fileRaw).ReadMultipleStruct<ARC0Support.FileEntry>(header.FileEntriesCount);

            reader.Seek((uint)header.NameOffset);
            byte[] nameComp = TrimTrailingZeroPadForLz(reader.GetSection(header.DataOffset - header.NameOffset));
            byte[] nameRaw = Compressor.Decompress(nameComp);
            var nameReader = new BinaryDataReader(nameRaw);

            var slots = new ArcSlotInfo[fileEntries.Length];
            var byPath = new Dictionary<string, int>(fileEntries.Length, StringComparer.OrdinalIgnoreCase);

            foreach (var dir in dirEntries)
            {
                nameReader.Seek((uint)dir.DirectoryNameStartOffset);
                string rawDir = (nameReader.ReadString(shiftJis) ?? string.Empty).Replace('\\', '/').Trim('/');
                string prefix = string.IsNullOrEmpty(rawDir) ? string.Empty : rawDir + "/";

                int first = dir.FirstFileIndex;
                int end = Math.Min(fileEntries.Length, first + dir.FileCount);
                for (int idx = first; idx < end; idx++)
                {
                    var fe = fileEntries[idx];
                    nameReader.Seek((uint)(dir.FileNameStartOffset + fe.NameOffsetInFolder));
                    string fName = nameReader.ReadString(shiftJis) ?? string.Empty;
                    string virtPath = prefix + fName;
                    slots[idx] = new ArcSlotInfo
                    {
                        FileIndex = idx,
                        VirtualPath = virtPath,
                        NameCrc32 = fe.Crc32,
                        NameOffsetInFolder = fe.NameOffsetInFolder,
                        FileOffsetInData = fe.FileOffset,
                        FileSize = fe.FileSize,
                        AbsoluteOffset = (long)header.DataOffset + fe.FileOffset
                    };
                    byPath[virtPath] = idx;
                }
            }

            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null)
                {
                    var fe = fileEntries[i];
                    slots[i] = new ArcSlotInfo
                    {
                        FileIndex = i,
                        VirtualPath = $"__unreferenced__/{i}",
                        NameCrc32 = fe.Crc32,
                        NameOffsetInFolder = fe.NameOffsetInFolder,
                        FileOffsetInData = fe.FileOffset,
                        FileSize = fe.FileSize,
                        AbsoluteOffset = (long)header.DataOffset + fe.FileOffset
                    };
                }
            }

            int detectedAlign = 4;
            foreach (int cand in new[] { 128, 64, 32, 16, 8, 4 })
            {
                if (header.DataOffset % cand != 0) continue;
                if (fileEntries.Where(f => f.FileOffset != 0).All(f => f.FileOffset % cand == 0))
                {
                    detectedAlign = cand;
                    break;
                }
            }

            return new ParsedArcLayout
            {
                Header = header,
                DirEntriesComp = dirComp,
                DirHashesComp = hashComp,
                NameTableComp = nameComp,
                DecompressedNameTableLength = nameRaw.Length,
                DetectedAlignment = detectedAlign,
                Slots = slots,
                SlotByPath = byPath
            };
        }

        private static byte[] TrimTrailingZeroPadForLz(byte[] sectionBytes)
        {
            if (sectionBytes == null || sectionBytes.Length < 4)
                return sectionBytes ?? Array.Empty<byte>();
            uint hdr = BitConverter.ToUInt32(sectionBytes, 0);
            int method = (int)(hdr & 0x7);
            int rawSize = (int)(hdr >> 3);
            if (method == 0 && 4 + rawSize <= sectionBytes.Length)
                return sectionBytes.AsSpan(0, 4 + rawSize).ToArray();
            return sectionBytes;
        }

        private static string NormalizeVirtPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim('/');
        }

        private static byte[] ReadBytes(FileStream fs, long offset, int len)
        {
            if (len == 0) return Array.Empty<byte>();
            byte[] buf = new byte[len];
            fs.Seek(offset, SeekOrigin.Begin);
            int done = 0;
            while (done < len)
            {
                int r = fs.Read(buf, done, len - done);
                if (r <= 0) throw new EndOfStreamException();
                done += r;
            }
            return buf;
        }

        private sealed class CleanArcSlotSpec
        {
            public uint NameCrc32;
            public uint NameOffsetInFolder;
            public uint CleanSize;
            public uint CleanDataCrc32;
            public byte SourceKind;
            public uint SourceIndex;
        }

        private sealed class VisualMemberSpec
        {
            public uint BustupArcIdx, RpgArcIdx, WazaArcIdx;
            public uint DonorBustupInstSlot, BustupSize, BustupCrc;
            public uint DonorRpgInstSlot, RpgSize, RpgCrc;
            public uint DonorWazaInstSlot, WazaSize, WazaCrc;
            public int NameHash, NicknameHash, DescHash, Body, Skin, Gender, Year;
            public uint CbTemplateIdx;
            public uint OffName, OffNick, OffBody, OffSkin, OffGender, OffYear, OffDesc;
        }

        private sealed class PlayerSpec
        {
            public int ParamHash, BaseHash, Body, Gender, OrigPosition;
            public uint Flags;
            public int MoveOffset, MoveCount;
            public int CleanFp, CleanTp, CleanKick, CleanDribble, CleanTechnique, CleanBlock, CleanSpeed, CleanStamina, CleanCatch, CleanLuck, CleanFreedom;
            public int CleanAvatarHash;
            public uint CpTemplateIdx;
            public uint[] FieldOffsets = Array.Empty<uint>();
        }

        private sealed class StoryTeamSpec
        {
            public int TeamConfigId;
            public uint Flags;
            public int CleanEmblem1;
            public uint EmblemOffset;
            public int[] CleanPlayers = Array.Empty<int>();
            public uint[] PlayerOffsets = Array.Empty<uint>();
            public uint[] ShirtOffsets = Array.Empty<uint>();
        }

        private sealed class EncountTeamSpec
        {
            public int TeamConfigId;
            public uint Flags;
            public int CleanEmblem1;
            public uint EmblemOffset;
            public int[] CleanPlayers = Array.Empty<int>();
            public uint[] PlayerOffsets = Array.Empty<uint>();
            public uint[] ShirtOffsets = Array.Empty<uint>();
        }

        private sealed class MapSpawnSpec
        {
            public uint TemplateIdx;
            public int LinkedEncountIdx;
            public int Uniform;
            public byte[] SlotDomains = Array.Empty<byte>();
            public int[] CleanSlots = Array.Empty<int>();
            public uint[] SlotOffsets = Array.Empty<uint>();
        }

        private sealed class PalpackSpec
        {
            public uint Eligible;
            public int CleanRecruitedHash;
            public uint RecruitedOffset;
            public int[] PrereqTypes = Array.Empty<int>();
            public int[] CleanPrereqIds = Array.Empty<int>();
            public uint[] PrereqIdOffsets = Array.Empty<uint>();
        }

        private sealed class SkillParamSpec
        {
            public int CleanElement, CleanTp, CleanPower;
            public uint ElemOffset, TpOffset, PowerOffset;
        }

        private sealed class SkillSlotSpec
        {
            public int CleanSkillHash, CleanLevel;
            public uint Flags;
            public uint LevelOffset, HashOffset;
        }

        private sealed class TeamParamSpec
        {
            public uint ModeFlags;
            public int CleanUniform;
            public uint UniformOffset, FormationOffset, CoachOffset, TacticOffset;
        }

        private sealed class ChestSpec
        {
            public uint TemplateIdx;
            public uint LightItemOffset, ShadowItemOffset;
        }
    }
}
