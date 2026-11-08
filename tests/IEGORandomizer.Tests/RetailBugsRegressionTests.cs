using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using IEGORandomizer.Engine;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Diagnostics;
using IEGORandomizer.Engine.Validation;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.Core.RomFs;
using Lynx.InazumaEleven.Games.GO;
using StudioElevenLib.Level5.Archive.ARC0;
using StudioElevenLib.Level5.Compression;
using StudioElevenLib.Level5.Compression.LZ10;
using StudioElevenLib.Tools;

namespace IEGORandomizer.Tests
{
    /// <summary>
    /// Regression tests for every v1.0.2 retail root cause:
    /// 1. Story/Pachanga VictoryCondition/Script/NextScript/btlXXXX over-protection
    /// 2. Uniform iiuXXXX CRC32 vs ItemNumber, uza/uzb/uzc/uzd body variants, EncountMapRecord ChasePlayer &amp; Uniform
    /// 3. Retail LZ10 ARC0 table preservation + DirectoryHashes (N-1) + SubMemoryStream/_fullPathIndex persistence
    /// 4. Ultimate Evolution Manual dummy skills (whs9999/who9999/whd9999/whk9999, Power=0) exclusion
    /// 5. EUR Shadow ProductCode CTR-P-AEDP detection, manifest post-verification flags, and Citra mod diagnostics
    /// </summary>
    public sealed class RetailBugsRegressionTests
    {
        [Fact]
        public void StoryRivalAndPachangaTeams_WithVictoryConditionAndScripts_RandomizeWhilePreservingChasePlayerAndRaimonKit()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_v102_story_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string srcPath = Path.Combine(tempDir, "source", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(srcPath);

                // Mutate source fixture so rival story team ts0101 & pachanga te0101 have retail VictoryCondition/Script/NextScript,
                // retail iiuXXXX CRC32 in EncountMapRecord.Uniform, ChasePlayer = 2, and dummy whs9999 manual in skill_config!
                string retailLikePath = Path.Combine(tempDir, "retail_like", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(retailLikePath)!);

                int ts0001 = Level5IdHelper.ComputeCrc32Utf8("ts0001");
                int ts0101 = Level5IdHelper.ComputeCrc32Utf8("ts0101");
                int whs9999 = Level5IdHelper.ComputeCrc32Utf8("whs9999");

                using (var setupGame = new SafeGOGame(srcPath, "es"))
                {
                    var storyTeams = setupGame.GetStoryTeamsOnly();
                    var encountTeams = setupGame.GetEncounterTeamsOnly();

                    foreach (var st in storyTeams)
                    {
                        st.Emblem1 = 10;
                    }
                    foreach (var et in encountTeams)
                    {
                        et.Emblem1 = 12;
                    }
                    setupGame.SaveTeamConfig(storyTeams, encountTeams);

                    var soccers = setupGame.GetSoccers();
                    foreach (var sc in soccers)
                    {
                        sc.VictoryCondition = 2; // Retail 5v5/story match victory condition
                        sc.Script = 123456;
                        sc.NextScript = 654321;
                    }
                    setupGame.SaveSoccers(soccers);

                    // Add Ultimate Evolution Manual dummy skill (whs9999, SkillPosition=2 Shot, TPCost=1, Power=0)
                    var skills = setupGame.GetSkillConfigs(false).ToList();
                    skills.Add(new GOSupport.SkillConfig
                    {
                        SkillHash = whs9999,
                        SkillPosition = 2,
                        Element = 0,
                        TPCost = 1,
                        Power = 0
                    });
                    setupGame.SaveSkillConfigs(skills.ToArray());

                    // Set retail EncountMapRecord: ChasePlayer = 2 (chase flag), Uniform = CRC32("iiu0002")
                    var spawns = MapDataService.LoadAllEncounterSpawns(setupGame.IE_A.Directory);
                    foreach (var sp in spawns)
                    {
                        sp.Record.ChasePlayer = 2;
                        sp.Record.Uniform = Level5IdHelper.ComputeCrc32Utf8("iiu0002");
                    }
                    MapDataService.SaveEncounterSpawns(setupGame.IE_A.Directory, spawns);

                    // Add emblem assets emb0001..emb0015 and kit 5 with uza+uzb ONLY (no uzc/uzd -> Away +2 forbidden!)
                    for (int e = 1; e <= 15; e++)
                    {
                        Arc0SafeArchive.WriteFileAtPath(
                            setupGame.IE_A.Directory,
                            $"/data/emblem/emb{e:D4}.xi",
                            Encoding.ASCII.GetBytes($"EMB_{e:D4}"));
                    }
                    Arc0SafeArchive.WriteFileAtPath(
                        setupGame.IE_A.Directory,
                        "/data/chr/model/rpg/body/uza0005.xc",
                        Encoding.ASCII.GetBytes("KIT_uza0005"));
                    Arc0SafeArchive.WriteFileAtPath(
                        setupGame.IE_A.Directory,
                        "/data/chr/model/rpg/body/uzb0005.xc",
                        Encoding.ASCII.GetBytes("KIT_uzb0005"));

                    setupGame.SaveCopyTo(retailLikePath);
                }

                string outDir = Path.Combine(tempDir, "out_run");
                var coordinator = new RandomizerCoordinator();
                var opts = new RandomizerOptions
                {
                    Seed = "RETAIL-LUZ-FIX-2026",
                    Edition = GameEdition.Light,
                    BlindPlayMode = true
                };
                opts.Skills.RandomizeSkillParameters = true;

                var report = coordinator.GenerateRun(retailLikePath, outDir, opts);

                Assert.True(report.OutputIntegrityVerified);
                Assert.True(report.SourceImmutabilityVerified);
                Assert.True(report.RandomizedTeamsCount >= 2, "Story rival teams with VictoryCondition/Script must now randomize!");
                Assert.True(report.RandomizedPachangaTeamsCount >= 2, "5v5 Pachanga teams with VictoryCondition!=0 must now randomize!");
                Assert.True(report.RandomizedTeamUniformsCount >= 3, "Rival & Pachanga uniforms must randomize!");
                Assert.True(report.PersistedDiffTelemetry.PersistedModifiedFilesCount >= 6, "Modified files must persist on disk.");

                // Verify manifest JSON written AFTER verification has true flags
                string manifestJson = File.ReadAllText(Path.Combine(outDir, "run_manifest.json"));
                using (JsonDocument doc = JsonDocument.Parse(manifestJson))
                {
                    Assert.True(doc.RootElement.GetProperty("outputIntegrityVerified").GetBoolean());
                    Assert.True(doc.RootElement.GetProperty("sourceImmutabilityVerified").GetBoolean());
                    Assert.True(doc.RootElement.GetProperty("persistedVerification").GetProperty("persistedModifiedFilesCount").GetInt32() >= 6);
                }

                using var modded = new SafeGOGame(Path.Combine(outDir, "Standalone", "ie_a.fa"), "es");

                // 1. Raimon core team ts0001 keeps Raimon's Home kit (1000) & emblem (10), while rival ts0101 randomizes
                var raimonTp = modded.GetTeamParams().Single(tp => tp.TeamConfigID == ts0001);
                Assert.Equal(1000, raimonTp.Uniform);
                var raimonSt = modded.GetStoryTeamsOnly().Single(st => st.TeamConfigID == ts0001);
                Assert.Equal(10, raimonSt.Emblem1);

                // 2. Kit 0005 has NO uzc0005.xc/uzd0005.xc, so Uniform must NEVER be 5002 (Away variant)
                foreach (var tp in modded.GetTeamParams())
                {
                    Assert.NotEqual(5002, tp.Uniform, "Kit without uzc/uzd away 3D models must never use +2 Away offset.");
                }

                // 3. Overworld EncountMapRecord.ChasePlayer must stay 2, and Uniform must be CRC32("iiuXXXX")
                var iiuLookup = Level5IdHelper.BuildPrefixLookup("iiu");
                foreach (var sp in MapDataService.LoadAllEncounterSpawns(modded.IE_A.Directory))
                {
                    Assert.Equal(2, sp.Record.ChasePlayer);
                    Assert.True(iiuLookup.ContainsKey(sp.Record.Uniform),
                        $"Expected CRC32('iiuXXXX') in EncountMapRecord.Uniform, got {sp.Record.Uniform}.");
                }

                // 4. Dummy manual whs9999 must NEVER be assigned to any player and keep Power == 0
                var moddedWhs9999 = modded.GetSkillConfigs(false).Single(s => s.SkillHash == whs9999);
                Assert.Equal(0, moddedWhs9999.Power);
                foreach (var stEntry in modded.GetSkillTable())
                {
                    Assert.NotEqual(whs9999, stEntry.SkillHash);
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void RetailLz10Arc0Layout_PreservesExactHeaderTables_AndDetectsCitraMisnestedFolders()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_v102_lz10_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string baseFa = Path.Combine(tempDir, "base.fa");
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(baseFa);

                // Convert header tables to retail-identical LZ10 compression + N-1 DirectoryHashes
                string lz10RetailFa = Path.Combine(tempDir, "ie luz romFS", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(lz10RetailFa)!);
                RewriteHeaderTablesAsRetailLz10(baseFa, lz10RetailFa);

                string outDir = Path.Combine(tempDir, "out");
                string fakeCitraRoot = Path.Combine(tempDir, "FakeAppData", "Citra", "load", "mods");

                // Plant a classic user mistake: double-nested Citra/load/mods/0004000000112F00 inside 0004000000112F00!
                string misnestedFa = Path.Combine(
                    fakeCitraRoot, "0004000000112F00", "Citra", "load", "mods", "0004000000112F00", "romfs", "ie_a.fa");
                Directory.CreateDirectory(Path.GetDirectoryName(misnestedFa)!);
                File.WriteAllBytes(misnestedFa, new byte[] { 0x41, 0x52, 0x43, 0x30 });

                // Verify diagnostic service flags both Extracted-RomFS notice AND mis-nested Citra folder!
                var diagBefore = LocalDiagnosticService.RunDiagnostics(
                    Path.GetDirectoryName(lz10RetailFa)!,
                    GameEdition.Auto,
                    fakeCitraRoot);

                Assert.Equal("Light", diagBefore.DetectedEdition);
                Assert.Equal("0004000000112F00", diagBefore.ExpectedPrimaryTitleId);
                Assert.True(diagBefore.Arc0Header.DirectoryEntriesCompression.StartsWith("LZ10", StringComparison.Ordinal));
                Assert.True(diagBefore.ActionableWarnings.Any(w => w.Contains("CARPETA MAL ANIDADA", StringComparison.Ordinal)));
                Assert.True(diagBefore.ActionableWarnings.Any(w => w.Contains("Carpeta RomFS extraída", StringComparison.Ordinal)));

                // Run full randomization with DirectCitraModsRootPath enabled
                var opts = new RandomizerOptions { Seed = "LZ10-CITRA-DIRECT-2026", Edition = GameEdition.Auto };
                opts.Export.DirectCitraModsRootPath = fakeCitraRoot;

                var coordinator = new RandomizerCoordinator();
                var rep = coordinator.GenerateRun(Path.GetDirectoryName(lz10RetailFa)!, outDir, opts);

                Assert.True(rep.Arc0WriteTelemetry.PreservedRetailTables, "Retail LZ10 ARC0 tables must be preserved verbatim!");
                string expectedInstalled = Path.Combine(fakeCitraRoot, "0004000000112F00", "romfs", "ie_a.fa");
                Assert.Equal(expectedInstalled, rep.DirectCitraInstalledPath);
                Assert.True(File.Exists(expectedInstalled));

                // Reopen installed Citra ie_a.fa and confirm LZ10 tables & persisted mutations
                var installedArc = Arc0SafeArchive.OpenReadOnly(expectedInstalled);
                try
                {
                    var headerInspect = Arc0SafeArchive.InspectArchive(installedArc);
                    Assert.True(headerInspect.DirectoryEntriesCompression.StartsWith("LZ10", StringComparison.Ordinal));
                    Assert.True(headerInspect.FileEntriesCompression.StartsWith("LZ10", StringComparison.Ordinal));
                    Assert.Equal(headerInspect.DirectoryEntriesCount - 1, headerInspect.DirectoryHashesCount);
                }
                finally
                {
                    installedArc.Close();
                }

                // Also verify CTR-P-AEDP resolves to Shadow
                Assert.Equal(GameEdition.Shadow, TitleIdCatalog.DetectEdition("", "CTR-P-AEDP"));
                Assert.Equal(GameEdition.Shadow, TitleIdCatalog.DetectEdition("000400000017C200", ""));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        private static void RewriteHeaderTablesAsRetailLz10(string unweightedArc0Path, string outputLz10Arc0Path)
        {
            byte[] src = File.ReadAllBytes(unweightedArc0Path);
            using var msIn = new MemoryStream(src, writable: false);
            var r = new BinaryDataReader(msIn);
            var h = r.ReadStruct<ARC0Support.Header>();

            r.Seek((uint)h.DirectoryEntriesOffset);
            byte[] dirEntriesRaw = Compressor.Decompress(r.GetSection(h.DirectoryHashOffset - h.DirectoryEntriesOffset));
            r.Seek((uint)h.DirectoryHashOffset);
            byte[] dirHashesRaw = Compressor.Decompress(r.GetSection(h.FileEntriesOffset - h.DirectoryHashOffset));
            r.Seek((uint)h.FileEntriesOffset);
            byte[] fileEntriesRaw = Compressor.Decompress(r.GetSection(h.NameOffset - h.FileEntriesOffset));
            r.Seek((uint)h.NameOffset);
            byte[] nameTableRaw = Compressor.Decompress(r.GetSection(h.DataOffset - h.NameOffset));

            var lz = new LZ10();
            var noComp = new StudioElevenLib.Level5.Compression.NoCompression.NoCompression();
            byte[] cDir = dirEntriesRaw.Length > 0 ? lz.Compress(dirEntriesRaw) : noComp.Compress(dirEntriesRaw);
            byte[] cHash = dirHashesRaw.Length > 0 ? lz.Compress(dirHashesRaw) : noComp.Compress(dirHashesRaw);
            byte[] cFiles = fileEntriesRaw.Length > 0 ? lz.Compress(fileEntriesRaw) : noComp.Compress(fileEntriesRaw);
            byte[] cNames = nameTableRaw.Length > 0 ? lz.Compress(nameTableRaw) : noComp.Compress(nameTableRaw);

            using var outFs = new FileStream(outputLz10Arc0Path, FileMode.Create, FileAccess.Write);
            var w = new BinaryDataWriter(outFs);

            int dirOff = System.Runtime.InteropServices.Marshal.SizeOf<ARC0Support.Header>();
            w.Seek(dirOff);
            w.Write(cDir);
            w.WriteAlignment(4);
            int hashOff = (int)w.Position;
            w.Write(cHash);
            w.WriteAlignment(4);
            int fileOff = (int)w.Position;
            w.Write(cFiles);
            w.WriteAlignment(4);
            int nameOff = (int)w.Position;
            w.Write(cNames);
            w.WriteAlignment(4);
            int dataOff = (int)w.Position;

            outFs.Write(src, h.DataOffset, src.Length - h.DataOffset);

            int uncompTables = (h.DirectoryEntriesCount * 20) + (h.DirectoryHashCount * 4) +
                               (h.FileEntriesCount * 16) + nameTableRaw.Length + 0x20;
            ARC0Support.Header newHeader = h;
            newHeader.DirectoryEntriesOffset = dirOff;
            newHeader.DirectoryHashOffset = hashOff;
            newHeader.FileEntriesOffset = fileOff;
            newHeader.NameOffset = nameOff;
            newHeader.DataOffset = dataOff;
            newHeader.TableChunkSize = (uncompTables + 3) & ~3;
            w.Seek(0);
            w.WriteStruct(newHeader);
        }
    }
}
