using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared.Config;
using Shared.Logging;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game;
using VRage.Game.Components.Session;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Groups;
using VRageMath;
using VRage.ModAPI;

namespace ServerPlugin;

public sealed class ConcealmentManager : IDisposable
{
    private const long ProductionCatchupTicks = 18000;
    private const string ProductivityUpgrade = "Productivity";

    private static readonly MethodInfo OnEntityClosingMethod = typeof(MyEntityComponentUpdater).GetMethod(
        "OnEntityClosing",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        null,
        new[] { typeof(MyEntity) },
        null);

    private readonly Plugin plugin;
    private readonly IPluginLogger log;
    private readonly List<ConcealGroup> intersectGroups = new List<ConcealGroup>();
    private readonly Dictionary<long, Timer> keepAliveTimers = new Dictionary<long, Timer>();
    private readonly Dictionary<long, ProductionCatchupBoost> productionCatchupBoosts =
        new Dictionary<long, ProductionCatchupBoost>();

    private readonly MyDynamicAABBTreeD concealedAabbTree =
        new MyDynamicAABBTreeD(MyConstants.GAME_PRUNING_STRUCTURE_AABB_EXTENSION);

    private Action<MyEntity> onEntityClosingAction;
    private ulong counter;
    private bool initialized;
    private bool ready;
    private bool configChanged;
    private long readyTick;
    private bool disposed;

    public List<ConcealGroup> ConcealedGroups { get; } = new List<ConcealGroup>();
    public DynamicConcealmentManager Dynamic { get; }

    private PluginConfig Config => plugin.ConfigData;

    public ConcealmentManager(Plugin plugin, HarmonyLib.Harmony harmony)
    {
        this.plugin = plugin;
        log = plugin.Log;
        Dynamic = new DynamicConcealmentManager(plugin, harmony);
        Config.PropertyChanged += ConfigOnPropertyChanged;
    }

    public void Update()
    {
        if (MyAPIGateway.Session == null)
            return;

        InitializeSessionFeatures();
        Dynamic.Update();
        UpdateProductionCatchups();

        if (initialized && !ready && plugin.Tick >= readyTick)
            ready = true;

        if (!Config.Enabled || !ready)
            return;

        if (counter % (ulong)Math.Max(1, Config.ConcealInterval) == 0)
            ConcealGrids(Config.ConcealDistance);

        if (counter % (ulong)Math.Max(1, Config.RevealInterval) == 0)
            RevealGrids(Config.RevealDistance);

        counter++;
    }

    public int ConcealGrids(double distanceFromPlayers = 0)
    {
        log.Debug("Concealing grids");

        var concealed = 0;
        var playerSpheres = GetPlayerViewSpheres(distanceFromPlayers);
        var groups = new ConcurrentBag<ConcealGroup>();
        var stopwatch = Stopwatch.StartNew();

        System.Threading.Tasks.Parallel.ForEach(MyCubeGridGroups.Static.Physical.Groups, group =>
        {
            var concealGroup = new ConcealGroup(group);

            if (distanceFromPlayers != 0)
            {
                var volume = GetGroupWorldAabb(group);
                if (playerSpheres.Any(s => s.Contains(volume) != ContainmentType.Disjoint))
                    return;
            }

            if (!IsExcluded(concealGroup))
                groups.Add(concealGroup);
        });

        log.Debug("Scanned grids in {0}ms.", stopwatch.ElapsedMilliseconds);
        stopwatch.Restart();

        var removeUpdatingComponents = GetRemoveUpdatingComponentsAction(MySession.Static);

        foreach (var group in groups)
            concealed += ConcealGroup(group, removeUpdatingComponents);

        log.Debug("Concealed grids in {0}ms.", stopwatch.ElapsedMilliseconds);

        var concealedCount = ConcealedGroups.SelectMany(x => x.Grids).Count();
        var totalCount = MyEntities.GetEntities().Count(x => x is MyCubeGrid);

        if (concealed > 0 && totalCount > 0)
            log.Info("{0}/{1} grids are concealed ({2:P}), {3} new.",
                concealedCount + concealed,
                totalCount,
                (concealedCount + concealed) / (float)totalCount,
                concealed);

        return concealed;
    }

