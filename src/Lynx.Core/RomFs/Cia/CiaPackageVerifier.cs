using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Lynx.Core.RomFs.Cia
{
    /// <summary>
    /// Cold-start cryptographic and structural verifier for generated <c>.cia</c> files.
    /// Re-opens the file from disk as an external installer (FBI / Citra / Azahar Service::AM) would,
    /// verifying CIA alignment, TMD SHA-256 cascade, every content chunk SHA-256, NCCH superblock hashes,
    /// full 4-level IVFC SHA-256 Merkle tree, kernel hash-bucket lookups, and expected RomFS file inventory.
    /// </summary>
    public static class CiaPackageVerifier
    {
        public static CiaVerificationResult VerifyColdCiaFile(
            string ciaFilePath,
            string expectedTitleIdHex = null,
            uint? expectedSaveDataSize = null,
            int? minimumExpectedRomFsFileCount = null,
            IReadOnlyDictionary<string, long> expectedOverlayFileSizes = null)
        {
            var result = new CiaVerificationResult();

            try
            {
                if (!File.Exists(ciaFilePath))
                {
                    result.Errors.Add($"No existe el archivo .cia generado: {ciaFilePath}");
                    return result;
                }

                // 1. Inspect with empty KeyStore — a rebuilt Plain CIA MUST be 100% readable with zero external keys
                var emptyKeys = new CtrKeyStore();
                var report = CiaFormatInspector.InspectFile(ciaFilePath, emptyKeys, computeFullFileSha256: false);

                if (!report.IsValidCiaStructure)
                {
                    result.Errors.AddRange(report.LimitationsAndWarnings);
                    result.Errors.Add("La estructura general de secciones del .cia es inválida.");
                    return result;
                }
                result.ChecksPassed.Add("Cabecera CIA (0x2020) y alineación 0x40 de Cert/Ticket/TMD/Content/Meta verificadas.");

                if (report.EncryptionKind != CiaEncryptionKind.PlainCiaPlainNcch)
                {
                    result.Errors.Add($"Se esperaba un Plain CIA desencriptado (NoCrypto), pero se detectó '{report.EncryptionKind}'.");
                    return result;
                }
                result.ChecksPassed.Add("Estado Plain CIA + Plain NCCH (NoCrypto=0x04, sin dependencia de claves externas) verificado.");

                if (!report.TmdInternalHashesValid)
                {
                    result.Errors.Add("La cascada interna SHA-256 del TMD (ContentChunk -> ContentInfo -> InfoRecordHash) es inválida.");
                    return result;
                }
                result.ChecksPassed.Add("Cascada SHA-256 del TMD (ContentInfo[0..63] -> Header) verificada.");

                if (report.IsTicketPersonalized)
                {
                    result.Errors.Add($"El Ticket conserva un ConsoleID no nulo (0x{report.TicketConsoleDeviceId:X8}).");
                    return result;
                }
                result.ChecksPassed.Add("Ticket universal (ConsoleID = 0) verificado.");

                if (!string.IsNullOrWhiteSpace(expectedTitleIdHex) &&
                    !report.TmdTitleIdHex.Equals(expectedTitleIdHex, StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add($"TitleID en TMD ({report.TmdTitleIdHex}) no coincide con el original ({expectedTitleIdHex}).");
                    return result;
                }

                if (!report.TicketTitleIdHex.Equals(report.TmdTitleIdHex, StringComparison.OrdinalIgnoreCase) ||
                    !report.NcchProgramIdHex.Equals(report.TmdTitleIdHex, StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add(
                        $"Inconsistencia de TitleID entre Ticket ({report.TicketTitleIdHex}), TMD ({report.TmdTitleIdHex}) y NCCH ProgramID ({report.NcchProgramIdHex}).");
                    return result;
                }
                result.ChecksPassed.Add($"Consistencia de TitleID ({report.TmdTitleIdHex}) y versión ({report.TmdTitleVersionFormatted}) verificada.");

                if (expectedSaveDataSize.HasValue && report.SaveDataSizeBytes != expectedSaveDataSize.Value)
                {
                    result.Errors.Add(
                        $"SaveDataSize en TMD ({report.SaveDataSizeBytes} B) no coincide con el original ({expectedSaveDataSize.Value} B).");
                    return result;
                }
                result.ChecksPassed.Add($"SaveDataSize preservado ({report.SaveDataSizeBytes / 1024} KB).");

                // 2. Stream-verify SHA-256 of EVERY present Content Chunk against TMD
                using var fs = new FileStream(ciaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072);
                int verifiedChunks = 0;

                foreach (var chunk in report.ContentChunks.Where(c => c.IsPresentInCia))
                {
                    if (chunk.IsCiaLayerEncrypted)
                    {
                        result.Errors.Add($"El contenido secundario #{chunk.ContentIndex} sigue marcado como cifrado (0x0001) en el TMD.");
                        return result;
                    }

                    if (chunk.SizeBytes > CiaConstants.MaxFat32ContentSizeBytes)
                    {
                        result.Errors.Add(
                            $"El contenido #{chunk.ContentIndex} ({chunk.SizeBytes:N0} B) supera el límite FAT32 de 3DS (4 GiB - 1).");
                        return result;
                    }

                    string actualChunkSha = ComputeSliceSha256Hex(fs, chunk.CiaFileOffset, chunk.SizeBytes);
                    if (!actualChunkSha.Equals(chunk.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Errors.Add(
                            $"SHA-256 del contenido #{chunk.ContentIndex} ({actualChunkSha}) no coincide con el registrado en TMD ({chunk.Sha256Hex}).");
                        return result;
                    }

                    // Also check NCCH NoCrypto flag & superblock hashes if NCCH
                    if (chunk.SizeBytes >= 0x200)
                    {
                        byte[] ncchHdr = CiaFormatInspector.ReadBytesAt(fs, chunk.CiaFileOffset, 0x200);
                        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.AsSpan(0x100, 4));
                        if (magic == CiaConstants.NcchMagic)
                        {
                            if ((ncchHdr[0x18F] & 0x04) == 0)
                            {
                                result.Errors.Add($"La partición NCCH del contenido #{chunk.ContentIndex} no tiene activado NoCrypto (0x04).");
                                return result;
                            }

                            VerifyNcchSuperblockHashesOrThrow(fs, chunk.CiaFileOffset, ncchHdr, chunk.ContentIndex);
                        }
                    }

                    verifiedChunks++;
                }

                result.VerifiedContentChunksCount = verifiedChunks;
                result.ChecksPassed.Add($"SHA-256 completo y superbloques NCCH verificados en los {verifiedChunks} contenido(s) del .cia.");

                // 3. Full 4-Level IVFC Merkle tree + Kernel Hash-Bucket + File Inventory verification on Content 0
                var mainChunk = report.ContentChunks.First(c => c.ContentIndex == 0 && c.IsPresentInCia);
                byte[] mainNcchHdr = CiaFormatInspector.ReadBytesAt(fs, mainChunk.CiaFileOffset, 0x200);
                long romfsOff = BinaryPrimitives.ReadUInt32LittleEndian(mainNcchHdr.AsSpan(0x1B0, 4)) * CiaConstants.NcchMediaUnitSize;
                long romfsLen = BinaryPrimitives.ReadUInt32LittleEndian(mainNcchHdr.AsSpan(0x1B4, 4)) * CiaConstants.NcchMediaUnitSize;

                using var romfsStream = new SubReadStream(fs, mainChunk.CiaFileOffset + romfsOff, romfsLen, leaveOpen: true);
                IvfcRomFsReader.VerifyIvfcAndHashTablesOrThrow(romfsStream, out int ivfcBlocks, out var inventory);

                result.VerifiedIvfcBlocksCount = ivfcBlocks;
                result.VerifiedRomFsDirectoriesCount = inventory.Directories.Count;
                result.VerifiedRomFsFilesCount = inventory.Files.Count;
                result.ChecksPassed.Add(
                    $"Árbol Merkle IVFC (Niveles 0, 1, 2 y 3: {ivfcBlocks} bloques SHA-256) y tablas hash de rutas verificados.");

                if (minimumExpectedRomFsFileCount.HasValue && inventory.Files.Count < minimumExpectedRomFsFileCount.Value)
                {
                    result.Errors.Add(
                        $"Pérdida de archivos en RomFS detectada: {inventory.Files.Count} archivos en salida < {minimumExpectedRomFsFileCount.Value} esperados.");
                    return result;
                }

                if (expectedOverlayFileSizes != null && expectedOverlayFileSizes.Count > 0)
                {
                    var byPath = inventory.Files.ToDictionary(f => f.RelativePath, f => f, StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in expectedOverlayFileSizes)
                    {
                        string norm = StreamingIvfcRomFsBuilder.NormalizeRelPath(kvp.Key);
                        if (!byPath.TryGetValue(norm, out var found))
                        {
                            result.Errors.Add($"El archivo modificado '{norm}' no aparece en el RomFS del .cia generado.");
                            return result;
                        }
                        if (found.DataLength != kvp.Value)
                        {
                            result.Errors.Add(
                                $"Tamaño de '{norm}' en RomFS ({found.DataLength} B) != tamaño esperado ({kvp.Value} B).");
                            return result;
                        }
                    }
                    result.ChecksPassed.Add(
                        $"Presencia y tamaños exactos verificados para los {expectedOverlayFileSizes.Count} archivo(s) incorporados al RomFS.");
                }

                // 4. Verify ie_a.fa inside the rebuilt .cia starts with valid ARC0 magic ("ARC0" = 0x30435241)
                var ieEntry = inventory.Files.FirstOrDefault(f => f.RelativePath.Equals("ie_a.fa", StringComparison.OrdinalIgnoreCase));
                if (ieEntry == null || ieEntry.DataLength < 0x40)
                {
                    result.Errors.Add("El RomFS del .cia generado no contiene un contenedor 'ie_a.fa' válido.");
                    return result;
                }

                Span<byte> arcMagic = stackalloc byte[4];
                romfsStream.Seek(ieEntry.DataOffsetInRomFs, SeekOrigin.Begin);
                IvfcRomFsReader.ReadExact(romfsStream, arcMagic);
                if (BinaryPrimitives.ReadUInt32LittleEndian(arcMagic) != 0x30435241u)
                {
                    result.Errors.Add("El archivo 'ie_a.fa' dentro del .cia generado no tiene cabecera válida 'ARC0'.");
                    return result;
                }
                result.ChecksPassed.Add($"Contenedor principal 'ie_a.fa' ({ieEntry.DataLength:N0} B, cabecera ARC0) verificado en frío.");

                result.IsValid = result.Errors.Count == 0;
                return result;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Fallo durante la verificación en frío del .cia: {ex.Message}");
                result.IsValid = false;
                return result;
            }
        }

        private static void VerifyNcchSuperblockHashesOrThrow(
            Stream ciaStream,
            long ncchStart,
            ReadOnlySpan<byte> ncchHdr,
            ushort contentIdx)
        {
            Span<byte> actual = stackalloc byte[32];

            uint exHdrDataSize = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x180, 4));
            if (exHdrDataSize > 0)
            {
                byte[] exHdr400 = CiaFormatInspector.ReadBytesAt(ciaStream, ncchStart + 0x200, 0x400);
                SHA256.HashData(exHdr400, actual);
                if (!actual.SequenceEqual(ncchHdr.Slice(0x160, 0x20)))
                    throw new InvalidDataException($"Hash de ExHeader inválido en contenido NCCH #{contentIdx}.");
            }

            long exefsOff = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x1A0, 4)) * CiaConstants.NcchMediaUnitSize;
            long exefsHashLen = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x1A8, 4)) * CiaConstants.NcchMediaUnitSize;
            if (exefsOff > 0 && exefsHashLen > 0)
            {
                byte[] exefsTop = CiaFormatInspector.ReadBytesAt(ciaStream, ncchStart + exefsOff, (int)exefsHashLen);
                SHA256.HashData(exefsTop, actual);
                if (!actual.SequenceEqual(ncchHdr.Slice(0x1C0, 0x20)))
                    throw new InvalidDataException($"Hash de superbloque ExeFS inválido en contenido NCCH #{contentIdx}.");
            }

            long romfsOff = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x1B0, 4)) * CiaConstants.NcchMediaUnitSize;
            long romfsHashLen = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x1B8, 4)) * CiaConstants.NcchMediaUnitSize;
            if (romfsOff > 0 && romfsHashLen > 0)
            {
                byte[] romfsTop = CiaFormatInspector.ReadBytesAt(ciaStream, ncchStart + romfsOff, (int)romfsHashLen);
                SHA256.HashData(romfsTop, actual);
                if (!actual.SequenceEqual(ncchHdr.Slice(0x1E0, 0x20)))
                    throw new InvalidDataException($"Hash de superbloque RomFS inválido en contenido NCCH #{contentIdx}.");
            }
        }

        private static string ComputeSliceSha256Hex(Stream s, long offset, long length)
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
                    throw new EndOfStreamException("Unexpected EOF hashing CIA content chunk.");
                sha.AppendData(buf, 0, r);
                rem -= r;
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
    }
}
