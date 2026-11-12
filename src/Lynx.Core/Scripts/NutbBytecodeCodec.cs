using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lynx.Core.RomFs;
using StudioElevenLib.Level5.Compression;
using StudioElevenLib.Level5.Compression.LZ10;
using StudioElevenLib.Level5.Compression.NoCompression;
using StudioElevenLib.Tools;

namespace Lynx.Core.Scripts
{
    public enum NutbOpcode : byte
    {
        Line = 0x00,
        Load = 0x01,
        LoadInt = 0x02,
        LoadFloat = 0x03,
        DLoad = 0x04,
        TailCall = 0x05,
        Call = 0x06,
        PrepCall = 0x07,
        PrepCallK = 0x08,
        GetK = 0x09,
        Move = 0x0A,
        NewSlot = 0x0B,
        Delete = 0x0C,
        Set = 0x0D,
        Get = 0x0E,
        Eq = 0x0F,
        Ne = 0x10,
        Arith = 0x11,
        Bitw = 0x12,
        Return = 0x13,
        LoadNulls = 0x14,
        LoadRootTable = 0x15,
        LoadBool = 0x16,
        DMove = 0x17,
        Jmp = 0x18,
        JnzOrJcmp = 0x19,
        Jz = 0x1A,
        LoadFreeVar = 0x1B,
        Vargc = 0x1C,
        GetVargv = 0x1D,
        NewTable = 0x1E,
        NewArray = 0x1F,
        AppendArray = 0x20,
        GetParent = 0x21,
        CompArith = 0x22,
        CompArithL = 0x23,
        Inc = 0x24,
        IncL = 0x25,
        PInc = 0x26,
        PIncL = 0x27,
        Cmp = 0x28,
        Exists = 0x29,
        InstanceOf = 0x2A,
        And = 0x2B,
        Or = 0x2C,
        Neg = 0x2D,
        Not = 0x2E,
        BwNot = 0x2F,
        Closure = 0x30,
        Yield = 0x31,
        Resume = 0x32,
        Foreach = 0x33,
        PostForeach = 0x34,
        Delegate = 0x35,
        Clone = 0x36,
        TypeOf = 0x37,
        PushTrap = 0x38,
        PopTrap = 0x39,
        Throw = 0x3A,
        Class = 0x3B,
        NewSlotA = 0x3C
    }

    public enum NutbSkillCallKind
    {
        Unlock,
        BattleLearn,
        BattleForget,
        BattleBan,
        BattleUse
    }

    public sealed class NutbSkillCallSiteRecord
    {
        public string VirtualPath { get; init; } = string.Empty;
        public string FunctionName { get; init; } = string.Empty;
        public string CalleeName { get; init; } = string.Empty;
        public NutbSkillCallKind Kind { get; init; }
        public int? Side { get; init; }
        public string CharaId { get; init; } = string.Empty;
        public string SkillId { get; init; } = string.Empty;
    }

    public sealed class NutbValue
    {
        public const uint OtNull = 0x01000001;
        public const uint OtInteger = 0x05000002;
        public const uint OtFloat = 0x05000004;
        public const uint OtBool = 0x01000008;
        public const uint OtString = 0x08000010;

        public uint TypeTag { get; init; }
        public int IntValue { get; init; }
        public float FloatValue { get; init; }
        public byte[] RawStringBytes { get; init; } = Array.Empty<byte>();

        public static NutbValue Null() => new() { TypeTag = OtNull };
        public static NutbValue FromInt(int value) => new() { TypeTag = OtInteger, IntValue = value };
        public static NutbValue FromBool(bool value) => new() { TypeTag = OtBool, IntValue = value ? 1 : 0 };
        public static NutbValue FromFloat(float value) => new() { TypeTag = OtFloat, FloatValue = value };

        public static NutbValue FromAsciiString(string value) => new()
        {
            TypeTag = OtString,
            RawStringBytes = Encoding.ASCII.GetBytes(value ?? string.Empty)
        };

        public string DecodeString(Encoding shiftJis)
        {
            if (TypeTag != OtString || RawStringBytes == null || RawStringBytes.Length == 0)
                return string.Empty;
            return shiftJis.GetString(RawStringBytes);
        }
    }

    public sealed class NutbOuterVar
    {
        public int OuterType { get; set; }
        public NutbValue Source { get; set; } = NutbValue.Null();
        public NutbValue Name { get; set; } = NutbValue.Null();
    }

    public sealed class NutbLocalVarInfo
    {
        public NutbValue Name { get; set; } = NutbValue.Null();
        public int Pos { get; set; }
        public int StartOp { get; set; }
        public int EndOp { get; set; }
    }

    public struct NutbLineInfo
    {
        public int Line;
        public int Op;
    }

    public struct NutbInstruction
    {
        public int Arg1;
        public NutbOpcode Op;
        public byte Arg0;
        public byte Arg2;
        public byte Arg3;

        public NutbInstruction(NutbOpcode op, byte arg0 = 0, int arg1 = 0, byte arg2 = 0, byte arg3 = 0)
        {
            Op = op;
            Arg0 = arg0;
            Arg1 = arg1;
            Arg2 = arg2;
            Arg3 = arg3;
        }
    }

    public sealed class NutbFunctionProto
    {
        public NutbValue SourceName { get; set; } = NutbValue.Null();
        public NutbValue Name { get; set; } = NutbValue.Null();
        public bool HasDefaultParamsSection { get; set; }
        public int VarParamsByteSize { get; set; } = 1;
        public List<NutbValue> Literals { get; } = new();
        public List<NutbValue> Parameters { get; } = new();
        public List<NutbOuterVar> OuterValues { get; } = new();
        public List<NutbLocalVarInfo> Locals { get; } = new();
        public List<NutbLineInfo> LineInfos { get; } = new();
        public List<int> DefaultParams { get; } = new();
        public List<NutbInstruction> Instructions { get; } = new();
        public List<NutbFunctionProto> Functions { get; } = new();
        public int StackSize { get; set; }
        public byte IsGenerator { get; set; }
        public int VarParams { get; set; }
    }

    public enum NutbOuterContainerKind
    {
        RawBytecode,
        Level5NoCompression,
        Level5Lz10
    }

    public sealed class NutbStream
    {
        public NutbOuterContainerKind ContainerKind { get; set; } = NutbOuterContainerKind.RawBytecode;
        public uint SqirMagic { get; set; } = NutbScriptAuditor.SqirMagicLe;
        public int CharSize { get; set; } = 1;
        public bool HasExtendedSizeHeader { get; set; }
        public int IntSize { get; set; } = 4;
        public int FloatSize { get; set; } = 4;
        public NutbFunctionProto MainFunction { get; set; } = new();
        public byte[] TrailingBytesAfterTail { get; set; } = Array.Empty<byte>();
    }

