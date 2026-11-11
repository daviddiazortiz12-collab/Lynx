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
    /// Non-destructive, strictly read-only local inspector for Nintendo 3DS <c>.cia</c> files.
    /// Never modifies the inspected file and never transmits any ROM bytes outside the user's machine.
    /// </summary>
    public static class CiaFormatInspector
    {
        public static CiaInspectionReport InspectFile(
            string ciaPath,
            CtrKeyStore keyStore = null,
            bool computeFullFileSha256 = false)
        {
            if (string.IsNullOrWhiteSpace(ciaPath))
                throw new ArgumentException("Ruta de archivo .cia vacía.", nameof(ciaPath));

            string fullPath = Path.GetFullPath(ciaPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"No existe el archivo .cia: {fullPath}", fullPath);

            keyStore ??= CtrKeyStore.LoadAvailableLocalKeys();
            var fi = new FileInfo(fullPath);
            var report = new CiaInspectionReport
            {
                FilePath = fullPath,
                FileSizeBytes = fi.Length,
                AesKeysSourcePath = keyStore.LoadedAesKeysPath
            };

            if (computeFullFileSha256)
            {
                report.Sha256Hex = ComputeStreamingFileSha256Hex(fullPath);
            }

            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            if (fs.Length < CiaConstants.CiaArchiveHeaderSize)
            {
                report.IsValidCiaStructure = false;
                report.LimitationsAndWarnings.Add("El archivo es demasiado pequeño para contener una cabecera CIA válida (0x2020 bytes).");
                return report;
            }

            // 1. Parse 0x2020 CIA Archive Header
            byte[] ciaHdr = new byte[CiaConstants.CiaArchiveHeaderSize];
            ReadExact(fs, ciaHdr);

            uint hdrSize = BinaryPrimitives.ReadUInt32LittleEndian(ciaHdr.AsSpan(0x00, 4));
            ushort ciaType = BinaryPrimitives.ReadUInt16LittleEndian(ciaHdr.AsSpan(0x04, 2));
            ushort ciaVer = BinaryPrimitives.ReadUInt16LittleEndian(ciaHdr.AsSpan(0x06, 2));
            uint certSize = BinaryPrimitives.ReadUInt32LittleEndian(ciaHdr.AsSpan(0x08, 4));
            uint tikSize = BinaryPrimitives.ReadUInt32LittleEndian(ciaHdr.AsSpan(0x0C, 4));
            uint tmdSize = BinaryPrimitives.ReadUInt32LittleEndian(ciaHdr.AsSpan(0x10, 4));
            uint metaSize = BinaryPrimitives.ReadUInt32LittleEndian(ciaHdr.AsSpan(0x14, 4));
            ulong contentSize = BinaryPrimitives.ReadUInt64LittleEndian(ciaHdr.AsSpan(0x18, 8));
            ReadOnlySpan<byte> contentMask = ciaHdr.AsSpan(0x20, 0x2000);

            if (hdrSize != CiaConstants.CiaArchiveHeaderSize || certSize == 0 || tikSize == 0 || tmdSize == 0 || contentSize == 0)
            {
                report.IsValidCiaStructure = false;
                report.LimitationsAndWarnings.Add(
                    $"Cabecera CIA inválida o no estándar (HeaderSize=0x{hdrSize:X}, Cert=0x{certSize:X}, Tik=0x{tikSize:X}, TMD=0x{tmdSize:X}).");
                return report;
            }

            long certOff = CiaConstants.AlignUp(hdrSize, CiaConstants.CiaSectionAlignment);
            long tikOff = CiaConstants.AlignUp(certOff + certSize, CiaConstants.CiaSectionAlignment);
            long tmdOff = CiaConstants.AlignUp(tikOff + tikSize, CiaConstants.CiaSectionAlignment);
            long contentOff = CiaConstants.AlignUp(tmdOff + tmdSize, CiaConstants.CiaSectionAlignment);
            long contentEnd = contentOff + (long)contentSize;
            long metaOff = metaSize > 0 ? CiaConstants.AlignUp(contentEnd, CiaConstants.CiaSectionAlignment) : 0;
            long minExpectedFileLen = metaSize > 0 ? metaOff + metaSize : contentEnd;

            report.HeaderSize = hdrSize;
            report.CiaType = ciaType;
            report.CiaFormatVersion = ciaVer;
            report.CertChainOffset = certOff;
            report.CertChainSize = certSize;
            report.TicketOffset = tikOff;
            report.TicketSize = tikSize;
            report.TmdOffset = tmdOff;
            report.TmdSize = tmdSize;
            report.ContentOffset = contentOff;
            report.ContentSize = (long)contentSize;
            report.MetaOffset = metaOff;
            report.MetaSize = metaSize;

            if (fs.Length < minExpectedFileLen)
            {
                report.IsValidCiaStructure = false;
                report.LimitationsAndWarnings.Add(
                    $"Archivo .cia truncado: tamaño real {fs.Length:N0} B < tamaño esperado {minExpectedFileLen:N0} B.");
                return report;
            }

            // 2. Parse Ticket
            byte[] tikBytes = ReadBytesAt(fs, tikOff, (int)tikSize);
            if (!TryParseTicket(tikBytes, out int tikBodyOff, out ulong tikTitleId, out uint consoleId, out byte commonKeyIdx, out byte[] encTitleKey))
            {
                report.IsValidCiaStructure = false;
                report.LimitationsAndWarnings.Add("No se pudo interpretar el bloque Ticket del .cia (tipo de firma desconocido o truncado).");
                return report;
            }

            report.TicketIssuer = ReadAsciiZeroTerminated(tikBytes.AsSpan(tikBodyOff, 0x40));
            report.TicketTitleIdHex = $"{tikTitleId:X16}";
            report.TicketCommonKeyIndex = commonKeyIdx;
            report.TicketConsoleDeviceId = consoleId;

            // 3. Parse TMD & Content Chunks
            byte[] tmdBytes = ReadBytesAt(fs, tmdOff, (int)tmdSize);
            if (!TryParseTmd(tmdBytes, contentMask, contentOff, out int tmdBodyOff, out bool tmdHashesValid, out var chunks))
            {
                report.IsValidCiaStructure = false;
                report.LimitationsAndWarnings.Add("No se pudo interpretar el bloque Title Metadata (TMD) del .cia.");
                return report;
            }

            ulong tmdTitleId = BinaryPrimitives.ReadUInt64BigEndian(tmdBytes.AsSpan(tmdBodyOff + 0x4C, 8));
            uint saveDataSizeLe = BinaryPrimitives.ReadUInt32LittleEndian(tmdBytes.AsSpan(tmdBodyOff + 0x5A, 4));
            ushort rawTitleVer = BinaryPrimitives.ReadUInt16BigEndian(tmdBytes.AsSpan(tmdBodyOff + 0x9C, 2));
            ushort contentCount = BinaryPrimitives.ReadUInt16BigEndian(tmdBytes.AsSpan(tmdBodyOff + 0x9E, 2));

            report.IsValidCiaStructure = true;
            report.TmdIssuer = ReadAsciiZeroTerminated(tmdBytes.AsSpan(tmdBodyOff, 0x40));
            report.TmdTitleIdHex = $"{tmdTitleId:X16}";
            report.TmdTitleVersionRaw = rawTitleVer;
            report.TmdTitleVersionFormatted = CiaConstants.FormatTitleVersion(rawTitleVer);
            report.SaveDataSizeBytes = saveDataSizeLe;
            report.ContentCount = contentCount;
            report.TmdInternalHashesValid = tmdHashesValid;
            report.ContentChunks.AddRange(chunks);

            // Check if this is an Update Patch CIA (0004000E) instead of a Base Game CIA (00040000)
            uint titleCategory = (uint)(tmdTitleId >> 32);
            if (titleCategory == 0x0004000Eu)
            {
                report.LimitationsAndWarnings.Add(
                    $"ATENCIÓN: Este .cia tiene TitleID {report.TmdTitleIdHex} (categoría 0004000E = parche de actualización), NO es el juego base completo (00040000).");
            }

            // 4. Parse optional Meta / SMDH icon title
            if (metaSize >= CiaConstants.CiaMetaBaseSize)
            {
                byte[] metaBytes = ReadBytesAt(fs, metaOff, (int)Math.Min(metaSize, CiaConstants.StandardCiaMetaSize));
                report.MetaCoreVersion = BinaryPrimitives.ReadUInt32LittleEndian(metaBytes.AsSpan(0x180, 4));
                if (metaBytes.Length >= CiaConstants.StandardCiaMetaSize)
                {
                    uint smdhMagic = BinaryPrimitives.ReadUInt32LittleEndian(metaBytes.AsSpan(CiaConstants.CiaMetaBaseSize, 4));
                    if (smdhMagic == CiaConstants.SmdhMagic)
                    {
                        // Spanish slot is index 5 (each application title slot is 0x200 bytes starting at SMDH + 0x08; fallback to English slot 1)
                        string esShort = ReadUtf16ZeroTerminated(metaBytes.AsSpan(CiaConstants.CiaMetaBaseSize + 0x08 + 5 * 0x200, 0x80));
                        string enShort = ReadUtf16ZeroTerminated(metaBytes.AsSpan(CiaConstants.CiaMetaBaseSize + 0x08 + 1 * 0x200, 0x80));
                        report.SmdhBannerTitle = !string.IsNullOrWhiteSpace(esShort) ? esShort : enShort;
                    }
                }
            }

            // 5. Inspect Content 0 (Main CXI) and classify encryption layers
            var mainChunk = chunks.FirstOrDefault(c => c.ContentIndex == 0 && c.IsPresentInCia);
            if (mainChunk == null)
            {
                report.EncryptionKind = CiaEncryptionKind.UnsupportedOrCorrupt;
                report.CanProcessNow = false;
                report.LimitationsAndWarnings.Add("El .cia no contiene el contenido ejecutable principal (ContentIndex 0).");
                return report;
            }

            Stream ciaLayerStream = new SubReadStream(fs, mainChunk.CiaFileOffset, mainChunk.SizeBytes, leaveOpen: true);
            Stream ncchRawStream = ciaLayerStream;
            bool ciaWasEncrypted = mainChunk.IsCiaLayerEncrypted;

            try
            {
                if (ciaWasEncrypted)
                {
                    report.EncryptionKind = CiaEncryptionKind.EncryptedCiaLayer;
                    if (report.IsTicketPersonalized)
                    {
                        report.CanProcessNow = false;
                        report.LimitationsAndWarnings.Add(
                            $"El Ticket está personalizado para una consola específica (ConsoleID=0x{report.TicketConsoleDeviceId:X8}). Debes exportarlo como 'Standard / Legit Decrypted CIA' con GodMode9 en tu 3DS.");
                        return report;
                    }

                    if (!keyStore.TryDecryptTicketTitleKey(commonKeyIdx, tikTitleId, encTitleKey, out byte[] decTitleKey))
                    {
                        report.CanProcessNow = false;
                        report.LimitationsAndWarnings.Add(
                            $"El contenedor .cia tiene cifrado externo AES-128-CBC (CommonKeyIndex={commonKeyIdx}). Para procesarlo directamente coloca tu 'aes_keys.txt' local (con common{commonKeyIdx} o slot0x3D) o descífralo en GodMode9 / Citra / Azahar.");
                        return report;
                    }

                    report.UsedExternalAesKeys = true;
                    ncchRawStream = new CiaAesCbcReadStream(ciaLayerStream, decTitleKey, mainChunk.ContentIndex, leaveOpen: true);
                }

                if (ncchRawStream.Length < 0x200)
                {
                    report.CanProcessNow = false;
                    report.LimitationsAndWarnings.Add("El contenido principal #0 es menor de 512 bytes.");
                    return report;
                }

                byte[] ncchHdr = new byte[0x200];
                ncchRawStream.Seek(0, SeekOrigin.Begin);
                ReadExact(ncchRawStream, ncchHdr);

                uint ncchMagic = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.AsSpan(0x100, 4));
                if (ncchMagic != CiaConstants.NcchMagic)
                {
                    report.CanProcessNow = false;
                    report.LimitationsAndWarnings.Add(
                        ciaWasEncrypted
                            ? "Tras descifrar la capa CIA no apareció la firma 'NCCH' (posible TitleKey corrupta o Ticket personalizado)."
                            : $"El contenido #0 no empieza con una partición 'NCCH' válida (magic=0x{ncchMagic:X8}).");
                    return report;
                }

                report.Ncch0HeaderReadable = true;
                ulong partId = BinaryPrimitives.ReadUInt64LittleEndian(ncchHdr.AsSpan(0x108, 8));
                ushort ncchVer = BinaryPrimitives.ReadUInt16LittleEndian(ncchHdr.AsSpan(0x112, 2));
                ulong progId = BinaryPrimitives.ReadUInt64LittleEndian(ncchHdr.AsSpan(0x118, 8));
                string prodCode = ReadAsciiZeroTerminated(ncchHdr.AsSpan(0x150, 0x10));
                byte flags3 = ncchHdr[0x18B];
                byte flags7 = ncchHdr[0x18F];

                bool noCrypto = (flags7 & 0x04) != 0;
                bool fixedKey = (flags7 & 0x01) != 0;
                bool seedCrypto = (flags7 & 0x20) != 0;

                report.NcchPartitionIdHex = $"{partId:X16}";
                report.NcchProgramIdHex = $"{progId:X16}";
                report.ProductCode = prodCode;
                report.DetectedEdition = TitleIdCatalog.DetectEdition(report.TmdTitleIdHex, prodCode);
                report.NcchFormatVersion = ncchVer;
                report.NcchSecondaryKeySlot = flags3;
                report.NcchNoCryptoFlag = noCrypto;
                report.NcchFixedKeyFlag = fixedKey;
                report.NcchSeedCryptoFlag = seedCrypto;

                if (!ciaWasEncrypted)
                {
                    if (noCrypto)
                        report.EncryptionKind = CiaEncryptionKind.PlainCiaPlainNcch;
                    else if (fixedKey)
                        report.EncryptionKind = CiaEncryptionKind.PlainCiaFixedKeyNcch;
                    else
                        report.EncryptionKind = CiaEncryptionKind.PlainCiaRetailEncryptedNcch;
                }

                if (!keyStore.TryResolveNcchKeys(ncchHdr, out byte[] primaryKey, out byte[] secondaryKey, out string keyFailure))
                {
                    report.CanProcessNow = false;
                    report.LimitationsAndWarnings.Add(keyFailure);
                    return report;
                }

                if (!noCrypto && !fixedKey)
                {
                    report.UsedExternalAesKeys = true;
                }

                using var plainNcch = new NcchDecryptedReadStream(ncchRawStream, primaryKey, secondaryKey, leaveOpen: true);

                // Check ExHeader remaster version
                uint exHdrDataSize = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.AsSpan(0x180, 4));
                if (exHdrDataSize >= 0x10 && plainNcch.Length >= 0x600)
                {
                    Span<byte> exHdrHead = stackalloc byte[0x10];
                    plainNcch.Seek(0x200, SeekOrigin.Begin);
                    ReadExact(plainNcch, exHdrHead);
                    report.ExHeaderRemasterVersion = BinaryPrimitives.ReadUInt16LittleEndian(exHdrHead.Slice(0x0E, 2));
                }

                long romfsOffset = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.AsSpan(0x1B0, 4)) * CiaConstants.NcchMediaUnitSize;
                long romfsSize = BinaryPrimitives.ReadUInt32LittleEndian(ncchHdr.AsSpan(0x1B4, 4)) * CiaConstants.NcchMediaUnitSize;
                if (romfsOffset <= 0 || romfsSize <= 0 || romfsOffset + romfsSize > plainNcch.Length)
                {
                    report.CanProcessNow = false;
                    report.LimitationsAndWarnings.Add("El contenido principal NCCH #0 no contiene una región RomFS válida.");
                    return report;
                }

                using var romfsSlice = new SubReadStream(plainNcch, romfsOffset, romfsSize, leaveOpen: true);
                var inv = IvfcRomFsReader.ReadInventory(romfsSlice);

                report.RomFsReadable = true;
                report.RomFsDirectoryCount = inv.Directories.Count;
                report.RomFsFileCount = inv.Files.Count;

                foreach (var f in inv.Files)
                {
                    if (f.RelativePath.Equals("ie_a.fa", StringComparison.OrdinalIgnoreCase))
                    {
                        report.HasIeAFa = true;
                        report.IeAFaSizeBytes = f.DataLength;
                    }
                    else if (f.RelativePath.Equals("fix.xr", StringComparison.OrdinalIgnoreCase))
                    {
                        report.HasFixXr = true;
                    }
                    else if (f.RelativePath.Equals("ctr/exsave/banner.icn", StringComparison.OrdinalIgnoreCase))
                    {
                        report.HasBannerIcn = true;
                    }

                    if (f.RelativePath.StartsWith("snd/", StringComparison.OrdinalIgnoreCase))
                        report.RomFsSoundFilesCount++;
                    if (f.RelativePath.StartsWith("movie/", StringComparison.OrdinalIgnoreCase))
                        report.RomFsMovieFilesCount++;
                }

                report.CanProcessNow = report.HasIeAFa && titleCategory == 0x00040000u;
                PopulateDiagnosticNotes(report);
                return report;
            }
            catch (Exception ex)
            {
                report.CanProcessNow = false;
                report.LimitationsAndWarnings.Add($"Error al inspeccionar RomFS interno: {ex.Message}");
                return report;
            }
            finally
            {
                if (!ReferenceEquals(ncchRawStream, ciaLayerStream))
                    ncchRawStream.Dispose();
                ciaLayerStream.Dispose();
            }
        }

        private static void PopulateDiagnosticNotes(CiaInspectionReport report)
        {
            if (report.IsBaseRevisionV000)
            {
                report.Findings.Add(
                    $"Revisión base confirmada: TMD TitleVersion = 0x{report.TmdTitleVersionRaw:X4} ({report.TmdTitleVersionFormatted}). Compatible al 100% con el esquema ARC0 de Inazuma Eleven GO.");
            }
            else
            {
                report.Findings.Add(
                    $"Revisión detectada en TMD: {report.TmdTitleVersionFormatted} (0x{report.TmdTitleVersionRaw:X4}).");
            }

            report.Findings.Add(
                $"Edición detectada: {report.DetectedEdition} | TitleID: {report.TmdTitleIdHex} | ProductCode: {report.ProductCode} | SaveDataSize: {report.SaveDataSizeBytes / 1024} KB.");

            report.Findings.Add(
                $"Inventario RomFS legible: {report.RomFsFileCount} archivos en {report.RomFsDirectoryCount} directorios (ie_a.fa={report.HasIeAFa}, fix.xr={report.HasFixXr}, snd={report.RomFsSoundFilesCount}, movie={report.RomFsMovieFilesCount}).");

            // Honest cryptographic & installability disclosure
            report.LimitationsAndWarnings.Add(
                "Consola real: Un .cia reconstruido invalida las firmas privadas RSA-2048 de Nintendo. Se instalará y ejecutará en consolas 3DS/2DS con Custom Firmware (Luma3DS + FBI/GodMode9), pero será rechazado en consolas 100% de fábrica sin CFW.");
            report.LimitationsAndWarnings.Add(
                "Emuladores (Citra / Azahar / Lime3DS): El .cia de salida se genera como 'Standard Decrypted Plain CIA' (sin requerir claves al instalar). Si usaste mods LayeredFS previos en 'load/mods/<TitleID>/', desactívalos para que no oculten el RomFS del nuevo .cia.");
        }

        /// <summary>
        /// Opens ANY content chunk <paramref name="chunk"/> inside an open <c>.cia</c> file stream as a seekable,
        /// fully plaintext NCCH stream (<c>NoCrypto = 0x04</c>), or raw decrypted stream if non-NCCH (e.g. SRL).
        /// </summary>
        public static Stream OpenDecryptedContentStream(
            Stream ciaFileStream,
            byte[] ticketBytes,
            CiaTmdChunkInfo chunk,
            CtrKeyStore keyStore,
            out bool isNcchContent)
        {
            isNcchContent = false;
            keyStore ??= CtrKeyStore.LoadAvailableLocalKeys();

            if (!TryParseTicket(ticketBytes, out _, out ulong tikTitleId, out uint consoleId, out byte commonKeyIdx, out byte[] encTitleKey))
                throw new InvalidDataException("Ticket inválido al abrir contenido del .cia.");

            Stream ciaSlice = new SubReadStream(ciaFileStream, chunk.CiaFileOffset, chunk.SizeBytes, leaveOpen: true);
            Stream afterCiaCrypto = ciaSlice;

            if (chunk.IsCiaLayerEncrypted)
            {
                if (consoleId != 0)
                {
                    ciaSlice.Dispose();
                    throw new InvalidOperationException(
                        $"El Ticket del .cia está personalizado para ConsoleID 0x{consoleId:X8} y no puede descifrarse sin la clave OTP privada de esa consola.");
                }

                if (!keyStore.TryDecryptTicketTitleKey(commonKeyIdx, tikTitleId, encTitleKey, out byte[] titleKey))
                {
                    ciaSlice.Dispose();
                    throw new InvalidOperationException(
                        $"Falta CommonKey #{commonKeyIdx} en aes_keys.txt para descifrar el contenido #{chunk.ContentIndex} del .cia.");
                }

                afterCiaCrypto = new CiaAesCbcReadStream(ciaSlice, titleKey, chunk.ContentIndex, leaveOpen: false);
            }

            if (afterCiaCrypto.Length < 0x200)
                return afterCiaCrypto;

            byte[] hdr = new byte[0x200];
            afterCiaCrypto.Seek(0, SeekOrigin.Begin);
            ReadExact(afterCiaCrypto, hdr);
            afterCiaCrypto.Seek(0, SeekOrigin.Begin);

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(0x100, 4));
            if (magic != CiaConstants.NcchMagic)
                return afterCiaCrypto;

            isNcchContent = true;
            if (!keyStore.TryResolveNcchKeys(hdr, out byte[] primaryKey, out byte[] secondaryKey, out string err))
            {
                afterCiaCrypto.Dispose();
                throw new InvalidOperationException(err);
            }

            return new NcchDecryptedReadStream(afterCiaCrypto, primaryKey, secondaryKey, leaveOpen: false);
        }

        internal static bool TryParseTicket(
            ReadOnlySpan<byte> tikBytes,
            out int bodyOffset,
            out ulong titleId,
            out uint consoleId,
            out byte commonKeyIndex,
            out byte[] encryptedTitleKey)
        {
            bodyOffset = 0;
            titleId = 0;
            consoleId = 0;
            commonKeyIndex = 0;
            encryptedTitleKey = Array.Empty<byte>();

            if (tikBytes.Length < 4)
                return false;

            uint sigType = BinaryPrimitives.ReadUInt32BigEndian(tikBytes.Slice(0, 4));
            int sigBlockSize = CiaConstants.GetSignatureBlockTotalSize(sigType);
            if (sigBlockSize <= 0 || tikBytes.Length < sigBlockSize + 0x164)
                return false;

            bodyOffset = sigBlockSize;
            encryptedTitleKey = tikBytes.Slice(bodyOffset + 0x7F, 0x10).ToArray();
            consoleId = BinaryPrimitives.ReadUInt32BigEndian(tikBytes.Slice(bodyOffset + 0x98, 4));
            titleId = BinaryPrimitives.ReadUInt64BigEndian(tikBytes.Slice(bodyOffset + 0x9C, 8));
            commonKeyIndex = tikBytes[bodyOffset + 0xB1];
            return true;
        }

        internal static bool TryParseTmd(
            ReadOnlySpan<byte> tmdBytes,
            ReadOnlySpan<byte> contentIndexMask,
            long contentSectionStartOffset,
            out int bodyOffset,
            out bool hashesValid,
            out List<CiaTmdChunkInfo> chunks)
        {
            bodyOffset = 0;
            hashesValid = false;
            chunks = new List<CiaTmdChunkInfo>();

            if (tmdBytes.Length < 4)
                return false;

            uint sigType = BinaryPrimitives.ReadUInt32BigEndian(tmdBytes.Slice(0, 4));
            int sigBlockSize = CiaConstants.GetSignatureBlockTotalSize(sigType);
            if (sigBlockSize <= 0 || tmdBytes.Length < sigBlockSize + 0x9C4)
                return false;

            bodyOffset = sigBlockSize;
            ushort contentCount = BinaryPrimitives.ReadUInt16BigEndian(tmdBytes.Slice(bodyOffset + 0x9E, 2));
            int chunksStart = bodyOffset + 0x9C4;
            if (tmdBytes.Length < chunksStart + contentCount * CiaConstants.TmdContentChunkRecordSize)
                return false;

            // Verify internal TMD ContentInfo SHA-256 cascade
            ReadOnlySpan<byte> expectedInfoHash = tmdBytes.Slice(bodyOffset + 0xA4, 0x20);
            ReadOnlySpan<byte> infoRecordsSpan = tmdBytes.Slice(bodyOffset + 0xC4, 0x900);
            Span<byte> actualInfoHash = stackalloc byte[32];
            SHA256.HashData(infoRecordsSpan, actualInfoHash);
            bool infoTopValid = actualInfoHash.SequenceEqual(expectedInfoHash);

            bool allSubGroupsValid = true;
            int chunkCursor = 0;
            for (int g = 0; g < CiaConstants.TmdContentInfoRecordCount; g++)
            {
                ReadOnlySpan<byte> infoRec = infoRecordsSpan.Slice(g * 0x24, 0x24);
                ushort cmdCount = BinaryPrimitives.ReadUInt16BigEndian(infoRec.Slice(2, 2));
                ReadOnlySpan<byte> recHash = infoRec.Slice(4, 0x20);
                if (cmdCount == 0)
                    continue;

                if (chunkCursor + cmdCount > contentCount)
                {
                    allSubGroupsValid = false;
                    break;
                }

                ReadOnlySpan<byte> coveredChunks = tmdBytes.Slice(
                    chunksStart + chunkCursor * CiaConstants.TmdContentChunkRecordSize,
                    cmdCount * CiaConstants.TmdContentChunkRecordSize);
                SHA256.HashData(coveredChunks, actualInfoHash);
                if (!actualInfoHash.SequenceEqual(recHash))
                    allSubGroupsValid = false;

                chunkCursor += cmdCount;
            }
            hashesValid = infoTopValid && allSubGroupsValid;

            bool maskHasAnyBit = false;
            for (int i = 0; i < contentIndexMask.Length; i++)
            {
                if (contentIndexMask[i] != 0)
                {
                    maskHasAnyBit = true;
                    break;
                }
            }

            long runningOffset = contentSectionStartOffset;
            for (int i = 0; i < contentCount; i++)
            {
                ReadOnlySpan<byte> rec = tmdBytes.Slice(
                    chunksStart + i * CiaConstants.TmdContentChunkRecordSize,
                    CiaConstants.TmdContentChunkRecordSize);

                uint cid = BinaryPrimitives.ReadUInt32BigEndian(rec.Slice(0x00, 4));
                ushort cidx = BinaryPrimitives.ReadUInt16BigEndian(rec.Slice(0x04, 2));
                ushort flags = BinaryPrimitives.ReadUInt16BigEndian(rec.Slice(0x06, 2));
                ulong csize = BinaryPrimitives.ReadUInt64BigEndian(rec.Slice(0x08, 8));
                string shaHex = Convert.ToHexString(rec.Slice(0x10, 0x20));

                bool present = !maskHasAnyBit || IsContentBitSet(contentIndexMask, cidx);
                long fileOff = present ? runningOffset : -1;
                if (present)
                {
                    runningOffset += (long)csize;
                }

                chunks.Add(new CiaTmdChunkInfo
                {
                    ContentId = cid,
                    ContentIndex = cidx,
                    Flags = flags,
                    SizeBytes = (long)csize,
                    Sha256Hex = shaHex,
                    CiaFileOffset = fileOff,
                    IsPresentInCia = present
                });
            }

            return true;
        }

        public static bool IsContentBitSet(ReadOnlySpan<byte> mask8192, ushort contentIndex)
        {
            int byteIdx = contentIndex >> 3;
            int bitMask = 0x80 >> (contentIndex & 7);
            return byteIdx < mask8192.Length && (mask8192[byteIdx] & bitMask) != 0;
        }

        public static void SetContentBit(Span<byte> mask8192, ushort contentIndex)
        {
            int byteIdx = contentIndex >> 3;
            int bitMask = 0x80 >> (contentIndex & 7);
            if (byteIdx < mask8192.Length)
                mask8192[byteIdx] |= (byte)bitMask;
        }

        public static string ComputeStreamingFileSha256Hex(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buf = new byte[131072];
            int read;
            while ((read = fs.Read(buf, 0, buf.Length)) > 0)
            {
                sha.AppendData(buf, 0, read);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }

        internal static byte[] ReadBytesAt(Stream s, long offset, int length)
        {
            byte[] buf = new byte[length];
            s.Seek(offset, SeekOrigin.Begin);
            ReadExact(s, buf);
            return buf;
        }

        internal static void ReadExact(Stream s, Span<byte> buf)
        {
            int done = 0;
            while (done < buf.Length)
            {
                int r = s.Read(buf.Slice(done));
                if (r <= 0)
                    throw new EndOfStreamException("Fin inesperado al leer archivo .cia.");
                done += r;
            }
        }

        private static string ReadAsciiZeroTerminated(ReadOnlySpan<byte> bytes)
        {
            int end = bytes.IndexOf((byte)0);
            if (end < 0)
                end = bytes.Length;
            return Encoding.ASCII.GetString(bytes.Slice(0, end)).Trim();
        }

        private static string ReadUtf16ZeroTerminated(ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i + 1 < bytes.Length; i += 2)
            {
                if (bytes[i] == 0 && bytes[i + 1] == 0)
                    return Encoding.Unicode.GetString(bytes.Slice(0, i)).Trim();
            }
            return Encoding.Unicode.GetString(bytes).Trim();
        }
    }
}
