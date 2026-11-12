using System;
using System.Collections.Generic;
using System.Linq;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Protection;
using IEGORandomizer.Engine.Seeding;
using IEGORandomizer.Engine.Validation;
using Lynx.Core.Binary;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.Core.RomFs;
using Lynx.InazumaEleven.Games.GO;
using Lynx.InazumaEleven.Logic;
using StudioElevenLib.Level5.Binary;
using StudioElevenLib.Level5.Binary.Collections;
using StudioElevenLib.Level5.Binary.Logic;

namespace IEGORandomizer.Engine.Modules
{
    /// <summary>
    /// Subsystem F: Randomizes Coaches (<c>ItemConfigDirector</c> stat/group profiles, localized
    /// <c>Dir-{N}-1/2</c> lines across <c>system_text*.cfg.bin</c>, and story <c>CMND_*_DIRECTOR</c> hooks),
    /// Team Managers (<c>cp0036..cp0038</c>, <c>cp0044</c> atomic <c>Body</c>-matched visual &amp; name swap),
    /// Story-Intervening Stand-Event NPCs (<c>CharaBaseType == 3</c>, <c>cn0001..cn0099</c> grouped by <c>(Body, Gender)</c>),
    /// and Overworld Map Human NPCs (<c>.npc.bin</c> <c>NPC_BASE</c> <c>Type == 1</c> &amp; <c>Type == 3</c>),
    /// while strictly quarantining <c>Type == 4</c> animals/props and <c>Body == 0</c> special cutscene rigs.
    /// </summary>
    public static class NpcAndCoachRandomizerModule
    {
        private static readonly int[] ManagerModelNumbers = { 36, 37, 38, 44 };
        private static readonly HashSet<int> SpecialCutsceneNpcModels = new() { 13, 15, 19, 45 };

        public static void Execute(
            SafeGOGame game,
            SeedEngine seeds,
            NpcsRandomizerOptions options,
            StoryProtectionContext protection,
            VisualCoherenceProfile visualProfile,
            RandomizationExecutionReport report)
        {
            if (options == null || !options.Enabled)
                return;

            // 1. Coaches (Entrenadores: ItemConfigDirector + localized system_text + .nutb CMND_ADD_SET_DIRECTOR)
            if (options.RandomizeCoaches)
            {
                RandomizeCoaches(
                    game,
                    seeds.GetSubsystem("npcs.coaches"),
                    protection,
                    report);
            }

            var charaBases = game.GetCharabase() ?? Array.Empty<ICharabase>();
            bool charaBaseModified = false;

            // 2. Team Managers (Gerentes: Skie cp0036, Jade cp0037, Rosie cp0038, Celia cp0044)
            if (options.RandomizeManagers && charaBases.Length > 0)
            {
                if (RandomizeManagers(
                    game,
                    seeds.GetSubsystem("npcs.managers"),
                    charaBases,
                    protection,
                    visualProfile,
                    report))
                {
                    charaBaseModified = true;
                }
            }

            // 3. Story-Intervening Stand-Event NPCs (cn0001..cn0099, CharaBaseType == 3, Body 1..8)
            if (options.RandomizeStoryNpcs && charaBases.Length > 0)
            {
                if (RandomizeStoryNpcs(
                    game,
                    seeds.GetSubsystem("npcs.story"),
                    charaBases,
                    visualProfile,
                    report))
                {
                    charaBaseModified = true;
                }
            }

            if (charaBaseModified)
            {
                game.SaveCharaBase(charaBases);
            }

            // 4. Overworld Map Human NPCs (.npc.bin NPC_BASE Type == 1 & Type == 3)
            if (options.RandomizeOverworldMapNpcs && charaBases.Length > 0)
            {
                RandomizeOverworldMapNpcs(
                    game,
                    seeds.GetSubsystem("npcs.maps"),
                    charaBases,
                    visualProfile,
                    report);
            }
        }

