using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using IEGORandomizer.Engine;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Protection;
using IEGORandomizer.Engine.Validation;
using Lynx.Core.Binary;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.Core.RomFs;
using StudioElevenLib.Level5.Binary;
using StudioElevenLib.Level5.Binary.Collections;
using StudioElevenLib.Level5.Binary.Logic;

namespace IEGORandomizer.Tests
{
    public sealed class Level5IdentifierRegressionTests
    {
        [Fact]
        public void Regression_ScrBr0005s_TalkConfigAndRoute_ParsesAndRandomizesWithoutDataLoss()
        {
            string tempDir = CreateTempDir();
            try
            {
                string romFsDir = Path.Combine(tempDir, "ie_luz_romfs");
                string outDir = Path.Combine(tempDir, "out_mod");
                Directory.CreateDirectory(romFsDir);

                string ieAPath = Path.Combine(romFsDir, "ie_a.fa");
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(ieAPath);
                string sourceHashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ieAPath)));

                // 1. Direct unit check on the exact identifier "scr_br_0005_s"
                var routeVar = new Variable(CfgValueType.String, "scr_br_0005_s");
                bool ok = Level5CfgValueParser.TryReadHashOrId(routeVar, out int routeHash, out string textualId);
                Assert.True(ok, "Level5CfgValueParser must accept valid Level-5 route identifier 'scr_br_0005_s'.");
                Assert.Equal("scr_br_0005_s", textualId);
                Assert.Equal(Level5IdHelper.ComputeCrc32Utf8("scr_br_0005_s"), routeHash);

                // Strict integer parser must NOT arbitrarily coerce "scr_br_0005_s" to 0
                Assert.False(
                    Level5CfgValueParser.TryReadInt32(routeVar, out _),
                    "TryReadInt32 must distinguish textual identifiers from numeric integers.");

                // 2. Verify StoryProtectionService at the 25% stage preserves "scr_br_0005_s"
                using (var game = new SafeGOGame(ieAPath))
                {
                    var protService = new StoryProtectionService();
                    StoryProtectionContext ctx = protService.BuildContext(game, GameEdition.Light, new ProtectionRandomizerOptions());
                    Assert.True(
                        ctx.MapDependencies.ReferencedRouteIds.Contains("scr_br_0005_s"),
                        "StoryProtectionContext must record 'scr_br_0005_s' in ReferencedRouteIds.");
                    Assert.True(
                        ctx.MapDependencies.ReferencedRouteHashes.Contains(Level5IdHelper.ComputeCrc32Utf8("scr_br_0005_s")),
                        "StoryProtectionContext must record CRC32('scr_br_0005_s') in ReferencedRouteHashes.");
                }

                // 3. End-to-end 0% -> 100% generation (including shops & competition routes)
                var options = new RandomizerOptions
                {
                    Seed = "REGRESSION-SCR-BR-0005-S",
                    Edition = GameEdition.Light,
                    BlindPlayMode = true
                };
                options.Rewards.RandomizeShopItems = true;

                var coordinator = new RandomizerCoordinator();
                var report = coordinator.GenerateRun(romFsDir, outDir, options);

