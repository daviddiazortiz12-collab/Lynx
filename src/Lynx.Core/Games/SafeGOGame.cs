using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lynx.Core.Binary;
using Lynx.Core.RomFs;
using Lynx.InazumaEleven.Games;
using Lynx.InazumaEleven.Games.GO;
using Lynx.InazumaEleven.Logic;
using StudioElevenLib.Level5.Archive.ARC0;
using StudioElevenLib.Level5.Binary;
using StudioElevenLib.Level5.Binary.Collections;
using static Lynx.InazumaEleven.Games.GO.GOSupport;

namespace Lynx.Core.Games
{
    /// <summary>
    /// Safe, cross-platform subclass of <see cref="Game"/> for Inazuma Eleven GO Light &amp; Shadow.
    /// Opens <c>ie_a.fa</c> strictly read-only, blocks destructive in-place <see cref="Save()"/> calls,
    /// and routes all <c>.cfg.bin</c> reads and writes through <see cref="Level5CfgBinAdapter"/> so
    /// polymorphic Level-5 <c>cfgBinArgReadHash</c> (<c>0x0012CFF0</c>) string/CRC32 identifiers and
    /// retail <c>CfgValueType</c> tags are preserved without data loss.
    /// </summary>
    public sealed class SafeGOGame : Game, IDisposable
    {
        public override string Name => "Inazuma Eleven GO: Luz y Sombra";

        public override IStatCalculator StatCalculator => GOStatCalculator.Instance;

        public string SourceArchivePath { get; }

        public Arc0SaveSummary LastSaveSummary { get; private set; }

        public SafeGOGame(string archiveFilePath, string preferredLanguageCode = null)
        {
            EncodingInitializer.EnsureRegistered();
            SourceArchivePath = Path.GetFullPath(archiveFilePath);
            IE_A = Arc0SafeArchive.OpenReadOnly(SourceArchivePath);
            LanguageCode = ResolveLanguageCode(IE_A, preferredLanguageCode);
            GetGameFiles();
            InitializeEntityTypes();
        }

        public SafeGOGame(ARC0 openArchive, string protectedSourcePath = null, string preferredLanguageCode = null)
        {
            EncodingInitializer.EnsureRegistered();
            IE_A = openArchive ?? throw new ArgumentNullException(nameof(openArchive));
            SourceArchivePath = protectedSourcePath ?? string.Empty;
            LanguageCode = ResolveLanguageCode(IE_A, preferredLanguageCode);
            GetGameFiles();
            InitializeEntityTypes();
        }

        private void InitializeEntityTypes()
        {
            TypeCharabase = typeof(CharaBase);
            TypeCharaparam = typeof(CharaParam);
            TypeTrainingUID = typeof(TrainingUD);
            TypeAvatar = typeof(Avatar);
            TypeAvatarTimeGrowth = typeof(AvatarTimeGrowth);
            TypeSkillConfig = typeof(SkillConfig);
            TypeSkillTable = typeof(SkillTable);
            TypeNPCPreset = typeof(NPCPreset);
            TypeNPCAppear = typeof(NPCAppear);
            TypeNPCBase = typeof(NPCBase);
            TypeTalkInfo = typeof(TalkInfo);
            TypeTalkConfig = typeof(TalkConfig);
            TypeShopConfig = typeof(ShopConfig);
            TypeCommunityInfo = typeof(CommunityInfo);
            TypeRouteConfig = typeof(RouteConfig);
            TypeEncountTeamInfo = typeof(EncountTeamInfo);
            TypeStoryTeamInfo = typeof(StoryTeamInfo);
            TypeTeamParamInfo = typeof(TeamParamInfo);
            TypeSoccerInfo = typeof(SoccerInfo);
            TypeItemConfig = typeof(ItemConfig);
            TypeItemConfigUniform = typeof(ItemConfigUniform);
            TypeItemConfigAvatar = typeof(ItemConfigAvatar);
            TypeItemConfigDirector = typeof(ItemConfigDirector);
            TypeItemConfigPalpackCard = typeof(ItemConfigPalpackCard);
            TypeItemReference = typeof(ItemReference);
        }

