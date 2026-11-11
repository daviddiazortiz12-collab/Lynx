using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Lynx.Core.RomFs.Cia
{
    /// <summary>
    /// Cold-start cryptographic and structural verifier for generated <c>.cia</c> files targeting real
    /// Nintendo 2DS / 3DS hardware (<c>FBI</c> + <c>Process9</c> + <c>Luma3DS</c>) and emulators.
    /// Re-opens the file from disk with zero keys, verifying CIA alignment, TMD SHA-256 cascade, every content
    /// chunk SHA-256, NCCH superblock hashes, ExHeader <c>SDApplication</c> flag, all ExeFS section SHA-256s
    /// (including ARM11 <c>.code</c>), full 4-level IVFC SHA-256 Merkle tree, case-sensitive UTF-16LE kernel
    /// hash-bucket lookups, and <c>ie_a.fa</c> <c>ARC0</c> header integrity.
    /// </summary>
    public static class CiaPackageVerifier
    {
        public static CiaVerificationResult VerifyColdCiaFile(
            string ciaFilePath,
            string expectedTitleIdHex = null,
            uint? expectedSaveDataSize = null,
            int? minimumExpectedRomFsFileCount = null,
            IReadOnlyDictionary<string, long> expectedOverlayFileSizes = null,
            ushort? expectedTitleVersionRaw = null)
        {
            var result = new CiaVerificationResult();

            try
            {
                if (!File.Exists(ciaFilePath))
                {
                    result.Errors.Add($"No existe el archivo .cia generado: {ciaFilePath}");
                    return result;
                }

                // 1. Inspect with empty KeyStore — a rebuilt Plain CIA MUST be 100% readable with zero keys
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
                result.ChecksPassed.Add("Estado Plain CIA + Plain NCCH (NoCrypto=0x04, compatible con FBI en 2DS/3DS y emuladores) verificado.");

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
                if (expectedTitleVersionRaw.HasValue && report.TmdTitleVersionRaw != expectedTitleVersionRaw.Value)
                {
                    result.Errors.Add(
                        $"TitleVersion en TMD ({report.TmdTitleVersionFormatted} / 0x{report.TmdTitleVersionRaw:X4}) no coincide con la versión esperada ({CiaConstants.FormatTitleVersion(expectedTitleVersionRaw.Value)} / 0x{expectedTitleVersionRaw.Value:X4}).");
                    return result;
                }

                result.ChecksPassed.Add($"Consistencia de TitleID ({report.TmdTitleIdHex}) y versión ({report.TmdTitleVersionFormatted} / raw={report.TmdTitleVersionRaw}) verificada.");

                if (expectedSaveDataSize.HasValue && report.SaveDataSizeBytes != expectedSaveDataSize.Value)
                {
                    result.Errors.Add(
                        $"SaveDataSize en TMD ({report.SaveDataSizeBytes} B) no coincide con el original ({expectedSaveDataSize.Value} B).");
                    return result;
                }
                result.ChecksPassed.Add($"SaveDataSize preservado ({report.SaveDataSizeBytes / 1024} KB).");

                // 2. Stream-verify SHA-256 of EVERY present Content Chunk against TMD + NCCH/ExeFS/ExHeader invariants
                using var fs = new FileStream(ciaFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 262144);

                if (report.MetaSize >= CiaConstants.StandardCiaMetaSize)
                {
                    byte[] smdhMagicBytes = CiaFormatInspector.ReadBytesAt(fs, report.MetaOffset + CiaConstants.CiaMetaBaseSize, 4);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(smdhMagicBytes) != CiaConstants.SmdhMagic)
                    {
                        result.Errors.Add("La sección Meta del .cia no contiene un icono SMDH válido (requerido por FBI en 2DS/3DS).");
                        return result;
                    }
                    result.ChecksPassed.Add("Sección Meta e icono SMDH para FBI / menú HOME de 2DS/3DS verificados.");
                }

                int verifiedChunks = 0;

                foreach (var chunk in report.ContentChunks.Where(c => c.IsPresentInCia))
                {
                    if (chunk.IsCiaLayerEncrypted)
                    {
                        result.Errors.Add($"El contenido #{chunk.ContentIndex} sigue marcado como cifrado (0x0001) en el TMD.");
                        return result;
                    }

                    if (chunk.SizeBytes > CiaConstants.MaxFat32ContentSizeBytes)
                    {
                        result.Errors.Add(
                            $"El contenido #{chunk.ContentIndex} ({chunk.SizeBytes:N0} B) supera el límite FAT32 de 2DS/3DS (4 GiB - 1).");
                        return result;
                    }

                    string actualChunkSha = ComputeSliceSha256Hex(fs, chunk.CiaFileOffset, chunk.SizeBytes);
                    if (!actualChunkSha.Equals(chunk.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Errors.Add(
                            $"SHA-256 del contenido #{chunk.ContentIndex} ({actualChunkSha}) no coincide con el registrado en TMD ({chunk.Sha256Hex}).");
                        return result;
                    }

                    // Check NCCH NoCrypto flag, ExHeader SDApplication flag, ExeFS section hashes (.code/icon/banner), & superblocks
                    if (chunk.SizeBytes >= 0x200)
                    {
                        byte[] ncchHdr = CiaFormatInspector.ReadBytesAt(fs, chunk.CiaFileOffset, 0x200);
                        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.AsSpan(0x100, 4));
                        if (magic == CiaConstants.NcchMagic)
                        {
                            if ((ncchHdr[0x18F] & 0x04) == 0 || (ncchHdr[0x18F] & 0x21) != 0 || ncchHdr[0x18B] != 0)
                            {
                                result.Errors.Add($"La partición NCCH del contenido #{chunk.ContentIndex} conserva indicadores de cifrado residuales (flags[3]=0x{ncchHdr[0x18B]:X2}, flags[7]=0x{ncchHdr[0x18F]:X2}).");
                                return result;
                            }

                            VerifyNcchSuperblockAndExeFsOrThrow(fs, chunk.CiaFileOffset, ncchHdr, chunk.ContentIndex);
                        }
                    }

                    verifiedChunks++;
                }

                result.VerifiedContentChunksCount = verifiedChunks;
                result.ChecksPassed.Add($"SHA-256 completo, superbloques NCCH y binario ARM11 ExeFS verificados en los {verifiedChunks} contenido(s) del .cia.");

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
                    $"Árbol Merkle IVFC (Niveles 0, 1, 2 y 3: {ivfcBlocks} bloques SHA-256) y tablas hash UTF-16LE de rutas verificados.");

                if (minimumExpectedRomFsFileCount.HasValue && inventory.Files.Count < minimumExpectedRomFsFileCount.Value)
                {
                    result.Errors.Add(
                        $"Pérdida de archivos en RomFS detectada: {inventory.Files.Count} archivos en salida < {minimumExpectedRomFsFileCount.Value} esperados.");
                    return result;
                }

                if (expectedOverlayFileSizes != null && expectedOverlayFileSizes.Count > 0)
                {
                    var byExactPath = inventory.Files.ToDictionary(f => f.RelativePath, f => f, StringComparer.Ordinal);
                    var byIgnoreCase = inventory.Files.ToDictionary(f => f.RelativePath, f => f, StringComparer.OrdinalIgnoreCase);

                    foreach (var kvp in expectedOverlayFileSizes)
                    {
                        string norm = StreamingIvfcRomFsBuilder.NormalizeRelPath(kvp.Key);
                        if (!byExactPath.TryGetValue(norm, out var found) && !byIgnoreCase.TryGetValue(norm, out found))
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
                        $"Presencia, mayúsculas/minúsculas de ruta y tamaños exactos verificados para los {expectedOverlayFileSizes.Count} archivo(s) incorporados al RomFS.");
                }

                // 4. Verify ie_a.fa inside the rebuilt .cia starts with valid ARC0 header and valid ARM11 TableChunkSize!
                var ieEntry = inventory.Files.FirstOrDefault(f => f.RelativePath.Equals("ie_a.fa", StringComparison.Ordinal));
                if (ieEntry == null)
                {
                    bool wrongCaseExists = inventory.Files.Any(f => f.RelativePath.Equals("ie_a.fa", StringComparison.OrdinalIgnoreCase));
                    result.Errors.Add(wrongCaseExists
                        ? "El archivo 'ie_a.fa' en el RomFS tiene mayúsculas/minúsculas incorrectas (3DS exige 'ie_a.fa' exacto en minúsculas)."
                        : "El RomFS del .cia generado no contiene un contenedor 'ie_a.fa' válido.");
                    return result;
                }

                if (ieEntry.DataLength < 0x48)
                {
                    result.Errors.Add("El archivo 'ie_a.fa' en el RomFS está truncado (< 72 bytes).");
                    return result;
                }

                Span<byte> arcHdr = stackalloc byte[0x48];
                romfsStream.Seek(ieEntry.DataOffsetInRomFs, SeekOrigin.Begin);
                IvfcRomFsReader.ReadExact(romfsStream, arcHdr);
                if (BinaryPrimitives.ReadUInt32LittleEndian(arcHdr.Slice(0x00, 4)) != 0x30435241u)
                {
                    result.Errors.Add("El archivo 'ie_a.fa' dentro del .cia generado no tiene cabecera válida 'ARC0'.");
                    return result;
                }

                short dirEntriesCount = BinaryPrimitives.ReadInt16LittleEndian(arcHdr.Slice(0x18, 2));
                short dirHashCount = BinaryPrimitives.ReadInt16LittleEndian(arcHdr.Slice(0x1A, 2));
                int fileEntriesCount = BinaryPrimitives.ReadInt32LittleEndian(arcHdr.Slice(0x1C, 4));
                int tableChunkSize = BinaryPrimitives.ReadInt32LittleEndian(arcHdr.Slice(0x20, 4));
                int minStructTableBytes = dirEntriesCount * 20 + dirHashCount * 4 + fileEntriesCount * 16;
                if (tableChunkSize < minStructTableBytes)
                {
                    result.Errors.Add(
                        $"ARC0 TableChunkSize inválido para 2DS/3DS ({tableChunkSize} B < mínimo descomprimido {minStructTableBytes} B).");
                    return result;
                }

                result.ChecksPassed.Add(
                    $"Contenedor principal 'ie_a.fa' ({ieEntry.DataLength:N0} B, cabecera ARC0 y TableChunkSize={tableChunkSize:N0} B apto para RAM de 2DS) verificado en frío.");

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

        private static void VerifyNcchSuperblockAndExeFsOrThrow(
            Stream ciaStream,
            long ncchStart,
            ReadOnlySpan<byte> ncchHdr,
            ushort contentIdx)
        {
            Span<byte> actual = stackalloc byte[32];
            ulong programId = BinaryPrimitives.ReadUInt64LittleEndian(ncchHdr.Slice(0x118, 8));

            uint exHdrDataSize = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x180, 4));
            if (exHdrDataSize > 0)
            {
                byte[] exHdr400 = CiaFormatInspector.ReadBytesAt(ciaStream, ncchStart + 0x200, 0x400);
                SHA256.HashData(exHdr400, actual);
                if (!actual.SequenceEqual(ncchHdr.Slice(0x160, 0x20)))
                    throw new InvalidDataException($"Hash de ExHeader inválido en contenido NCCH #{contentIdx}.");

                if (contentIdx == 0)
                {
                    if ((exHdr400[0x0D] & 0x02) == 0)
                        throw new InvalidDataException("El ExHeader del contenido #0 no tiene activado el flag SDApplication (0x02) necesario para guardar partida en SD en 2DS/3DS.");

                    ulong jumpId = BinaryPrimitives.ReadUInt64LittleEndian(exHdr400.AsSpan(0x1C8, 8));
                    ulong aciProgId = BinaryPrimitives.ReadUInt64LittleEndian(exHdr400.AsSpan(0x200, 8));
                    if (jumpId != programId || aciProgId != programId)
                        throw new InvalidDataException($"ExHeader JumpID (0x{jumpId:X16}) / ACI ProgramID (0x{aciProgId:X16}) no coinciden con NCCH ProgramID (0x{programId:X16}).");
                }
            }

            long logoOff = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x198, 4)) * CiaConstants.NcchMediaUnitSize;
            long logoLen = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x19C, 4)) * CiaConstants.NcchMediaUnitSize;
            if (logoOff > 0 && logoLen > 0)
            {
                byte[] logoBytes = CiaFormatInspector.ReadBytesAt(ciaStream, ncchStart + logoOff, (int)logoLen);
                SHA256.HashData(logoBytes, actual);
                if (!actual.SequenceEqual(ncchHdr.Slice(0x130, 0x20)))
                    throw new InvalidDataException($"Hash de región Logo inválido en contenido NCCH #{contentIdx}.");
            }

            long exefsOff = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x1A0, 4)) * CiaConstants.NcchMediaUnitSize;
            long exefsSize = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x1A4, 4)) * CiaConstants.NcchMediaUnitSize;
            long exefsHashLen = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.Slice(0x1A8, 4)) * CiaConstants.NcchMediaUnitSize;
            if (exefsOff > 0 && exefsHashLen > 0)
            {
                byte[] exefsTop = CiaFormatInspector.ReadBytesAt(ciaStream, ncchStart + exefsOff, (int)exefsHashLen);
                SHA256.HashData(exefsTop, actual);
                if (!actual.SequenceEqual(ncchHdr.Slice(0x1C0, 0x20)))
                    throw new InvalidDataException($"Hash de superbloque ExeFS inválido en contenido NCCH #{contentIdx}.");

                if (exefsSize >= 0x200 && exefsTop.Length >= 0x200)
                {
                    CiaPackageRebuilder.VerifyExeFsSectionsOrThrow(ciaStream, ncchStart + exefsOff, exefsSize, exefsTop, contentIdx);
                }
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
            return Convert.ToHexString(CiaPackageRebuilder.ComputeStreamSliceSha256(s, offset, length));
        }
    }
}
