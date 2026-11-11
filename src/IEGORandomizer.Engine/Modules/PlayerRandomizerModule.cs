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
using Lynx.Core.RomFs;
using Lynx.InazumaEleven.Games.GO;
using Lynx.InazumaEleven.Logic;
using static Lynx.InazumaEleven.Games.GO.GOSupport;

namespace IEGORandomizer.Engine.Modules
{
    /// <summary>
    /// Subsystem A: Randomizes playable characters across Story/Route Teams, Pachangas,
    /// Overworld Encounter Spawns, and PalPack Recruitment Cards (using strict derangements so
    /// no included character stays in place), plus Positions, Elements, Base Stats, and Fighting Spirits.
    /// Strictly enforces Visual Coherence and Story Softlock Protection.
    /// </summary>
    public static class PlayerRandomizerModule
    {
        public static void Execute(
            SafeGOGame game,
            SeedEngine seeds,
            PlayersRandomizerOptions options,
            StoryProtectionContext protection,
            VisualCoherenceProfile visualProfile,
            RandomizationExecutionReport report)
        {
            if (options == null || !options.Enabled)
                return;

            var charaBases = game.GetCharabase();
            var charaParams = game.GetCharaparams();
            var storyTeams = game.GetStoryTeamsOnly();
            var encountTeams = game.GetEncounterTeamsOnly();
            var mapSpawns = MapDataService.LoadAllEncounterSpawns(game.IE_A.Directory);
            var palpackCards = (game.GetItems("kizunax") ?? Array.Empty<IItemConfig>())
                .OfType<ItemConfigPalpackCard>()
                .ToArray();

            var baseByHash = charaBases
                .Where(b => b.BaseHash != 0)
                .GroupBy(b => b.BaseHash)
                .ToDictionary(g => g.Key, g => g.First());

            var paramByHash = charaParams
                .Where(p => p.ParamHash != 0)
                .GroupBy(p => p.ParamHash)
                .ToDictionary(g => g.Key, g => g.First());

            // Eligible swap pool: visually coherent soccer players excluding protected story IDs
            var swappableParams = charaParams
                .Where(p => visualProfile.CoherentPlayableParamHashes.Contains(p.ParamHash) &&
                            !protection.ProtectedStoryPlayerParamHashes.Contains(p.ParamHash))
                .ToList();

            // 1. Strict Derangement across Team Rosters, Pachangas, and PalPack Cards
            if (swappableParams.Count >= 2 &&
                (options.RandomizeTeamAndRivalSlots || options.RandomizePachangaPlayers || options.RandomizePalpackRecruits))
            {
                DeterministicRng swapRng = seeds.GetSubsystem("players.swap");
                DeterministicRng dedupRng = seeds.GetSubsystem("players.team_dedup");
                Dictionary<int, int> paramDerangement = BuildPlayerDerangementMap(
                    swappableParams,
                    swapRng,
                    options.PreserveGoalkeeperRolesInRosters);

                // Build BaseHash -> BaseHash remap for overworld encounter tables that reference BaseHash (cpXXXX)
                var baseDerangement = new Dictionary<int, int>();
                foreach (var kvp in paramDerangement)
                {
                    if (paramByHash.TryGetValue(kvp.Key, out var srcParam) &&
                        paramByHash.TryGetValue(kvp.Value, out var dstParam) &&
                        srcParam.BaseHash != 0 &&
                        dstParam.BaseHash != 0 &&
                        !baseDerangement.ContainsKey(srcParam.BaseHash))
                    {
                        baseDerangement[srcParam.BaseHash] = dstParam.BaseHash;
                    }
                }

                if (options.RandomizeTeamAndRivalSlots)
                {
                    foreach (var team in storyTeams)
                    {
                        if (protection.ScriptedTeamConfigHashes.Contains(team.TeamConfigID))
                            continue;

                        // Pre-seed BaseHashes of protected / fixed slots so no swapped player duplicates a story teammate's 3D head
                        var usedBasesOnTeam = new HashSet<int>();
                        for (int slot = 1; slot <= 16; slot++)
                        {
                            int h = TeamSlotAccessor.GetStoryPlayer(team, slot);
                            if (h != 0 && !paramDerangement.ContainsKey(h) && paramByHash.TryGetValue(h, out var fixedP) && fixedP.BaseHash != 0)
                            {
                                usedBasesOnTeam.Add(fixedP.BaseHash);
                            }
                        }

                        for (int slot = 1; slot <= 16; slot++)
                        {
                            int oldHash = TeamSlotAccessor.GetStoryPlayer(team, slot);
                            if (oldHash != 0 && paramDerangement.TryGetValue(oldHash, out int newHash))
                            {
                                newHash = EnsureUniqueTeamPlayer(
                                    oldHash,
                                    newHash,
                                    slot == 1 && options.PreserveGoalkeeperRolesInRosters,
                                    usedBasesOnTeam,
                                    paramByHash,
                                    swappableParams,
                                    dedupRng);
                                TeamSlotAccessor.SetStoryPlayer(team, slot, newHash);
                                report.RandomizedTeamPlayerSlotsCount++;
                            }
                        }
                    }
                }

                if (options.RandomizePachangaPlayers)
                {
                    var encountTeamMap = new Dictionary<int, (int[] ParamHashes, int[] BaseHashes)>();
                    foreach (var team in encountTeams)
                    {
                        if (protection.ScriptedTeamConfigHashes.Contains(team.TeamConfigID))
                            continue;

                        var usedBasesOnTeam = new HashSet<int>();
                        for (int i = 0; i < 5; i++)
                        {
                            int h = TeamSlotAccessor.GetEncountPlayer(team, i + 1);
                            if (h != 0 && !paramDerangement.ContainsKey(h) && paramByHash.TryGetValue(h, out var fixedP) && fixedP.BaseHash != 0)
                            {
                                usedBasesOnTeam.Add(fixedP.BaseHash);
                            }
                        }

                        int[] updatedParams = new int[5];
                        int[] updatedBases = new int[5];
                        for (int i = 0; i < 5; i++)
                        {
                            int oldHash = TeamSlotAccessor.GetEncountPlayer(team, i + 1);
                            int resolvedHash = oldHash;
                            if (oldHash != 0 && paramDerangement.TryGetValue(oldHash, out int newHash))
                            {
                                resolvedHash = EnsureUniqueTeamPlayer(
                                    oldHash,
                                    newHash,
                                    requireGk: false,
                                    usedBasesOnTeam,
                                    paramByHash,
                                    swappableParams,
                                    dedupRng);
                                TeamSlotAccessor.SetEncountPlayer(team, i + 1, resolvedHash);
                                report.RandomizedPachangaPlayerSlotsCount++;
                            }
                            updatedParams[i] = resolvedHash;
                            updatedBases[i] = paramByHash.TryGetValue(resolvedHash, out var pObj) ? pObj.BaseHash : 0;
                        }

                        if (team.TeamConfigID != 0)
                            encountTeamMap[team.TeamConfigID] = (updatedParams, updatedBases);
                    }

                    // Also alias TeamParamID and SoccerID -> encountTeamMap so ENCOUNT_INFO.TeamID matches regardless of linkage
                    var teamParamsForSpawns = game.GetTeamParams();
                    var soccersForSpawns = game.GetSoccers() ?? Array.Empty<ISoccerInfo>();
                    foreach (var tp in teamParamsForSpawns)
                    {
                        if (tp.TeamParamID != 0 && tp.TeamConfigID != 0 &&
                            encountTeamMap.TryGetValue(tp.TeamConfigID, out var slots) &&
                            !encountTeamMap.ContainsKey(tp.TeamParamID))
                        {
                            encountTeamMap[tp.TeamParamID] = slots;
                        }
                    }
                    foreach (var sc in soccersForSpawns)
                    {
                        if (sc != null && sc.SoccerID != 0 && sc.TeamParamID != 0 &&
                            encountTeamMap.TryGetValue(sc.TeamParamID, out var slots) &&
                            !encountTeamMap.ContainsKey(sc.SoccerID))
                        {
                            encountTeamMap[sc.SoccerID] = slots;
                        }
                    }

                    // Synchronize overworld 3D pachanga NPCs (*.encount.bin) strictly preserving each slot's hash domain
                    // (ParamHash -> ParamHash, BaseHash -> BaseHash) and NEVER overwriting ChasePlayer!
                    if (mapSpawns.Count > 0)
                    {
                        bool anySpawnModified = false;
                        foreach (var spawn in mapSpawns)
                        {
                            int tid = spawn.Record.TeamID;
                            if (tid != 0 &&
                                (protection.ScriptedMatchHashes.Contains(tid) ||
                                 protection.ScriptedTeamConfigHashes.Contains(tid) ||
                                 protection.ScriptedTeamParamHashes.Contains(tid)))
                            {
                                continue;
                            }

                            (int[] ParamHashes, int[] BaseHashes) linkedSlots = default;
                            bool hasLinkedRoster = tid != 0 && encountTeamMap.TryGetValue(tid, out linkedSlots);
                            var usedBasesInSpawn = new HashSet<int>();

                            for (int slotIdx = 0; slotIdx < 5; slotIdx++)
                            {
                                int origId = GetSpawnPlayerSlot(spawn.Record, slotIdx);
                                if (origId == 0)
                                    continue;

                                if (paramByHash.ContainsKey(origId))
                                {
                                    // Slot stores a CharaParam hash (para_cpXXXX): MUST write a CharaParam hash!
                                    if (protection.ProtectedStoryPlayerParamHashes.Contains(origId))
                                    {
                                        if (paramByHash[origId].BaseHash != 0)
                                            usedBasesInSpawn.Add(paramByHash[origId].BaseHash);
                                        continue;
                                    }

                                    int candidateParam = 0;
                                    if (hasLinkedRoster && linkedSlots.ParamHashes[slotIdx] != 0 && paramByHash.ContainsKey(linkedSlots.ParamHashes[slotIdx]))
                                    {
                                        candidateParam = linkedSlots.ParamHashes[slotIdx];
                                    }
                                    else if (paramDerangement.TryGetValue(origId, out int derangedParam))
                                    {
                                        candidateParam = derangedParam;
                                    }

                                    if (candidateParam != 0)
                                    {
                                        candidateParam = EnsureUniqueTeamPlayer(
                                            origId,
                                            candidateParam,
                                            requireGk: false,
                                            usedBasesInSpawn,
                                            paramByHash,
                                            swappableParams,
                                            dedupRng);
                                        if (candidateParam != origId)
                                        {
                                            SetSpawnPlayerSlot(spawn.Record, slotIdx, candidateParam);
                                            anySpawnModified = true;
                                        }
                                    }
                                }
                                else if (baseByHash.ContainsKey(origId))
                                {
                                    // Slot stores a CharaBase hash (cpXXXX): MUST write a CharaBase hash!
                                    int candidateBase = 0;
                                    if (hasLinkedRoster && linkedSlots.BaseHashes[slotIdx] != 0 && baseByHash.ContainsKey(linkedSlots.BaseHashes[slotIdx]))
                                    {
                                        candidateBase = linkedSlots.BaseHashes[slotIdx];
                                    }
                                    else if (baseDerangement.TryGetValue(origId, out int derangedBase) && baseByHash.ContainsKey(derangedBase))
                                    {
                                        candidateBase = derangedBase;
                                    }

                                    if (candidateBase != 0 && usedBasesInSpawn.Contains(candidateBase))
                                    {
                                        var alt = swappableParams.FirstOrDefault(p => p.BaseHash != 0 && p.BaseHash != origId && !usedBasesInSpawn.Contains(p.BaseHash));
                                        if (alt != null)
                                            candidateBase = alt.BaseHash;
                                    }

                                    if (candidateBase != 0)
                                    {
                                        usedBasesInSpawn.Add(candidateBase);
                                        if (candidateBase != origId)
                                        {
                                            SetSpawnPlayerSlot(spawn.Record, slotIdx, candidateBase);
                                            anySpawnModified = true;
                                        }
                                    }
                                }
                            }
                        }

                        if (anySpawnModified)
                        {
                            MapDataService.SaveEncounterSpawns(game.IE_A.Directory, mapSpawns);
                        }
                    }
                }

                if (options.RandomizePalpackRecruits && palpackCards.Length >= 2)
                {
                    DeterministicRng palpackRng = seeds.GetSubsystem("players.palpack");
                    // Derange RecrutedCharacterId strictly among coherent non-protected PalPack recruits
                    var eligibleCards = palpackCards
                        .Where(c => c.RecrutedCharacterId != 0 &&
                                    !protection.ProtectedStoryPlayerParamHashes.Contains(c.RecrutedCharacterId) &&
                                    visualProfile.CoherentPlayableParamHashes.Contains(c.RecrutedCharacterId))
                        .ToList();

                    if (eligibleCards.Count >= 2)
                    {
                        var originalRecruits = eligibleCards.Select(c => c.RecrutedCharacterId).ToList();
                        var derangedRecruits = palpackRng.CreateDerangement(originalRecruits);
                        var recruitRemap = new Dictionary<int, int>(eligibleCards.Count);

                        for (int i = 0; i < eligibleCards.Count; i++)
                        {
                            recruitRemap[originalRecruits[i]] = derangedRecruits[i];
                            eligibleCards[i].RecrutedCharacterId = derangedRecruits[i];
                            report.RandomizedPalpackCardsCount++;
                        }

                        // Remap player prerequisites (PalpackConditionTypes: 2 = Player) and prevent self-cycles
                        foreach (var card in palpackCards)
                        {
                            RemapPalpackPrerequisiteSlot(card, 1, recruitRemap, swappableParams, palpackRng);
                            RemapPalpackPrerequisiteSlot(card, 2, recruitRemap, swappableParams, palpackRng);
                            RemapPalpackPrerequisiteSlot(card, 3, recruitRemap, swappableParams, palpackRng);
                            RemapPalpackPrerequisiteSlot(card, 4, recruitRemap, swappableParams, palpackRng);
                        }

                        game.SaveItems(palpackCards);
                    }
                }

                game.SaveTeamConfig(storyTeams, encountTeams);

                if (!report.BlindPlayMode && paramDerangement.Count > 0)
                {
                    report.SpoilerLogEntries.Add(
                        $"[Jugadores] Derangement estricto aplicado sobre {paramDerangement.Count} jugadores compatibles.");
                }
            }

            // 2. Optional Atomic Visual Swap for Story Characters (preserving BaseHash & ModelNumber < 2048)
            if (options.SwapStoryPlayerVisualIdentities)
            {
                SwapVisualAssetsAtomically(game, seeds.GetSubsystem("players.visual_swap"), charaBases, charaParams, visualProfile);
                game.SaveCharaBase(charaBases);
            }

            // Identify which players currently sit in Goalkeeper slot #1 on any active team so we don't leave a team GK-less
            var activeSlot1Goalkeepers = new HashSet<int>();
            if (options.PreserveGoalkeeperRolesInRosters)
            {
                foreach (var st in storyTeams)
                {
                    if (st.Player1 != 0) activeSlot1Goalkeepers.Add(st.Player1);
                }
                foreach (var et in encountTeams)
                {
                    if (et.Player1 != 0) activeSlot1Goalkeepers.Add(et.Player1);
                }
            }

            // 3. Positions (1=GK, 2=FW, 3=MF, 4=DF)
            if (options.RandomizePositions)
            {
                DeterministicRng posRng = seeds.GetSubsystem("players.positions");
                foreach (var cp in charaParams)
                {
                    if (!visualProfile.CoherentPlayableParamHashes.Contains(cp.ParamHash))
                        continue;
                    if (protection.ProtectedStoryPlayerParamHashes.Contains(cp.ParamHash))
                        continue;
                    if (activeSlot1Goalkeepers.Contains(cp.ParamHash))
                    {
                        cp.PlayerPosition = 1;
                        continue;
                    }

                    int newPos = posRng.NextIntInclusive(1, 4);
                    if (newPos != cp.PlayerPosition)
                    {
                        cp.PlayerPosition = newPos;
                        report.RandomizedPlayerPositionsCount++;
                    }
                }
            }

            // 4. Elements (1=Wind, 2=Wood, 3=Fire, 4=Earth)
            if (options.RandomizeElements)
            {
                DeterministicRng elemRng = seeds.GetSubsystem("players.elements");
                foreach (var cp in charaParams)
                {
                    if (!visualProfile.CoherentPlayableParamHashes.Contains(cp.ParamHash))
                        continue;

                    cp.Element = elemRng.NextIntInclusive(1, 4);
                    report.RandomizedPlayerElementsCount++;
                }
            }

            // 5. Base Stats & Growth Curves
            if (options.RandomizeBaseStats)
            {
                DeterministicRng statRng = seeds.GetSubsystem("players.stats");
                int variance = Math.Clamp(options.StatVariancePercent, 5, 60);

                foreach (var cp in charaParams)
                {
                    if (!visualProfile.CoherentPlayableParamHashes.Contains(cp.ParamHash))
                        continue;

                    cp.FP = ScaleStat(cp.FP, variance, 95, 220, statRng);
                    cp.TP = ScaleStat(cp.TP, variance, 95, 210, statRng); // TP >= 95 ensures starter moves remain usable
                    cp.Kick = ScaleStat(cp.Kick, variance, 25, 180, statRng);
                    cp.Dribble = ScaleStat(cp.Dribble, variance, 25, 180, statRng);
                    cp.Technique = ScaleStat(cp.Technique, variance, 25, 180, statRng);
                    cp.Block = ScaleStat(cp.Block, variance, 25, 180, statRng);
                    cp.Speed = ScaleStat(cp.Speed, variance, 25, 180, statRng);
                    cp.Stamina = ScaleStat(cp.Stamina, variance, 25, 180, statRng);
                    cp.Catch = ScaleStat(cp.Catch, variance, 25, 180, statRng);
                    cp.Luck = ScaleStat(cp.Luck, variance, 20, 160, statRng);
                    cp.Freedom = ScaleStat(Math.Max(20, cp.Freedom), variance, 15, 120, statRng);

                    // Role-coherent floor so randomized GKs can actually stop shots and FWs can score
                    if (cp.PlayerPosition == 1 && cp.Catch < 65)
                        cp.Catch = statRng.NextIntInclusive(65, 95);
                    else if (cp.PlayerPosition == 2 && cp.Kick < 65)
                        cp.Kick = statRng.NextIntInclusive(65, 95);

                    report.RandomizedPlayerStatsCount++;
                }
            }

            // 6. Fighting Spirits (Avatars)
            if (options.RandomizeFightingSpirits && visualProfile.ValidAvatarHashes.Count > 0)
            {
                DeterministicRng avatarRng = seeds.GetSubsystem("players.avatars");
                var avatarPool = visualProfile.ValidAvatarHashes.OrderBy(x => x).ToList();
                int chance = Math.Clamp(options.FightingSpiritChancePercent, 0, 100);

                foreach (var cp in charaParams)
                {
                    if (!visualProfile.CoherentPlayableParamHashes.Contains(cp.ParamHash))
                        continue;

                    // Story protagonists whose Fighting Spirits are scripted in story matches:
                    // NEVER strip their Fighting Spirit to 0, and preserve exact avatar if script-referenced!
                    if (protection.ProtectedAvatarBearerParamHashes.Contains(cp.ParamHash))
                    {
                        if (cp.FightingSpiritHash != 0 &&
                            protection.ScriptReferencedAvatarHashes.Contains(cp.FightingSpiritHash))
                        {
                            continue;
                        }

                        int assigned = avatarRng.PickOne(avatarPool);
                        cp.FightingSpiritHash = assigned;
                        cp.FightingSpiritMatchkHash = assigned;
                        report.RandomizedPlayerAvatarsCount++;
                        continue;
                    }

                    if (avatarRng.NextChancePercent(chance))
                    {
                        int assigned = avatarRng.PickOne(avatarPool);
                        cp.FightingSpiritHash = assigned;
                        cp.FightingSpiritMatchkHash = assigned;
                        report.RandomizedPlayerAvatarsCount++;
                    }
                    else
                    {
                        cp.FightingSpiritHash = 0;
                        cp.FightingSpiritMatchkHash = 0;
                    }
                }
            }

            game.SaveCharaparams(charaParams);
        }

