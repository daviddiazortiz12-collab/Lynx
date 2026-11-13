using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IEGORandomizer.Engine.Validation;
using Lynx.Core.Binary;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.Core.RomFs;
using Lynx.Core.RomFs.Cia;
using Lynx.InazumaEleven.Games.GO;
using StudioElevenLib.Level5.Binary;
using StudioElevenLib.Level5.Binary.Collections;
using StudioElevenLib.Level5.Binary.Logic;
using StudioElevenLib.Level5.Text;
using StudioElevenLib.Tools;

namespace IEGORandomizer.Engine.Repair
{
    public sealed class InstalledV122RepairReport
    {
        public string InstalledSourcePath { get; init; } = string.Empty;
        public string CleanReferencePath { get; init; } = string.Empty;
        public string OutputRepairedArc0Path { get; init; } = string.Empty;
        public string OutputDeltaPackPath { get; init; } = string.Empty;
        public bool RebuiltItemConfigPreservingRefs { get; set; }
        public int RepairedSystemTextFilesCount { get; set; }
        public int RestoredMapNpcHeadsCount { get; set; }
        public int SyncedCnWazaFacesCount { get; set; }
        public bool RepairedVictorCp0010BodyRig { get; set; }
        public int SanitizedDummyTeamItemsCount { get; set; }
        public Arc0DeltaPatchPackResult DeltaPack { get; set; }
    }

    /// <summary>
    /// Surgically repairs an already-installed <c>v1.2.2</c> randomized game (<c>_Randomizado.cia</c> or <c>ie_a.fa</c>)
    /// using clean retail tables as structural reference while preserving 100% of the user's current seed's
    /// players, stats, movesets, keshins, coaches, teams, and chests so existing <c>00000001.sav</c> saves
    /// continue seamlessly past Chapter 1 (<c>btl0002</c> Caballeros Templarios) with coherent cutscene/match sprites.
    /// </summary>
    public static class InstalledV122RepairService
    {
        private static readonly string[] SystemTextLanguages = { "es", "en", "fr", "de", "it", "ja" };
        private static readonly HashSet<int> ReservedManagerModels = new() { 36, 37, 38, 44 };

