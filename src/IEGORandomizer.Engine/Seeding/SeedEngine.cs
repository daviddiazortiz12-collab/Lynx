using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace IEGORandomizer.Engine.Seeding
{
    /// <summary>
    /// Derives cryptographically isolated, deterministic <see cref="DeterministicRng"/> instances
    /// for each randomizer subsystem from a single user-visible seed string.
    /// Toggling or reconfiguring one subsystem never alters the random sequence of any other subsystem.
    /// </summary>
    public sealed class SeedEngine
    {
        private const string DomainVersionPrefix = "IEGO1-LUZ-SOMBRA-V1:";
        private static readonly string[] SeedWords =
        {
            "PEGASO", "LANCELOT", "MAESTRO", "RAIMON", "SOL", "SOMBRA",
            "LUZ", "TORNADO", "RELAMPAGO", "DRAGON", "FENIX", "TITAN"
        };

        private readonly byte[] _masterKey;
        private readonly Dictionary<string, DeterministicRng> _subsystems = new(StringComparer.Ordinal);

        public string CanonicalSeed { get; }
        public string VerificationFingerprint { get; }

        public SeedEngine(string seedInput)
        {
            CanonicalSeed = NormalizeOrGenerateSeed(seedInput);
            _masterKey = SHA256.HashData(Encoding.UTF8.GetBytes(DomainVersionPrefix + CanonicalSeed));
            VerificationFingerprint = Convert.ToHexString(_masterKey, 0, 4);
        }

        /// <summary>
        /// Returns an independent deterministic PRNG dedicated to <paramref name="subsystemKey"/>.
        /// Calling <c>Fork("players.swap")</c> vs <c>GetSubsystem("players.swap")</c>:
        /// <see cref="GetSubsystem"/> caches per key within a single run; <see cref="CreateFreshSubsystem"/>
        /// always starts from offset 0 of that subsystem's stream.
        /// </summary>
        public DeterministicRng GetSubsystem(string subsystemKey)
        {
            if (string.IsNullOrWhiteSpace(subsystemKey))
                throw new ArgumentException("Subsystem key cannot be empty.", nameof(subsystemKey));

            if (!_subsystems.TryGetValue(subsystemKey, out var rng))
            {
                rng = CreateFreshSubsystem(subsystemKey);
                _subsystems[subsystemKey] = rng;
            }
            return rng;
        }

        public DeterministicRng CreateFreshSubsystem(string subsystemKey)
        {
            if (string.IsNullOrWhiteSpace(subsystemKey))
                throw new ArgumentException("Subsystem key cannot be empty.", nameof(subsystemKey));

            byte[] tagBytes = Encoding.UTF8.GetBytes(DomainVersionPrefix + subsystemKey);
            byte[] derived = HMACSHA256.HashData(_masterKey, tagBytes);

            ulong s0 = BinaryPrimitives.ReadUInt64LittleEndian(derived.AsSpan(0, 8));
            ulong s1 = BinaryPrimitives.ReadUInt64LittleEndian(derived.AsSpan(8, 8));
            ulong s2 = BinaryPrimitives.ReadUInt64LittleEndian(derived.AsSpan(16, 8));
            ulong s3 = BinaryPrimitives.ReadUInt64LittleEndian(derived.AsSpan(24, 8));

            return new DeterministicRng(subsystemKey, s0, s1, s2, s3);
        }

        public static string GenerateRandomSeedString()
        {
            Span<byte> randomBytes = stackalloc byte[5];
            RandomNumberGenerator.Fill(randomBytes);
            string word = SeedWords[randomBytes[0] % SeedWords.Length];
            ushort part1 = BinaryPrimitives.ReadUInt16LittleEndian(randomBytes.Slice(1, 2));
            ushort part2 = BinaryPrimitives.ReadUInt16LittleEndian(randomBytes.Slice(3, 2));
            return $"{word}-{part1:X4}-{part2:X4}";
        }

        public static string NormalizeOrGenerateSeed(string rawSeed)
        {
            if (string.IsNullOrWhiteSpace(rawSeed))
                return GenerateRandomSeedString();
            return rawSeed.Trim();
        }
    }
}
