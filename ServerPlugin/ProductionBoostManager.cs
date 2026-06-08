using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Shared.Config;
using Shared.Logging;
using VRage.FileSystem;
using VRage.Game.Entity;

namespace ServerPlugin;

// Approximate production catch-up, tracked per grid. While a grid is concealed its
// assemblers and refineries do not run, so on reveal we hand the grid a bank of
// "boost time". While that bank lasts every production block on the grid processes
// bigger steps each update (boost level times the normal amount of material) and
// draws boost level times the power, catching up the work missed while hidden
// without changing update frequency or touching upgrade module values.
//
// The banks are a persistent per-grid database stored as JSONL (one grid per row).
// It is written at most once every five minutes and on shutdown. A grid that cannot
// supply the boosted power falls back to normal speed and does not spend its bank
// until power is available again; that power check runs at most once every 30 seconds
// per grid and is staggered across grids so the checks do not all land on one tick.
public sealed class ProductionBoostManager : IDisposable
{
    // 30 seconds at 60 simulation frames per second.
    private const long CheckIntervalFrames = 30 * 60;

    // 5 minutes at 60 simulation frames per second.
    private const long SaveIntervalFrames = 5 * 60 * 60;

    private const string BanksFileName = "ConcealmentBanks.jsonl";

    private static readonly WeakReference<Plugin> PluginRef = new WeakReference<Plugin>(null);

    // Banked online boost time per grid, keyed by grid EntityId, measured in
    // simulation frames (60 per second, same clock as Plugin.Tick).
    private static readonly ConcurrentDictionary<long, double> BoostBankFrames =
        new ConcurrentDictionary<long, double>();

    // Latest power-gate decision per grid: true while the grid can supply the boosted
    // draw. Absent or false means produce at normal speed and do not spend the bank.
    private static readonly ConcurrentDictionary<long, bool> CanBoostGrid =
        new ConcurrentDictionary<long, bool>();

    // Last tick the grid's bank was drained, so consumption tracks real elapsed time
    // regardless of how many blocks the grid has or when each one updates.
    private static readonly ConcurrentDictionary<long, long> LastConsumeTick =
        new ConcurrentDictionary<long, long>();

    // Set whenever a bank changes so the periodic flush knows there is work to persist.
    private static volatile bool dirty;