    public int RevealGrids(double distanceFromPlayers)
    {
        var componentUpdater = MySession.Static?.GetComponent<MyEntityComponentUpdater>();
        var revealed = 0;

        foreach (var sphere in GetPlayerViewSpheres(distanceFromPlayers))
            revealed += RevealGridsInSphere(sphere, componentUpdater);

        if (configChanged)
        {
            for (var i = ConcealedGroups.Count - 1; i >= 0; i--)
                if (IsExcluded(ConcealedGroups[i]))
                    revealed += RevealGroup(ConcealedGroups[i], componentUpdater);

            configChanged = false;
        }

        if (revealed != 0)
            log.Info("Revealed {0} grids near players.", revealed);

        return revealed;
    }

    public int RevealGridsInSphere(BoundingSphereD sphere)
    {
        return RevealGridsInSphere(sphere, MySession.Static?.GetComponent<MyEntityComponentUpdater>());
    }

    public int RevealAll()
    {
        log.Debug("Revealing all grids");

        var componentUpdater = MySession.Static?.GetComponent<MyEntityComponentUpdater>();
        var revealed = 0;

        for (var i = ConcealedGroups.Count - 1; i >= 0; i--)
            revealed += RevealGroup(ConcealedGroups[i], componentUpdater);

        return revealed;
    }

    public bool IsExcluded(ConcealGroup group)
    {
        var pirateId = MyPirateAntennas.GetPiratesId();
        var keepActiveProductionRevealed = Config.ProductionConcealment == ProductionConcealmentMode.Off;

        foreach (var grid in group.Grids)
        {
            lock (keepAliveTimers)
            {
                if (keepAliveTimers.ContainsKey(grid.EntityId))
                {
                    log.Debug("{0} is kept alive by remote-control action", group.GridNames);
                    return true;
                }
            }

            if (!Config.ConcealPirates && grid.BigOwners.Contains(pirateId))
            {
                log.Debug("{0} is kept alive by pirate ownership", group.GridNames);
                return true;
            }
        }

        var excludedSubtypes = Config.ExcludedSubtypes;
        var exclude = false;

        System.Threading.Tasks.Parallel.ForEach(group.Grids, (grid, loopState) =>
        {
            foreach (var block in grid.GetFatBlocks())
            {
                if (block is MyRefinery refinery &&
                    keepActiveProductionRevealed &&
                    !refinery.InputInventory.Empty() &&
                    refinery.IsFunctional &&
                    refinery.Enabled)
                {
                    log.Debug("{0} exempted refinery ({1} active)", group.GridNames, refinery.CustomName);
                    exclude = true;
                    loopState.Stop();
                    return;
                }

                if (block is MyProductionBlock production && keepActiveProductionRevealed && production.IsProducing)
                {
                    log.Debug("{0} exempted production ({1} active)", group.GridNames, production.CustomName);
                    exclude = true;
                    loopState.Stop();
                    return;
                }

                var subtype = block.BlockDefinition.Id.SubtypeName;
                if (excludedSubtypes != null && excludedSubtypes.Contains(subtype))
                {
                    log.Debug("{0} exempted subtype {1}", group.GridNames, subtype);
                    exclude = true;
                    loopState.Stop();
                    return;
                }
            }
        });

        return exclude;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        Config.PropertyChanged -= ConfigOnPropertyChanged;
        Dynamic.Dispose();

        if (MyMultiplayer.Static != null)
            MyMultiplayer.Static.ClientJoined -= RevealCryoPod;

        foreach (var timer in keepAliveTimers.Values)
            timer.Dispose();

        foreach (var boost in productionCatchupBoosts.Values.ToArray())
            RemoveProductionCatchupBoost(boost);

        keepAliveTimers.Clear();
        productionCatchupBoosts.Clear();
    }

    private void InitializeSessionFeatures()
    {
        if (initialized || MyAPIGateway.TerminalControls == null)
            return;

        readyTick = plugin.Tick + 60 * 30;

        if (Config.RemoteControlKeepAliveAction)
        {
            var keepAliveAction = MyAPIGateway.TerminalControls.CreateAction<IMyRemoteControl>("Concealment.KeepAlive");
            keepAliveAction.Name = new StringBuilder("Concealment keep alive");
            keepAliveAction.Action = KeepAlive;
            MyAPIGateway.TerminalControls.AddAction<IMyRemoteControl>(keepAliveAction);
        }

        if (MyMultiplayer.Static != null)
            MyMultiplayer.Static.ClientJoined += RevealCryoPod;

        initialized = true;
        Dynamic.Refresh();
    }

    private void ConfigOnPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        configChanged = true;

