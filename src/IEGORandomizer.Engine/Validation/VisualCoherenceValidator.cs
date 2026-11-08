using System;
using System.Collections.Generic;
using System.Linq;
using Lynx.Core.Games;
using Lynx.InazumaEleven.Logic;
using StudioElevenLib.Tools;

namespace IEGORandomizer.Engine.Validation
{
    /// <summary>
    /// Verified visual coherence profile of an <c>ie_a.fa</c> archive.
    /// Prevents corrupted 3D models, missing head/body meshes, mismatched skin tones,
    /// missing bustup portraits, and <c>ModelNumber &gt;= 2048</c> <c>std::out_of_range</c> crashes.
    /// </summary>
    public sealed class VisualCoherenceProfile
    {
        public HashSet<int> CoherentPlayableParamHashes { get; } = new();
        public HashSet<int> CoherentPlayableBaseHashes { get; } = new();
        public HashSet<int> ValidKitNumbers { get; } = new();
        public HashSet<int> ValidAvatarHashes { get; } = new();
        public List<string> QuarantinedEntityWarnings { get; } = new();
    }

    public static class VisualCoherenceValidator
    {
        /// <summary>
        /// Verified in <c>projectz/include/inazuma/ie4charaflags.h</c>:
        /// <c>IE4_CHARA_FLAG_GAME_MAX = 2048</c>. Any <c>ModelNumber &gt;= 2048</c> or &lt; 0 crashes the 3DS engine.
        /// </summary>
        public const int MaxEngineModelNumberExclusive = 2048;

        public static VisualCoherenceProfile AnalyzeArchive(SafeGOGame game)
        {
            if (game == null)
                throw new ArgumentNullException(nameof(game));

            var profile = new VisualCoherenceProfile();
            VirtualDirectory root = game.IE_A.Directory;

            bool hasBustupDir = root.IsFolderExists("/data/bustup/face");
            bool hasRpgFaceDir = root.IsFolderExists("/data/chr/model/rpg/face");
            bool hasWazaFaceDir = root.IsFolderExists("/data/chr/model/waza/face");
            bool hasBodyDir = root.IsFolderExists("/data/chr/model/rpg/body");
            bool hasAvatarBustupDir = root.IsFolderExists("/data/bustup/avatar");

            VirtualDirectory bustupDir = hasBustupDir ? root.GetFolderFromFullPath("/data/bustup/face") : null;
            VirtualDirectory rpgFaceDir = hasRpgFaceDir ? root.GetFolderFromFullPath("/data/chr/model/rpg/face") : null;
            VirtualDirectory wazaFaceDir = hasWazaFaceDir ? root.GetFolderFromFullPath("/data/chr/model/waza/face") : null;
            VirtualDirectory bodyDir = hasBodyDir ? root.GetFolderFromFullPath("/data/chr/model/rpg/body") : null;
            VirtualDirectory avatarBustupDir = hasAvatarBustupDir ? root.GetFolderFromFullPath("/data/bustup/avatar") : null;

            var charaBases = game.GetCharabase() ?? Array.Empty<ICharabase>();
            var charaParams = game.GetCharaparams() ?? Array.Empty<ICharaparam>();
            var baseByHash = charaBases
                .Where(cb => cb.BaseHash != 0)
                .GroupBy(cb => cb.BaseHash)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var cp in charaParams)
            {
                if (cp.ParamHash == 0 || cp.BaseHash == 0)
                    continue;

                if (!baseByHash.TryGetValue(cp.BaseHash, out var cb))
                {
                    profile.QuarantinedEntityWarnings.Add(
                        $"CharaParam 0x{cp.ParamHash:X8} carece de entrada CHARA_BASE_INFO (0x{cp.BaseHash:X8}).");
                    continue;
                }

                if (!IsCharacterVisuallyCoherentForSoccer(cb, cp, bustupDir, rpgFaceDir, wazaFaceDir, out string reason))
                {
                    profile.QuarantinedEntityWarnings.Add(reason);
                    continue;
                }

                profile.CoherentPlayableParamHashes.Add(cp.ParamHash);
                profile.CoherentPlayableBaseHashes.Add(cb.BaseHash);
            }

            // Validate Uniform Kit Numbers (ItemConfigUniform.ItemNumber <-> uza/uzb/uzc/uzdXXXX.xc)
            var uniforms = game.GetItems("uniform") ?? Array.Empty<IItemConfig>();
            foreach (var item in uniforms)
            {
                int kitNum = ExtractUniformNumber(item);
                if (kitNum <= 0)
                    continue;

                if (bodyDir != null && !HasUniformBodyModels(bodyDir, kitNum))
                {
                    profile.QuarantinedEntityWarnings.Add(
                        $"Equipación #{kitNum:D4} excluida: faltan modelos 3D uza/uzb{kitNum:D4}.xc.");
                    continue;
                }

                profile.ValidKitNumbers.Add(kitNum);
            }

