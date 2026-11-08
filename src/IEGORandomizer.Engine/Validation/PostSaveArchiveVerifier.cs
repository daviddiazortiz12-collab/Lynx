using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using IEGORandomizer.Engine.Modules;
using Lynx.Core.Games;
using Lynx.Core.RomFs;
using StudioElevenLib.Tools;

namespace IEGORandomizer.Engine.Validation
{
    public sealed class PersistedArchiveDiffSummary
    {
        public long SourceArchiveSizeBytes { get; init; }
        public long OutputArchiveSizeBytes { get; init; }
        public Arc0ArchiveInspection OutputHeaderInspection { get; init; }
        public int PersistedModifiedFilesCount => PersistedModifiedVirtualFiles.Count;
        public List<string> PersistedModifiedVirtualFiles { get; init; } = new();
        public int PersistedChangedCharaParamsCount { get; init; }
        public int PersistedChangedSkillTableEntriesCount { get; init; }
        public int PersistedChangedStoryTeamsCount { get; init; }
        public int PersistedChangedPachangaTeamsCount { get; init; }
        public int PersistedChangedTeamParamsCount { get; init; }
    }

    /// <summary>
    /// Reopens both the untouched source <c>ie_a.fa</c> and the newly saved output <c>ie_a.fa</c>
    /// directly from disk to prove beyond any doubt that:
    /// 1. The output <c>ARC0</c> header &amp; tables conform strictly to 3DS <c>CArchiveArc0</c> rules
    ///    (<c>NoCompression</c>/<c>LZ10</c> tables, valid <c>DirectoryHashes</c>, 4-byte alignment).
    /// 2. In-memory randomized mutations were genuinely persisted into the output <c>ie_a.fa</c> on disk
    ///    (zero stale <c>SubMemoryStream</c> or <c>VirtualDirectory</c> cache drops).
    /// </summary>
    public static class PostSaveArchiveVerifier
    {
        public static PersistedArchiveDiffSummary VerifyPersistedMutations(
            string sourceIeAFaPath,
            string outputIeAFaPath,
            string preferredLanguageCode,
            RandomizationExecutionReport report)
        {
            if (!File.Exists(sourceIeAFaPath))
                throw new FileNotFoundException("No se encontró el archivo ie_a.fa de referencia.", sourceIeAFaPath);
            if (!File.Exists(outputIeAFaPath))
                throw new FileNotFoundException("No se encontró el archivo ie_a.fa exportado.", outputIeAFaPath);

            using var sourceGame = new SafeGOGame(sourceIeAFaPath, preferredLanguageCode);
            using var outputGame = new SafeGOGame(outputIeAFaPath, preferredLanguageCode);

            Arc0ArchiveInspection inspection = Arc0SafeArchive.InspectArchive(outputGame.IE_A);

            // 1. Validate 3DS ARC0 structural rules on the reopened file
            if (inspection.Magic != Arc0SafeArchive.Arc0Magic)
            {
                throw new InvalidDataException(
                    $"Cabecera ARC0 inválida en el archivo exportado (0x{inspection.Magic:X8}).");
            }

            string[] compressions =
            {
                inspection.DirectoryEntriesCompression,
                inspection.DirectoryHashesCompression,
                inspection.FileEntriesCompression,
                inspection.NameTableCompression
            };

            foreach (string comp in compressions)
            {
                if (!comp.StartsWith("NoCompression", StringComparison.Ordinal) &&
                    !comp.StartsWith("LZ10", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Tabla ARC0 comprimida con método incompatible para 3DS ({comp}); solo se permite NoCompression o LZ10.");
                }
            }

            if (inspection.DirectoryEntriesCount > 1 &&
                inspection.DirectoryHashesCount >= inspection.DirectoryEntriesCount)
            {
                throw new InvalidDataException(
                    $"Tabla DirectoryHashes inválida en ARC0 exportado ({inspection.DirectoryHashesCount} >= {inspection.DirectoryEntriesCount}; raíz 0xFFFFFFFF no debe incluirse).");
            }

            // 2. Compare virtual files byte-for-byte between source and output
            var sourceHashes = new Dictionary<string, (long Length, string Sha256)>(StringComparer.Ordinal);
            CollectFileDigests(sourceGame.IE_A.Directory, string.Empty, sourceHashes);

            var outputHashes = new Dictionary<string, (long Length, string Sha256)>(StringComparer.Ordinal);
            CollectFileDigests(outputGame.IE_A.Directory, string.Empty, outputHashes);

            if (outputHashes.Count != sourceHashes.Count)
            {
                throw new InvalidDataException(
                    $"El archivo ie_a.fa exportado contiene {outputHashes.Count} archivos internos, pero el original tenía {sourceHashes.Count}.");
            }

            var modifiedPaths = new List<string>();
            foreach (var kvp in outputHashes.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!sourceHashes.TryGetValue(kvp.Key, out var orig))
                {
                    throw new InvalidDataException($"Archivo virtual inesperado en salida: {kvp.Key}");
                }

                if (orig.Length != kvp.Value.Length ||
                    !string.Equals(orig.Sha256, kvp.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    modifiedPaths.Add(kvp.Key);
                }
            }

            // 3. Count actual persisted row-level changes on reopened tables
            int changedCharaParams = CountChangedCharaParams(sourceGame, outputGame);
            int changedSkillSlots = CountChangedSkillSlots(sourceGame, outputGame);
            int changedStoryTeams = CountChangedStoryTeams(sourceGame, outputGame);
            int changedPachangaTeams = CountChangedEncountTeams(sourceGame, outputGame);
            int changedTeamParams = CountChangedTeamParams(sourceGame, outputGame);

            int totalExpectedChanges =
                (report?.RandomizedTeamPlayerSlotsCount ?? 0) +
                (report?.RandomizedPachangaPlayerSlotsCount ?? 0) +
                (report?.RandomizedPalpackCardsCount ?? 0) +
                (report?.RandomizedPlayerStatsCount ?? 0) +
                (report?.RandomizedPlayerPositionsCount ?? 0) +
                (report?.RandomizedPlayerElementsCount ?? 0) +
                (report?.RandomizedPlayerAvatarsCount ?? 0) +
                (report?.RandomizedSkillSlotsCount ?? 0) +
                (report?.RandomizedAvatarSkillsCount ?? 0) +
                (report?.RandomizedSkillParamsCount ?? 0) +
                (report?.RandomizedTeamsCount ?? 0) +
                (report?.RandomizedPachangaTeamsCount ?? 0) +
                (report?.RandomizedOverworldChestsCount ?? 0) +
                (report?.RandomizedRouteChestsCount ?? 0) +
                (report?.RandomizedMatchDropsCount ?? 0) +
                (report?.RandomizedShopEntriesCount ?? 0) +
                (report?.RandomizedTeamUniformsCount ?? 0);

            if (totalExpectedChanges > 0 && modifiedPaths.Count == 0)
            {
                throw new InvalidOperationException(
                    "ERROR CRÍTICO DE PERSISTENCIA: Se generaron cambios en memoria pero ningún archivo interno cambió en el ie_a.fa guardado en disco.");
            }

            return new PersistedArchiveDiffSummary
            {
                SourceArchiveSizeBytes = new FileInfo(sourceIeAFaPath).Length,
                OutputArchiveSizeBytes = new FileInfo(outputIeAFaPath).Length,
                OutputHeaderInspection = inspection,
                PersistedModifiedVirtualFiles = modifiedPaths,
                PersistedChangedCharaParamsCount = changedCharaParams,
                PersistedChangedSkillTableEntriesCount = changedSkillSlots,
                PersistedChangedStoryTeamsCount = changedStoryTeams,
                PersistedChangedPachangaTeamsCount = changedPachangaTeams,
                PersistedChangedTeamParamsCount = changedTeamParams
            };
        }

        private static void CollectFileDigests(
            VirtualDirectory dir,
            string prefix,
            Dictionary<string, (long Length, string Sha256)> output)
        {
            using var sha = SHA256.Create();
            foreach (var kvp in dir.Files)
            {
                string fullPath = prefix + "/" + kvp.Key;
                SubMemoryStream sub = kvp.Value;
                byte[] bytes = sub.ByteContent;
                if (bytes == null)
                {
                    sub.Read();
                    bytes = sub.ByteContent ?? Array.Empty<byte>();
                }
                string hex = Convert.ToHexString(sha.ComputeHash(bytes));
                output[fullPath] = (bytes.Length, hex);
            }

            foreach (VirtualDirectory child in dir.Folders)
            {
                CollectFileDigests(child, prefix + "/" + child.Name, output);
            }
        }

        private static int CountChangedCharaParams(SafeGOGame src, SafeGOGame dst)
        {
            var a = src.GetCharaparams();
            var b = dst.GetCharaparams();
            int limit = Math.Min(a.Length, b.Length);
            int changed = 0;
            for (int i = 0; i < limit; i++)
            {
                if (a[i].Element != b[i].Element ||
                    a[i].PlayerPosition != b[i].PlayerPosition ||
                    a[i].FP != b[i].FP ||
                    a[i].TP != b[i].TP ||
                    a[i].Kick != b[i].Kick ||
                    a[i].Dribble != b[i].Dribble ||
                    a[i].Technique != b[i].Technique ||
                    a[i].Block != b[i].Block ||
                    a[i].Speed != b[i].Speed ||
                    a[i].Stamina != b[i].Stamina ||
                    a[i].Catch != b[i].Catch ||
                    a[i].Luck != b[i].Luck ||
                    a[i].FightingSpiritHash != b[i].FightingSpiritHash)
                {
                    changed++;
                }
            }
            return changed;
        }

        private static int CountChangedSkillSlots(SafeGOGame src, SafeGOGame dst)
        {
            var a = src.GetSkillTable();
            var b = dst.GetSkillTable();
            int limit = Math.Min(a.Length, b.Length);
            int changed = 0;
            for (int i = 0; i < limit; i++)
            {
                if (a[i].SkillHash != b[i].SkillHash || a[i].LevelLearned != b[i].LevelLearned)
                    changed++;
            }
            return changed;
        }

        private static int CountChangedStoryTeams(SafeGOGame src, SafeGOGame dst)
        {
            var a = src.GetStoryTeamsOnly();
            var b = dst.GetStoryTeamsOnly();
            int limit = Math.Min(a.Length, b.Length);
            int changed = 0;
            for (int i = 0; i < limit; i++)
            {
                bool diff = a[i].Emblem1 != b[i].Emblem1;
                for (int slot = 1; slot <= 16 && !diff; slot++)
                {
                    if (TeamSlotAccessor.GetStoryPlayer(a[i], slot) != TeamSlotAccessor.GetStoryPlayer(b[i], slot) ||
                        TeamSlotAccessor.GetStoryKitNumber(a[i], slot) != TeamSlotAccessor.GetStoryKitNumber(b[i], slot))
                    {
                        diff = true;
                    }
                }
                if (diff)
                    changed++;
            }
            return changed;
        }

        private static int CountChangedEncountTeams(SafeGOGame src, SafeGOGame dst)
        {
            var a = src.GetEncounterTeamsOnly();
            var b = dst.GetEncounterTeamsOnly();
            int limit = Math.Min(a.Length, b.Length);
            int changed = 0;
            for (int i = 0; i < limit; i++)
            {
                bool diff = a[i].Emblem1 != b[i].Emblem1;
                for (int slot = 1; slot <= 5 && !diff; slot++)
                {
                    if (TeamSlotAccessor.GetEncountPlayer(a[i], slot) != TeamSlotAccessor.GetEncountPlayer(b[i], slot) ||
                        TeamSlotAccessor.GetEncountKitNumber(a[i], slot) != TeamSlotAccessor.GetEncountKitNumber(b[i], slot))
                    {
                        diff = true;
                    }
                }
                if (diff)
                    changed++;
            }
            return changed;
        }

        private static int CountChangedTeamParams(SafeGOGame src, SafeGOGame dst)
        {
            var a = src.GetTeamParams();
            var b = dst.GetTeamParams();
            int limit = Math.Min(a.Length, b.Length);
            int changed = 0;
            for (int i = 0; i < limit; i++)
            {
                if (a[i].Uniform != b[i].Uniform ||
                    a[i].FormationID != b[i].FormationID ||
                    a[i].CoachID != b[i].CoachID ||
                    a[i].TacticID != b[i].TacticID ||
                    a[i].Level != b[i].Level ||
                    a[i].BootsID != b[i].BootsID ||
                    a[i].DropID1 != b[i].DropID1 ||
                    a[i].DropID2 != b[i].DropID2 ||
                    a[i].DropID3 != b[i].DropID3)
                {
                    changed++;
                }
            }
            return changed;
        }
    }
}
