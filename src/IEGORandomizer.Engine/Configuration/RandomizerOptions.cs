using Lynx.Core.RomFs;

namespace IEGORandomizer.Engine.Configuration
{
    /// <summary>
    /// Complete, serializable configuration for a single IEGO Randomizer generation run.
    /// Completely decoupled from any UI technology.
    /// </summary>
    public sealed class RandomizerOptions
    {
        public string Seed { get; set; } = string.Empty;
        public GameEdition Edition { get; set; } = GameEdition.Auto;
        public string PreferredLanguageCode { get; set; } = "es";

        /// <summary>
        /// Optional path to local user-supplied <c>aes_keys.txt</c> when opening encrypted .cia / NCCH dumps.
        /// </summary>
        public string AesKeysPath { get; set; } = string.Empty;

        /// <summary>
        /// When enabled (default), suppresses spoiler details from the user-facing summary
        /// and does not write a spoiler log file so the user can play completely blind.
        /// </summary>
        public bool BlindPlayMode { get; set; } = true;

        public PlayersRandomizerOptions Players { get; set; } = new();
        public SkillsRandomizerOptions Skills { get; set; } = new();
        public TeamsRandomizerOptions Teams { get; set; } = new();
        public RewardsRandomizerOptions Rewards { get; set; } = new();
        public UniformsRandomizerOptions Uniforms { get; set; } = new();
        public ProtectionRandomizerOptions Protection { get; set; } = new();
        public ExportRandomizerOptions Export { get; set; } = new();
        public CiaPackageOptions Cia { get; set; } = new();
    }

    public sealed class PlayersRandomizerOptions
    {
        public bool Enabled { get; set; } = true;
        public bool RandomizeTeamAndRivalSlots { get; set; } = true;
        public bool RandomizePachangaPlayers { get; set; } = true;
        public bool RandomizePalpackRecruits { get; set; } = true;
        public bool SwapStoryPlayerVisualIdentities { get; set; } = false;
        public bool EnforceStrictDerangement { get; set; } = true;
        public bool PreserveGoalkeeperRolesInRosters { get; set; } = true;
        public bool RandomizePositions { get; set; } = true;
        public bool RandomizeElements { get; set; } = true;
        public bool RandomizeBaseStats { get; set; } = true;
        public int StatVariancePercent { get; set; } = 25;
        public bool RandomizeFightingSpirits { get; set; } = true;
        public int FightingSpiritChancePercent { get; set; } = 25;
    }

    public sealed class SkillsRandomizerOptions
    {
        public bool Enabled { get; set; } = true;
        public bool RandomizeInitialSkills { get; set; } = true;
        public bool RandomizeLevelUpLearnsets { get; set; } = true;
        public bool RandomizeTalents { get; set; } = true;
        public bool RandomizeFightingSpiritSkills { get; set; } = true;
        public bool RandomizeSkillParameters { get; set; } = false;
        public int SkillParamVariancePercent { get; set; } = 20;
        public bool EnforcePositionCompatibility { get; set; } = true;
        public bool EnsureUsableInitialTp { get; set; } = true;
    }

    public sealed class TeamsRandomizerOptions
    {
        public bool Enabled { get; set; } = true;
        public bool RandomizeStoryAndRouteTeams { get; set; } = true;
        public bool RandomizePachangaTeams { get; set; } = true;
        public bool RandomizeKitNumbers { get; set; } = true;
        public bool RandomizeEmblems { get; set; } = true;
        public bool RandomizeFormations { get; set; } = true;
        public bool RandomizeCoaches { get; set; } = true;
        public bool RandomizeTactics { get; set; } = true;
        public bool RandomizeLevels { get; set; } = false;
        public int MaxLevelDelta { get; set; } = 4;
        public bool RandomizeEquipment { get; set; } = true;
    }

    public sealed class RewardsRandomizerOptions
    {
        public bool Enabled { get; set; } = true;
        public bool RandomizeOverworldChests { get; set; } = true;
        public bool RandomizeCompetitionRouteChests { get; set; } = true;
        public bool RandomizeMatchDrops { get; set; } = true;
        public bool RandomizePachangaDrops { get; set; } = true;
        public bool RandomizeShopItems { get; set; } = false;
    }

    public sealed class UniformsRandomizerOptions
    {
        public bool Enabled { get; set; } = true;
        public bool RandomizeRivalUniforms { get; set; } = true;
        public bool RandomizePachangaUniforms { get; set; } = true;
        public bool RandomizeObtainableUniformRewards { get; set; } = true;
    }

    public sealed class ProtectionRandomizerOptions
    {
        public bool EnableStorySoftlockProtection { get; set; } = true;
        public bool AuditNutbEventScripts { get; set; } = true;
        public bool ProtectKeyItemsAndFuncpointChests { get; set; } = true;
        public bool EnforceVisualAssetCoherence { get; set; } = true;
    }

    public sealed class ExportRandomizerOptions
    {
        public bool ExportStandaloneIeAFa { get; set; } = true;
        public bool ExportCitraModStructure { get; set; } = true;
        public bool ExportLuma3dsLayeredFsStructure { get; set; } = true;
        public string CustomTitleIdOverride { get; set; } = string.Empty;
        public string DirectCitraModsRootPath { get; set; } = string.Empty;
    }

    public sealed class CiaPackageOptions
    {
        /// <summary>
        /// When enabled (default), bumps the TMD TitleVersion above the source .cia (e.g. V.0.0.0 -&gt; V.1.1.2)
        /// while keeping the base TitleID and SaveDataSize identical so FBI updates the game in-place on
        /// Nintendo 2DS/3DS without deleting existing save data (00000001.sav).
        /// </summary>
        public bool BumpTitleVersionForFbiUpdate { get; set; } = true;

        /// <summary>
        /// Optional explicit target TitleVersion string (e.g. "1.1.2", "1.2.0", "1042", "0x0412").
        /// </summary>
        public string CustomTitleVersionOverride { get; set; } = string.Empty;

        /// <summary>
        /// When enabled during .cia generation, also writes the companion Luma3DS LayeredFS file
        /// (&lt;outputFolder&gt;/Luma3DS/titles/&lt;TitleID&gt;/romfs/ie_a.fa) for fast SD patching without reinstalling the 3 GB .cia.
        /// </summary>
        public bool ExportCompanionLuma3dsPatch { get; set; } = false;
    }
}
