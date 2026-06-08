using System.ComponentModel;
using System.Collections.Generic;

namespace Shared.Config;

public interface IPluginConfig : INotifyPropertyChanged
{
    // Enables the plugin
    bool Enabled { get; set; }

    // Enables checking for changes in patched game code (disable this on Proton/Linux)
    bool DetectCodeChanges { get; set; }

    double ConcealDistance { get; set; }
    int ConcealInterval { get; set; }
    double RevealDistance { get; set; }
    int RevealInterval { get; set; }
    ProductionConcealmentMode ProductionConcealment { get; set; }
    double ProductionBoostLevel { get; set; }
    double MaxBoostHours { get; set; }
    bool ConcealPirates { get; set; }
    bool RemoteControlKeepAliveAction { get; set; }
    double DynamicConcealQueryInterval { get; set; }
    double DynamicConcealScanInterval { get; set; }
    List<string> ExcludedSubtypes { get; set; }
    List<DynamicConcealmentRule> DynamicConcealment { get; set; }
}
