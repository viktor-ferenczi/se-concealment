#if !TORCH

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

    [DoubleOption(0, 10000000, "Conceal grids farther than this distance from every online player", Parent = "general-timing")]
    public double ConcealDistance { get; set => SetField(ref field, value); } = 15000;

    [IntOption(1, 864000, "Ticks between concealment scans", Parent = "general-timing")]
    public int ConcealInterval { get; set => SetField(ref field, value); } = 18000;

    [DoubleOption(0, 10000000, "Reveal concealed grids within this distance of an online player", Parent = "general-timing")]
    public double RevealDistance { get; set => SetField(ref field, value); } = 12000;

    [IntOption(1, 864000, "Ticks between reveal scans", Parent = "general-timing")]
    public int RevealInterval { get; set => SetField(ref field, value); } = 60;

    [BoolOption("Allow grids with active production/refineries to be concealed", Parent = "general-rules")]
    public bool ConcealProduction { get; set => SetField(ref field, value); } = true;

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

#endif