        public static InstalledV122RepairReport RepairAndBuildDeltaPack(
            string installedCiaOrFaPath,
            string cleanRetailCiaOrFaPath,
            string outputRepairedArc0Path,
            string outputDeltaPackPath,
            string aesKeysPath = null)
        {
            if (string.IsNullOrWhiteSpace(installedCiaOrFaPath) || !File.Exists(installedCiaOrFaPath))
                throw new FileNotFoundException("No se encontró el archivo instalado (_Randomizado).", installedCiaOrFaPath);
            if (string.IsNullOrWhiteSpace(cleanRetailCiaOrFaPath) || !File.Exists(cleanRetailCiaOrFaPath))
                throw new FileNotFoundException("No se encontró el archivo base limpio de referencia.", cleanRetailCiaOrFaPath);

            using var installedWs = GameWorkspace.Open(installedCiaOrFaPath, GameEdition.Auto, "es", computeSourceSha256: false, aesKeysPath: aesKeysPath);
            using var cleanWs = GameWorkspace.Open(cleanRetailCiaOrFaPath, GameEdition.Auto, "es", computeSourceSha256: false, aesKeysPath: aesKeysPath);

            string installedFaPath = installedWs.ActiveIeAFaPath;
            string cleanFaPath = cleanWs.ActiveIeAFaPath;

            var report = new InstalledV122RepairReport
            {
                InstalledSourcePath = Path.GetFullPath(installedCiaOrFaPath),
                CleanReferencePath = Path.GetFullPath(cleanRetailCiaOrFaPath),
                OutputRepairedArc0Path = Path.GetFullPath(outputRepairedArc0Path),
                OutputDeltaPackPath = string.IsNullOrWhiteSpace(outputDeltaPackPath)
                    ? string.Empty
                    : Path.GetFullPath(outputDeltaPackPath)
            };

            // Open a dedicated mutable SafeGOGame instance over installedFaPath
            using (var mutableInstalled = new SafeGOGame(installedFaPath, "es"))
            {
                // 1) Repair /data/res/item/item_config.cfg.bin:
                //    Restore 100% of clean retail ITEM_REF_BEGIN + polymorphic tags while preserving
                //    all randomized PalPack, Director, Uniform, and Avatar entries from _Randomizado.cia.
                RepairItemConfig(mutableInstalled, cleanWs.Game, report);

                // 2) Repair /data/res/text/system_text_*.cfg.bin:
                //    Restore clean retail 3-column IEGO1 TEXT_INFO + TEXT_CONFIG + TEXT_WASHA while
                //    preserving localized coach name edits in place.
                RepairSystemTextFiles(mutableInstalled.IE_A.Directory, cleanWs.Game.IE_A.Directory, report);

                // 3) Repair /data/map/*/*.npc.bin:
                //    Restore retail HeadId & Type for all Type==1 (cpXXXX), Type==3 (<1000 cnXXXX),
                //    and evcXXXX cutscene actors so cutscene sprites match match models 100%.
                RepairMapNpcCoherence(mutableInstalled.IE_A.Directory, cleanWs.Game.IE_A.Directory, report);

                // 4) Sync /data/chr/model/waza/face/cnXXXXa.xc for cn0001..cn0099 if donor bustup is present
                SyncCnWazaFaceModels(mutableInstalled.IE_A.Directory, cleanWs.Game.IE_A.Directory, report);

                // 5) Ensure para_cp0010 (Victor Blade, ModelNumber=10) has Body==1 && Gender==1
                //    so bespoke kit 903 in btl0002 BtlSetting() never crashes on rig mismatch.
                EnsureVictorCp0010BodyRig(mutableInstalled, cleanWs.Game, report);

                // 6) Sanitize any NameID==0 / missing item references in team_param
                SanitizeTeamParams(mutableInstalled, cleanWs.Game, report);

                string outParent = Path.GetDirectoryName(Path.GetFullPath(outputRepairedArc0Path));
                if (!string.IsNullOrEmpty(outParent))
                    Directory.CreateDirectory(outParent);

                Arc0SafeArchive.SaveCopyTo(mutableInstalled.IE_A, outputRepairedArc0Path, installedFaPath);
            }

            // Cold-verify the repaired ie_a.fa reopens cleanly with intact ITEM_REF and 3-column system_text
            using (var verifyGame = new SafeGOGame(outputRepairedArc0Path, "es"))
            {
                var directors = verifyGame.GetItems("director");
                if (directors == null || directors.Length == 0)
                    throw new InvalidDataException("Verificación fallida: item_config reparado no contiene entrenadores.");

                byte[] itemBytes = Arc0SafeArchive.TryReadFileFromPath(verifyGame.IE_A.Directory, "/data/res/item/item_config.cfg.bin");
                byte[] cleanItemBytes = Arc0SafeArchive.TryReadFileFromPath(cleanWs.Game.IE_A.Directory, "/data/res/item/item_config.cfg.bin");
                var repCfg = new CfgBin<CfgTreeNode>();
                repCfg.Open(itemBytes);
                var clnCfg = new CfgBin<CfgTreeNode>();
                clnCfg.Open(cleanItemBytes);

                var repRef = repCfg.Entries.FindByName("ITEM_REF_BEGIN");
                var clnRef = clnCfg.Entries.FindByName("ITEM_REF_BEGIN");
                if (repRef == null || clnRef == null || repRef.Children.Count != clnRef.Children.Count)
                {
                    throw new InvalidDataException(
                        $"Verificación fallida: ITEM_REF_BEGIN ({repRef?.Children.Count ?? -1}) no coincide con retail ({clnRef?.Children.Count ?? -1}).");
                }
            }

            if (!string.IsNullOrWhiteSpace(outputDeltaPackPath))
            {
                if (Environment.GetEnvironmentVariable("IE4P_EXPORT_INSTALLED_BASE") == "1")
                {
                    File.Copy(installedFaPath, outputRepairedArc0Path + ".installed_base.fa", overwrite: true);
                }

                // Build compact IE4P delta pack DIRECTLY against the _Randomizado ie_a.fa installed in the console!
                report.DeltaPack = Arc0DeltaPatchPackBuilder.BuildPack(
                    installedFaPath,
                    outputRepairedArc0Path,
                    outputDeltaPackPath,
                    assumePriorRandomizedInstall: false);
            }

            return report;
        }

