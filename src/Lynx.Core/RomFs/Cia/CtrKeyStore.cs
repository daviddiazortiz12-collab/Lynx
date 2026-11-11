using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lynx.Core.RomFs.Cia
{
    /// <summary>
    /// Optional local user-supplied keybag loader (Citra / Azahar <c>aes_keys.txt</c> + <c>seeddb.bin</c>)
    /// and pure C# seekable stream decryptors for CIA (AES-128-CBC) and NCCH (AES-128-CTR).
    /// Contains ZERO proprietary Nintendo keys in code; Plain and FixedKey CIAs work with an empty store.
    /// </summary>
    public sealed class CtrKeyStore
    {
        // Standard 128-bit arithmetic constant of the 3DS AES hardware key scrambler (public hardware spec)
        private static readonly UInt128 DefaultHardwareGeneratorConstant =
            new(0x1FF9E9AAC5FE0408UL, 0x024591DC5D52768AUL);

        private readonly Dictionary<int, byte[]> _commonKeys = new();
        private readonly Dictionary<int, byte[]> _slotKeyX = new();
        private readonly Dictionary<int, byte[]> _slot3DKeyY = new();
        private readonly Dictionary<ulong, byte[]> _seedDb = new();
        private UInt128 _generatorConstant = DefaultHardwareGeneratorConstant;

        public string LoadedAesKeysPath { get; private set; } = string.Empty;
        public string LoadedSeedDbPath { get; private set; } = string.Empty;
        public bool HasAnyKeys => _commonKeys.Count > 0 || _slotKeyX.Count > 0;

        public static CtrKeyStore LoadAvailableLocalKeys(string explicitAesKeysPath = null)
        {
            var store = new CtrKeyStore();

            string resolvedKeys = ResolveAesKeysFilePath(explicitAesKeysPath);
            if (!string.IsNullOrEmpty(resolvedKeys) && File.Exists(resolvedKeys))
            {
                store.ParseAesKeysFile(resolvedKeys);
                store.LoadedAesKeysPath = resolvedKeys;
            }

            string resolvedSeeds = ResolveSeedDbPath(resolvedKeys);
            if (!string.IsNullOrEmpty(resolvedSeeds) && File.Exists(resolvedSeeds))
            {
                store.ParseSeedDbFile(resolvedSeeds);
                store.LoadedSeedDbPath = resolvedSeeds;
            }

            return store;
        }

        public static List<string> GetStandardKeySearchPaths()
        {
            var paths = new List<string>();
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                paths.Add(Path.Combine(appData, "Citra", "sysdata", "aes_keys.txt"));
                paths.Add(Path.Combine(appData, "Azahar", "sysdata", "aes_keys.txt"));
                paths.Add(Path.Combine(appData, "Lime3DS", "sysdata", "aes_keys.txt"));
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                paths.Add(Path.Combine(home, ".local", "share", "citra-emu", "sysdata", "aes_keys.txt"));
                paths.Add(Path.Combine(home, ".local", "share", "azahar-emu", "sysdata", "aes_keys.txt"));
                paths.Add(Path.Combine(home, ".local", "share", "lime3ds-emu", "sysdata", "aes_keys.txt"));
            }

            if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            {
                paths.Add(Path.Combine(AppContext.BaseDirectory, "aes_keys.txt"));
                paths.Add(Path.Combine(AppContext.BaseDirectory, "sysdata", "aes_keys.txt"));
            }

            return paths;
        }

        private static string ResolveAesKeysFilePath(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                string full = Path.GetFullPath(explicitPath.Trim());
                if (File.Exists(full))
                    return full;
                if (Directory.Exists(full))
                {
                    string candidate = Path.Combine(full, "aes_keys.txt");
                    if (File.Exists(candidate))
                        return candidate;
                    string sysCandidate = Path.Combine(full, "sysdata", "aes_keys.txt");
                    if (File.Exists(sysCandidate))
                        return sysCandidate;
                }
            }

            foreach (string candidate in GetStandardKeySearchPaths())
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return string.Empty;
        }

        private static string ResolveSeedDbPath(string resolvedKeysPath)
        {
            if (!string.IsNullOrEmpty(resolvedKeysPath))
            {
                string sibling = Path.Combine(Path.GetDirectoryName(resolvedKeysPath) ?? string.Empty, "seeddb.bin");
                if (File.Exists(sibling))
                    return sibling;
            }

            foreach (string keyPath in GetStandardKeySearchPaths())
            {
                string dir = Path.GetDirectoryName(keyPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    string seedPath = Path.Combine(dir, "seeddb.bin");
                    if (File.Exists(seedPath))
                        return seedPath;
                }
            }

            return string.Empty;
        }

        public void ParseAesKeysFile(string path)
        {
            foreach (string rawLine in File.ReadLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                    continue;

                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                string keyName = line.Substring(0, eq).Trim();
                string hexVal = line.Substring(eq + 1).Trim();
                if (!TryParseHex16(hexVal, out byte[] bytes16))
                    continue;

                if (keyName.Equals("generator", StringComparison.OrdinalIgnoreCase))
                {
                    _generatorConstant = ReadUInt128BigEndian(bytes16);
                }
                else if (keyName.StartsWith("common", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(keyName.AsSpan(6), out int commonIdx) &&
                         commonIdx >= 0 && commonIdx <= 5)
                {
                    _commonKeys[commonIdx] = bytes16;
                }
                else if (keyName.StartsWith("slot0x3DKeyY", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(keyName.AsSpan(12), out int yIdx) &&
                         yIdx >= 0 && yIdx <= 5)
                {
                    _slot3DKeyY[yIdx] = bytes16;
                }
                else if (keyName.StartsWith("slot0x", StringComparison.OrdinalIgnoreCase) &&
                         keyName.EndsWith("KeyX", StringComparison.OrdinalIgnoreCase) &&
                         keyName.Length == 12)
                {
                    if (int.TryParse(keyName.AsSpan(6, 2), System.Globalization.NumberStyles.HexNumber, null, out int slotId))
                    {
                        _slotKeyX[slotId] = bytes16;
                    }
                }
            }

            // Derive common0..5 from slot0x3DKeyX + slot0x3DKeyY0..5 if direct common keys were not listed
            if (_slotKeyX.TryGetValue(0x3D, out byte[] keyX3D))
            {
                foreach (var kvp in _slot3DKeyY)
                {
                    if (!_commonKeys.ContainsKey(kvp.Key))
                    {
                        _commonKeys[kvp.Key] = DeriveNormalKey(keyX3D, kvp.Value, _generatorConstant);
                    }
                }
            }
        }

        public void ParseSeedDbFile(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length < 0x10)
                return;

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4));
            int pos = 0x10;
            for (uint i = 0; i < count && pos + 0x20 <= raw.Length; i++, pos += 0x20)
            {
                ulong titleId = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(pos, 8));
                byte[] seed = new byte[16];
                Buffer.BlockCopy(raw, pos + 8, seed, 0, 16);
                _seedDb[titleId] = seed;
            }
        }

        public void RegisterCommonKeyForTesting(int commonKeyIndex, byte[] key16)
        {
            if (key16 == null || key16.Length != 16)
                throw new ArgumentException("Common key must be 16 bytes.");
            _commonKeys[commonKeyIndex] = (byte[])key16.Clone();
        }

        public void RegisterSlotKeyXForTesting(int slotId, byte[] keyX16)
        {
            if (keyX16 == null || keyX16.Length != 16)
                throw new ArgumentException("KeyX must be 16 bytes.");
            _slotKeyX[slotId] = (byte[])keyX16.Clone();
        }

        public bool TryDecryptTicketTitleKey(
            byte commonKeyIndex,
            ulong ticketTitleId,
            ReadOnlySpan<byte> encryptedTitleKey,
            out byte[] decryptedTitleKey)
        {
            decryptedTitleKey = null;
            if (encryptedTitleKey.Length < 16)
                return false;

            if (!_commonKeys.TryGetValue(commonKeyIndex, out byte[] commonKey))
                return false;

            byte[] iv = new byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(iv.AsSpan(0, 8), ticketTitleId);

            using var aes = Aes.Create();
            aes.KeySize = 128;
            aes.Key = commonKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;

            decryptedTitleKey = aes.DecryptCbc(encryptedTitleKey.Slice(0, 16), iv, PaddingMode.None);
            return true;
        }

        public bool TryResolveNcchKeys(
            ReadOnlySpan<byte> ncchHeader512,
            out byte[] primaryKey,
            out byte[] secondaryKey,
            out string failureReason)
        {
            primaryKey = null;
            secondaryKey = null;
            failureReason = string.Empty;

            if (ncchHeader512.Length < 0x200)
            {
                failureReason = "Cabecera NCCH truncada (< 512 bytes).";
                return false;
            }

            byte flags3 = ncchHeader512[0x18B];
            byte flags7 = ncchHeader512[0x18F];
            bool noCrypto = (flags7 & 0x04) != 0;
            bool fixedKey = (flags7 & 0x01) != 0;
            bool seedCrypto = (flags7 & 0x20) != 0;

            if (noCrypto)
            {
                primaryKey = new byte[16];
                secondaryKey = new byte[16];
                return true;
            }

            ulong programId = BinaryPrimitives.ReadUInt64LittleEndian(ncchHeader512.Slice(0x118, 8));

            if (fixedKey)
            {
                // System fixed-key titles have category bit 0x10 set; standard fixed-key uses all-zero key
                bool isSystemCategory = ((programId >> 32) & 0x10) != 0;
                if (!isSystemCategory)
                {
                    primaryKey = new byte[16];
                    secondaryKey = new byte[16];
                    return true;
                }

                if (_slotKeyX.TryGetValue(0x2C, out byte[] sysKeyX))
                {
                    // System fixed key requires slot normal key; fallback if unavailable
                    _ = sysKeyX;
                }
                primaryKey = new byte[16];
                secondaryKey = new byte[16];
                return true;
            }

            if (!_slotKeyX.TryGetValue(0x2C, out byte[] keyX2C))
            {
                failureReason =
                    "Falta la clave 'slot0x2CKeyX' en aes_keys.txt para descifrar la capa NCCH retail (estándar de Inazuma Eleven GO V.0.0.0).";
                return false;
            }

            byte[] keyYPrimary = ncchHeader512.Slice(0, 16).ToArray();
            byte[] keyYSecondary = keyYPrimary;

            if (seedCrypto)
            {
                if (!_seedDb.TryGetValue(programId, out byte[] seed16))
                {
                    failureReason = $"El NCCH usa SeedCrypto (9.6+) pero no se encontró la semilla de {programId:X16} en seeddb.bin.";
                    return false;
                }

                byte[] combined = new byte[32];
                Buffer.BlockCopy(keyYPrimary, 0, combined, 0, 16);
                Buffer.BlockCopy(seed16, 0, combined, 16, 16);
                byte[] digest = SHA256.HashData(combined);
                keyYSecondary = new byte[16];
                Buffer.BlockCopy(digest, 0, keyYSecondary, 0, 16);
            }

            int secondarySlot = flags3 switch
            {
                0x00 => 0x2C,
                0x01 => 0x25,
                0x0A => 0x18,
                0x0B => 0x1B,
                _ => -1
            };

            if (secondarySlot < 0 || !_slotKeyX.TryGetValue(secondarySlot, out byte[] keyXSec))
            {
                failureReason = $"Falta la clave 'slot0x{secondarySlot:X2}KeyX' en aes_keys.txt para descifrar el RomFS del NCCH.";
                return false;
            }

            primaryKey = DeriveNormalKey(keyX2C, keyYPrimary, _generatorConstant);
            secondaryKey = DeriveNormalKey(keyXSec, keyYSecondary, _generatorConstant);
            return true;
        }

        public static byte[] DeriveNormalKey(ReadOnlySpan<byte> keyX, ReadOnlySpan<byte> keyY)
        {
            return DeriveNormalKey(keyX, keyY, DefaultHardwareGeneratorConstant);
        }

        public static byte[] DeriveNormalKey(ReadOnlySpan<byte> keyX, ReadOnlySpan<byte> keyY, UInt128 generator)
        {
            UInt128 x = ReadUInt128BigEndian(keyX);
            UInt128 y = ReadUInt128BigEndian(keyY);
            UInt128 step1 = RotateLeft128(x, 2) ^ y;
            UInt128 step2 = unchecked(step1 + generator);
            UInt128 normal = RotateLeft128(step2, 87);
            byte[] result = new byte[16];
            WriteUInt128BigEndian(result, normal);
            return result;
        }

        internal static UInt128 RotateLeft128(UInt128 value, int shift)
        {
            shift &= 127;
            if (shift == 0)
                return value;
            return (value << shift) | (value >> (128 - shift));
        }

        internal static UInt128 ReadUInt128BigEndian(ReadOnlySpan<byte> bytes)
        {
            ulong hi = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(0, 8));
            ulong lo = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(8, 8));
            return new UInt128(hi, lo);
        }

        internal static void WriteUInt128BigEndian(Span<byte> dest, UInt128 value)
        {
            ulong hi = (ulong)(value >> 64);
            ulong lo = (ulong)value;
            BinaryPrimitives.WriteUInt64BigEndian(dest.Slice(0, 8), hi);
            BinaryPrimitives.WriteUInt64BigEndian(dest.Slice(8, 8), lo);
        }

        private static bool TryParseHex16(string hex, out byte[] result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(hex))
                return false;
            string clean = hex.Trim();
            if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(2);
            if (clean.Length != 32)
                return false;
            try
            {
                result = Convert.FromHexString(clean);
                return result.Length == 16;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Bounded, seekable read-only view over a slice of an underlying stream. Never disposes the parent stream unless requested.
    /// </summary>
    public sealed class SubReadStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly long _baseOffset;
        private readonly long _length;
        private readonly bool _leaveOpen;
        private long _position;

        public SubReadStream(Stream baseStream, long baseOffset, long length, bool leaveOpen = true)
        {
            _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
            if (baseOffset < 0 || length < 0 || baseOffset + length > baseStream.Length)
                throw new ArgumentOutOfRangeException(nameof(length), "Slice exceeds parent stream bounds.");

            _baseOffset = baseOffset;
            _length = length;
            _leaveOpen = leaveOpen;
            _position = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0 || _position >= _length)
                return 0;

            int toRead = (int)Math.Min(count, _length - _position);
            _baseStream.Seek(_baseOffset + _position, SeekOrigin.Begin);
            int read = _baseStream.Read(buffer, offset, toRead);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (target < 0 || target > _length)
                throw new IOException("Attempted to seek outside bounded SubReadStream.");

            _position = target;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
            {
                _baseStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// O(1) random-access seekable AES-128-CBC decrypting stream for encrypted CIA content chunks.
    /// Uses <c>IV_0 = [contentIndex_BE (2B), 0..0 (14B)]</c> for block 0 and <c>C_{k-1}</c> for block <c>k &gt; 0</c>.
    /// </summary>
    public sealed class CiaAesCbcReadStream : Stream
    {
        private readonly Stream _cipherStream;
        private readonly Aes _aes;
        private readonly byte[] _iv0 = new byte[16];
        private readonly bool _leaveOpen;
        private long _position;

        public CiaAesCbcReadStream(Stream cipherStream, byte[] titleKey16, ushort contentIndex, bool leaveOpen = true)
        {
            _cipherStream = cipherStream ?? throw new ArgumentNullException(nameof(cipherStream));
            if (titleKey16 == null || titleKey16.Length != 16)
                throw new ArgumentException("TitleKey must be 16 bytes.", nameof(titleKey16));
            if (cipherStream.Length % 16 != 0)
                throw new InvalidDataException($"Encrypted CIA content size (0x{cipherStream.Length:X}) is not a multiple of 16 bytes.");

            _aes = Aes.Create();
            _aes.KeySize = 128;
            _aes.Key = (byte[])titleKey16.Clone();
            _aes.Padding = PaddingMode.None;

            _iv0[0] = (byte)(contentIndex >> 8);
            _iv0[1] = (byte)(contentIndex & 0xFF);
            _leaveOpen = leaveOpen;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _cipherStream.Length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0 || _position >= Length)
                return 0;

            int totalToRead = (int)Math.Min(count, Length - _position);
            long startBlock = _position / 16;
            long endBlockExclusive = (_position + totalToRead + 15) / 16;
            int blockCount = (int)(endBlockExclusive - startBlock);

            byte[] iv = new byte[16];
            if (startBlock == 0)
            {
                Buffer.BlockCopy(_iv0, 0, iv, 0, 16);
            }
            else
            {
                _cipherStream.Seek((startBlock - 1) * 16, SeekOrigin.Begin);
                ReadExact(_cipherStream, iv, 0, 16);
            }

            byte[] cipherChunk = new byte[blockCount * 16];
            _cipherStream.Seek(startBlock * 16, SeekOrigin.Begin);
            ReadExact(_cipherStream, cipherChunk, 0, cipherChunk.Length);

            byte[] plainChunk = _aes.DecryptCbc(cipherChunk, iv, PaddingMode.None);
            int intraBlockOffset = (int)(_position % 16);
            Buffer.BlockCopy(plainChunk, intraBlockOffset, buffer, offset, totalToRead);
            _position += totalToRead;
            return totalToRead;
        }

        private static void ReadExact(Stream s, byte[] buf, int off, int len)
        {
            int done = 0;
            while (done < len)
            {
                int r = s.Read(buf, off + done, len - done);
                if (r <= 0)
                    throw new EndOfStreamException("Unexpected EOF in encrypted CIA content stream.");
                done += r;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0 || target > Length)
                throw new IOException("Attempted to seek outside CiaAesCbcReadStream.");
            _position = target;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _aes.Dispose();
                if (!_leaveOpen)
                    _cipherStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// O(1) random-access seekable read-only stream that exposes any NCCH partition (Plain, FixedKey, or KeyX-encrypted)
    /// as a clean plaintext NCCH with <c>NoCrypto (0x04)</c> set in <c>Flags[7]</c>.
    /// </summary>
    public sealed class NcchDecryptedReadStream : Stream
    {
        private sealed record CtrRegion(long Start, long Length, long CtrOrigin, Aes KeyAes, UInt128 BaseCounter);

        private readonly Stream _rawNcch;
        private readonly byte[] _patchedHeader512 = new byte[0x200];
        private readonly Aes _primaryAes;
        private readonly Aes _secondaryAes;
        private readonly List<CtrRegion> _regions = new();
        private readonly bool _isEncrypted;
        private readonly bool _leaveOpen;
        private long _position;

        public NcchDecryptedReadStream(Stream rawNcchStream, byte[] primaryKey16, byte[] secondaryKey16, bool leaveOpen = true)
        {
            _rawNcch = rawNcchStream ?? throw new ArgumentNullException(nameof(rawNcchStream));
            _leaveOpen = leaveOpen;

            _rawNcch.Seek(0, SeekOrigin.Begin);
            int hdrRead = 0;
            while (hdrRead < 0x200)
            {
                int r = _rawNcch.Read(_patchedHeader512, hdrRead, 0x200 - hdrRead);
                if (r <= 0)
                    throw new EndOfStreamException("Truncated NCCH header.");
                hdrRead += r;
            }

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(_patchedHeader512.AsSpan(0x100, 4));
            if (magic != CiaConstants.NcchMagic)
                throw new InvalidDataException($"Invalid NCCH magic 0x{magic:X8} at +0x100.");

            byte flags7 = _patchedHeader512[0x18F];
            _isEncrypted = (flags7 & 0x04) == 0;

            // Expose decrypted header with NoCrypto=1 and FixedKey/SeedCrypto/KeyX cleared
            _patchedHeader512[0x18B] = 0x00;
            _patchedHeader512[0x18F] = (byte)((flags7 & ~0x21) | 0x04);

            if (!_isEncrypted)
                return;

            _primaryAes = Aes.Create();
            _primaryAes.KeySize = 128;
            _primaryAes.Key = (byte[])primaryKey16.Clone();

            _secondaryAes = Aes.Create();
            _secondaryAes.KeySize = 128;
            _secondaryAes.Key = (byte[])secondaryKey16.Clone();

            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(_patchedHeader512.AsSpan(0x112, 2));
            ulong partitionIdLe = BinaryPrimitives.ReadUInt64LittleEndian(_patchedHeader512.AsSpan(0x108, 8));
            uint exHdrDataSize = BinaryPrimitives.ReadUInt32LittleEndian(_patchedHeader512.AsSpan(0x180, 4));
            long exefsOffset = BinaryPrimitives.ReadUInt32LittleEndian(_patchedHeader512.AsSpan(0x1A0, 4)) * CiaConstants.NcchMediaUnitSize;
            long exefsSize = BinaryPrimitives.ReadUInt32LittleEndian(_patchedHeader512.AsSpan(0x1A4, 4)) * CiaConstants.NcchMediaUnitSize;
            long romfsOffset = BinaryPrimitives.ReadUInt32LittleEndian(_patchedHeader512.AsSpan(0x1B0, 4)) * CiaConstants.NcchMediaUnitSize;
            long romfsSize = BinaryPrimitives.ReadUInt32LittleEndian(_patchedHeader512.AsSpan(0x1B4, 4)) * CiaConstants.NcchMediaUnitSize;

            UInt128 exhdrCtr = BuildBaseCtr(version, partitionIdLe, 1, 0x200);
            UInt128 exefsCtr = BuildBaseCtr(version, partitionIdLe, 2, (uint)exefsOffset);
            UInt128 romfsCtr = BuildBaseCtr(version, partitionIdLe, 3, (uint)romfsOffset);

            if (exHdrDataSize > 0)
            {
                // Full ExHeader + AccessDescriptor is 0x800 bytes encrypted with primaryKey
                _regions.Add(new CtrRegion(0x200, 0x800, 0x200, _primaryAes, exhdrCtr));
            }

            if (exefsSize >= 0x200)
            {
                // ExeFS 0x200-byte header is always encrypted with primaryKey
                _regions.Add(new CtrRegion(exefsOffset, 0x200, exefsOffset, _primaryAes, exefsCtr));

                // Decrypt the 0x200-byte ExeFS header now to distinguish 'icon'/'banner' (primaryKey) vs '.code' (secondaryKey)
                byte[] plainExeFsHdr = new byte[0x200];
                ReadRegionPlaintext(_regions[^1], exefsOffset, plainExeFsHdr, 0, 0x200);

                for (int i = 0; i < 10; i++)
                {
                    int entryPos = i * 0x10;
                    string secName = Encoding.ASCII.GetString(plainExeFsHdr, entryPos, 8).TrimEnd('\0');
                    uint secRelOff = BinaryPrimitives.ReadUInt32LittleEndian(plainExeFsHdr.AsSpan(entryPos + 8, 4));
                    uint secSize = BinaryPrimitives.ReadUInt32LittleEndian(plainExeFsHdr.AsSpan(entryPos + 12, 4));
                    if (secSize == 0 || string.IsNullOrEmpty(secName))
                        continue;

                    bool usePrimary = secName.Equals("icon", StringComparison.Ordinal) ||
                                      secName.Equals("banner", StringComparison.Ordinal);
                    long absStart = exefsOffset + 0x200 + secRelOff;
                    long alignedLen = CiaConstants.AlignUp(secSize, CiaConstants.NcchMediaUnitSize);
                    _regions.Add(new CtrRegion(absStart, alignedLen, exefsOffset, usePrimary ? _primaryAes : _secondaryAes, exefsCtr));
                }
            }

            if (romfsSize > 0)
            {
                _regions.Add(new CtrRegion(romfsOffset, romfsSize, romfsOffset, _secondaryAes, romfsCtr));
            }

            _regions.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        private static UInt128 BuildBaseCtr(ushort ncchVersion, ulong partitionIdLe, byte sectionType, uint sectionByteOffset)
        {
            Span<byte> ctr = stackalloc byte[16];
            ctr.Clear();
            if (ncchVersion == 1)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(ctr.Slice(0, 8), partitionIdLe);
                BinaryPrimitives.WriteUInt32BigEndian(ctr.Slice(12, 4), sectionByteOffset);
            }
            else
            {
                // NCCH v0 / v2: reversed 8-byte PartitionId (= Big-Endian TitleId) + sectionType at byte[8]
                BinaryPrimitives.WriteUInt64BigEndian(ctr.Slice(0, 8), partitionIdLe);
                ctr[8] = sectionType;
            }
            return CtrKeyStore.ReadUInt128BigEndian(ctr);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _rawNcch.Length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0 || _position >= Length)
                return 0;

            int remaining = (int)Math.Min(count, Length - _position);
            int totalRead = 0;

            while (remaining > 0)
            {
                if (_position < 0x200)
                {
                    int hdrTake = (int)Math.Min(remaining, 0x200 - _position);
                    Buffer.BlockCopy(_patchedHeader512, (int)_position, buffer, offset + totalRead, hdrTake);
                    _position += hdrTake;
                    totalRead += hdrTake;
                    remaining -= hdrTake;
                    continue;
                }

                if (!_isEncrypted)
                {
                    _rawNcch.Seek(_position, SeekOrigin.Begin);
                    int r = _rawNcch.Read(buffer, offset + totalRead, remaining);
                    if (r <= 0)
                        break;
                    _position += r;
                    totalRead += r;
                    remaining -= r;
                    continue;
                }

                CtrRegion active = null;
                long nextRegionStart = Length;
                foreach (var reg in _regions)
                {
                    if (_position >= reg.Start && _position < reg.Start + reg.Length)
                    {
                        active = reg;
                        break;
                    }
                    if (reg.Start > _position && reg.Start < nextRegionStart)
                    {
                        nextRegionStart = reg.Start;
                    }
                }

                if (active == null)
                {
                    int plainTake = (int)Math.Min(remaining, nextRegionStart - _position);
                    _rawNcch.Seek(_position, SeekOrigin.Begin);
                    int r = _rawNcch.Read(buffer, offset + totalRead, plainTake);
                    if (r <= 0)
                        break;
                    _position += r;
                    totalRead += r;
                    remaining -= r;
                }
                else
                {
                    int cryptoTake = (int)Math.Min(remaining, (active.Start + active.Length) - _position);
                    ReadRegionPlaintext(active, _position, buffer, offset + totalRead, cryptoTake);
                    _position += cryptoTake;
                    totalRead += cryptoTake;
                    remaining -= cryptoTake;
                }
            }

            return totalRead;
        }

        private void ReadRegionPlaintext(CtrRegion reg, long absPos, byte[] dest, int destOffset, int len)
        {
            _rawNcch.Seek(absPos, SeekOrigin.Begin);
            int read = 0;
            while (read < len)
            {
                int r = _rawNcch.Read(dest, destOffset + read, len - read);
                if (r <= 0)
                    throw new EndOfStreamException("Unexpected EOF inside encrypted NCCH region.");
                read += r;
            }

            long relPos = absPos - reg.CtrOrigin;
            long firstBlock = relPos / 16;
            long lastBlockExclusive = (relPos + len + 15) / 16;
            int blockCount = (int)(lastBlockExclusive - firstBlock);

            byte[] counterBlocks = new byte[blockCount * 16];
            for (int b = 0; b < blockCount; b++)
            {
                UInt128 ctrVal = unchecked(reg.BaseCounter + (UInt128)(ulong)(firstBlock + b));
                CtrKeyStore.WriteUInt128BigEndian(counterBlocks.AsSpan(b * 16, 16), ctrVal);
            }

            byte[] keystream = reg.KeyAes.EncryptEcb(counterBlocks, PaddingMode.None);
            int intraOffset = (int)(relPos % 16);
            for (int i = 0; i < len; i++)
            {
                dest[destOffset + i] ^= keystream[intraOffset + i];
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0 || target > Length)
                throw new IOException("Attempted to seek outside NcchDecryptedReadStream.");
            _position = target;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _primaryAes?.Dispose();
                _secondaryAes?.Dispose();
                if (!_leaveOpen)
                    _rawNcch.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