        public override void GetGameFiles()
        {
            Files = new Dictionary<string, GameSupports.GameFile>
            {
                // Core game tables
                { "chara_base", new GameSupports.GameFile(IE_A, "/data/res/character/chara_base.cfg.bin") },
                { "chara_param", new GameSupports.GameFile(IE_A, "/data/res/character/chara_param.cfg.bin") },
                { "item_config", new GameSupports.GameFile(IE_A, "/data/res/item/item_config.cfg.bin") },
                { "skill_config", new GameSupports.GameFile(IE_A, "/data/res/skill/skill_config.cfg.bin") },
                { "skill_table", new GameSupports.GameFile(IE_A, "/data/res/character/skill_table.cfg.bin") },
                { "community_config", new GameSupports.GameFile(IE_A, "/data/res/shop/community_config.cfg.bin") },
                { "soccer_config", new GameSupports.GameFile(IE_A, "/data/res/soccer/soccer_config.cfg.bin") },
                { "team_param", new GameSupports.GameFile(IE_A, "/data/res/team/team_param.cfg.bin") },
                { "team_config", new GameSupports.GameFile(IE_A, "/data/res/team/team_config.cfg.bin") },

                // Asset & map directories
                { "map", new GameSupports.GameFile(IE_A, "/data/map/") },
                { "shop", new GameSupports.GameFile(IE_A, "/data/res/shop") },
                { "soccer", new GameSupports.GameFile(IE_A, "/data/res/soccer/") },
                { "face", new GameSupports.GameFile(IE_A, "/data/bustup/face") },
                { "faceAvatar", new GameSupports.GameFile(IE_A, "/data/bustup/avatar") },
                { "modelRpgPlayer", new GameSupports.GameFile(IE_A, "/data/chr/model/rpg/face") },
                { "modelWazaPlayer", new GameSupports.GameFile(IE_A, "/data/chr/model/waza/face") },
                { "modelRpgNPC", new GameSupports.GameFile(IE_A, "/data/chr/model/rpg/npc") },
                { "modelWazaNPC", new GameSupports.GameFile(IE_A, "/data/chr/model/waza/npc") },
                { "eventScript", new GameSupports.GameFile(IE_A, "/data/script/event") },
                { "script", new GameSupports.GameFile(IE_A, "/data/script/") },
                { "modelRPGShoes", new GameSupports.GameFile(IE_A, "/data/chr/model/rpg/shoes") },
                { "modelRPGBody", new GameSupports.GameFile(IE_A, "/data/chr/model/rpg/body") },
                { "modelRPGGloves", new GameSupports.GameFile(IE_A, "/data/chr/model/rpg/glove") },
                { "emblem", new GameSupports.GameFile(IE_A, "/data/emblem/") },

                // Localized text tables
                { "encount_area_text", new GameSupports.GameFile(IE_A, "/data/res/text/encount_area_text_" + LanguageCode + ".cfg.bin") },
                { "system_text", new GameSupports.GameFile(IE_A, "/data/res/text/system_text_" + LanguageCode + ".cfg.bin") },
                { "chara_text", new GameSupports.GameFile(IE_A, "/data/res/text/chara_text_" + LanguageCode + ".cfg.bin") },
                { "item_text", new GameSupports.GameFile(IE_A, "/data/res/text/item_text_" + LanguageCode + ".cfg.bin") },
                { "skill_text", new GameSupports.GameFile(IE_A, "/data/res/text/skill_text_" + LanguageCode + ".cfg.bin") },
                { "kiznax_hint_text", new GameSupports.GameFile(IE_A, "/data/res/text/kiznax_hint_text_" + LanguageCode + ".cfg.bin") },
                { "team_text", new GameSupports.GameFile(IE_A, "/data/res/text/team_text_" + LanguageCode + ".cfg.bin") },
                { "troute_text", new GameSupports.GameFile(IE_A, "/data/res/text/troute_text_" + LanguageCode + ".cfg.bin") },
                { "community_text", new GameSupports.GameFile(IE_A, "/data/txt/43010000_" + LanguageCode + ".cfg.bin") },
                { "shop_text", new GameSupports.GameFile(IE_A, "/data/txt/43020000_" + LanguageCode + ".cfg.bin") }
            };
        }

        /// <summary>
        /// Explicitly blocked to guarantee the original <c>ie_a.fa</c> is never overwritten in place.
        /// Call <see cref="SaveCopyTo"/> with a separate output destination path instead.
        /// </summary>
        public override void Save()
        {
            throw new InvalidOperationException(
                "In-place Save() is disabled by safety policy. Use SaveCopyTo(destinationFilePath) to write to a separate output directory.");
        }

        public Arc0SaveSummary SaveCopyTo(string destinationIeAFaPath, IProgress<int> progress = null)
        {
            LastSaveSummary = Arc0SafeArchive.SaveCopyTo(IE_A, destinationIeAFaPath, SourceArchivePath, progress);
            return LastSaveSummary;
        }