        private static void RepairItemConfig(SafeGOGame mutableInstalled, SafeGOGame cleanRetail, InstalledV122RepairReport report)
        {
            byte[] cleanItemBytes = Arc0SafeArchive.TryReadFileFromPath(cleanRetail.IE_A.Directory, "/data/res/item/item_config.cfg.bin");
            if (cleanItemBytes == null || cleanItemBytes.Length == 0)
                return;

            var kizunax = mutableInstalled.GetItems("kizunax")?.OfType<GOSupport.ItemConfigPalpackCard>().ToArray();
            var directors = mutableInstalled.GetItems("director")?.OfType<GOSupport.ItemConfigDirector>().ToArray();
            var uniforms = mutableInstalled.GetItems("uniform")?.OfType<GOSupport.ItemConfigUniform>().ToArray();
            var avatars = mutableInstalled.GetAvatars();

            // Replace item_config.cfg.bin with the pristine retail binary (restoring full ITEM_REF_BEGIN + tags)
            Arc0SafeArchive.WriteFileAtPath(mutableInstalled.IE_A.Directory, "/data/res/item/item_config.cfg.bin", cleanItemBytes);

            // Re-apply all randomized item payloads strictly in place (never calling RebuildItemReferences)
            if (kizunax != null && kizunax.Length > 0)
                mutableInstalled.SaveItems(kizunax);
            if (directors != null && directors.Length > 0)
                mutableInstalled.SaveItems(directors);
            if (uniforms != null && uniforms.Length > 0)
                mutableInstalled.SaveItems(uniforms);
            if (avatars != null && avatars.Length > 0)
                mutableInstalled.SaveAvatars(avatars);

            report.RebuiltItemConfigPreservingRefs = true;
        }

        private static void RepairSystemTextFiles(VirtualDirectory installedRoot, VirtualDirectory cleanRoot, InstalledV122RepairReport report)
        {
            foreach (string lang in SystemTextLanguages)
            {
                string path = $"/data/res/text/system_text_{lang}.cfg.bin";
                byte[] cleanBytes = Arc0SafeArchive.TryReadFileFromPath(cleanRoot, path);
                byte[] instBytes = Arc0SafeArchive.TryReadFileFromPath(installedRoot, path);
                if (cleanBytes == null || instBytes == null || cleanBytes.Length < 16 || instBytes.Length < 16)
                    continue;

                Dictionary<int, string> installedStrings = new();
                try
                {
                    var t2b = new T2bþ(instBytes);
                    foreach (var kv in t2b.Texts)
                    {
                        if (kv.Value?.Strings != null && kv.Value.Strings.Count > 0 && kv.Value.Strings[0]?.Text != null)
                        {
                            installedStrings[kv.Key] = kv.Value.Strings[0].Text;
                        }
                    }
                }
                catch
                {
                    // If installed system_text couldn't be parsed, cleanBytes alone restores valid retail text
                }

                var cleanCfg = new CfgBin<CfgTreeNode>();
                cleanCfg.Open(cleanBytes);

                if (installedStrings.Count > 0)
                {
                    var textInfoBegin = cleanCfg.Entries.FindByName("TEXT_INFO_BEGIN");
                    if (textInfoBegin != null)
                    {
                        foreach (CfgTreeNode child in textInfoBegin.Children)
                        {
                            var vars = child.Item?.Variables;
                            if (vars != null && vars.Count >= 3 &&
                                Level5CfgValueParser.TryReadHashOrId(vars[0], out int hash, out _) &&
                                installedStrings.TryGetValue(hash, out string updatedVal) &&
                                !string.IsNullOrEmpty(updatedVal))
                            {
                                vars[2].Value = updatedVal;
                            }
                        }
                    }
                }

                Arc0SafeArchive.WriteFileAtPath(installedRoot, path, cleanCfg.Save());
                report.RepairedSystemTextFilesCount++;
            }
        }

        private static void RepairMapNpcCoherence(VirtualDirectory installedRoot, VirtualDirectory cleanRoot, InstalledV122RepairReport report)
        {
            HashSet<int> evcHashes = new();
            for (int i = 0; i <= 9999; i++)
            {
                evcHashes.Add(unchecked((int)Crc32.Compute(System.Text.Encoding.ASCII.GetBytes($"evc{i:D4}"))));
            }

            var cleanNpcs = MapDataService.LoadAllMapNpcBases(cleanRoot)
                .ToDictionary(n => (n.VirtualFilePath, n.NodeIndex), n => n);

            var installedNpcs = MapDataService.LoadAllMapNpcBases(installedRoot);
            var modified = new List<MapNpcBaseEntry>();

            foreach (var inst in installedNpcs)
            {
                if (!cleanNpcs.TryGetValue((inst.VirtualFilePath, inst.NodeIndex), out var cln))
                    continue;

                bool isPlayerOrStoryActor =
                    cln.Record.Type == 1 ||
                    (cln.Record.Type == 3 && cln.Record.HeadId > 0 && cln.Record.HeadId < 1000) ||
                    evcHashes.Contains(cln.Record.NpcId);

                if (isPlayerOrStoryActor &&
                    (inst.Record.HeadId != cln.Record.HeadId || inst.Record.Type != cln.Record.Type))
                {
                    inst.Record.HeadId = cln.Record.HeadId;
                    inst.Record.Type = cln.Record.Type;
                    modified.Add(inst);
                }
            }

            if (modified.Count > 0)
            {
                MapDataService.SaveMapNpcBases(installedRoot, modified);
                report.RestoredMapNpcHeadsCount = modified.Count;
            }
        }

