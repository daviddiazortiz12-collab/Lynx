using System;
using System.IO;
using System.Linq;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.Core.RomFs;
using Lynx.Core.Scripts;
using Lynx.InazumaEleven.Data;
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Tests
{
    public sealed class LynxCoreIoTests
    {
        [Fact]
        public void EmbeddedCommonJsonMetadata_LoadsCleanlyWithoutWinForms()
        {
            Assert.True(CommonData.Get(CommonData.PlayerPositions).Length >= 4, "PlayerPositions.json should load.");
            Assert.True(CommonData.Get(CommonData.Elements).Length >= 4, "Elements.json should load.");
            Assert.True(CommonData.Get(CommonData.Bodies).Length >= 8, "Bodies.json should load.");
            Assert.True(CommonData.Get(CommonData.Skins).Length >= 10, "Skins.json should load.");
        }

        [Fact]
        public void SafeGOGame_ReadsAndSavesCopy_WithoutTouchingOriginalSource()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_test_io_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string sourcePath = Path.Combine(tempDir, "ie_a.fa");
                string copyPath = Path.Combine(tempDir, "out", "ie_a.fa");
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(sourcePath);

                using var workspace = GameWorkspace.Open(sourcePath, GameEdition.Light);
                Assert.Equal("es", workspace.Game.LanguageCode);

                var charaBases = workspace.Game.GetCharabase();
                var charaParams = workspace.Game.GetCharaparams();
                var skills = workspace.Game.GetSkillConfigs(false);
                var storyTeams = workspace.Game.GetStoryTeamsOnly();
                var encountTeams = workspace.Game.GetEncounterTeamsOnly();
                var palpacks = workspace.Game.GetItems("kizunax");
                var keyItems = workspace.Game.GetItems("important");

                Assert.Equal(27, charaBases.Length);
                Assert.Equal(27, charaParams.Length);
                Assert.Equal(20, skills.Length);
                Assert.Equal(3, storyTeams.Length);
                Assert.Equal(2, encountTeams.Length);
                Assert.Equal(6, palpacks.Length);
                Assert.Equal(1, keyItems.Length);

                // In-place Save() MUST be blocked
                Assert.Throws<InvalidOperationException>(() => workspace.Game.Save());
                // Overwriting source path via SaveCopyTo MUST also be blocked
                Assert.Throws<InvalidOperationException>(() => workspace.Game.SaveCopyTo(sourcePath));

                // Mutate a stat in memory and save to separate copy
                charaParams[0].Kick += 15;
                workspace.Game.SaveCharaparams(charaParams);
                workspace.Game.SaveCopyTo(copyPath);

                // Source file MUST remain byte-for-byte identical
                Assert.True(workspace.VerifySourceUnchanged(), "Original ie_a.fa SHA-256 must remain untouched.");

                // Output copy MUST reopen cleanly and contain our change
                using var reopened = new SafeGOGame(copyPath);
                Assert.Equal(charaParams[0].Kick, reopened.GetCharaparams()[0].Kick);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Nintendo3dsRomReader_ExtractsIeAFaFromDecrypted3ds_WithoutModifyingRom()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_test_3ds_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string rawIea = Path.Combine(tempDir, "raw_ie_a.fa");
                string rom3ds = Path.Combine(tempDir, "InazumaElevenGO_Sombra.3ds");
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(rawIea);
                SyntheticGameArchiveBuilder.WrapIeAFaInDecrypted3dsContainer(
                    rawIea,
                    rom3ds,
                    titleId: 0x0004000000113000UL,
                    productCode: "CTR-P-AE5P");

                using var workspace = GameWorkspace.Open(rom3ds, GameEdition.Auto);
                Assert.Equal(GameInputKind.Nintendo3dsRom, workspace.InputKind);
                Assert.Equal(GameEdition.Shadow, workspace.EffectiveEdition);
                Assert.Equal("0004000000113000", workspace.ResolvedTitleId);
                Assert.Equal("CTR-P-AE5P", workspace.ProductCode);
                Assert.Equal(27, workspace.Game.GetCharaparams().Length);
                Assert.True(workspace.VerifySourceUnchanged(), "Original .3ds file must remain untouched.");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void MapAndNutbAuditors_ReadChestsEncountersFuncptAndSquirrelScripts()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "iego_test_audit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string sourcePath = Path.Combine(tempDir, "ie_a.fa");
                SyntheticGameArchiveBuilder.CreateRealisticIeAFa(sourcePath);

                using var game = new SafeGOGame(sourcePath);
                var chests = MapDataService.LoadAllTreasureChests(game.IE_A.Directory);
                var spawns = MapDataService.LoadAllEncounterSpawns(game.IE_A.Directory);
                var mapDeps = MapDataService.ScanMapProgressionDependencies(game.IE_A.Directory);
                var nutbReport = NutbScriptAuditor.AuditArchive(game.IE_A.Directory);

                Assert.Equal(4, chests.Count);
                Assert.Equal(2, spawns.Count);
                Assert.True(mapDeps.ProtectedTBoxFlags.Contains(999), "Should detect FUNCPOINT TBoxBitCheck=999.");

                Assert.Equal(1, nutbReport.ScannedScriptCount);
                Assert.True(nutbReport.NativeCommands.Contains("CMND_START_SOCCER"));
                Assert.True(nutbReport.ReferencedMatchIds.Contains("btl0001"));
                Assert.True(nutbReport.ReferencedCharacterIds.Contains("cp0008"));
                Assert.True(nutbReport.ReferencedSkillIds.Contains("whs0001"));
                Assert.True(nutbReport.ReferencedAvatarIds.Contains("ck0001"));
                Assert.True(nutbReport.ReferencedItemIds.Contains("iik0001"));
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
