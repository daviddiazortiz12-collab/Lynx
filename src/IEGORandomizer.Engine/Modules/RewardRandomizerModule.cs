using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IEGORandomizer.Engine.Configuration;
using IEGORandomizer.Engine.Protection;
using IEGORandomizer.Engine.Seeding;
using IEGORandomizer.Engine.Validation;
using Lynx.Core.Games;
using Lynx.Core.Maps;
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Engine.Modules
{
    /// <summary>
    /// Subsystem D: Randomizes Overworld Treasure Chests (<c>TBoxConfig</c> Light &amp; Shadow items),
    /// Competition Route Chests (<c>scr_br*.cfg.bin</c>), Match Drops, Pachanga Drops, and optional
    /// Shop Inventories while strictly protecting all Key Items (<c>ITEM_IMPORTANT</c>), Versus Tickets,
    /// <c>FUNCPOINT</c> <c>TBoxBitCheck</c>-gated chests, and script-checked items.
    /// </summary>
    public static class RewardRandomizerModule
    {
        public static void Execute(
            SafeGOGame game,
            SeedEngine seeds,
            RewardsRandomizerOptions options,
            UniformsRandomizerOptions uniformOptions,
            StoryProtectionContext protection,
            VisualCoherenceProfile visualProfile,
            RandomizationExecutionReport report)
        {
            if (options == null || !options.Enabled)
                return;

            var equipment = (game.GetItems("equipment") ?? Array.Empty<IItemConfig>())
                .Where(i => i.ItemID != 0 && !protection.IsProtectedItem(i.ItemID))
                .Select(i => i.ItemID)
                .Distinct()
                .ToList();

            var consumables = (game.GetItems("consumable") ?? Array.Empty<IItemConfig>())
                .Where(i => i.ItemID != 0 && !protection.IsProtectedItem(i.ItemID))
                // Exclude Key Items (14) & Versus Tickets (15)
                .Where(i => i.ItemCategory != 14 && i.ItemCategory != 15)
                .Select(i => i.ItemID)
                .Distinct()
                .ToList();

            bool includeUniformRewards = uniformOptions == null ||
                                         !uniformOptions.Enabled ||
                                         uniformOptions.RandomizeObtainableUniformRewards;

            var validUniformItems = (game.GetItems("uniform") ?? Array.Empty<IItemConfig>())
                .Where(u => u.ItemID != 0 &&
                            !protection.IsProtectedItem(u.ItemID) &&
                            visualProfile.ValidKitNumbers.Contains(VisualCoherenceValidator.ExtractUniformNumber(u)))
                .Select(u => u.ItemID)
                .Distinct()
                .ToList();

            var standardPool = consumables.Concat(equipment).Distinct().ToList();
            var premiumPool = equipment
                .Concat(includeUniformRewards ? validUniformItems : Enumerable.Empty<int>())
                .Distinct()
                .ToList();

            if (premiumPool.Count == 0)
                premiumPool = standardPool;
            if (standardPool.Count == 0)
                standardPool = premiumPool;
            if (standardPool.Count == 0)
                return;

            var allSafeRewardPool = standardPool.Concat(premiumPool).Distinct().ToList();

            // 1. Overworld Map Treasure Chests (TBoxConfigRecord: ItemIDLight & ItemIDShadow)
            if (options.RandomizeOverworldChests)
            {
                DeterministicRng chestRng = seeds.GetSubsystem("rewards.map_chests");
                var mapChests = MapDataService.LoadAllTreasureChests(game.IE_A.Directory);
                var modifiedChests = new List<MapTreasureChestEntry>();

                foreach (var chest in mapChests)
                {
                    var rec = chest.Record;
                    bool isGatedByFuncpoint = protection.ProtectedTBoxFlags.Contains(rec.TBoxFlag);
                    bool holdsProtectedLight = protection.IsProtectedItem(rec.ItemIDLight);
                    bool holdsProtectedShadow = protection.IsProtectedItem(rec.ItemIDShadow);

                    if (isGatedByFuncpoint || holdsProtectedLight || holdsProtectedShadow)
                    {
                        report.ProtectedOverworldChestsSkipped++;
                        continue;
                    }

                    // TboxType: 0 = Normal, 1 = Gold, 2 = Silver
                    var targetPool = (rec.TboxType == 1 || rec.TboxType == 2) ? premiumPool : allSafeRewardPool;
                    bool changed = false;

                    if (rec.ItemIDLight != 0)
                    {
                        rec.ItemIDLight = PickDifferentIfPossible(targetPool, rec.ItemIDLight, chestRng);
                        changed = true;
                    }

                    if (rec.ItemIDShadow != 0)
                    {
                        rec.ItemIDShadow = PickDifferentIfPossible(targetPool, rec.ItemIDShadow, chestRng);
                        changed = true;
                    }

                    if (changed)
                    {
                        modifiedChests.Add(chest);
                        report.RandomizedOverworldChestsCount++;
                    }
                }

                if (modifiedChests.Count > 0)
                {
                    MapDataService.SaveTreasureChests(game.IE_A.Directory, modifiedChests);
                }
            }

            // 2. Competition Route Chests (/data/res/soccer/scr_br*.cfg.bin & any valid route file)
            if (options.RandomizeCompetitionRouteChests)
            {
                DeterministicRng routeRng = seeds.GetSubsystem("rewards.route_chests");
                var soccerDir = game.GetDirectory("soccer");
                if (soccerDir != null)
                {
                    foreach (string fileName in soccerDir.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
                    {
                        if (!fileName.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(fileName, "soccer_config.cfg.bin", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        IRouteConfig[] cells;
                        int routeNameId;
                        try
                        {
                            (cells, routeNameId) = game.GetRoutes("/" + fileName.TrimStart('/'));
                        }
                        catch
                        {
                            continue;
                        }

                        bool routeModified = false;
                        foreach (var cell in cells)
                        {
                            // CellType 3 = Normal Route Chest, CellType 4 = Special Locked Route Chest
                            if (cell.CellType != 3 && cell.CellType != 4)
                                continue;
                            if (cell.ContentID == 0 || protection.IsProtectedItem(cell.ContentID))
                                continue;

                            var pool = cell.CellType == 4 ? premiumPool : allSafeRewardPool;
                            cell.ContentID = PickDifferentIfPossible(pool, cell.ContentID, routeRng);
                            routeModified = true;
                            report.RandomizedRouteChestsCount++;
                        }

                        if (routeModified)
                        {
                            SaveRouteSafe(game, fileName.TrimStart('/'), routeNameId, cells);
                        }
                    }
                }
            }

            // 3. Match Drops & Pachanga Drops (TeamParamInfo.DropID1..6)
            if (options.RandomizeMatchDrops || options.RandomizePachangaDrops)
            {
                DeterministicRng dropRng = seeds.GetSubsystem("rewards.drops");
                var teamParams = game.GetTeamParams();
                var encountTeamIds = new HashSet<int>(game.GetEncounterTeamsOnly().Select(e => e.TeamConfigID));
                bool anyDropChanged = false;

                foreach (var tp in teamParams)
                {
                    if (tp.TeamParamID == 0)
                        continue;

                    bool isPachanga = encountTeamIds.Contains(tp.TeamConfigID);
                    if (isPachanga && !options.RandomizePachangaDrops)
                        continue;
                    if (!isPachanga && !options.RandomizeMatchDrops)
                        continue;

                    for (int slot = 1; slot <= 6; slot++)
                    {
                        int currentDrop = TeamSlotAccessor.GetTeamDrop(tp, slot);
                        if (currentDrop == 0 || protection.IsProtectedItem(currentDrop))
                            continue;

                        int newDrop = PickDifferentIfPossible(allSafeRewardPool, currentDrop, dropRng);
                        TeamSlotAccessor.SetTeamDrop(tp, slot, newDrop);
                        anyDropChanged = true;
                        report.RandomizedMatchDropsCount++;
                    }
                }

                if (anyDropChanged)
                {
                    game.SaveTeamParams(teamParams);
                }
            }

            // 4. Optional Shop Inventory Randomization (excluding Key Items and PalPack dealers)
            if (options.RandomizeShopItems)
            {
                DeterministicRng shopRng = seeds.GetSubsystem("rewards.shops");
                var shopDir = game.GetDirectory("shop");
                if (shopDir != null)
                {
                    var palpackIds = new HashSet<int>((game.GetItems("kizunax") ?? Array.Empty<IItemConfig>()).Select(k => k.ItemID));
                    foreach (string fileName in shopDir.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
                    {
                        if (!fileName.StartsWith("shop", StringComparison.OrdinalIgnoreCase) ||
                            !fileName.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string shopKey = fileName.Substring(0, fileName.Length - ".cfg.bin".Length);
                        IShopConfig[] entries;
                        try
                        {
                            entries = game.GetShop(shopKey);
                        }
                        catch
                        {
                            continue;
                        }

                        bool changed = false;
                        foreach (var entry in entries)
                        {
                            if (entry.ItemID == 0 ||
                                protection.IsProtectedItem(entry.ItemID) ||
                                palpackIds.Contains(entry.ItemID))
                            {
                                continue;
                            }

                            entry.ItemID = PickDifferentIfPossible(allSafeRewardPool, entry.ItemID, shopRng);
                            changed = true;
                            report.RandomizedShopEntriesCount++;
                        }

                        if (changed)
                        {
                            game.SaveShop(shopKey, entries);
                        }
                    }
                }
            }
        }

        private static void SaveRouteSafe(SafeGOGame game, string cleanFileName, int nameCrc32, IRouteConfig[] routes)
        {
            // Game.SaveRoutes calls IsFullPathExists(filename) + Files[filename], so build directly on soccer directory
            var soccerDir = game.GetDirectory("soccer");
            var routeFile = new StudioElevenLib.Level5.Binary.CfgBin<StudioElevenLib.Level5.Binary.Collections.CfgTreeNode>();
            routeFile.Open(soccerDir.GetFileFromFullPath("/" + cleanFileName));

            var baseBegin = routeFile.Entries.FindByName("ROUTE_CONFIG_BEGIN");
            baseBegin.Children.Clear();
            baseBegin.Item.Variables[0].Value = routes.Length;
            baseBegin.Item.Variables[1].Value = nameCrc32;

            foreach (var r in routes)
            {
                var vars = StudioElevenLib.Level5.Binary.Mapper.CfgBinMapper.GetVariablesFromInstance(r);
                baseBegin.AddChild(new StudioElevenLib.Level5.Binary.Collections.CfgTreeNode(
                    new StudioElevenLib.Level5.Binary.Logic.Entry("ROUTE_CONFIG", vars)));
            }

            soccerDir.Files[cleanFileName].ByteContent = routeFile.Save();
        }

        private static int PickDifferentIfPossible(IReadOnlyList<int> pool, int currentValue, DeterministicRng rng)
        {
            if (pool.Count == 1)
                return pool[0];

            for (int attempt = 0; attempt < 6; attempt++)
            {
                int candidate = rng.PickOne(pool);
                if (candidate != currentValue)
                    return candidate;
            }
            return rng.PickOne(pool);
        }
    }
}
