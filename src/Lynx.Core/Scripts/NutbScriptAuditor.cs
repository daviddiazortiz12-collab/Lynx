using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using StudioElevenLib.Level5.Compression;
using StudioElevenLib.Tools;

namespace Lynx.Core.Scripts
{
    /// <summary>
    /// Summary of story script dependencies extracted from compiled Squirrel 2.2.3 (<c>.nutb</c>)
    /// bytecode files inside <c>ie_a.fa</c>.
    /// </summary>
    public sealed class NutbAuditReport
    {
        public int ScannedScriptCount { get; set; }
        public HashSet<string> NativeCommands { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ReferencedCharacterIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReferencedPartyParamIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReferencedBattleCharacterIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReferencedCutsceneModelIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReferencedSkillIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReferencedAvatarIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReferencedMatchIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReferencedTeamIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReferencedItemIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> IntegerLiterals { get; } = new();
        public List<NutbSkillCallSiteRecord> SkillCallSites { get; } = new();
        public HashSet<string> StoryDirectorItemIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scans compiled Squirrel 2.2.3 (<c>.nutb</c>) bytecode inside <c>ie_a.fa</c>
    /// (verifying <c>SQ_BYTECODE_STREAM_TAG = 0xFAFA</c>, <c>"RIQS"</c> header, and <c>SQObjectType</c>
    /// literal entries documented in <c>projectz/include/squirrel-2.2.3</c>) to identify all characters,
    /// skills, avatars, matches, teams, and items directly referenced by story or event scripts.
    /// </summary>
    public static class NutbScriptAuditor
    {
        public const ushort SqBytecodeStreamTag = 0xFAFA;
        public const uint SqirMagicLe = 0x53514952; // 'R','I','Q','S' in little-endian memory of int 'SQIR'
        public const uint SqirMagicAlt = 0x52495153;
        public const uint OtStringTag = 0x08000010;  // _RT_STRING | SQOBJECT_REF_COUNTED
        public const uint OtIntegerTag = 0x05000002; // _RT_INTEGER | SQOBJECT_NUMERIC | SQOBJECT_CANBEFALSE

        private static readonly Regex EntityTokenRegex = new(
            @"^(?:para_cp\d{4}|cp\d{4}|cn\d{4}|ca\d{4}|wh[sodkp]\d{4}|wk[sodkp]\d{4}|ck\d{4}|btl\d{4}|para_ts\d{4}|para_te\d{4}|ts\d{4}|te\d{4}|ika\d{4}|ii[a-z]\d{4})$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static NutbAuditReport AuditArchive(VirtualDirectory archiveRoot)
        {
            var report = new NutbAuditReport();
            if (archiveRoot == null)
                return report;

            WalkVirtualDirectory(archiveRoot, string.Empty, report);
            return report;
        }

        public static void InspectNutbPayload(byte[] rawBytes, NutbAuditReport report, string virtualPath = null)
        {
            if (rawBytes == null || rawBytes.Length < 18 || report == null)
                return;

            byte[] payload = TryUnwrapPayload(rawBytes);
            if (payload == null || payload.Length < 18)
                return;

            ushort streamTag = BitConverter.ToUInt16(payload, 0);
            uint sqir = BitConverter.ToUInt32(payload, 2);
            if (streamTag != SqBytecodeStreamTag || (sqir != SqirMagicLe && sqir != SqirMagicAlt))
            {
                return;
            }

            report.ScannedScriptCount++;
            EncodingInitializer.EnsureRegistered();
            Encoding shiftJis = Encoding.GetEncoding("Shift-JIS");

            bool isBattleScript = !string.IsNullOrEmpty(virtualPath) &&
                (virtualPath.Contains("/battle/", StringComparison.OrdinalIgnoreCase) ||
                 virtualPath.Contains("/btl", StringComparison.OrdinalIgnoreCase));
            var localCharTokens = new List<string>();

            // Scan Squirrel 2.2.3 typed object records in constant pools:
            // OT_STRING (0x08000010): [uint32 tag][int32 len][byte[len] chars]
            // OT_INTEGER (0x05000002): [uint32 tag][int32 value]
            for (int i = 18; i <= payload.Length - 8; i++)
            {
                uint tag = BitConverter.ToUInt32(payload, i);
                if (tag == OtStringTag)
                {
                    int strLen = BitConverter.ToInt32(payload, i + 4);
                    if (strLen > 0 && strLen <= 256 && i + 8 + strLen <= payload.Length)
                    {
                        if (LooksPrintableBytes(payload, i + 8, strLen))
                        {
                            string literal = shiftJis.GetString(payload, i + 8, strLen);
                            ClassifyStringLiteral(literal, report, ref isBattleScript, localCharTokens);
                            i += 7 + strLen;
                        }
                    }
                }
                else if (tag == OtIntegerTag)
                {
                    int val = BitConverter.ToInt32(payload, i + 4);
                    if (val != 0 && val != -1)
                    {
                        report.IntegerLiterals.Add(val);
                    }
                    i += 7;
                }
            }

            if (isBattleScript && localCharTokens.Count > 0)
            {
                foreach (string tok in localCharTokens)
                {
                    report.ReferencedBattleCharacterIds.Add(tok);
                }
            }

            // Perform full Squirrel 2.2.x / 3.x SQFunctionProto instruction call-site analysis when valid
            if (NutbBytecodeCodec.TryParse(rawBytes, out NutbStream parsedStream))
            {
                NutbBytecodeCodec.AnalyzeFunctionTree(parsedStream.MainFunction, virtualPath, report);
            }
        }

        private static void WalkVirtualDirectory(VirtualDirectory directory, string currentPath, NutbAuditReport report)
        {
            foreach (var kvp in directory.Files)
            {
                if (kvp.Key.EndsWith(".nutb", StringComparison.OrdinalIgnoreCase))
                {
                    string vPath = currentPath + "/" + kvp.Key;
                    kvp.Value.Read();
                    if (kvp.Value.ByteContent != null)
                    {
                        InspectNutbPayload(kvp.Value.ByteContent, report, vPath);
                    }
                }
            }

            foreach (VirtualDirectory child in directory.Folders)
            {
                WalkVirtualDirectory(child, currentPath + "/" + child.Name, report);
            }
        }

        private static byte[] TryUnwrapPayload(byte[] rawBytes)
        {
            if (rawBytes.Length >= 6 && BitConverter.ToUInt16(rawBytes, 0) == SqBytecodeStreamTag)
            {
                return rawBytes;
            }

            try
            {
                byte[] decompressed = Compressor.Decompress(rawBytes);
                if (decompressed != null && decompressed.Length >= 18)
                {
                    return decompressed;
                }
            }
            catch
            {
                // Not Level-5 compressed; ignore
            }

            return rawBytes;
        }

        private static bool LooksPrintableBytes(byte[] data, int offset, int length)
        {
            for (int i = 0; i < length; i++)
            {
                byte b = data[offset + i];
                if (b == 0 || (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D))
                    return false;
            }
            return true;
        }

        private static void ClassifyStringLiteral(
            string literal,
            NutbAuditReport report,
            ref bool isBattleScript,
            List<string> localCharTokens)
        {
            if (string.IsNullOrWhiteSpace(literal))
                return;

            string trimmed = literal.Trim();
            if (trimmed.StartsWith("CMND_", StringComparison.Ordinal))
            {
                report.NativeCommands.Add(trimmed);
                if (trimmed.StartsWith("CMND_BTL_", StringComparison.Ordinal) ||
                    trimmed.StartsWith("CMND_BTLEV_", StringComparison.Ordinal))
                {
                    isBattleScript = true;
                }
                return;
            }

            if (!EntityTokenRegex.IsMatch(trimmed))
                return;

            string lower = trimmed.ToLowerInvariant();
            if (lower.StartsWith("para_cp"))
            {
                report.ReferencedCharacterIds.Add(lower);
                report.ReferencedPartyParamIds.Add(lower);
                localCharTokens.Add(lower);
            }
            else if (lower.StartsWith("cp") || lower.StartsWith("cn") || lower.StartsWith("ca"))
            {
                report.ReferencedCharacterIds.Add(lower);
                report.ReferencedCutsceneModelIds.Add(lower);
                if (lower.StartsWith("cp"))
                {
                    localCharTokens.Add(lower);
                }
            }
            else if (lower.StartsWith("wh") || lower.StartsWith("wk"))
            {
                report.ReferencedSkillIds.Add(lower);
            }
            else if (lower.StartsWith("ck"))
            {
                report.ReferencedAvatarIds.Add(lower);
            }
            else if (lower.StartsWith("btl"))
            {
                report.ReferencedMatchIds.Add(lower);
            }
            else if (lower.StartsWith("para_ts") || lower.StartsWith("para_te") || lower.StartsWith("ts") || lower.StartsWith("te"))
            {
                report.ReferencedTeamIds.Add(lower);
            }
            else if (lower.StartsWith("ik") || lower.StartsWith("ii"))
            {
                report.ReferencedItemIds.Add(lower);
            }
        }
    }
}
