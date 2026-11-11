using System;
using System.Collections.Generic;
using System.Linq;
using IEGORandomizer.Engine.Protection;
using Lynx.Core.Binary;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.InazumaEleven.Games.GO;
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Engine.Validation
{
    /// <summary>
    /// Captures a pre-randomization baseline of protected items across chests, routes, and drops
    /// (plus any untouched pre-existing retail dummy entries) so post-randomization validation
    /// proves zero Key Items were lost/overwritten and zero broken references were introduced.
    /// </summary>
    public sealed class ProtectedRewardsBaseline
    {
        public Dictionary<(string Path, int NodeIdx), (int Light, int Shadow)> ProtectedMapChests { get; } = new();
        public Dictionary<(string RouteFile, int CellNum), int> ProtectedRouteCells { get; } = new();
        public Dictionary<(int TeamParamId, int Slot), int> ProtectedTeamDrops { get; } = new();
        public HashSet<string> PreExistingVanillaAnomalies { get; } = new(StringComparer.Ordinal);
    }

    public sealed class IntegrityValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public List<string> Errors { get; } = new();
        public List<string> ChecksPassed { get; } = new();
    }

    /// <summary>
    /// Performs post-generation structural, referential, visual, and anti-softlock validation
    /// on a randomized <see cref="SafeGOGame"/> archive (either in memory or reopened from disk).
    /// </summary>
    public static class ArchiveIntegrityValidator
    {
        public static ProtectedRewardsBaseline CaptureBaseline(SafeGOGame game, StoryProtectionContext protection)
        {
            var baseline = new ProtectedRewardsBaseline();

            foreach (var chest in MapDataService.LoadAllTreasureChests(game.IE_A.Directory))
            {
                var r = chest.Record;
                if (protection.ProtectedTBoxFlags.Contains(r.TBoxFlag) ||
                    protection.IsProtectedItem(r.ItemIDLight) ||
                    protection.IsProtectedItem(r.ItemIDShadow))
                {
                    baseline.ProtectedMapChests[(chest.VirtualFilePath, chest.NodeIndex)] = (r.ItemIDLight, r.ItemIDShadow);
                }
            }

            var soccerDir = game.GetDirectory("soccer");
            if (soccerDir != null)
            {
                foreach (string fileName in soccerDir.Files.Keys.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    if (!fileName.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fileName, "soccer_config.cfg.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (RouteDataService.TryReadRouteFile(soccerDir, fileName, out RouteFileData routeData))
                    {
                        foreach (var c in routeData.Cells)
                        {
                            if ((c.CellType == 3 || c.CellType == 4) && protection.IsProtectedItem(c.ContentID))
                            {
                                baseline.ProtectedRouteCells[(fileName, c.CellNum)] = c.ContentID;
                            }
                        }
                    }
                }
            }

            foreach (var tp in game.GetTeamParams())
            {
                for (int s = 1; s <= 6; s++)
                {
                    int dropId = TeamSlotAccessor.GetTeamDrop(tp, s);
                    if (protection.IsProtectedItem(dropId))
                    {
                        baseline.ProtectedTeamDrops[(tp.TeamParamID, s)] = dropId;
                    }
                }
            }

            // Record any pre-existing untouched anomalies in vanilla retail tables so we only flag
            // errors introduced by randomization (never blocking on unused vanilla test/dummy rows)
            CollectReferentialAnomalies(game, baseline.PreExistingVanillaAnomalies);

            return baseline;
        }

        public static IntegrityValidationResult Validate(
            SafeGOGame game,
            StoryProtectionContext protection,
            ProtectedRewardsBaseline baseline)
        {
            var result = new IntegrityValidationResult();

            var charaBases = game.GetCharabase();
            var charaParams = game.GetCharaparams();
            var skills = game.GetSkillConfigs(false);
            var skillTable = game.GetSkillTable();
            var storyTeams = game.GetStoryTeamsOnly();
            var encountTeams = game.GetEncounterTeamsOnly();
            var teamParams = game.GetTeamParams();
            var palpacks = (game.GetItems("kizunax") ?? Array.Empty<IItemConfig>())
                .OfType<GOSupport.ItemConfigPalpackCard>()
                .ToArray();

            // 1. Engine ModelNumber safety (< 2048) for playable soccer models (CharaBaseType == 1, "cp%04d")
            // Verified against retail chara_base.cfg.bin: CharaBaseType == 1 has 1..1826 (< 2048),
            // whereas CharaBaseType == 3 (overworld NPCs "cn%04d") legitimately uses 9001..9005.
            int playableBaseCount = 0;
            foreach (var cb in charaBases)
            {
                if (cb.CharaBaseType != 1)
                    continue;

                playableBaseCount++;
                if (cb.ModelNumber < 0 || cb.ModelNumber >= VisualCoherenceValidator.MaxEngineModelNumberExclusive)
                {
                    result.Errors.Add(
                        $"ModelNumber inválido {cb.ModelNumber} en CHARA_BASE jugable 0x{cb.BaseHash:X8} (límite del motor < 2048).");
                }
            }
            result.ChecksPassed.Add(
                $"Verificados {playableBaseCount} modelos jugables CHARA_BASE (ModelNumber < 2048; {charaBases.Length} registros totales).");

            // 2-5. Referential integrity across CharaParam, SkillTable, StoryTeam, EncountTeam, and PalPack
            var postAnomalies = new HashSet<string>(StringComparer.Ordinal);
            CollectReferentialAnomalies(game, postAnomalies);

            foreach (string anomaly in postAnomalies)
            {
                if (baseline == null || !baseline.PreExistingVanillaAnomalies.Contains(anomaly))
                {
                    result.Errors.Add(anomaly);
                }
            }

            // Always strictly enforce anti-self-cycle on PalPack cards regardless of baseline
            foreach (var card in palpacks)
            {
                for (int s = 1; s <= 4; s++)
                {
                    int cType = (int)typeof(GOSupport.ItemConfigPalpackCard).GetProperty($"PrerequisitesType{s}")!.GetValue(card)!;
                    int cId = (int)typeof(GOSupport.ItemConfigPalpackCard).GetProperty($"PrerequisitesId{s}")!.GetValue(card)!;
                    if (cType == 2 && cId != 0 && cId == card.RecrutedCharacterId)
                    {
                        result.Errors.Add($"Fichaje PalPack 0x{card.ItemID:X8} tiene autorrequisito circular sobre 0x{cId:X8}.");
                    }
                }
            }

            result.ChecksPassed.Add($"Verificada integridad referencial de {charaParams.Length} jugadores en CHARA_PARAM.");
            result.ChecksPassed.Add($"Verificados {skillTable.Length} nodos de supertécnicas en SKILL_TABLE.");
            result.ChecksPassed.Add($"Verificadas alineaciones de {storyTeams.Length} equipos y {encountTeams.Length} pachangas.");
            result.ChecksPassed.Add($"Verificadas {palpacks.Length} cartas de fichaje PalPack sin ciclos.");

            // 6. Key Item & FUNCPOINT chest preservation
            if (baseline != null)
            {
                var currentChests = MapDataService.LoadAllTreasureChests(game.IE_A.Directory)
                    .ToDictionary(c => (c.VirtualFilePath, c.NodeIndex), c => (c.Record.ItemIDLight, c.Record.ItemIDShadow));

                foreach (var kvp in baseline.ProtectedMapChests)
                {
                    if (!currentChests.TryGetValue(kvp.Key, out var actual) ||
                        actual.ItemIDLight != kvp.Value.Light ||
                        actual.ItemIDShadow != kvp.Value.Shadow)
                    {
                        result.Errors.Add($"Cofre protegido alterado en {kvp.Key.Path} [#{kvp.Key.NodeIdx}].");
                    }
                }

                var soccerDir = game.GetDirectory("soccer");
                foreach (var kvp in baseline.ProtectedRouteCells)
                {
                    if (soccerDir == null ||
                        !RouteDataService.TryReadRouteFile(soccerDir, kvp.Key.RouteFile, out RouteFileData routeData))
                    {
                        result.Errors.Add($"Ruta protegida {kvp.Key.RouteFile} inaccesible tras la generación.");
                        continue;
                    }

                    var cell = routeData.Cells.FirstOrDefault(c => c.CellNum == kvp.Key.CellNum);
                    if (cell == null || cell.ContentID != kvp.Value)
                    {
                        result.Errors.Add($"Cofre de cadena protegido alterado en {kvp.Key.RouteFile} celda #{kvp.Key.CellNum}.");
                    }
                }

                var tpById = teamParams.GroupBy(t => t.TeamParamID).ToDictionary(g => g.Key, g => g.First());
                foreach (var kvp in baseline.ProtectedTeamDrops)
                {
                    if (!tpById.TryGetValue(kvp.Key.TeamParamId, out var tp))
                    {
                        result.Errors.Add($"TeamParam protegido 0x{kvp.Key.TeamParamId:X8} ausente.");
                        continue;
                    }
                    int actualDrop = TeamSlotAccessor.GetTeamDrop(tp, kvp.Key.Slot);
                    if (actualDrop != kvp.Value)
                    {
                        result.Errors.Add($"Drop de objeto clave alterado en TeamParam 0x{kvp.Key.TeamParamId:X8} slot #{kvp.Key.Slot}.");
                    }
                }

                result.ChecksPassed.Add(
                    $"Verificada preservación íntegra de objetos clave ({baseline.ProtectedMapChests.Count} cofres de mapa, " +
                    $"{baseline.ProtectedRouteCells.Count} cofres de cadena, {baseline.ProtectedTeamDrops.Count} drops protegidos).");
            }

            return result;
        }

        private static void CollectReferentialAnomalies(SafeGOGame game, HashSet<string> sink)
        {
            var charaBases = game.GetCharabase();
            var charaParams = game.GetCharaparams();
            var skills = game.GetSkillConfigs(false);
            var skillTable = game.GetSkillTable();
            var storyTeams = game.GetStoryTeamsOnly();
            var encountTeams = game.GetEncounterTeamsOnly();
            var palpacks = (game.GetItems("kizunax") ?? Array.Empty<IItemConfig>())
                .OfType<GOSupport.ItemConfigPalpackCard>()
                .ToArray();

            var baseHashes = new HashSet<int>(charaBases.Select(b => b.BaseHash).Where(h => h != 0));
            var paramHashes = new HashSet<int>(charaParams.Select(p => p.ParamHash).Where(h => h != 0));
            var skillHashes = new HashSet<int>(skills.Select(s => s.SkillHash).Where(h => h != 0));

            foreach (var cp in charaParams)
            {
                if (cp.ParamHash == 0)
                    continue;

                if (cp.BaseHash != 0 && !baseHashes.Contains(cp.BaseHash))
                {
                    sink.Add($"CharaParam 0x{cp.ParamHash:X8} apunta a BaseHash inexistente 0x{cp.BaseHash:X8}.");
                }

                if (cp.SpecialMoveCount > 0 &&
                    (cp.SpecialMoveOffset < 0 || cp.SpecialMoveOffset + cp.SpecialMoveCount > skillTable.Length))
                {
                    sink.Add($"CharaParam 0x{cp.ParamHash:X8} excede límites de SKILL_TABLE.");
                }
            }

            foreach (var st in skillTable)
            {
                if (st.SkillHash != 0 && !skillHashes.Contains(st.SkillHash))
                {
                    sink.Add($"SKILL_TABLE #{st.SkillIndex} referencia SkillHash inexistente 0x{st.SkillHash:X8}.");
                }
            }

            foreach (var team in storyTeams)
            {
                for (int slot = 1; slot <= 16; slot++)
                {
                    int pid = TeamSlotAccessor.GetStoryPlayer(team, slot);
                    if (pid != 0 && !paramHashes.Contains(pid))
                    {
                        sink.Add($"STORY_TEAM 0x{team.TeamConfigID:X8} [slot {slot}] referencia jugador inexistente 0x{pid:X8}.");
                    }
                }
            }

            foreach (var team in encountTeams)
            {
                for (int slot = 1; slot <= 5; slot++)
                {
                    int pid = TeamSlotAccessor.GetEncountPlayer(team, slot);
                    if (pid != 0 && !paramHashes.Contains(pid))
                    {
                        sink.Add($"ENCOUNT_TEAM 0x{team.TeamConfigID:X8} [slot {slot}] referencia jugador inexistente 0x{pid:X8}.");
                    }
                }
            }

            foreach (var card in palpacks)
            {
                if (card.RecrutedCharacterId != 0 && !paramHashes.Contains(card.RecrutedCharacterId))
                {
                    sink.Add($"Fichaje PalPack 0x{card.ItemID:X8} apunta a jugador inexistente 0x{card.RecrutedCharacterId:X8}.");
                }
            }

            // Verify 5v5 Pachanga vs 11v11 Match formation/category invariants (prevents first-pachanga btl_manager freeze!)
            var teamParams = game.GetTeamParams();
            var soccers = game.GetSoccers() ?? Array.Empty<ISoccerInfo>();
            var allItems = game.GetItems("all") ?? Array.Empty<IItemConfig>();
            var itemById = allItems.Where(i => i.ItemID != 0).GroupBy(i => i.ItemID).ToDictionary(g => g.Key, g => g.First());

            var storyIds = new HashSet<int>(storyTeams.Select(t => t.TeamConfigID).Where(id => id != 0));
            var encountIds = new HashSet<int>(encountTeams.Select(t => t.TeamConfigID).Where(id => id != 0));
            var miniBattleParamIds = new HashSet<int>(
                soccers.Where(s => s != null && s.SoccerMode == 1 && s.TeamParamID != 0).Select(s => s.TeamParamID));
            var fullMatchParamIds = new HashSet<int>(
                soccers.Where(s => s != null && s.SoccerMode == 2 && s.TeamParamID != 0).Select(s => s.TeamParamID));
            var ifaLookup = Lynx.Core.RomFs.Level5IdHelper.BuildPrefixLookup("ifa");
            var ifbLookup = Lynx.Core.RomFs.Level5IdHelper.BuildPrefixLookup("ifb");

            foreach (var tp in teamParams)
            {
                if (tp.TeamParamID == 0)
                    continue;

                bool isPachanga = IEGORandomizer.Engine.Modules.TeamRandomizerModule.IsMiniBattleTeamParam(
                    tp,
                    storyIds,
                    encountIds,
                    miniBattleParamIds,
                    fullMatchParamIds);

                if (tp.FormationID != 0)
                {
                    if (itemById.TryGetValue(tp.FormationID, out var fItem) && fItem.ItemCategory == 17)
                    {
                        sink.Add($"TeamParam 0x{tp.TeamParamID:X8} tiene objeto PalPack (Cat.17) 0x{tp.FormationID:X8} como Formación.");
                    }

                    if (isPachanga && (ifaLookup.ContainsKey(tp.FormationID) ||
                                       (itemById.TryGetValue(tp.FormationID, out var mfItem) && mfItem.ItemCategory == 19)))
                    {
                        sink.Add($"Pachanga 5v5 TeamParam 0x{tp.TeamParamID:X8} tiene Formación 11v11 (ifa/Cat.19) 0x{tp.FormationID:X8} (causa cuelgue en btl_manager).");
                    }
                    else if (!isPachanga && storyIds.Contains(tp.TeamConfigID) &&
                             (ifbLookup.ContainsKey(tp.FormationID) ||
                              (itemById.TryGetValue(tp.FormationID, out var bfItem) && bfItem.ItemCategory == 20)))
                    {
                        sink.Add($"Partido 11v11 TeamParam 0x{tp.TeamParamID:X8} tiene Formación 5v5 (ifb/Cat.20) 0x{tp.FormationID:X8}.");
                    }
                }

                if (tp.TacticID != 0 && itemById.TryGetValue(tp.TacticID, out var tItem) && tItem.ItemCategory == 18)
                {
                    sink.Add($"TeamParam 0x{tp.TeamParamID:X8} tiene Espíritu Guerrero (Cat.18) 0x{tp.TacticID:X8} como Supertáctica.");
                }
            }

            // Verify overworld ENCOUNT_INFO (*.encount.bin) PlayerID1..5 referential validity
            foreach (var sp in MapDataService.LoadAllEncounterSpawns(game.IE_A.Directory))
            {
                int[] pids = { sp.Record.PlayerID1, sp.Record.PlayerID2, sp.Record.PlayerID3, sp.Record.PlayerID4, sp.Record.PlayerID5 };
                for (int i = 0; i < pids.Length; i++)
                {
                    int pid = pids[i];
                    if (pid != 0 && !paramHashes.Contains(pid) && !baseHashes.Contains(pid))
                    {
                        sink.Add($"ENCOUNT_INFO {sp.VirtualFilePath} [#{sp.NodeIndex} slot {i + 1}] referencia ID inexistente 0x{pid:X8}.");
                    }
                }
            }
        }
    }
}
