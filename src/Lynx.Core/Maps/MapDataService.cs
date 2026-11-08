using System;
using System.Collections.Generic;
using System.Linq;
using Lynx.Core.Binary;
using Lynx.Core.RomFs;
using StudioElevenLib.Level5.Binary;
using StudioElevenLib.Level5.Binary.Collections;
using StudioElevenLib.Level5.Binary.Logic;
using StudioElevenLib.Tools;

namespace Lynx.Core.Maps
{
    /// <summary>
    /// Non-destructive reader and writer for overworld map <c>TBOX_CONFIG</c> (treasure chests),
    /// <c>ENCOUNT_INFO</c> (5v5 pachanga spawns), <c>TALK_CONFIG</c> (NPC event triggers including
    /// textual Competition Route IDs like <c>"scr_br_0005_s"</c>), and <c>FUNCPOINT</c>
    /// (<c>TBoxBitCheck</c> progression flags) across <c>/data/map/&lt;mapId&gt;/</c> inside <c>ie_a.fa</c>.
    /// </summary>
    public static class MapDataService
    {
        public static List<MapTreasureChestEntry> LoadAllTreasureChests(VirtualDirectory archiveRoot)
        {
            var results = new List<MapTreasureChestEntry>();
            if (archiveRoot == null || !archiveRoot.IsFolderExists("/data/map"))
                return results;

            VirtualDirectory mapRoot = archiveRoot.GetFolderFromFullPath("/data/map");
            foreach (VirtualDirectory mapFolder in mapRoot.Folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var kvp in mapFolder.Files.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string fileName = kvp.Key;
                    if (!IsCandidateMapCfgFile(fileName))
                        continue;

                    string fullVirtualPath = $"/data/map/{mapFolder.Name}/{fileName}";
                    byte[] bytes = Arc0SafeArchive.TryReadFileFromPath(mapFolder, fileName);
                    if (bytes == null || bytes.Length < 16)
                        continue;

                    if (!TryOpenCfgTree(bytes, out CfgBin<CfgTreeNode> cfg))
                        continue;

                    var tboxNodes = FindEntriesByPrefix(cfg.Entries, "TBOX_CONFIG");
                    for (int idx = 0; idx < tboxNodes.Count; idx++)
                    {
                        var node = tboxNodes[idx];
                        if (TryReadTBoxRecord(node, out TBoxConfigRecord record))
                        {
                            results.Add(new MapTreasureChestEntry
                            {
                                MapId = mapFolder.Name,
                                VirtualFilePath = fullVirtualPath,
                                EntryName = node.Item.Name,
                                NodeIndex = idx,
                                Record = record
                            });
                        }
                    }
                }
            }

            return results;
        }

        public static void SaveTreasureChests(VirtualDirectory archiveRoot, IEnumerable<MapTreasureChestEntry> modifiedChests)
        {
            if (archiveRoot == null || modifiedChests == null)
                return;

            foreach (var byFile in modifiedChests.GroupBy(c => c.VirtualFilePath, StringComparer.Ordinal))
            {
                byte[] existing = Arc0SafeArchive.TryReadFileFromPath(archiveRoot, byFile.Key);
                if (existing == null)
                    continue;

                var cfg = new CfgBin<CfgTreeNode>();
                cfg.Open(existing);

                var tboxNodes = FindEntriesByPrefix(cfg.Entries, "TBOX_CONFIG");
                foreach (MapTreasureChestEntry entry in byFile)
                {
                    if (entry.NodeIndex < 0 || entry.NodeIndex >= tboxNodes.Count)
                        continue;

                    var vars = tboxNodes[entry.NodeIndex].Item.Variables;
                    if (vars.Count >= 9)
                    {
                        Level5CfgValueParser.UpdateHashOrIntVariableInPlace(vars[5], entry.Record.ItemIDLight);
                        Level5CfgValueParser.UpdateHashOrIntVariableInPlace(vars[6], entry.Record.TBoxFlag);
                        Level5CfgValueParser.UpdateHashOrIntVariableInPlace(vars[7], entry.Record.TboxType);
                        Level5CfgValueParser.UpdateHashOrIntVariableInPlace(vars[8], entry.Record.ItemIDShadow);
                    }
                }

                Arc0SafeArchive.WriteFileAtPath(archiveRoot, byFile.Key, cfg.Save());
            }
        }

