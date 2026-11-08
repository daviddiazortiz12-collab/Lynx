using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using StudioElevenLib.Level5.Binary;
using StudioElevenLib.Level5.Binary.Collections;
using StudioElevenLib.Level5.Binary.Logic;
using StudioElevenLib.Level5.Binary.Mapper;
using StudioElevenLib.Tools;

namespace Lynx.Core.Binary
{
    /// <summary>
    /// Polymorphic, non-destructive adapter for Level-5 <c>CfgBin&lt;CfgTreeNode&gt;</c> tables.
    /// Supports <c>cfgBinArgReadHash</c> (<c>0x0012CFF0</c>) string/CRC32 polymorphism when reading
    /// and updates <see cref="CfgTreeNode"/> variables in place when writing so original 2-bit
    /// <see cref="CfgValueType"/> tags, <c>null</c>/zero condition fields, file encodings, and any
    /// trailing retail columns remain untouched.
    /// </summary>
    public static class Level5CfgBinAdapter
    {
        public static List<T> ReadEntries<T>(CfgTreeNode root, string targetEntryName, string fileContext)
            where T : class, new()
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            var nodes = new List<CfgTreeNode>();
            CollectMatchingNodes(root, targetEntryName, nodes);

            PropertyInfo[] props = GetMappableProperties(typeof(T));
            var list = new List<T>(nodes.Count);

            for (int rowIndex = 0; rowIndex < nodes.Count; rowIndex++)
            {
                CfgTreeNode node = nodes[rowIndex];
                var vars = node.Item?.Variables;
                var instance = new T();

                if (vars != null)
                {
                    int limit = Math.Min(props.Length, vars.Count);
                    for (int col = 0; col < limit; col++)
                    {
                        PropertyInfo prop = props[col];
                        Variable variable = vars[col];
                        string ctx = $"{fileContext}:{targetEntryName}[{rowIndex}].{prop.Name}";

                        object converted = ReadPropertyValue(prop.PropertyType, variable, ctx);
                        prop.SetValue(instance, converted);
                    }
                }

                list.Add(instance);
            }

            return list;
        }

        public static void UpdateEntriesInPlace<T>(
            CfgTreeNode root,
            string beginEntryName,
            string childEntryName,
            IReadOnlyList<T> items,
            string fileContext) where T : class
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            if (items == null)
                throw new ArgumentNullException(nameof(items));

            CfgTreeNode beginNode = root.FindByName(beginEntryName)
                ?? throw new InvalidOperationException($"Section '{beginEntryName}' not found in {fileContext}.");

            var matchingChildren = beginNode.Children
                .OfType<CfgTreeNode>()
                .Where(c => c.Item?.Name == childEntryName)
                .ToList();

            if (matchingChildren.Count == items.Count)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    T item = items[i];
                    PropertyInfo[] props = GetMappableProperties(item.GetType());
                    var vars = matchingChildren[i].Item.Variables;