        public new ICharabase[] GetCharabase()
        {
            var cfg = OpenCfgFile("chara_base");
            return Level5CfgBinAdapter.ReadEntries<CharaBase>(cfg.Entries, "CHARA_BASE_INFO", "chara_base.cfg.bin")
                .Cast<ICharabase>()
                .ToArray();
        }

        public new void SaveCharaBase(ICharabase[] charabase)
        {
            var cfg = OpenCfgFile("chara_base");
            Level5CfgBinAdapter.UpdateEntriesInPlace(
                cfg.Entries,
                "CHARA_BASE_INFO_BEGIN",
                "CHARA_BASE_INFO",
                charabase,
                "chara_base.cfg.bin");
            WriteGameFileContent("chara_base", cfg.Save());
        }

        public new ICharaparam[] GetCharaparams()
        {
            var cfg = OpenCfgFile("chara_param");
            return Level5CfgBinAdapter.ReadEntries<CharaParam>(cfg.Entries, "CHARA_PARAM_INFO", "chara_param.cfg.bin")
                .Cast<ICharaparam>()
                .ToArray();
        }

        public new void SaveCharaparams(ICharaparam[] charaparams)
        {
            var cfg = OpenCfgFile("chara_param");
            Level5CfgBinAdapter.UpdateEntriesInPlace(
                cfg.Entries,
                "CHARA_PARAM_INFO_BEGIN",
                "CHARA_PARAM_INFO",
                charaparams,
                "chara_param.cfg.bin");
            WriteGameFileContent("chara_param", cfg.Save());
        }

        public new ISkillConfig[] GetSkillConfigs(bool emptySkillConfig = false)
        {
            var cfg = OpenCfgFile("skill_config");
            var list = Level5CfgBinAdapter.ReadEntries<SkillConfig>(cfg.Entries, "SKILL_CONFIG_INFO", "skill_config.cfg.bin")
                .Cast<ISkillConfig>()
                .ToList();

            if (emptySkillConfig)
            {
                list.Add(new SkillConfig());
            }

            return list.ToArray();
        }

        public new void SaveSkillConfigs(ISkillConfig[] skills)
        {
            var cfg = OpenCfgFile("skill_config");
            Level5CfgBinAdapter.UpdateEntriesInPlace(
                cfg.Entries,
                "SKILL_CONFIG_INFO_BEGIN",
                "SKILL_CONFIG_INFO",
                skills,
                "skill_config.cfg.bin");
            WriteGameFileContent("skill_config", cfg.Save());
        }

        public new ISkillTable[] GetSkillTable()
        {
            var cfg = OpenCfgFile("skill_table");
            return Level5CfgBinAdapter.ReadEntries<SkillTable>(cfg.Entries, "SKILL_TABLE", "skill_table.cfg.bin")
                .Cast<ISkillTable>()
                .ToArray();
        }

        public new void SaveSkillTable(ISkillTable[] skilltable)
        {
            var cfg = OpenCfgFile("skill_table");
            Level5CfgBinAdapter.UpdateEntriesInPlace(
                cfg.Entries,
                "SKILL_TABLE_BEGIN",
                "SKILL_TABLE",
                skilltable,
                "skill_table.cfg.bin");
            WriteGameFileContent("skill_table", cfg.Save());
        }

        public new IAvatar[] GetAvatars(bool emptyAvatar = false)
        {
            var cfg = OpenCfgFile("item_config");
            var list = Level5CfgBinAdapter.ReadEntries<Avatar>(cfg.Entries, "ITEM_AVATAR", "item_config.cfg.bin")
                .Cast<IAvatar>()
                .ToList();

            if (emptyAvatar)
            {
                list.Add(new Avatar());
            }

            return list.ToArray();
        }

        public new void SaveAvatars(IAvatar[] avatars)
        {
            var cfg = OpenCfgFile("item_config");
            Level5CfgBinAdapter.UpdateEntriesInPlace(
                cfg.Entries,
                "ITEM_AVATAR_BEGIN",
                "ITEM_AVATAR",
                avatars,
                "item_config.cfg.bin");
            WriteGameFileContent("item_config", cfg.Save());
        }

        public new ISoccerInfo[] GetSoccers()
        {
            var cfg = OpenCfgFile("soccer_config");
            return Level5CfgBinAdapter.ReadEntries<SoccerInfo>(cfg.Entries, "SOCCER_INFO", "soccer_config.cfg.bin")
                .Cast<ISoccerInfo>()
                .ToArray();
        }