        private static Dictionary<int, int> BuildPlayerDerangementMap(
            List<ICharaparam> swappablePlayers,
            DeterministicRng rng,
            bool preservePositionBuckets)
        {
            var map = new Dictionary<int, int>(swappablePlayers.Count);
            if (!preservePositionBuckets)
            {
                var allHashes = swappablePlayers.Select(p => p.ParamHash).Distinct().ToList();
                return rng.CreateDerangementMap(allHashes);
            }

            var leftovers = new List<int>();
            foreach (var group in swappablePlayers.GroupBy(p => p.PlayerPosition))
            {
                var bucket = group.Select(p => p.ParamHash).Distinct().ToList();
                if (bucket.Count >= 2)
                {
                    var bucketMap = rng.CreateDerangementMap(bucket);
                    foreach (var kvp in bucketMap)
                        map[kvp.Key] = kvp.Value;
                }
                else
                {
                    leftovers.AddRange(bucket);
                }
            }

            if (leftovers.Count >= 2)
            {
                var leftoverMap = rng.CreateDerangementMap(leftovers);
                foreach (var kvp in leftoverMap)
                    map[kvp.Key] = kvp.Value;
            }

            return map;
        }

        private static void RemapPalpackPrerequisiteSlot(
            ItemConfigPalpackCard card,
            int slotIndex,
            Dictionary<int, int> recruitRemap,
            List<ICharaparam> fallbackPool,
            DeterministicRng rng)
        {
            var typeProp = typeof(ItemConfigPalpackCard).GetProperty($"PrerequisitesType{slotIndex}")!;
            var idProp = typeof(ItemConfigPalpackCard).GetProperty($"PrerequisitesId{slotIndex}")!;

            int condType = (int)typeProp.GetValue(card)!;
            int condId = (int)idProp.GetValue(card)!;

            // PalpackConditionTypes.json: 2 = Player prerequisite
            if (condType != 2 || condId == 0)
                return;

            int newPrereq = recruitRemap.TryGetValue(condId, out int mapped) ? mapped : condId;

            // Prevent impossible self-prerequisite cycle (cannot require Player X to scout Player X!)
            if (newPrereq == card.RecrutedCharacterId && fallbackPool.Count >= 2)
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    int candidate = rng.PickOne(fallbackPool).ParamHash;
                    if (candidate != card.RecrutedCharacterId)
                    {
                        newPrereq = candidate;
                        break;
                    }
                }
            }