        private static void SyncCnWazaFaceModels(VirtualDirectory installedRoot, VirtualDirectory cleanRoot, InstalledV122RepairReport report)
        {
            // Build lookup of clean cpXXXX bustup bytes -> clean cpXXXX waza face bytes
            var bustupToWaza = new List<(byte[] Bustup, byte[] Waza)>();
            for (int num = 1; num <= 2500; num++)
            {
                string cp = $"cp{num:D4}";
                byte[] b = Arc0SafeArchive.TryReadFileFromPath(cleanRoot, $"/data/bustup/face/{cp}a.xi");
                byte[] w = Arc0SafeArchive.TryReadFileFromPath(cleanRoot, $"/data/chr/model/waza/face/{cp}a.xc");
                if (b != null && b.Length > 0 && w != null && w.Length > 0)
                    bustupToWaza.Add((b, w));
            }

            for (int cn = 1; cn < 100; cn++)
            {
                string code = $"cn{cn:D4}";
                string wazaPath = $"/data/chr/model/waza/face/{code}a.xc";
                byte[] existingCleanWaza = Arc0SafeArchive.TryReadFileFromPath(cleanRoot, wazaPath);
                if (existingCleanWaza == null || existingCleanWaza.Length == 0)
                    continue;

                byte[] instBustup = Arc0SafeArchive.TryReadFileFromPath(installedRoot, $"/data/bustup/face/{code}a.xi");
                if (instBustup == null || instBustup.Length == 0)
                    continue;

                var donor = bustupToWaza.FirstOrDefault(d => d.Bustup.Length == instBustup.Length && d.Bustup.AsSpan().SequenceEqual(instBustup));
                if (donor.Waza != null)
                {
                    Arc0SafeArchive.WriteFileAtPath(installedRoot, wazaPath, donor.Waza);
                    report.SyncedCnWazaFacesCount++;
                }
            }
        }