    public sealed class NutbPatchSummary
    {
        public int PatchedFilesCount { get; set; }
        public int PatchedUnlockCallsCount { get; set; }
        public int PatchedBattleLearnCallsCount { get; set; }
        public int PatchedBattleForgetCallsCount { get; set; }
        public int PatchedBattleBanCallsCount { get; set; }
        public int InjectedTemporaryBattleSwapsCount { get; set; }
        public int PatchedDirectorCallsCount { get; set; }
    }

    /// <summary>
    /// Lossless Squirrel 2.2.x / 3.x (<c>.nutb</c>) bytecode reader, call-site analyzer, and
    /// instruction-level patcher for Dynamic Story-Technique Swaps and Story Coach randomization.
    /// </summary>
    public static class NutbBytecodeCodec
    {
        public const uint PartMagicLe = 0x54524150; // 'PART'
        public const uint TailMagicLe = 0x4C494154; // 'TAIL'

        private static readonly HashSet<NutbOpcode> RelativeJumpOpcodes = new()
        {
            NutbOpcode.Jmp,
            NutbOpcode.JnzOrJcmp,
            NutbOpcode.Jz,
            NutbOpcode.And,
            NutbOpcode.Or,
            NutbOpcode.Foreach,
            NutbOpcode.PostForeach,
            NutbOpcode.PushTrap
        };

        public static bool TryParse(byte[] rawArchiveBytes, out NutbStream stream)
        {
            stream = null;
            if (rawArchiveBytes == null || rawArchiveBytes.Length < 18)
                return false;

            NutbOuterContainerKind containerKind = NutbOuterContainerKind.RawBytecode;
            byte[] payload = rawArchiveBytes;

            if (BitConverter.ToUInt16(rawArchiveBytes, 0) != NutbScriptAuditor.SqBytecodeStreamTag)
            {
                try
                {
                    uint compHeader = BitConverter.ToUInt32(rawArchiveBytes, 0);
                    int method = (int)(compHeader & 0x7);
                    byte[] decompressed = Compressor.Decompress(rawArchiveBytes);
                    if (decompressed == null || decompressed.Length < 18 ||
                        BitConverter.ToUInt16(decompressed, 0) != NutbScriptAuditor.SqBytecodeStreamTag)
                    {
                        return false;
                    }

                    payload = decompressed;
                    containerKind = method == 1
                        ? NutbOuterContainerKind.Level5Lz10
                        : NutbOuterContainerKind.Level5NoCompression;
                }
                catch
                {
                    return false;
                }
            }

            try
            {
                int pos = 0;
                ushort tag = ReadUInt16(payload, ref pos);
                if (tag != NutbScriptAuditor.SqBytecodeStreamTag)
                    return false;

                uint sqir = ReadUInt32(payload, ref pos);
                if (sqir != NutbScriptAuditor.SqirMagicLe && sqir != NutbScriptAuditor.SqirMagicAlt)
                    return false;

                int charSize = ReadInt32(payload, ref pos);
                if (charSize != 1 && charSize != 2)
                    return false;

                uint peek = BitConverter.ToUInt32(payload, pos);
                bool hasExtendedHeader = false;
                int intSize = 4;
                int floatSize = 4;

                if (peek != PartMagicLe)
                {
                    hasExtendedHeader = true;
                    intSize = ReadInt32(payload, ref pos);
                    floatSize = ReadInt32(payload, ref pos);
                    if (intSize != 4 || floatSize != 4)
                        return false;
                }

                if (!TryReadFunction(payload, ref pos, charSize, hasExtendedHeader, out NutbFunctionProto main))
                    return false;

                if (pos + 4 > payload.Length || ReadUInt32(payload, ref pos) != TailMagicLe)
                    return false;

                byte[] trailing = pos < payload.Length
                    ? payload.AsSpan(pos).ToArray()
                    : Array.Empty<byte>();

                stream = new NutbStream
                {
                    ContainerKind = containerKind,
                    SqirMagic = sqir,
                    CharSize = charSize,
                    HasExtendedSizeHeader = hasExtendedHeader,
                    IntSize = intSize,
                    FloatSize = floatSize,
                    MainFunction = main,
                    TrailingBytesAfterTail = trailing
                };
                return true;
            }
            catch
            {
                stream = null;
                return false;
            }
        }

        public static byte[] Serialize(NutbStream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            using var ms = new MemoryStream(4096);
            using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
            {
                bw.Write(NutbScriptAuditor.SqBytecodeStreamTag);
                bw.Write(stream.SqirMagic);
                bw.Write(stream.CharSize);
                if (stream.HasExtendedSizeHeader)
                {
                    bw.Write(stream.IntSize);
                    bw.Write(stream.FloatSize);
                }

                WriteFunction(bw, stream.MainFunction, stream.CharSize);
                bw.Write(TailMagicLe);
                if (stream.TrailingBytesAfterTail != null && stream.TrailingBytesAfterTail.Length > 0)
                {
                    bw.Write(stream.TrailingBytesAfterTail);
                }
            }

            byte[] rawBytecode = ms.ToArray();
            return stream.ContainerKind switch
            {
                NutbOuterContainerKind.Level5Lz10 => new LZ10().Compress(rawBytecode),
                NutbOuterContainerKind.Level5NoCompression => new NoCompression().Compress(rawBytecode),
                _ => rawBytecode
            };
        }

        /// <summary>
        /// Walks Squirrel instructions in <paramref name="stream"/> to extract exact call-site arguments
        /// (including integer character model targets in <c>CMND_BTL_SET_PURPOSE</c>, <c>CreateEv*ForNumber</c>,
        /// <c>SetChrPrmDetailForSkillScript</c>, and exact skill/director calls).
        /// </summary>
        public static void AnalyzeFunctionTree(
            NutbFunctionProto root,
            string virtualPath,
            NutbAuditReport report)
        {
            if (root == null || report == null)
                return;

            EncodingInitializer.EnsureRegistered();
            Encoding shiftJis = Encoding.GetEncoding("Shift-JIS");
            AnalyzeFunctionRecursive(root, virtualPath ?? string.Empty, report, shiftJis);
        }

        /// <summary>
        /// Applies Dynamic Story-Technique Swaps and Story Coach remaps across all <c>.nutb</c> scripts
        /// in <paramref name="archiveRoot"/> without mutating <c>RunSkillScript</c> cutscene animations.
        /// </summary>
        public static NutbPatchSummary PatchArchiveStorySkillsAndDirectors(
            VirtualDirectory archiveRoot,
            IReadOnlyDictionary<(string CharaParamCode, string VanillaSkillCode), string> storySkillRemap,
            IReadOnlyDictionary<string, string> directorItemRemap,
            bool injectTemporarySkillForBattleUse = true)
        {
            var summary = new NutbPatchSummary();
            if (archiveRoot == null)
                return summary;

            EncodingInitializer.EnsureRegistered();
            Encoding shiftJis = Encoding.GetEncoding("Shift-JIS");

            WalkAndPatchDirectory(
                archiveRoot,
                string.Empty,
                storySkillRemap ?? new Dictionary<(string, string), string>(),
                directorItemRemap ?? new Dictionary<string, string>(),
                injectTemporarySkillForBattleUse,
                shiftJis,
                summary);

            return summary;
        }

