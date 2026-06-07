using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Shared.Config;
using Shared.Logging;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace ServerPlugin;

public sealed class DynamicConcealmentManager : IDisposable
{
    private static readonly MethodInfo PrefixUpdateMethod =
        typeof(DynamicConcealmentManager).GetMethod(nameof(PrefixUpdate), BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly string[] PatchTargets =
    {
        nameof(MyEntity.UpdateBeforeSimulation),
        nameof(MyEntity.UpdateBeforeSimulation10),
        nameof(MyEntity.UpdateBeforeSimulation100),
        nameof(MyEntity.UpdateAfterSimulation),
        nameof(MyEntity.UpdateAfterSimulation10),
        nameof(MyEntity.UpdateAfterSimulation100)
    };

    private static readonly ReaderWriterLockSlim ConfigLock =
        new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

    private static readonly Dictionary<MyObjectBuilderType, TargetInfo> GenericConfig =
        new Dictionary<MyObjectBuilderType, TargetInfo>(MyObjectBuilderType.Comparer);

    private static readonly Dictionary<MyDefinitionId, TargetInfo> SubtypeConfig =
        new Dictionary<MyDefinitionId, TargetInfo>(MyDefinitionId.Comparer);

    private static readonly ConditionalWeakTable<MyCubeBlock, BlockInfo> BlockUpdateInfo =
        new ConditionalWeakTable<MyCubeBlock, BlockInfo>();

    private static readonly ConditionalWeakTable<MyCubeGrid, GridInfo> GridUpdateInfo =
        new ConditionalWeakTable<MyCubeGrid, GridInfo>();

    private static readonly WeakReference<Plugin> PluginRef = new WeakReference<Plugin>(null);

    private static int configVersion;

    private readonly Plugin plugin;
    private readonly Harmony harmony;
    private readonly IPluginLogger log;
    private readonly HashSet<MethodBase> patchedMethods = new HashSet<MethodBase>();
    private long nextPeriodicRefreshTick;
    private bool disposed;

    private PluginConfig Config => plugin.ConfigData;

    private static TimeSpan QueryInterval
    {
        get
        {
            return PluginRef.TryGetTarget(out var plugin)
                ? TimeSpan.FromSeconds(Math.Max(1, plugin.ConfigData.DynamicConcealQueryInterval))
                : TimeSpan.FromSeconds(15);
        }
    }

    private static TimeSpan ScanInterval
    {
        get
        {
            return PluginRef.TryGetTarget(out var plugin)
                ? TimeSpan.FromSeconds(Math.Max(0.1, plugin.ConfigData.DynamicConcealScanInterval))
                : TimeSpan.FromSeconds(2);
        }
    }

    public DynamicConcealmentManager(Plugin plugin, Harmony harmony)
    {
        this.plugin = plugin;
        this.harmony = harmony;
        log = plugin.Log;
        PluginRef.SetTarget(plugin);
    }

    public void Update()
    {
        if (plugin.Tick >= nextPeriodicRefreshTick)
        {
            Refresh();
            nextPeriodicRefreshTick = plugin.Tick + 60 * 30;
        }
    }

    public void Refresh()
    {
        if (disposed)
            return;

        RebuildFromSettings(Config.DynamicConcealment ?? new List<DynamicConcealmentRule>());

        foreach (var type in GenericConfig.Keys.Concat(SubtypeConfig.Keys.Select(x => x.TypeId)).Distinct())
        {
            try
            {
                var entityType = TryGetProducedEntityType(type);
                if (entityType == null)
                {
                    log.Warning("Unable to determine entity type of object builder type {0}", type);
                    continue;
                }

                if (!typeof(MyEntity).IsAssignableFrom(entityType))
                {
                    log.Warning("Type {0}, object builder {1}, is not assignable to MyEntity", entityType, type);
                    continue;
                }

                PatchType(entityType);
            }
            catch (Exception ex)
            {
                log.Error(ex, "Failed to attach dynamic concealment to {0}", type);
            }
        }
    }

    public void Dispose()
    {
        disposed = true;

        foreach (var method in patchedMethods.ToArray())
            harmony.Unpatch(method, HarmonyPatchType.Prefix, harmony.Id);

        patchedMethods.Clear();
        PluginRef.SetTarget(null);
    }

    private void PatchType(Type type)
    {
        while (type != null && typeof(MyEntity).IsAssignableFrom(type))
        {
            var patched = 0;
            foreach (var name in PatchTargets)
            {
                var target = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (target == null || !patchedMethods.Add(target))
                    continue;

                harmony.Patch(target, prefix: new HarmonyMethod(PrefixUpdateMethod));
                patched++;
            }

            if (patched > 0)
                log.Debug("Attached dynamic concealment to {0} update methods on {1}", patched, type);

            type = type.BaseType;
        }
    }

    private void RebuildFromSettings(ICollection<DynamicConcealmentRule> rules)
    {
        ConfigLock.EnterWriteLock();
        try
        {
            GenericConfig.Clear();
            SubtypeConfig.Clear();

            foreach (var rule in rules)
                MergeRule(rule, genericOnly: true);

            foreach (var rule in rules)
                MergeRule(rule, genericOnly: false);

            foreach (var pair in SubtypeConfig)
            {
                if (!GenericConfig.TryGetValue(pair.Key.TypeId, out var baseConfig))
                    continue;

                foreach (var inherited in baseConfig.Config)
                {
                    if (pair.Value.Config.ContainsKey(inherited.Key))
                        continue;

                    pair.Value.Config.Add(inherited.Key, inherited.Value);
                    pair.Value.MaxDistance = Math.Max(pair.Value.MaxDistance, Math.Sqrt(inherited.Value));
                }
            }

            configVersion++;
        }
        finally
        {
            ConfigLock.ExitWriteLock();
        }

        void MergeRule(DynamicConcealmentRule rule, bool genericOnly)
        {
            if (rule.Distance <= 0 || !TryParseObjectBuilderType(rule.TargetType, out var type))
            {
                log.Warning("Ignoring dynamic rule {0}/{1} {2} {3}", rule.TargetType, rule.TargetSubtype, rule.ConcealType, rule.Distance);
                return;
            }

            var hasSubtype = !string.IsNullOrWhiteSpace(rule.TargetSubtype);
            if (genericOnly == hasSubtype)
                return;

            if (!hasSubtype)
            {
                if (!GenericConfig.TryGetValue(type, out var target))
                    target = GenericConfig[type] = new TargetInfo();

                target.Merge(rule);
                log.Debug("Registered dynamic rule {0}/{1} {2} {3}", rule.TargetType, rule.TargetSubtype, rule.ConcealType, rule.Distance);
                return;
            }

            var id = new MyDefinitionId(type, rule.TargetSubtype);
            if (!SubtypeConfig.TryGetValue(id, out var subtypeTarget))
                subtypeTarget = SubtypeConfig[id] = new TargetInfo();

            subtypeTarget.Merge(rule);
            log.Debug("Registered dynamic rule {0}/{1} {2} {3}", rule.TargetType, rule.TargetSubtype, rule.ConcealType, rule.Distance);
        }
    }

    private static TargetInfo QueryConfig(MyDefinitionId id)
    {
        if (SubtypeConfig.TryGetValue(id, out var result))
            return result;

        var type = id.TypeId;
        while (!type.IsNull)
        {
            if (GenericConfig.TryGetValue(type, out result))
                return result;

            type = ((Type)type).BaseType;
        }

        return null;
    }

    private static BlockInfo GetConcealInfo(MyCubeBlock key)
    {
        var data = BlockUpdateInfo.GetValue(key, block => new BlockInfo(block));

        if (data.ConfigVersion == configVersion)
            return data;

        ConfigLock.EnterReadLock();
        try
        {
            data.ConcealState = true;
            data.ConfigVersion = configVersion;
            data.Config = QueryConfig(key.BlockDefinition.Id);
            data.LastConcealStateUpdate = DateTime.MinValue;
        }
        finally
        {
            ConfigLock.ExitReadLock();
        }

        return data;
    }

    private static GridInfo GetConcealInfo(MyCubeGrid key)
    {
        var data = GridUpdateInfo.GetValue(key, grid => new GridInfo(grid));

        if (data.ConfigVersion == configVersion)
            return data;

        ConfigLock.EnterReadLock();
        try
        {
            data.ConfigVersion = configVersion;
            data.NextQueryDistance = 0;
            lock (data.Sync)
                data.NearbyEntities.Clear();
            data.LastNearbyUpdate = DateTime.MinValue;
        }
        finally
        {
            ConfigLock.ExitReadLock();
        }

        return data;
    }

    private static bool PrefixUpdate(MyEntity __instance)
    {
        if (!(__instance is MyCubeBlock block))
            return true;

        if (block is MyLargeTurretBase turret && turret.IsControlledByLocalPlayer)
            return true;

        var info = GetConcealInfo(block);
        if (info.Config == null || info.Config.Config.Count == 0)
            return true;

        if (info.LastConcealStateUpdate + ScanInterval < DateTime.Now)
            info.ScheduleRefresh();

        return !info.ConcealState;
    }

    private static MyRelationsBetweenPlayerAndBlock GetRelationTolerant(long id1, long id2)
    {
        if (id1 == id2)
            return MyRelationsBetweenPlayerAndBlock.Owner;

        if (id1 == 0 || id2 == 0)
            return MyRelationsBetweenPlayerAndBlock.NoOwnership;

        var faction1 = MySession.Static.Factions.TryGetPlayerFaction(id1);
        var faction2 = MySession.Static.Factions.TryGetPlayerFaction(id2);

        if (faction1 == null || faction2 == null)
            return MyRelationsBetweenPlayerAndBlock.Enemies;

        if (faction1 == faction2)
            return MyRelationsBetweenPlayerAndBlock.FactionShare;

        return MySession.Static.Factions.GetRelationBetweenFactions(faction1.FactionId, faction2.FactionId).Item1 ==
               MyRelationsBetweenFactions.Neutral
            ? MyRelationsBetweenPlayerAndBlock.Neutral
            : MyRelationsBetweenPlayerAndBlock.Enemies;
    }

    private static DynamicConcealType GetConcealType(MyRelationsBetweenPlayerAndBlock relation, bool grid)
    {
        switch (relation)
        {
            case MyRelationsBetweenPlayerAndBlock.Owner:
            case MyRelationsBetweenPlayerAndBlock.FactionShare:
                return grid ? DynamicConcealType.None : DynamicConcealType.FriendlyCharacters;

            case MyRelationsBetweenPlayerAndBlock.Enemies:
                return grid ? DynamicConcealType.HostileGrids : DynamicConcealType.HostileCharacters;

            case MyRelationsBetweenPlayerAndBlock.Neutral:
            case MyRelationsBetweenPlayerAndBlock.NoOwnership:
            default:
                return grid ? DynamicConcealType.NeutralGrids : DynamicConcealType.NeutralCharacters;
        }
    }

    private static bool TryParseObjectBuilderType(string value, out MyObjectBuilderType type)
    {
        type = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        return MyObjectBuilderType.TryParse(trimmed, out type) ||
               MyObjectBuilderType.TryParse("MyObjectBuilder_" + trimmed, out type);
    }

    private static Type TryGetProducedEntityType(MyObjectBuilderType objectBuilderType)
    {
        var factoryType = AccessTools.TypeByName("Sandbox.Game.Entities.Cube.MyCubeBlockFactory") ??
                          Type.GetType("Sandbox.Game.Entities.Cube.MyCubeBlockFactory, Sandbox.Game");
        var field = factoryType == null ? null : AccessTools.Field(factoryType, "m_objectFactory");
        var factory = field?.GetValue(null);
        var method = factory == null ? null : AccessTools.Method(factory.GetType(), "TryGetProducedType", new[] { typeof(MyObjectBuilderType) });
        return method?.Invoke(factory, new object[] { objectBuilderType }) as Type;
    }

    private sealed class TargetInfo
    {
        public Dictionary<DynamicConcealType, double> Config { get; } = new Dictionary<DynamicConcealType, double>();
        public double MaxDistance;

        public void Merge(DynamicConcealmentRule rule)
        {
            var distanceSquared = rule.Distance * rule.Distance;
            if (!Config.TryGetValue(rule.ConcealType, out var current) || current < distanceSquared)
                Config[rule.ConcealType] = distanceSquared;

            MaxDistance = Math.Max(MaxDistance, rule.Distance);
        }
    }

    private sealed class GridInfo
    {
        private readonly WeakReference<MyCubeGrid> gridRef;
        private int scheduled;

        public GridInfo(MyCubeGrid grid)
        {
            gridRef = new WeakReference<MyCubeGrid>(grid);
        }

        public object Sync { get; } = new object();
        public int ConfigVersion = -1;
        public double NextQueryDistance;
        public readonly List<WeakReference<MyEntity>> NearbyEntities = new List<WeakReference<MyEntity>>();
        public DateTime LastNearbyUpdate;

        public void ScheduleRefresh()
        {
            if (Interlocked.Exchange(ref scheduled, 1) == 1)
                return;

            System.Threading.Tasks.Task.Run(DoWork);
        }

        private void DoWork()
        {
            try
            {
                if (!gridRef.TryGetTarget(out var grid) || grid.MarkedForClose)
                    return;

                var aabb = grid.PositionComp.WorldAABB.Inflate(NextQueryDistance);
                var list = new List<MyEntity>();
                MyGamePruningStructure.GetTopMostEntitiesInBox(ref aabb, list, MyEntityQueryType.Static);

                var seconds = Math.Max(0, (DateTime.Now - LastNearbyUpdate).TotalSeconds);
                aabb = aabb.Inflate(Math.Min(seconds * 30, NextQueryDistance));
                MyGamePruningStructure.GetTopMostEntitiesInBox(ref aabb, list, MyEntityQueryType.Dynamic);

                lock (Sync)
                {
                    NearbyEntities.Clear();
                    foreach (var entity in list)
                        NearbyEntities.Add(new WeakReference<MyEntity>(entity));
                }

                LastNearbyUpdate = DateTime.Now;
            }
            finally
            {
                Interlocked.Exchange(ref scheduled, 0);
            }
        }
    }

    private sealed class BlockInfo
    {
        private readonly WeakReference<MyCubeBlock> blockRef;
        private int scheduled;

        public BlockInfo(MyCubeBlock block)
        {
            blockRef = new WeakReference<MyCubeBlock>(block);
        }

        public int ConfigVersion = -1;
        public bool ConcealState = true;
        public TargetInfo Config;
        public DateTime LastConcealStateUpdate;

        public void ScheduleRefresh()
        {
            if (Interlocked.Exchange(ref scheduled, 1) == 1)
                return;

            System.Threading.Tasks.Task.Run(DoWork);
        }

        private void DoWork()
        {
            try
            {
                if (!blockRef.TryGetTarget(out var block) || block.MarkedForClose || block.CubeGrid == null)
                    return;

                var gridInfo = GetConcealInfo(block.CubeGrid);
                gridInfo.NextQueryDistance = Math.Max(gridInfo.NextQueryDistance, Config.MaxDistance);

                if (gridInfo.LastNearbyUpdate + QueryInterval < DateTime.Now)
                    gridInfo.ScheduleRefresh();

                var nearestByType = new double[(int)DynamicConcealType.None];
                for (var i = 0; i < nearestByType.Length; i++)
                    nearestByType[i] = double.MaxValue;

                lock (gridInfo.Sync)
                {
                    foreach (var weakEntity in gridInfo.NearbyEntities)
                    {
                        if (!weakEntity.TryGetTarget(out var entity) || entity.MarkedForClose)
                            continue;

                        var type = DynamicConcealType.None;

                        if (entity is MyCubeGrid grid && grid != block.CubeGrid)
                        {
                            var worstRelation = 0;
                            foreach (var otherOwner in grid.SmallOwners)
                            {
                                var relation = (int)GetRelationTolerant(block.OwnerId, otherOwner);
                                if (relation > worstRelation)
                                    worstRelation = relation;
                            }

                            type = GetConcealType((MyRelationsBetweenPlayerAndBlock)worstRelation, grid: true);
                        }
                        else if (entity is MyCharacter character)
                        {
                            type = GetConcealType(
                                GetRelationTolerant(block.OwnerId, character.GetPlayerIdentityId()),
                                grid: false);
                        }

                        if (type == DynamicConcealType.None)
                            continue;

                        var distanceSquared = Vector3D.DistanceSquared(
                            entity.PositionComp.WorldVolume.Center,
                            block.PositionComp.WorldVolume.Center);

                        if (distanceSquared < nearestByType[(int)type])
                            nearestByType[(int)type] = distanceSquared;
                    }
                }

                var concealed = true;
                for (var i = 0; i < nearestByType.Length; i++)
                {
                    var type = (DynamicConcealType)i;
                    if (!Config.Config.TryGetValue(type, out var minDistance))
                        minDistance = 0;

                    if (nearestByType[i] >= minDistance)
                        continue;

                    concealed = false;
                    break;
                }

                ConcealState = concealed;
                LastConcealStateUpdate = DateTime.Now;
            }
            finally
            {
                Interlocked.Exchange(ref scheduled, 0);
            }
        }
    }
}
