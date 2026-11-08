using System;
using System.Collections.Generic;
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

        public static string GetDefaultTitleId(GameEdition edition)
        {
            return edition == GameEdition.Shadow ? ShadowEurTitleId : LightEurTitleId;
        }

        public static GameEdition DetectEdition(string titleIdHex, string productCode)
        {
            if (!string.IsNullOrWhiteSpace(titleIdHex))
            {
                string norm = titleIdHex.Trim().ToUpperInvariant();
                if (norm == LightEurTitleId)
                    return GameEdition.Light;
                if (norm == ShadowEurTitleId || norm == ShadowEurAltTitleId)
                    return GameEdition.Shadow;
            }

            if (!string.IsNullOrWhiteSpace(productCode))
            {
                string pc = productCode.Trim().ToUpperInvariant();
                // CTR-P-AE4P / CTR-P-AE4J (Shine / Light), CTR-P-AE5P / CTR-P-AE5J (Dark / Shadow)
                if (pc.Contains("AE4"))
                    return GameEdition.Light;
                if (pc.Contains("AE5"))
                    return GameEdition.Shadow;
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
