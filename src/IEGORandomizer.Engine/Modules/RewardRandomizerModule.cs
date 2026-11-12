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
using Lynx.InazumaEleven.Logic;

namespace IEGORandomizer.Engine.Modules
{
    /// <summary>
    /// Subsystem D: Randomizes Overworld Treasure Chests (<c>TBoxConfig</c> Light &amp; Shadow items),
    /// Competition Route Chests (<c>scr_br*.cfg.bin</c>, including <c>scr_br_0005_s.cfg.bin</c>),
    /// Match Drops, Pachanga Drops, and optional Shop Inventories (<c>shop_*.cfg.bin</c>) while strictly
    /// protecting all Key Items (<c>ITEM_IMPORTANT</c>), Versus Tickets, <c>FUNCPOINT</c>
    /// <c>TBoxBitCheck</c>-gated chests, and script-checked items.
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
                    foreach (string fileName in soccerDir.Files.Keys.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!fileName.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(fileName, "soccer_config.cfg.bin", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!RouteDataService.TryReadRouteFile(soccerDir, fileName, out RouteFileData routeData))
                            continue;

                        var cellUpdates = new Dictionary<int, int>();
                        foreach (var cell in routeData.Cells)
                        {
                            // RouteCellTypes.json: 3 = Chest, 4 = Locked Chest
                            if (cell.CellType != 3 && cell.CellType != 4)
                                continue;

                            if (cell.ContentID == 0 || protection.IsProtectedItem(cell.ContentID))
                            {
                                continue;
                            }

                            var pool = cell.CellType == 4 ? premiumPool : allSafeRewardPool;
                            int nextContentId = PickDifferentIfPossible(pool, cell.ContentID, routeRng);
                            cell.ContentID = nextContentId;
                            cellUpdates[cell.NodeIndex] = nextContentId;
                            report.RandomizedRouteChestsCount++;
                        }

                        if (cellUpdates.Count > 0)
                        {
                            RouteDataService.UpdateRouteContentIdsInPlace(soccerDir, fileName, cellUpdates);
                        }
                    }
                }
            }

            // 3. Match & Pachanga Team Drops (DropID1..DropID6 on TeamParamInfo)
            if (options.RandomizeMatchDrops || options.RandomizePachangaDrops)
            {
                DeterministicRng dropRng = seeds.GetSubsystem("rewards.team_drops");
                var storyTeamIds = new HashSet<int>(
                    game.GetStoryTeamsOnly().Select(s => s.TeamConfigID).Where(id => id != 0));
                var encountTeamIds = new HashSet<int>(
                    game.GetEncounterTeamsOnly().Select(e => e.TeamConfigID).Where(id => id != 0));
                var soccers = game.GetSoccers() ?? Array.Empty<ISoccerInfo>();
                var miniBattleParamIds = new HashSet<int>(
                    soccers.Where(s => s != null && s.SoccerMode == 1 && s.TeamParamID != 0).Select(s => s.TeamParamID));
                var fullMatchParamIds = new HashSet<int>(
                    soccers.Where(s => s != null && s.SoccerMode == 2 && s.TeamParamID != 0).Select(s => s.TeamParamID));
                var teamParams = game.GetTeamParams();
                bool modifiedDrops = false;

                foreach (var tp in teamParams)
                {
                    if (tp.TeamParamID == 0 || protection.ScriptedTeamParamHashes.Contains(tp.TeamParamID))
                        continue;

                    bool isPachanga = TeamRandomizerModule.IsMiniBattleTeamParam(
                        tp,
                        storyTeamIds,
                        encountTeamIds,
                        miniBattleParamIds,
                        fullMatchParamIds);
                    if (isPachanga && !options.RandomizePachangaDrops)
                        continue;
                    if (!isPachanga && !options.RandomizeMatchDrops)
                        continue;

                    for (int slot = 1; slot <= 6; slot++)
                    {
                        int currentDrop = TeamSlotAccessor.GetTeamDrop(tp, slot);
                        if (currentDrop == 0)
                            continue;

                        if (protection.IsProtectedItem(currentDrop))
                            continue;

                        int nextDrop = PickDifferentIfPossible(allSafeRewardPool, currentDrop, dropRng);
                        TeamSlotAccessor.SetTeamDrop(tp, slot, nextDrop);
                        modifiedDrops = true;
                        report.RandomizedMatchDropsCount++;
                    }
                }

                if (modifiedDrops)
                {
                    game.SaveTeamParams(teamParams);
                }
            }

            // 4. Optional Shop Inventories (/data/res/shop/shop_*.cfg.bin)
            if (options.RandomizeShopItems)
            {
                DeterministicRng shopRng = seeds.GetSubsystem("rewards.shops");
                var shopDir = game.GetDirectory("shop");
                if (shopDir != null)
                {
                    foreach (string fileName in ShopDataService.ListShopFiles(shopDir))
                    {
                        if (!ShopDataService.TryReadShopFile(shopDir, fileName, out List<ShopSlotRecord> shopSlots))
                            continue;

                        var updates = new Dictionary<int, int>();
                        foreach (var slot in shopSlots)
                        {
                            if (slot.ItemID == 0 || protection.IsProtectedItem(slot.ItemID))
                                continue;

                            int nextItemId = PickDifferentIfPossible(allSafeRewardPool, slot.ItemID, shopRng);
                            slot.ItemID = nextItemId;
                            updates[slot.NodeIndex] = nextItemId;
                            report.RandomizedShopEntriesCount++;
                        }

                        if (updates.Count > 0)
                        {
                            ShopDataService.UpdateShopItemsInPlace(shopDir, fileName, updates);
                        }
                    }
                }
            }
        }

        private static int PickDifferentIfPossible(IReadOnlyList<int> pool, int currentId, DeterministicRng rng)
        {
            if (pool.Count == 1)
                return pool[0];

            for (int attempt = 0; attempt < 6; attempt++)
            {
                int candidate = rng.PickOne(pool);
                if (candidate != currentId)
                    return candidate;
            }

            return rng.PickOne(pool);
        }
    }
}
