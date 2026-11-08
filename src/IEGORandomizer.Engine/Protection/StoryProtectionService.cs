using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using IEGORandomizer.Engine.Configuration;
using Lynx.Core.Binary;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.Core.RomFs;
using Lynx.Core.Scripts;
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Engine.Protection
{
    /// <summary>
    /// Immutable protection profile built prior to randomization to prevent softlocks across
    /// story events, scripted soccer battles, forced skills/avatars, temporary party joins/departures,
    /// chapter gates, and Key Items.
    /// </summary>
    public sealed class StoryProtectionContext
    {
        /// <summary>Player <c>ParamHash</c> values (<c>para_cpXXXX</c>) whose story slot identity or mandatory script hooks are protected.</summary>
        public HashSet<int> ProtectedStoryPlayerParamHashes { get; } = new();

        /// <summary>Player <c>BaseHash</c> values (<c>cpXXXX</c>) referenced by story scripts or Raimon chapter progression.</summary>
        public HashSet<int> ProtectedStoryPlayerBaseHashes { get; } = new();

        /// <summary>Player <c>ParamHash</c> values whose Fighting Spirit must not be stripped because story matches awaken/invoke it.</summary>
        public HashSet<int> ProtectedAvatarBearerParamHashes { get; } = new();

        /// <summary>Skill hashes (<c>wh*XXXX</c> / <c>wk*XXXX</c>) explicitly invoked by story event scripts.</summary>
        public HashSet<int> ScriptReferencedSkillHashes { get; } = new();

        /// <summary>Avatar hashes (<c>ckXXXX</c>) explicitly invoked by story event scripts.</summary>
        public HashSet<int> ScriptReferencedAvatarHashes { get; } = new();

        /// <summary>Story match hashes (<c>btlXXXX</c>) triggered by Squirrel scripts or main story progression.</summary>
        public HashSet<int> ScriptedMatchHashes { get; } = new();

        /// <summary>Story <c>TeamConfigID</c> (<c>tsXXXX</c>) and <c>TeamParamID</c> (<c>para_tsXXXX</c>) explicitly pinned by team scripts or manual overrides.</summary>
        public HashSet<int> ScriptedTeamConfigHashes { get; } = new();
        public HashSet<int> ScriptedTeamParamHashes { get; } = new();

        /// <summary>Preset Raimon story <c>TeamConfigID</c> entries (composed predominantly of Raimon core members) whose kit/emblem are preserved in story cutscenes.</summary>
        public HashSet<int> RaimonCoreTeamConfigHashes { get; } = new();

        /// <summary>All Key Items (<c>ITEM_IMPORTANT</c>), route lock tickets, script-checked items, and funcpt items that MUST NEVER be overwritten or lost.</summary>
        public HashSet<int> ProtectedItemHashes { get; } = new();

        /// <summary>Map <c>TBoxFlag</c> IDs gated by <c>FUNCPOINT</c> <c>TBoxBitCheck</c> triggers.</summary>
        public HashSet<int> ProtectedTBoxFlags { get; } = new();

        /// <summary>Map progression &amp; <c>TALK_CONFIG</c> dependencies (including textual route IDs such as <c>"scr_br_0005_s"</c>).</summary>
        public MapProgressionDependencies MapDependencies { get; set; } = new();

        public NutbAuditReport NutbAudit { get; set; } = new();
        public int CuratedProtectedPlayersCount { get; set; }

        public bool IsProtectedItem(int itemId) => itemId != 0 && ProtectedItemHashes.Contains(itemId);
    }

    /// <summary>
    /// Builds and allows extension of <see cref="StoryProtectionContext"/> for Inazuma Eleven GO: Luz y Sombra.
    /// Combines curated IEGO1 story knowledge (verified against <c>PlayerHeads.cs</c>, <c>PlayablePhase.cs</c>,
    /// and <c>projectz</c>), dynamic <c>ITEM_IMPORTANT</c> &amp; route-ticket inspection, map <c>FUNCPOINT</c>
    /// <c>TBoxBitCheck</c> &amp; <c>TALK_CONFIG</c> scanning, and Squirrel 2.2.3 (<c>.nutb</c>) bytecode literal auditing.
    /// </summary>
    public sealed class StoryProtectionService
    {
        private static readonly int[] CuratedCoreStoryModelNumbers =
        {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 26, 29, 290, 350, 370
        };

        private static readonly int[] StoryAvatarBearers =
        {
            5, 8, 9, 10, 290, 350, 370
        };

        private readonly HashSet<string> _extraProtectedPlayerCodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _extraProtectedItemCodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _extraProtectedMatchCodes = new(StringComparer.OrdinalIgnoreCase);

        public void AddProtectedCharacter(string cpCode) => _extraProtectedPlayerCodes.Add(cpCode);
        public void AddProtectedItem(string itemCode) => _extraProtectedItemCodes.Add(itemCode);
        public void AddProtectedMatch(string btlCode) => _extraProtectedMatchCodes.Add(btlCode);

        public StoryProtectionContext BuildContext(
            SafeGOGame game,
            GameEdition edition,
            ProtectionRandomizerOptions options)
        {
            if (game == null)
                throw new ArgumentNullException(nameof(game));
            options ??= new ProtectionRandomizerOptions();

            var ctx = new StoryProtectionContext();

            // 1. Always protect Key Items (ITEM_IMPORTANT + Category 14 + Category 15 + ItemRef 80 + Route Locks)
            if (options.ProtectKeyItemsAndFuncpointChests)
            {
                var importantItems = game.GetItems("important") ?? Array.Empty<IItemConfig>();
                foreach (var item in importantItems)
                {
                    if (item.ItemID != 0)
                        ctx.ProtectedItemHashes.Add(item.ItemID);
                }

                var allItems = game.GetItems("all") ?? Array.Empty<IItemConfig>();
                foreach (var item in allItems)
                {
                    // ItemCategory 14 = Key Item, 15 = Versus Ticket (Competition Route unlocker), ItemRef 80 = ITEM_IMPORTANT
                    if (item.ItemID != 0 && (item.ItemCategory == 14 || item.ItemCategory == 15 || item.ItemRef == 80))
                    {
                        ctx.ProtectedItemHashes.Add(item.ItemID);
                    }
                }

                // Scan map FUNCPOINT TBoxBitCheck, ItemID, and TALK_CONFIG dependencies (e.g. "scr_br_0005_s")
                var mapDeps = MapDataService.ScanMapProgressionDependencies(game.IE_A.Directory);
                ctx.MapDependencies = mapDeps;

                foreach (int flag in mapDeps.ProtectedTBoxFlags)
                    ctx.ProtectedTBoxFlags.Add(flag);
                foreach (int itemId in mapDeps.ReferencedFuncptItemIds)
                    ctx.ProtectedItemHashes.Add(itemId);

                // Protect route-unlocking tickets referenced by MatchRestriction in any competition route
                var soccerDir = game.GetDirectory("soccer");
                if (soccerDir != null)
                {
                    var candidateRoutes = new HashSet<string>(
                        soccerDir.Files.Keys.Where(f =>
                            f.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(f, "soccer_config.cfg.bin", StringComparison.OrdinalIgnoreCase)),
                        StringComparer.OrdinalIgnoreCase);

                    // Also ensure every route explicitly referenced by map TALK_CONFIG (e.g. "scr_br_0005_s") is checked
                    foreach (string routeId in mapDeps.ReferencedRouteIds)
                    {
                        string routeFile = routeId.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase)
                            ? routeId
                            : routeId + ".cfg.bin";
                        if (soccerDir.Files.ContainsKey(routeFile))
                            candidateRoutes.Add(routeFile);
                    }

                    foreach (string fileName in candidateRoutes.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        if (RouteDataService.TryReadRouteFile(soccerDir, fileName, out RouteFileData routeData))
                        {
                            foreach (var cell in routeData.Cells)
                            {
                                if (cell.MatchRestriction != 0)
                                {
                                    ctx.ProtectedItemHashes.Add(cell.MatchRestriction);
                                }
                            }
                        }
                    }
                }
            }

            foreach (string extraItem in _extraProtectedItemCodes)
            {
                ctx.ProtectedItemHashes.Add(Level5IdHelper.ComputeCrc32Utf8(extraItem.ToLowerInvariant()));
            }

            // 2. Curated IEGO1 Story Softlock Protection
            if (options.EnableStorySoftlockProtection)
            {
                foreach (int modelNum in CuratedCoreStoryModelNumbers)
                {
                    if (modelNum == 350 && edition == GameEdition.Shadow)
                        continue;
                    if (modelNum == 370 && edition == GameEdition.Light)
                        continue;

                    RegisterProtectedPlayerModel(ctx, modelNum);
                    ctx.CuratedProtectedPlayersCount++;
                }

                foreach (int modelNum in StoryAvatarBearers)
                {
                    ctx.ProtectedAvatarBearerParamHashes.Add(
                        Level5IdHelper.ComputeCrc32Utf8($"para_cp{modelNum:D4}"));
                }

                foreach (string extraCp in _extraProtectedPlayerCodes)
                {
                    string clean = extraCp.Trim().ToLowerInvariant();
                    if (clean.StartsWith("cp", StringComparison.Ordinal) &&
                        int.TryParse(clean.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int num))
                    {
                        RegisterProtectedPlayerModel(ctx, num);
                    }
                }
            }

            // 3. Dynamic Squirrel 2.2.3 (.nutb) Script Bytecode Audit
            if (options.AuditNutbEventScripts)
            {
                var nutb = NutbScriptAuditor.AuditArchive(game.IE_A.Directory);
                ctx.NutbAudit = nutb;

                // Only party-recruitment ("para_cpXXXX") and in-match battle script ("CMND_BTL_*") character IDs
                // lock a character's CharaParam slot; general map cutscene "cpXXXX" model literals only register BaseHash.
                foreach (string partyCode in nutb.ReferencedPartyParamIds)
                {
                    if (partyCode.StartsWith("para_cp", StringComparison.Ordinal))
                    {
                        ctx.ProtectedStoryPlayerParamHashes.Add(Level5IdHelper.ComputeCrc32Utf8(partyCode));
                        string cp = partyCode.Substring("para_".Length);
                        ctx.ProtectedStoryPlayerBaseHashes.Add(Level5IdHelper.ComputeCrc32Utf8(cp));
                    }
                }

                foreach (string btlCharCode in nutb.ReferencedBattleCharacterIds)
                {
                    if (btlCharCode.StartsWith("para_cp", StringComparison.Ordinal))
                    {
                        ctx.ProtectedStoryPlayerParamHashes.Add(Level5IdHelper.ComputeCrc32Utf8(btlCharCode));
                        string cp = btlCharCode.Substring("para_".Length);
                        ctx.ProtectedStoryPlayerBaseHashes.Add(Level5IdHelper.ComputeCrc32Utf8(cp));
                    }
                    else if (btlCharCode.StartsWith("cp", StringComparison.Ordinal))
                    {
                        ctx.ProtectedStoryPlayerBaseHashes.Add(Level5IdHelper.ComputeCrc32Utf8(btlCharCode));
                        ctx.ProtectedStoryPlayerParamHashes.Add(Level5IdHelper.ComputeCrc32Utf8("para_" + btlCharCode));
                    }
                }

                foreach (string cutsceneCode in nutb.ReferencedCutsceneModelIds)
                {
                    if (cutsceneCode.StartsWith("cp", StringComparison.Ordinal))
                    {
                        ctx.ProtectedStoryPlayerBaseHashes.Add(Level5IdHelper.ComputeCrc32Utf8(cutsceneCode));
                    }
                }

                foreach (string skillCode in nutb.ReferencedSkillIds)
                {
                    ctx.ScriptReferencedSkillHashes.Add(Level5IdHelper.ComputeCrc32Utf8(skillCode));
                }

                foreach (string avatarCode in nutb.ReferencedAvatarIds)
                {
                    ctx.ScriptReferencedAvatarHashes.Add(Level5IdHelper.ComputeCrc32Utf8(avatarCode));
                }

                foreach (string itemCode in nutb.ReferencedItemIds)
                {
                    ctx.ProtectedItemHashes.Add(Level5IdHelper.ComputeCrc32Utf8(itemCode));
                }

                foreach (string matchCode in nutb.ReferencedMatchIds.Concat(_extraProtectedMatchCodes))
                {
                    ctx.ScriptedMatchHashes.Add(Level5IdHelper.ComputeCrc32Utf8(matchCode.ToLowerInvariant()));
                }

                foreach (string teamCode in nutb.ReferencedTeamIds)
                {
                    if (teamCode.StartsWith("para_", StringComparison.Ordinal))
                        ctx.ScriptedTeamParamHashes.Add(Level5IdHelper.ComputeCrc32Utf8(teamCode));
                    else
                        ctx.ScriptedTeamConfigHashes.Add(Level5IdHelper.ComputeCrc32Utf8(teamCode));
                }
            }

            // Resolve explicit manual match overrides (_extraProtectedMatchCodes) and identify Raimon's own preset story rosters.
            // IMPORTANT: Do NOT mark rival story matches (Script/NextScript/btlXXXX) or 5v5 pachangas (VictoryCondition 1..4)
            // as ScriptedTeamConfigHashes/ScriptedTeamParamHashes — individual scripted player slots on rival teams are already
            // protected via ProtectedStoryPlayerParamHashes, allowing all rival rosters, formations, emblems, and kits to randomize!
            if (options.EnableStorySoftlockProtection)
            {
                var raimonCoreParams = new HashSet<int>();
                for (int modelNum = 1; modelNum <= 17; modelNum++)
                {
                    raimonCoreParams.Add(Level5IdHelper.ComputeCrc32Utf8($"para_cp{modelNum:D4}"));
                }

                var storyTeams = game.GetStoryTeamsOnly() ?? Array.Empty<IStoryTeamInfo>();
                foreach (var st in storyTeams)
                {
                    if (st.TeamConfigID == 0)
                        continue;

                    int raimonMembers = 0;
                    for (int slot = 1; slot <= 16; slot++)
                    {
                        int pHash = TeamSlotAccessor.GetStoryPlayer(st, slot);
                        if (pHash != 0 && raimonCoreParams.Contains(pHash))
                        {
                            raimonMembers++;
                        }
                    }

                    if (raimonMembers >= 3)
                    {
                        ctx.RaimonCoreTeamConfigHashes.Add(st.TeamConfigID);
                    }
                }

                if (_extraProtectedMatchCodes.Count > 0)
                {
                    var manualMatchHashes = new HashSet<int>(
                        _extraProtectedMatchCodes.Select(m => Level5IdHelper.ComputeCrc32Utf8(m.ToLowerInvariant())));
                    var soccers = game.GetSoccers() ?? Array.Empty<ISoccerInfo>();
                    var teamParams = game.GetTeamParams() ?? Array.Empty<ITeamParamInfo>();
                    var paramById = teamParams
                        .Where(tp => tp.TeamParamID != 0)
                        .GroupBy(tp => tp.TeamParamID)
                        .ToDictionary(g => g.Key, g => g.First());

                    foreach (var soccer in soccers)
                    {
                        if (manualMatchHashes.Contains(soccer.SoccerID) && soccer.TeamParamID != 0)
                        {
                            ctx.ScriptedTeamParamHashes.Add(soccer.TeamParamID);
                            if (paramById.TryGetValue(soccer.TeamParamID, out var tp) && tp.TeamConfigID != 0)
                            {
                                ctx.ScriptedTeamConfigHashes.Add(tp.TeamConfigID);
                            }
                        }
                    }
                }
            }

            return ctx;
        }

        private static void RegisterProtectedPlayerModel(StoryProtectionContext ctx, int modelNum)
        {
            string cp = $"cp{modelNum:D4}";
            ctx.ProtectedStoryPlayerBaseHashes.Add(Level5IdHelper.ComputeCrc32Utf8(cp));
            ctx.ProtectedStoryPlayerParamHashes.Add(Level5IdHelper.ComputeCrc32Utf8("para_" + cp));
        }
    }
}
