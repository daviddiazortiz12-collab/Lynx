using System;
using System.Collections.Generic;
using IEGORandomizer.Engine.Validation;
using Lynx.Core.RomFs;
using Lynx.Core.RomFs.Cia;

namespace IEGORandomizer.Engine.Modules
{
    /// <summary>
    /// Structured summary produced by a randomization run.
    /// Designed to be completely safe to display in Blind-Play mode without leaking spoilers.
    /// </summary>
    public sealed class RandomizationExecutionReport
    {
        public string Seed { get; set; } = string.Empty;
        public string SeedFingerprint { get; set; } = string.Empty;
        public GameEdition EffectiveEdition { get; set; }
        public string ResolvedTitleId { get; set; } = string.Empty;
        public bool BlindPlayMode { get; set; } = true;
        public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

        // Non-spoiler aggregate counters
        public int PlayablePoolCount { get; set; }
        public int QuarantinedModelsCount { get; set; }
        public int ProtectedStoryCharactersCount { get; set; }
        public int ProtectedKeyItemsCount { get; set; }
        public int ScannedNutbScriptsCount { get; set; }

        public int RandomizedTeamPlayerSlotsCount { get; set; }
        public int RandomizedPachangaPlayerSlotsCount { get; set; }
        public int RandomizedPalpackCardsCount { get; set; }
        public int RandomizedPlayerStatsCount { get; set; }
        public int RandomizedPlayerPositionsCount { get; set; }
        public int RandomizedPlayerElementsCount { get; set; }
        public int RandomizedPlayerAvatarsCount { get; set; }

        public int RandomizedSkillSlotsCount { get; set; }
        public int RandomizedAvatarSkillsCount { get; set; }
        public int RandomizedSkillParamsCount { get; set; }
        public int GuaranteedInitialStarterCount { get; set; }
        public int DynamicStorySkillMappingsCount { get; set; }
        public int PatchedNutbScriptsCount { get; set; }
        public int PatchedNutbSkillCallSitesCount { get; set; }
        public int InjectedTemporaryBattleSkillNodesCount { get; set; }

        public int RandomizedTeamsCount { get; set; }
        public int RandomizedPachangaTeamsCount { get; set; }

        public int RandomizedCoachesCount { get; set; }
        public int RandomizedManagersCount { get; set; }
        public int RandomizedStoryNpcsCount { get; set; }
        public int RandomizedOverworldMapNpcsCount { get; set; }

        public int RandomizedOverworldChestsCount { get; set; }
        public int ProtectedOverworldChestsSkipped { get; set; }
        public int RandomizedRouteChestsCount { get; set; }
        public int RandomizedMatchDropsCount { get; set; }
        public int RandomizedShopEntriesCount { get; set; }

        public int RandomizedTeamUniformsCount { get; set; }

        public bool SourceImmutabilityVerified { get; set; }
        public bool OutputIntegrityVerified { get; set; }
        public Arc0SaveSummary Arc0WriteTelemetry { get; set; }
        public PersistedArchiveDiffSummary PersistedDiffTelemetry { get; set; }
        public CiaRebuildTelemetry CiaBuildTelemetry { get; set; }
        public CiaVerificationResult CiaVerification { get; set; }
        public string DirectCitraInstalledPath { get; set; } = string.Empty;

        public List<string> ExportedPaths { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> ValidationMessages { get; } = new();

        /// <summary>
        /// Populated ONLY when <see cref="BlindPlayMode"/> is <c>false</c>.
        /// Always empty in Blind-Play mode so spoilers never leak to UI or disk.
        /// </summary>
        public List<string> SpoilerLogEntries { get; } = new();
    }
}