                    int limit = Math.Min(props.Length, vars.Count);
                    for (int col = 0; col < limit; col++)
                    {
                        PropertyInfo prop = props[col];
                        object newVal = prop.GetValue(item);
                        UpdateVariableFromProperty(vars[col], prop, newVal);
                    }
                }

                if (beginNode.Item.Variables.Count > 0)
                {
                    Level5CfgValueParser.UpdateHashOrIntVariableInPlace(beginNode.Item.Variables[0], items.Count);
                }
                return;
            }

            // Fallback only if entry count actually changed
            beginNode.Children.Clear();
            if (beginNode.Item.Variables.Count > 0)
            {
                beginNode.Item.Variables[0].Value = items.Count;
            }
            for (int i = 0; i < items.Count; i++)
            {
                Entry newEntry = new Entry(childEntryName, CfgBinMapper.GetVariablesFromInstance(items[i]));
                beginNode.AddChild(new CfgTreeNode(newEntry));
            }
        }

        private static object ReadPropertyValue(Type targetType, Variable variable, string context)
        {
            Type underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (underlying == typeof(int))
            {
                return Level5CfgValueParser.ReadHashOrIdOrThrow(variable, context, out _);
            }

            if (underlying == typeof(float))
            {
                return Level5CfgValueParser.ReadSingleOrThrow(variable, context);
            }

            if (underlying == typeof(string))
            {
                return Level5CfgValueParser.ReadStringOrCondition(variable);
            }

            if (underlying == typeof(object))
            {
                return variable?.Value;
            }

            if (variable?.Value == null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            return Convert.ChangeType(variable.Value, underlying, CultureInfo.InvariantCulture);
        }

        private static void UpdateVariableFromProperty(Variable variable, PropertyInfo prop, object newVal)
        {
            Type propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

            if (propType == typeof(int))
            {
                int intVal = newVal is int i ? i : 0;
                Level5CfgValueParser.UpdateHashOrIntVariableInPlace(variable, intVal);
                return;
            }

            if (propType == typeof(float))
            {
                float floatVal = newVal is float f ? f : 0f;
                Level5CfgValueParser.UpdateSingleVariableInPlace(variable, floatVal);
                return;
            }

            if (propType == typeof(string))
            {
                string nextStr = newVal as string ?? string.Empty;
                string currStr = Level5CfgValueParser.ReadStringOrCondition(variable);
                if (string.Equals(currStr, nextStr, StringComparison.Ordinal))
                    return;

                bool stringAsInt = prop.GetCustomAttribute<CfgBinStringAsIntAttribute>() != null;
                if (stringAsInt && string.IsNullOrEmpty(nextStr))
                {
                    variable.Type = CfgValueType.Int;
                    variable.Value = 0;
                }
                else
                {
                    variable.Type = CfgValueType.String;
                    variable.Value = nextStr;
                }
                return;
            }

            if (!Equals(variable.Value, newVal))
            {
                variable.Value = newVal;
            }
        }

        private static PropertyInfo[] GetMappableProperties(Type type)
        {
            return type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<CfgBinIgnoreAttribute>() == null)
                .OrderBy(p => p.MetadataToken)
                .ToArray();
        }

        private static void CollectMatchingNodes(CfgTreeNode current, string targetName, List<CfgTreeNode> output)
        {
            if (current.Item?.Name == targetName)
            {
                output.Add(current);
            }

            foreach (var child in current.Children)
            {
                if (child is CfgTreeNode cfgChild)
                {
                    CollectMatchingNodes(cfgChild, targetName, output);
                }
            }
        }
    }

    public sealed class RouteCellRecord
    {
        public int NodeIndex { get; init; }
        public int Flag { get; init; }
        public int CellType { get; init; }
        public int ContentID { get; set; }
        public string ContentIdentifier { get; init; }
        public int MatchRestriction { get; init; }
        public string MatchRestrictionIdentifier { get; init; }
        public int CellNum { get; init; }
        public int CellLink1 { get; init; }
        public int CellLink2 { get; init; }
        public int CellLink3 { get; init; }
        public string PhaseAppear { get; init; } = string.Empty;
        public string Map { get; init; } = string.Empty;
        public int Unk10 { get; init; }
        public string MatchTextLock { get; init; } = string.Empty;
    }

    public sealed class RouteFileData
    {
        public string FileName { get; init; } = string.Empty;
        public int RouteNameId { get; init; }
        public string RouteNameIdentifier { get; init; }
        public List<RouteCellRecord> Cells { get; } = new();
    }

    /// <summary>
    /// Non-destructive reader and in-place writer for Competition Route files
    /// (<c>/data/res/soccer/scr_br*.cfg.bin</c>, including <c>scr_br_0005_s.cfg.bin</c>).
    /// Preserves polymorphic <c>PhaseAppear</c>, <c>Map</c>, and <c>MatchTextLock</c> tags
    /// (<c>CfgValueType.Int 0</c>, <c>null</c>, or Base64 <c>CfgValueType.String</c>) verbatim.
    /// </summary>
    public static class RouteDataService
    {
        public static bool TryReadRouteFile(VirtualDirectory soccerDir, string fileName, out RouteFileData routeData)
        {
            routeData = null;
            if (soccerDir == null || string.IsNullOrWhiteSpace(fileName))
                return false;

            string cleanName = fileName.TrimStart('/');
            if (!soccerDir.Files.ContainsKey(cleanName))
                return false;

            byte[] bytes = soccerDir.GetFileFromFullPath("/" + cleanName);
            if (bytes == null || bytes.Length < 16)
                return false;

            var cfg = new CfgBin<CfgTreeNode>();
            cfg.Open(bytes);

            CfgTreeNode beginNode = cfg.Entries?.FindByName("ROUTE_CONFIG_BEGIN");
            if (beginNode == null)
                return false;

            int routeNameId = 0;
            string routeNameIdent = null;
            if (beginNode.Item?.Variables != null && beginNode.Item.Variables.Count >= 2)
            {
                routeNameId = Level5CfgValueParser.ReadHashOrIdOrThrow(
                    beginNode.Item.Variables[1],
                    $"{cleanName}:ROUTE_CONFIG_BEGIN[1]",
                    out routeNameIdent);
            }

            var result = new RouteFileData
            {
                FileName = cleanName,
                RouteNameId = routeNameId,
                RouteNameIdentifier = routeNameIdent
            };

            int idx = 0;
            foreach (var child in beginNode.Children.OfType<CfgTreeNode>())
            {
                if (child.Item?.Name != "ROUTE_CONFIG")
                    continue;

                var v = child.Item.Variables;
                if (v == null || v.Count < 5)
                {
                    idx++;
                    continue;
                }

                string ctx = $"{cleanName}:ROUTE_CONFIG[{idx}]";
                int flag = Level5CfgValueParser.ReadInt32OrThrow(v[0], ctx + ".Flag");
                int cellType = Level5CfgValueParser.ReadInt32OrThrow(v[1], ctx + ".CellType");
                int contentId = Level5CfgValueParser.ReadHashOrIdOrThrow(v[2], ctx + ".ContentID", out string contentIdent);
                int matchRestr = Level5CfgValueParser.ReadHashOrIdOrThrow(v[3], ctx + ".MatchRestriction", out string restrIdent);
                int cellNum = Level5CfgValueParser.ReadInt32OrThrow(v[4], ctx + ".CellNum");

                int link1 = v.Count > 5 ? Level5CfgValueParser.ReadInt32OrThrow(v[5], ctx + ".CellLink1") : -1;
                int link2 = v.Count > 6 ? Level5CfgValueParser.ReadInt32OrThrow(v[6], ctx + ".CellLink2") : -1;
                int link3 = v.Count > 7 ? Level5CfgValueParser.ReadInt32OrThrow(v[7], ctx + ".CellLink3") : -1;
                string phaseAppear = v.Count > 8 ? Level5CfgValueParser.ReadStringOrCondition(v[8]) : string.Empty;
                string map = v.Count > 9 ? Level5CfgValueParser.ReadStringOrCondition(v[9]) : string.Empty;
                int unk10 = v.Count > 10 ? Level5CfgValueParser.ReadInt32OrThrow(v[10], ctx + ".Unk10") : 0;
                string textLock = v.Count > 11 ? Level5CfgValueParser.ReadStringOrCondition(v[11]) : string.Empty;

                result.Cells.Add(new RouteCellRecord
                {
                    NodeIndex = idx,
                    Flag = flag,
                    CellType = cellType,
                    ContentID = contentId,
                    ContentIdentifier = contentIdent,
                    MatchRestriction = matchRestr,
                    MatchRestrictionIdentifier = restrIdent,
                    CellNum = cellNum,
                    CellLink1 = link1,
                    CellLink2 = link2,
                    CellLink3 = link3,
                    PhaseAppear = phaseAppear,
                    Map = map,
                    Unk10 = unk10,
                    MatchTextLock = textLock
                });

                idx++;
            }

            routeData = result;
            return true;
        }

        public static void UpdateRouteContentIdsInPlace(
            VirtualDirectory soccerDir,
            string fileName,
            IReadOnlyDictionary<int, int> updatedContentByNodeIndex)
        {
            if (soccerDir == null || updatedContentByNodeIndex == null || updatedContentByNodeIndex.Count == 0)
                return;

            string cleanName = fileName.TrimStart('/');
            byte[] bytes = soccerDir.GetFileFromFullPath("/" + cleanName);
            var cfg = new CfgBin<CfgTreeNode>();
            cfg.Open(bytes);

            CfgTreeNode beginNode = cfg.Entries?.FindByName("ROUTE_CONFIG_BEGIN")
                ?? throw new InvalidOperationException($"ROUTE_CONFIG_BEGIN missing in {cleanName}.");

            int idx = 0;
            foreach (var child in beginNode.Children.OfType<CfgTreeNode>())
            {
                if (child.Item?.Name != "ROUTE_CONFIG")
                    continue;

                if (updatedContentByNodeIndex.TryGetValue(idx, out int newContentId) &&
                    child.Item.Variables != null &&
                    child.Item.Variables.Count > 2)
                {
                    Level5CfgValueParser.UpdateHashOrIntVariableInPlace(child.Item.Variables[2], newContentId);
                }

                idx++;
            }

            soccerDir.Files[cleanName].ByteContent = cfg.Save();
        }
    }

    public sealed class ShopSlotRecord
    {
        public int NodeIndex { get; init; }
        public int ItemID { get; set; }
        public string ItemIdentifier { get; init; }
        public bool HasCondition { get; init; }
    }

    /// <summary>
    /// Non-destructive reader and in-place writer for <c>/data/res/shop/shop_*.cfg.bin</c>.
    /// Fixes Lynx's <c>"shop_shop_XX.cfg.bin"</c> prefix bug and <c>Condition == null</c>
    /// <see cref="NullReferenceException"/>, updating only <c>Variables[0]</c> (<c>ItemID</c>) in place.
    /// </summary>
    public static class ShopDataService
    {
        public static IReadOnlyList<string> ListShopFiles(VirtualDirectory shopDir)
        {
            if (shopDir == null)
                return Array.Empty<string>();

            return shopDir.Files.Keys
                .Where(f => f.StartsWith("shop_", StringComparison.OrdinalIgnoreCase) &&
                            f.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(f, "community_config.cfg.bin", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool TryReadShopFile(VirtualDirectory shopDir, string fileName, out List<ShopSlotRecord> slots)
        {
            slots = null;
            if (shopDir == null || string.IsNullOrWhiteSpace(fileName))
                return false;

            string cleanName = fileName.TrimStart('/');
            if (!shopDir.Files.ContainsKey(cleanName))
                return false;

            byte[] bytes = shopDir.GetFileFromFullPath("/" + cleanName);
            if (bytes == null || bytes.Length < 16)
                return false;

            var cfg = new CfgBin<CfgTreeNode>();
            cfg.Open(bytes);

            CfgTreeNode beginNode = cfg.Entries?.FindByName("SHOP_CONFIG_INFO_BEGIN");
            if (beginNode == null)
                return false;

            var result = new List<ShopSlotRecord>();
            int idx = 0;
            foreach (var child in beginNode.Children.OfType<CfgTreeNode>())
            {
                if (child.Item?.Name != "SHOP_CONFIG_INFO")
                    continue;

                var v = child.Item.Variables;
                if (v == null || v.Count < 1)
                {
                    idx++;
                    continue;
                }

                int itemId = Level5CfgValueParser.ReadHashOrIdOrThrow(
                    v[0],
                    $"{cleanName}:SHOP_CONFIG_INFO[{idx}].ItemID",
                    out string itemIdent);

                bool hasCond = v.Count > 1 &&
                               !string.IsNullOrEmpty(Level5CfgValueParser.ReadStringOrCondition(v[1]));

                result.Add(new ShopSlotRecord
                {
                    NodeIndex = idx,
                    ItemID = itemId,
                    ItemIdentifier = itemIdent,
                    HasCondition = hasCond
                });

                idx++;
            }

            slots = result;
            return true;
        }

        public static void UpdateShopItemsInPlace(
            VirtualDirectory shopDir,
            string fileName,
            IReadOnlyDictionary<int, int> updatedItemByNodeIndex)
        {
            if (shopDir == null || updatedItemByNodeIndex == null || updatedItemByNodeIndex.Count == 0)
                return;

            string cleanName = fileName.TrimStart('/');
            byte[] bytes = shopDir.GetFileFromFullPath("/" + cleanName);
            var cfg = new CfgBin<CfgTreeNode>();
            cfg.Open(bytes);

            CfgTreeNode beginNode = cfg.Entries?.FindByName("SHOP_CONFIG_INFO_BEGIN")
                ?? throw new InvalidOperationException($"SHOP_CONFIG_INFO_BEGIN missing in {cleanName}.");

            int idx = 0;
            foreach (var child in beginNode.Children.OfType<CfgTreeNode>())
            {
                if (child.Item?.Name != "SHOP_CONFIG_INFO")
                    continue;

                if (updatedItemByNodeIndex.TryGetValue(idx, out int newItemId) &&
                    child.Item.Variables != null &&
                    child.Item.Variables.Count > 0)
                {
                    Level5CfgValueParser.UpdateHashOrIntVariableInPlace(child.Item.Variables[0], newItemId);
                }

                idx++;
            }

            shopDir.Files[cleanName].ByteContent = cfg.Save();
        }
    }
}
