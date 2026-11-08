using System;
using Lynx.InazumaEleven.Logic;

namespace Lynx.Core.Games
{
    /// <summary>
    /// Compile-time, zero-reflection slot accessors for <see cref="IStoryTeamInfo"/>,
    /// <see cref="IEncountTeamInfo"/>, and <see cref="ITeamParamInfo"/>.
    /// Avoids C# interface reflection pitfalls where <c>IStoryTeamInfo : IEncountTeamInfo</c>
    /// declares <c>Player1..5</c> on the base interface and <c>Player6..16</c> on the derived interface.
    /// </summary>
    public static class TeamSlotAccessor
    {
        public static int GetStoryPlayer(IStoryTeamInfo team, int slot1Based) => slot1Based switch
        {
            1 => team.Player1,
            2 => team.Player2,
            3 => team.Player3,
            4 => team.Player4,
            5 => team.Player5,
            6 => team.Player6,
            7 => team.Player7,
            8 => team.Player8,
            9 => team.Player9,
            10 => team.Player10,
            11 => team.Player11,
            12 => team.Player12,
            13 => team.Player13,
            14 => team.Player14,
            15 => team.Player15,
            16 => team.Player16,
            _ => throw new ArgumentOutOfRangeException(nameof(slot1Based))
        };

        public static void SetStoryPlayer(IStoryTeamInfo team, int slot1Based, int paramHash)
        {
            switch (slot1Based)
            {
                case 1: team.Player1 = paramHash; break;
                case 2: team.Player2 = paramHash; break;
                case 3: team.Player3 = paramHash; break;
                case 4: team.Player4 = paramHash; break;
                case 5: team.Player5 = paramHash; break;
                case 6: team.Player6 = paramHash; break;
                case 7: team.Player7 = paramHash; break;
                case 8: team.Player8 = paramHash; break;
                case 9: team.Player9 = paramHash; break;
                case 10: team.Player10 = paramHash; break;
                case 11: team.Player11 = paramHash; break;
                case 12: team.Player12 = paramHash; break;
                case 13: team.Player13 = paramHash; break;
                case 14: team.Player14 = paramHash; break;
                case 15: team.Player15 = paramHash; break;
                case 16: team.Player16 = paramHash; break;
                default: throw new ArgumentOutOfRangeException(nameof(slot1Based));
            }
        }

        public static void SetStoryKitNumber(IStoryTeamInfo team, int slot1Based, int kitNumber)
        {
            switch (slot1Based)
            {
                case 1: team.NumberPlayer1 = kitNumber; break;
                case 2: team.NumberPlayer2 = kitNumber; break;
                case 3: team.NumberPlayer3 = kitNumber; break;
                case 4: team.NumberPlayer4 = kitNumber; break;
                case 5: team.NumberPlayer5 = kitNumber; break;
                case 6: team.NumberPlayer6 = kitNumber; break;
                case 7: team.NumberPlayer7 = kitNumber; break;
                case 8: team.NumberPlayer8 = kitNumber; break;
                case 9: team.NumberPlayer9 = kitNumber; break;
                case 10: team.NumberPlayer10 = kitNumber; break;
                case 11: team.NumberPlayer11 = kitNumber; break;
                case 12: team.NumberPlayer12 = kitNumber; break;
                case 13: team.NumberPlayer13 = kitNumber; break;
                case 14: team.NumberPlayer14 = kitNumber; break;
                case 15: team.NumberPlayer15 = kitNumber; break;
                case 16: team.NumberPlayer16 = kitNumber; break;
                default: throw new ArgumentOutOfRangeException(nameof(slot1Based));
            }
        }

        public static int GetEncountPlayer(IEncountTeamInfo team, int slot1Based) => slot1Based switch
        {
            1 => team.Player1,
            2 => team.Player2,
            3 => team.Player3,
            4 => team.Player4,
            5 => team.Player5,
            _ => throw new ArgumentOutOfRangeException(nameof(slot1Based))
        };

        public static void SetEncountPlayer(IEncountTeamInfo team, int slot1Based, int paramHash)
        {
            switch (slot1Based)
            {
                case 1: team.Player1 = paramHash; break;
                case 2: team.Player2 = paramHash; break;
                case 3: team.Player3 = paramHash; break;
                case 4: team.Player4 = paramHash; break;
                case 5: team.Player5 = paramHash; break;
                default: throw new ArgumentOutOfRangeException(nameof(slot1Based));
            }
        }

        public static void SetEncountKitNumber(IEncountTeamInfo team, int slot1Based, int kitNumber)
        {
            switch (slot1Based)
            {
                case 1: team.NumberPlayer1 = kitNumber; break;
                case 2: team.NumberPlayer2 = kitNumber; break;
                case 3: team.NumberPlayer3 = kitNumber; break;
                case 4: team.NumberPlayer4 = kitNumber; break;
                case 5: team.NumberPlayer5 = kitNumber; break;
                default: throw new ArgumentOutOfRangeException(nameof(slot1Based));
            }
        }

        public static int GetTeamDrop(ITeamParamInfo tp, int slot1Based) => slot1Based switch
        {
            1 => tp.DropID1,
            2 => tp.DropID2,
            3 => tp.DropID3,
            4 => tp.DropID4,
            5 => tp.DropID5,
            6 => tp.DropID6,
            _ => throw new ArgumentOutOfRangeException(nameof(slot1Based))
        };

        public static void SetTeamDrop(ITeamParamInfo tp, int slot1Based, int itemId)
        {
            switch (slot1Based)
            {
                case 1: tp.DropID1 = itemId; break;
                case 2: tp.DropID2 = itemId; break;
                case 3: tp.DropID3 = itemId; break;
                case 4: tp.DropID4 = itemId; break;
                case 5: tp.DropID5 = itemId; break;
                case 6: tp.DropID6 = itemId; break;
                default: throw new ArgumentOutOfRangeException(nameof(slot1Based));
            }
        }
    }
}
