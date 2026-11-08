using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lynx.Core.RomFs;
using StudioElevenLib.Level5.Binary;
using StudioElevenLib.Level5.Binary.Collections;
using StudioElevenLib.Level5.Binary.Logic;
using StudioElevenLib.Tools;

namespace Lynx.Core.Maps
{
    /// <summary>
    /// Discovers, reads, and updates map-level treasure chests (<see cref="TBoxConfigRecord"/>),
    /// overworld pachanga spawns (<see cref="EncountMapRecord"/>), and map progression bit checks
    /// (<c>FUNCPOINT</c> <c>TBoxBitCheck</c> / <c>ItemID</c>) inside <c>ie_a.fa</c> without assuming
    /// hardcoded file names or dropping unrelated sections in multi-table CfgBin files.
    /// </summary>
    public static class MapDataService
    {
        private static readonly string[] MapRootCandidates = { "/data/map", "/data/res/map" };

        private static readonly HashSet<string> KnownTBoxEntryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "TBOX_CONFIG",
            "TBOX_INFO",
            "ONEPLACE_TBOX",
            "TREASURE_BOX"
        };

        private static readonly HashSet<string> KnownEncountEntryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "ENCOUNT_INFO",
            "ENCOUNT_CONFIG",
            "MAP_ENCOUNT_INFO"
        };

        public static List<MapTreasureChestEntry> LoadAllTreasureChests(VirtualDirectory archiveRoot)
        {
            var results = new List<MapTreasureChestEntry>();
            foreach (var (mapId, filePath, bytes) in EnumerateMapBinaryFiles(archiveRoot))
            {
                if (filePath.EndsWith("_funcpt.bin", StringComparison.OrdinalIgnoreCase) ||
                    filePath.EndsWith("_healpt.bin", StringComparison.OrdinalIgnoreCase) ||
                    filePath.EndsWith("_mapenv.bin", StringComparison.OrdinalIgnoreCase) ||
                    filePath.EndsWith(".npc.bin", StringComparison.OrdinalIgnoreCase) ||
                    filePath.EndsWith(".talk.bin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryOpenCfgTree(bytes, out var cfgBin))
                    continue;

                var nodes = new List<(string EntryName, CfgTreeNode Node)>();
                CollectMatchingNodes(cfgBin.Entries, IsTBoxNode, nodes);

                for (int i = 0; i < nodes.Count; i++)
                {
                    var (entryName, node) = nodes[i];
                    if (TryReadTBoxRecord(node, out var record))
                    {
                        results.Add(new MapTreasureChestEntry
                        {
                            MapId = mapId,
                            VirtualFilePath = filePath,
                            EntryName = entryName,
                            NodeIndex = i,
                            Record = record
                        });
                    }
                }
            }

            return results;
        }

        public static void SaveTreasureChests(VirtualDirectory archiveRoot, IEnumerable<MapTreasureChestEntry> modifiedEntries)
        {
            if (archiveRoot == null)
                throw new ArgumentNullException(nameof(archiveRoot));
            if (modifiedEntries == null)
                return;

            foreach (var group in modifiedEntries.GroupBy(e => e.VirtualFilePath, StringComparer.Ordinal))
            {
                byte[] currentBytes = archiveRoot.GetFileFromFullPath(group.Key);
                var cfgBin = new CfgBin<CfgTreeNode>();
                cfgBin.Open(currentBytes);

                var nodes = new List<(string EntryName, CfgTreeNode Node)>();
                CollectMatchingNodes(cfgBin.Entries, IsTBoxNode, nodes);

                foreach (var entry in group)
                {
                    if (entry.NodeIndex >= 0 && entry.NodeIndex < nodes.Count)
                    {
                        WriteTBoxRecordInPlace(nodes[entry.NodeIndex].Node, entry.Record);
                    }
                }

                Arc0SafeArchive.WriteFileAtPath(archiveRoot, group.Key, cfgBin.Save());
            }
        }

        public static List<MapEncounterSpawnEntry> LoadAllEncounterSpawns(VirtualDirectory archiveRoot)
        {
            var results = new List<MapEncounterSpawnEntry>();
            foreach (var (mapId, filePath, bytes) in EnumerateMapBinaryFiles(archiveRoot))
            {
                if (filePath.EndsWith("_funcpt.bin", StringComparison.OrdinalIgnoreCase) ||
                    filePath.EndsWith("_healpt.bin", StringComparison.OrdinalIgnoreCase) ||
                    filePath.EndsWith("_mapenv.bin", StringComparison.OrdinalIgnoreCase) ||
                    filePath.EndsWith(".npc.bin", StringComparison.OrdinalIgnoreCase) ||
                    filePath.EndsWith(".talk.bin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryOpenCfgTree(bytes, out var cfgBin))
                    continue;

                var nodes = new List<(string EntryName, CfgTreeNode Node)>();
                CollectMatchingNodes(cfgBin.Entries, IsEncountNode, nodes);

                for (int i = 0; i < nodes.Count; i++)
                {
                    var (entryName, node) = nodes[i];
                    if (TryReadEncountRecord(node, out var record))
                    {
                        results.Add(new MapEncounterSpawnEntry
                        {
                            MapId = mapId,
                            VirtualFilePath = filePath,
                            EntryName = entryName,
                            NodeIndex = i,
                            Record = record
                        });
                    }
                }
            }

            return results;
        }

        public static void SaveEncounterSpawns(VirtualDirectory archiveRoot, IEnumerable<MapEncounterSpawnEntry> modifiedEntries)
        {
            if (archiveRoot == null)
                throw new ArgumentNullException(nameof(archiveRoot));
            if (modifiedEntries == null)
                return;

            foreach (var group in modifiedEntries.GroupBy(e => e.VirtualFilePath, StringComparer.Ordinal))
            {
                byte[] currentBytes = archiveRoot.GetFileFromFullPath(group.Key);
                var cfgBin = new CfgBin<CfgTreeNode>();
                cfgBin.Open(currentBytes);

                var nodes = new List<(string EntryName, CfgTreeNode Node)>();
                CollectMatchingNodes(cfgBin.Entries, IsEncountNode, nodes);

                foreach (var entry in group)
                {
                    if (entry.NodeIndex >= 0 && entry.NodeIndex < nodes.Count)
                    {
                        WriteEncountRecordInPlace(nodes[entry.NodeIndex].Node, entry.Record);
                    }
                }

                Arc0SafeArchive.WriteFileAtPath(archiveRoot, group.Key, cfgBin.Save());
            }
        }

        public static MapProgressionDependencies ScanMapProgressionDependencies(VirtualDirectory archiveRoot)
        {
            var deps = new MapProgressionDependencies();
            foreach (var (_, filePath, bytes) in EnumerateMapBinaryFiles(archiveRoot))
            {
                if (filePath.EndsWith("_funcpt.bin", StringComparison.OrdinalIgnoreCase))
                {
                    ScanFuncpointFile(bytes, deps);
                }
                else if (filePath.EndsWith(".talk.bin", StringComparison.OrdinalIgnoreCase))
                {
                    ScanTalkFile(bytes, deps);
                }
            }
            return deps;
        }

        private static void ScanFuncpointFile(byte[] bytes, MapProgressionDependencies deps)
        {
            try
            {
                var ptree = new CfgBin<PtreeNode>();
                ptree.Open(bytes);
                WalkPtree(ptree.Entries, deps);
            }
            catch
            {
                // Ignore malformed or empty ptree files
            }
        }

        private static void WalkPtree(PtreeNode node, MapProgressionDependencies deps)
        {
            if (node == null)
                return;

            int? tboxBit = node.GetValueFromChild<int?>("TBoxBitCheck");
            if (tboxBit.HasValue)
            {
                deps.ProtectedTBoxFlags.Add(tboxBit.Value);
            }
            else if (string.Equals(node.Header, "TBoxBitCheck", StringComparison.OrdinalIgnoreCase))
            {
                int? headerVal = node.GetValue<int?>(1);
                if (headerVal.HasValue)
                    deps.ProtectedTBoxFlags.Add(headerVal.Value);
            }

            int? itemId = node.GetValueFromChild<int?>("ItemID");
            if (itemId.HasValue && itemId.Value != 0)
            {
                deps.ReferencedFuncptItemIds.Add(itemId.Value);
            }
            else if (string.Equals(node.Header, "ItemID", StringComparison.OrdinalIgnoreCase))
            {
                int? headerVal = node.GetValue<int?>(1);
                if (headerVal.HasValue && headerVal.Value != 0)
                    deps.ReferencedFuncptItemIds.Add(headerVal.Value);
            }

            foreach (var child in node.Children)
            {
                if (child is PtreeNode pChild)
                {
                    WalkPtree(pChild, deps);
                }
            }
        }

        private static void ScanTalkFile(byte[] bytes, MapProgressionDependencies deps)
        {
            if (!TryOpenCfgTree(bytes, out var cfgBin))
                return;

            var talkNodes = new List<(string EntryName, CfgTreeNode Node)>();
            CollectMatchingNodes(cfgBin.Entries, n => n.Item?.Name == "TALK_CONFIG", talkNodes);

            foreach (var (_, node) in talkNodes)
            {
                var vars = node.Item?.Variables;
                if (vars == null || vars.Count < 2)
                    continue;

                int talkType = Convert.ToInt32(vars[0].Value);
                int talkVal = Convert.ToInt32(vars[1].Value);
                if ((talkType == 2 || talkType == 3) && talkVal != 0)
                {
                    deps.ReferencedScriptHashes.Add(talkVal);
                }
                else if (talkType == 7 && talkVal != 0)
                {
                    deps.ReferencedRouteHashes.Add(talkVal);
                }
            }
        }

        private static IEnumerable<(string MapId, string VirtualPath, byte[] Bytes)> EnumerateMapBinaryFiles(VirtualDirectory archiveRoot)
        {
            if (archiveRoot == null)
                yield break;

            foreach (string mapRootPath in MapRootCandidates)
            {
                if (!archiveRoot.IsFolderExists(mapRootPath))
                    continue;

                VirtualDirectory mapRoot = archiveRoot.GetFolderFromFullPath(mapRootPath);
                foreach (VirtualDirectory mapFolder in mapRoot.Folders)
                {
                    string mapId = mapFolder.Name;
                    foreach (var kvp in mapFolder.Files)
                    {
                        if (!kvp.Key.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string fullPath = $"{mapRootPath}/{mapId}/{kvp.Key}";
                        byte[] data = archiveRoot.GetFileFromFullPath(fullPath);
                        if (data != null && data.Length >= 16)
                        {
                            yield return (mapId, fullPath, data);
                        }
                    }
                }
            }
        }

        private static bool TryOpenCfgTree(byte[] bytes, out CfgBin<CfgTreeNode> cfgBin)
        {
            cfgBin = null;
            try
            {
                var candidate = new CfgBin<CfgTreeNode>();
                candidate.Open(bytes);
                if (candidate.Entries != null && candidate.Entries.Children.Count > 0)
                {
                    cfgBin = candidate;
                    return true;
                }
            }
            catch
            {
                // Not a CfgTreeNode binary (e.g., PTree or raw mesh/collision binary)
            }
            return false;
        }

        private static bool IsTBoxNode(CfgTreeNode node)
        {
            string name = node.Item?.Name;
            if (string.IsNullOrEmpty(name))
                return false;
            if (name.EndsWith("_BEGIN", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_END", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (KnownTBoxEntryNames.Contains(name))
                return true;

            // Structural check matching Ocelot's 9-field TBoxConfig:
            // (int TBoxID, float X, float Z, float Y, float Rot, int ItemLight, int TBoxFlag, int TBoxType, int ItemShadow)
            var vars = node.Item?.Variables;
            if (vars != null && vars.Count == 9 && name.IndexOf("TBOX", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return vars[0].Type == CfgValueType.Int &&
                       vars[1].Type == CfgValueType.Float &&
                       vars[5].Type == CfgValueType.Int &&
                       vars[8].Type == CfgValueType.Int;
            }

            return false;
        }

        private static bool IsEncountNode(CfgTreeNode node)
        {
            string name = node.Item?.Name;
            if (string.IsNullOrEmpty(name))
                return false;
            if (name.EndsWith("_BEGIN", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_END", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (KnownEncountEntryNames.Contains(name))
                return node.Item?.Variables != null && node.Item.Variables.Count >= 20;

            return false;
        }

        private static void CollectMatchingNodes(
            CfgTreeNode current,
            Func<CfgTreeNode, bool> predicate,
            List<(string EntryName, CfgTreeNode Node)> output)
        {
            if (current == null)
                return;

            if (predicate(current))
            {
                output.Add((current.Item?.Name ?? string.Empty, current));
            }

            foreach (var child in current.Children)
            {
                if (child is CfgTreeNode cfgChild)
                {
                    CollectMatchingNodes(cfgChild, predicate, output);
                }
            }
        }

        private static bool TryReadTBoxRecord(CfgTreeNode node, out TBoxConfigRecord record)
        {
            record = null;
            var vars = node.Item?.Variables;
            if (vars == null || vars.Count < 9)
                return false;

            try
            {
                record = new TBoxConfigRecord
                {
                    TBoxID = Convert.ToInt32(vars[0].Value),
                    LocationX = Convert.ToSingle(vars[1].Value),
                    LocationZ = Convert.ToSingle(vars[2].Value),
                    LocationY = Convert.ToSingle(vars[3].Value),
                    Rotation = Convert.ToSingle(vars[4].Value),
                    ItemIDLight = Convert.ToInt32(vars[5].Value),
                    TBoxFlag = Convert.ToInt32(vars[6].Value),
                    TboxType = Convert.ToInt32(vars[7].Value),
                    ItemIDShadow = Convert.ToInt32(vars[8].Value)
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteTBoxRecordInPlace(CfgTreeNode node, TBoxConfigRecord record)
        {
            var vars = node.Item.Variables;
            vars[0].Value = record.TBoxID;
            vars[1].Value = record.LocationX;
            vars[2].Value = record.LocationZ;
            vars[3].Value = record.LocationY;
            vars[4].Value = record.Rotation;
            vars[5].Value = record.ItemIDLight;
            vars[6].Value = record.TBoxFlag;
            vars[7].Value = record.TboxType;
            vars[8].Value = record.ItemIDShadow;
        }

        private static bool TryReadEncountRecord(CfgTreeNode node, out EncountMapRecord record)
        {
            record = null;
            var vars = node.Item?.Variables;
            if (vars == null || vars.Count < 21)
                return false;

            try
            {
                record = new EncountMapRecord
                {
                    ChasePlayer = Convert.ToInt32(vars[0].Value),
                    TeamID = Convert.ToInt32(vars[1].Value),
                    Unk2 = Convert.ToInt32(vars[2].Value),
                    LocationX = Convert.ToSingle(vars[3].Value),
                    LocationZ = Convert.ToSingle(vars[4].Value),
                    LocationY = Convert.ToSingle(vars[5].Value),
                    Unk6 = Convert.ToInt32(vars[6].Value),
                    Unk7 = Convert.ToInt32(vars[7].Value),
                    Rotation = Convert.ToSingle(vars[8].Value),
                    TextIDAsk = Convert.ToInt32(vars[9].Value),
                    TextIDWait = Convert.ToInt32(vars[10].Value),
                    Condition = vars[11].Value?.ToString() ?? string.Empty,
                    PlayerID1 = Convert.ToInt32(vars[12].Value),
                    PlayerID2 = Convert.ToInt32(vars[13].Value),
                    PlayerID3 = Convert.ToInt32(vars[14].Value),
                    PlayerID4 = Convert.ToInt32(vars[15].Value),
                    PlayerID5 = Convert.ToInt32(vars[16].Value),
                    Uniform = Convert.ToInt32(vars[17].Value),
                    Boots = Convert.ToInt32(vars[18].Value),
                    TextIDLost = Convert.ToInt32(vars[19].Value),
                    TextIDWin = Convert.ToInt32(vars[20].Value),
                    Unk21 = vars.Count > 21 ? Convert.ToInt32(vars[21].Value) : 0
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteEncountRecordInPlace(CfgTreeNode node, EncountMapRecord record)
        {
            var vars = node.Item.Variables;
            vars[1].Value = record.TeamID;
            vars[12].Value = record.PlayerID1;
            vars[13].Value = record.PlayerID2;
            vars[14].Value = record.PlayerID3;
            vars[15].Value = record.PlayerID4;
            vars[16].Value = record.PlayerID5;
            vars[17].Value = record.Uniform;
            vars[18].Value = record.Boots;
        }
    }
}
