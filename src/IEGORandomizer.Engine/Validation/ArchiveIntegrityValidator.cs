using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IEGORandomizer.Engine.Protection;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.InazumaEleven.Games.GO;
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Engine.Validation
{
    /// <summary>
    /// Captures a pre-randomization baseline of protected items across chests, routes, and drops
    /// so post-randomization validation can prove zero Key Items were lost or overwritten.
    /// </summary>
    public sealed class ProtectedRewardsBaseline
    {
        public Dictionary<(string Path, int NodeIdx), (int Light, int Shadow)> ProtectedMapChests { get; } = new();
        public Dictionary<(string RouteFile, int CellNum), int> ProtectedRouteCells { get; } = new();
        public Dictionary<(int TeamParamId, int Slot), int> ProtectedTeamDrops { get; } = new();
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
                foreach (string fileName in soccerDir.Files.Keys)
                {
                    if (!fileName.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fileName, "soccer_config.cfg.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var (cells, _) = game.GetRoutes("/" + fileName.TrimStart('/'));
                        foreach (var c in cells)
                        {
                            if ((c.CellType == 3 || c.CellType == 4) && protection.IsProtectedItem(c.ContentID))
                            {
                                baseline.ProtectedRouteCells[(fileName, c.CellNum)] = c.ContentID;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore non-route files
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

            var baseHashes = new HashSet<int>(charaBases.Select(b => b.BaseHash).Where(h => h != 0));
            var paramHashes = new HashSet<int>(charaParams.Select(p => p.ParamHash).Where(h => h != 0));
            var skillHashes = new HashSet<int>(skills.Select(s => s.SkillHash).Where(h => h != 0));

            // 1. Engine ModelNumber safety (< 2048)
            foreach (var cb in charaBases)
            {
                if (cb.ModelNumber < 0 || cb.ModelNumber >= VisualCoherenceValidator.MaxEngineModelNumberExclusive)
                {
                    result.Errors.Add(
                        $"ModelNumber inválido {cb.ModelNumber} en CHARA_BASE 0x{cb.BaseHash:X8} (límite del motor < 2048).");
                }
            }
            result.ChecksPassed.Add($"Verificados {charaBases.Length} registros CHARA_BASE (ModelNumber < 2048).");

            // 2. CharaParam -> CharaBase & SkillTable bounds
            foreach (var cp in charaParams)
            {
                if (cp.ParamHash == 0)
                    continue;

                if (cp.BaseHash != 0 && !baseHashes.Contains(cp.BaseHash))
                {
                    result.Errors.Add($"CharaParam 0x{cp.ParamHash:X8} apunta a BaseHash inexistente 0x{cp.BaseHash:X8}.");
                }

                if (cp.SpecialMoveCount > 0 &&
                    (cp.SpecialMoveOffset < 0 || cp.SpecialMoveOffset + cp.SpecialMoveCount > skillTable.Length))
                {
                    result.Errors.Add($"CharaParam 0x{cp.ParamHash:X8} excede límites de SKILL_TABLE.");
                }
            }
            result.ChecksPassed.Add($"Verificada integridad referencial de {charaParams.Length} jugadores en CHARA_PARAM.");

            // 3. SkillTable -> SkillConfig
            foreach (var st in skillTable)
            {
                if (st.SkillHash != 0 && !skillHashes.Contains(st.SkillHash))
                {
                    result.Errors.Add($"SKILL_TABLE #{st.SkillIndex} referencia SkillHash inexistente 0x{st.SkillHash:X8}.");
                }
            }
            result.ChecksPassed.Add($"Verificados {skillTable.Length} nodos de supertécnicas en SKILL_TABLE.");

            // 4. Team slots -> CharaParam
            foreach (var team in storyTeams)
            {
                for (int slot = 1; slot <= 16; slot++)
                {
                    int pid = TeamSlotAccessor.GetStoryPlayer(team, slot);
                    if (pid != 0 && !paramHashes.Contains(pid))
                    {
                        result.Errors.Add($"STORY_TEAM 0x{team.TeamConfigID:X8} referencia jugador inexistente 0x{pid:X8}.");
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
                        result.Errors.Add($"ENCOUNT_TEAM 0x{team.TeamConfigID:X8} referencia jugador inexistente 0x{pid:X8}.");
                    }
                }
            }
            result.ChecksPassed.Add($"Verificadas alineaciones de {storyTeams.Length} equipos y {encountTeams.Length} pachangas.");

            // 5. PalPack Cards -> CharaParam & anti-self-cycle check
            foreach (var card in palpacks)
            {
                if (card.RecrutedCharacterId != 0 && !paramHashes.Contains(card.RecrutedCharacterId))
                {
                    result.Errors.Add($"Fichaje PalPack 0x{card.ItemID:X8} apunta a jugador inexistente 0x{card.RecrutedCharacterId:X8}.");
                }

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
                    var (cells, _) = game.GetRoutes("/" + kvp.Key.RouteFile.TrimStart('/'));
                    var cell = cells.FirstOrDefault(c => c.CellNum == kvp.Key.CellNum);
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
    }
}