        private static void WalkAndPatchDirectory(
            VirtualDirectory dir,
            string currentPrefix,
            IReadOnlyDictionary<(string CharaParamCode, string VanillaSkillCode), string> storySkillRemap,
            IReadOnlyDictionary<string, string> directorItemRemap,
            bool injectTemporarySkillForBattleUse,
            Encoding shiftJis,
            NutbPatchSummary summary)
        {
            foreach (var kvp in dir.Files.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!kvp.Key.EndsWith(".nutb", StringComparison.OrdinalIgnoreCase))
                    continue;

                string vPath = currentPrefix + "/" + kvp.Key;
                byte[] rawBytes = kvp.Value.ByteContent;
                if (rawBytes == null)
                {
                    kvp.Value.Read();
                    rawBytes = kvp.Value.ByteContent;
                }

                if (rawBytes == null || !TryParse(rawBytes, out NutbStream parsed))
                    continue;

                bool fileModified = PatchFunctionRecursive(
                    parsed.MainFunction,
                    storySkillRemap,
                    directorItemRemap,
                    injectTemporarySkillForBattleUse,
                    shiftJis,
                    summary);

                if (fileModified)
                {
                    byte[] updatedBytes = Serialize(parsed);
                    Arc0SafeArchive.WriteFileAtPath(dir, kvp.Key, updatedBytes);
                    summary.PatchedFilesCount++;
                }
            }

            foreach (VirtualDirectory sub in dir.Folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                WalkAndPatchDirectory(
                    sub,
                    currentPrefix + "/" + sub.Name,
                    storySkillRemap,
                    directorItemRemap,
                    injectTemporarySkillForBattleUse,
                    shiftJis,
                    summary);
            }
        }

        private readonly struct StackSlotProvenance
        {
            public bool IsKnown { get; init; }
            public bool IsStringLiteral { get; init; }
            public bool IsIntValue { get; init; }
            public string StringText { get; init; }
            public int IntValue { get; init; }
            public int ProducerIp { get; init; }
            public int LiteralIndex { get; init; }
            public bool IsDLoadSecondOperand { get; init; }
            public string PrepCallCallee { get; init; }
        }

        private static void AnalyzeFunctionRecursive(
            NutbFunctionProto fn,
            string virtualPath,
            NutbAuditReport report,
            Encoding shiftJis)
        {
            var stack = new StackSlotProvenance[Math.Max(256, fn.StackSize + 8)];
            string fnName = fn.Name.DecodeString(shiftJis);

            for (int ip = 0; ip < fn.Instructions.Count; ip++)
            {
                NutbInstruction ins = fn.Instructions[ip];
                UpdateStackProvenance(fn, ip, ins, stack, shiftJis);

                if (ins.Op != NutbOpcode.Call && ins.Op != NutbOpcode.TailCall)
                    continue;

                string callee = stack[ins.Arg1].PrepCallCallee;
                if (string.IsNullOrEmpty(callee))
                    continue;

                int argCount = Math.Max(0, ins.Arg3 - 1);
                StackSlotProvenance GetArg(int idx) =>
                    idx >= 0 && idx < argCount && (ins.Arg2 + 1 + idx) < stack.Length
                        ? stack[ins.Arg2 + 1 + idx]
                        : default;

                // 1. CMND_BTL_SET_PURPOSE(kind, iconChara, iconVariant, timeLimit, targetChara)
                if (callee == "CMND_BTL_SET_PURPOSE" && argCount >= 2)
                {
                    var kindArg = GetArg(0);
                    var iconArg = GetArg(1);
                    if (iconArg.IsIntValue && iconArg.IntValue >= 1 && iconArg.IntValue <= 2500 &&
                        kindArg.IsIntValue && kindArg.IntValue != 4 && kindArg.IntValue != 9)
                    {
                        string cp = $"cp{iconArg.IntValue:D4}";
                        report.ReferencedCharacterIds.Add(cp);
                        report.ReferencedBattleCharacterIds.Add(cp);
                    }

                    if (kindArg.IsIntValue && (kindArg.IntValue == 1 || kindArg.IntValue == 5) && argCount >= 5)
                    {
                        var targetArg = GetArg(4);
                        if (targetArg.IsIntValue && targetArg.IntValue >= 1 && targetArg.IntValue <= 2500)
                        {
                            string cp = $"cp{targetArg.IntValue:D4}";
                            string para = "para_" + cp;
                            report.ReferencedCharacterIds.Add(para);
                            report.ReferencedBattleCharacterIds.Add(para);
                        }
                    }
                }
                // 2. CreateEvHiMdlForNumber / CreateEvLowMdlForNumber / SetChrPrmDetailForSkillScript
                else if ((callee == "CreateEvHiMdlForNumber" || callee == "CreateEvLowMdlForNumber" ||
                          callee == "CreateEvLowMdlForNumberNoBustup") && argCount >= 2)
                {
                    var numArg = GetArg(1);
                    if (numArg.IsIntValue && numArg.IntValue >= 1 && numArg.IntValue <= 2500)
                    {
                        string cp = $"cp{numArg.IntValue:D4}";
                        report.ReferencedCharacterIds.Add(cp);
                        report.ReferencedCutsceneModelIds.Add(cp);
                    }
                }
                else if ((callee == "CreateEvHiMdlForCharaNo_NPCOnly" || callee == "CreateEvLowMdlForCharaNo_NPCOnly" ||
                          callee == "CreateEvLowMdlForCharaNo_NPCOnlyNoBustup") && argCount >= 2)
                {
                    var npcArg = GetArg(1);
                    if (npcArg.IsIntValue && npcArg.IntValue >= 1 && npcArg.IntValue <= 2500)
                    {
                        string cn = $"cn{npcArg.IntValue:D4}";
                        report.ReferencedCharacterIds.Add(cn);
                        report.ReferencedCutsceneModelIds.Add(cn);
                    }
                }
                else if (callee == "SetChrPrmDetailForSkillScript" && argCount >= 3)
                {
                    var numArg = GetArg(2);
                    if (numArg.IsIntValue && numArg.IntValue >= 1 && numArg.IntValue <= 2500)
                    {
                        string cp = $"cp{numArg.IntValue:D4}";
                        report.ReferencedCharacterIds.Add(cp);
                        report.ReferencedCutsceneModelIds.Add(cp);
                    }
                }
                // 3. Story Skill Call-Sites (Unlock / BattleLearn / BattleForget / BattleBan / BattleUse)
                else if (TryClassifySkillCall(callee, argCount, GetArg, out NutbSkillCallKind kind, out int? side, out string charaId, out string skillId))
                {
                    report.SkillCallSites.Add(new NutbSkillCallSiteRecord
                    {
                        VirtualPath = virtualPath,
                        FunctionName = fnName,
                        CalleeName = callee,
                        Kind = kind,
                        Side = side,
                        CharaId = NormalizeParaCp(charaId),
                        SkillId = skillId.ToLowerInvariant()
                    });
                }
                // 4. Story Coach Item Awards (CMND_ADD_SET_DIRECTOR / CMND_SET_DIRECTOR)
                else if ((callee == "CMND_ADD_SET_DIRECTOR" || callee == "CMND_SET_DIRECTOR") && argCount >= 1)
                {
                    var dirArg = GetArg(0);
                    if (dirArg.IsStringLiteral && dirArg.StringText.StartsWith("iim", StringComparison.OrdinalIgnoreCase))
                    {
                        report.StoryDirectorItemIds.Add(dirArg.StringText.ToLowerInvariant());
                    }
                }
            }

            foreach (NutbFunctionProto child in fn.Functions)
            {
                AnalyzeFunctionRecursive(child, virtualPath, report, shiftJis);
            }
        }

