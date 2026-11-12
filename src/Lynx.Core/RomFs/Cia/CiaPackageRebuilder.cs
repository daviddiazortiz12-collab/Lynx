using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Lynx.Core.RomFs.Cia
{
    /// <summary>
    /// Streaming, source-immutable, multi-content <c>.cia</c> rebuilder engineered for real Nintendo 2DS / 3DS
    /// hardware (<c>FBI</c> + <c>Luma3DS</c> + <c>Process9</c>) and emulators (<c>Citra</c> / <c>Azahar</c> / <c>Lime3DS</c>).
    /// Preserves 100% of original RomFS files (with exact UTF-16LE retail casing for kernel <c>CalcPathHash</c>),
    /// secondary contents (Manual / Download Play), Meta SMDH, and <c>SaveDataSize</c> while cryptographically
    /// verifying authentic decryption before rebuild and cold integrity prior to atomic commit.
    /// </summary>
    public static class CiaPackageRebuilder
    {
        public static (CiaRebuildTelemetry Telemetry, CiaVerificationResult Verification) RebuildFromRomFsDirectoryOverlay(
            string sourceCiaPath,
            string modifiedRomFsDirectory,
            string outputCiaPath,
            CtrKeyStore keyStore = null,
            Action<int, string> onProgress = null,
            string precomputedSourceSha256 = null,
            CiaBuildOptions buildOptions = null)
        {
            if (string.IsNullOrWhiteSpace(modifiedRomFsDirectory))
                throw new ArgumentException("Ruta de carpeta RomFS modificada vacía.", nameof(modifiedRomFsDirectory));

            string fullDir = Path.GetFullPath(modifiedRomFsDirectory);
            if (!Directory.Exists(fullDir))
                throw new DirectoryNotFoundException($"No existe la carpeta RomFS modificada: {fullDir}");

            // Support passing either the root containing ie_a.fa OR a parent folder containing a 'romfs/' subfolder
            if (!Directory.EnumerateFiles(fullDir, "*", SearchOption.TopDirectoryOnly)
                .Any(f => Path.GetFileName(f).Equals("ie_a.fa", StringComparison.OrdinalIgnoreCase)))
            {
                string subRomfs = Path.Combine(fullDir, "romfs");
                string subRomFsCaps = Path.Combine(fullDir, "RomFS");
                if (Directory.Exists(subRomfs))
                    fullDir = subRomfs;
                else if (Directory.Exists(subRomFsCaps))
                    fullDir = subRomFsCaps;
            }

            var overlayFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var extraDirs = new List<string>();

            foreach (string dirPath in Directory.EnumerateDirectories(fullDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(fullDir, dirPath);
                extraDirs.Add(StreamingIvfcRomFsBuilder.NormalizeRelPath(rel));
            }

            foreach (string filePath in Directory.EnumerateFiles(fullDir, "*", SearchOption.AllDirectories))
            {
                string rel = StreamingIvfcRomFsBuilder.NormalizeRelPath(Path.GetRelativePath(fullDir, filePath));
                if (string.IsNullOrEmpty(rel))
                    continue;
                overlayFiles[rel] = filePath;
            }

            if (overlayFiles.Count == 0)
                throw new InvalidOperationException($"La carpeta RomFS indicada está vacía: {fullDir}");

            return RebuildWithFileOverlays(
                sourceCiaPath,
                overlayFiles,
                extraDirs,
                outputCiaPath,
                keyStore,
                onProgress,
                precomputedSourceSha256,
                buildOptions);
        }

        public static (CiaRebuildTelemetry Telemetry, CiaVerificationResult Verification) RebuildWithFileOverlays(
            string sourceCiaPath,
            IReadOnlyDictionary<string, string> overlayFilesByRelPath,
            IEnumerable<string> extraRelativeDirectories,
            string outputCiaPath,
            CtrKeyStore keyStore = null,
            Action<int, string> onProgress = null,
            string precomputedSourceSha256 = null,
            CiaBuildOptions buildOptions = null)
        {
            if (string.IsNullOrWhiteSpace(sourceCiaPath))
                throw new ArgumentException("Ruta del .cia original vacía.", nameof(sourceCiaPath));
            if (string.IsNullOrWhiteSpace(outputCiaPath))
                throw new ArgumentException("Ruta del .cia de salida vacía.", nameof(outputCiaPath));

            string fullSource = Path.GetFullPath(sourceCiaPath);
            string fullOutput = Path.GetFullPath(outputCiaPath);

            if (!File.Exists(fullSource))
                throw new FileNotFoundException($"No existe el archivo .cia original: {fullSource}", fullSource);

            if (PathsReferToSameFile(fullSource, fullOutput))
            {
                throw new InvalidOperationException(
                    "Protección de archivo fuente activada: la ruta del .cia de salida no puede coincidir con la del .cia original.");
            }

            keyStore ??= CtrKeyStore.LoadAvailableLocalKeys();

            string sourcePreSha256;
            if (!string.IsNullOrWhiteSpace(precomputedSourceSha256))
            {
                sourcePreSha256 = precomputedSourceSha256;
            }
            else
            {
                onProgress?.Invoke(2, "Calculando huella SHA-256 de protección del .cia original (solo lectura)...");
                sourcePreSha256 = CiaFormatInspector.ComputeStreamingFileSha256Hex(fullSource);
            }

            onProgress?.Invoke(6, "Inspeccionando estructura, cabeceras TMD/Ticket y particiones del .cia original...");
            var inspection = CiaFormatInspector.InspectFile(fullSource, keyStore, computeFullFileSha256: false);
            if (!inspection.IsValidCiaStructure || !inspection.CanProcessNow)
            {
                string details = inspection.LimitationsAndWarnings.Count > 0
                    ? string.Join(" ", inspection.LimitationsAndWarnings)
                    : "El archivo .cia no puede procesarse en su estado actual.";
                throw new InvalidOperationException(details);
            }

            string outDir = Path.GetDirectoryName(fullOutput);
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            // Pre-flight disk space check on output volume so multi-GB writes never fail half-way with opaque IOException
            long estimatedOutBytes = buildOptions?.DeduplicateOtherLanguageStreamsToSpanish == true
                ? Math.Min(inspection.FileSizeBytes, 2_000_000_000L)
                : inspection.FileSizeBytes;
            EnsureSufficientDiskSpaceIfKnown(outDir ?? fullOutput, estimatedOutBytes + (64L * 1024 * 1024), "guardar el nuevo archivo .cia");

            string tempPartPath = $"{fullOutput}.part.{Guid.NewGuid():N}";
            var expectedOverlaySizes = new Dictionary<string, long>(StringComparer.Ordinal);

            try
            {
                using (var srcFs = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read, 262144))
                {
                    byte[] origCiaHdr = CiaFormatInspector.ReadBytesAt(srcFs, 0, (int)CiaConstants.CiaArchiveHeaderSize);
                    byte[] origCert = CiaFormatInspector.ReadBytesAt(srcFs, inspection.CertChainOffset, (int)inspection.CertChainSize);
                    byte[] origTik = CiaFormatInspector.ReadBytesAt(srcFs, inspection.TicketOffset, (int)inspection.TicketSize);
                    byte[] origTmd = CiaFormatInspector.ReadBytesAt(srcFs, inspection.TmdOffset, (int)inspection.TmdSize);
                    byte[] origMeta = inspection.MetaSize > 0
                        ? CiaFormatInspector.ReadBytesAt(srcFs, inspection.MetaOffset, (int)inspection.MetaSize)
                        : Array.Empty<byte>();

                    // Prepare Ticket copy with ConsoleID cleared to 0 (universal ticket for FBI on 2DS/3DS & emulators)
                    byte[] newTik = (byte[])origTik.Clone();
                    if (CiaFormatInspector.TryParseTicket(newTik, out int tikBodyOff, out _, out _, out _, out _))
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(newTik.AsSpan(tikBodyOff + 0x98, 4), 0u);
                    }

                    byte[] newTmd = (byte[])origTmd.Clone();
                    if (!CiaFormatInspector.TryParseTmd(
                            newTmd,
                            origCiaHdr.AsSpan(0x20, 0x2000),
                            inspection.ContentOffset,
                            out int tmdBodyOff,
                            out _,
                            out var parsedChunks))
                    {
                        throw new InvalidDataException("No se pudo reconstruir el TMD del .cia.");
                    }

                    // Resolve effective TMD TitleVersion (default bumps V.0.0.0 -> V.1.1.2 [1042] or source + 1
                    // so FBI updates content/ in-place on Nintendo 2DS/3DS without deleting data/00000001.sav).
                    ushort effectiveTitleVersionRaw = CiaConstants.ResolveEffectiveTitleVersion(
                        inspection.TmdTitleVersionRaw,
                        buildOptions);
                    string effectiveTitleVersionFormatted = CiaConstants.FormatTitleVersion(effectiveTitleVersionRaw);
                    BinaryPrimitives.WriteUInt16BigEndian(newTmd.AsSpan(tmdBodyOff + 0x9C, 2), effectiveTitleVersionRaw);

                    long certOff = inspection.CertChainOffset;
                    long tikOff = inspection.TicketOffset;
                    long tmdOff = inspection.TmdOffset;
                    long contentStartOff = inspection.ContentOffset;

                    int sourceRomFsFileCount = 0;
                    int outputRomFsFileCount = 0;
                    var overlaidPaths = new List<string>();
                    byte[] newContentIndexMask = new byte[0x2000];
                    long currentOutOffset = contentStartOff;

                    using (var outFs = new FileStream(tempPartPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 262144))
                    {
                        // Reserve prefix up to contentStartOff; write final header/cert/tik/tmd after content hashes are known
                        outFs.SetLength(contentStartOff);

                        int chunksTableStart = tmdBodyOff + 0x9C4;
                        for (int ci = 0; ci < parsedChunks.Count; ci++)
                        {
                            var chunk = parsedChunks[ci];
                            int chunkRecPos = chunksTableStart + ci * CiaConstants.TmdContentChunkRecordSize;

                            if (!chunk.IsPresentInCia)
                            {
                                ushort absentFlags = (ushort)(chunk.Flags & ~CiaConstants.TmdContentFlagEncrypted);
                                BinaryPrimitives.WriteUInt16BigEndian(newTmd.AsSpan(chunkRecPos + 0x06, 2), absentFlags);
                                continue;
                            }

                            CiaFormatInspector.SetContentBit(newContentIndexMask, chunk.ContentIndex);

                            if (chunk.ContentIndex == 0)
                            {
                                // Rebuild Main CXI (Content 0) with full RomFS preservation + case-preserving overlays
                                onProgress?.Invoke(14, "Descifrando y verificando cabeceras de la partición principal CXI (#0)...");
                                using Stream decMainNcch = CiaFormatInspector.OpenDecryptedContentStream(
                                    srcFs, origTik, chunk, keyStore, out bool isMainNcch);
                                if (!isMainNcch)
                                    throw new InvalidDataException("El contenido principal #0 no es una partición NCCH válida.");

                                byte[] mainHdr = CiaFormatInspector.ReadBytesAt(decMainNcch, 0, 0x200);

                                // Cryptographically verify authentic decryption of Content #0 BEFORE rebuilding!
                                VerifyAuthenticDecryptedNcchOrThrow(decMainNcch, mainHdr, chunk.ContentIndex, verifyRomFsSuperblock: true);

                                long origRomFsOff = BinaryPrimitives.ReadUInt32LittleEndian(mainHdr.AsSpan(0x1B0, 4)) * CiaConstants.NcchMediaUnitSize;
                                long origRomFsLen = BinaryPrimitives.ReadUInt32LittleEndian(mainHdr.AsSpan(0x1B4, 4)) * CiaConstants.NcchMediaUnitSize;

                                using var origRomFsStream = new SubReadStream(decMainNcch, origRomFsOff, origRomFsLen, leaveOpen: true);
                                var origInventory = IvfcRomFsReader.ReadInventory(origRomFsStream);
                                sourceRomFsFileCount = origInventory.Files.Count;

                                // Build canonical retail casing maps so Windows case-variations (e.g. IE_A.FA) NEVER break
                                // 3DS Process9's case-sensitive UTF-16LE CalcPathHash lookup on a real Nintendo 2DS!
                                var canonicalFileByIgnoreCase = new Dictionary<string, string>(origInventory.Files.Count, StringComparer.OrdinalIgnoreCase);
                                var canonicalDirByIgnoreCase = new Dictionary<string, string>(origInventory.Directories.Count, StringComparer.OrdinalIgnoreCase);

                                foreach (var d in origInventory.Directories)
                                {
                                    if (!string.IsNullOrEmpty(d.RelativePath))
                                        canonicalDirByIgnoreCase[d.RelativePath] = d.RelativePath;
                                }

                                var mergedSpecs = new Dictionary<string, RomFsBuildFileSpec>(StringComparer.OrdinalIgnoreCase);
                                foreach (var origFile in origInventory.Files)
                                {
                                    var captured = origFile;
                                    canonicalFileByIgnoreCase[origFile.RelativePath] = origFile.RelativePath;
                                    mergedSpecs[origFile.RelativePath] = new RomFsBuildFileSpec
                                    {
                                        RelativePath = origFile.RelativePath,
                                        Length = origFile.DataLength,
                                        WriteContentTo = target => CopySlice(origRomFsStream, captured.DataOffsetInRomFs, captured.DataLength, target)
                                    };
                                }

                                // Apply ALL overlay files (preserving canonical retail casing for existing paths!)
                                if (overlayFilesByRelPath != null)
                                {
                                    foreach (var kvp in overlayFilesByRelPath)
                                    {
                                        string normRel = StreamingIvfcRomFsBuilder.NormalizeRelPath(kvp.Key);
                                        if (string.IsNullOrEmpty(normRel))
                                            continue;

                                        string canonicalRel = CanonicalizeOverlayPath(normRel, canonicalFileByIgnoreCase, canonicalDirByIgnoreCase);
                                        string diskPath = Path.GetFullPath(kvp.Value);
                                        var diskInfo = new FileInfo(diskPath);
                                        if (!diskInfo.Exists)
                                            throw new FileNotFoundException($"Archivo de overlay no encontrado: {diskPath}", diskPath);

                                        expectedOverlaySizes[canonicalRel] = diskInfo.Length;
                                        overlaidPaths.Add(canonicalRel);

                                        mergedSpecs[canonicalRel] = new RomFsBuildFileSpec
                                        {
                                            RelativePath = canonicalRel,
                                            Length = diskInfo.Length,
                                            WriteContentTo = target => CopyDiskFile(diskPath, target)
                                        };
                                    }
                                }

                                if (buildOptions?.DeduplicateOtherLanguageStreamsToSpanish == true)
                                {
                                    ApplySpanishMediaDeduplicationAliases(mergedSpecs, expectedOverlaySizes);
                                }

                                var allDirs = origInventory.Directories
                                    .Select(d => d.RelativePath)
                                    .Concat((extraRelativeDirectories ?? Enumerable.Empty<string>())
                                        .Select(d => CanonicalizeDirectoryPath(StreamingIvfcRomFsBuilder.NormalizeRelPath(d), canonicalDirByIgnoreCase)))
                                    .Where(d => !string.IsNullOrEmpty(d))
                                    .ToList();

                                long c0Start = currentOutOffset;

                                // Copy NCCH prefix [0 .. origRomFsOff) (Header + ExHeader + PlainRegion + Logo + ExeFS)
                                outFs.Seek(c0Start, SeekOrigin.Begin);
                                CopySlice(decMainNcch, 0, origRomFsOff, outFs);

                                // Ensure SDApplication bit (0x02 at ExHeader + 0x0D) is set so real 2DS/3DS mounts SD save data cleanly,
                                // and normalize unused ExeFS section tail padding to clean 0x00 bytes
                                EnsureExHeaderSdApplicationFlag(outFs, c0Start, mainHdr);
                                NormalizePlainExeFsSectionPadding(outFs, c0Start, mainHdr);

                                // Stream-build IVFC Level 0-3 directly at c0Start + origRomFsOff
                                var ivfcProgress = new Progress<int>(pct =>
                                {
                                    int mapped = 20 + (pct * 52) / 100;
                                    onProgress?.Invoke(mapped, $"Empaquetando RomFS completo e IVFC SHA-256 ({pct}%)...");
                                });

                                var ivfcRes = StreamingIvfcRomFsBuilder.BuildToStream(
                                    outFs,
                                    c0Start + origRomFsOff,
                                    allDirs,
                                    mergedSpecs.Values,
                                    ivfcProgress);

                                outputRomFsFileCount = ivfcRes.FileCount;
                                long newNcch0Size = origRomFsOff + ivfcRes.TotalIvfcSizeBytes;
                                if (newNcch0Size % CiaConstants.NcchMediaUnitSize != 0)
                                {
                                    long alignedNcch = CiaConstants.AlignUp(newNcch0Size, CiaConstants.NcchMediaUnitSize);
                                    WriteZerosAt(outFs, c0Start + newNcch0Size, alignedNcch - newNcch0Size);
                                    newNcch0Size = alignedNcch;
                                }

                                if (newNcch0Size > CiaConstants.MaxFat32ContentSizeBytes)
                                {
                                    throw new InvalidOperationException(
                                        $"El contenido principal reconstruido ({newNcch0Size:N0} B) excede el límite real de 4 GiB - 1 de FAT32 en Nintendo 2DS / 3DS.");
                                }

                                // Update NCCH #0 header fields (ExHeader & ExeFS hashes were already verified; RomFS superblock hash updated to ivfcRes)
                                BinaryPrimitives.WriteUInt32LittleEndian(mainHdr.AsSpan(0x104, 4), (uint)(newNcch0Size / CiaConstants.NcchMediaUnitSize));
                                BinaryPrimitives.WriteUInt32LittleEndian(mainHdr.AsSpan(0x1B0, 4), (uint)(origRomFsOff / CiaConstants.NcchMediaUnitSize));
                                BinaryPrimitives.WriteUInt32LittleEndian(mainHdr.AsSpan(0x1B4, 4), (uint)(ivfcRes.TotalIvfcSizeBytes / CiaConstants.NcchMediaUnitSize));
                                BinaryPrimitives.WriteUInt32LittleEndian(mainHdr.AsSpan(0x1B8, 4), ivfcRes.HashRegionSizeMediaUnits);
                                Buffer.BlockCopy(ivfcRes.SuperblockSha256, 0, mainHdr, 0x1E0, 0x20);
                                mainHdr[0x18B] = 0x00;
                                mainHdr[0x18F] = (byte)((mainHdr[0x18F] & ~0x21) | 0x04);

                                outFs.Seek(c0Start, SeekOrigin.Begin);
                                outFs.Write(mainHdr, 0, 0x200);

                                onProgress?.Invoke(75, "Calculando SHA-256 del ejecutable principal CXI (#0) para TMD...");
                                byte[] c0Sha = ComputeStreamSliceSha256(outFs, c0Start, newNcch0Size);

                                ushort newFlags = (ushort)(chunk.Flags & ~CiaConstants.TmdContentFlagEncrypted);
                                BinaryPrimitives.WriteUInt16BigEndian(newTmd.AsSpan(chunkRecPos + 0x06, 2), newFlags);
                                BinaryPrimitives.WriteUInt64BigEndian(newTmd.AsSpan(chunkRecPos + 0x08, 8), (ulong)newNcch0Size);
                                Buffer.BlockCopy(c0Sha, 0, newTmd, chunkRecPos + 0x10, 0x20);

                                currentOutOffset = c0Start + newNcch0Size;
                            }
                            else
                            {
                                // Preserve & cleanly decrypt secondary contents (Manual #1, Download Play #2, etc.)
                                onProgress?.Invoke(80, $"Preservando y verificando contenido secundario #{chunk.ContentIndex} ({chunk.RoleDescription})...");
                                using Stream decSec = CiaFormatInspector.OpenDecryptedContentStream(
                                    srcFs, origTik, chunk, keyStore, out bool isSecNcch);

                                long copyLen = decSec.Length;
                                byte[] secHdr = null;
                                if (isSecNcch && copyLen >= 0x200)
                                {
                                    secHdr = CiaFormatInspector.ReadBytesAt(decSec, 0, 0x200);
                                    uint declaredUnits = BinaryPrimitives.ReadUInt32LittleEndian(secHdr.AsSpan(0x104, 4));
                                    long declaredBytes = declaredUnits * CiaConstants.NcchMediaUnitSize;
                                    if (declaredBytes >= 0x200 && declaredBytes <= decSec.Length)
                                    {
                                        copyLen = declaredBytes;
                                    }

                                    // Verify secondary CFA decryption against Nintendo's own NCCH hashes!
                                    VerifyAuthenticDecryptedNcchOrThrow(decSec, secHdr, chunk.ContentIndex, verifyRomFsSuperblock: true);
                                }

                                long secStart = currentOutOffset;
                                outFs.Seek(secStart, SeekOrigin.Begin);
                                CopySlice(decSec, 0, copyLen, outFs);

                                long secSize = copyLen;
                                if (secSize % CiaConstants.CiaContentAlignment != 0)
                                {
                                    long alignedSec = CiaConstants.AlignUp(secSize, CiaConstants.CiaContentAlignment);
                                    WriteZerosAt(outFs, secStart + secSize, alignedSec - secSize);
                                    secSize = alignedSec;
                                }

                                if (isSecNcch && secHdr != null)
                                {
                                    NormalizePlainExeFsSectionPadding(outFs, secStart, secHdr);
                                    secHdr[0x18B] = 0x00;
                                    secHdr[0x18F] = (byte)((secHdr[0x18F] & ~0x21) | 0x04);
                                    outFs.Seek(secStart, SeekOrigin.Begin);
                                    outFs.Write(secHdr, 0, 0x200);
                                }

                                byte[] secSha = ComputeStreamSliceSha256(outFs, secStart, secSize);
                                ushort newFlags = (ushort)(chunk.Flags & ~CiaConstants.TmdContentFlagEncrypted);
                                BinaryPrimitives.WriteUInt16BigEndian(newTmd.AsSpan(chunkRecPos + 0x06, 2), newFlags);
                                BinaryPrimitives.WriteUInt64BigEndian(newTmd.AsSpan(chunkRecPos + 0x08, 8), (ulong)secSize);
                                Buffer.BlockCopy(secSha, 0, newTmd, chunkRecPos + 0x10, 0x20);

                                currentOutOffset = secStart + secSize;
                            }
                        }

                        long totalNewContentSize = currentOutOffset - contentStartOff;

                        // Cascade TMD ContentChunkRecords -> ContentInfo[0..63] -> TMD.InfoRecordHash (GodMode9 FixTmdHashes compatible)
                        UpdateTmdContentInfoHashes(newTmd, tmdBodyOff, chunksTableStart, (ushort)parsedChunks.Count);

                        // Build final 0x2020 CIA Header
                        byte[] newCiaHdr = (byte[])origCiaHdr.Clone();
                        BinaryPrimitives.WriteUInt64LittleEndian(newCiaHdr.AsSpan(0x18, 8), (ulong)totalNewContentSize);
                        Buffer.BlockCopy(newContentIndexMask, 0, newCiaHdr, 0x20, 0x2000);

                        // Write Header, CertChain, Ticket, TMD at their 0x40-aligned offsets
                        outFs.Seek(0, SeekOrigin.Begin);
                        outFs.Write(newCiaHdr, 0, newCiaHdr.Length);

                        outFs.Seek(certOff, SeekOrigin.Begin);
                        outFs.Write(origCert, 0, origCert.Length);

                        outFs.Seek(tikOff, SeekOrigin.Begin);
                        outFs.Write(newTik, 0, newTik.Length);

                        outFs.Seek(tmdOff, SeekOrigin.Begin);
                        outFs.Write(newTmd, 0, newTmd.Length);

                        // Write optional Meta / SMDH at AlignUp(contentStartOff + totalNewContentSize, 0x40)
                        if (origMeta.Length > 0)
                        {
                            long newMetaOff = CiaConstants.AlignUp(contentStartOff + totalNewContentSize, CiaConstants.CiaSectionAlignment);
                            if (newMetaOff > currentOutOffset)
                                WriteZerosAt(outFs, currentOutOffset, newMetaOff - currentOutOffset);

                            outFs.Seek(newMetaOff, SeekOrigin.Begin);
                            outFs.Write(origMeta, 0, origMeta.Length);
                        }

                        outFs.Flush(flushToDisk: true);
                    }

                    // Cold-start verification of the .part file BEFORE atomic rename
                    onProgress?.Invoke(88, "Ejecutando verificación criptográfica en frío para 2DS / 3DS (TMD, NCCH, .code, IVFC 4 niveles y RomFS)...");
                    var verification = CiaPackageVerifier.VerifyColdCiaFile(
                        tempPartPath,
                        expectedTitleIdHex: inspection.TmdTitleIdHex,
                        expectedSaveDataSize: inspection.SaveDataSizeBytes,
                        minimumExpectedRomFsFileCount: inspection.RomFsFileCount,
                        expectedOverlayFileSizes: expectedOverlaySizes,
                        expectedTitleVersionRaw: effectiveTitleVersionRaw);

                    if (!verification.IsValid)
                    {
                        throw new InvalidDataException(
                            "El nuevo .cia no superó la verificación en frío y fue descartado: " +
                            string.Join(" | ", verification.Errors));
                    }

                    // Verify source immutability (SHA-256 before == SHA-256 after)
                    onProgress?.Invoke(95, "Comprobando inmutabilidad SHA-256 del .cia original y confirmando escritura atómica...");
                    string sourcePostSha256 = CiaFormatInspector.ComputeStreamingFileSha256Hex(fullSource);
                    if (!string.Equals(sourcePreSha256, sourcePostSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Alerta crítica: el hash SHA-256 del archivo fuente cambió durante la operación.");
                    }

                    // Atomic rename .part -> final .cia
                    File.Move(tempPartPath, fullOutput, overwrite: true);

                    string outputSha256 = CiaFormatInspector.ComputeStreamingFileSha256Hex(fullOutput);
                    long outLen = new FileInfo(fullOutput).Length;
                    overlaidPaths.Sort(StringComparer.Ordinal);

                    var telemetry = new CiaRebuildTelemetry
                    {
                        SourceCiaPath = fullSource,
                        OutputCiaPath = fullOutput,
                        SourceCiaSizeBytes = inspection.FileSizeBytes,
                        OutputCiaSizeBytes = outLen,
                        SourceCiaSha256Hex = sourcePostSha256,
                        OutputCiaSha256Hex = outputSha256,
                        TitleIdHex = inspection.TmdTitleIdHex,
                        SourceTitleVersionRaw = inspection.TmdTitleVersionRaw,
                        SourceTitleVersionFormatted = inspection.TmdTitleVersionFormatted,
                        TitleVersionRaw = effectiveTitleVersionRaw,
                        TitleVersionFormatted = effectiveTitleVersionFormatted,
                        IsFbiInPlaceUpdateReady = effectiveTitleVersionRaw > inspection.TmdTitleVersionRaw,
                        PreservedSaveDataSizeBytes = inspection.SaveDataSizeBytes,
                        PreservedContentsCount = verification.VerifiedContentChunksCount,
                        SourceRomFsFileCount = sourceRomFsFileCount,
                        OutputRomFsFileCount = outputRomFsFileCount,
                        OverlaidModifiedRomFsFilesCount = overlaidPaths.Count,
                        OverlaidRelativePaths = overlaidPaths
                    };

                    onProgress?.Invoke(100, "Nuevo archivo .cia generado y verificado con éxito.");
                    return (telemetry, verification);
                }
            }
            catch
            {
                TryDeleteTempFile(tempPartPath);
                throw;
            }
        }

        internal static string CanonicalizeOverlayPath(
            string normalizedRelPath,
            IReadOnlyDictionary<string, string> canonicalFileByIgnoreCase,
            IReadOnlyDictionary<string, string> canonicalDirByIgnoreCase)
        {
            if (canonicalFileByIgnoreCase.TryGetValue(normalizedRelPath, out string exactFile))
                return exactFile;

            int lastSlash = normalizedRelPath.LastIndexOf('/');
            if (lastSlash < 0)
                return normalizedRelPath;

            string dirPart = normalizedRelPath.Substring(0, lastSlash);
            string fileName = normalizedRelPath.Substring(lastSlash + 1);
            string canonicalDir = CanonicalizeDirectoryPath(dirPart, canonicalDirByIgnoreCase);
            return canonicalDir + "/" + fileName;
        }

        internal static string CanonicalizeDirectoryPath(
            string normalizedDir,
            IReadOnlyDictionary<string, string> canonicalDirByIgnoreCase)
        {
            if (string.IsNullOrEmpty(normalizedDir))
                return string.Empty;

            if (canonicalDirByIgnoreCase.TryGetValue(normalizedDir, out string exactDir))
                return exactDir;

            string[] parts = normalizedDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string built = string.Empty;
            for (int i = 0; i < parts.Length; i++)
            {
                string candidate = i == 0 ? parts[0] : built + "/" + parts[i];
                if (canonicalDirByIgnoreCase.TryGetValue(candidate, out string matchedPrefix))
                {
                    built = matchedPrefix;
                }
                else
                {
                    built = candidate;
                }
            }
            return built;
        }

        private static void VerifyAuthenticDecryptedNcchOrThrow(
            Stream plainNcch,
            ReadOnlySpan<byte> ncchHdr,
            ushort contentIndex,
            bool verifyRomFsSuperblock)
        {
            Span<byte> actual = stackalloc byte[32];

            uint exHdrDataSize = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x180, 4));
            if (exHdrDataSize >= 0x400)
            {
                byte[] exHdr400 = CiaFormatInspector.ReadBytesAt(plainNcch, 0x200, 0x400);
                SHA256.HashData(exHdr400, actual);
                if (!actual.SequenceEqual(ncchHdr.Slice(0x160, 0x20)))
                {
                    throw new InvalidDataException(
                        $"El ExHeader descifrado del contenido #{contentIndex} no coincide con su SHA-256 original (clave incorrecta o archivo corrupto).");
                }
            }

            long exefsOff = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x1A0, 4)) * CiaConstants.NcchMediaUnitSize;
            long exefsSize = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x1A4, 4)) * CiaConstants.NcchMediaUnitSize;
            long exefsHashLen = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x1A8, 4)) * CiaConstants.NcchMediaUnitSize;
            if (exefsOff > 0 && exefsHashLen > 0)
            {
                byte[] exefsTop = CiaFormatInspector.ReadBytesAt(plainNcch, exefsOff, (int)exefsHashLen);
                SHA256.HashData(exefsTop, actual);
                if (!actual.SequenceEqual(ncchHdr.Slice(0x1C0, 0x20)))
                {
                    throw new InvalidDataException(
                        $"La cabecera ExeFS descifrada del contenido #{contentIndex} no coincide con su SHA-256 original.");
                }

                if (exefsSize >= 0x200 && exefsTop.Length >= 0x200)
                {
                    VerifyExeFsSectionsOrThrow(plainNcch, exefsOff, exefsSize, exefsTop, contentIndex);
                }
            }

            if (verifyRomFsSuperblock)
            {
                long romfsOff = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x1B0, 4)) * CiaConstants.NcchMediaUnitSize;
                long romfsHashLen = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x1B8, 4)) * CiaConstants.NcchMediaUnitSize;
                if (romfsOff > 0 && romfsHashLen > 0)
                {
                    byte[] romfsTop = CiaFormatInspector.ReadBytesAt(plainNcch, romfsOff, (int)romfsHashLen);
                    SHA256.HashData(romfsTop, actual);
                    if (!actual.SequenceEqual(ncchHdr.Slice(0x1E0, 0x20)))
                    {
                        throw new InvalidDataException(
                            $"El superbloque RomFS descifrado del contenido #{contentIndex} no coincide con su SHA-256 original.");
                    }
                }
            }
        }

        internal static void VerifyExeFsSectionsOrThrow(
            Stream ncchContainerStream,
            long exefsAbsoluteOffset,
            long exefsTotalSizeBytes,
            ReadOnlySpan<byte> exefsHeader512,
            ushort contentIndex)
        {
            Span<byte> actual = stackalloc byte[32];
            for (int i = 0; i < 10; i++)
            {
                int entryPos = i * 0x10;
                string secName = Encoding.ASCII.GetString(exefsHeader512.Slice(entryPos, 8)).TrimEnd('\0');
                uint secRelOff = BinaryPrimitives.ReadUInt32LittleEndian(exefsHeader512.Slice(entryPos + 8, 4));
                uint secSize = BinaryPrimitives.ReadUInt32LittleEndian(exefsHeader512.Slice(entryPos + 12, 4));
                if (secSize == 0 || string.IsNullOrEmpty(secName))
                    continue;

                if (0x200L + secRelOff + secSize > exefsTotalSizeBytes)
                {
                    throw new InvalidDataException(
                        $"La sección ExeFS '{secName}' en contenido #{contentIndex} excede los límites de ExeFS.");
                }

                ReadOnlySpan<byte> expectedHash = exefsHeader512.Slice(0x200 - (i + 1) * 0x20, 0x20);
                byte[] hashBytes = ComputeStreamSliceSha256(ncchContainerStream, exefsAbsoluteOffset + 0x200 + secRelOff, secSize);
                hashBytes.AsSpan().CopyTo(actual);

                if (!actual.SequenceEqual(expectedHash))
                {
                    // Fallback check for non-standard synthetic tools that hashed AlignUp(secSize, 0x200) when padding is zero
                    long alignedSize = CiaConstants.AlignUp(secSize, CiaConstants.NcchMediaUnitSize);
                    bool matchedAligned = false;
                    if (alignedSize > secSize && 0x200L + secRelOff + alignedSize <= exefsTotalSizeBytes)
                    {
                        byte[] alignedHash = ComputeStreamSliceSha256(ncchContainerStream, exefsAbsoluteOffset + 0x200 + secRelOff, alignedSize);
                        matchedAligned = alignedHash.AsSpan().SequenceEqual(expectedHash);
                    }

                    if (!matchedAligned)
                    {
                        throw new InvalidDataException(
                            $"El ejecutable/sección ExeFS '{secName}' en contenido #{contentIndex} no coincide con su hash SHA-256 (corrupción detectada).");
                    }
                }
            }
        }

        private static void EnsureExHeaderSdApplicationFlag(Stream outFs, long ncchStart, byte[] ncchHdr512)
        {
            uint exHdrDataSize = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr512.AsSpan(0x180, 4));
            if (exHdrDataSize < 0x400)
                return;

            byte[] exHdr400 = CiaFormatInspector.ReadBytesAt(outFs, ncchStart + 0x200, 0x400);
            if ((exHdr400[0x0D] & 0x02) == 0)
            {
                // Set SDApplication flag (bit 1 of SCI.Flags, 3dsconv / GodMode9 SetNcchSdFlag) so 2DS mounts SD save data
                exHdr400[0x0D] |= 0x02;
                outFs.Seek(ncchStart + 0x200, SeekOrigin.Begin);
                outFs.Write(exHdr400, 0, 0x400);
                SHA256.HashData(exHdr400, ncchHdr512.AsSpan(0x160, 0x20));
            }
        }

        private static void NormalizePlainExeFsSectionPadding(Stream outFs, long ncchStart, ReadOnlySpan<byte> ncchHdr512)
        {
            long exefsOff = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr512.Slice(0x1A0, 4)) * CiaConstants.NcchMediaUnitSize;
            long exefsSize = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr512.Slice(0x1A4, 4)) * CiaConstants.NcchMediaUnitSize;
            if (exefsOff <= 0 || exefsSize < 0x200)
                return;

            byte[] exefsHdr = CiaFormatInspector.ReadBytesAt(outFs, ncchStart + exefsOff, 0x200);
            Span<byte> actual = stackalloc byte[32];

            for (int i = 0; i < 10; i++)
            {
                int entryPos = i * 0x10;
                string secName = Encoding.ASCII.GetString(exefsHdr, entryPos, 8).TrimEnd('\0');
                uint secRelOff = BinaryPrimitives.ReadUInt32LittleEndian(exefsHdr.AsSpan(entryPos + 8, 4));
                uint secSize = BinaryPrimitives.ReadUInt32LittleEndian(exefsHdr.AsSpan(entryPos + 12, 4));
                if (secSize == 0 || string.IsNullOrEmpty(secName))
                    continue;

                long aligned = CiaConstants.AlignUp(secSize, CiaConstants.NcchMediaUnitSize);
                if (aligned > secSize && 0x200L + secRelOff + aligned <= exefsSize)
                {
                    // Only zero tail padding when section hash covers exact secSize (standard Nintendo SDK / makerom)
                    byte[] exactHash = ComputeStreamSliceSha256(outFs, ncchStart + exefsOff + 0x200 + secRelOff, secSize);
                    exactHash.AsSpan().CopyTo(actual);
                    if (actual.SequenceEqual(exefsHdr.AsSpan(0x200 - (i + 1) * 0x20, 0x20)))
                    {
                        WriteZerosAt(outFs, ncchStart + exefsOff + 0x200 + secRelOff + secSize, aligned - secSize);
                    }
                }
            }
        }

        private static void UpdateTmdContentInfoHashes(
            byte[] newTmd,
            int tmdBodyOff,
            int chunksTableStart,
            ushort totalChunkRecords)
        {
            Span<byte> infoRecordsSpan = newTmd.AsSpan(tmdBodyOff + 0xC4, 0x900);

            // Check if existing ContentInfo[0..63] cmd_count sum equals totalChunkRecords; if so, preserve grouping (GodMode9 FixTmdHashes)
            int existingSum = 0;
            bool validGrouping = true;
            for (int g = 0; g < CiaConstants.TmdContentInfoRecordCount; g++)
            {
                ushort cmdCount = BinaryPrimitives.ReadUInt16BigEndian(infoRecordsSpan.Slice(g * 0x24 + 2, 2));
                if (cmdCount == 0)
                    continue;
                if (existingSum + cmdCount > totalChunkRecords)
                {
                    validGrouping = false;
                    break;
                }
                existingSum += cmdCount;
            }

            if (validGrouping && existingSum == totalChunkRecords && totalChunkRecords > 0)
            {
                int cursor = 0;
                for (int g = 0; g < CiaConstants.TmdContentInfoRecordCount && cursor < totalChunkRecords; g++)
                {
                    Span<byte> rec = infoRecordsSpan.Slice(g * 0x24, 0x24);
                    ushort cmdCount = BinaryPrimitives.ReadUInt16BigEndian(rec.Slice(2, 2));
                    if (cmdCount == 0)
                        continue;

                    SHA256.HashData(
                        newTmd.AsSpan(chunksTableStart + cursor * CiaConstants.TmdContentChunkRecordSize, cmdCount * CiaConstants.TmdContentChunkRecordSize),
                        rec.Slice(4, 0x20));
                    cursor += cmdCount;
                }
            }
            else
            {
                infoRecordsSpan.Clear();
                if (totalChunkRecords > 0)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(infoRecordsSpan.Slice(0, 2), 0);
                    BinaryPrimitives.WriteUInt16BigEndian(infoRecordsSpan.Slice(2, 2), totalChunkRecords);
                    SHA256.HashData(
                        newTmd.AsSpan(chunksTableStart, totalChunkRecords * CiaConstants.TmdContentChunkRecordSize),
                        infoRecordsSpan.Slice(4, 0x20));
                }
            }

            SHA256.HashData(infoRecordsSpan, newTmd.AsSpan(tmdBodyOff + 0xA4, 0x20));
        }

        internal static void EnsureSufficientDiskSpaceIfKnown(string targetPath, long requiredBytes, string actionDescription)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(targetPath));
                if (string.IsNullOrWhiteSpace(root))
                    return;

                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace > 0 && drive.AvailableFreeSpace < requiredBytes)
                {
                    double freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                    double reqGb = requiredBytes / (1024.0 * 1024.0 * 1024.0);
                    throw new IOException(
                        $"Espacio insuficiente en la unidad '{drive.Name}' para {actionDescription}: disponibles {freeGb:F2} GB, necesarios al menos {reqGb:F2} GB.");
                }
            }
            catch (IOException)
            {
                throw;
            }
            catch
            {
                // Ignore if virtual/UNC filesystem does not report DriveInfo
            }
        }

        private static bool PathsReferToSameFile(string pathA, string pathB)
        {
            StringComparison cmp = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(
                Path.GetFullPath(pathA).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(pathB).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                cmp);
        }

        private static void CopySlice(Stream source, long sourceOffset, long length, Stream destination)
        {
            if (source.Position != sourceOffset)
                source.Seek(sourceOffset, SeekOrigin.Begin);
            byte[] buf = new byte[65536];
            long rem = length;
            while (rem > 0)
            {
                int take = (int)Math.Min(buf.Length, rem);
                int r = source.Read(buf, 0, take);
                if (r <= 0)
                    throw new EndOfStreamException("Unexpected EOF while copying stream slice.");
                destination.Write(buf, 0, r);
                rem -= r;
            }
        }

        private static void CopyDiskFile(string diskPath, Stream destination)
        {
            using var fs = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.Read, 262144);
            byte[] buf = new byte[65536];
            int r;
            while ((r = fs.Read(buf, 0, buf.Length)) > 0)
            {
                destination.Write(buf, 0, r);
            }
        }

        private static void WriteZerosAt(Stream s, long offset, long length)
        {
            if (length <= 0)
                return;
            s.Seek(offset, SeekOrigin.Begin);
            byte[] zeros = new byte[4096];
            long rem = length;
            while (rem > 0)
            {
                int take = (int)Math.Min(zeros.Length, rem);
                s.Write(zeros, 0, take);
                rem -= take;
            }
        }

        internal static byte[] ComputeStreamSliceSha256(Stream s, long offset, long length)
        {
            s.Seek(offset, SeekOrigin.Begin);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buf = new byte[65536];
            long rem = length;
            while (rem > 0)
            {
                int take = (int)Math.Min(buf.Length, rem);
                int r = s.Read(buf, 0, take);
                if (r <= 0)
                    throw new EndOfStreamException("Unexpected EOF computing content SHA-256.");
                sha.AppendData(buf, 0, r);
                rem -= r;
            }
            return sha.GetHashAndReset();
        }

        private static readonly string[] NonSpanishMediaLangSuffixes = { "_de", "_en", "_fr", "_it" };

        private static void ApplySpanishMediaDeduplicationAliases(
            Dictionary<string, RomFsBuildFileSpec> mergedSpecs,
            IReadOnlyDictionary<string, long> expectedOverlaySizes)
        {
            var keys = mergedSpecs.Keys.ToList();
            foreach (string key in keys)
            {
                if (expectedOverlaySizes != null && expectedOverlaySizes.ContainsKey(key))
                    continue;

                if (!key.StartsWith("movie/", StringComparison.OrdinalIgnoreCase) &&
                    !key.StartsWith("snd/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string ext = Path.GetExtension(key);
                if (!ext.Equals(".bcstm", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".moflex", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string withoutExt = key.Substring(0, key.Length - ext.Length);
                string matchedSuffix = null;
                foreach (string suffix in NonSpanishMediaLangSuffixes)
                {
                    if (withoutExt.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedSuffix = suffix;
                        break;
                    }
                }

                if (matchedSuffix == null)
                    continue;

                string esCandidate = withoutExt.Substring(0, withoutExt.Length - matchedSuffix.Length) + "_es" + ext;
                if (!mergedSpecs.TryGetValue(esCandidate, out var esSpec) ||
                    !string.IsNullOrEmpty(esSpec.AliasOfRelativePath))
                {
                    continue;
                }

                var origSpec = mergedSpecs[key];
                mergedSpecs[key] = new RomFsBuildFileSpec
                {
                    RelativePath = origSpec.RelativePath,
                    Length = esSpec.Length,
                    AliasOfRelativePath = esSpec.RelativePath
                };
            }
        }

        private static void TryDeleteTempFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }
}
