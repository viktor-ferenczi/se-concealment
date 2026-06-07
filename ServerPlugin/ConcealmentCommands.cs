using System.Collections.Generic;
using System.Linq;
using PluginSdk.Commands;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace ServerPlugin;

[CommandRoot("conceal", "Concealment", "grid concealment controls")]
public sealed class ConcealmentCommands : CommandModule
{
    private static Plugin Plugin => Plugin.Instance;

    [Command("run", "Conceal grids farther than the configured or supplied distance from online players")]
    [Permission(MyPromoteLevel.SpaceMaster)]
    public string Conceal(double distance = 0)
    {
        if (Plugin?.Manager == null)
            return "Concealment is not loaded.";

        if (distance == 0)
            distance = Plugin.ConfigData.ConcealDistance;

        var count = Plugin.Manager.ConcealGrids(distance);
        return $"{count} grids concealed.";
    }

    [Command("reveal", "Reveal concealed grids near your controlled entity")]
    [Permission(MyPromoteLevel.SpaceMaster)]
    public CommandReply Reveal(double distance = 1000)
    {
        if (Plugin?.Manager == null)
            return CommandReply.Error("Concealment is not loaded.");

        if (!TryGetCallerPosition(out var position))
            return CommandReply.Error("You must be controlling an entity.");

        var sphere = new BoundingSphereD(position, distance);
        var count = Plugin.Manager.RevealGridsInSphere(sphere);
        return CommandReply.Ok($"{count} grids revealed.");
    }

    [Command("reveal all", "Reveal every concealed grid")]
    [Permission(MyPromoteLevel.SpaceMaster)]
    public string RevealAll()
    {
        if (Plugin?.Manager == null)
            return "Concealment is not loaded.";

        var count = Plugin.Manager.RevealAll();
        return $"{count} grids revealed.";
    }

    [Command("on", "Enable concealment and run one concealment scan")]
    [Permission(MyPromoteLevel.SpaceMaster)]
    public string Enable()
    {
        if (Plugin?.Manager == null)
            return "Concealment is not loaded.";

        Plugin.ConfigData.Enabled = true;
        var count = Plugin.Manager.ConcealGrids(Plugin.ConfigData.ConcealDistance);
        return $"Concealment enabled. {count} grids concealed.";
    }

    [Command("off", "Disable concealment and reveal every concealed grid")]
    [Permission(MyPromoteLevel.SpaceMaster)]
    public string Disable()
    {
        if (Plugin?.Manager == null)
            return "Concealment is not loaded.";

        Plugin.ConfigData.Enabled = false;
        var count = Plugin.Manager.RevealAll();
        return $"Concealment disabled. {count} grids revealed.";
    }

    private bool TryGetCallerPosition(out Vector3D position)
    {
        var players = new List<IMyPlayer>();
        MyAPIGateway.Players.GetPlayers(players, player => player != null && player.SteamUserId == Context.Caller.SteamId);
        var entity = players.FirstOrDefault()?.Controller?.ControlledEntity?.Entity;

        if (entity == null)
        {
            position = default;
            return false;
        }

        position = entity.GetPosition();
        return true;
    }
}