        private static void EnsureVictorCp0010BodyRig(SafeGOGame game, SafeGOGame cleanRetail, InstalledV122RepairReport report)
        {
            var visualProfile = VisualCoherenceValidator.AnalyzeArchive(game);
            var bases = game.GetCharabase();
            int cp0010Hash = unchecked((int)Crc32.Compute(System.Text.Encoding.ASCII.GetBytes("chara_cp0010")));
            var victor = bases.FirstOrDefault(b => b.BaseHash == cp0010Hash)
                ?? bases.FirstOrDefault(b => b.ModelNumber == 10 && visualProfile.CoherentPlayableBaseHashes.Contains(b.BaseHash))
                ?? bases.FirstOrDefault(b => b.ModelNumber == 10);
            if (victor == null || (victor.Body == 1 && victor.Gender == 1))
                return;

            VirtualDirectory root = game.IE_A.Directory;
            byte[] vBust = Arc0SafeArchive.TryReadFileFromPath(root, "/data/bustup/face/cp0010a.xi");
            byte[] vRpg = Arc0SafeArchive.TryReadFileFromPath(root, "/data/chr/model/rpg/face/cp0010m.xc");
            byte[] vWaza = Arc0SafeArchive.TryReadFileFromPath(root, "/data/chr/model/waza/face/cp0010a.xc");

            if (vBust != null && vRpg != null && vWaza != null)
            {
                foreach (var cand in bases)
                {
                    if (!visualProfile.CoherentPlayableBaseHashes.Contains(cand.BaseHash) ||
                        cand.ModelNumber <= 0 || cand.ModelNumber == 10 ||
                        ReservedManagerModels.Contains(cand.ModelNumber) ||
                        cand.Body != 1 || cand.Gender != 1)
                    {
                        continue;
                    }

                    string cCode = $"cp{cand.ModelNumber:D4}";
                    byte[] cBust = Arc0SafeArchive.TryReadFileFromPath(root, $"/data/bustup/face/{cCode}a.xi");
                    byte[] cRpg = Arc0SafeArchive.TryReadFileFromPath(root, $"/data/chr/model/rpg/face/{cCode}m.xc");
                    byte[] cWaza = Arc0SafeArchive.TryReadFileFromPath(root, $"/data/chr/model/waza/face/{cCode}a.xc");
                    if (cBust == null || cBust.Length == 0 || cRpg == null || cRpg.Length == 0 || cWaza == null || cWaza.Length == 0)
                        continue;

                    // Swap Body/Gender/Skin + all 3 visual assets atomically between cp0010 and donor
                    (victor.Body, cand.Body) = (cand.Body, victor.Body);
                    (victor.Gender, cand.Gender) = (cand.Gender, victor.Gender);
                    (victor.Skin, cand.Skin) = (cand.Skin, victor.Skin);

                    Arc0SafeArchive.WriteFileAtPath(root, "/data/bustup/face/cp0010a.xi", cBust);
                    Arc0SafeArchive.WriteFileAtPath(root, "/data/chr/model/rpg/face/cp0010m.xc", cRpg);
                    Arc0SafeArchive.WriteFileAtPath(root, "/data/chr/model/waza/face/cp0010a.xc", cWaza);

                    Arc0SafeArchive.WriteFileAtPath(root, $"/data/bustup/face/{cCode}a.xi", vBust);
                    Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/rpg/face/{cCode}m.xc", vRpg);
                    Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/waza/face/{cCode}a.xc", vWaza);

                    game.SaveCharaBase(bases);
                    report.RepairedVictorCp0010BodyRig = true;
                    return;
                }
            }

            // Fallback: restore clean retail cp0010 rig + face assets
            var cleanVictor = cleanRetail.GetCharabase().FirstOrDefault(b => b.BaseHash == victor.BaseHash);
            victor.Body = 1;
            victor.Gender = 1;
            if (cleanVictor != null)
                victor.Skin = cleanVictor.Skin;

            foreach (string rel in new[]
            {
                "/data/bustup/face/cp0010a.xi",
                "/data/chr/model/rpg/face/cp0010m.xc",
                "/data/chr/model/waza/face/cp0010a.xc"
            })
            {
                byte[] cleanAsset = Arc0SafeArchive.TryReadFileFromPath(cleanRetail.IE_A.Directory, rel);
                if (cleanAsset != null && cleanAsset.Length > 0)
                    Arc0SafeArchive.WriteFileAtPath(root, rel, cleanAsset);
            }

            game.SaveCharaBase(bases);
            report.RepairedVictorCp0010BodyRig = true;
        }

        private static void SanitizeTeamParams(SafeGOGame game, SafeGOGame cleanGame, InstalledV122RepairReport report)
        {
            var allItems = game.GetItems("all");
            bool hasPopulatedNames = allItems.Any(i => i.NameID != 0);
            var validItems = allItems
                .Where(i => i.ItemID != 0 && (!hasPopulatedNames || i.NameID != 0))
                .Select(i => i.ItemID)
                .ToHashSet();

            var cleanById = cleanGame.GetTeamParams().ToDictionary(t => t.TeamParamID, t => t);
            var teams = game.GetTeamParams();
            int fixedCount = 0;

            foreach (var tp in teams)
            {
                if (!cleanById.TryGetValue(tp.TeamParamID, out var cln))
                    continue;

                if (tp.FormationID != 0 && !validItems.Contains(tp.FormationID))
                {
                    tp.FormationID = cln.FormationID;
                    fixedCount++;
                }
                if (tp.TacticID != 0 && !validItems.Contains(tp.TacticID))
                {
                    tp.TacticID = cln.TacticID;
                    fixedCount++;
                }
                if (tp.Uniform != 0 && !validItems.Contains(tp.Uniform))
                {
                    tp.Uniform = cln.Uniform;
                    fixedCount++;
                }
                if (tp.CoachID != 0 && !validItems.Contains(tp.CoachID))
                {
                    tp.CoachID = cln.CoachID;
                    fixedCount++;
                }
            }

            if (fixedCount > 0)
            {
                game.SaveTeamParams(teams);
                report.SanitizedDummyTeamItemsCount = fixedCount;
            }
        }
    }
}
