using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace IEGORandomizer.Engine.Seeding
{
    /// <summary>
    /// Platform-independent, deterministic Xoshiro256** pseudo-random number generator.
    /// Guarantees bit-for-bit identical sequences across .NET versions and operating systems,
    /// unlike <see cref="System.Random"/> whose internal algorithm is not version-locked.
    /// </summary>
    public sealed class DeterministicRng
    {
        private ulong _s0;
        private ulong _s1;
        private ulong _s2;
        private ulong _s3;

        public string SubsystemName { get; }

        public DeterministicRng(string subsystemName, ulong s0, ulong s1, ulong s2, ulong s3)
        {
            SubsystemName = subsystemName ?? string.Empty;
            _s0 = s0;
            _s1 = s1;
            _s2 = s2;
            _s3 = s3;

            // Xoshiro256** forbids the all-zero state
            if ((_s0 | _s1 | _s2 | _s3) == 0UL)
            {
                _s0 = 0x9E3779B97F4A7C15UL;
                _s1 = 0xBF58476D1CE4E5B9UL;
                _s2 = 0x94D049BB133111EBUL;
                _s3 = 0xD6E8FEB86659FD93UL;
            }
        }

        public ulong NextUInt64()
        {
            ulong result = BitOperations.RotateLeft(_s1 * 5UL, 7) * 9UL;
            ulong t = _s1 << 17;

            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;

            _s2 ^= t;
            _s3 = BitOperations.RotateLeft(_s3, 45);

            return result;
        }

        /// <summary>
        /// Unbiased bounded integer in [0, maxExclusive) using Lemire's rejection method.
        /// </summary>
        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), "maxExclusive must be > 0.");
            if (maxExclusive == 1)
                return 0;

            uint range = (uint)maxExclusive;
            ulong x = (uint)NextUInt64();
            ulong m = x * range;
            uint l = (uint)m;
            if (l < range)
            {
                uint threshold = (uint)(0 - range) % range;
                while (l < threshold)
                {
                    x = (uint)NextUInt64();
                    m = x * range;
                    l = (uint)m;
                }
            }
            return (int)(m >> 32);
        }

        /// <summary>
        /// Unbiased integer in [minInclusive, maxInclusive].
        /// </summary>
        public int NextIntInclusive(int minInclusive, int maxInclusive)
        {
            if (maxInclusive < minInclusive)
                throw new ArgumentOutOfRangeException(nameof(maxInclusive), "maxInclusive must be >= minInclusive.");
            long span = (long)maxInclusive - minInclusive + 1L;
            if (span <= 1)
                return minInclusive;
            return minInclusive + NextInt((int)span);
        }

        public bool NextChancePercent(int percent)
        {
            if (percent <= 0)
                return false;
            if (percent >= 100)
                return true;
            return NextInt(100) < percent;
        }

        public T PickOne<T>(IReadOnlyList<T> items)
        {
            if (items == null || items.Count == 0)
                throw new ArgumentException("Cannot pick from an empty collection.", nameof(items));
            return items[NextInt(items.Count)];
        }

        /// <summary>
        /// Standard Fisher-Yates in-place shuffle.
        /// </summary>
        public void Shuffle<T>(IList<T> list)
        {
            if (list == null)
                throw new ArgumentNullException(nameof(list));

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = NextInt(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>
        /// Produces a strict mathematical derangement of <paramref name="distinctItems"/>
        /// using Sattolo's cyclic permutation algorithm: for all i in [0..N-1], result[i] != distinctItems[i]
        /// whenever N &gt;= 2 and elements are distinct.
        /// </summary>
        public List<T> CreateDerangement<T>(IReadOnlyList<T> distinctItems)
        {
            if (distinctItems == null)
                throw new ArgumentNullException(nameof(distinctItems));

            var result = distinctItems.ToList();
            if (result.Count <= 1)
                return result;

            // Sattolo's algorithm: j is chosen in [0, i) instead of [0, i], guaranteeing a single N-cycle
            // with zero fixed points!
            for (int i = result.Count - 1; i > 0; i--)
            {
                int j = NextInt(i); // strictly < i
                (result[i], result[j]) = (result[j], result[i]);
            }

            // If input contained duplicate values, repair any accidental value-level fixed point where possible
            var eq = EqualityComparer<T>.Default;
            for (int i = 0; i < result.Count; i++)
            {
                if (eq.Equals(result[i], distinctItems[i]))
                {
                    for (int k = 0; k < result.Count; k++)
                    {
                        if (k == i)
                            continue;
                        if (!eq.Equals(result[k], distinctItems[i]) && !eq.Equals(result[i], distinctItems[k]))
                        {
                            (result[i], result[k]) = (result[k], result[i]);
                            break;
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Builds a bijection <c>item -&gt; derangedItem</c> over <paramref name="distinctPool"/>
        /// such that <c>map[x] != x</c> for every item whenever <c>distinctPool.Count &gt;= 2</c>.
        /// </summary>
        public Dictionary<T, T> CreateDerangementMap<T>(IReadOnlyList<T> distinctPool)
        {
            var unique = distinctPool.Distinct().ToList();
            var deranged = CreateDerangement(unique);
            var map = new Dictionary<T, T>(unique.Count);
            for (int i = 0; i < unique.Count; i++)
            {
                map[unique[i]] = deranged[i];
            }
            return map;
        }
    }
}