        private static void RandomizeCoaches(
            SafeGOGame game,
            DeterministicRng rng,
            StoryProtectionContext protection,
            RandomizationExecutionReport report)
        {
            var directors = (game.GetItems("director") ?? Array.Empty<IItemConfig>())
                .OfType<GOSupport.ItemConfigDirector>()
                .ToArray();

            if (directors.Length < 2)
                return;

            var iimLookup = Level5IdHelper.BuildPrefixLookup("iim");
            var validDirectors = directors
                .Where(d => d.ItemID != 0 && d.NameID != 0 && d.ItemNumber > 0)
                .ToList();

            if (validDirectors.Count < 2)
                return;

            // Layer A: Remap story CMND_ADD_SET_DIRECTOR / CMND_SET_DIRECTOR coach item codes ("iimXXXX" -> "iimYYYY")
            var codedDirectors = validDirectors
                .Where(d => iimLookup.ContainsKey(d.ItemID))
                .Select(d => iimLookup[d.ItemID])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (codedDirectors.Count >= 2)
            {
                var derangedCodes = rng.CreateDerangement(codedDirectors);
                for (int i = 0; i < codedDirectors.Count; i++)
                {
                    protection.StoryDirectorRemaps[codedDirectors[i]] = derangedCodes[i];
                }
            }

            // Layer B: Derange coach stat-boost profiles together with localized Dir-{ItemNumber}-1/2 text
            var snapshots = validDirectors.Select(d => new DirectorProfileSnapshot(d)).ToList();
            var indices = Enumerable.Range(0, validDirectors.Count).ToList();
            var derangedIndices = rng.CreateDerangement(indices);

            for (int i = 0; i < validDirectors.Count; i++)
            {
                snapshots[derangedIndices[i]].ApplyTo(validDirectors[i]);
                report.RandomizedCoachesCount++;
            }

            game.SaveItems(directors);

            // Synchronize Dir-{ItemNumber}-1 and Dir-{ItemNumber}-2 across all system_text*.cfg.bin files
            // STRICTLY IN-PLACE via CfgBin<CfgTreeNode> (NEVER T2bþ.Save(), which forces 4-column IEGO2/3 variance=true!)
            var textDir = game.IE_A?.Directory?.GetFolderFromFullPath("/data/res/text");
            if (textDir != null)
            {
                foreach (string fileName in textDir.Files.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList())
                {
                    if (!fileName.StartsWith("system_text", StringComparison.OrdinalIgnoreCase) ||
                        !fileName.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string fullPath = $"/data/res/text/{fileName}";
                    byte[] rawBytes = Arc0SafeArchive.TryReadFileFromPath(game.IE_A.Directory, fullPath);
                    if (rawBytes == null || rawBytes.Length == 0)
                        continue;

                    if (TryUpdateCoachTextsInPlace(rawBytes, validDirectors, derangedIndices, out byte[] updatedBytes))
                    {
                        Arc0SafeArchive.WriteFileAtPath(game.IE_A.Directory, fullPath, updatedBytes);
                    }
                }
            }
        }

        private static bool TryUpdateCoachTextsInPlace(
            byte[] rawBytes,
            List<GOSupport.ItemConfigDirector> validDirectors,
            List<int> derangedIndices,
            out byte[] updatedBytes)
        {
            updatedBytes = null;
            try
            {
                var cfg = new CfgBin<CfgTreeNode>();
                cfg.Open(rawBytes);

                var textNodesByHash = new Dictionary<int, List<(int LineNum, Variable StringVar)>>();
                CollectTextInfoStringVariables(cfg.Entries.Children.OfType<CfgTreeNode>(), textNodesByHash);

                if (textNodesByHash.Count == 0)
                    return false;

                var line1Snap = new Dictionary<int, string>[validDirectors.Count];
                var line2Snap = new Dictionary<int, string>[validDirectors.Count];

                for (int i = 0; i < validDirectors.Count; i++)
                {
                    int num = validDirectors[i].ItemNumber;
                    int crc1 = Level5IdHelper.ComputeCrc32Utf8($"Dir-{num}-1");
                    int crc2 = Level5IdHelper.ComputeCrc32Utf8($"Dir-{num}-2");

                    if (textNodesByHash.TryGetValue(crc1, out var list1))
                    {
                        line1Snap[i] = list1.ToDictionary(x => x.LineNum, x => x.StringVar.Value as string ?? string.Empty);
                    }
                    if (textNodesByHash.TryGetValue(crc2, out var list2))
                    {
                        line2Snap[i] = list2.ToDictionary(x => x.LineNum, x => x.StringVar.Value as string ?? string.Empty);
                    }
                }

                bool anyChanged = false;
                for (int i = 0; i < validDirectors.Count; i++)
                {
                    int targetNum = validDirectors[i].ItemNumber;
                    int donorIdx = derangedIndices[i];
                    int crc1 = Level5IdHelper.ComputeCrc32Utf8($"Dir-{targetNum}-1");
                    int crc2 = Level5IdHelper.ComputeCrc32Utf8($"Dir-{targetNum}-2");

                    if (line1Snap[donorIdx] != null && textNodesByHash.TryGetValue(crc1, out var targetList1))
                    {
                        anyChanged |= ApplySnapshotToTextVariables(targetList1, line1Snap[donorIdx]);
                    }
                    if (line2Snap[donorIdx] != null && textNodesByHash.TryGetValue(crc2, out var targetList2))
                    {
                        anyChanged |= ApplySnapshotToTextVariables(targetList2, line2Snap[donorIdx]);
                    }
                }

                if (!anyChanged)
                    return false;

                updatedBytes = cfg.Save();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CollectTextInfoStringVariables(
            IEnumerable<CfgTreeNode> nodes,
            Dictionary<int, List<(int LineNum, Variable StringVar)>> byHash)
        {
            if (nodes == null)
                return;

            foreach (var node in nodes)
            {
                if (node?.Item != null &&
                    string.Equals(node.Item.Name, "TEXT_INFO", StringComparison.Ordinal) &&
                    node.Item.Variables != null &&
                    node.Item.Variables.Count >= 3)
                {
                    var vars = node.Item.Variables;
                    if (Level5CfgValueParser.TryReadInt32(vars[0], out int crc) &&
                        Level5CfgValueParser.TryReadInt32(vars[1], out int lineNum))
                    {
                        Variable strVar = null;
                        for (int v = vars.Count - 1; v >= 2; v--)
                        {
                            if (vars[v]?.Type == CfgValueType.String)
                            {
                                strVar = vars[v];
                                break;
                            }
                        }

                        if (strVar != null)
                        {
                            if (!byHash.TryGetValue(crc, out var list))
                            {
                                list = new List<(int LineNum, Variable StringVar)>();
                                byHash[crc] = list;
                            }
                            list.Add((lineNum, strVar));
                        }
                    }
                }

                if (node?.Children != null && node.Children.Count > 0)
                {
                    CollectTextInfoStringVariables(node.Children.OfType<CfgTreeNode>(), byHash);
                }
            }
        }

        private static bool ApplySnapshotToTextVariables(
            List<(int LineNum, Variable StringVar)> targetVars,
            Dictionary<int, string> donorByLine)
        {
            bool changed = false;
            string fallback = donorByLine.Values.FirstOrDefault() ?? string.Empty;
            foreach (var (lineNum, strVar) in targetVars)
            {
                string nextVal = donorByLine.TryGetValue(lineNum, out string exact) ? exact : fallback;
                if (!string.Equals(strVar.Value as string, nextVal, StringComparison.Ordinal))
                {
                    strVar.Value = nextVal;
                    changed = true;
                }
            }
            return changed;
        }

        private static bool RandomizeManagers(
            SafeGOGame game,
            DeterministicRng rng,
            ICharabase[] charaBases,
            StoryProtectionContext protection,
            VisualCoherenceProfile visualProfile,
            RandomizationExecutionReport report)
        {
            var root = game.IE_A.Directory;
            var managerSet = new HashSet<int>(ManagerModelNumbers);

            var managers = charaBases
                .Where(cb => cb.CharaBaseType == 1 && managerSet.Contains(cb.ModelNumber) && cb.Body > 0)
                .OrderBy(cb => cb.ModelNumber)
                .ToList();

            if (managers.Count == 0)
                return false;

            // Build pool of coherent female CharaBaseType == 1 characters grouped by exact (Body, Gender),
            // strictly excluding protected story soccer players so story rosters never get double-swapped!
            var coherentCandidates = charaBases
                .Where(cb => cb.CharaBaseType == 1 &&
                             cb.ModelNumber > 0 &&
                             cb.ModelNumber < VisualCoherenceValidator.MaxEngineModelNumberExclusive &&
                             cb.Body > 0 &&
                             cb.Gender == 2 &&
                             (managerSet.Contains(cb.ModelNumber) ||
                              (visualProfile.CoherentPlayableBaseHashes.Contains(cb.BaseHash) &&
                               (protection == null || !protection.ProtectedStoryPlayerBaseHashes.Contains(cb.BaseHash)))))
                .OrderBy(cb => cb.ModelNumber)
                .ToList();

            var loadedAssets = new Dictionary<int, CpVisualSnapshot>();
            foreach (var cb in coherentCandidates)
            {
                if (TryLoadCpVisualSnapshot(root, cb, out var snap))
                {
                    loadedAssets[cb.BaseHash] = snap;
                }
            }

            bool anySwapped = false;
            var usedBaseHashes = new HashSet<int>();

            foreach (var mgr in managers)
            {
                if (!loadedAssets.TryGetValue(mgr.BaseHash, out var mgrSnap) || usedBaseHashes.Contains(mgr.BaseHash))
                    continue;

                var compatibleDonors = coherentCandidates
                    .Where(c => c.BaseHash != mgr.BaseHash &&
                                !usedBaseHashes.Contains(c.BaseHash) &&
                                c.Body == mgr.Body &&
                                c.Gender == mgr.Gender &&
                                loadedAssets.ContainsKey(c.BaseHash))
                    .ToList();

                if (compatibleDonors.Count == 0)
                    continue;

                var partner = rng.PickOne(compatibleDonors);
                var partnerSnap = loadedAssets[partner.BaseHash];

                // Reciprocal swap keeps every character visual unique in the game while preserving BaseHash, ModelNumber & Body rig!
                ApplyCpVisualSnapshot(root, mgr, partnerSnap);
                ApplyCpVisualSnapshot(root, partner, mgrSnap);

                usedBaseHashes.Add(mgr.BaseHash);
                usedBaseHashes.Add(partner.BaseHash);
                anySwapped = true;
                report.RandomizedManagersCount++;
            }

            return anySwapped;
        }

        private static bool RandomizeStoryNpcs(
            SafeGOGame game,
            DeterministicRng rng,
            ICharabase[] charaBases,
            VisualCoherenceProfile visualProfile,
            RandomizationExecutionReport report)
        {
            var root = game.IE_A.Directory;

            var standNpcs = charaBases
                .Where(cb => cb.CharaBaseType == 3 &&
                             cb.ModelNumber >= 1 &&
                             cb.ModelNumber <= 99 &&
                             cb.Body >= 1 &&
                             cb.Body <= 8 &&
                             !SpecialCutsceneNpcModels.Contains(cb.ModelNumber) &&
                             (visualProfile.CoherentStandEventNpcBaseHashes.Count == 0 ||
                              visualProfile.CoherentStandEventNpcBaseHashes.Contains(cb.BaseHash)))
                .OrderBy(cb => cb.ModelNumber)
                .ToList();

            if (standNpcs.Count < 2)
                return false;

            bool anySwapped = false;

            foreach (var rigGroup in standNpcs.GroupBy(cb => (cb.Body, cb.Gender)).OrderBy(g => g.Key.Body).ThenBy(g => g.Key.Gender))
            {
                var groupMembers = new List<ICharabase>();
                var snapshots = new List<CnVisualSnapshot>();

                foreach (var cb in rigGroup)
                {
                    if (TryLoadCnVisualSnapshot(root, cb, out var snap))
                    {
                        groupMembers.Add(cb);
                        snapshots.Add(snap);
                    }
                }

                if (groupMembers.Count < 2)
                    continue;

                var indices = Enumerable.Range(0, groupMembers.Count).ToList();
                var deranged = rng.CreateDerangement(indices);

                for (int i = 0; i < groupMembers.Count; i++)
                {
                    ApplyCnVisualSnapshot(root, groupMembers[i], snapshots[deranged[i]]);
                    anySwapped = true;
                    report.RandomizedStoryNpcsCount++;
                }
            }

            return anySwapped;
        }

        private static readonly Lazy<HashSet<int>> EventCutsceneNpcIdHashes = new(() =>
        {
            var set = new HashSet<int>(10000);
            for (int i = 0; i <= 9999; i++)
            {
                set.Add(Level5IdHelper.ComputeCrc32Utf8($"evc{i:D4}"));
            }
            return set;
        });

        private static void RandomizeOverworldMapNpcs(
            SafeGOGame game,
            DeterministicRng rng,
            ICharabase[] charaBases,
            VisualCoherenceProfile visualProfile,
            RandomizationExecutionReport report)
        {
            var mapNpcs = MapDataService.LoadAllMapNpcBases(game.IE_A.Directory);
            if (mapNpcs.Count == 0)
                return;

            // Index CharaBase by (CharaBaseType, ModelNumber)
            var cbByTypeAndHead = new Dictionary<(int Type, int HeadId), ICharabase>();
            foreach (var cb in charaBases)
            {
                if (cb.CharaBaseType > 0 && cb.ModelNumber > 0)
                {
                    cbByTypeAndHead[(cb.CharaBaseType, cb.ModelNumber)] = cb;
                }
            }

            // Build rig-compatible HeadID pools strictly for generic overworld town pedestrians (Type == 3, cn1000..cn1999).
            // CRITICAL COHERENCE INVARIANT:
            // - Type == 1 (cpXXXX soccer players & managers) and Type == 3 < 1000 (cn0001..cn0099 named story NPCs)
            //   MUST keep their exact HeadId in .npc.bin so map/cutscene actors (e.g. CreateEvHiMdlForNPCID("evcXXXX"))
            //   automatically display the exact same character as matches (para_cpXXXX) and 2D dialogue portraits!
            var headPools = new Dictionary<(int Body, int Gender), List<int>>();

            foreach (var cb in charaBases)
            {
                if (cb.CharaBaseType != 3 ||
                    cb.Body <= 0 ||
                    cb.Body > 8 ||
                    cb.ModelNumber < 1000 ||
                    cb.ModelNumber > 1999)
                {
                    continue;
                }

                if (visualProfile.ValidOverworldNpcModelNumbers.Count > 0 &&
                    !visualProfile.ValidOverworldNpcModelNumbers.Contains(cb.ModelNumber))
                {
                    continue;
                }

                var key = (cb.Body, cb.Gender);
                if (!headPools.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    headPools[key] = list;
                }
                list.Add(cb.ModelNumber);
            }

            foreach (var list in headPools.Values)
            {
                list.Sort();
            }

            var evcHashes = EventCutsceneNpcIdHashes.Value;
            var modifiedEntries = new List<MapNpcBaseEntry>();
            foreach (var entry in mapNpcs)
            {
                var rec = entry.Record;
                // Strictly preserve:
                // - evcXXXX scripted cutscene actor slots (CreateEvHiMdlForNPCID / CreateEvLowMdlForNPCID)
                // - Type == 1 (cpXXXX playable/story soccer characters & managers -> 100% cutscene/map/match sync!)
                // - Type == 3 < 1000 (cn0001..cn0099 named story NPCs -> already swapped globally in RandomizeStoryNpcs!)
                // - Type == 4 (caXXXX animals/props)
                if (evcHashes.Contains(rec.NpcId))
                    continue;
                if (rec.Type != 3 || rec.HeadId < 1000 || rec.HeadId > 1999)
                    continue;

                if (!cbByTypeAndHead.TryGetValue((rec.Type, rec.HeadId), out var origCb) ||
                    origCb.Body <= 0 ||
                    origCb.Body > 8)
                {
                    continue;
                }

                if (!headPools.TryGetValue((origCb.Body, origCb.Gender), out var pool) ||
                    pool.Count < 2)
                {
                    continue;
                }

                int nextHead = PickDistinctHead(pool, rec.HeadId, rng);
                if (nextHead != rec.HeadId)
                {
                    rec.HeadId = nextHead;
                    modifiedEntries.Add(entry);
                    report.RandomizedOverworldMapNpcsCount++;
                }
            }

            if (modifiedEntries.Count > 0)
            {
                MapDataService.SaveMapNpcBases(game.IE_A.Directory, modifiedEntries);
            }
        }

        private static int PickDistinctHead(List<int> pool, int current, DeterministicRng rng)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                int candidate = rng.PickOne(pool);
                if (candidate != current)
                    return candidate;
            }
            int idx = pool.IndexOf(current);
            return idx >= 0 ? pool[(idx + 1) % pool.Count] : rng.PickOne(pool);
        }

        private static bool TryLoadCpVisualSnapshot(
            StudioElevenLib.Tools.VirtualDirectory root,
            ICharabase cb,
            out CpVisualSnapshot snapshot)
        {
            snapshot = default;
            string code = $"cp{cb.ModelNumber:D4}";
            byte[] bust = Arc0SafeArchive.TryReadFileFromPath(root, $"/data/bustup/face/{code}a.xi");
            byte[] rpg = Arc0SafeArchive.TryReadFileFromPath(root, $"/data/chr/model/rpg/face/{code}m.xc");
            byte[] waza = Arc0SafeArchive.TryReadFileFromPath(root, $"/data/chr/model/waza/face/{code}a.xc");
            if (bust == null || rpg == null || waza == null)
                return false;

            snapshot = new CpVisualSnapshot(
                cb.NameHash,
                cb.NicknameHash,
                cb.DescriptionHash,
                cb.Skin,
                cb.Year,
                bust,
                rpg,
                waza);
            return true;
        }

        private static void ApplyCpVisualSnapshot(
            StudioElevenLib.Tools.VirtualDirectory root,
            ICharabase target,
            CpVisualSnapshot donor)
        {
            string code = $"cp{target.ModelNumber:D4}";
            target.NameHash = donor.NameHash;
            target.NicknameHash = donor.NicknameHash;
            target.DescriptionHash = donor.DescriptionHash;
            target.Skin = donor.Skin;
            target.Year = donor.Year;

            Arc0SafeArchive.WriteFileAtPath(root, $"/data/bustup/face/{code}a.xi", donor.Bustup);
            Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/rpg/face/{code}m.xc", donor.RpgModel);
            Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/waza/face/{code}a.xc", donor.WazaModel);
        }

        private static bool TryLoadCnVisualSnapshot(
            StudioElevenLib.Tools.VirtualDirectory root,
            ICharabase cb,
            out CnVisualSnapshot snapshot)
        {
            snapshot = default;
            string code = $"cn{cb.ModelNumber:D4}";
            byte[] bust = Arc0SafeArchive.TryReadFileFromPath(root, $"/data/bustup/face/{code}a.xi");
            byte[] rpg = Arc0SafeArchive.TryReadFileFromPath(root, $"/data/chr/model/rpg/face/{code}m.xc");
            if (bust == null || rpg == null)
                return false;

            byte[] waza = Arc0SafeArchive.TryReadFileFromPath(root, $"/data/chr/model/waza/face/{code}a.xc");

            snapshot = new CnVisualSnapshot(
                cb.NameHash,
                cb.NicknameHash,
                cb.DescriptionHash,
                cb.Skin,
                cb.Year,
                bust,
                rpg,
                waza);
            return true;
        }

        private static void ApplyCnVisualSnapshot(
            StudioElevenLib.Tools.VirtualDirectory root,
            ICharabase target,
            CnVisualSnapshot donor)
        {
            string code = $"cn{target.ModelNumber:D4}";
            target.NameHash = donor.NameHash;
            target.NicknameHash = donor.NicknameHash;
            target.DescriptionHash = donor.DescriptionHash;
            target.Skin = donor.Skin;
            target.Year = donor.Year;

            Arc0SafeArchive.WriteFileAtPath(root, $"/data/bustup/face/{code}a.xi", donor.Bustup);
            Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/rpg/face/{code}m.xc", donor.RpgModel);
            if (donor.WazaModel != null)
            {
                Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/waza/face/{code}a.xc", donor.WazaModel);
            }
        }

        private readonly record struct CpVisualSnapshot(
            int NameHash,
            int NicknameHash,
            int DescriptionHash,
            int Skin,
            int Year,
            byte[] Bustup,
            byte[] RpgModel,
            byte[] WazaModel);

        private readonly record struct CnVisualSnapshot(
            int NameHash,
            int NicknameHash,
            int DescriptionHash,
            int Skin,
            int Year,
            byte[] Bustup,
            byte[] RpgModel,
            byte[] WazaModel);

        private sealed class DirectorProfileSnapshot
        {
            private readonly int _gBuff1, _gBuff2, _gBuff3;
            private readonly int _gDebuff1, _gDebuff2, _gDebuff3;
            private readonly int _fpC, _tpC, _kickC, _dribC, _techC, _blkC, _spdC, _staC, _catC, _lckC;
            private readonly int _fpN, _tpN, _kickN, _dribN, _techN, _blkN, _spdN, _staN, _catN, _lckN;

            public DirectorProfileSnapshot(GOSupport.ItemConfigDirector src)
            {
                _gBuff1 = src.PlayerGroupBuff1; _gBuff2 = src.PlayerGroupBuff2; _gBuff3 = src.PlayerGroupBuff3;
                _gDebuff1 = src.PlayerGroupDebuff1; _gDebuff2 = src.PlayerGroupDebuff2; _gDebuff3 = src.PlayerGroupDebuff3;
                _fpC = src.FPCompatible; _tpC = src.TPCompatible; _kickC = src.KickCompatible;
                _dribC = src.DribbleCompatible; _techC = src.TechniqueCompatible; _blkC = src.BlockCompatible;
                _spdC = src.SpeedCompatible; _staC = src.StaminaCompatible; _catC = src.CatchCompatible; _lckC = src.LuckCompatible;
                _fpN = src.FPNotCompatible; _tpN = src.TPNotCompatible; _kickN = src.KickNotCompatible;
                _dribN = src.DribbleNotCompatible; _techN = src.TechniqueNotCompatible; _blkN = src.BlockNotCompatible;
                _spdN = src.SpeedNotCompatible; _staN = src.StaminaNotCompatible; _catN = src.CatchNotCompatible; _lckN = src.LuckNotCompatible;
            }

            public void ApplyTo(GOSupport.ItemConfigDirector dst)
            {
                dst.PlayerGroupBuff1 = _gBuff1; dst.PlayerGroupBuff2 = _gBuff2; dst.PlayerGroupBuff3 = _gBuff3;
                dst.PlayerGroupDebuff1 = _gDebuff1; dst.PlayerGroupDebuff2 = _gDebuff2; dst.PlayerGroupDebuff3 = _gDebuff3;
                dst.FPCompatible = _fpC; dst.TPCompatible = _tpC; dst.KickCompatible = _kickC;
                dst.DribbleCompatible = _dribC; dst.TechniqueCompatible = _techC; dst.BlockCompatible = _blkC;
                dst.SpeedCompatible = _spdC; dst.StaminaCompatible = _staC; dst.CatchCompatible = _catC; dst.LuckCompatible = _lckC;
                dst.FPNotCompatible = _fpN; dst.TPNotCompatible = _tpN; dst.KickNotCompatible = _kickN;
                dst.DribbleNotCompatible = _dribN; dst.TechniqueNotCompatible = _techN; dst.BlockNotCompatible = _blkN;
                dst.SpeedNotCompatible = _spdN; dst.StaminaNotCompatible = _staN; dst.CatchNotCompatible = _catN; dst.LuckNotCompatible = _lckN;
            }
        }
    }
}
