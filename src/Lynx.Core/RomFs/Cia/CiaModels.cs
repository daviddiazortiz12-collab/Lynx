using System;
using System.Collections.Generic;

namespace Lynx.Core.RomFs.Cia
{
    public enum CiaEncryptionKind
    {
        PlainCiaPlainNcch = 0,
        PlainCiaFixedKeyNcch = 1,
        PlainCiaRetailEncryptedNcch = 2,
        EncryptedCiaLayer = 3,
        UnsupportedOrCorrupt = 4
    }

    public static class CiaConstants
    {
        public const uint CiaArchiveHeaderSize = 0x2020;
        public const long CiaSectionAlignment = 0x40;
        public const long CiaContentAlignment = 0x10;
        public const long NcchMediaUnitSize = 0x200;
        public const long IvfcBlockSize = 0x1000;
        public const int IvfcBlockSizeLog2 = 12;
        public const long RomFsFileDataAlignment = 0x10;
        public const uint IvfcMagic = 0x43465649; // "IVFC"
        public const uint IvfcRomFsId = 0x00010000;
        public const uint IvfcHeaderSize = 0x5C;
        public const uint IvfcPaddedHeaderSize = 0x60;
        public const uint RomFsLevel3HeaderSize = 0x28;
        public const uint NcchMagic = 0x4843434E; // "NCCH"
        public const uint SmdhMagic = 0x48444D53; // "SMDH"

        public const uint SigTypeRsa4096Sha1 = 0x00010000;
        public const uint SigTypeRsa2048Sha1 = 0x00010001;
        public const uint SigTypeEccSha1 = 0x00010002;
        public const uint SigTypeRsa4096Sha256 = 0x00010003;
        public const uint SigTypeRsa2048Sha256 = 0x00010004;
        public const uint SigTypeEccSha256 = 0x00010005;

        public const ushort TmdContentFlagEncrypted = 0x0001;
        public const ushort TmdContentFlagOptional = 0x4000;
        public const ushort TmdContentFlagShared = 0x8000;

        public const int TmdHeaderBodySize = 0xC4;
        public const int TmdContentInfoRecordSize = 0x24;
        public const int TmdContentInfoRecordCount = 64;
        public const int TmdContentChunkRecordSize = 0x30;

        public const int CiaMetaBaseSize = 0x400;
        public const int SmdhByteSize = 0x36C0;
        public const int StandardCiaMetaSize = CiaMetaBaseSize + SmdhByteSize; // 0x3AC0

        /// <summary>
        /// Real 3DS hardware FAT32 per-content limit enforced by Service::AM (4 GiB - 1).
        /// </summary>
        public const long MaxFat32ContentSizeBytes = 0xFFFFFFFFL;

        public static int GetSignatureBlockTotalSize(uint sigType)
        {
            return sigType switch
            {
                SigTypeRsa4096Sha1 or SigTypeRsa4096Sha256 => 0x240,
                SigTypeRsa2048Sha1 or SigTypeRsa2048Sha256 => 0x140,
                SigTypeEccSha1 or SigTypeEccSha256 => 0x80,
                _ => 0
            };
        }

        public static long AlignUp(long value, long alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        public static string FormatTitleVersion(ushort rawVersion)
        {
            int major = (rawVersion >> 10) & 0x3F;
            int minor = (rawVersion >> 4) & 0x3F;
            int micro = rawVersion & 0x0F;
            return $"V.{major}.{minor}.{micro}";
        }
    }

    public sealed class CiaTmdChunkInfo
    {
        public uint ContentId { get; init; }
        public ushort ContentIndex { get; init; }
        public ushort Flags { get; init; }
        public long SizeBytes { get; init; }
        public string Sha256Hex { get; init; } = string.Empty;
        public long CiaFileOffset { get; init; } = -1;
        public bool IsPresentInCia { get; init; }
        public bool IsCiaLayerEncrypted => (Flags & CiaConstants.TmdContentFlagEncrypted) != 0;
        public string RoleDescription => ContentIndex switch
        {
            0 => "Ejecutable principal del juego (CXI)",
            1 => "Manual electrónico (CFA)",
            2 => "Modo Descarga / Download Play (CFA)",
            _ => $"Contenido secundario #{ContentIndex}"
        };
    }

    public sealed class CiaInspectionReport
    {
        public string FilePath { get; init; } = string.Empty;
        public long FileSizeBytes { get; init; }
        public string Sha256Hex { get; set; } = string.Empty;
        public bool IsValidCiaStructure { get; set; }
        public CiaEncryptionKind EncryptionKind { get; set; } = CiaEncryptionKind.UnsupportedOrCorrupt;
        public bool CanProcessNow { get; set; }
        public bool UsedExternalAesKeys { get; set; }
        public string AesKeysSourcePath { get; set; } = string.Empty;