    private static readonly MethodInfo PrefixUpdateProductionMethod =
        typeof(ProductionBoostManager).GetMethod(nameof(PrefixUpdateProduction),
            BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly MethodInfo PostfixUpdateProductionMethod =
        typeof(ProductionBoostManager).GetMethod(nameof(PostfixUpdateProduction),
            BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly MethodInfo PostfixPowerMethod =
        typeof(ProductionBoostManager).GetMethod(nameof(PostfixPowerConsumption),
            BindingFlags.Static | BindingFlags.NonPublic);

    private readonly Harmony harmony;
    private readonly IPluginLogger log;
    private readonly string banksPath;
    private readonly object saveLock = new object();
    private readonly HashSet<MethodBase> prefixed = new HashSet<MethodBase>();
    private readonly HashSet<MethodBase> postfixed = new HashSet<MethodBase>();
    private long lastSaveTick;
    private bool disposed;

    public ProductionBoostManager(Plugin plugin, Harmony harmony)
    {
        this.harmony = harmony;
        log = plugin.Log;
        PluginRef.SetTarget(plugin);
        banksPath = Path.Combine(MyFileSystem.UserDataPath, BanksFileName);

        Load();

        PatchBlock(typeof(MyRefinery));
        PatchBlock(typeof(MyAssembler));
    }

    // Grant catch-up boost to a grid for the time it spent concealed. concealedFrames
    // divided by the boost level is how long the boost runs online; unused time stays
    // banked, clamped to the configured maximum.
    public void GrantBoost(MyCubeGrid grid, long concealedFrames)
    {
        if (disposed || grid == null || concealedFrames <= 0)
            return;

        var boostLevel = BoostLevel;
        var maxFrames = MaxBoostFrames;
        if (boostLevel <= 1.0 || maxFrames <= 0)
            return;

        var granted = concealedFrames / boostLevel;
        var gridId = grid.EntityId;

        BoostBankFrames.AddOrUpdate(
            gridId,
            Math.Min(maxFrames, granted),
            (_, existing) => Math.Min(maxFrames, existing + granted));

        // Start draining from now and seed an initial power decision so the grid can
        // begin boosting immediately rather than waiting for the first staggered check.
        LastConsumeTick[gridId] = CurrentTick;
        EvaluateGate(grid);
        dirty = true;
    }

    // Run the staggered per-grid power checks and the throttled disk flush. Called once
    // per simulation tick from the concealment manager.
    public void Update(long tick)
    {
        if (disposed)
            return;

        if (Enabled && !BoostBankFrames.IsEmpty)
        {
            foreach (var entry in BoostBankFrames)
            {
                if (entry.Value <= 0.0)
                    continue;

                // Stagger: each grid is offset by a stable phase derived from its id, so
                // the 30 second checks spread out instead of bunching on the same tick.
                var offset = (int)(((entry.Key % CheckIntervalFrames) + CheckIntervalFrames) % CheckIntervalFrames);
                if ((tick + offset) % CheckIntervalFrames != 0)
                    continue;

                if (MyEntities.TryGetEntityById(entry.Key, out MyEntity entity) && entity is MyCubeGrid grid)
                    EvaluateGate(grid);
                else
                    CanBoostGrid[entry.Key] = false;
            }
        }

        // Persist no more often than once every five minutes, and only when a bank has
        // actually changed since the last write.
        if (dirty && tick - lastSaveTick >= SaveIntervalFrames)
        {
            Save();
            lastSaveTick = tick;
            dirty = false;
        }
    }

    // Flush the bank database to disk immediately, regardless of the five minute
    // throttle. Hooked to the world save so the database stays consistent with the
    // saved world, including the autosave performed before a server stop or restart.
    public void SaveNow()
    {
        if (disposed)
            return;

        Save();
        dirty = false;
        lastSaveTick = CurrentTick;
    }

    public void Dispose()
    {
        disposed = true;

        foreach (var method in prefixed)
            harmony.Unpatch(method, PrefixUpdateProductionMethod);

        foreach (var method in postfixed)
            harmony.Unpatch(method, PostfixUpdateProductionMethod);

        foreach (var method in postfixed)
            harmony.Unpatch(method, PostfixPowerMethod);

        prefixed.Clear();
        postfixed.Clear();

        // Best-effort flush only. The durable save points are the world save hook and the
        // periodic throttle; Dispose cannot be relied on, because the host may kill the
        // process after saving the world and a crash skips finalization entirely.
        if (dirty)
            Save();

        BoostBankFrames.Clear();
        CanBoostGrid.Clear();
        LastConsumeTick.Clear();
        PluginRef.SetTarget(null);
    }

    private void PatchBlock(Type blockType)
    {
        var updateProduction = blockType.GetMethod("UpdateProduction",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (updateProduction != null && prefixed.Add(updateProduction))
        {
            harmony.Patch(updateProduction,
                prefix: new HarmonyMethod(PrefixUpdateProductionMethod),
                postfix: new HarmonyMethod(PostfixUpdateProductionMethod));
        }
        else
        {
            log.Warning("ProductionBoost: UpdateProduction not found on {0}", blockType);
        }

        var power = blockType.GetMethod("GetOperationalPowerConsumption",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (power != null && postfixed.Add(power))
            harmony.Patch(power, postfix: new HarmonyMethod(PostfixPowerMethod));
        else
            log.Warning("ProductionBoost: GetOperationalPowerConsumption not found on {0}", blockType);
    }

    private static bool Enabled
    {
        get
        {
            return PluginRef.TryGetTarget(out var plugin) &&
                   plugin.ConfigData.ProductionConcealment == ProductionConcealmentMode.Approximate;
        }
    }

    private static long CurrentTick => PluginRef.TryGetTarget(out var plugin) ? plugin.Tick : 0;

    private static double BoostLevel =>
        PluginRef.TryGetTarget(out var plugin) ? Math.Max(1.0, plugin.ConfigData.ProductionBoostLevel) : 1.0;

    private static double MaxBoostFrames =>
        PluginRef.TryGetTarget(out var plugin) ? Math.Max(0.0, plugin.ConfigData.MaxBoostHours) * 3600.0 * 60.0 : 0.0;

    // Decide whether a grid can currently support the boosted power draw. Hysteresis is
    // built in: a grid that is already boosting keeps boosting while supply meets the
    // (already boosted) demand; a grid that is not boosting only starts once there is
    // headroom for the full extra draw. That dead band keeps the decision from flapping.
    private static void EvaluateGate(MyCubeGrid grid)
    {
        var gridId = grid.EntityId;
        var distributor = grid.GridSystems?.ResourceDistributor;
        if (distributor == null)
        {
            CanBoostGrid[gridId] = false;
            return;
        }

        var electricity = MyResourceDistributorComponent.ElectricityId;
        var maxAvailable = distributor.MaxAvailableResourceByType(electricity, grid);
        var required = distributor.TotalRequiredInputByType(electricity, grid);

        double baseConsumption = 0.0;
        foreach (var block in grid.GetFatBlocks().OfType<MyProductionBlock>())
        {
            if (block.IsProducing && block.BlockDefinition is MyProductionBlockDefinition definition)
                baseConsumption += definition.OperationalPowerConsumption;
        }

        var extra = (BoostLevel - 1.0) * baseConsumption;
        var wasBoosting = CanBoostGrid.TryGetValue(gridId, out var previous) && previous;

        // While boosting the boosted draw is already part of 'required', so we only need
        // supply to keep meeting it. While not boosting we must fit the extra on top.
        CanBoostGrid[gridId] = wasBoosting
            ? maxAvailable >= required
            : maxAvailable >= required + extra;
    }

    // Boost multiplier currently applied to a block: the configured level while its grid
    // has banked time and the grid can supply the boosted power, otherwise 1.0 (normal,
    // unmodified production).
    private static double GetBoostMultiplier(MyProductionBlock block)
    {
        if (block == null || !Enabled)
            return 1.0;

        var grid = block.CubeGrid;
        if (grid == null)
            return 1.0;

        var gridId = grid.EntityId;
        if (!BoostBankFrames.TryGetValue(gridId, out var bank) || bank <= 0.0)
            return 1.0;

        if (!CanBoostGrid.TryGetValue(gridId, out var canBoost) || !canBoost)
            return 1.0;

        return BoostLevel;
    }

    // Bigger steps: scale the frame budget so the block processes boost level times
    // more material this update. The bank is read (not consumed) here so the same
    // multiplier stays consistent with the power patch within one update.
    private static void PrefixUpdateProduction(MyProductionBlock __instance, ref uint framesFromLastTrigger,
        out BoostState __state)
    {
        var multiplier = GetBoostMultiplier(__instance);
        __state = new BoostState { OriginalFrames = framesFromLastTrigger, Multiplier = multiplier };

        if (multiplier <= 1.0)
            return;

        var boosted = framesFromLastTrigger * multiplier;
        framesFromLastTrigger = boosted >= uint.MaxValue ? uint.MaxValue : (uint)boosted;
    }

    // Consume the grid's banked boost time by the real frames elapsed since the bank was
    // last drained, clamped to the frames this update actually processed. The clamp keeps
    // a grid that resumes boosting after a power-starved gap from draining the whole gap,
    // and makes the per-grid bank drain at real time no matter how many blocks it has.
    private static void PostfixUpdateProduction(MyProductionBlock __instance, BoostState __state)
    {
        if (__state.Multiplier <= 1.0 || __instance == null || !__instance.IsProducing)
            return;

        var grid = __instance.CubeGrid;
        if (grid == null)
            return;

        var gridId = grid.EntityId;
        if (!BoostBankFrames.TryGetValue(gridId, out var bank))
            return;

        var now = CurrentTick;
        var last = LastConsumeTick.GetOrAdd(gridId, now);
        var elapsed = now - last;
        if (elapsed <= 0)
            return;

        var drain = Math.Min(elapsed, __state.OriginalFrames);
        if (drain <= 0)
            return;

        LastConsumeTick[gridId] = now;

        var remaining = bank - drain;
        if (remaining <= 0.0)
        {
            BoostBankFrames.TryRemove(gridId, out _);
            LastConsumeTick.TryRemove(gridId, out _);
            CanBoostGrid.TryRemove(gridId, out _);
        }
        else
        {
            BoostBankFrames[gridId] = remaining;
        }

        dirty = true;
    }

    // Power draw scales with the boost so catch-up is not free. The power gate decides
    // whether the grid can afford this, so here we only need to mirror the multiplier.
    private static void PostfixPowerConsumption(MyProductionBlock __instance, ref float __result)
    {
        var multiplier = GetBoostMultiplier(__instance);
        if (multiplier > 1.0)
            __result *= (float)multiplier;
    }

    // Try the main file first, then the freshly written file (left over if a save was
    // interrupted mid-swap on a non-atomic volume), then the backup. The first one that
    // reads is used, so a database damaged by a crash during save still recovers.
    private void Load()
    {
        foreach (var path in new[] { banksPath, banksPath + ".new", banksPath + ".bak" })
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var loaded = 0;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0)
                        continue;

                    if (TryParseRow(line, out var gridId, out var frames) && frames > 0.0)
                    {
                        BoostBankFrames[gridId] = frames;
                        loaded++;
                    }
                }

                log.Info("Loaded {0} production boost banks from {1}", loaded, path);
                return;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Failed to load production boost banks from {0}, trying next", path);
                BoostBankFrames.Clear();
            }
        }
    }

