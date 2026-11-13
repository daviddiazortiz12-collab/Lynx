using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using IEGORandomizer.App;
using IEGORandomizer.Engine;
using IEGORandomizer.Engine.Cia;
using IEGORandomizer.Engine.Configuration;
using Lynx.Core.Games;
using Lynx.Core.RomFs;
using Lynx.Core.RomFs.Cia;

namespace IEGORandomizer.Tests
{
    public sealed class CiaPipelineTests
    {
        private const ulong LuzEurTitleId = 0x0004000000112F00UL;
        private const uint LuzSaveDataSize = 512 * 1024; // 512 KB

        [Fact]
        public void InspectLocalCia_LuzV000PlainMultiContent_DetectsRevisionEditionSaveSizeAndRomFreeDiag()
        {
            string tempDir = CreateTempTestDir("cia_inspect");
            try
            {
                string ciaPath = Path.Combine(tempDir, "InazumaElevenGO_Luz_V000.cia");
                BuildSyntheticLuzV000MultiContentCia(ciaPath, out var originalAssets);
                string preSha = CiaFormatInspector.ComputeStreamingFileSha256Hex(ciaPath);

                var svc = new CiaWorkflowService();
                var rep = svc.InspectLocalCia(ciaPath, computeFullSha256: true);

                Assert.True(rep.IsValidCiaStructure, "Expected valid CIA structure.");
                Assert.True(rep.CanProcessNow, "Expected Plain CIA to be immediately processable.");
                Assert.Equal(CiaEncryptionKind.PlainCiaPlainNcch, rep.EncryptionKind);
                Assert.Equal("0004000000112F00", rep.TmdTitleIdHex);
                Assert.Equal((ushort)0x0000, rep.TmdTitleVersionRaw);
                Assert.Equal("V.0.0.0", rep.TmdTitleVersionFormatted);
                Assert.True(rep.IsBaseRevisionV000);
                Assert.Equal(GameEdition.Light, rep.DetectedEdition);
                Assert.Equal("CTR-P-AE4P", rep.ProductCode);
                Assert.Equal(LuzSaveDataSize, rep.SaveDataSizeBytes);
                Assert.Equal((ushort)3, rep.ContentCount);
                Assert.True(rep.TmdInternalHashesValid);
                Assert.Equal("Inazuma Eleven GO: Luz", rep.SmdhBannerTitle);
                Assert.True(rep.RomFsReadable);
                Assert.Equal(originalAssets.Count, rep.RomFsFileCount);
                Assert.True(rep.HasIeAFa);
                Assert.True(rep.HasFixXr);
                Assert.True(rep.HasBannerIcn);
                Assert.Equal(1, rep.RomFsSoundFilesCount);
                Assert.Equal(1, rep.RomFsMovieFilesCount);

                string diagJson = Path.Combine(tempDir, "diag_no_rom.json");
                svc.SaveRomFreeDiagnosticJson(rep, diagJson);
                Assert.True(File.Exists(diagJson));
                string diagText = File.ReadAllText(diagJson);
                Assert.True(diagText.Contains("\"ContainsRomBytes\": false"));
                Assert.True(diagText.Contains("V.0.0.0"));

                string postSha = CiaFormatInspector.ComputeStreamingFileSha256Hex(ciaPath);
                Assert.Equal(preSha, postSha, "InspectLocalCia must never modify a single byte of the source .cia.");
            }
            finally
            {
                TryCleanup(tempDir);
            }
        }