        private static bool PatchFunctionRecursive(
            NutbFunctionProto fn,
            IReadOnlyDictionary<(string CharaParamCode, string VanillaSkillCode), string> storySkillRemap,
            IReadOnlyDictionary<string, string> directorItemRemap,
            bool injectTemporarySkillForBattleUse,
            Encoding shiftJis,
            NutbPatchSummary summary)
        {
            bool modified = false;

            // Pass 1: Remap argument literals at exact call-sites (never mutating shared literals used by RunSkillScript!)
            var stack = new StackSlotProvenance[Math.Max(256, fn.StackSize + 16)];
            var pendingBattleUseInjections = new List<(int CallPrepIp, int Side, string CharaCode, string SkillCode, byte ScratchBaseSlot)>();

            for (int ip = 0; ip < fn.Instructions.Count; ip++)
            {
                NutbInstruction ins = fn.Instructions[ip];
                UpdateStackProvenance(fn, ip, ins, stack, shiftJis);

                if (ins.Op != NutbOpcode.Call && ins.Op != NutbOpcode.TailCall)
                    continue;

                string callee = stack[ins.Arg1].PrepCallCallee;
                if (string.IsNullOrEmpty(callee))
                    continue;

                int argCount = Math.Max(0, ins.Arg3 - 1);
                StackSlotProvenance GetArg(int idx) =>
                    idx >= 0 && idx < argCount && (ins.Arg2 + 1 + idx) < stack.Length
                        ? stack[ins.Arg2 + 1 + idx]
                        : default;

                // Director item remap on CMND_ADD_SET_DIRECTOR / CMND_SET_DIRECTOR
                if ((callee == "CMND_ADD_SET_DIRECTOR" || callee == "CMND_SET_DIRECTOR") && argCount >= 1)
                {
                    var dirArg = GetArg(0);
                    if (dirArg.IsStringLiteral &&
                        directorItemRemap.TryGetValue(dirArg.StringText.ToLowerInvariant(), out string newDirCode) &&
                        !string.Equals(dirArg.StringText, newDirCode, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryRedirectProducedLiteral(fn, ref ip, dirArg, newDirCode))
                        {
                            modified = true;
                            summary.PatchedDirectorCallsCount++;
                        }
                    }
                    continue;
                }

                if (!TryClassifySkillCall(
                        callee,
                        argCount,
                        GetArg,
                        out NutbSkillCallKind skillKind,
                        out int? side,
                        out string rawChara,
                        out string rawSkill,
                        out StackSlotProvenance skillSlotProv))
                {
                    continue;
                }

                string normChara = NormalizeParaCp(rawChara);
                string normSkill = rawSkill.ToLowerInvariant();
                string effectiveSkill = normSkill;

                if (storySkillRemap.TryGetValue((normChara, normSkill), out string mappedSkill) &&
                    !string.IsNullOrWhiteSpace(mappedSkill))
                {
                    effectiveSkill = mappedSkill.ToLowerInvariant();
                    if (!string.Equals(normSkill, effectiveSkill, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryRedirectProducedLiteral(fn, ref ip, skillSlotProv, effectiveSkill))
                        {
                            modified = true;
                            switch (skillKind)
                            {
                                case NutbSkillCallKind.Unlock: summary.PatchedUnlockCallsCount++; break;
                                case NutbSkillCallKind.BattleLearn: summary.PatchedBattleLearnCallsCount++; break;
                                case NutbSkillCallKind.BattleForget: summary.PatchedBattleForgetCallsCount++; break;
                                case NutbSkillCallKind.BattleBan: summary.PatchedBattleBanCallsCount++; break;
                            }
                        }
                    }
                }

                // Dynamic Story-Technique Swap for CMND_BTL_USE_SKILL(side, charaId, effectiveSkill):
                // Inject a temporary match-only CMND_BTL_LEARNING_SKILL(side, charaId, effectiveSkill, 0)
                // immediately before the CMND_BTL_USE_SKILL preparation sequence so the player always has
                // the randomized technique equipped on their temporary BattlePlayer pitch copy without
                // sacrificing their 100% randomized party moveset!
                if (injectTemporarySkillForBattleUse &&
                    skillKind == NutbSkillCallKind.BattleUse &&
                    side.HasValue)
                {
                    int prepIp = stack[ins.Arg1].ProducerIp;
                    byte scratchBase = (byte)Math.Min(245, Math.Max(fn.StackSize, ins.Arg2 + ins.Arg3));
                    if (prepIp >= 0 && prepIp <= ip && scratchBase + 6 <= 254)
                    {
                        pendingBattleUseInjections.Add((prepIp, side.Value, normChara, effectiveSkill, scratchBase));
                    }
                }
            }

            // Pass 2: Inject temporary CMND_BTL_LEARNING_SKILL sequences in reverse IP order
            for (int idx = pendingBattleUseInjections.Count - 1; idx >= 0; idx--)
            {
                var inj = pendingBattleUseInjections[idx];
                InjectBattleLearningSkillCall(fn, inj.CallPrepIp, inj.Side, inj.CharaCode, inj.SkillCode, inj.ScratchBaseSlot, shiftJis);
                modified = true;
                summary.InjectedTemporaryBattleSwapsCount++;
            }

            foreach (NutbFunctionProto child in fn.Functions)
            {
                if (PatchFunctionRecursive(child, storySkillRemap, directorItemRemap, injectTemporarySkillForBattleUse, shiftJis, summary))
                {
                    modified = true;
                }
            }

            return modified;
        }