        public new ITeamParamInfo[] GetTeamParams()
        {
            var cfg = OpenCfgFile("team_param");
            return Level5CfgBinAdapter.ReadEntries<TeamParamInfo>(cfg.Entries, "TEAM_PARAM_INFO", "team_param.cfg.bin")
                .Cast<ITeamParamInfo>()
                .ToArray();
        }

        public new void SaveTeamParams(ITeamParamInfo[] teams)
        {
            var cfg = OpenCfgFile("team_param");
            Level5CfgBinAdapter.UpdateEntriesInPlace(
                cfg.Entries,
                "TEAM_PARAM_INFO_BEGIN",
                "TEAM_PARAM_INFO",
                teams,
                "team_param.cfg.bin");
            WriteGameFileContent("team_param", cfg.Save());
        }

        public IStoryTeamInfo[] GetStoryTeamsOnly()
        {
            var cfg = OpenCfgFile("team_config");
            return Level5CfgBinAdapter.ReadEntries<StoryTeamInfo>(cfg.Entries, "STORY_TEAM_INFO", "team_config.cfg.bin")
                .Cast<IStoryTeamInfo>()
                .ToArray();
        }

        public IEncountTeamInfo[] GetEncounterTeamsOnly()
        {
            var cfg = OpenCfgFile("team_config");
            return Level5CfgBinAdapter.ReadEntries<EncountTeamInfo>(cfg.Entries, "ENCOUNT_TEAM_INFO", "team_config.cfg.bin")
                .Cast<IEncountTeamInfo>()
                .ToArray();
        }

        public new void SaveTeamConfig(IStoryTeamInfo[] storyTeams, IEncountTeamInfo[] encounterTeams)
        {
            var cfg = OpenCfgFile("team_config");
            Level5CfgBinAdapter.UpdateEntriesInPlace(
                cfg.Entries,
                "STORY_TEAM_INFO_BEGIN",
                "STORY_TEAM_INFO",
                storyTeams,
                "team_config.cfg.bin");
            Level5CfgBinAdapter.UpdateEntriesInPlace(
                cfg.Entries,
                "ENCOUNT_TEAM_INFO_BEGIN",
                "ENCOUNT_TEAM_INFO",
                encounterTeams,
                "team_config.cfg.bin");
            WriteGameFileContent("team_config", cfg.Save());
        }

        public new IItemConfig[] GetItems(string itemType)
        {
            var cfg = OpenCfgFile("item_config");
            const string ctx = "item_config.cfg.bin";

            return itemType switch
            {
                "equipment" => Level5CfgBinAdapter.ReadEntries<ItemConfig>(cfg.Entries, "ITEM_EQUIPMENT", ctx).Cast<IItemConfig>().ToArray(),
                "consumable" => Level5CfgBinAdapter.ReadEntries<ItemConfig>(cfg.Entries, "ITEM_CONSUME", ctx).Cast<IItemConfig>().ToArray(),
                "important" => Level5CfgBinAdapter.ReadEntries<ItemConfig>(cfg.Entries, "ITEM_IMPORTANT", ctx).Cast<IItemConfig>().ToArray(),
                "uniform" => Level5CfgBinAdapter.ReadEntries<ItemConfigUniform>(cfg.Entries, "ITEM_UNIFORM", ctx).Cast<IItemConfig>().ToArray(),
                "kizunax" => Level5CfgBinAdapter.ReadEntries<ItemConfigPalpackCard>(cfg.Entries, "ITEM_KIZUNAX", ctx).Cast<IItemConfig>().ToArray(),
                "avatar" => Level5CfgBinAdapter.ReadEntries<ItemConfigAvatar>(cfg.Entries, "ITEM_AVATAR", ctx).Cast<IItemConfig>().ToArray(),
                "director" => Level5CfgBinAdapter.ReadEntries<ItemConfigDirector>(cfg.Entries, "ITEM_DIRECTOR", ctx).Cast<IItemConfig>().ToArray(),
                "all" => Level5CfgBinAdapter.ReadEntries<ItemConfig>(cfg.Entries, "ITEM_EQUIPMENT", ctx).Cast<IItemConfig>()
                    .Concat(Level5CfgBinAdapter.ReadEntries<ItemConfig>(cfg.Entries, "ITEM_CONSUME", ctx))
                    .Concat(Level5CfgBinAdapter.ReadEntries<ItemConfig>(cfg.Entries, "ITEM_IMPORTANT", ctx))
                    .Concat(Level5CfgBinAdapter.ReadEntries<ItemConfigAvatar>(cfg.Entries, "ITEM_AVATAR", ctx))
                    .Concat(Level5CfgBinAdapter.ReadEntries<ItemConfigDirector>(cfg.Entries, "ITEM_DIRECTOR", ctx))
                    .Concat(Level5CfgBinAdapter.ReadEntries<ItemConfigUniform>(cfg.Entries, "ITEM_UNIFORM", ctx))
                    .Concat(Level5CfgBinAdapter.ReadEntries<ItemConfigPalpackCard>(cfg.Entries, "ITEM_KIZUNAX", ctx))
                    .ToArray(),
                _ => null
            };
        }