        if (e.PropertyName == nameof(PluginConfig.DynamicConcealment) ||
            e.PropertyName == nameof(PluginConfig.DynamicConcealQueryInterval) ||
            e.PropertyName == nameof(PluginConfig.DynamicConcealScanInterval))
        {
            Dynamic.Refresh();
        }
    }

    private int ConcealGroup(ConcealGroup group, Action<MyEntity> removeUpdatingComponents)
    {
        if (ConcealedGroups.Any(g => g.Id == group.Id))
            return 0;

        log.Debug("Concealing grids: {0}", group.GridNames);

        RemoveProductionCatchups(group);
        CacheProductionCatchupBlocks(group);
        group.ConcealedAtTick = plugin.Tick;

        group.Conceal(removeUpdatingComponents);
        group.UpdateAabb();
        var aabb = group.WorldAabb;
        group.ProxyId = concealedAabbTree.AddProxy(ref aabb, group, 0);
        group.Closing += GroupOnClosing;
        group.UpdatePostConceal();
        ConcealedGroups.Add(group);

        return group.Grids.Count;
    }

    private void GroupOnClosing(ConcealGroup group)
    {
        RevealGroup(group);
    }

    private int RevealGroup(ConcealGroup group)
    {
        return RevealGroup(group, MySession.Static?.GetComponent<MyEntityComponentUpdater>());
    }

    private int RevealGroup(ConcealGroup group, MyEntityComponentUpdater componentUpdater)
    {
        if (!group.IsConcealed)
        {
            log.Warning("Attempted to reveal a group that was not concealed: {0}", group.GridNames);
            return 0;
        }

        var count = group.Grids.Count;
        log.Debug("Revealing grids: {0}", group.GridNames);

        group.Reveal(componentUpdater);
        ApplyProductionCatchup(group);
        ConcealedGroups.Remove(group);

        if (group.ProxyId >= 0)
            concealedAabbTree.RemoveProxy(group.ProxyId);

        group.UpdatePostReveal();
        return count;
    }

    private int RevealGridsInSphere(BoundingSphereD sphere, MyEntityComponentUpdater componentUpdater)
    {
        var revealed = 0;
        concealedAabbTree.OverlapAllBoundingSphere(ref sphere, intersectGroups);

        foreach (var group in intersectGroups.ToArray())
            revealed += RevealGroup(group, componentUpdater);

        intersectGroups.Clear();
        return revealed;
    }

    private void RevealCryoPod(ulong steamId, string username)
    {
        try
        {
            var componentUpdater = MySession.Static?.GetComponent<MyEntityComponentUpdater>();

            for (var i = ConcealedGroups.Count - 1; i >= 0; i--)
            {
                var group = ConcealedGroups[i];
                if (group.IsCryoOccupied(steamId))
                {
                    RevealGroup(group, componentUpdater);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Reveal cryopod failed");
        }
    }

    private void KeepAlive(IMyTerminalBlock block)
    {
        if (block?.CubeGrid == null || block.CubeGrid.IsStatic)
            return;

        log.Debug("Keepalive triggered on grid {0}", block.CubeGrid.DisplayName);
        var dueTime = TimeSpan.FromSeconds(Math.Max(1, Config.ConcealInterval) / 60d);

        lock (keepAliveTimers)
        {
            if (keepAliveTimers.TryGetValue(block.CubeGrid.EntityId, out var timer))
            {
                timer.Change(dueTime, Timeout.InfiniteTimeSpan);
            }
            else
            {
                keepAliveTimers.Add(
                    block.CubeGrid.EntityId,
                    new Timer(KeepAliveCallback, block.CubeGrid.EntityId, dueTime, Timeout.InfiniteTimeSpan));
            }
        }
    }

    private void KeepAliveCallback(object state)
    {
        var gridId = (long)state;
        log.Debug("Keepalive expired on grid {0}", gridId);

        lock (keepAliveTimers)
        {
            if (!keepAliveTimers.TryGetValue(gridId, out var timer))
                return;

            timer.Dispose();
            keepAliveTimers.Remove(gridId);
        }
    }

    private List<BoundingSphereD> GetPlayerViewSpheres(double distance)
    {
        var players = new List<IMyPlayer>();
        MyAPIGateway.Players.GetPlayers(players, p => p != null && !p.IsBot);
        return players.Select(p => new BoundingSphereD(p.GetPosition(), distance)).ToList();
    }

    private void CacheProductionCatchupBlocks(ConcealGroup group)
    {
        group.ProductionCatchupBlockIds.Clear();

        if (Config.ProductionConcealment != ProductionConcealmentMode.Approximate)
            return;

        foreach (var block in group.Grids.SelectMany(grid => grid.GetFatBlocks()).OfType<MyProductionBlock>())
            if (IsProductionActiveForCatchup(block))
                group.ProductionCatchupBlockIds.Add(block.EntityId);
    }

    private static bool IsProductionActiveForCatchup(MyProductionBlock block)
    {
        if (block == null || !block.IsFunctional || !block.Enabled)
            return false;

        if (block.IsProducing)
            return true;

        return block is MyRefinery refinery && !refinery.InputInventory.Empty();
    }

    private void ApplyProductionCatchup(ConcealGroup group)
    {
        if (Config.ProductionConcealment != ProductionConcealmentMode.Approximate ||
            group.ProductionCatchupBlockIds.Count == 0)
        {
            return;
        }

        var offlineTicks = Math.Max(0, plugin.Tick - group.ConcealedAtTick);
        if (offlineTicks == 0)
            return;

        var boost = (float)offlineTicks / ProductionCatchupTicks;
        var endTick = plugin.Tick + ProductionCatchupTicks;

        foreach (var blockId in group.ProductionCatchupBlockIds)
        {
            if (!MyEntities.TryGetEntityById(blockId, out MyEntity entity) || !(entity is MyProductionBlock production))
                continue;

            if (productionCatchupBoosts.TryGetValue(blockId, out var existing))
                RemoveProductionCatchupBoost(existing);

            AddProductivityBoost(production, boost);
            productionCatchupBoosts[blockId] = new ProductionCatchupBoost(blockId, boost, endTick);
        }

        log.Info("Applied {0:P0} production catch-up boost for {1} ticks to {2} blocks in {3}.",
            boost,
            ProductionCatchupTicks,
            group.ProductionCatchupBlockIds.Count,
            group.GridNames);
    }

    private void RemoveProductionCatchups(ConcealGroup group)
    {
        foreach (var block in group.Grids.SelectMany(grid => grid.GetFatBlocks()).OfType<MyProductionBlock>())
        {
            if (!productionCatchupBoosts.TryGetValue(block.EntityId, out var boost))
                continue;

            RemoveProductionCatchupBoost(boost);
            productionCatchupBoosts.Remove(block.EntityId);
        }
    }

    private void UpdateProductionCatchups()
    {
        if (productionCatchupBoosts.Count == 0)
            return;

        foreach (var boost in productionCatchupBoosts.Values.ToArray())
        {
            if (plugin.Tick < boost.EndTick)
                continue;

            RemoveProductionCatchupBoost(boost);
            productionCatchupBoosts.Remove(boost.EntityId);
        }
    }

    private static void AddProductivityBoost(MyProductionBlock block, float boost)
    {
        if (!block.UpgradeValues.ContainsKey(ProductivityUpgrade))
            block.UpgradeValues[ProductivityUpgrade] = 0f;

        block.UpgradeValues[ProductivityUpgrade] += boost;
    }

    private static void RemoveProductionCatchupBoost(ProductionCatchupBoost boost)
    {
        if (!MyEntities.TryGetEntityById(boost.EntityId, out MyEntity entity) || !(entity is MyProductionBlock block))
            return;

        if (!block.UpgradeValues.ContainsKey(ProductivityUpgrade))
            return;

        var value = block.UpgradeValues[ProductivityUpgrade] - boost.Boost;
        block.UpgradeValues[ProductivityUpgrade] = Math.Abs(value) < 0.0001f ? 0f : value;
    }

    private static BoundingBoxD GetGroupWorldAabb(MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group group)
    {
        var first = true;
        var box = default(BoundingBoxD);

        foreach (var node in group.Nodes)
        {
            var gridBox = node.NodeData.PositionComp.WorldAABB;
            if (first)
            {
                box = gridBox;
                first = false;
            }
            else
            {
                box.Include(gridBox);
            }
        }

        return box;
    }

    private Action<MyEntity> GetRemoveUpdatingComponentsAction(MySession session)
    {
        if (session == null || OnEntityClosingMethod == null)
        {
            onEntityClosingAction = null;
            return null;
        }

        var componentUpdater = session.GetComponent<MyEntityComponentUpdater>();

        if (componentUpdater != null && (onEntityClosingAction == null || onEntityClosingAction.Target != componentUpdater))
            onEntityClosingAction = (Action<MyEntity>)OnEntityClosingMethod.CreateDelegate(typeof(Action<MyEntity>), componentUpdater);

        return onEntityClosingAction;
    }

    private sealed class ProductionCatchupBoost
    {
        public ProductionCatchupBoost(long entityId, float boost, long endTick)
        {
            EntityId = entityId;
            Boost = boost;
            EndTick = endTick;
        }

        public long EntityId { get; }
        public float Boost { get; }
        public long EndTick { get; }
    }
}