        private static bool TryRedirectProducedLiteral(
            NutbFunctionProto fn,
            ref int currentCallIp,
            StackSlotProvenance prov,
            string newAsciiLiteral)
        {
            if (!prov.IsKnown || !prov.IsStringLiteral || prov.ProducerIp < 0 || prov.ProducerIp >= fn.Instructions.Count)
                return false;

            int newLitIdx = AddOrGetAsciiLiteral(fn, newAsciiLiteral);
            NutbInstruction prod = fn.Instructions[prov.ProducerIp];

            if (prod.Op == NutbOpcode.Load)
            {
                prod.Arg1 = newLitIdx;
                fn.Instructions[prov.ProducerIp] = prod;
                return true;
            }

            if (prod.Op == NutbOpcode.DLoad)
            {
                if (!prov.IsDLoadSecondOperand)
                {
                    prod.Arg1 = newLitIdx;
                    fn.Instructions[prov.ProducerIp] = prod;
                    return true;
                }

                if (newLitIdx <= byte.MaxValue)
                {
                    prod.Arg3 = (byte)newLitIdx;
                    fn.Instructions[prov.ProducerIp] = prod;
                    return true;
                }

                // Split OP_DLOAD(arg0, arg1, arg2, arg3) into two OP_LOAD instructions when newLitIdx > 255!
                var firstLoad = new NutbInstruction(NutbOpcode.Load, prod.Arg0, prod.Arg1);
                var secondLoad = new NutbInstruction(NutbOpcode.Load, prod.Arg2, newLitIdx);
                fn.Instructions[prov.ProducerIp] = firstLoad;
                InsertInstructionsWithRelocation(fn, prov.ProducerIp + 1, new[] { secondLoad });
                if (prov.ProducerIp + 1 <= currentCallIp)
                    currentCallIp++;
                return true;
            }

            return false;
        }

        private static void InjectBattleLearningSkillCall(
            NutbFunctionProto fn,
            int insertIp,
            int side,
            string charaCode,
            string skillCode,
            byte baseSlot,
            Encoding shiftJis)
        {
            int fnLit = AddOrGetStringLiteral(fn, "CMND_BTL_LEARNING_SKILL", shiftJis);
            int charaLit = AddOrGetAsciiLiteral(fn, charaCode);
            int skillLit = AddOrGetAsciiLiteral(fn, skillCode);

            // Squirrel call frame layout at baseSlot:
            // stack[baseSlot]     = closure (CMND_BTL_LEARNING_SKILL from root/env stack[0])
            // stack[baseSlot + 1] = this (populated by OP_PREPCALLK arg3)
            // stack[baseSlot + 2] = arg0: side (0 or 1)
            // stack[baseSlot + 3] = arg1: charaCode ("para_cpXXXX")
            // stack[baseSlot + 4] = arg2: skillCode ("whsYYYY")
            // stack[baseSlot + 5] = arg3: 0
            // OP_CALL(arg0: 0xFF, arg1: baseSlot, arg2: baseSlot + 1, arg3: 5 [this + 4 args])
            var seq = new[]
            {
                new NutbInstruction(NutbOpcode.PrepCallK, baseSlot, fnLit, 0, (byte)(baseSlot + 1)),
                new NutbInstruction(NutbOpcode.LoadInt, (byte)(baseSlot + 2), side),
                new NutbInstruction(NutbOpcode.Load, (byte)(baseSlot + 3), charaLit),
                new NutbInstruction(NutbOpcode.Load, (byte)(baseSlot + 4), skillLit),
                new NutbInstruction(NutbOpcode.LoadInt, (byte)(baseSlot + 5), 0),
                new NutbInstruction(NutbOpcode.Call, 0xFF, baseSlot, (byte)(baseSlot + 1), 5)
            };

            InsertInstructionsWithRelocation(fn, insertIp, seq);
            if (fn.StackSize < baseSlot + 6)
            {
                fn.StackSize = baseSlot + 6;
            }
        }

        private static void InsertInstructionsWithRelocation(
            NutbFunctionProto fn,
            int insertIndex,
            IReadOnlyList<NutbInstruction> toInsert)
        {
            int delta = toInsert.Count;
            if (delta <= 0)
                return;

            // Relocate existing relative jump offsets BEFORE inserting new instructions
            for (int oldIp = 0; oldIp < fn.Instructions.Count; oldIp++)
            {
                NutbInstruction ins = fn.Instructions[oldIp];
                if (!RelativeJumpOpcodes.Contains(ins.Op))
                    continue;

                int oldTarget = (oldIp + 1) + ins.Arg1;
                int newIp = oldIp >= insertIndex ? oldIp + delta : oldIp;
                int newTarget = oldTarget >= insertIndex ? oldTarget + delta : oldTarget;
                ins.Arg1 = newTarget - (newIp + 1);
                fn.Instructions[oldIp] = ins;
            }

            fn.Instructions.InsertRange(insertIndex, toInsert);

            for (int i = 0; i < fn.Locals.Count; i++)
            {
                var loc = fn.Locals[i];
                if (loc.StartOp >= insertIndex) loc.StartOp += delta;
                if (loc.EndOp >= insertIndex) loc.EndOp += delta;
            }

            for (int i = 0; i < fn.LineInfos.Count; i++)
            {
                var li = fn.LineInfos[i];
                if (li.Op >= insertIndex)
                {
                    li.Op += delta;
                    fn.LineInfos[i] = li;
                }
            }
        }

        private static int AddOrGetAsciiLiteral(NutbFunctionProto fn, string asciiValue)
        {
            byte[] target = Encoding.ASCII.GetBytes(asciiValue);
            // Always append a fresh literal when remapping call-sites so shared literals (e.g. RunSkillScript) stay untouched!
            fn.Literals.Add(new NutbValue
            {
                TypeTag = NutbValue.OtString,
                RawStringBytes = target
            });
            return fn.Literals.Count - 1;
        }