        // CIA Section layout
        public uint HeaderSize { get; set; }
        public ushort CiaType { get; set; }
        public ushort CiaFormatVersion { get; set; }
        public long CertChainOffset { get; set; }
        public uint CertChainSize { get; set; }
        public long TicketOffset { get; set; }
        public uint TicketSize { get; set; }
        public long TmdOffset { get; set; }
        public uint TmdSize { get; set; }
        public long ContentOffset { get; set; }
        public long ContentSize { get; set; }
        public long MetaOffset { get; set; }
        public uint MetaSize { get; set; }

        // Ticket & TMD metadata
        public string TicketIssuer { get; set; } = string.Empty;
        public string TicketTitleIdHex { get; set; } = string.Empty;
        public byte TicketCommonKeyIndex { get; set; }
        public uint TicketConsoleDeviceId { get; set; }
        public bool IsTicketPersonalized => TicketConsoleDeviceId != 0;

        public string TmdIssuer { get; set; } = string.Empty;
        public string TmdTitleIdHex { get; set; } = string.Empty;
        public ushort TmdTitleVersionRaw { get; set; }
        public string TmdTitleVersionFormatted { get; set; } = string.Empty;
        public bool IsBaseRevisionV000 => TmdTitleVersionRaw == 0;
        public uint SaveDataSizeBytes { get; set; }
        public ushort ContentCount { get; set; }
        public bool TmdInternalHashesValid { get; set; }
        public List<CiaTmdChunkInfo> ContentChunks { get; init; } = new();

        // Meta / SMDH
        public uint MetaCoreVersion { get; set; }
        public string SmdhBannerTitle { get; set; } = string.Empty;

        // Content 0 (CXI / NCCH) & RomFS
        public bool Ncch0HeaderReadable { get; set; }
        public string NcchProgramIdHex { get; set; } = string.Empty;
        public string NcchPartitionIdHex { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public GameEdition DetectedEdition { get; set; } = GameEdition.Auto;
        public ushort NcchFormatVersion { get; set; }
        public byte NcchSecondaryKeySlot { get; set; }
        public bool NcchNoCryptoFlag { get; set; }
        public bool NcchFixedKeyFlag { get; set; }
        public bool NcchSeedCryptoFlag { get; set; }
        public ushort ExHeaderRemasterVersion { get; set; }

        public bool RomFsReadable { get; set; }
        public int RomFsDirectoryCount { get; set; }
        public int RomFsFileCount { get; set; }
        public bool HasIeAFa { get; set; }
        public long IeAFaSizeBytes { get; set; }
        public bool HasFixXr { get; set; }
        public bool HasBannerIcn { get; set; }
        public int RomFsSoundFilesCount { get; set; }
        public int RomFsMovieFilesCount { get; set; }

        // Honest console / emulator compatibility & diagnostic notes
        public List<string> Findings { get; init; } = new();
        public List<string> LimitationsAndWarnings { get; init; } = new();
    }

    public sealed class RomFsDirectoryEntryInfo
    {
        public string RelativePath { get; init; } = string.Empty; // "" for root, "snd/product" for nested
        public string Name { get; init; } = string.Empty;
    }

    public sealed class RomFsFileEntryInfo
    {
        public string RelativePath { get; init; } = string.Empty; // e.g. "ie_a.fa" or "movie/ed00s_c.moflex"
        public string Name { get; init; } = string.Empty;
        public long DataOffsetInRomFs { get; init; }
        public long DataLength { get; init; }
    }

    public sealed class RomFsInventory
    {
        public long RomFsTotalLength { get; init; }
        public long Level3Offset { get; init; }
        public long FileDataBaseOffsetInRomFs { get; init; }
        public IReadOnlyList<RomFsDirectoryEntryInfo> Directories { get; init; } = Array.Empty<RomFsDirectoryEntryInfo>();
        public IReadOnlyList<RomFsFileEntryInfo> Files { get; init; } = Array.Empty<RomFsFileEntryInfo>();
    }

    public sealed class CiaRebuildTelemetry
    {
        public string SourceCiaPath { get; init; } = string.Empty;
        public string OutputCiaPath { get; init; } = string.Empty;
        public long SourceCiaSizeBytes { get; init; }
        public long OutputCiaSizeBytes { get; init; }
        public string SourceCiaSha256Hex { get; init; } = string.Empty;
        public string OutputCiaSha256Hex { get; init; } = string.Empty;
        public string TitleIdHex { get; init; } = string.Empty;
        public string TitleVersionFormatted { get; init; } = string.Empty;
        public uint PreservedSaveDataSizeBytes { get; init; }
        public int PreservedContentsCount { get; init; }
        public int SourceRomFsFileCount { get; init; }
        public int OutputRomFsFileCount { get; init; }
        public int OverlaidModifiedRomFsFilesCount { get; init; }
        public List<string> OverlaidRelativePaths { get; init; } = new();
    }

    public sealed class CiaVerificationResult
    {
        public bool IsValid { get; set; }
        public int VerifiedContentChunksCount { get; set; }
        public int VerifiedRomFsDirectoriesCount { get; set; }
        public int VerifiedRomFsFilesCount { get; set; }
        public int VerifiedIvfcBlocksCount { get; set; }
        public List<string> ChecksPassed { get; init; } = new();
        public List<string> Errors { get; init; } = new();
    }
}