        public static List<MapEncounterSpawnEntry> LoadAllEncounterSpawns(VirtualDirectory archiveRoot)
        {
            var results = new List<MapEncounterSpawnEntry>();
            if (archiveRoot == null || !archiveRoot.IsFolderExists("/data/map"))
                return results;

            VirtualDirectory mapRoot = archiveRoot.GetFolderFromFullPath("/data/map");
            foreach (VirtualDirectory mapFolder in mapRoot.Folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var kvp in mapFolder.Files.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string fileName = kvp.Key;
                    if (!IsCandidateMapCfgFile(fileName))
                        continue;

                    string fullVirtualPath = $"/data/map/{mapFolder.Name}/{fileName}";
                    byte[] bytes = Arc0SafeArchive.TryReadFileFromPath(mapFolder, fileName);
                    if (bytes == null || bytes.Length < 16)
                        continue;

                    if (!TryOpenCfgTree(bytes, out CfgBin<CfgTreeNode> cfg))
                        continue;

                    var encountNodes = FindEntriesByPrefix(cfg.Entries, "ENCOUNT_INFO");
                    for (int idx = 0; idx < encountNodes.Count; idx++)
                    {
                        var node = encountNodes[idx];
                        if (TryReadEncountRecord(node, out EncountMapRecord record))
                        {
                            results.Add(new MapEncounterSpawnEntry
                            {
                                MapId = mapFolder.Name,
                                VirtualFilePath = fullVirtualPath,
                                EntryName = node.Item.Name,
                                NodeIndex = idx,
                                Record = record
                            });
                        }
                    }
                }
            }