        private static int AddOrGetStringLiteral(NutbFunctionProto fn, string text, Encoding shiftJis)
        {
            for (int i = 0; i < fn.Literals.Count; i++)
            {
                if (fn.Literals[i].TypeTag == NutbValue.OtString &&
                    string.Equals(fn.Literals[i].DecodeString(shiftJis), text, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            fn.Literals.Add(NutbValue.FromAsciiString(text));
            return fn.Literals.Count - 1;
        }

        private static bool TryClassifySkillCall(
            string callee,
            int argCount,
            Func<int, StackSlotProvenance> getArg,
            out NutbSkillCallKind kind,
            out int? side,
            out string charaId,
            out string skillId)
        {
            return TryClassifySkillCall(callee, argCount, getArg, out kind, out side, out charaId, out skillId, out _);
        }

        private static bool TryClassifySkillCall(
            string callee,
            int argCount,
            Func<int, StackSlotProvenance> getArg,
            out NutbSkillCallKind kind,
            out int? side,
            out string charaId,
            out string skillId,
            out StackSlotProvenance skillSlotProv)
        {
            kind = default;
            side = null;
            charaId = string.Empty;
            skillId = string.Empty;
            skillSlotProv = default;

            // Unlock: RunUnlockSkill(_chr, _skill), RunUnlockSkillEx(_chr, _skill, ...),
            // RunUnlockSkillUseMsg(_chr, _skill, _msg), CMND_SKILL_UNLOCK(_chr, _skill, ...)
            if ((callee == "RunUnlockSkill" || callee == "RunUnlockSkillEx" ||
                 callee == "RunUnlockSkillUseMsg" || callee == "CMND_SKILL_UNLOCK") && argCount >= 2)
            {
                var c = getArg(0);
                var s = getArg(1);
                if (c.IsStringLiteral && IsPlayerToken(c.StringText) && s.IsStringLiteral && IsPlayableSkillToken(s.StringText))
                {
                    kind = NutbSkillCallKind.Unlock;
                    charaId = c.StringText;
                    skillId = s.StringText;
                    skillSlotProv = s;
                    return true;
                }
            }
            // BattleLearn / BattleForget / BattleUse: (side, charaId, skillId, ...)
            else if ((callee == "CMND_BTL_LEARNING_SKILL" || callee == "CMND_BTL_FORGET_SKILL" ||
                      callee == "CMND_BTL_USE_SKILL") && argCount >= 3)
            {
                var sideArg = getArg(0);
                var c = getArg(1);
                var s = getArg(2);
                if (c.IsStringLiteral && IsPlayerToken(c.StringText) && s.IsStringLiteral && IsPlayableSkillToken(s.StringText))
                {
                    kind = callee switch
                    {
                        "CMND_BTL_LEARNING_SKILL" => NutbSkillCallKind.BattleLearn,
                        "CMND_BTL_FORGET_SKILL" => NutbSkillCallKind.BattleForget,
                        _ => NutbSkillCallKind.BattleUse
                    };
                    side = sideArg.IsIntValue ? sideArg.IntValue : null;
                    charaId = c.StringText;
                    skillId = s.StringText;
                    skillSlotProv = s;
                    return true;
                }
            }
            // BattleBan: CMND_BTL_SET_SKILL_BAN(banFlag, charaId, skillId)
            else if (callee == "CMND_BTL_SET_SKILL_BAN" && argCount >= 3)
            {
                var c = getArg(1);
                var s = getArg(2);
                if (c.IsStringLiteral && IsPlayerToken(c.StringText) && s.IsStringLiteral && IsPlayableSkillToken(s.StringText))
                {
                    kind = NutbSkillCallKind.BattleBan;
                    charaId = c.StringText;
                    skillId = s.StringText;
                    skillSlotProv = s;
                    return true;
                }
            }

            return false;
        }

        private static bool IsPlayerToken(string text) =>
            !string.IsNullOrEmpty(text) &&
            (text.StartsWith("para_cp", StringComparison.OrdinalIgnoreCase) ||
             text.StartsWith("cp", StringComparison.OrdinalIgnoreCase));

        private static bool IsPlayableSkillToken(string text) =>
            !string.IsNullOrEmpty(text) &&
            text.Length == 7 &&
            text.StartsWith("wh", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeParaCp(string token)
        {
            string lower = token.Trim().ToLowerInvariant();
            return lower.StartsWith("para_", StringComparison.Ordinal) ? lower : "para_" + lower;
        }

        private static void UpdateStackProvenance(
            NutbFunctionProto fn,
            int ip,
            NutbInstruction ins,
            StackSlotProvenance[] stack,
            Encoding shiftJis)
        {
            switch (ins.Op)
            {
                case NutbOpcode.Load:
                    if (ins.Arg1 >= 0 && ins.Arg1 < fn.Literals.Count)
                    {
                        var lit = fn.Literals[ins.Arg1];
                        stack[ins.Arg0] = BuildLiteralProvenance(lit, ip, ins.Arg1, isDLoadSecond: false, shiftJis);
                    }
                    else
                    {
                        stack[ins.Arg0] = default;
                    }
                    break;

                case NutbOpcode.DLoad:
                    if (ins.Arg1 >= 0 && ins.Arg1 < fn.Literals.Count)
                        stack[ins.Arg0] = BuildLiteralProvenance(fn.Literals[ins.Arg1], ip, ins.Arg1, isDLoadSecond: false, shiftJis);
                    else
                        stack[ins.Arg0] = default;

                    if (ins.Arg3 < fn.Literals.Count)
                        stack[ins.Arg2] = BuildLiteralProvenance(fn.Literals[ins.Arg3], ip, ins.Arg3, isDLoadSecond: true, shiftJis);
                    else
                        stack[ins.Arg2] = default;
                    break;

                case NutbOpcode.LoadInt:
                    stack[ins.Arg0] = new StackSlotProvenance
                    {
                        IsKnown = true,
                        IsIntValue = true,
                        IntValue = ins.Arg1,
                        ProducerIp = ip,
                        LiteralIndex = -1
                    };
                    break;

                case NutbOpcode.LoadBool:
                    stack[ins.Arg0] = new StackSlotProvenance
                    {
                        IsKnown = true,
                        IsIntValue = true,
                        IntValue = ins.Arg1 != 0 ? 1 : 0,
                        ProducerIp = ip,
                        LiteralIndex = -1
                    };
                    break;

                case NutbOpcode.Move:
                    if (ins.Arg1 >= 0 && ins.Arg1 < stack.Length)
                        stack[ins.Arg0] = stack[ins.Arg1];
                    else
                        stack[ins.Arg0] = default;
                    break;

                case NutbOpcode.DMove:
                    if (ins.Arg1 >= 0 && ins.Arg1 < stack.Length)
                        stack[ins.Arg0] = stack[ins.Arg1];
                    else
                        stack[ins.Arg0] = default;

                    if (ins.Arg3 < stack.Length)
                        stack[ins.Arg2] = stack[ins.Arg3];
                    else
                        stack[ins.Arg2] = default;
                    break;

                case NutbOpcode.PrepCallK:
                    if (ins.Arg1 >= 0 && ins.Arg1 < fn.Literals.Count && fn.Literals[ins.Arg1].TypeTag == NutbValue.OtString)
                    {
                        stack[ins.Arg0] = new StackSlotProvenance
                        {
                            IsKnown = true,
                            PrepCallCallee = fn.Literals[ins.Arg1].DecodeString(shiftJis),
                            ProducerIp = ip,
                            LiteralIndex = ins.Arg1
                        };
                    }
                    else
                    {
                        stack[ins.Arg0] = default;
                    }
                    if (ins.Arg3 < stack.Length)
                        stack[ins.Arg3] = default;
                    break;

                case NutbOpcode.PrepCall:
                    stack[ins.Arg0] = (ins.Arg1 >= 0 && ins.Arg1 < stack.Length && stack[ins.Arg1].IsStringLiteral)
                        ? new StackSlotProvenance
                        {
                            IsKnown = true,
                            PrepCallCallee = stack[ins.Arg1].StringText,
                            ProducerIp = ip
                        }
                        : default;
                    if (ins.Arg3 < stack.Length)
                        stack[ins.Arg3] = default;
                    break;

                case NutbOpcode.LoadNulls:
                    for (int n = 0; n < ins.Arg1 && (ins.Arg0 + n) < stack.Length; n++)
                        stack[ins.Arg0 + n] = default;
                    break;

                default:
                    if (ins.Arg0 != 0xFF && ins.Arg0 < stack.Length &&
                        ins.Op != NutbOpcode.Line && ins.Op != NutbOpcode.Jmp &&
                        ins.Op != NutbOpcode.Jz && ins.Op != NutbOpcode.JnzOrJcmp &&
                        ins.Op != NutbOpcode.Return)
                    {
                        stack[ins.Arg0] = default;
                    }
                    break;
            }
        }

        private static StackSlotProvenance BuildLiteralProvenance(
            NutbValue lit,
            int ip,
            int litIdx,
            bool isDLoadSecond,
            Encoding shiftJis)
        {
            if (lit.TypeTag == NutbValue.OtString)
            {
                return new StackSlotProvenance
                {
                    IsKnown = true,
                    IsStringLiteral = true,
                    StringText = lit.DecodeString(shiftJis),
                    ProducerIp = ip,
                    LiteralIndex = litIdx,
                    IsDLoadSecondOperand = isDLoadSecond
                };
            }

            if (lit.TypeTag == NutbValue.OtInteger || lit.TypeTag == NutbValue.OtBool)
            {
                return new StackSlotProvenance
                {
                    IsKnown = true,
                    IsIntValue = true,
                    IntValue = lit.IntValue,
                    ProducerIp = ip,
                    LiteralIndex = litIdx,
                    IsDLoadSecondOperand = isDLoadSecond
                };
            }

            return default;
        }

        private static bool TryReadFunction(
            byte[] data,
            ref int pos,
            int charSize,
            bool isExtendedHeader,
            out NutbFunctionProto fn)
        {
            fn = null;
            if (pos + 4 > data.Length || ReadUInt32(data, ref pos) != PartMagicLe)
                return false;

            if (!TryReadValue(data, ref pos, charSize, out NutbValue sourceName) ||
                !TryReadValue(data, ref pos, charSize, out NutbValue funcName))
                return false;

            if (pos + 4 > data.Length || ReadUInt32(data, ref pos) != PartMagicLe)
                return false;

            // Detect 7-count (Squirrel 2.2.0..2.2.2) vs 8-count (Squirrel 2.2.3/2.2.4/3.x with DefaultParams)
            bool hasDefaultParams;
            if (pos + 32 <= data.Length && BitConverter.ToUInt32(data, pos + 28) == PartMagicLe &&
                (!isExtendedHeader || (pos + 36 <= data.Length && BitConverter.ToUInt32(data, pos + 32) != PartMagicLe)))
            {
                hasDefaultParams = false;
            }
            else if (pos + 36 <= data.Length && BitConverter.ToUInt32(data, pos + 32) == PartMagicLe)
            {
                hasDefaultParams = true;
            }
            else
            {
                return false;
            }

            int nLiterals = ReadInt32(data, ref pos);
            int nParameters = ReadInt32(data, ref pos);
            int nOuterValues = ReadInt32(data, ref pos);
            int nLocalVarInfos = ReadInt32(data, ref pos);
            int nLineInfos = ReadInt32(data, ref pos);
            int nDefaultParams = hasDefaultParams ? ReadInt32(data, ref pos) : 0;
            int nInstructions = ReadInt32(data, ref pos);
            int nFunctions = ReadInt32(data, ref pos);

            if (nLiterals < 0 || nParameters < 0 || nOuterValues < 0 || nLocalVarInfos < 0 ||
                nLineInfos < 0 || nDefaultParams < 0 || nInstructions < 0 || nFunctions < 0)
                return false;

            var proto = new NutbFunctionProto
            {
                SourceName = sourceName,
                Name = funcName,
                HasDefaultParamsSection = hasDefaultParams
            };

            // Literals
            if (ReadUInt32(data, ref pos) != PartMagicLe) return false;
            for (int i = 0; i < nLiterals; i++)
            {
                if (!TryReadValue(data, ref pos, charSize, out NutbValue lit)) return false;
                proto.Literals.Add(lit);
            }

            // Parameters
            if (ReadUInt32(data, ref pos) != PartMagicLe) return false;
            for (int i = 0; i < nParameters; i++)
            {
                if (!TryReadValue(data, ref pos, charSize, out NutbValue param)) return false;
                proto.Parameters.Add(param);
            }

            // OuterValues
            if (ReadUInt32(data, ref pos) != PartMagicLe) return false;
            for (int i = 0; i < nOuterValues; i++)
            {
                int ovType = ReadInt32(data, ref pos);
                if (!TryReadValue(data, ref pos, charSize, out NutbValue ovSrc) ||
                    !TryReadValue(data, ref pos, charSize, out NutbValue ovName))
                    return false;
                proto.OuterValues.Add(new NutbOuterVar { OuterType = ovType, Source = ovSrc, Name = ovName });
            }

            // Locals
            if (ReadUInt32(data, ref pos) != PartMagicLe) return false;
            for (int i = 0; i < nLocalVarInfos; i++)
            {
                if (!TryReadValue(data, ref pos, charSize, out NutbValue locName)) return false;
                int locPos = ReadInt32(data, ref pos);
                int startOp = ReadInt32(data, ref pos);
                int endOp = ReadInt32(data, ref pos);
                proto.Locals.Add(new NutbLocalVarInfo { Name = locName, Pos = locPos, StartOp = startOp, EndOp = endOp });
            }

            // LineInfos
            if (ReadUInt32(data, ref pos) != PartMagicLe) return false;
            if (pos + (long)nLineInfos * 8 > data.Length) return false;
            for (int i = 0; i < nLineInfos; i++)
            {
                proto.LineInfos.Add(new NutbLineInfo
                {
                    Line = ReadInt32(data, ref pos),
                    Op = ReadInt32(data, ref pos)
                });
            }

            // DefaultParams (when present)
            if (hasDefaultParams)
            {
                if (ReadUInt32(data, ref pos) != PartMagicLe) return false;
                if (pos + (long)nDefaultParams * 4 > data.Length) return false;
                for (int i = 0; i < nDefaultParams; i++)
                {
                    proto.DefaultParams.Add(ReadInt32(data, ref pos));
                }
            }

            // Instructions
            if (ReadUInt32(data, ref pos) != PartMagicLe) return false;
            if (pos + (long)nInstructions * 8 > data.Length) return false;
            for (int i = 0; i < nInstructions; i++)
            {
                int arg1 = ReadInt32(data, ref pos);
                var op = (NutbOpcode)data[pos++];
                byte arg0 = data[pos++];
                byte arg2 = data[pos++];
                byte arg3 = data[pos++];
                proto.Instructions.Add(new NutbInstruction(op, arg0, arg1, arg2, arg3));
            }

            // Child Functions
            if (ReadUInt32(data, ref pos) != PartMagicLe) return false;
            for (int i = 0; i < nFunctions; i++)
            {
                if (!TryReadFunction(data, ref pos, charSize, isExtendedHeader, out NutbFunctionProto child))
                    return false;
                proto.Functions.Add(child);
            }

            // Function trailer: StackSize (4) + IsGenerator (1) + VarParams (1 byte in Squirrel 2.x, 4 bytes in Squirrel 3.x)
            if (pos + 6 > data.Length) return false;
            proto.StackSize = ReadInt32(data, ref pos);
            proto.IsGenerator = data[pos++];

            // Probe next boundary ('PART' of sibling function or 'TAIL' at end of root)
            bool oneByteMatchesBoundary = pos + 5 <= data.Length &&
                (BitConverter.ToUInt32(data, pos + 1) == PartMagicLe ||
                 BitConverter.ToUInt32(data, pos + 1) == TailMagicLe);
            bool fourByteMatchesBoundary = pos + 8 <= data.Length &&
                (BitConverter.ToUInt32(data, pos + 4) == PartMagicLe ||
                 BitConverter.ToUInt32(data, pos + 4) == TailMagicLe);

            if (oneByteMatchesBoundary && (!fourByteMatchesBoundary || !isExtendedHeader))
            {
                proto.VarParamsByteSize = 1;
                proto.VarParams = data[pos++];
            }
            else if (fourByteMatchesBoundary)
            {
                proto.VarParamsByteSize = 4;
                proto.VarParams = ReadInt32(data, ref pos);
            }
            else
            {
                return false;
            }

            fn = proto;
            return true;
        }

        private static bool TryReadValue(byte[] data, ref int pos, int charSize, out NutbValue val)
        {
            val = null;
            if (pos + 4 > data.Length)
                return false;

            uint tag = ReadUInt32(data, ref pos);
            switch (tag)
            {
                case NutbValue.OtNull:
                    val = NutbValue.Null();
                    return true;

                case NutbValue.OtInteger:
                case NutbValue.OtBool:
                    if (pos + 4 > data.Length) return false;
                    val = new NutbValue { TypeTag = tag, IntValue = ReadInt32(data, ref pos) };
                    return true;

                case NutbValue.OtFloat:
                    if (pos + 4 > data.Length) return false;
                    float fv = BitConverter.ToSingle(data, pos);
                    pos += 4;
                    val = NutbValue.FromFloat(fv);
                    return true;

                case NutbValue.OtString:
                    if (pos + 4 > data.Length) return false;
                    int len = ReadInt32(data, ref pos);
                    if (len < 0) return false;
                    long byteLen = (long)len * charSize;
                    if (pos + byteLen > data.Length) return false;
                    byte[] raw = byteLen > 0 ? data.AsSpan(pos, (int)byteLen).ToArray() : Array.Empty<byte>();
                    pos += (int)byteLen;
                    val = new NutbValue { TypeTag = tag, RawStringBytes = raw };
                    return true;

                default:
                    return false;
            }
        }

        private static void WriteFunction(BinaryWriter bw, NutbFunctionProto fn, int charSize)
        {
            bw.Write(PartMagicLe);
            WriteValue(bw, fn.SourceName, charSize);
            WriteValue(bw, fn.Name, charSize);

            bw.Write(PartMagicLe);
            bw.Write(fn.Literals.Count);
            bw.Write(fn.Parameters.Count);
            bw.Write(fn.OuterValues.Count);
            bw.Write(fn.Locals.Count);
            bw.Write(fn.LineInfos.Count);
            if (fn.HasDefaultParamsSection)
                bw.Write(fn.DefaultParams.Count);
            bw.Write(fn.Instructions.Count);
            bw.Write(fn.Functions.Count);

            bw.Write(PartMagicLe);
            foreach (var lit in fn.Literals)
                WriteValue(bw, lit, charSize);

            bw.Write(PartMagicLe);
            foreach (var p in fn.Parameters)
                WriteValue(bw, p, charSize);

            bw.Write(PartMagicLe);
            foreach (var ov in fn.OuterValues)
            {
                bw.Write(ov.OuterType);
                WriteValue(bw, ov.Source, charSize);
                WriteValue(bw, ov.Name, charSize);
            }

            bw.Write(PartMagicLe);
            foreach (var loc in fn.Locals)
            {
                WriteValue(bw, loc.Name, charSize);
                bw.Write(loc.Pos);
                bw.Write(loc.StartOp);
                bw.Write(loc.EndOp);
            }

            bw.Write(PartMagicLe);
            foreach (var li in fn.LineInfos)
            {
                bw.Write(li.Line);
                bw.Write(li.Op);
            }

            if (fn.HasDefaultParamsSection)
            {
                bw.Write(PartMagicLe);
                foreach (int dp in fn.DefaultParams)
                    bw.Write(dp);
            }

            bw.Write(PartMagicLe);
            foreach (var ins in fn.Instructions)
            {
                bw.Write(ins.Arg1);
                bw.Write((byte)ins.Op);
                bw.Write(ins.Arg0);
                bw.Write(ins.Arg2);
                bw.Write(ins.Arg3);
            }

            bw.Write(PartMagicLe);
            foreach (var child in fn.Functions)
                WriteFunction(bw, child, charSize);

            bw.Write(fn.StackSize);
            bw.Write(fn.IsGenerator);
            if (fn.VarParamsByteSize == 1)
                bw.Write((byte)fn.VarParams);
            else
                bw.Write(fn.VarParams);
        }

        private static void WriteValue(BinaryWriter bw, NutbValue val, int charSize)
        {
            bw.Write(val.TypeTag);
            switch (val.TypeTag)
            {
                case NutbValue.OtNull:
                    break;
                case NutbValue.OtInteger:
                case NutbValue.OtBool:
                    bw.Write(val.IntValue);
                    break;
                case NutbValue.OtFloat:
                    bw.Write(val.FloatValue);
                    break;
                case NutbValue.OtString:
                    byte[] raw = val.RawStringBytes ?? Array.Empty<byte>();
                    bw.Write(raw.Length / Math.Max(1, charSize));
                    if (raw.Length > 0)
                        bw.Write(raw);
                    break;
            }
        }

        private static ushort ReadUInt16(byte[] data, ref int pos)
        {
            ushort v = BitConverter.ToUInt16(data, pos);
            pos += 2;
            return v;
        }

        private static uint ReadUInt32(byte[] data, ref int pos)
        {
            uint v = BitConverter.ToUInt32(data, pos);
            pos += 4;
            return v;
        }

        private static int ReadInt32(byte[] data, ref int pos)
        {
            int v = BitConverter.ToInt32(data, pos);
            pos += 4;
            return v;
        }
    }
}