    // The world save callback may run on a different thread than the per-tick Update, so
    // serialize all writers to keep two flushes from clobbering the file. The new content
    // is written to a side file, then swapped into place atomically (File.Replace, which
    // also keeps the previous copy as .bak). On volumes that do not support the atomic
    // replace we fall back to two quick renames: move the current file to .bak, then move
    // the new file into place. Either way a kill mid-save never leaves a half-written
    // main database, and the previous good copy is retained as .bak.
    private void Save()
    {
        lock (saveLock)
        {
            var newPath = banksPath + ".new";
            var bakPath = banksPath + ".bak";

            try
            {
                var builder = new StringBuilder();
                foreach (var entry in BoostBankFrames)
                {
                    if (entry.Value <= 0.0)
                        continue;

                    builder.Append("{\"gridId\":")
                        .Append(entry.Key.ToString(CultureInfo.InvariantCulture))
                        .Append(",\"frames\":")
                        .Append(entry.Value.ToString("R", CultureInfo.InvariantCulture))
                        .Append("}\n");
                }

                File.WriteAllText(newPath, builder.ToString());

                if (!File.Exists(banksPath))
                {
                    // First save: nothing to back up or replace, just put it in place.
                    File.Move(newPath, banksPath);
                    return;
                }

                try
                {
                    // Atomic on NTFS: banksPath becomes newPath's content and the old
                    // banksPath is moved to bakPath (overwriting any previous backup).
                    File.Replace(newPath, banksPath, bakPath, ignoreMetadataErrors: true);
                }
                catch (Exception replaceEx) when (
                    replaceEx is PlatformNotSupportedException ||
                    replaceEx is IOException ||
                    replaceEx is UnauthorizedAccessException)
                {
                    // Fallback for volumes without atomic replace: two quick renames.
                    if (File.Exists(bakPath))
                        File.Delete(bakPath);

                    File.Move(banksPath, bakPath);
                    File.Move(newPath, banksPath);
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Failed to save production boost banks to {0}", banksPath);
            }
        }
    }

    // Minimal tolerant parse of a {"gridId":<long>,"frames":<double>} row. The schema is
    // fixed and written by Save, so we just pull the two named numbers out of the line.
    private static bool TryParseRow(string line, out long gridId, out double frames)
    {
        gridId = 0;
        frames = 0.0;
        return TryReadNumber(line, "\"gridId\"", out var gridText) &&
               long.TryParse(gridText, NumberStyles.Integer, CultureInfo.InvariantCulture, out gridId) &&
               TryReadNumber(line, "\"frames\"", out var framesText) &&
               double.TryParse(framesText, NumberStyles.Float, CultureInfo.InvariantCulture, out frames);
    }

    private static bool TryReadNumber(string line, string key, out string number)
    {
        number = null;

        var keyIndex = line.IndexOf(key, StringComparison.Ordinal);
        if (keyIndex < 0)
            return false;

        var start = keyIndex + key.Length;
        while (start < line.Length && (line[start] == ':' || line[start] == ' '))
            start++;

        var end = start;
        while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '-' || line[end] == '+' ||
                                     line[end] == '.' || line[end] == 'e' || line[end] == 'E'))
            end++;

        if (end == start)
            return false;

        number = line.Substring(start, end - start);
        return true;
    }

    private struct BoostState
    {
        public uint OriginalFrames;
        public double Multiplier;
    }
}