            return results;
        }

        public static void SaveEncounterSpawns(VirtualDirectory archiveRoot, IEnumerable<MapEncounterSpawnEntry> modifiedSpawns)
        {
            if (archiveRoot == null || modifiedSpawns == null)
                return;

            foreach (var byFile in modifiedSpawns.GroupBy(s => s.VirtualFilePath, StringComparer.Ordinal))
            {
                byte[] existing = Arc0SafeArchive.TryReadFileFromPath(archiveRoot, byFile.Key);
                if (existing == null)
                    continue;

                var cfg = new CfgBin<CfgTreeNode>();
                cfg.Open(existing);

                var encountNodes = FindEntriesByPrefix(cfg.Entries, "ENCOUNT_INFO");
                foreach (MapEncounterSpawnEntry entry in byFile)
                {
                    if (entry.NodeIndex < 0 || entry.NodeIndex >= encountNodes.Count)
                        continue;

                    var vars = encountNodes[entry.NodeIndex].Item.Variables;
                    if (vars.Count >= 22)
                    {
                        Level5CfgValueParser.UpdateHashOrIntVariableInPlace(vars[0], entry.Record.ChasePlayer);
                        Level5CfgValueParser.UpdateHashOrIntVariableInPlace(vars[1], entry.Record.TeamID);
                        Level5CfgValueParser.UpdateHashOrIntVariableInPlace(vars[12], entry.Record.PlayerID1);
                        Level5CfgValueParser.UpdateHashOrIntVariableInPlace(vars[13], entry.Record.PlayerID2);
                        Level5CfgValueParser.UpdateHashOrIntVariableInPlace(vars[14], entry.Record.PlayerID3);
                        Level5CfgValueParser.UpdateHashOrIntVariableInPlace(vars[15], entry.Record.PlayerID4);
                        Level5CfgValueParser.UpdateHashOrIntVariableInPlace(vars[16], entry.Record.PlayerID5);
                        Level5CfgValueParser.UpdateHashOrIntVariableInPlace(vars[17], entry.Record.Uniform);
                        Level5CfgValueParser.UpdateHashOrIntVariableInPlace(vars[18], entry.Record.Boots);
                    }
                }

                Arc0SafeArchive.WriteFileAtPath(archiveRoot, byFile.Key, cfg.Save());
            }
        }

        /// <summary>
        /// Scans map <c>*_funcpt.bin</c> (PTREE <c>TBoxBitCheck</c> &amp; <c>ItemID</c>) and <c>*.talk.bin</c>
        /// (<c>TALK_CONFIG</c> NPC scripts, competition routes such as <c>"scr_br_0005_s"</c>, and shops)
        /// to protect story-gating chests and event dependencies.
        /// </summary>
        public static MapProgressionDependencies ScanMapProgressionDependencies(VirtualDirectory archiveRoot)
        {
            var deps = new MapProgressionDependencies();
            if (archiveRoot == null || !archiveRoot.IsFolderExists("/data/map"))
                return deps;

            VirtualDirectory mapRoot = archiveRoot.GetFolderFromFullPath("/data/map");
            foreach (VirtualDirectory mapFolder in mapRoot.Folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var kvp in mapFolder.Files.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string fileName = kvp.Key;
                    string virtualPath = $"/data/map/{mapFolder.Name}/{fileName}";

                    if (fileName.EndsWith("_funcpt.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] bytes = Arc0SafeArchive.TryReadFileFromPath(mapFolder, fileName);
                        ScanFuncpointFile(bytes, deps, virtualPath);
                    }
                    else if (fileName.EndsWith(".talk.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] bytes = Arc0SafeArchive.TryReadFileFromPath(mapFolder, fileName);
                        ScanTalkFile(bytes, deps, virtualPath);
                    }
                }
            }

            return deps;
        }

        private static void ScanFuncpointFile(byte[] bytes, MapProgressionDependencies deps, string virtualPath)
        {
            if (bytes == null || bytes.Length < 16)
                return;

            var cfg = new CfgBin<PtreeNode>();
            cfg.Open(bytes);
            if (cfg.Entries == null)
                return;

            WalkPtree(cfg.Entries, deps, virtualPath);
        }

        private static void WalkPtree(PtreeNode node, MapProgressionDependencies deps, string virtualPath)
        {
            if (node == null)
                return;

            Entry entry = node.Item;
            if (entry?.Variables != null && entry.Variables.Count >= 2)
            {
                // In Level-5 PTREE binaries:
                // - PTVAL has Variables[0] = value, Variables[1] = key name
                // - PTREE has Variables[0] = header key, Variables[1] = value
                string keyName = null;
                Variable valueVar = null;

                if (string.Equals(entry.Name, "PTVAL", StringComparison.OrdinalIgnoreCase))
                {
                    keyName = entry.Variables[1].Value as string;
                    valueVar = entry.Variables[0];
                }
                else if (string.Equals(entry.Name, "PTREE", StringComparison.OrdinalIgnoreCase))
                {
                    keyName = entry.Variables[0].Value as string;
                    valueVar = entry.Variables[1];
                }

                if (string.Equals(keyName, "TBoxBitCheck", StringComparison.OrdinalIgnoreCase))
                {
                    int flag = Level5CfgValueParser.ReadHashOrIdOrThrow(
                        valueVar,
                        $"{virtualPath}:{entry.Name}(TBoxBitCheck)",
                        out _);
                    deps.ProtectedTBoxFlags.Add(flag);
                }
                else if (string.Equals(keyName, "ItemID", StringComparison.OrdinalIgnoreCase))
                {
                    int itemId = Level5CfgValueParser.ReadHashOrIdOrThrow(
                        valueVar,
                        $"{virtualPath}:{entry.Name}(ItemID)",
                        out _);
                    if (itemId != 0)
                        deps.ReferencedFuncptItemIds.Add(itemId);
                }
            }

            foreach (var child in node.Children)
            {
                if (child is PtreeNode pChild)
                {
                    WalkPtree(pChild, deps, virtualPath);
                }
            }
        }

        /// <summary>
        /// Parses <c>TALK_CONFIG</c> entries in <c>*.talk.bin</c> files.
        /// Per <c>Ocelot/Models/MapEnum.cs</c> and <c>ie4cfgbin.cpp</c>, <c>Variables[0]</c> is <c>EventType</c>
        /// and <c>Variables[1]</c> is polymorphic (<c>CfgValueType.Int</c> or <c>CfgValueType.String</c>):
        /// <list type="bullet">
        ///   <item><c>1</c> (<c>Text</c>): dialogue text CRC32/ID</item>
        ///   <item><c>2, 3</c> (<c>UnkScript</c>, <c>Script</c>): event script hash or textual script ID</item>
        ///   <item><c>4, 5</c> (<c>CommunityMaster</c>, <c>Shop</c>): recruitment/shop config ID</item>
        ///   <item><c>6</c> (<c>ChangeMap</c>): destination map ID</item>
        ///   <item><c>7</c> (<c>CompetitiveRoute</c>): route script identifier such as <c>"scr_br_0005_s"</c></item>
        /// </list>
        /// </summary>
        public static void ScanTalkFile(byte[] bytes, MapProgressionDependencies deps, string virtualPath = "<talk.bin>")
        {
            if (bytes == null || bytes.Length < 16)
                return;
            if (!TryOpenCfgTree(bytes, out CfgBin<CfgTreeNode> cfg))
                return;

            var talkNodes = FindEntriesByPrefix(cfg.Entries, "TALK_CONFIG");
            for (int idx = 0; idx < talkNodes.Count; idx++)
            {
                var vars = talkNodes[idx].Item.Variables;
                if (vars == null || vars.Count < 2)
                    continue;

                string contextPrefix = $"{virtualPath}:TALK_CONFIG[{idx}]";
                int talkType = Level5CfgValueParser.ReadInt32OrThrow(vars[0], contextPrefix + ".TalkType");
                int talkHashOrId = Level5CfgValueParser.ReadHashOrIdOrThrow(
                    vars[1],
                    contextPrefix + ".TalkValue",
                    out string textualId);

                switch (talkType)
                {
                    case 2:
                    case 3:
                        if (talkHashOrId != 0)
                            deps.ReferencedScriptHashes.Add(talkHashOrId);
                        if (!string.IsNullOrEmpty(textualId))
                            deps.ReferencedScriptIds.Add(textualId);
                        break;

                    case 4:
                    case 5:
                        if (talkHashOrId != 0)
                            deps.ReferencedShopHashes.Add(talkHashOrId);
                        if (!string.IsNullOrEmpty(textualId))
                            deps.ReferencedShopIds.Add(textualId);
                        break;

                    case 7:
                        if (talkHashOrId != 0)
                            deps.ReferencedRouteHashes.Add(talkHashOrId);
                        if (!string.IsNullOrEmpty(textualId))
                            deps.ReferencedRouteIds.Add(textualId);
                        break;
                }
            }
        }

        private static bool IsCandidateMapCfgFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;

            if (!fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) &&
                !fileName.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (fileName.EndsWith("_funcpt.bin", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".npc.bin", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".talk.bin", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("_es.cfg.bin", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("_en.cfg.bin", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("_fr.cfg.bin", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("_de.cfg.bin", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("_it.cfg.bin", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("_ja.cfg.bin", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool TryOpenCfgTree(byte[] data, out CfgBin<CfgTreeNode> cfg)
        {
            cfg = null;
            try
            {
                var candidate = new CfgBin<CfgTreeNode>();
                candidate.Open(data);
                if (candidate.Entries == null || candidate.Entries.Children.Count == 0)
                    return false;

                cfg = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<CfgTreeNode> FindEntriesByPrefix(CfgTreeNode root, string baseEntryName)
        {
            var found = new List<CfgTreeNode>();
            if (root == null)
                return found;

            void Walk(CfgTreeNode current)
            {
                string name = current.Item?.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    bool isBeginOrEnd = name.EndsWith("_BEGIN", StringComparison.OrdinalIgnoreCase) ||
                                        name.EndsWith("_END", StringComparison.OrdinalIgnoreCase);
                    if (!isBeginOrEnd &&
                        (string.Equals(name, baseEntryName, StringComparison.OrdinalIgnoreCase) ||
                         name.StartsWith(baseEntryName + "_", StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add(current);
                    }
                }

                foreach (var child in current.Children)
                {
                    if (child is CfgTreeNode cfgChild)
                        Walk(cfgChild);
                }
            }

            Walk(root);
            return found;
        }

        private static bool TryReadTBoxRecord(CfgTreeNode node, out TBoxConfigRecord record)
        {
            record = null;
            var vars = node?.Item?.Variables;
            if (vars == null || vars.Count < 9)
                return false;

            if (!Level5CfgValueParser.TryReadInt32(vars[0], out int tboxId) ||
                !Level5CfgValueParser.TryReadSingle(vars[1], out float locX) ||
                !Level5CfgValueParser.TryReadSingle(vars[2], out float locZ) ||
                !Level5CfgValueParser.TryReadSingle(vars[3], out float locY) ||
                !Level5CfgValueParser.TryReadSingle(vars[4], out float rot) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[5], out int itemLight, out _) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[6], out int tboxFlag, out _) ||
                !Level5CfgValueParser.TryReadInt32(vars[7], out int tboxType) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[8], out int itemShadow, out _))
            {
                return false;
            }

            record = new TBoxConfigRecord
            {
                TBoxID = tboxId,
                LocationX = locX,
                LocationZ = locZ,
                LocationY = locY,
                Rotation = rot,
                ItemIDLight = itemLight,
                TBoxFlag = tboxFlag,
                TboxType = tboxType,
                ItemIDShadow = itemShadow
            };
            return true;
        }

        private static bool TryReadEncountRecord(CfgTreeNode node, out EncountMapRecord record)
        {
            record = null;
            var vars = node?.Item?.Variables;
            if (vars == null || vars.Count < 22)
                return false;

            if (!Level5CfgValueParser.TryReadHashOrId(vars[0], out int chasePlayer, out _) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[1], out int teamId, out _) ||
                !Level5CfgValueParser.TryReadInt32(vars[2], out int unk2) ||
                !Level5CfgValueParser.TryReadSingle(vars[3], out float locX) ||
                !Level5CfgValueParser.TryReadSingle(vars[4], out float locZ) ||
                !Level5CfgValueParser.TryReadSingle(vars[5], out float locY) ||
                !Level5CfgValueParser.TryReadInt32(vars[6], out int unk6) ||
                !Level5CfgValueParser.TryReadInt32(vars[7], out int unk7) ||
                !Level5CfgValueParser.TryReadSingle(vars[8], out float rot) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[9], out int textAsk, out _) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[10], out int textWait, out _) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[12], out int pid1, out _) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[13], out int pid2, out _) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[14], out int pid3, out _) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[15], out int pid4, out _) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[16], out int pid5, out _) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[17], out int uniform, out _) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[18], out int boots, out _) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[19], out int textLost, out _) ||
                !Level5CfgValueParser.TryReadHashOrId(vars[20], out int textWin, out _) ||
                !Level5CfgValueParser.TryReadInt32(vars[21], out int unk21))
            {
                return false;
            }

            record = new EncountMapRecord
            {
                ChasePlayer = chasePlayer,
                TeamID = teamId,
                Unk2 = unk2,
                LocationX = locX,
                LocationZ = locZ,
                LocationY = locY,
                Unk6 = unk6,
                Unk7 = unk7,
                Rotation = rot,
                TextIDAsk = textAsk,
                TextIDWait = textWait,
                Condition = Level5CfgValueParser.ReadStringOrCondition(vars[11]),
                PlayerID1 = pid1,
                PlayerID2 = pid2,
                PlayerID3 = pid3,
                PlayerID4 = pid4,
                PlayerID5 = pid5,
                Uniform = uniform,
                Boots = boots,
                TextIDLost = textLost,
                TextIDWin = textWin,
                Unk21 = unk21
            };
            return true;
        }
    }
}