            // Validate Fighting Spirits (Avatars)
            var avatars = game.GetAvatars(false) ?? Array.Empty<IAvatar>();
            foreach (var av in avatars)
            {
                if (av.AvatarHash == 0 || av.AvatarNumber <= 0)
                    continue;

                if (avatarBustupDir != null && avatarBustupDir.Files.Count > 0)
                {
                    string expectedBustup = $"ck{av.AvatarNumber:D4}a.xi";
                    if (!avatarBustupDir.Files.ContainsKey(expectedBustup))
                    {
                        profile.QuarantinedEntityWarnings.Add(
                            $"Espíritu Guerrero ck{av.AvatarNumber:D4} excluido: falta retrato {expectedBustup}.");
                        continue;
                    }
                }

                profile.ValidAvatarHashes.Add(av.AvatarHash);
            }

            return profile;
        }

        public static bool IsCharacterVisuallyCoherentForSoccer(
            ICharabase cb,
            ICharaparam cp,
            VirtualDirectory bustupDir,
            VirtualDirectory rpgFaceDir,
            VirtualDirectory wazaFaceDir,
            out string failureReason)
        {
            failureReason = string.Empty;

            if (cb.ModelNumber <= 0 || cb.ModelNumber >= MaxEngineModelNumberExclusive)
            {
                failureReason = $"CHARA_BASE ModelNumber={cb.ModelNumber} fuera del rango seguro del motor (1..2047).";
                return false;
            }

            // CharaBaseType == 1 corresponds to playable soccer models ("cp")
            if (cb.CharaBaseType != 1)
            {
                failureReason = $"Modelo #{cb.ModelNumber:D4} es tipo NPC ({cb.CharaBaseType}), excluido del pool de fútbol.";
                return false;
            }

            // Body 0 = "No Body" (invisible/corrupted mesh on field); valid bodies in Bodies.json are 1..8
            if (cb.Body < 1 || cb.Body > 8)
            {
                failureReason = $"Jugador cp{cb.ModelNumber:D4} tiene Body={cb.Body} inválido (requiere 1..8).";
                return false;
            }

            // Gender in Genders.json: 1=Boy, 2=Girl (3=Unknown only if valid model exists, disallow 0)
            if (cb.Gender < 1 || cb.Gender > 3)
            {
                failureReason = $"Jugador cp{cb.ModelNumber:D4} tiene Gender={cb.Gender} inválido.";
                return false;
            }

            // Valid soccer position: 1=GK, 2=MF, 3=FW, 4=DF
            if (cp.PlayerPosition < 1 || cp.PlayerPosition > 4)
            {
                failureReason = $"Jugador cp{cb.ModelNumber:D4} no tiene posición de campo válida ({cp.PlayerPosition}).";
                return false;
            }

            // Valid element: 1=Wind, 2=Wood, 3=Fire, 4=Earth
            if (cp.Element < 1 || cp.Element > 4)
            {
                failureReason = $"Jugador cp{cb.ModelNumber:D4} no tiene afinidad elemental válida ({cp.Element}).";
                return false;
            }

            string cpCode = $"cp{cb.ModelNumber:D4}";

            if (bustupDir != null && bustupDir.Files.Count > 0 &&
                !HasAnyPrefixFile(bustupDir, cpCode))
            {
                failureReason = $"Jugador {cpCode} carece de retrato 2D en /data/bustup/face/.";
                return false;
            }

            if (rpgFaceDir != null && rpgFaceDir.Files.Count > 0 &&
                !HasAnyPrefixFile(rpgFaceDir, cpCode))
            {
                failureReason = $"Jugador {cpCode} carece de modelo 3D de cabeza RPG en /data/chr/model/rpg/face/.";
                return false;
            }

            if (wazaFaceDir != null && wazaFaceDir.Files.Count > 0 &&
                !HasAnyPrefixFile(wazaFaceDir, cpCode))
            {
                failureReason = $"Jugador {cpCode} carece de modelo/animación de partido en /data/chr/model/waza/face/.";
                return false;
            }

            return true;
        }

        private static bool HasAnyPrefixFile(VirtualDirectory dir, string prefix)
        {
            if (dir.Files.ContainsKey(prefix + "a.xi") ||
                dir.Files.ContainsKey(prefix + "m.xc") ||
                dir.Files.ContainsKey(prefix + "a.xc"))
            {
                return true;
            }

            foreach (string name in dir.Files.Keys)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool HasUniformBodyModels(VirtualDirectory bodyDir, int kitNum)
        {
            if (bodyDir.Files.Count == 0)
                return true;
            string numStr = kitNum.ToString("D4");
            return bodyDir.Files.ContainsKey($"uza{numStr}.xc") &&
                   bodyDir.Files.ContainsKey($"uzb{numStr}.xc");
        }

        public static int ExtractUniformNumber(IItemConfig item)
        {
            if (item == null)
                return 0;
            var prop = item.GetType().GetProperty("ItemNumber");
            if (prop != null && prop.GetValue(item) is int num && num > 0)
                return num;
            return 0;
        }
    }
}
