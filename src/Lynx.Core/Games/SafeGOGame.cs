using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lynx.Core.RomFs;
using Lynx.InazumaEleven.Games;
using Lynx.InazumaEleven.Games.GO;
using Lynx.InazumaEleven.Logic;
using StudioElevenLib.Level5.Archive.ARC0;
using static Lynx.InazumaEleven.Games.GO.GOSupport;

namespace Lynx.Core.Games
{
    /// <summary>
    /// Safe, cross-platform subclass of <see cref="Game"/> for Inazuma Eleven GO Light &amp; Shadow.
    /// Reuses 100% of Lynx's binary mappers, logic interfaces, and stat calculator while opening
    /// <c>ie_a.fa</c> strictly read-only and blocking destructive in-place <see cref="Save()"/> calls.
    /// </summary>
    public sealed class SafeGOGame : Game, IDisposable
    {
        public override string Name => "Inazuma Eleven GO: Luz y Sombra";

        public override IStatCalculator StatCalculator => GOStatCalculator.Instance;

        public string SourceArchivePath { get; }

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

        public void SaveCopyTo(string destinationIeAFaPath, IProgress<int> progress = null)
        {
            Arc0SafeArchive.SaveCopyTo(IE_A, destinationIeAFaPath, SourceArchivePath, progress);
        }

        /// <summary>
        /// Returns only STORY_TEAM_INFO records (disambiguating IStoryTeamInfo : IEncountTeamInfo).
        /// </summary>
        public IStoryTeamInfo[] GetStoryTeamsOnly()
        {
            return GetTeamConfig().OfType<IStoryTeamInfo>().ToArray();
        }

        /// <summary>
        /// Returns only ENCOUNT_TEAM_INFO records, excluding IStoryTeamInfo subclasses.
        /// </summary>
        public IEncountTeamInfo[] GetEncounterTeamsOnly()
        {
            return GetTeamConfig()
                .Where(t => t is IEncountTeamInfo && !(t is IStoryTeamInfo))
                .Cast<IEncountTeamInfo>()
                .ToArray();
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
