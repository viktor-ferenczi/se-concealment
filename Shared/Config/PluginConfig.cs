using System.Collections.Generic;
using System.Xml.Serialization;
using PluginSdk.Config;

namespace Shared.Config;

public enum DynamicConcealType
{
    HostileCharacters = 0,
    NeutralCharacters,
    FriendlyCharacters,
    HostileGrids,
    NeutralGrids,
    None
}

public enum ProductionConcealmentMode
{
    [EnumCaption("Off - keep active production revealed")]
    Off = 0,

    [EnumCaption("Conceal - production pauses while hidden")]
    Conceal,

    [EnumCaption("Approximate - catch up after reveal")]
    Approximate
}

public struct DynamicConcealmentRule
{
    [StructMember("Displayed rule name")]
    [StructCaption]
    public string Name { get; set; }

    [StructMember("Object builder type, without the MyObjectBuilder_ prefix. Example: LargeGatlingTurret")]
    public string TargetType { get; set; }

    [StructMember("Subtype to target. Leave empty to target every subtype of the type.")]
    public string TargetSubtype { get; set; }

    [StructMember("Nearby entity relation that reveals the block update")]
    public DynamicConcealType ConcealType { get; set; }

    [StructMember("Distance in meters")]
    public double Distance { get; set; }
}

[Tab("general", caption: "General")]
[Tab("dynamic", caption: "Dynamic")]
[Section("general-timing", parent: "general", caption: "Timing")]
[Section("general-rules", parent: "general", caption: "Rules")]
[Section("dynamic-timing", parent: "dynamic", caption: "Timing")]
[Section("dynamic-rules", parent: "dynamic", caption: "Rules")]
[XmlRoot("Settings")]
public class PluginConfig : PluginSdk.Config.PluginConfig, IPluginConfig
{
    [BoolOption("Enable periodic concealment", Parent = "general")]
    public bool Enabled { get; set => SetField(ref field, value); } = true;

    [BoolOption("Check patched game code for expected IL changes", Parent = "general")]
    public bool DetectCodeChanges { get; set => SetField(ref field, value); } = true;

    [DoubleOption(0, 10000000, "Hide eligible grid groups when every online player is farther away than this many meters.", Parent = "general-timing")]
    public double ConcealDistance { get; set => SetField(ref field, value); } = 15000;

    [IntOption(1, 864000, "Simulation ticks between scans that hide eligible grids. At 60 TPS, 18000 ticks is 5 minutes.", Parent = "general-timing")]
    public int ConcealInterval { get; set => SetField(ref field, value); } = 18000;

    [DoubleOption(0, 10000000, "Reveal concealed grid groups when an online player is within this many meters.", Parent = "general-timing")]
    public double RevealDistance { get; set => SetField(ref field, value); } = 12000;

    [IntOption(1, 864000, "Simulation ticks between scans that reveal hidden grids near players.", Parent = "general-timing")]
    public int RevealInterval { get; set => SetField(ref field, value); } = 60;

    [EnumOption("Active assembler/refinery handling. Off keeps active production grids revealed. Conceal hides them and production pauses. Approximate hides them, then banks boost time on reveal so they catch up at higher speed while online.", Parent = "general-rules")]
    public ProductionConcealmentMode ProductionConcealment { get; set => SetField(ref field, value); } =
        ProductionConcealmentMode.Approximate;

    [DoubleOption(1.0, 1000.0, "Approximate catch-up speed. Production runs this many times faster while boosting, drawing this many times more power. Concealed time divided by this level is how long the boost lasts online.", Parent = "general-rules")]
    public double ProductionBoostLevel { get; set => SetField(ref field, value); } = 5.0;

    [DoubleOption(0.0, 168.0, "Maximum banked boost time in hours of online catch-up. Boost banked above this limit is lost, capping how much catch-up can accumulate while concealed.", Parent = "general-rules")]
    public double MaxBoostHours { get; set => SetField(ref field, value); } = 8.0;

    [BoolOption("Allow pirate-owned grids to be concealed", Parent = "general-rules")]
    public bool ConcealPirates { get; set => SetField(ref field, value); }

    [BoolOption("Register a remote control terminal action that temporarily keeps a grid revealed", Parent = "general-rules")]
    public bool RemoteControlKeepAliveAction { get; set => SetField(ref field, value); }

    [ListOption(description: "Block subtypes that keep an entire physical grid group revealed", Parent = "general-rules")]
    public List<string> ExcludedSubtypes { get; set => SetField(ref field, value); } = new List<string>();

    [DoubleOption(1, 3600, "Seconds between expensive nearby-entity queries for dynamic concealment", Parent = "dynamic-timing")]
    public double DynamicConcealQueryInterval { get; set => SetField(ref field, value); } = 15;

    [DoubleOption(0.1, 3600, "Seconds between per-block dynamic concealment state refreshes", Parent = "dynamic-timing")]
    public double DynamicConcealScanInterval { get; set => SetField(ref field, value); } = 2;

    [ListOption(description: "Rules that skip selected block updates while no configured entities are nearby", Parent = "dynamic-rules")]
    public List<DynamicConcealmentRule> DynamicConcealment { get; set => SetField(ref field, value); } =
        new List<DynamicConcealmentRule>();
}
