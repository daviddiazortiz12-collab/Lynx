using StudioElevenLib.Level5.Binary.Logic;

namespace Lynx.Core.Maps
{
    /// <summary>
    /// Overworld treasure chest record in Inazuma Eleven GO Light &amp; Shadow
    /// (verified from Tiniifan/Ocelot TBoxConfig).
    /// </summary>
    public sealed class TBoxConfigRecord
    {
        public int TBoxID { get; set; }
        public float LocationX { get; set; }
        public float LocationZ { get; set; }
        public float LocationY { get; set; }
        public float Rotation { get; set; }
        public int ItemIDLight { get; set; }
        public int TBoxFlag { get; set; }
        public int TboxType { get; set; } // 0 = Normal, 1 = Gold, 2 = Silver
        public int ItemIDShadow { get; set; }
    }

    /// <summary>
    /// Overworld soccer battle (pachanga) encounter spawn record in Inazuma Eleven GO Light &amp; Shadow
    /// (verified from Tiniifan/Ocelot EncountInfo).
    /// </summary>
    public sealed class EncountMapRecord
    {
        public int ChasePlayer { get; set; }
        public int TeamID { get; set; }
        public int Unk2 { get; set; }
        public float LocationX { get; set; }
        public float LocationZ { get; set; }
        public float LocationY { get; set; }
        public int Unk6 { get; set; }
        public int Unk7 { get; set; }
        public float Rotation { get; set; }
        public int TextIDAsk { get; set; }
        public int TextIDWait { get; set; }

        [CfgBinStringAsInt]
        public string Condition { get; set; }

        public int PlayerID1 { get; set; }
        public int PlayerID2 { get; set; }
        public int PlayerID3 { get; set; }
        public int PlayerID4 { get; set; }
        public int PlayerID5 { get; set; }
        public int Uniform { get; set; }
        public int Boots { get; set; }
        public int TextIDLost { get; set; }
        public int TextIDWin { get; set; }
        public int Unk21 { get; set; }
    }

    /// <summary>
    /// Represents a treasure chest located in a specific map binary inside <c>ie_a.fa</c>,
    /// retaining its virtual path and entry index for non-destructive saving.
    /// </summary>
    public sealed class MapTreasureChestEntry
    {
        public string MapId { get; init; } = string.Empty;
        public string VirtualFilePath { get; init; } = string.Empty;
        public string EntryName { get; init; } = string.Empty;
        public int NodeIndex { get; init; }
        public TBoxConfigRecord Record { get; init; } = new TBoxConfigRecord();
    }

    /// <summary>
    /// Represents an overworld pachanga spawn located in a specific map binary inside <c>ie_a.fa</c>.
    /// </summary>
    public sealed class MapEncounterSpawnEntry
    {
        public string MapId { get; init; } = string.Empty;
        public string VirtualFilePath { get; init; } = string.Empty;
        public string EntryName { get; init; } = string.Empty;
        public int NodeIndex { get; init; }
        public EncountMapRecord Record { get; init; } = new EncountMapRecord();
    }

    /// <summary>
    /// Dependencies discovered in map <c>*_funcpt.bin</c> and <c>*.talk.bin</c> files that gate progression.
    /// </summary>
    public sealed class MapProgressionDependencies
    {
        public System.Collections.Generic.HashSet<int> ProtectedTBoxFlags { get; } = new();
        public System.Collections.Generic.HashSet<int> ReferencedFuncptItemIds { get; } = new();
        public System.Collections.Generic.HashSet<int> ReferencedScriptHashes { get; } = new();
        public System.Collections.Generic.HashSet<int> ReferencedRouteHashes { get; } = new();
    }
}