        public new void SaveItems<T>(T[] items) where T : class, IItemConfig
        {
            (string beginName, string entryName) = typeof(T) switch
            {
                Type t when t == typeof(ItemConfigPalpackCard) => ("ITEM_KIZUNAX_BEGIN", "ITEM_KIZUNAX"),
                Type t when t == typeof(ItemConfigDirector) => ("ITEM_DIRECTOR_BEGIN", "ITEM_DIRECTOR"),
                Type t when t == typeof(ItemConfigUniform) => ("ITEM_UNIFORM_BEGIN", "ITEM_UNIFORM"),
                Type t when t == typeof(ItemConfigAvatar) => ("ITEM_AVATAR_BEGIN", "ITEM_AVATAR"),
                _ => throw new NotSupportedException(
                    $"SafeGOGame.SaveItems<{typeof(T).Name}> rechaza la reconstrucción destructiva de ITEM_REF. Use actualización in-place explícita.")
            };

            var cfg = OpenCfgFile("item_config");
            Level5CfgBinAdapter.UpdateEntriesInPlace(
                cfg.Entries,
                beginName,
                entryName,
                items,
                "item_config.cfg.bin");
            WriteGameFileContent("item_config", cfg.Save());
        }

        private void WriteGameFileContent(string fileKey, byte[] newBytes)
        {
            if (Files != null && Files.TryGetValue(fileKey, out GameSupports.GameFile gameFile) && gameFile?.File is ARC0 arc0)
            {
                Arc0SafeArchive.WriteFileAtPath(arc0.Directory, gameFile.Path, newBytes);
            }

            var sub = GetFile(fileKey);
            if (sub != null)
            {
                Arc0SafeArchive.SetSubStreamContent(sub, newBytes);
            }
        }

        private CfgBin<CfgTreeNode> OpenCfgFile(string fileKey)
        {
            byte[] content = null;
            if (Files != null && Files.TryGetValue(fileKey, out GameSupports.GameFile gameFile) && gameFile?.File is ARC0 arc0)
            {
                content = Arc0SafeArchive.TryReadFileFromPath(arc0.Directory, gameFile.Path);
            }

            content ??= GetFileContent(fileKey)
                ?? throw new FileNotFoundException($"Required game table '{fileKey}' not found in ie_a.fa.");

            var cfg = new CfgBin<CfgTreeNode>();
            cfg.Open(content);
            return cfg;
        }

        public static IReadOnlyList<string> DetectAvailableLanguageCodes(ARC0 archive)
        {
            var found = new List<string>();
            if (archive?.Directory == null || !archive.Directory.IsFolderExists("/data/res/text"))
                return found;

            var textFolder = archive.Directory.GetFolderFromFullPath("/data/res/text");
            foreach (string code in new[] { "es", "en", "fr", "de", "it", "ja" })
            {
                if (textFolder.Files.ContainsKey($"chara_text_{code}.cfg.bin") ||
                    textFolder.Files.ContainsKey($"system_text_{code}.cfg.bin"))
                {
                    found.Add(code);
                }
            }
            return found;
        }

        public static string ResolveLanguageCode(ARC0 archive, string preferredLanguageCode)
        {
            var available = DetectAvailableLanguageCodes(archive);
            if (!string.IsNullOrWhiteSpace(preferredLanguageCode))
            {
                string trimmed = preferredLanguageCode.Trim().ToLowerInvariant();
                if (GOSupport.AvailableLanguages.TryGetValue(preferredLanguageCode, out string mapped))
                {
                    trimmed = mapped;
                }
                if (available.Count == 0 || available.Contains(trimmed))
                {
                    return trimmed;
                }
            }

            if (available.Contains("es"))
                return "es";
            if (available.Contains("en"))
                return "en";
            if (available.Count > 0)
                return available[0];

            return "es";
        }

        public void Dispose()
        {
            try
            {
                IE_A?.Close();
            }
            catch
            {
                // Ignore close exceptions during disposal
            }
        }
    }
}
