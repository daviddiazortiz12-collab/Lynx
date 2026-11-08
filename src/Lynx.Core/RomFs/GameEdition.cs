using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using StudioElevenLib.Tools;

namespace Lynx.Core.RomFs
{
    /// <summary>
    /// Identifies the Inazuma Eleven GO 1 edition.
    /// </summary>
    public enum GameEdition
    {
        Auto = 0,
        Light = 1,
        Shadow = 2
    }

    /// <summary>
    /// Identifies the type of game input selected by the user.
    /// </summary>
    public enum GameInputKind
    {
        Arc0File = 0,
        RomFsDirectory = 1,
        Nintendo3dsRom = 2,
        NcchContainer = 3,
        RomFsImage = 4
    }

    /// <summary>
    /// Canonical Nintendo 3DS Title IDs and Product Codes for Inazuma Eleven GO: Light &amp; Shadow.
    /// </summary>
    public static class TitleIdCatalog
    {
        public const string LightEurTitleId = "0004000000112F00";
        public const string ShadowEurTitleId = "0004000000113000";
        public const string ShadowEurAltTitleId = "000400000017C200";
        public const string LightJpnTitleId = "000400000006D400";
        public const string ShadowJpnTitleId = "000400000006D500";

        public static string GetDefaultTitleId(GameEdition edition)
        {
            return edition == GameEdition.Shadow ? ShadowEurTitleId : LightEurTitleId;
        }

        public static GameEdition DetectEdition(string titleIdHex, string productCode)
        {
            if (!string.IsNullOrWhiteSpace(titleIdHex))
            {
                string norm = titleIdHex.Trim().ToUpperInvariant();
                if (norm == LightEurTitleId || norm == LightJpnTitleId)
                    return GameEdition.Light;
                if (norm == ShadowEurTitleId || norm == ShadowEurAltTitleId || norm == ShadowJpnTitleId)
                    return GameEdition.Shadow;
            }

            if (!string.IsNullOrWhiteSpace(productCode))
            {
                string pc = productCode.Trim().ToUpperInvariant();
                // CTR-P-AE4P / CTR-P-AE4J (Light / Luz / Shine)
                // CTR-P-AEDP (EUR Shadow / Sombra), CTR-P-AE5J (JPN Dark)
                if (pc.Contains("AE4"))
                    return GameEdition.Light;
                if (pc.Contains("AED") || pc.Contains("AE5"))
                    return GameEdition.Shadow;
            }

            return GameEdition.Auto;
        }

        public static GameEdition DetectEditionFromPathHint(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return GameEdition.Auto;

            string norm = path.ToUpperInvariant();
            if (norm.Contains(ShadowEurTitleId) || norm.Contains(ShadowEurAltTitleId) || norm.Contains(ShadowJpnTitleId) ||
                norm.Contains("CTR-P-AED") || norm.Contains("CTR-P-AE5") ||
                norm.Contains("SOMBRA") || norm.Contains("SHADOW") || norm.Contains("OMBRE") || norm.Contains("OMBRA") || norm.Contains("SCHATTEN"))
            {
                return GameEdition.Shadow;
            }

            if (norm.Contains(LightEurTitleId) || norm.Contains(LightJpnTitleId) ||
                norm.Contains("CTR-P-AE4") ||
                norm.Contains("LUZ") || norm.Contains("LIGHT") || norm.Contains("LUMIERE") || norm.Contains("LUMIÈRE") || norm.Contains("LUCE") || norm.Contains("LICHT") || norm.Contains("SHINE"))
            {
                return GameEdition.Light;
            }

            // Check sibling RomFS ending movie signature (ed00s_c.moflex = Light/Shine, ed00d_c.moflex = Shadow/Dark)
            try
            {
                string dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    bool hasShineEnding = File.Exists(Path.Combine(dir, "movie", "ed00s_c.moflex"));
                    bool hasDarkEnding = File.Exists(Path.Combine(dir, "movie", "ed00d_c.moflex"));
                    if (hasShineEnding && !hasDarkEnding) return GameEdition.Light;
                    if (hasDarkEnding && !hasShineEnding) return GameEdition.Shadow;
                }
            }
            catch
            {
                // Non-fatal
            }

            return GameEdition.Auto;
        }
    }

    /// <summary>
    /// Deterministic CRC32 ID helpers matching the Level-5 engine conventions in IEGO1.
    /// </summary>
    public static class Level5IdHelper
    {
        private static Encoding ShiftJis
        {
            get
            {
                EncodingInitializer.EnsureRegistered();
                return Encoding.GetEncoding("Shift-JIS");
            }
        }

        public static int ComputeCrc32Utf8(string code)
        {
            if (string.IsNullOrEmpty(code))
                return 0;
            return unchecked((int)Crc32.Compute(Encoding.UTF8.GetBytes(code)));
        }

        public static int ComputeCrc32ShiftJis(string code)
        {
            if (string.IsNullOrEmpty(code))
                return 0;
            return unchecked((int)Crc32.Compute(ShiftJis.GetBytes(code)));
        }

        public static Dictionary<int, string> BuildPrefixLookup(params string[] prefixes)
        {
            var dict = new Dictionary<int, string>(prefixes.Length * 4096);
            foreach (string prefix in prefixes)
            {
                for (int i = 0; i < 10000; i++)
                {
                    string id = $"{prefix}{i:D4}";
                    int crc = ComputeCrc32Utf8(id);
                    if (!dict.ContainsKey(crc))
                    {
                        dict[crc] = id;
                    }
                }
            }
            return dict;
        }
    }
}
