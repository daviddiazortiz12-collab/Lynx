using System;
using System.Globalization;
using Lynx.Core.RomFs;
using StudioElevenLib.Level5.Binary.Logic;

namespace Lynx.Core.Binary
{
    /// <summary>
    /// Faithful parser and non-destructive updater for polymorphic Level-5 <c>CfgBin</c> variables,
    /// matching the Inazuma Eleven GO 1 3DS engine (<c>CScriptInterpreter3Ex</c> in <c>ie4cfgbin.cpp</c>):
    /// <list type="bullet">
    ///   <item><c>cfgBinArgReadHash</c> (<c>0x0012CFF0</c>): returns <c>(int32)*value</c> when tag is <c>CFGBIN_TYPE_INT</c>,
    ///   or <c>CRC32(utf8String)</c> (<c>0</c> for null/empty) when tag is <c>CFGBIN_TYPE_STRING</c>.</item>
    ///   <item><c>cfgBinArgReadInt</c> (<c>0x0012D07C</c>): reads strict numeric parameters.</item>
    ///   <item><c>cfgBinArgReadString</c> (<c>0x0012F8A0</c>): reads textual identifiers such as <c>"scr_br_0005_s"</c>.</item>
    /// </list>
    /// Never performs arbitrary lossy conversions or swallows malformed binary corruption.
    /// </summary>
    public static class Level5CfgValueParser
    {
        public static bool IsValidLevel5Identifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128)
                return false;

            char first = value[0];
            bool validStart = (first >= 'A' && first <= 'Z') ||
                              (first >= 'a' && first <= 'z') ||
                              first == '_';
            if (!validStart)
                return false;

            for (int i = 1; i < value.Length; i++)
            {
                char c = value[i];
                bool validChar = (c >= 'A' && c <= 'Z') ||
                                 (c >= 'a' && c <= 'z') ||
                                 (c >= '0' && c <= '9') ||
                                 c == '_' || c == '-' || c == '.' || c == '/' || c == ':';
                if (!validChar)
                    return false;
            }

