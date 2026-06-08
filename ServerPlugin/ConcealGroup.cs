using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using VRage.Game.Components.Session;
using VRage.Game.Entity;
using VRage.Game.Entity.EntityComponents.Interfaces;
using VRage.Groups;
using VRage.ModAPI;
using VRageMath;

namespace ServerPlugin;

public sealed class ConcealGroup
{
    private static readonly FieldInfo CurrentPlayerIdField = typeof(MyCryoChamber).GetField(
        "m_currentPlayerId",
        BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly HashSet<long> projectors = new HashSet<long>();

    public long Id { get; }
    public bool IsConcealed { get; private set; }
    public BoundingBoxD WorldAabb { get; private set; }
    public List<MyCubeGrid> Grids { get; }
    public List<MyMedicalRoom> MedicalRooms { get; } = new List<MyMedicalRoom>();
    public List<MyCryoChamber> CryoChambers { get; } = new List<MyCryoChamber>();
    public HashSet<long> ProductionCatchupGridIds { get; } = new HashSet<long>();
    public event Action<ConcealGroup> Closing;
    internal int ProxyId = -1;
    internal long ConcealedAtTick;

    public ConcealGroup(MyGroups<MyCubeGrid, MyGridPhysicalGroupData>.Group group)
    {
        Grids = group.Nodes.Select(n => n.NodeData).ToList();
        Id = Grids.First().EntityId;
    }

    public string GridNames => string.Join(", ", Grids.Select(g => g.DisplayName));

    public void UpdatePostConceal()
    {
        IsConcealed = true;
        UpdateAabb();
        CacheSpawns();
        HookOnClosing();
    }

    public void UpdatePostReveal()
    {
        IsConcealed = false;
        UnhookOnClosing();
    }

    public void UpdateAabb()
    {
        var startPos = Grids.First().PositionComp.GetPosition();
        var box = new BoundingBoxD(startPos, startPos);

        foreach (var aabb in Grids.Select(g => g.PositionComp.WorldAABB))
            box.Include(aabb);

        WorldAabb = box;
    }

    public bool IsMedicalRoomAvailable(long identityId)
    {
        foreach (var room in MedicalRooms)
            if (room.HasPlayerAccess(identityId) && room.IsWorking)
                return true;

        return false;
    }

    public bool IsCryoOccupied(ulong steamId)
    {
        if (CurrentPlayerIdField == null)
            return false;

        foreach (var chamber in CryoChambers)
        {
            var value = (MyPlayer.PlayerId?)CurrentPlayerIdField.GetValue(chamber);
            if (value?.SteamId == steamId)
                return true;
        }

        return false;
    }

    public void Conceal(Action<MyEntity> removeUpdatingComponents)
    {
        foreach (var grid in Grids)
        {
            DisableProjectors(grid);

            if (grid.Parent == null)
                UnregisterRecursive(grid);
        }

        void UnregisterRecursive(MyEntity entity)
        {
            if (entity.IsPreview)
                return;

            removeUpdatingComponents?.Invoke(entity);
            MyEntities.UnregisterForUpdate(entity);
            (entity.GameLogic as IMyGameLogicComponent)?.UnregisterForUpdate();
            entity.Flags |= (EntityFlags)4;

            if (entity.Hierarchy == null)
                return;

            foreach (var child in entity.Hierarchy.Children)
                UnregisterRecursive((MyEntity)child.Container.Entity);
        }
    }

    public void Reveal(MyEntityComponentUpdater componentUpdater)
    {
        foreach (var grid in Grids)
            if (grid.Parent == null)
                RegisterRecursive(grid);

        EnableProjectors();

        void RegisterRecursive(MyEntity entity)
        {
            if (entity.IsPreview)
                return;

            componentUpdater?.AddEntityComponents(entity);
            MyEntities.RegisterForUpdate(entity);
            (entity.GameLogic as IMyGameLogicComponent)?.RegisterForUpdate();
            entity.Flags &= ~(EntityFlags)4;

            if (entity.Hierarchy == null)
                return;

            foreach (var child in entity.Hierarchy.Children)
                RegisterRecursive((MyEntity)child.Container.Entity);
        }
    }

    public void EnableProjectors()
    {
        foreach (var projector in projectors.Select(x => MyEntities.GetEntityById(x) as MyProjectorBase))
            if (projector != null)
                projector.Enabled = true;

        projectors.Clear();
    }

    private void HookOnClosing()
    {
        foreach (var grid in Grids)
            grid.OnMarkForClose += GridOnMarkForClose;
    }

    private void UnhookOnClosing()
    {
        foreach (var grid in Grids)
            grid.OnMarkForClose -= GridOnMarkForClose;
    }

    private void GridOnMarkForClose(MyEntity entity)
    {
        EnableProjectors();
        UnhookOnClosing();
        Closing?.Invoke(this);
    }

    private void CacheSpawns()
    {
        MedicalRooms.Clear();
        CryoChambers.Clear();

        foreach (var block in Grids.SelectMany(x => x.GetFatBlocks()))
        {
            if (block is MyMedicalRoom medical)
                MedicalRooms.Add(medical);
            else if (block is MyCryoChamber cryo)
                CryoChambers.Add(cryo);
        }
    }

    private void DisableProjectors(MyCubeGrid grid)
    {
        foreach (var projector in grid.GetFatBlocks<MyProjectorBase>())
        {
            if (projector.ProjectedGrid == null)
                continue;

            projector.Enabled = false;
            projectors.Add(projector.EntityId);
        }
    }
}
