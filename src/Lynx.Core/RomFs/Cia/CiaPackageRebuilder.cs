using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Lynx.Core.RomFs.Cia
{
    /// <summary>
    /// Streaming, source-immutable, multi-content <c>.cia</c> rebuilder.
    /// Preserves 100% of original RomFS files and secondary contents (Manual / Download Play / Meta SMDH / SaveDataSize)
    /// while incorporating all modified/added RomFS files and verifying cold integrity prior to atomic commit.
    /// </summary>
    public static class CiaPackageRebuilder
    {
        public static (CiaRebuildTelemetry Telemetry, CiaVerificationResult Verification) RebuildFromRomFsDirectoryOverlay(
            string sourceCiaPath,
            string modifiedRomFsDirectory,
            string outputCiaPath,
            CtrKeyStore keyStore = null,
            Action<int, string> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(modifiedRomFsDirectory))
                throw new ArgumentException("Ruta de carpeta RomFS modificada vacía.", nameof(modifiedRomFsDirectory));

            string fullDir = Path.GetFullPath(modifiedRomFsDirectory);
            if (!Directory.Exists(fullDir))
                throw new DirectoryNotFoundException($"No existe la carpeta RomFS modificada: {fullDir}");

            // Support passing either the root containing ie_a.fa OR a parent folder containing a 'romfs/' subfolder
            if (!File.Exists(Path.Combine(fullDir, "ie_a.fa")))
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
                onProgress);
        }

        public static (CiaRebuildTelemetry Telemetry, CiaVerificationResult Verification) RebuildWithFileOverlays(
            string sourceCiaPath,
            IReadOnlyDictionary<string, string> overlayFilesByRelPath,
            IEnumerable<string> extraRelativeDirectories,
            string outputCiaPath,
            CtrKeyStore keyStore = null,
            Action<int, string> onProgress = null)
        {
            if (string.IsNullOrWhiteSpace(sourceCiaPath))
                throw new ArgumentException("Ruta del .cia original vacía.", nameof(sourceCiaPath));
            if (string.IsNullOrWhiteSpace(outputCiaPath))
                throw new ArgumentException("Ruta del .cia de salida vacía.", nameof(outputCiaPath));

            string fullSource = Path.GetFullPath(sourceCiaPath);
            string fullOutput = Path.GetFullPath(outputCiaPath);

            if (!File.Exists(fullSource))
                throw new FileNotFoundException($"No existe el archivo .cia original: {fullSource}", fullSource);

            if (string.Equals(fullSource, fullOutput, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Protección de archivo fuente activada: la ruta del .cia de salida no puede coincidir con la del .cia original.");
            }

            keyStore ??= CtrKeyStore.LoadAvailableLocalKeys();

            onProgress?.Invoke(2, "Calculando huella SHA-256 de protección del .cia original (solo lectura)...");
            string sourcePreSha256 = CiaFormatInspector.ComputeStreamingFileSha256Hex(fullSource);

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

            string tempPartPath = $"{fullOutput}.part.{Guid.NewGuid():N}";
            var expectedOverlaySizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var srcFs = new FileStream(fullSource, FileMode.Open, FileAccess.Read, FileShare.Read, 131072))
                {
                    byte[] origCiaHdr = CiaFormatInspector.ReadBytesAt(srcFs, 0, (int)CiaConstants.CiaArchiveHeaderSize);
                    byte[] origCert = CiaFormatInspector.ReadBytesAt(srcFs, inspection.CertChainOffset, (int)inspection.CertChainSize);
                    byte[] origTik = CiaFormatInspector.ReadBytesAt(srcFs, inspection.TicketOffset, (int)inspection.TicketSize);
                    byte[] origTmd = CiaFormatInspector.ReadBytesAt(srcFs, inspection.TmdOffset, (int)inspection.TmdSize);
                    byte[] origMeta = inspection.MetaSize > 0
                        ? CiaFormatInspector.ReadBytesAt(srcFs, inspection.MetaOffset, (int)inspection.MetaSize)
                        : Array.Empty<byte>();

                    // Prepare Ticket copy with ConsoleID cleared to 0 (universal ticket under CFW/emulators)
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

                    long certOff = inspection.CertChainOffset;
                    long tikOff = inspection.TicketOffset;
                    long tmdOff = inspection.TmdOffset;
                    long contentStartOff = inspection.ContentOffset;

                    int sourceRomFsFileCount;
                    int outputRomFsFileCount;
                    var overlaidPaths = new List<string>();
                    byte[] newContentIndexMask = new byte[0x2000];
                    long currentOutOffset = contentStartOff;

                    using (var outFs = new FileStream(tempPartPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072))
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
                                // Ensure absent optional/DLC chunks also have encrypted bit cleared if untouched, or keep record
                                continue;
                            }

                            CiaFormatInspector.SetContentBit(newContentIndexMask, chunk.ContentIndex);

                            if (chunk.ContentIndex == 0)
                            {
                                // Rebuild Main CXI (Content 0) with full RomFS preservation + overlays
                                onProgress?.Invoke(14, "Abriendo partición principal CXI (#0) y construyendo árbol RomFS completo...");
                                using Stream decMainNcch = CiaFormatInspector.OpenDecryptedContentStream(
                                    srcFs, origTik, chunk, keyStore, out bool isMainNcch);
                                if (!isMainNcch)
                                    throw new InvalidDataException("El contenido principal #0 no es una partición NCCH válida.");

                                byte[] mainHdr = CiaFormatInspector.ReadBytesAt(decMainNcch, 0, 0x200);
                                long origRomFsOff = BinaryPrimitives.ReadUInt32LittleEndian(mainHdr.AsSpan(0x1B0, 4)) * CiaConstants.NcchMediaUnitSize;
                                long origRomFsLen = BinaryPrimitives.ReadUInt32LittleEndian(mainHdr.AsSpan(0x1B4, 4)) * CiaConstants.NcchMediaUnitSize;

                                using var origRomFsStream = new SubReadStream(decMainNcch, origRomFsOff, origRomFsLen, leaveOpen: true);
                                var origInventory = IvfcRomFsReader.ReadInventory(origRomFsStream);
                                sourceRomFsFileCount = origInventory.Files.Count;

                                // Assemble complete RomFS specs: start with 100% of original RomFS files
                                var mergedSpecs = new Dictionary<string, RomFsBuildFileSpec>(StringComparer.OrdinalIgnoreCase);
                                foreach (var origFile in origInventory.Files)
                                {
                                    var captured = origFile;
                                    mergedSpecs[origFile.RelativePath] = new RomFsBuildFileSpec
                                    {
                                        RelativePath = origFile.RelativePath,
                                        Length = origFile.DataLength,
                                        WriteContentTo = target => CopySlice(origRomFsStream, captured.DataOffsetInRomFs, captured.DataLength, target)
                                    };
                                }

                                // Apply ALL overlay files (replacing existing or adding new files!)
                                if (overlayFilesByRelPath != null)
                                {
                                    foreach (var kvp in overlayFilesByRelPath)
                                    {
                                        string normRel = StreamingIvfcRomFsBuilder.NormalizeRelPath(kvp.Key);
                                        string diskPath = Path.GetFullPath(kvp.Value);
                                        var diskInfo = new FileInfo(diskPath);
                                        if (!diskInfo.Exists)
                                            throw new FileNotFoundException($"Archivo de overlay no encontrado: {diskPath}", diskPath);

                                        expectedOverlaySizes[normRel] = diskInfo.Length;
                                        overlaidPaths.Add(normRel);

                                        mergedSpecs[normRel] = new RomFsBuildFileSpec
                                        {
                                            RelativePath = normRel,
                                            Length = diskInfo.Length,
                                            WriteContentTo = target => CopyDiskFile(diskPath, target)
                                        };
                                    }
                                }

                                var allDirs = origInventory.Directories
                                    .Select(d => d.RelativePath)
                                    .Concat(extraRelativeDirectories ?? Enumerable.Empty<string>())
                                    .Where(d => !string.IsNullOrEmpty(d))
                                    .ToList();

                                long c0Start = currentOutOffset;

                                // Copy NCCH prefix [0 .. origRomFsOff) (Header + ExHeader + PlainRegion + Logo + ExeFS)
                                outFs.Seek(c0Start, SeekOrigin.Begin);
                                CopySlice(decMainNcch, 0, origRomFsOff, outFs);

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
                                        $"El contenido principal reconstruido ({newNcch0Size:N0} B) excede el límite real de 4 GiB - 1 de FAT32 en Nintendo 3DS.");
                                }

                                // Update NCCH #0 header fields and recompute plaintext superblock hashes
                                BinaryPrimitives.WriteUInt32LittleEndian(mainHdr.AsSpan(0x104, 4), (uint)(newNcch0Size / CiaConstants.NcchMediaUnitSize));
                                BinaryPrimitives.WriteUInt32LittleEndian(mainHdr.AsSpan(0x1B0, 4), (uint)(origRomFsOff / CiaConstants.NcchMediaUnitSize));
                                BinaryPrimitives.WriteUInt32LittleEndian(mainHdr.AsSpan(0x1B4, 4), (uint)(ivfcRes.TotalIvfcSizeBytes / CiaConstants.NcchMediaUnitSize));
                                BinaryPrimitives.WriteUInt32LittleEndian(mainHdr.AsSpan(0x1B8, 4), ivfcRes.HashRegionSizeMediaUnits);
                                Buffer.BlockCopy(ivfcRes.SuperblockSha256, 0, mainHdr, 0x1E0, 0x20);
                                mainHdr[0x18B] = 0x00;
                                mainHdr[0x18F] = (byte)((mainHdr[0x18F] & ~0x21) | 0x04);

                                RefreshPlainNcchHeaderHashes(outFs, c0Start, mainHdr);
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
                                onProgress?.Invoke(80, $"Preservando contenido secundario #{chunk.ContentIndex} ({chunk.RoleDescription})...");
                                using Stream decSec = CiaFormatInspector.OpenDecryptedContentStream(
                                    srcFs, origTik, chunk, keyStore, out bool isSecNcch);

                                long secStart = currentOutOffset;
                                outFs.Seek(secStart, SeekOrigin.Begin);
                                CopySlice(decSec, 0, decSec.Length, outFs);

                                long secSize = decSec.Length;
                                if (secSize % CiaConstants.CiaContentAlignment != 0)
                                {
                                    long alignedSec = CiaConstants.AlignUp(secSize, CiaConstants.CiaContentAlignment);
                                    WriteZerosAt(outFs, secStart + secSize, alignedSec - secSize);
                                    secSize = alignedSec;
                                }

                                if (isSecNcch && secSize >= 0x200)
                                {
                                    byte[] secHdr = CiaFormatInspector.ReadBytesAt(outFs, secStart, 0x200);
                                    secHdr[0x18B] = 0x00;
                                    secHdr[0x18F] = (byte)((secHdr[0x18F] & ~0x21) | 0x04);
                                    RefreshPlainNcchHeaderHashes(outFs, secStart, secHdr);
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

                        // Cascade TMD ContentChunkRecords -> ContentInfo[0] -> TMD.InfoRecordHash
                        Span<byte> infoRecordsSpan = newTmd.AsSpan(tmdBodyOff + 0xC4, 0x900);
                        infoRecordsSpan.Clear();

                        ushort totalChunkRecords = (ushort)parsedChunks.Count;
                        BinaryPrimitives.WriteUInt16BigEndian(infoRecordsSpan.Slice(0, 2), 0);
                        BinaryPrimitives.WriteUInt16BigEndian(infoRecordsSpan.Slice(2, 2), totalChunkRecords);
                        SHA256.HashData(
                            newTmd.AsSpan(chunksTableStart, totalChunkRecords * CiaConstants.TmdContentChunkRecordSize),
                            infoRecordsSpan.Slice(4, 0x20));

                        SHA256.HashData(infoRecordsSpan, newTmd.AsSpan(tmdBodyOff + 0xA4, 0x20));

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
                    onProgress?.Invoke(88, "Ejecutando verificación criptográfica en frío del nuevo .cia (TMD, NCCH, IVFC 4 niveles y RomFS)...");
                    var verification = CiaPackageVerifier.VerifyColdCiaFile(
                        tempPartPath,
                        expectedTitleIdHex: inspection.TmdTitleIdHex,
                        expectedSaveDataSize: inspection.SaveDataSizeBytes,
                        minimumExpectedRomFsFileCount: inspection.RomFsFileCount,
                        expectedOverlayFileSizes: expectedOverlaySizes);

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
                    overlaidPaths.Sort(StringComparer.OrdinalIgnoreCase);

                    var telemetry = new CiaRebuildTelemetry
                    {
                        SourceCiaPath = fullSource,
                        OutputCiaPath = fullOutput,
                        SourceCiaSizeBytes = inspection.FileSizeBytes,
                        OutputCiaSizeBytes = outLen,
                        SourceCiaSha256Hex = sourcePostSha256,
                        OutputCiaSha256Hex = outputSha256,
                        TitleIdHex = inspection.TmdTitleIdHex,
                        TitleVersionFormatted = inspection.TmdTitleVersionFormatted,
                        PreservedSaveDataSizeBytes = inspection.SaveDataSizeBytes,
                        PreservedContentsCount = verification.VerifiedContentChunksCount,
                        SourceRomFsFileCount = inspection.RomFsFileCount,
                        OutputRomFsFileCount = verification.VerifiedRomFsFilesCount,
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

        private static void RefreshPlainNcchHeaderHashes(Stream ncchContainerStream, long ncchStart, byte[] ncchHdr512)
        {
            uint exHdrDataSize = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr512.AsSpan(0x180, 4));
            if (exHdrDataSize > 0)
            {
                byte[] exHdr400 = CiaFormatInspector.ReadBytesAt(ncchContainerStream, ncchStart + 0x200, 0x400);
                SHA256.HashData(exHdr400, ncchHdr512.AsSpan(0x160, 0x20));
            }

            long logoOff = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr512.AsSpan(0x198, 4)) * CiaConstants.NcchMediaUnitSize;
            long logoLen = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr512.AsSpan(0x19C, 4)) * CiaConstants.NcchMediaUnitSize;
            if (logoOff > 0 && logoLen > 0)
            {
                byte[] logoBytes = CiaFormatInspector.ReadBytesAt(ncchContainerStream, ncchStart + logoOff, (int)logoLen);
                SHA256.HashData(logoBytes, ncchHdr512.AsSpan(0x130, 0x20));
            }

            long exefsOff = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr512.AsSpan(0x1A0, 4)) * CiaConstants.NcchMediaUnitSize;
            long exefsHashLen = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr512.AsSpan(0x1A8, 4)) * CiaConstants.NcchMediaUnitSize;
            if (exefsOff > 0 && exefsHashLen > 0)
            {
                byte[] exefsTop = CiaFormatInspector.ReadBytesAt(ncchContainerStream, ncchStart + exefsOff, (int)exefsHashLen);
                SHA256.HashData(exefsTop, ncchHdr512.AsSpan(0x1C0, 0x20));
            }

            long romfsOff = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr512.AsSpan(0x1B0, 4)) * CiaConstants.NcchMediaUnitSize;
            long romfsHashLen = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr512.AsSpan(0x1B8, 4)) * CiaConstants.NcchMediaUnitSize;
            if (romfsOff > 0 && romfsHashLen > 0)
            {
                byte[] romfsTop = CiaFormatInspector.ReadBytesAt(ncchContainerStream, ncchStart + romfsOff, (int)romfsHashLen);
                SHA256.HashData(romfsTop, ncchHdr512.AsSpan(0x1E0, 0x20));
            }
        }

        private static void CopySlice(Stream source, long sourceOffset, long length, Stream destination)
        {
            source.Seek(sourceOffset, SeekOrigin.Begin);
            byte[] buf = new byte[131072];
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
            using var fs = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072);
            byte[] buf = new byte[131072];
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

        private static byte[] ComputeStreamSliceSha256(Stream s, long offset, long length)
        {
            s.Seek(offset, SeekOrigin.Begin);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buf = new byte[131072];
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