            return true;
        }

        public static bool TryReadInt32(Variable variable, out int value)
        {
            value = 0;
            if (variable == null || variable.Value == null)
                return true;

            switch (variable.Value)
            {
                case int i:
                    value = i;
                    return true;
                case uint ui:
                    value = unchecked((int)ui);
                    return true;
                case short s:
                    value = s;
                    return true;
                case ushort us:
                    value = us;
                    return true;
                case byte b:
                    value = b;
                    return true;
                case sbyte sb:
                    value = sb;
                    return true;
                case long l when l >= int.MinValue && l <= uint.MaxValue:
                    value = unchecked((int)l);
                    return true;
                case float f:
                    value = (int)f;
                    return true;
                case double d:
                    value = (int)d;
                    return true;
                case string str:
                    if (str.Length == 0)
                    {
                        value = 0;
                        return true;
                    }
                    return TryParseNumericString(str, out value);
                default:
                    return false;
            }
        }

        public static int ReadInt32OrThrow(Variable variable, string context)
        {
            if (TryReadInt32(variable, out int value))
                return value;

            throw new FormatException(
                $"Invalid numeric CfgBin value '{variable?.Value}' (type={variable?.Type}) in {context}.");
        }

        /// <summary>
        /// Implements Level-5's <c>cfgBinArgReadHash</c> (<c>0x0012CFF0</c>): accepts integer IDs/CRC32s
        /// as well as valid Level-5 textual game identifiers (e.g. <c>"scr_br_0005_s"</c>, <c>"para_cp0008"</c>),
        /// returning both the exact CRC32/integer and the preserved textual identifier when present.
        /// </summary>
        public static bool TryReadHashOrId(Variable variable, out int hashOrId, out string textualIdentifier)
        {
            hashOrId = 0;
            textualIdentifier = null;

            if (variable == null || variable.Value == null)
            {
                textualIdentifier = string.Empty;
                return true;
            }

            if (variable.Value is string str)
            {
                if (str.Length == 0)
                {
                    textualIdentifier = string.Empty;
                    return true;
                }

                if (TryParseNumericString(str, out int numericValue))
                {
                    hashOrId = numericValue;
                    return true;
                }

                if (IsValidLevel5Identifier(str))
                {
                    textualIdentifier = str;
                    hashOrId = Level5IdHelper.ComputeCrc32Utf8(str);
                    return true;
                }

                return false;
            }

            return TryReadInt32(variable, out hashOrId);
        }

        public static int ReadHashOrIdOrThrow(Variable variable, string context, out string textualIdentifier)
        {
            if (TryReadHashOrId(variable, out int hashOrId, out textualIdentifier))
                return hashOrId;

            throw new FormatException(
                $"Invalid Level-5 identifier or hash '{variable?.Value}' (type={variable?.Type}) in {context}.");
        }

        public static bool TryReadSingle(Variable variable, out float value)
        {
            value = 0f;
            if (variable == null || variable.Value == null)
                return true;

            switch (variable.Value)
            {
                case float f:
                    value = f;
                    return true;
                case double d:
                    value = (float)d;
                    return true;
                case int i:
                    value = i;
                    return true;
                case uint ui:
                    value = ui;
                    return true;
                case short s:
                    value = s;
                    return true;
                case long l:
                    value = l;
                    return true;
                case string str:
                    if (str.Length == 0)
                    {
                        value = 0f;
                        return true;
                    }
                    return float.TryParse(
                        str,
                        NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture,
                        out value);
                default:
                    return false;
            }
        }

        public static float ReadSingleOrThrow(Variable variable, string context)
        {
            if (TryReadSingle(variable, out float value))
                return value;

            throw new FormatException(
                $"Invalid floating-point CfgBin value '{variable?.Value}' (type={variable?.Type}) in {context}.");
        }

        public static string ReadStringOrCondition(Variable variable)
        {
            if (variable == null || variable.Value == null)
                return string.Empty;

            if (variable.Value is string s)
                return s;

            if (TryReadInt32(variable, out int intVal))
                return intVal == 0 ? string.Empty : intVal.ToString(CultureInfo.InvariantCulture);

            return Convert.ToString(variable.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        /// <summary>
        /// Updates an integer/hash variable in place ONLY if the resolved hash/integer actually changed,
        /// preserving original <see cref="CfgValueType.String"/> or <see cref="CfgValueType.Int"/> tags
        /// on untouched values.
        /// </summary>
        public static void UpdateHashOrIntVariableInPlace(Variable variable, int newValue)
        {
            if (variable == null)
                throw new ArgumentNullException(nameof(variable));

            if (TryReadHashOrId(variable, out int existingValue, out _) && existingValue == newValue)
                return;

            variable.Type = CfgValueType.Int;
            variable.Value = newValue;
        }

        public static void UpdateSingleVariableInPlace(Variable variable, float newValue)
        {
            if (variable == null)
                throw new ArgumentNullException(nameof(variable));

            if (TryReadSingle(variable, out float existingValue) &&
                BitConverter.SingleToInt32Bits(existingValue) == BitConverter.SingleToInt32Bits(newValue))
            {
                return;
            }

            variable.Type = CfgValueType.Float;
            variable.Value = newValue;
        }

        private static bool TryParseNumericString(string str, out int value)
        {
            value = 0;
            ReadOnlySpan<char> span = str.AsSpan().Trim();
            if (span.IsEmpty)
                return false;

            if (span.StartsWith("0x".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                if (uint.TryParse(span.Slice(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hexVal))
                {
                    value = unchecked((int)hexVal);
                    return true;
                }
                return false;
            }

            if (span.StartsWith("-0x".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                if (uint.TryParse(span.Slice(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint negHex))
                {
                    value = unchecked(-(int)negHex);
                    return true;
                }
                return false;
            }

            if (long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longVal) &&
                longVal >= int.MinValue && longVal <= uint.MaxValue)
            {
                value = unchecked((int)longVal);
                return true;
            }

            return false;
        }
    }
}
