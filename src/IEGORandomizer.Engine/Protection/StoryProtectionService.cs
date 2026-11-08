using System;
using System.Collections.Generic;
using System.Linq;
using IEGORandomizer.Engine.Configuration;
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

        /// <summary>Story <c>TeamConfigID</c> (<c>tsXXXX</c>) and <c>TeamParamID</c> (<c>para_tsXXXX</c>) referenced by scripted matches.</summary>
        public HashSet<int> ScriptedTeamConfigHashes { get; } = new();
        public HashSet<int> ScriptedTeamParamHashes { get; } = new();

        /// <summary>All Key Items (<c>ITEM_IMPORTANT</c>), route lock tickets, script-checked items, and funcpt items that MUST NEVER be overwritten or lost.</summary>
        public HashSet<int> ProtectedItemHashes { get; } = new();

        /// <summary>Map <c>TBoxFlag</c> IDs gated by <c>FUNCPOINT</c> <c>TBoxBitCheck</c> triggers.</summary>
        public HashSet<int> ProtectedTBoxFlags { get; } = new();

        public NutbAuditReport NutbAudit { get; set; } = new();
        public int CuratedProtectedPlayersCount { get; set; }

        public bool IsProtectedItem(int itemId) => itemId != 0 && ProtectedItemHashes.Contains(itemId);
    }

    /// <summary>
    /// Builds and allows extension of <see cref="StoryProtectionContext"/> for Inazuma Eleven GO: Luz y Sombra.
    /// Combines curated IEGO1 story knowledge (verified against <c>PlayerHeads.cs</c>, <c>PlayablePhase.cs</c>,
    /// and <c>projectz</c>), dynamic <c>ITEM_IMPORTANT</c> &amp; route-ticket inspection, map <c>FUNCPOINT</c>
    /// <c>TBoxBitCheck</c> scanning, and Squirrel 2.2.3 (<c>.nutb</c>) bytecode literal auditing.
    /// </summary>
    public sealed class StoryProtectionService
    {
        /// <summary>
        /// Core Raimon story roster &amp; temporary chapter members in IEGO1 (verified in PlayerHeads.cs):
        /// cp0001 Samguk, cp0002 Subaru, cp0003 Gabriel, cp0004 Wanli, cp0005 JP, cp0006 Adé,
        /// cp0007 Eugene, cp0008 Arion, cp0009 Riccardo, cp0010 Victor, cp0011 Michael,
        /// cp0012 Lars (leaves Ch.2), cp0013 Doug (leaves early), cp0014 Sven (leaves early),
        /// cp0015 Aitor, cp0016 Ryoma, cp0017 Lucian, cp0026 Shunsuke, cp0029 Hugues,
        /// cp0290 Sol Daystar, cp0350 Bailong (Light), cp0370 Tezcat (Shadow).
        /// </summary>
        private static readonly int[] CuratedCoreStoryModelNumbers =
        {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 26, 29, 290, 350, 370
        };

        /// <summary>
        /// Story protagonists whose Fighting Spirits are awakened/commanded in scripted story matches:
        /// JP (cp0005), Arion (cp0008), Riccardo (cp0009), Victor (cp0010), Sol (cp0290), Bailong (cp0350), Tezcat (cp0370).
        /// </summary>
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

            // 1. Always protect Key Items (ITEM_IMPORTANT + Category 14 + ItemRef 80 + Route Locks)
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

                // Protect route-unlocking tickets referenced by MatchRestriction in any competition route
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
                            foreach (var cell in cells)
                            {
                                if (cell.MatchRestriction != 0)
                                {
                                    ctx.ProtectedItemHashes.Add(cell.MatchRestriction);
                                }
                            }
                        }
                        catch
                        {
                            // Non-route soccer cfg.bin; skip safely
                        }
                    }
                }

                // Scan map FUNCPOINT TBoxBitCheck & ItemID dependencies
                var mapDeps = MapDataService.ScanMapProgressionDependencies(game.IE_A.Directory);
                foreach (int flag in mapDeps.ProtectedTBoxFlags)
                    ctx.ProtectedTBoxFlags.Add(flag);
                foreach (int itemId in mapDeps.ReferencedFuncptItemIds)
                    ctx.ProtectedItemHashes.Add(itemId);
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
                    // Edition-specific postgame/story rivals
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
                    if (clean.StartsWith("cp") && int.TryParse(clean.Substring(2), out int num))
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

                foreach (string charCode in nutb.ReferencedCharacterIds)
                {
                    if (charCode.StartsWith("para_cp"))
                    {
                        ctx.ProtectedStoryPlayerParamHashes.Add(Level5IdHelper.ComputeCrc32Utf8(charCode));
                        string cp = charCode.Substring("para_".Length);
                        ctx.ProtectedStoryPlayerBaseHashes.Add(Level5IdHelper.ComputeCrc32Utf8(cp));
                    }
                    else if (charCode.StartsWith("cp"))
                    {
                        ctx.ProtectedStoryPlayerBaseHashes.Add(Level5IdHelper.ComputeCrc32Utf8(charCode));
                        ctx.ProtectedStoryPlayerParamHashes.Add(Level5IdHelper.ComputeCrc32Utf8("para_" + charCode));
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
                    if (teamCode.StartsWith("para_"))
                        ctx.ScriptedTeamParamHashes.Add(Level5IdHelper.ComputeCrc32Utf8(teamCode));
                    else
                        ctx.ScriptedTeamConfigHashes.Add(Level5IdHelper.ComputeCrc32Utf8(teamCode));
                }
            }

            // Resolve SoccerInfo -> TeamParamID -> TeamConfigID links for scripted matches
            if (options.EnableStorySoftlockProtection)
            {
                var soccers = game.GetSoccers() ?? Array.Empty<ISoccerInfo>();
                var teamParams = game.GetTeamParams() ?? Array.Empty<ITeamParamInfo>();
                var paramById = teamParams
                    .Where(tp => tp.TeamParamID != 0)
                    .GroupBy(tp => tp.TeamParamID)
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (var soccer in soccers)
                {
                    bool isScriptedSoccer = ctx.ScriptedMatchHashes.Contains(soccer.SoccerID) ||
                                            soccer.Script != 0 ||
                                            soccer.NextScript != 0 ||
                                            soccer.VictoryCondition != 0;

                    if (isScriptedSoccer)
                    {
                        ctx.ScriptedMatchHashes.Add(soccer.SoccerID);
                        if (soccer.TeamParamID != 0)
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