        [Fact]
        public void MainMode_PlainCiaToRandomizedCia_Applies5ModulesPreservesAllRomFsFilesAndDeterminism()
        {
            string tempDir = CreateTempTestDir("cia_main_mode");
            try
            {
                string sourceCia = Path.Combine(tempDir, "Original_Luz_V000.cia");
                BuildSyntheticLuzV000MultiContentCia(sourceCia, out var origRomFsBytes);
                string sourceShaBefore = CiaFormatInspector.ComputeStreamingFileSha256Hex(sourceCia);

                string outCiaA = Path.Combine(tempDir, "OutA", "Luz_Random_A.cia");
                string outCiaB = Path.Combine(tempDir, "OutB", "Luz_Random_B.cia");

                var svc = new CiaWorkflowService();
                var optsA = new RandomizerOptions { Seed = "LUZ-CIA-2026", BlindPlayMode = false };
                var optsB = new RandomizerOptions { Seed = "LUZ-CIA-2026", BlindPlayMode = true };

                var repA = svc.RandomizeCiaToCia(sourceCia, outCiaA, optsA);
                var repB = svc.RandomizeCiaToCia(sourceCia, outCiaB, optsB);

                Assert.True(File.Exists(outCiaA));
                Assert.True(File.Exists(outCiaB));
                Assert.True(repA.OutputIntegrityVerified);
                Assert.True(repA.SourceImmutabilityVerified);
                Assert.NotNull(repA.CiaVerification);
                Assert.True(repA.CiaVerification.IsValid);
                Assert.Equal(3, repA.CiaVerification.VerifiedContentChunksCount);
                Assert.Equal(origRomFsBytes.Count, repA.CiaBuildTelemetry.OutputRomFsFileCount);
                Assert.Equal(LuzSaveDataSize, repA.CiaBuildTelemetry.PreservedSaveDataSizeBytes);

                // Verify source .cia was never touched
                Assert.Equal(sourceShaBefore, CiaFormatInspector.ComputeStreamingFileSha256Hex(sourceCia));

                // Extract full RomFS from rebuilt outCiaA & outCiaB and verify every non-ie_a.fa file is 100% preserved
                string extDirA = Path.Combine(tempDir, "extA");
                string extDirB = Path.Combine(tempDir, "extB");
                ExtractFullRomFsFromPlainCia(outCiaA, extDirA);
                ExtractFullRomFsFromPlainCia(outCiaB, extDirB);

                foreach (var kvp in origRomFsBytes)
                {
                    string fileInA = Path.Combine(extDirA, kvp.Key.Replace('/', Path.DirectorySeparatorChar));
                    Assert.True(File.Exists(fileInA), $"Missing preserved RomFS file '{kvp.Key}' in rebuilt .cia.");

                    if (!kvp.Key.Equals("ie_a.fa", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.SequenceEqual(kvp.Value, File.ReadAllBytes(fileInA), $"Untouched RomFS asset '{kvp.Key}' must match byte-for-byte.");
                    }
                }

                // Prove ie_a.fa inside rebuilt .cia actually contains persisted randomization mutations AND matches deterministically
                byte[] ieaA = File.ReadAllBytes(Path.Combine(extDirA, "ie_a.fa"));
                byte[] ieaB = File.ReadAllBytes(Path.Combine(extDirB, "ie_a.fa"));
                Assert.False(ieaA.SequenceEqual(origRomFsBytes["ie_a.fa"]), "Rebuilt .cia ie_a.fa must contain randomized data, not unmodified original.");
                Assert.SequenceEqual(ieaA, ieaB, "Same seed must produce identical randomized ie_a.fa inside rebuilt .cia.");
                Assert.True(repA.PersistedDiffTelemetry.PersistedModifiedFilesCount >= 5);
            }
            finally
            {
                TryCleanup(tempDir);
            }
        }

        [Fact]
        public void SecondMode_OriginalCiaPlusModifiedRomFsFolder_IncorporatesAllModifiedAndAddedFiles()
        {
            string tempDir = CreateTempTestDir("cia_second_mode");
            try
            {
                string sourceCia = Path.Combine(tempDir, "Original_Luz_V000.cia");
                BuildSyntheticLuzV000MultiContentCia(sourceCia, out var origRomFsBytes);
                string sourceShaBefore = CiaFormatInspector.ComputeStreamingFileSha256Hex(sourceCia);

                // First generate a modified RomFS using Classic v1.0.2 mode, then ALSO modify fix.xr, sound, and add a brand-new file!
                string classicOut = Path.Combine(tempDir, "classic_v102_out");
                var coordinator = new RandomizerCoordinator();
                var classicRep = coordinator.GenerateRun(
                    sourceCia,
                    classicOut,
                    new RandomizerOptions { Seed = "MERGE-ROMFS-777" });

                string modRomFsDir = Path.GetDirectoryName(
                    classicRep.ExportedPaths.First(p => p.Contains("Citra") && p.EndsWith("ie_a.fa")))!;

                byte[] customFixXr = Encoding.UTF8.GetBytes("MODIFIED-FIX-XR-PAYLOAD-V110-TEST");
                byte[] customBcsar = Encoding.UTF8.GetBytes("MODIFIED-SOUND-ARCHIVE-BCSAR-PAYLOAD");
                byte[] brandNewExtraFile = Encoding.UTF8.GetBytes("BRAND-NEW-CUSTOM-ROMFS-FILE-CONTENT");

                File.WriteAllBytes(Path.Combine(modRomFsDir, "fix.xr"), customFixXr);
                Directory.CreateDirectory(Path.Combine(modRomFsDir, "snd", "product"));
                File.WriteAllBytes(Path.Combine(modRomFsDir, "snd", "product", "ie4_sound.bcsar"), customBcsar);
                Directory.CreateDirectory(Path.Combine(modRomFsDir, "data", "custom_mod"));
                File.WriteAllBytes(Path.Combine(modRomFsDir, "data", "custom_mod", "extra_asset.bin"), brandNewExtraFile);

                string mergedCia = Path.Combine(tempDir, "Merged_Luz.cia");
                var svc = new CiaWorkflowService();
                var mergeRep = svc.MergeModifiedRomFsIntoCia(sourceCia, modRomFsDir, mergedCia);

                Assert.True(mergeRep.PreValidatedIeAFa);
                Assert.True(mergeRep.Verification.IsValid);
                Assert.Equal(4, mergeRep.RebuildTelemetry.OverlaidModifiedRomFsFilesCount); // ie_a.fa + fix.xr + bcsar + extra_asset.bin
                Assert.Equal(origRomFsBytes.Count + 1, mergeRep.RebuildTelemetry.OutputRomFsFileCount);
                Assert.Equal(sourceShaBefore, CiaFormatInspector.ComputeStreamingFileSha256Hex(sourceCia));

                // Extract full RomFS from mergedCia and verify ALL 4 modified/added files + 2 untouched files!
                string extracted = Path.Combine(tempDir, "merged_extracted");
                ExtractFullRomFsFromPlainCia(mergedCia, extracted);

                Assert.SequenceEqual(
                    File.ReadAllBytes(Path.Combine(modRomFsDir, "ie_a.fa")),
                    File.ReadAllBytes(Path.Combine(extracted, "ie_a.fa")));
                Assert.SequenceEqual(
                    customFixXr,
                    File.ReadAllBytes(Path.Combine(extracted, "fix.xr")));
                Assert.SequenceEqual(
                    customBcsar,
                    File.ReadAllBytes(Path.Combine(extracted, "snd", "product", "ie4_sound.bcsar")));
                Assert.SequenceEqual(
                    brandNewExtraFile,
                    File.ReadAllBytes(Path.Combine(extracted, "data", "custom_mod", "extra_asset.bin")));

                // Untouched files from original .cia must still be intact!
                Assert.SequenceEqual(
                    origRomFsBytes["ctr/exsave/banner.icn"],
                    File.ReadAllBytes(Path.Combine(extracted, "ctr", "exsave", "banner.icn")));
                Assert.SequenceEqual(
                    origRomFsBytes["movie/ed00s_c.moflex"],
                    File.ReadAllBytes(Path.Combine(extracted, "movie", "ed00s_c.moflex")));
            }
            finally
            {
                TryCleanup(tempDir);
            }
        }

        [Fact]
        public void EncryptionMatrix_FixedKey_KeyXNcch_AndDoubleEncryptedCia_DecryptWithLocalTestKeysToPlainCia()
        {
            string tempDir = CreateTempTestDir("cia_crypto_matrix");
            try
            {
                // Synthetic random test keys (zero proprietary keys!)
                byte[] testKeyX3D = Convert.FromHexString("112233445566778899AABBCCDDEEFF00");
                byte[] testKeyY3D0 = Convert.FromHexString("FFEEDDCCBBAA99887766554433221100");
                byte[] derivedCommon0 = CtrKeyStore.DeriveNormalKey(testKeyX3D, testKeyY3D0);
                byte[] testKeyX2C = Convert.FromHexString("A1B2C3D4E5F60718293A4B5C6D7E8F90");
                byte[] testPlainTitleKey = Convert.FromHexString("55AA55AA123456789ABCDEF00FEDCBA9");

                string keysFile = Path.Combine(tempDir, "aes_keys.txt");
                File.WriteAllLines(keysFile, new[]
                {
                    "# Synthetic unit-test keyfile",
                    $"slot0x3DKeyX={Convert.ToHexString(testKeyX3D)}",
                    $"slot0x3DKeyY0={Convert.ToHexString(testKeyY3D0)}",
                    $"slot0x2CKeyX={Convert.ToHexString(testKeyX2C)}"
                });

                // 1) Fixed-Key NCCH CIA (works with ZERO external keys!)
                string fixedKeyCia = Path.Combine(tempDir, "Luz_FixedKey.cia");
                BuildSyntheticLuzV000MultiContentCia(
                    fixedKeyCia,
                    out _,
                    useFixedKeyNcch: true);

                var svc = new CiaWorkflowService();
                var fixedInspect = CiaFormatInspector.InspectFile(fixedKeyCia, new CtrKeyStore());
                Assert.Equal(CiaEncryptionKind.PlainCiaFixedKeyNcch, fixedInspect.EncryptionKind);
                Assert.True(fixedInspect.CanProcessNow, "Fixed-key NCCH must be processable without external keys.");

                string fixedOut = Path.Combine(tempDir, "Luz_FromFixedKey_Out.cia");
                var fixedRep = svc.RandomizeCiaToCia(fixedKeyCia, fixedOut, new RandomizerOptions { Seed = "FIXED-KEY-SEED" });
                Assert.True(fixedRep.CiaVerification.IsValid);

                // 2) Double-Encrypted CIA (CIA AES-128-CBC TitleKey layer + Retail-style KeyX 0x2C AES-128-CTR NCCH layer)
                string doubleEncCia = Path.Combine(tempDir, "Luz_DoubleEncrypted.cia");
                BuildSyntheticLuzV000MultiContentCia(
                    doubleEncCia,
                    out var origAssets,
                    useRetailKeyX2CNcch: true,
                    keyX2C: testKeyX2C,
                    encryptCiaLayer: true,
                    commonKey0: derivedCommon0,
                    plainTitleKey: testPlainTitleKey);

                // Without keys -> must honestly report CanProcessNow == false without throwing
                var noKeyInspect = CiaFormatInspector.InspectFile(doubleEncCia, new CtrKeyStore());
                Assert.Equal(CiaEncryptionKind.EncryptedCiaLayer, noKeyInspect.EncryptionKind);
                Assert.False(noKeyInspect.CanProcessNow);

                // With local test aes_keys.txt -> must inspect, decrypt all 3 contents, randomize, and output clean Plain CIA!
                var withKeyInspect = svc.InspectLocalCia(doubleEncCia, keysFile);
                Assert.True(withKeyInspect.CanProcessNow, "Double-encrypted CIA must become processable when matching local aes_keys.txt is provided.");
                Assert.Equal(origAssets.Count, withKeyInspect.RomFsFileCount);

                string decOutCia = Path.Combine(tempDir, "Luz_FromDoubleEncrypted_PlainOut.cia");
                var decRep = svc.RandomizeCiaToCia(
                    doubleEncCia,
                    decOutCia,
                    new RandomizerOptions { Seed = "DOUBLE-CRYPTO-OK", AesKeysPath = keysFile });

                Assert.True(decRep.CiaVerification.IsValid);
                // Re-inspect output with EMPTY KeyStore to prove zero residual encryption on any of the 3 contents!
                var verifyPlainOut = CiaFormatInspector.InspectFile(decOutCia, new CtrKeyStore());
                Assert.Equal(CiaEncryptionKind.PlainCiaPlainNcch, verifyPlainOut.EncryptionKind);
                Assert.True(verifyPlainOut.CanProcessNow);
                Assert.True(verifyPlainOut.ContentChunks.All(c => !c.IsCiaLayerEncrypted));
            }
            finally
            {
                TryCleanup(tempDir);
            }
        }

        [Fact]
        public void SafetyGuardsAndCli_RejectOverwriteAndCorruptRomFs_AndRunAllCliModesCleanly()
        {
            string tempDir = CreateTempTestDir("cia_safety_cli");
            try
            {
                string sourceCia = Path.Combine(tempDir, "Luz_Source.cia");
                BuildSyntheticLuzV000MultiContentCia(sourceCia, out _);
                string origSha = CiaFormatInspector.ComputeStreamingFileSha256Hex(sourceCia);

                var svc = new CiaWorkflowService();

                // Guard 1: Refuses to overwrite source .cia
                Assert.Throws<InvalidOperationException>(() =>
                {
                    svc.RandomizeCiaToCia(sourceCia, sourceCia, new RandomizerOptions { Seed = "GUARD-1" });
                });

                // Guard 2: Corrupt ie_a.fa in modified RomFS folder is rejected cleanly with zero .part files left behind
                string badRomFs = Path.Combine(tempDir, "bad_romfs");
                Directory.CreateDirectory(badRomFs);
                File.WriteAllBytes(Path.Combine(badRomFs, "ie_a.fa"), Encoding.ASCII.GetBytes("NOT_AN_ARC0_ARCHIVE_AT_ALL"));
                string rejectedOut = Path.Combine(tempDir, "ShouldNotExist.cia");

                Assert.Throws<Exception>(() =>
                {
                    svc.MergeModifiedRomFsIntoCia(sourceCia, badRomFs, rejectedOut);
                });
                Assert.False(File.Exists(rejectedOut), "Corrupt merge must not create output .cia.");
                Assert.Equal(0, Directory.GetFiles(tempDir, "*.part*").Length, "Zero temporary .part files may remain after failure.");

                // CLI Mode A: --inspect-cia
                string diagOut = Path.Combine(tempDir, "cli_diag.json");
                int codeInspect = CliRunner.Run(new[] { "--inspect-cia", sourceCia, "--export-diag", diagOut });
                Assert.Equal(0, codeInspect);
                Assert.True(File.Exists(diagOut));

                // CLI Mode B: Main Mode (-i source.cia --output-cia out.cia)
                string cliRandCia = Path.Combine(tempDir, "Cli_Randomized.cia");
                int codeMain = CliRunner.Run(new[] { "-i", sourceCia, "--output-cia", cliRandCia, "-s", "CLI-CIA-SEED" });
                Assert.Equal(0, codeMain);
                Assert.True(File.Exists(cliRandCia));

                // CLI Mode C: Classic v1.0.2 Mode -> then CLI Mode D: --merge-cia
                string cliClassicOut = Path.Combine(tempDir, "cli_classic");
                int codeClassic = CliRunner.Run(new[] { "--cli", "-i", sourceCia, "-o", cliClassicOut, "-s", "CLI-CLASSIC-SEED" });
                Assert.Equal(0, codeClassic);

                string classicRomFs = Path.Combine(cliClassicOut, "Citra", "load", "mods", "0004000000112F00", "romfs");
                File.WriteAllBytes(Path.Combine(classicRomFs, "extra_cli_test.bin"), new byte[] { 1, 2, 3, 4, 5 });

                string cliMergedCia = Path.Combine(tempDir, "Cli_Merged.cia");
                int codeMerge = CliRunner.Run(new[] { "--merge-cia", sourceCia, "--romfs", classicRomFs, "--output-cia", cliMergedCia });
                Assert.Equal(0, codeMerge);
                Assert.True(File.Exists(cliMergedCia));
                Assert.True(CiaPackageVerifier.VerifyColdCiaFile(cliMergedCia).IsValid);

                // Guard 3: 1-byte corruption inside IVFC RomFS of a .cia is immediately caught by CiaPackageVerifier
                string tamperedCia = Path.Combine(tempDir, "Tampered_BitFlip.cia");
                File.Copy(cliMergedCia, tamperedCia);
                var repTamper = CiaFormatInspector.InspectFile(tamperedCia, new CtrKeyStore());
                long c0Off = repTamper.ContentChunks.First(c => c.ContentIndex == 0).CiaFileOffset;
                using (var tamperFs = new FileStream(tamperedCia, FileMode.Open, FileAccess.ReadWrite))
                {
                    // Flip 1 byte near the middle of Content 0 RomFS
                    tamperFs.Seek(c0Off + 0x5000, SeekOrigin.Begin);
                    int origByte = tamperFs.ReadByte();
                    tamperFs.Seek(c0Off + 0x5000, SeekOrigin.Begin);
                    tamperFs.WriteByte((byte)(origByte ^ 0xFF));
                }
                var tamperVerification = CiaPackageVerifier.VerifyColdCiaFile(tamperedCia);
                Assert.False(tamperVerification.IsValid, "Cold verifier must reject a 1-byte bitflip inside the rebuilt .cia.");

                // Source .cia remained 100% immutable across every single test!
                Assert.Equal(origSha, CiaFormatInspector.ComputeStreamingFileSha256Hex(sourceCia));
            }
            finally
            {
                TryCleanup(tempDir);
            }
        }

        [Fact]
        public void PirateLegitCia_OutOfTheBoxZeroKeyFile_DecryptsRandomizesAll5ModulesAndPasses2dsHardwareChecks()
        {
            string tempDir = CreateTempTestDir("piratelegit_2ds_e2e");
            try
            {
                // Exact retail keys matching Nintendo 3DS / 2DS CDN .piratelegit.cia (CommonKeyIndex=0 + 0x2C / 0x25 NCCH)
                byte[] retailCommon0 = Convert.FromHexString("64C5FD55DD3AD988325BAAEC5243DB98");
                byte[] retailSlot2C = Convert.FromHexString("B98E95CECA3E4D171F76A94DE934C053");
                byte[] retailSlot25 = Convert.FromHexString("CEE7D8AB30C00DAE850EF5E382AC5AF3");
                byte[] randomTitleKey = Convert.FromHexString("9E41C7305A18D20FB46EC829713AD504");

                // 1) Exact replica of user's Google Drive file:
                // "0004000000112F00 Inazuma Eleven Go - Light (CTR-P-AE4P) (v0.0.0) (W).piratelegit.cia"
                // Outer AES-128-CBC (CommonKeyIndex=0) + Inner Retail 0x2C AES-128-CTR + Cartridge-origin ExHeader without SDApplication bit
                string pirateLegitCia = Path.Combine(
                    tempDir,
                    "0004000000112F00 Inazuma Eleven Go - Light (CTR-P-AE4P) (v0.0.0) (W).piratelegit.cia");

                BuildSyntheticLuzV000MultiContentCia(
                    pirateLegitCia,
                    out var origRomFsAssets,
                    useRetailKeyX2CNcch: true,
                    keyX2C: retailSlot2C,
                    encryptCiaLayer: true,
                    commonKey0: retailCommon0,
                    plainTitleKey: randomTitleKey,
                    compressArc0TablesWithLz10: true,
                    omitSdApplicationFlagInSource: true,
                    nonZeroExeFsPadding: true);

                string preSha = CiaFormatInspector.ComputeStreamingFileSha256Hex(pirateLegitCia);

                var svc = new CiaWorkflowService();

                // Inspect with ZERO external aes_keys.txt (aesKeysPath: null)!
                var inspect = svc.InspectLocalCia(pirateLegitCia, aesKeysPath: null, computeFullSha256: true);
                Assert.True(inspect.IsValidCiaStructure, "Expected valid .piratelegit.cia structure.");
                Assert.True(inspect.CanProcessNow, $".piratelegit.cia must work out-of-the-box without aes_keys.txt! Warnings={string.Join("; ", inspect.LimitationsAndWarnings)}");
                Assert.False(inspect.UsedExternalAesKeys, "Built-in retail keys must not report external aes_keys.txt usage.");
                Assert.Equal(CiaEncryptionKind.EncryptedCiaLayer, inspect.EncryptionKind);
                Assert.Equal("0004000000112F00", inspect.TmdTitleIdHex);
                Assert.Equal("V.0.0.0", inspect.TmdTitleVersionFormatted);
                Assert.Equal(GameEdition.Light, inspect.DetectedEdition);
                Assert.True(inspect.RomFsReadable);
                Assert.True(inspect.HasIeAFa);

                // Randomize .piratelegit.cia -> 2DS Ready Plain .cia with all 5 modules active and NO aes_keys.txt!
                string out2dsCia = Path.Combine(tempDir, "InazumaElevenGO_Luz_2DS_Randomizado.cia");
                var opts = new RandomizerOptions
                {
                    Seed = "2DS-PIRATELEGIT-2026",
                    AesKeysPath = string.Empty
                };

                var runRep = svc.RandomizeCiaToCia(pirateLegitCia, out2dsCia, opts);
                Assert.True(runRep.CiaVerification.IsValid, "Cold 2DS hardware verifier must approve rebuilt .cia.");
                Assert.True(runRep.OutputIntegrityVerified, "Output integrity check must pass.");
                Assert.True(runRep.SourceImmutabilityVerified, "Source immutability check must pass.");
                Assert.True(runRep.PersistedDiffTelemetry.PersistedModifiedFilesCount >= 5, "All 5 randomization modules must persist binary table mutations into ie_a.fa.");

                // Prove source .piratelegit.cia was never modified
                Assert.Equal(preSha, CiaFormatInspector.ComputeStreamingFileSha256Hex(pirateLegitCia));

                // Prove rebuilt .cia passes cold zero-key inspection + ExHeader SDApplication flag + ARC0 decompressed TableChunkSize!
                var coldOut = CiaFormatInspector.InspectFile(out2dsCia, new CtrKeyStore());
                Assert.Equal(CiaEncryptionKind.PlainCiaPlainNcch, coldOut.EncryptionKind);
                Assert.True(coldOut.CanProcessNow);
                Assert.Equal(origRomFsAssets.Count, coldOut.RomFsFileCount);

                string extractedDir = Path.Combine(tempDir, "extracted_2ds_out");
                ExtractFullRomFsFromPlainCia(out2dsCia, extractedDir);

                // Check non-ie_a.fa retail RomFS files preserved byte-for-byte
                foreach (var kvp in origRomFsAssets)
                {
                    string diskPath = Path.Combine(extractedDir, kvp.Key.Replace('/', Path.DirectorySeparatorChar));
                    Assert.True(File.Exists(diskPath), $"Expected RomFS file preserved: {kvp.Key}");
                    if (!string.Equals(kvp.Key, "ie_a.fa", StringComparison.Ordinal))
                    {
                        Assert.True(kvp.Value.AsSpan().SequenceEqual(File.ReadAllBytes(diskPath)),
                            $"Untouched RomFS file '{kvp.Key}' must remain byte-for-byte identical.");
                    }
                }

                // Check Level-5 ARC0 TableChunkSize >= decompressed table size (critical for real 2DS ARM11 heap!)
                byte[] outIea = File.ReadAllBytes(Path.Combine(extractedDir, "ie_a.fa"));
                short dirEntriesCount = BinaryPrimitives.ReadInt16LittleEndian(outIea.AsSpan(0x18, 2));
                short dirHashCount = BinaryPrimitives.ReadInt16LittleEndian(outIea.AsSpan(0x1A, 2));
                int fileEntriesCount = BinaryPrimitives.ReadInt32LittleEndian(outIea.AsSpan(0x1C, 4));
                int tableChunkSize = BinaryPrimitives.ReadInt32LittleEndian(outIea.AsSpan(0x20, 4));
                int minTableBytes = (dirEntriesCount * 20 + dirHashCount * 4 + fileEntriesCount * 16 + 0x20 + 3) & ~3;
                Assert.True(tableChunkSize >= minTableBytes,
                    $"2DS ARM11 heap guard: ARC0 TableChunkSize ({tableChunkSize}) must be >= decompressed minimum ({minTableBytes}).");

                // 2) Also verify 7.x split-key NCCH (slot0x25KeyX for .code vs slot0x2CKeyX for ExeFS header & padding) with non-16B-aligned .code!
                string splitKey7xCia = Path.Combine(tempDir, "Luz_7x_SplitKey_PirateLegit.cia");
                BuildSyntheticLuzV000MultiContentCia(
                    splitKey7xCia,
                    out _,
                    useRetailKeyX2CNcch: true,
                    keyX2C: retailSlot2C,
                    use7xKeyX25Secondary: true,
                    keyX25: retailSlot25,
                    encryptCiaLayer: true,
                    commonKey0: retailCommon0,
                    plainTitleKey: randomTitleKey,
                    nonZeroExeFsPadding: true);

                string out7xCia = Path.Combine(tempDir, "Luz_7x_Out.cia");
                var rep7x = svc.RandomizeCiaToCia(splitKey7xCia, out7xCia, new RandomizerOptions { Seed = "7X-SPLIT-KEY-2DS" });
                Assert.True(rep7x.CiaVerification.IsValid, "7.x split-key .code + padding NCCH must decrypt and rebuild with valid ExeFS SHA-256s.");

                // 3) Verify LocalDiagnosticService diffs encrypted .piratelegit.cia vs generated 2DS .cia cleanly!
                var diagResult = IEGORandomizer.Engine.Diagnostics.LocalDiagnosticService.RunDiagnostics(
                    pirateLegitCia,
                    GameEdition.Auto,
                    out2dsCia);
                Assert.True(diagResult.HasValidIeAFa, "LocalDiagnosticService must inspect encrypted .piratelegit.cia cleanly.");
                Assert.NotNull(diagResult.TargetOutputDiff);
                Assert.True(diagResult.TargetOutputDiff.PersistedModifiedFilesCount >= 5, "Diagnostic diff against randomized .cia must detect mutated inner ARC0 tables.");
            }
            finally
            {
                TryCleanup(tempDir);
            }
        }

        [Fact]
        public void Real2dsCaseSensitiveRomFs_WindowsUppercaseOverlayPreservesCanonicalRetailCasingAndZeroByteFiles()
        {
            string tempDir = CreateTempTestDir("cia_2ds_casing");
            try
            {
                string sourceCia = Path.Combine(tempDir, "Luz_RetailCasing.cia");
                BuildSyntheticLuzV000MultiContentCia(
                    sourceCia,
                    out var origAssets,
                    includeZeroByteSentinelFile: true);

                string stageOutDir = Path.Combine(tempDir, "stage_classic");
                var classicRun = new RandomizerCoordinator().GenerateRun(
                    sourceCia,
                    stageOutDir,
                    new RandomizerOptions { Seed = "CASE-TEST-2DS" });
                string modIeaPath = classicRun.ExportedPaths.First(p => p.EndsWith("ie_a.fa", StringComparison.OrdinalIgnoreCase));

                // Simulate Windows modder folder with UPPERCASE / mixed-case paths + new custom file + 0-byte custom file
                string upperModRomFs = Path.Combine(tempDir, "MOD_ROMFS_UPPER");
                Directory.CreateDirectory(Path.Combine(upperModRomFs, "CTR", "EXSAVE"));
                Directory.CreateDirectory(Path.Combine(upperModRomFs, "Snd", "Product"));

                byte[] expectedModIea = File.ReadAllBytes(modIeaPath);
                byte[] expectedModFixXr = Encoding.ASCII.GetBytes("MODIFIED-FIX-XR-PAYLOAD-UPPERCASE-OVERLAY");
                byte[] expectedModSound = Encoding.ASCII.GetBytes("MODIFIED-BCSAR-SOUND-PAYLOAD-2DS");
                byte[] expectedCustomNew = Encoding.ASCII.GetBytes("NEW-CUSTOM-STREAM-FILE-IN-CANONICAL-DIR");

                File.WriteAllBytes(Path.Combine(upperModRomFs, "IE_A.FA"), expectedModIea);
                File.WriteAllBytes(Path.Combine(upperModRomFs, "FIX.XR"), expectedModFixXr);
                File.WriteAllBytes(Path.Combine(upperModRomFs, "Snd", "Product", "IE4_SOUND.BCSAR"), expectedModSound);
                File.WriteAllBytes(Path.Combine(upperModRomFs, "Snd", "Product", "custom_theme.bcstm"), expectedCustomNew);
                File.WriteAllBytes(Path.Combine(upperModRomFs, "Snd", "Product", "zero_len_mod.bin"), Array.Empty<byte>());

                string mergedCia = Path.Combine(tempDir, "Luz_Merged_Canonical2ds.cia");
                var svc = new CiaWorkflowService();
                var mergeRep = svc.MergeModifiedRomFsIntoCia(sourceCia, upperModRomFs, mergedCia);

                Assert.True(mergeRep.Verification.IsValid, "Merged CIA with uppercase Windows overlay must pass strict 2DS cold verification.");

                // Open rebuilt RomFS and assert exact Ordinal retail casing + 3DS kernel CalcPathHash lookup!
                var rep = CiaFormatInspector.InspectFile(mergedCia, new CtrKeyStore());
                var c0 = rep.ContentChunks.First(c => c.ContentIndex == 0);
                using var fs = new FileStream(mergedCia, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] ncchHdr = new byte[0x200];
                fs.Seek(c0.CiaFileOffset, SeekOrigin.Begin);
                fs.ReadExactly(ncchHdr);
                long romfsOff = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.AsSpan(0x1B0, 4)) * 0x200L;
                long romfsLen = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.AsSpan(0x1B4, 4)) * 0x200L;
                using var romfsSlice = new SubReadStream(fs, c0.CiaFileOffset + romfsOff, romfsLen, leaveOpen: true);
                IvfcRomFsReader.VerifyIvfcAndHashTablesOrThrow(romfsSlice, out _, out var ivfc);

                // Must NOT have duplicate uppercase entries!
                Assert.False(ivfc.Files.Any(f => f.RelativePath == "IE_A.FA"), "Uppercase IE_A.FA must be canonicalized to retail lowercase ie_a.fa.");
                Assert.False(ivfc.Files.Any(f => f.RelativePath.StartsWith("Snd/", StringComparison.Ordinal)), "Mixed-case Snd/Product must be canonicalized to snd/product.");

                // Exact Ordinal path presence in verified kernel hash-bucket inventory!
                Assert.True(ivfc.Files.Any(f => f.RelativePath == "ie_a.fa"));
                Assert.True(ivfc.Files.Any(f => f.RelativePath == "fix.xr"));
                Assert.True(ivfc.Files.Any(f => f.RelativePath == "ctr/exsave/banner.icn"));
                Assert.True(ivfc.Files.Any(f => f.RelativePath == "snd/product/ie4_sound.bcsar"));
                Assert.True(ivfc.Files.Any(f => f.RelativePath == "snd/product/custom_theme.bcstm"));
                Assert.True(ivfc.Files.Any(f => f.RelativePath == "data/empty_sentinel.bin"));
                Assert.True(ivfc.Files.Any(f => f.RelativePath == "snd/product/zero_len_mod.bin"));

                var zeroOrig = ivfc.Files.First(f => f.RelativePath == "data/empty_sentinel.bin");
                Assert.Equal(0L, zeroOrig.DataLength);
                Assert.Equal(ivfc.FileDataBaseOffsetInRomFs, zeroOrig.DataOffsetInRomFs);

                var zeroMod = ivfc.Files.First(f => f.RelativePath == "snd/product/zero_len_mod.bin");
                Assert.Equal(0L, zeroMod.DataLength);
                Assert.Equal(ivfc.FileDataBaseOffsetInRomFs, zeroMod.DataOffsetInRomFs);

                string checkExtractDir = Path.Combine(tempDir, "check_extract");
                ExtractFullRomFsFromPlainCia(mergedCia, checkExtractDir);
                Assert.True(expectedModFixXr.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(checkExtractDir, "fix.xr"))));
            }
            finally
            {
                TryCleanup(tempDir);
            }
        }

        [Fact]
        public void RealRetailIeGoLuzAssets_WhenPresentInWorkspace_FullPirateLegitCiaTo2dsHardwareFlow()
        {
            const string retailLuzDir = "/tmp/ie luz romFS";
            string retailBanner = Path.Combine(retailLuzDir, "ctr", "exsave", "banner.icn");
            string retailFixXr = Path.Combine(retailLuzDir, "fix.xr");
            string retailSound = Path.Combine(retailLuzDir, "snd", "product", "product_es.bcsar");
            string retailMovie = Path.Combine(retailLuzDir, "movie", "logo_l5.moflex");

            if (!Directory.Exists(retailLuzDir) || !File.Exists(retailBanner) || !File.Exists(retailFixXr))
            {
                // Clean no-op on remote Windows CI runners where /tmp/ie luz romFS is not mounted
                return;
            }

            string tempDir = CreateTempTestDir("real_luz_assets_2ds");
            try
            {
                byte[] retailCommon0 = Convert.FromHexString("64C5FD55DD3AD988325BAAEC5243DB98");
                byte[] retailSlot2C = Convert.FromHexString("B98E95CECA3E4D171F76A94DE934C053");
                byte[] titleKey = Convert.FromHexString("3A7F91C20E58B4D6129EC40587A3B1F0");

                // Package real retail Inazuma Eleven GO: Luz (CTR-P-AE4P) RomFS assets (banner.icn, fix.xr, product_es.bcsar, logo_l5.moflex)
                // together with LZ10-compressed ARC0 ie_a.fa into a double-encrypted .piratelegit.cia!
                var extraRealAssets = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["fix.xr"] = File.ReadAllBytes(retailFixXr),
                    ["ctr/exsave/banner.icn"] = File.ReadAllBytes(retailBanner),
                    ["snd/product/product_es.bcsar"] = File.Exists(retailSound) ? File.ReadAllBytes(retailSound) : Encoding.ASCII.GetBytes("BCSAR"),
                    ["movie/logo_l5.moflex"] = File.Exists(retailMovie) ? File.ReadAllBytes(retailMovie) : Encoding.ASCII.GetBytes("MOFLEX")
                };

                string realPirateLegitCia = Path.Combine(
                    tempDir,
                    "0004000000112F00 Inazuma Eleven Go - Light (CTR-P-AE4P) (v0.0.0) (W).piratelegit.cia");

                BuildSyntheticLuzV000MultiContentCia(
                    realPirateLegitCia,
                    out var builtAssets,
                    useRetailKeyX2CNcch: true,
                    keyX2C: retailSlot2C,
                    encryptCiaLayer: true,
                    commonKey0: retailCommon0,
                    plainTitleKey: titleKey,
                    compressArc0TablesWithLz10: true,
                    customRomFsOverrides: extraRealAssets);

                var svc = new CiaWorkflowService();
                var inspect = svc.InspectLocalCia(realPirateLegitCia, aesKeysPath: null);
                Assert.True(inspect.CanProcessNow, "Real retail IEGO Luz .piratelegit.cia must inspect cleanly with zero external keys.");
                Assert.True(inspect.HasIeAFa && inspect.HasFixXr && inspect.HasBannerIcn);

                string out2dsCia = Path.Combine(tempDir, "IEGO_Luz_RealAssets_2DS_Ready.cia");
                var res = svc.RandomizeCiaToCia(
                    realPirateLegitCia,
                    out2dsCia,
                    new RandomizerOptions { Seed = "REAL-IEGO-LUZ-2DS" });

                Assert.True(res.CiaVerification.IsValid);

                string extractOut = Path.Combine(tempDir, "extract_real_out");
                ExtractFullRomFsFromPlainCia(out2dsCia, extractOut);

                Assert.True(extraRealAssets["fix.xr"].AsSpan().SequenceEqual(
                    File.ReadAllBytes(Path.Combine(extractOut, "fix.xr"))));
                Assert.True(extraRealAssets["ctr/exsave/banner.icn"].AsSpan().SequenceEqual(
                    File.ReadAllBytes(Path.Combine(extractOut, "ctr", "exsave", "banner.icn"))));
                Assert.True(extraRealAssets["snd/product/product_es.bcsar"].AsSpan().SequenceEqual(
                    File.ReadAllBytes(Path.Combine(extractOut, "snd", "product", "product_es.bcsar"))));
                Assert.True(extraRealAssets["movie/logo_l5.moflex"].AsSpan().SequenceEqual(
                    File.ReadAllBytes(Path.Combine(extractOut, "movie", "logo_l5.moflex"))));
            }
            finally
            {
                TryCleanup(tempDir);
            }
        }

        // =========================================================================
        // Synthetic Spec-Exact Multi-Content Nintendo 3DS .CIA Fixture Builder
        // =========================================================================

        internal static void BuildSyntheticLuzV000MultiContentCia(
            string outputCiaPath,
            out Dictionary<string, byte[]> romFsAssets,
            bool useFixedKeyNcch = false,
            bool useRetailKeyX2CNcch = false,
            byte[] keyX2C = null,
            bool use7xKeyX25Secondary = false,
            byte[] keyX25 = null,
            bool encryptCiaLayer = false,
            byte[] commonKey0 = null,
            byte[] plainTitleKey = null,
            bool compressArc0TablesWithLz10 = false,
            bool omitSdApplicationFlagInSource = false,
            bool nonZeroExeFsPadding = false,
            bool includeZeroByteSentinelFile = false,
            Dictionary<string, byte[]> customRomFsOverrides = null)
        {
            string stageDir = CreateTempTestDir("cia_fixture_build");
            try
            {
                string ieaPath = Path.Combine(stageDir, "ie_a.fa");
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(ieaPath);
                if (compressArc0TablesWithLz10)
                {
                    CompressArc0TablesWithRetailLz10Layout(ieaPath);
                }

                byte[] ieaBytes = File.ReadAllBytes(ieaPath);
                byte[] fixXrBytes = Encoding.ASCII.GetBytes("LEVEL5-FIX-XR-ARCHIVE-HEADER-V000-SYNTHETIC");
                byte[] bannerIcnBytes = CreateSyntheticSmdhBytes("Inazuma Eleven GO: Luz");
                byte[] bcsarBytes = Encoding.ASCII.GetBytes("CSAR-SYNTHETIC-IE4-SOUND-BANK-DATA-0123456789");
                byte[] moflexBytes = Encoding.ASCII.GetBytes("L5V-SYNTHETIC-MOFLEX-ENDING-VIDEO-STREAM-DATA");

                romFsAssets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ie_a.fa"] = ieaBytes,
                    ["fix.xr"] = fixXrBytes,
                    ["ctr/exsave/banner.icn"] = bannerIcnBytes,
                    ["snd/product/ie4_sound.bcsar"] = bcsarBytes,
                    ["movie/ed00s_c.moflex"] = moflexBytes
                };

                if (includeZeroByteSentinelFile)
                {
                    romFsAssets["data/empty_sentinel.bin"] = Array.Empty<byte>();
                }

                if (customRomFsOverrides != null)
                {
                    romFsAssets = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                    {
                        ["ie_a.fa"] = ieaBytes
                    };
                    foreach (var kvp in customRomFsOverrides)
                    {
                        romFsAssets[kvp.Key] = kvp.Value;
                        if (string.Equals(kvp.Key, "ctr/exsave/banner.icn", StringComparison.OrdinalIgnoreCase) &&
                            kvp.Value.Length == CiaConstants.SmdhByteSize)
                        {
                            bannerIcnBytes = kvp.Value;
                        }
                    }
                }

                // Build Content 0 (Main CXI with ExHeader + ExeFS + IVFC RomFS), Content 1 (Manual CFA), Content 2 (DLP CFA)
                byte[] content0 = BuildSyntheticNcchPartition(
                    LuzEurTitleId,
                    LuzEurTitleId,
                    "CTR-P-AE4P",
                    includeExHeaderAndExeFs: true,
                    romFsAssets,
                    new[] { "empty_dir_preserved" },
                    useFixedKeyNcch,
                    useRetailKeyX2CNcch,
                    keyX2C,
                    use7xKeyX25Secondary,
                    keyX25,
                    omitSdApplicationFlagInSource,
                    nonZeroExeFsPadding);

                byte[] content1 = BuildSyntheticNcchPartition(
                    LuzEurTitleId | 0x0000000100000000UL,
                    LuzEurTitleId,
                    "CTR-P-AE4P",
                    includeExHeaderAndExeFs: false,
                    new Dictionary<string, byte[]> { ["manual.bcma"] = Encoding.UTF8.GetBytes("ELECTRONIC-MANUAL-CFA") },
                    Array.Empty<string>(),
                    useFixedKeyNcch,
                    useRetailKeyX2CNcch,
                    keyX2C);

                byte[] content2 = BuildSyntheticNcchPartition(
                    LuzEurTitleId | 0x0000000200000000UL,
                    LuzEurTitleId,
                    "CTR-P-AE4P",
                    includeExHeaderAndExeFs: false,
                    new Dictionary<string, byte[]> { ["dlp_child.cfa"] = Encoding.UTF8.GetBytes("DOWNLOAD-PLAY-CHILD-CFA") },
                    Array.Empty<string>(),
                    useFixedKeyNcch,
                    useRetailKeyX2CNcch,
                    keyX2C);

                byte[][] rawPlainContents = { content0, content1, content2 };
                byte[][] storedContents = new byte[3][];
                byte[][] tmdChunkHashes = new byte[3][];

                // Note: Per real 3DS TMD spec, TMD SHA-256 hashes are ALWAYS computed over the PLAINTEXT (pre-CIA-CBC) NCCH!
                // Wait: in 3DS CIA/TMD, TMD ContentChunk.Hash is the SHA-256 of the decrypted content (NCCH), which is why our Plain CIA output matches TMD!
                for (int i = 0; i < 3; i++)
                {
                    tmdChunkHashes[i] = SHA256.HashData(rawPlainContents[i]);
                    if (encryptCiaLayer)
                    {
                        byte[] iv = new byte[16];
                        iv[0] = 0;
                        iv[1] = (byte)i;
                        using var aes = Aes.Create();
                        aes.KeySize = 128;
                        aes.Key = plainTitleKey!;
                        storedContents[i] = aes.EncryptCbc(rawPlainContents[i], iv, PaddingMode.None);
                    }
                    else
                    {
                        storedContents[i] = rawPlainContents[i];
                    }
                }

                byte[] certBytes = new byte[0xA00]; // Standard 2560-byte cert chain placeholder
                Encoding.ASCII.GetBytes("Root-CA00000003").CopyTo(certBytes, 0);

                // Ticket (0x140 RSA-2048 sig + 0x164 body = 0x2A4, padded to 0x350)
                byte[] tikBytes = new byte[0x350];
                BinaryPrimitives.WriteUInt32BigEndian(tikBytes.AsSpan(0, 4), CiaConstants.SigTypeRsa2048Sha256);
                int tikBody = 0x140;
                Encoding.ASCII.GetBytes("Root-CA00000003-XS0000000c").CopyTo(tikBytes, tikBody);
                BinaryPrimitives.WriteUInt64BigEndian(tikBytes.AsSpan(tikBody + 0x9C, 8), LuzEurTitleId);
                tikBytes[tikBody + 0xB1] = 0; // CommonKeyIndex = 0 (eShop/Standard)

                if (encryptCiaLayer)
                {
                    byte[] tikIv = new byte[16];
                    BinaryPrimitives.WriteUInt64BigEndian(tikIv.AsSpan(0, 8), LuzEurTitleId);
                    using var aesTik = Aes.Create();
                    aesTik.KeySize = 128;
                    aesTik.Key = commonKey0!;
                    byte[] encTitleKey = aesTik.EncryptCbc(plainTitleKey!, tikIv, PaddingMode.None);
                    Buffer.BlockCopy(encTitleKey, 0, tikBytes, tikBody + 0x7F, 16);
                }

                // TMD (0x140 sig + 0x9C4 body + 3 * 0x30 chunks)
                int tmdSize = 0x140 + 0x9C4 + 3 * 0x30;
                byte[] tmdBytes = new byte[tmdSize];
                BinaryPrimitives.WriteUInt32BigEndian(tmdBytes.AsSpan(0, 4), CiaConstants.SigTypeRsa2048Sha256);
                int tmdBody = 0x140;
                Encoding.ASCII.GetBytes("Root-CA00000003-CP0000000b").CopyTo(tmdBytes, tmdBody);
                BinaryPrimitives.WriteUInt64BigEndian(tmdBytes.AsSpan(tmdBody + 0x4C, 8), LuzEurTitleId);
                BinaryPrimitives.WriteUInt32BigEndian(tmdBytes.AsSpan(tmdBody + 0x54, 4), 0x00000040u);
                BinaryPrimitives.WriteUInt32LittleEndian(tmdBytes.AsSpan(tmdBody + 0x5A, 4), LuzSaveDataSize); // LE SaveDataSize!
                BinaryPrimitives.WriteUInt16BigEndian(tmdBytes.AsSpan(tmdBody + 0x9C, 2), 0x0000); // V.0.0.0!
                BinaryPrimitives.WriteUInt16BigEndian(tmdBytes.AsSpan(tmdBody + 0x9E, 2), 3);      // 3 contents

                int chunksOff = tmdBody + 0x9C4;
                for (ushort i = 0; i < 3; i++)
                {
                    int cpos = chunksOff + i * 0x30;
                    BinaryPrimitives.WriteUInt32BigEndian(tmdBytes.AsSpan(cpos + 0x00, 4), i);
                    BinaryPrimitives.WriteUInt16BigEndian(tmdBytes.AsSpan(cpos + 0x04, 2), i);
                    ushort flags = encryptCiaLayer ? CiaConstants.TmdContentFlagEncrypted : (ushort)0;
                    BinaryPrimitives.WriteUInt16BigEndian(tmdBytes.AsSpan(cpos + 0x06, 2), flags);
                    BinaryPrimitives.WriteUInt64BigEndian(tmdBytes.AsSpan(cpos + 0x08, 8), (ulong)storedContents[i].Length);
                    Buffer.BlockCopy(tmdChunkHashes[i], 0, tmdBytes, cpos + 0x10, 0x20);
                }

                // Populate ContentInfo[0] & TMD.InfoRecordHash
                BinaryPrimitives.WriteUInt16BigEndian(tmdBytes.AsSpan(tmdBody + 0xC4 + 0, 2), 0);
                BinaryPrimitives.WriteUInt16BigEndian(tmdBytes.AsSpan(tmdBody + 0xC4 + 2, 2), 3);
                SHA256.HashData(tmdBytes.AsSpan(chunksOff, 3 * 0x30), tmdBytes.AsSpan(tmdBody + 0xC4 + 4, 0x20));
                SHA256.HashData(tmdBytes.AsSpan(tmdBody + 0xC4, 0x900), tmdBytes.AsSpan(tmdBody + 0xA4, 0x20));

                // Meta (0x400 + 0x36C0 SMDH = 0x3AC0)
                byte[] metaBytes = new byte[CiaConstants.StandardCiaMetaSize];
                BinaryPrimitives.WriteUInt32LittleEndian(metaBytes.AsSpan(0x180, 4), 2u);
                Buffer.BlockCopy(bannerIcnBytes, 0, metaBytes, CiaConstants.CiaMetaBaseSize, bannerIcnBytes.Length);

                long totalContentLen = storedContents.Sum(c => (long)c.Length);
                byte[] ciaHdr = new byte[CiaConstants.CiaArchiveHeaderSize];
                BinaryPrimitives.WriteUInt32LittleEndian(ciaHdr.AsSpan(0x00, 4), CiaConstants.CiaArchiveHeaderSize);
                BinaryPrimitives.WriteUInt32LittleEndian(ciaHdr.AsSpan(0x08, 4), (uint)certBytes.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(ciaHdr.AsSpan(0x0C, 4), (uint)tikBytes.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(ciaHdr.AsSpan(0x10, 4), (uint)tmdBytes.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(ciaHdr.AsSpan(0x14, 4), (uint)metaBytes.Length);
                BinaryPrimitives.WriteUInt64LittleEndian(ciaHdr.AsSpan(0x18, 8), (ulong)totalContentLen);
                CiaFormatInspector.SetContentBit(ciaHdr.AsSpan(0x20, 0x2000), 0);
                CiaFormatInspector.SetContentBit(ciaHdr.AsSpan(0x20, 0x2000), 1);
                CiaFormatInspector.SetContentBit(ciaHdr.AsSpan(0x20, 0x2000), 2);

                long certOff = CiaConstants.AlignUp(ciaHdr.Length, 0x40);
                long tikOff = CiaConstants.AlignUp(certOff + certBytes.Length, 0x40);
                long tmdOff = CiaConstants.AlignUp(tikOff + tikBytes.Length, 0x40);
                long contentOff = CiaConstants.AlignUp(tmdOff + tmdBytes.Length, 0x40);
                long metaOff = CiaConstants.AlignUp(contentOff + totalContentLen, 0x40);

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputCiaPath))!);
                using var fs = new FileStream(outputCiaPath, FileMode.Create, FileAccess.Write, FileShare.None);
                fs.Write(ciaHdr);
                fs.Seek(certOff, SeekOrigin.Begin);
                fs.Write(certBytes);
                fs.Seek(tikOff, SeekOrigin.Begin);
                fs.Write(tikBytes);
                fs.Seek(tmdOff, SeekOrigin.Begin);
                fs.Write(tmdBytes);
                fs.Seek(contentOff, SeekOrigin.Begin);
                foreach (byte[] c in storedContents)
                    fs.Write(c);
                fs.Seek(metaOff, SeekOrigin.Begin);
                fs.Write(metaBytes);
            }
            finally
            {
                TryCleanup(stageDir);
            }
        }

        private static byte[] BuildSyntheticNcchPartition(
            ulong partitionId,
            ulong programId,
            string productCode,
            bool includeExHeaderAndExeFs,
            Dictionary<string, byte[]> files,
            IEnumerable<string> extraDirs,
            bool useFixedKeyNcch,
            bool useRetailKeyX2C,
            byte[] keyX2C,
            bool use7xKeyX25Secondary = false,
            byte[] keyX25 = null,
            bool omitSdApplicationFlag = false,
            bool nonZeroExeFsPadding = false)
        {
            using var ms = new MemoryStream();
            byte[] hdr = new byte[0x200];

            // Deterministic RSA signature bytes [0x00..0xFF] (first 16 bytes act as KeyY!)
            for (int i = 0; i < 0x100; i++)
                hdr[i] = (byte)(0xA5 ^ i);

            Encoding.ASCII.GetBytes("NCCH").CopyTo(hdr, 0x100);
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(0x108, 8), partitionId);
            BinaryPrimitives.WriteUInt16LittleEndian(hdr.AsSpan(0x112, 2), 2); // NCCH v2
            BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(0x118, 8), programId);
            Encoding.ASCII.GetBytes(productCode).CopyTo(hdr, 0x150);

            ms.Write(hdr, 0, 0x200);

            long exefsOffset = 0;
            long exefsSize = 0;
            int codeSecLen = 0;
            if (includeExHeaderAndExeFs)
            {
                byte[] exHeader800 = new byte[0x800];
                Encoding.ASCII.GetBytes("IEGO_APP").CopyTo(exHeader800, 0);
                if (!omitSdApplicationFlag)
                {
                    exHeader800[0x0D] |= 0x02; // SCI.Flags SDApplication bit
                }
                BinaryPrimitives.WriteUInt64LittleEndian(exHeader800.AsSpan(0x1C8, 8), programId);
                BinaryPrimitives.WriteUInt64LittleEndian(exHeader800.AsSpan(0x200, 8), programId);
                BinaryPrimitives.WriteUInt64LittleEndian(exHeader800.AsSpan(0x600, 8), programId);
                SHA256.HashData(exHeader800.AsSpan(0, 0x400), hdr.AsSpan(0x160, 0x20));
                BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x180, 4), 0x400);
                ms.Write(exHeader800, 0, exHeader800.Length);

                exefsOffset = ms.Position;
                byte[] exefs = new byte[0x600]; // 0x200 header + 0x200 .code + 0x200 icon
                // Non-16-byte-aligned .code size (333 bytes) exercises split-block CTR decryption on 7.x NCCHs!
                byte[] codeSec = new byte[333];
                for (int i = 0; i < codeSec.Length; i++)
                    codeSec[i] = (byte)(((i * 31) ^ 0x5A) & 0xFF);
                codeSecLen = codeSec.Length;

                byte[] iconSec = Encoding.ASCII.GetBytes("SMDH-MINI-ICON-SECTION-PAYLOAD-EXACT-HASH");

                if (nonZeroExeFsPadding)
                {
                    for (int i = 0x200; i < 0x600; i++)
                        exefs[i] = 0xCC;
                }

                Encoding.ASCII.GetBytes(".code").CopyTo(exefs, 0x00);
                BinaryPrimitives.WriteUInt32LittleEndian(exefs.AsSpan(0x08, 4), 0u);
                BinaryPrimitives.WriteUInt32LittleEndian(exefs.AsSpan(0x0C, 4), (uint)codeSec.Length);
                Buffer.BlockCopy(codeSec, 0, exefs, 0x200, codeSec.Length);
                // Per Nintendo makerom/src/exefs.c:76, SHA-256 is computed over exact fileSize[i], NOT padded 0x200!
                SHA256.HashData(codeSec, exefs.AsSpan(0x200 - 0x20, 0x20));

                Encoding.ASCII.GetBytes("icon").CopyTo(exefs, 0x10);
                BinaryPrimitives.WriteUInt32LittleEndian(exefs.AsSpan(0x18, 4), 0x200u);
                BinaryPrimitives.WriteUInt32LittleEndian(exefs.AsSpan(0x1C, 4), (uint)iconSec.Length);
                Buffer.BlockCopy(iconSec, 0, exefs, 0x400, iconSec.Length);
                SHA256.HashData(iconSec, exefs.AsSpan(0x200 - 0x40, 0x20));

                SHA256.HashData(exefs.AsSpan(0, 0x200), hdr.AsSpan(0x1C0, 0x20));
                exefsSize = exefs.Length;
                BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x1A0, 4), (uint)(exefsOffset / 0x200));
                BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x1A4, 4), (uint)(exefsSize / 0x200));
                BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x1A8, 4), 1u);
                ms.Write(exefs, 0, exefs.Length);
            }

            // Align RomFS to 0x1000 inside NCCH
            while (ms.Position % 0x1000 != 0)
                ms.WriteByte(0);

            long romfsOffset = ms.Position;
            var specs = files.Select(kvp => new RomFsBuildFileSpec
            {
                RelativePath = kvp.Key,
                Length = kvp.Value.Length,
                WriteContentTo = s => s.Write(kvp.Value, 0, kvp.Value.Length)
            }).ToList();

            var ivfc = StreamingIvfcRomFsBuilder.BuildToStream(ms, romfsOffset, extraDirs, specs);
            long totalLen = romfsOffset + ivfc.TotalIvfcSizeBytes;

            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x104, 4), (uint)(totalLen / 0x200));
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x1B0, 4), (uint)(romfsOffset / 0x200));
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x1B4, 4), (uint)(ivfc.TotalIvfcSizeBytes / 0x200));
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0x1B8, 4), ivfc.HashRegionSizeMediaUnits);
            Buffer.BlockCopy(ivfc.SuperblockSha256, 0, hdr, 0x1E0, 0x20);

            byte[] ncchBytes = ms.ToArray();

            if (useFixedKeyNcch)
            {
                hdr[0x18B] = 0x00;
                hdr[0x18F] = 0x01; // FixedCryptoKey (all-zero key)
                byte[] zeroKey = new byte[16];
                EncryptNcchRegionsInPlace(ncchBytes, partitionId, includeExHeaderAndExeFs, exefsOffset, exefsSize, codeSecLen, romfsOffset, ivfc.TotalIvfcSizeBytes, zeroKey, zeroKey);
            }
            else if (useRetailKeyX2C)
            {
                hdr[0x18B] = use7xKeyX25Secondary ? (byte)0x01 : (byte)0x00; // 0x00 = Slot 0x2C, 0x01 = 7.x Slot 0x25 secondary
                hdr[0x18F] = 0x00; // Standard retail NCCH crypto
                byte[] primaryKey = CtrKeyStore.DeriveNormalKey(keyX2C!, hdr.AsSpan(0, 16));
                byte[] secondaryKey = (use7xKeyX25Secondary && keyX25 != null)
                    ? CtrKeyStore.DeriveNormalKey(keyX25, hdr.AsSpan(0, 16))
                    : primaryKey;
                EncryptNcchRegionsInPlace(ncchBytes, partitionId, includeExHeaderAndExeFs, exefsOffset, exefsSize, codeSecLen, romfsOffset, ivfc.TotalIvfcSizeBytes, primaryKey, secondaryKey);
            }
            else
            {
                hdr[0x18B] = 0x00;
                hdr[0x18F] = 0x04; // NoCrypto
            }

            Buffer.BlockCopy(hdr, 0, ncchBytes, 0, 0x200);
            return ncchBytes;
        }

        private static void EncryptNcchRegionsInPlace(
            byte[] ncchBytes,
            ulong partitionIdLe,
            bool hasExHeaderAndExeFs,
            long exefsOffset,
            long exefsSize,
            int codeSecLen,
            long romfsOffset,
            long romfsSize,
            byte[] primaryKey,
            byte[] secondaryKey)
        {
            if (hasExHeaderAndExeFs)
            {
                XorCtrRangeByteExact(ncchBytes, 0x200, 0x800, 0x200, partitionIdLe, 1, primaryKey);
                // Per GodMode9 ncch.c:197-218:
                // - ExeFS header [0..0x200): primaryKey
                // - .code [0x200 .. 0x200 + codeSecLen): secondaryKey
                // - .code padding [0x200 + codeSecLen .. 0x400) and icon [0x400 .. exefsSize): primaryKey!
                XorCtrRangeByteExact(ncchBytes, exefsOffset, 0x200, exefsOffset, partitionIdLe, 2, primaryKey);
                XorCtrRangeByteExact(ncchBytes, exefsOffset + 0x200, codeSecLen, exefsOffset, partitionIdLe, 2, secondaryKey);
                XorCtrRangeByteExact(ncchBytes, exefsOffset + 0x200 + codeSecLen, exefsSize - (0x200 + codeSecLen), exefsOffset, partitionIdLe, 2, primaryKey);
            }
            XorCtrRangeByteExact(ncchBytes, romfsOffset, romfsSize, romfsOffset, partitionIdLe, 3, secondaryKey);
        }

        private static void XorCtrRangeByteExact(
            byte[] data,
            long start,
            long length,
            long ctrOrigin,
            ulong partitionIdLe,
            byte sectionType,
            byte[] key16)
        {
            if (length <= 0)
                return;

            Span<byte> baseCtrBytes = stackalloc byte[16];
            baseCtrBytes.Clear();
            BinaryPrimitives.WriteUInt64BigEndian(baseCtrBytes.Slice(0, 8), partitionIdLe);
            baseCtrBytes[8] = sectionType;
            UInt128 baseCtr = ReadBe128(baseCtrBytes);

            using var aes = Aes.Create();
            aes.KeySize = 128;
            aes.Key = key16;

            byte[] ctrBlock = new byte[16];
            long end = start + length;
            long pos = start;
            while (pos < end)
            {
                long relPos = pos - ctrOrigin;
                long blockIdx = relPos / 16;
                int byteInBlock = (int)(relPos % 16);

                UInt128 c = unchecked(baseCtr + (UInt128)(ulong)blockIdx);
                WriteBe128(ctrBlock, c);
                byte[] ks = aes.EncryptEcb(ctrBlock, PaddingMode.None);

                int take = (int)Math.Min(16 - byteInBlock, end - pos);
                for (int i = 0; i < take; i++)
                    data[pos + i] ^= ks[byteInBlock + i];

                pos += take;
            }
        }

        private static void CompressArc0TablesWithRetailLz10Layout(string ieaPath)
        {
            byte[] src = File.ReadAllBytes(ieaPath);
            using var msIn = new MemoryStream(src, writable: false);
            var r = new StudioElevenLib.Tools.BinaryDataReader(msIn);
            var h = r.ReadStruct<StudioElevenLib.Level5.Archive.ARC0.ARC0Support.Header>();

            r.Seek((uint)h.DirectoryEntriesOffset);
            byte[] decompDirEntries = StudioElevenLib.Level5.Compression.Compressor.Decompress(
                r.GetSection(h.DirectoryHashOffset - h.DirectoryEntriesOffset));
            r.Seek((uint)h.DirectoryHashOffset);
            byte[] decompDirHashes = StudioElevenLib.Level5.Compression.Compressor.Decompress(
                r.GetSection(h.FileEntriesOffset - h.DirectoryHashOffset));
            r.Seek((uint)h.FileEntriesOffset);
            byte[] decompFileEntries = StudioElevenLib.Level5.Compression.Compressor.Decompress(
                r.GetSection(h.NameOffset - h.FileEntriesOffset));
            r.Seek((uint)h.NameOffset);
            byte[] decompNames = StudioElevenLib.Level5.Compression.Compressor.Decompress(
                r.GetSection(h.DataOffset - h.NameOffset));

            var lz10 = new StudioElevenLib.Level5.Compression.LZ10.LZ10();
            var noComp = new StudioElevenLib.Level5.Compression.NoCompression.NoCompression();
            byte[] compDirEntries = decompDirEntries.Length > 0 ? lz10.Compress(decompDirEntries) : noComp.Compress(decompDirEntries);
            byte[] compDirHashes = decompDirHashes.Length > 0 ? lz10.Compress(decompDirHashes) : noComp.Compress(decompDirHashes);
            byte[] compFileEntries = decompFileEntries.Length > 0 ? lz10.Compress(decompFileEntries) : noComp.Compress(decompFileEntries);
            byte[] compNames = decompNames.Length > 0 ? lz10.Compress(decompNames) : noComp.Compress(decompNames);

            using var outFs = new FileStream(ieaPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var w = new StudioElevenLib.Tools.BinaryDataWriter(outFs);

            int dirOff = System.Runtime.InteropServices.Marshal.SizeOf<StudioElevenLib.Level5.Archive.ARC0.ARC0Support.Header>();
            w.Seek(dirOff);
            w.Write(compDirEntries);
            w.WriteAlignment(4);
            int hashOff = (int)w.Position;
            w.Write(compDirHashes);
            w.WriteAlignment(4);
            int fileOff = (int)w.Position;
            w.Write(compFileEntries);
            w.WriteAlignment(4);
            int nameOff = (int)w.Position;
            w.Write(compNames);
            w.WriteAlignment(4);
            int dataOff = (int)w.Position;

            outFs.Write(src, h.DataOffset, src.Length - h.DataOffset);

            int decompTableChunkSize = (h.DirectoryEntriesCount * 20 + h.DirectoryHashCount * 4 +
                                        h.FileEntriesCount * 16 + decompNames.Length + 0x20 + 3) & ~3;

            var newHeader = h;
            newHeader.DirectoryEntriesOffset = dirOff;
            newHeader.DirectoryHashOffset = hashOff;
            newHeader.FileEntriesOffset = fileOff;
            newHeader.NameOffset = nameOff;
            newHeader.DataOffset = dataOff;
            newHeader.TableChunkSize = decompTableChunkSize;
            w.Seek(0);
            w.WriteStruct(newHeader);
        }

        private static UInt128 ReadBe128(ReadOnlySpan<byte> b)
        {
            return new UInt128(
                BinaryPrimitives.ReadUInt64BigEndian(b.Slice(0, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(b.Slice(8, 8)));
        }

        private static void WriteBe128(Span<byte> b, UInt128 v)
        {
            BinaryPrimitives.WriteUInt64BigEndian(b.Slice(0, 8), (ulong)(v >> 64));
            BinaryPrimitives.WriteUInt64BigEndian(b.Slice(8, 8), (ulong)v);
        }

        private static byte[] CreateSyntheticSmdhBytes(string spanishTitle)
        {
            byte[] smdh = new byte[CiaConstants.SmdhByteSize];
            Encoding.ASCII.GetBytes("SMDH").CopyTo(smdh, 0);
            byte[] utf16 = Encoding.Unicode.GetBytes(spanishTitle);
            // Slot 5 = Spanish short title at SMDH + 0x08 + 5 * 0x200
            Buffer.BlockCopy(utf16, 0, smdh, 0x08 + 5 * 0x200, utf16.Length);
            // Slot 1 = English fallback
            Buffer.BlockCopy(utf16, 0, smdh, 0x08 + 1 * 0x200, utf16.Length);
            return smdh;
        }

        private static void ExtractFullRomFsFromPlainCia(string plainCiaPath, string targetDir)
        {
            var rep = CiaFormatInspector.InspectFile(plainCiaPath, new CtrKeyStore());
            var c0 = rep.ContentChunks.First(c => c.ContentIndex == 0 && c.IsPresentInCia);
            using var fs = new FileStream(plainCiaPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] ncchHdr = new byte[0x200];
            fs.Seek(c0.CiaFileOffset, SeekOrigin.Begin);
            fs.ReadExactly(ncchHdr);
            long romfsOff = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.AsSpan(0x1B0, 4)) * CiaConstants.NcchMediaUnitSize;
            long romfsLen = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.AsSpan(0x1B4, 4)) * CiaConstants.NcchMediaUnitSize;
            using var romfsSlice = new SubReadStream(fs, c0.CiaFileOffset + romfsOff, romfsLen, leaveOpen: true);
            IvfcRomFsReader.ExtractAllToDirectory(romfsSlice, targetDir);
        }

        [Fact]
        public void SpanishMediaDeduplication_AliasesNonSpanishStreamsInIvfcLevel3_PassesCold2dsVerifierAndShrinksCia()
        {
            string tempDir = CreateTempTestDir("cia_dedup_es");
            try
            {
                string baseCia = Path.Combine(tempDir, "Luz_Multi5.cia");
                BuildSyntheticLuzV000MultiContentCia(baseCia, out _);

                // Overlay 5 localized 32 KB .bcstm streams + 5 localized 32 KB .moflex streams into a multi-5 source .cia
                string multi5OverlayDir = Path.Combine(tempDir, "multi5_romfs");
                Directory.CreateDirectory(Path.Combine(multi5OverlayDir, "snd", "product", "stream"));
                Directory.CreateDirectory(Path.Combine(multi5OverlayDir, "movie"));

                byte[] esAudio = Encoding.ASCII.GetBytes("ES_AUDIO_HEADER_").Concat(Enumerable.Repeat((byte)0x53, 32768)).ToArray();
                byte[] esMovie = Encoding.ASCII.GetBytes("ES_MOVIE_HEADER_").Concat(Enumerable.Repeat((byte)0x4D, 32768)).ToArray();

                foreach (string lang in new[] { "de", "en", "es", "fr", "it" })
                {
                    byte[] audioPayload = lang == "es"
                        ? esAudio
                        : Encoding.ASCII.GetBytes(lang.ToUpperInvariant() + "_AUDIO_HEADER_").Concat(Enumerable.Repeat((byte)(lang[0]), 32768)).ToArray();
                    byte[] moviePayload = lang == "es"
                        ? esMovie
                        : Encoding.ASCII.GetBytes(lang.ToUpperInvariant() + "_MOVIE_HEADER_").Concat(Enumerable.Repeat((byte)(lang[1]), 32768)).ToArray();

                    File.WriteAllBytes(Path.Combine(multi5OverlayDir, "snd", "product", "stream", $"a4m01a_{lang}.bcstm"), audioPayload);
                    File.WriteAllBytes(Path.Combine(multi5OverlayDir, "movie", $"scene04_{lang}.moflex"), moviePayload);
                }

                string multi5SourceCia = Path.Combine(tempDir, "Luz_Multi5_Full.cia");
                CiaPackageRebuilder.RebuildFromRomFsDirectoryOverlay(
                    baseCia,
                    multi5OverlayDir,
                    multi5SourceCia,
                    buildOptions: new CiaBuildOptions { BumpVersionForFbiUpdate = false });

                long fullSize = new FileInfo(multi5SourceCia).Length;

                // Now randomize with DeduplicateOtherLanguageStreamsToSpanish = true (CLI --dedup-es)
                string dedupCia = Path.Combine(tempDir, "Luz_UpdatePatch_v1.2.3_2DS.cia");
                int cliExit = CliRunner.Run(new[]
                {
                    "--cli",
                    "--input", multi5SourceCia,
                    "--output-cia", dedupCia,
                    "--preset", "full",
                    "--seed", "DEDUP-2DS-ES",
                    "--dedup-es"
                });
                Assert.Equal(0, cliExit);

                long dedupSize = new FileInfo(dedupCia).Length;
                // Saved at least 8 * 32 KB = 256 KB of duplicated non-Spanish streams!
                Assert.True(dedupSize <= fullSize - (220 * 1024),
                    $"Expected deduplicated .cia ({dedupSize} B) to be >= 220 KB smaller than multi-5 .cia ({fullSize} B).");

                var coldVerify = CiaPackageVerifier.VerifyColdCiaFile(
                    dedupCia,
                    expectedTitleIdHex: "0004000000112F00",
                    expectedSaveDataSize: LuzSaveDataSize,
                    minimumExpectedRomFsFileCount: 15,
                    expectedTitleVersionRaw: CiaConstants.DefaultFbiUpdateTitleVersionRaw);
                Assert.True(coldVerify.IsValid, string.Join(" | ", coldVerify.Errors));

                // Extract full RomFS and verify all 5 language files exist and return valid Spanish media bytes!
                string extractedDir = Path.Combine(tempDir, "extracted_romfs");
                ExtractFullRomFsFromPlainCia(dedupCia, extractedDir);
                foreach (string lang in new[] { "de", "en", "es", "fr", "it" })
                {
                    byte[] gotAudio = File.ReadAllBytes(Path.Combine(extractedDir, "snd", "product", "stream", $"a4m01a_{lang}.bcstm"));
                    byte[] gotMovie = File.ReadAllBytes(Path.Combine(extractedDir, "movie", $"scene04_{lang}.moflex"));
                    Assert.SequenceEqual(esAudio, gotAudio);
                    Assert.SequenceEqual(esMovie, gotMovie);
                }
            }
            finally
            {
                TryCleanup(tempDir);
            }
        }

        [Fact]
        public void Arc0DeltaPatchPack_ReconstructsBitForBitOverVanillaAndPriorV122Install()
        {
            string tempDir = CreateTempTestDir("arc0_ie4p_delta");
            try
            {
                string sourceCia = Path.Combine(tempDir, "Luz_v000.cia");
                BuildSyntheticLuzV000MultiContentCia(sourceCia, out var origAssets);

                string vanillaIea = Path.Combine(tempDir, "vanilla_ie_a.fa");
                File.WriteAllBytes(vanillaIea, origAssets["ie_a.fa"]);

                // Append realistic untouched 3D assets (uniforms, stadiums, effects) to prove delta reuse
                var arc = Arc0SafeArchive.OpenReadOnly(vanillaIea);
                try
                {
                    byte[] stadiumBytes = new byte[128 * 1024];
                    for (int i = 0; i < stadiumBytes.Length; i++) stadiumBytes[i] = (byte)((i * 31 + 7) & 0xFF);
                    Arc0SafeArchive.WriteFileAtPath(arc.Directory, "/data/chr/model/waza/uniform/uni0001m1.xc", stadiumBytes);
                    Arc0SafeArchive.WriteFileAtPath(arc.Directory, "/data/map/m001a1/m001a1.pck", stadiumBytes);
                    string expandedVanilla = Path.Combine(tempDir, "vanilla_expanded_ie_a.fa");
                    Arc0SafeArchive.SaveCopyTo(arc, expandedVanilla, vanillaIea);
                    vanillaIea = expandedVanilla;
                }
                finally
                {
                    arc.Close();
                }

                // 1) Simulate an ALREADY-INSTALLED v1.2.2 "Todo Activado" game on the user's 2DS
                string oldV122OutDir = Path.Combine(tempDir, "installed_v122_out");
                int rcOld = CliRunner.Run(new[]
                {
                    "--cli",
                    "--input", vanillaIea,
                    "--output", oldV122OutDir,
                    "--preset", "full",
                    "--seed", "PRIOR-2DS-V122"
                });
                Assert.Equal(0, rcOld);
                string installedV122Iea = Directory.GetFiles(oldV122OutDir, "ie_a.fa", SearchOption.AllDirectories)[0];
                Assert.True(File.Exists(installedV122Iea));

                // 2) Run v1.2.3 CLI with --export-2ds-delta-pack
                string newV123OutDir = Path.Combine(tempDir, "target_v123_out");
                string deltaPackPath = Path.Combine(tempDir, "patch_v123.ie4p");
                int rcNew = CliRunner.Run(new[]
                {
                    "--cli",
                    "--input", vanillaIea,
                    "--output", newV123OutDir,
                    "--preset", "full",
                    "--seed", "PATCH-2DS-V123",
                    "--export-2ds-delta-pack", deltaPackPath
                });
                Assert.Equal(0, rcNew);

                string targetV123Iea = Directory.GetFiles(newV123OutDir, "ie_a.fa", SearchOption.AllDirectories)[0];
                Assert.True(File.Exists(deltaPackPath));
                string expectedSha = CiaFormatInspector.ComputeStreamingFileSha256Hex(targetV123Iea);

                // Delta pack must be much smaller than full ie_a.fa because untouched stadium/uniform assets are reused
                Assert.True(new FileInfo(deltaPackPath).Length < new FileInfo(targetV123Iea).Length - (200 * 1024),
                    "Delta pack must exclude untouched 3D stadium/uniform assets and compress embedded files.");

                // 3) Reconstruct over vanilla v0.0.0 ie_a.fa -> must match target v1.2.3 SHA-256 bit-for-bit!
                string reconOverVanilla = Path.Combine(tempDir, "recon_over_vanilla.fa");
                Arc0DeltaPatchPackBuilder.ApplyPackToStream(vanillaIea, deltaPackPath, reconOverVanilla);
                Assert.Equal(expectedSha, CiaFormatInspector.ComputeStreamingFileSha256Hex(reconOverVanilla));

                // 4) Reconstruct over ALREADY-RANDOMIZED v1.2.2 ie_a.fa -> must ALSO match target v1.2.3 SHA-256 bit-for-bit!
                string reconOverV122 = Path.Combine(tempDir, "recon_over_v122.fa");
                Arc0DeltaPatchPackBuilder.ApplyPackToStream(installedV122Iea, deltaPackPath, reconOverV122);
                Assert.Equal(expectedSha, CiaFormatInspector.ComputeStreamingFileSha256Hex(reconOverV122));

                // 5) Compile and run the exact portable ARM11 C99 engine (src/IEGORandomizer.Updater2DS/source/ie4p_core.c) if gcc is installed
                string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
                string hostVerifyC = Path.Combine(repoRoot, "src", "IEGORandomizer.Updater2DS", "host_verify.c");
                string coreC = Path.Combine(repoRoot, "src", "IEGORandomizer.Updater2DS", "source", "ie4p_core.c");
                if (File.Exists(hostVerifyC) && File.Exists(coreC) && File.Exists("/usr/bin/gcc"))
                {
                    string cBin = Path.Combine(tempDir, "ie4p_host_verify");
                    var gccProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "/usr/bin/gcc",
                        ArgumentList = { "-std=c99", "-O2", "-Wall", "-Wextra", "-Werror", hostVerifyC, coreC, "-o", cBin },
                        RedirectStandardError = true,
                        UseShellExecute = false
                    });
                    gccProc!.WaitForExit();
                    Assert.Equal(0, gccProc.ExitCode);

                    foreach (string baseArchive in new[] { vanillaIea, installedV122Iea })
                    {
                        string cOut = Path.Combine(tempDir, "c_recon_" + Path.GetFileName(baseArchive));
                        var runProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = cBin,
                            ArgumentList = { baseArchive, deltaPackPath, cOut },
                            RedirectStandardError = true,
                            RedirectStandardOutput = true,
                            UseShellExecute = false
                        });
                        runProc!.WaitForExit();
                        Assert.Equal(0, runProc.ExitCode);
                        Assert.Equal(expectedSha, CiaFormatInspector.ComputeStreamingFileSha256Hex(cOut));
                    }
                }
            }
            finally
            {
                TryCleanup(tempDir);
            }
        }

        private static string CreateTempTestDir(string prefix)
        {
            string dir = Path.Combine(Path.GetTempPath(), $"iego_test_{prefix}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryCleanup(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch { }
        }
    }
}