            idProp.SetValue(card, newPrereq);
        }

        private static void SwapVisualAssetsAtomically(
            SafeGOGame game,
            DeterministicRng rng,
            ICharabase[] charaBases,
            ICharaparam[] charaParams,
            VisualCoherenceProfile visualProfile)
        {
            var coherentBases = charaBases
                .Where(b => visualProfile.CoherentPlayableBaseHashes.Contains(b.BaseHash) &&
                            b.ModelNumber > 0 &&
                            b.ModelNumber < VisualCoherenceValidator.MaxEngineModelNumberExclusive)
                .ToList();

            if (coherentBases.Count < 2)
                return;

            var root = game.IE_A.Directory;
            bool hasAllDirs = root.IsFolderExists("/data/bustup/face") &&
                              root.IsFolderExists("/data/chr/model/rpg/face") &&
                              root.IsFolderExists("/data/chr/model/waza/face");
            if (!hasAllDirs)
                return;

            // Snapshot complete visual tuples + binary files so swaps are 100% atomic
            var snapshots = new List<(
                int NameHash, int NicknameHash, int DescHash, int Body, int Gender, int Skin, int Year,
                byte[] Bustup, byte[] RpgModel, byte[] WazaModel)>();

            var validSwapTargets = new List<ICharabase>();
            foreach (var cb in coherentBases)
            {
                string cpCode = $"cp{cb.ModelNumber:D4}";
                string pBust = $"/data/bustup/face/{cpCode}a.xi";
                string pRpg = $"/data/chr/model/rpg/face/{cpCode}m.xc";
                string pWaza = $"/data/chr/model/waza/face/{cpCode}a.xc";

                byte[] bustBytes = Arc0SafeArchive.TryReadFileFromPath(root, pBust);
                byte[] rpgBytes = Arc0SafeArchive.TryReadFileFromPath(root, pRpg);
                byte[] wazaBytes = Arc0SafeArchive.TryReadFileFromPath(root, pWaza);
                if (bustBytes == null || rpgBytes == null || wazaBytes == null)
                    continue;

                validSwapTargets.Add(cb);
                snapshots.Add((
                    cb.NameHash, cb.NicknameHash, cb.DescriptionHash, cb.Body, cb.Gender, cb.Skin, cb.Year,
                    bustBytes,
                    rpgBytes,
                    wazaBytes));
            }

            if (validSwapTargets.Count < 2)
                return;

            var indices = Enumerable.Range(0, validSwapTargets.Count).ToList();
            var derangedIndices = rng.CreateDerangement(indices);

            for (int i = 0; i < validSwapTargets.Count; i++)
            {
                var target = validSwapTargets[i];
                var donor = snapshots[derangedIndices[i]];
                string cpCode = $"cp{target.ModelNumber:D4}";

                // Keep BaseHash and ModelNumber unchanged (< 2048!) while atomically transferring
                // body rig, skin tone, gender, name hashes, 2D portrait, RPG head, and Waza match head!
                target.NameHash = donor.NameHash;
                target.NicknameHash = donor.NicknameHash;
                target.DescriptionHash = donor.DescHash;
                target.Body = donor.Body;
                target.Gender = donor.Gender;
                target.Skin = donor.Skin;
                target.Year = donor.Year;

                Arc0SafeArchive.WriteFileAtPath(root, $"/data/bustup/face/{cpCode}a.xi", donor.Bustup);
                Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/rpg/face/{cpCode}m.xc", donor.RpgModel);
                Arc0SafeArchive.WriteFileAtPath(root, $"/data/chr/model/waza/face/{cpCode}a.xc", donor.WazaModel);
            }
        }

        private static int EnsureUniqueTeamPlayer(
            int oldParamHash,
            int proposedParamHash,
            bool requireGk,
            HashSet<int> usedBasesOnTeam,
            Dictionary<int, ICharaparam> paramByHash,
            List<ICharaparam> swappableParams,
            DeterministicRng rng)
        {
            if (paramByHash.TryGetValue(proposedParamHash, out var proposedObj) &&
                proposedObj.BaseHash != 0 &&
                !usedBasesOnTeam.Contains(proposedObj.BaseHash))
            {
                usedBasesOnTeam.Add(proposedObj.BaseHash);
                return proposedParamHash;
            }

            var candidates = swappableParams
                .Where(p => p.ParamHash != oldParamHash &&
                            p.BaseHash != 0 &&
                            !usedBasesOnTeam.Contains(p.BaseHash) &&
                            (!requireGk || p.PlayerPosition == 1))
                .ToList();

            if (candidates.Count == 0 && requireGk)
            {
                candidates = swappableParams
                    .Where(p => p.ParamHash != oldParamHash && p.BaseHash != 0 && !usedBasesOnTeam.Contains(p.BaseHash))
                    .ToList();
            }

            if (candidates.Count > 0)
            {
                var chosen = rng.PickOne(candidates);
                usedBasesOnTeam.Add(chosen.BaseHash);
                return chosen.ParamHash;
            }

            if (paramByHash.TryGetValue(proposedParamHash, out var fallbackObj) && fallbackObj.BaseHash != 0)
                usedBasesOnTeam.Add(fallbackObj.BaseHash);
            return proposedParamHash;
        }

        private static int GetSpawnPlayerSlot(EncountMapRecord rec, int slotIndex)
        {
            return slotIndex switch
            {
                0 => rec.PlayerID1,
                1 => rec.PlayerID2,
                2 => rec.PlayerID3,
                3 => rec.PlayerID4,
                4 => rec.PlayerID5,
                _ => 0
            };
        }

        private static void SetSpawnPlayerSlot(EncountMapRecord rec, int slotIndex, int value)
        {
            switch (slotIndex)
            {
                case 0: rec.PlayerID1 = value; break;
                case 1: rec.PlayerID2 = value; break;
                case 2: rec.PlayerID3 = value; break;
                case 3: rec.PlayerID4 = value; break;
                case 4: rec.PlayerID5 = value; break;
            }
        }

        private static int ScaleStat(int baseValue, int variancePercent, int minBound, int maxBound, DeterministicRng rng)
        {
            int deltaPct = rng.NextIntInclusive(-variancePercent, variancePercent);
            int scaled = (int)Math.Round(baseValue * (100 + deltaPct) / 100.0);
            return Math.Clamp(scaled, minBound, maxBound);
        }
    }
}