                Assert.True(report.SourceImmutabilityVerified);
                Assert.True(report.OutputIntegrityVerified);
                Assert.Equal(sourceHashBefore, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ieAPath))));
                Assert.True(report.RandomizedShopEntriesCount >= 2, "Shop slots in shop_01.cfg.bin must be randomized.");

                // 4. Reopen exported ie_a.fa and prove scr_br_0005_s.cfg.bin & shop_01.cfg.bin kept exact CfgValueType tags
                string exportedIeA = Path.Combine(outDir, "Standalone", "ie_a.fa");
                using (var outGame = new SafeGOGame(exportedIeA))
                {
                    byte[] rawRouteBytes = Arc0SafeArchive.TryReadFileFromPath(
                        outGame.IE_A.Directory,
                        "/data/res/soccer/scr_br_0005_s.cfg.bin");
                    Assert.NotNull(rawRouteBytes);

                    var rawCfg = new CfgBin<CfgTreeNode>();
                    rawCfg.Open(rawRouteBytes);
                    var begin = rawCfg.Entries.FindByName("ROUTE_CONFIG_BEGIN");
                    Assert.NotNull(begin);
                    Assert.Equal("scr_br_0005_s", begin.Item.Variables[1].Value as string);

                    var cells = begin.Children.OfType<CfgTreeNode>().ToList();
                    Assert.Equal(3, cells.Count);

                    // Cell 0 (battle node): untouched String "btl0102", Int 0 PhaseAppear, null MatchTextLock preserved!
                    Assert.Equal(CfgValueType.String, cells[0].Item.Variables[2].Type);
                    Assert.Equal("btl0102", cells[0].Item.Variables[2].Value as string);
                    Assert.Equal(CfgValueType.Int, cells[0].Item.Variables[8].Type);
                    Assert.Equal(0, Convert.ToInt32(cells[0].Item.Variables[8].Value));

                    // Cell 1 (unlocked route chest): ContentID randomized, Base64 PhaseAppear ("AQIDBA==") preserved!
                    Assert.Equal("AQIDBA==", cells[1].Item.Variables[8].Value as string);
                    Assert.Equal(CfgValueType.Int, cells[1].Item.Variables[9].Type);

                    // Cell 2 (protected Key Item chest): ContentID == iik0001 and MatchTextLock == "LOCK_ROUTE_05" preserved!
                    Assert.Equal(
                        Level5IdHelper.ComputeCrc32Utf8("iik0001"),
                        Convert.ToInt32(cells[2].Item.Variables[2].Value));
                    Assert.Equal("LOCK_ROUTE_05", cells[2].Item.Variables[11].Value as string);
                }
            }
            finally
            {
                SafeDeleteDir(tempDir);
            }
        }

        [Fact]
        public void Regression_Level5CfgValueParser_RejectsCorruptStringsWithoutArbitraryFallback()
        {
            // Malformed numbers or corrupt strings with spaces/invalid chars MUST fail cleanly
            // rather than silently returning 0 or deleting data
            var malformedNum = new Variable(CfgValueType.String, "123abc");
            var corruptText = new Variable(CfgValueType.String, "not a valid id!");

            Assert.False(Level5CfgValueParser.TryReadHashOrId(malformedNum, out _, out _));
            Assert.False(Level5CfgValueParser.TryReadHashOrId(corruptText, out _, out _));

            Assert.Throws<FormatException>(() =>
                Level5CfgValueParser.ReadHashOrIdOrThrow(malformedNum, "test.cfg.bin:ROW[0]", out _));
            Assert.Throws<FormatException>(() =>
                Level5CfgValueParser.ReadHashOrIdOrThrow(corruptText, "test.cfg.bin:ROW[1]", out _));

            // Valid hex, signed integer, null/empty, and valid Level-5 identifiers succeed cleanly
            Assert.Equal(
                unchecked((int)0xDEADBEEFu),
                Level5CfgValueParser.ReadHashOrIdOrThrow(new Variable(CfgValueType.String, "0xDEADBEEF"), "hex", out string hexId));
            Assert.True(hexId == null);

            Assert.Equal(
                -1,
                Level5CfgValueParser.ReadHashOrIdOrThrow(new Variable(CfgValueType.String, "-1"), "neg", out _));
            Assert.Equal(
                0,
                Level5CfgValueParser.ReadHashOrIdOrThrow(new Variable(CfgValueType.String, null), "null", out _));
        }

        [Fact]
        public void Regression_FuncpointPtree_StringItemId_ProtectsItemWithoutThrowing()
        {
            var cfg = new CfgBin<PtreeNode> { Encoding = Encoding.UTF8 };
            var root = new PtreeNode(
                new Entry("PTREE", new List<Variable> { new Variable(CfgValueType.String, "FUNCPOINT") }),
                1);
            var ev = new PtreeNode(
                new Entry("PTREE", new List<Variable> { new Variable(CfgValueType.String, "EVENT_DEF") }),
                2);
            ev.AddChild(new PtreeNode(
                new Entry("PTVAL", new List<Variable>
                {
                    new Variable(CfgValueType.String, "iik0001"),
                    new Variable(CfgValueType.String, "ItemID")
                }),
                3));
            ev.AddChild(new PtreeNode(
                new Entry("PTVAL", new List<Variable>
                {
                    new Variable(CfgValueType.Int, 777),
                    new Variable(CfgValueType.String, "TBoxBitCheck")
                }),
                3));
            root.AddChild(ev);
            cfg.Entries.AddChild(root);

            var vdir = new StudioElevenLib.Tools.VirtualDirectory("");
            Arc0SafeArchive.WriteFileAtPath(vdir, "/data/map/m1010/m1010_funcpt.bin", cfg.Save());

            MapProgressionDependencies deps = MapDataService.ScanMapProgressionDependencies(vdir);
            Assert.True(deps.ReferencedFuncptItemIds.Contains(Level5IdHelper.ComputeCrc32Utf8("iik0001")));
            Assert.True(deps.ProtectedTBoxFlags.Contains(777));
        }

        [Fact]
        public void Regression_SpanishLocaleAndShiftJisTalkWithScrBr0005s_CompletesFullRun()
        {
            var prevCulture = System.Globalization.CultureInfo.CurrentCulture;
            string tempDir = CreateTempDir();
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-ES");

                string romFsDir = Path.Combine(tempDir, "ie luz romFS");
                string outDir = Path.Combine(tempDir, "out_luz");
                Directory.CreateDirectory(romFsDir);
                string ieAPath = Path.Combine(romFsDir, "ie_a.fa");
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(ieAPath);

                // Inject Shift-JIS .talk.bin containing "scr_br_0005_s" and "scr_br_0005_d"
                var arc = Arc0SafeArchive.OpenReadOnly(ieAPath);
                Encoding shiftJis = Encoding.GetEncoding("SHIFT-JIS");
                var talkCfg = new CfgBin<CfgTreeNode> { Encoding = shiftJis };
                var tb = new CfgTreeNode(new Entry("TALK_CONFIG_BEGIN", new List<Variable> { new Variable(CfgValueType.Int, 2) }));
                tb.AddChild(new CfgTreeNode(new Entry("TALK_CONFIG", new List<Variable>
                {
                    new Variable(CfgValueType.Int, 7),
                    new Variable(CfgValueType.String, "scr_br_0005_s"),
                    new Variable(CfgValueType.Int, 0),
                    new Variable(CfgValueType.Int, 0)
                })));
                tb.AddChild(new CfgTreeNode(new Entry("TALK_CONFIG", new List<Variable>
                {
                    new Variable(CfgValueType.Int, 7),
                    new Variable(CfgValueType.String, "scr_br_0005_d"),
                    new Variable(CfgValueType.Int, 0),
                    new Variable(CfgValueType.Int, 0)
                })));
                talkCfg.Entries.AddChild(tb);
                talkCfg.Entries.AddChild(new CfgTreeNode(new Entry("TALK_CONFIG_END")));
                Arc0SafeArchive.WriteFileAtPath(arc.Directory, "/data/map/m1010/m1010.talk.bin", talkCfg.Save());

                string patched = ieAPath + ".sjis";
                new StudioElevenLib.Level5.Archive.ARC0.ARC0Writer(arc.Directory, arc.Header).Save(patched);
                arc.Close();
                File.Move(patched, ieAPath, overwrite: true);

                var opts = new RandomizerOptions { Seed = "ES-LOCALE-SJIS-0005S", Edition = GameEdition.Light };
                opts.Rewards.RandomizeShopItems = true;
                var report = new RandomizerCoordinator().GenerateRun(romFsDir, outDir, opts);

                Assert.True(report.SourceImmutabilityVerified);
                Assert.True(report.OutputIntegrityVerified);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = prevCulture;
                SafeDeleteDir(tempDir);
            }
        }

        private static string CreateTempDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "iego_reg_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void SafeDeleteDir(string path)
        {
            if (Directory.Exists(path))
            {
                try { Directory.Delete(path, recursive: true); } catch { /* ignore */ }
            }
        }
    }
}
