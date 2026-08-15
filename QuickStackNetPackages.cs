using System;
using System.Collections.Generic;

// Net Package for sending a list of container entities
public abstract class NetPackageInvManageAction : NetPackage
{
    public override NetPackageDirection PackageDirection => NetPackageDirection.Both;
    public override bool AllowedBeforeAuth => false;
    public abstract override void ProcessPackage(World _world, GameManager _callbacks);
    protected Vector3i center;
    protected List<Vector3i> containerEntities = new List<Vector3i>();

    protected NetPackageInvManageAction Setup(Vector3i _center, List<Vector3i> _containerEntities)
    {
        try
        {
            center = _center;
            containerEntities = _containerEntities;
            return this;
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
            return null;
        }
    }

    // Requantizes Vector3i to a 3-bytes. Requires -128 < x, y, z <= 128 
    protected static void WriteOptimized(PooledBinaryWriter _writer, Vector3i ivec3)
    {
        try
        {
            _writer.Write((sbyte)ivec3.x);
            _writer.Write((sbyte)ivec3.y);
            _writer.Write((sbyte)ivec3.z);
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    protected static void ReadOptimized(PooledBinaryReader _reader, out Vector3i ivec3)
    {
        try
        {
            ivec3 = new Vector3i
            {
                x = _reader.ReadSByte(),
                y = _reader.ReadSByte(),
                z = _reader.ReadSByte()
            };
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
            ivec3 = new Vector3i(0, 0, 0);
        }
    }

    // Vector3i without any requantization. Full range, but takes up 4x more space
    protected static void Write(PooledBinaryWriter _writer, Vector3i ivec3)
    {
        try
        {
            _writer.Write(ivec3.x);
            _writer.Write(ivec3.y);
            _writer.Write(ivec3.z);
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    protected static void Read(PooledBinaryReader _reader, out Vector3i ivec3)
    {
        try
        {
            ivec3 = new Vector3i
            {
                x = _reader.ReadInt32(),
                y = _reader.ReadInt32(),
                z = _reader.ReadInt32()
            };
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
            ivec3 = new Vector3i(0, 0, 0);
        }
    }

    public override int GetLength()
    {
        try
        {
            return 3 * sizeof(int) + sizeof(ushort) + 3 * containerEntities.Count;
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
            return 0;
        }
    }

    public override void read(PooledBinaryReader _reader)
    {
        try
        {
            Read(_reader, out center);

            int count = _reader.ReadInt16();
            containerEntities = new List<Vector3i>(count);
            for (int i = 0; i < count; ++i)
            {
                ReadOptimized(_reader, out var idx);
                containerEntities.Add(idx);
            }
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    public override void write(PooledBinaryWriter _writer)
    {
        try
        {
            base.write(_writer);

            Write(_writer, center);

            if (containerEntities == null)
            {
                _writer.Write((ushort)0);
                return;
            }

            _writer.Write((ushort)containerEntities.Count);
            foreach (var id in containerEntities)
            {
                WriteOptimized(_writer, id);
            }
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }
}

public class NetPackageDoQuickStack : NetPackageInvManageAction
{
    protected QuickStackType type;

    public NetPackageDoQuickStack Setup(Vector3i _center, List<Vector3i> _containerEntities, QuickStackType _type)
    {
        base.Setup(_center, _containerEntities);
        type = _type;
        return this;
    }

    public override void ProcessPackage(World _world, GameManager _callbacks)
    {
        if (_world == null)
            return;

        try
        {
            switch (type)
            {
                case QuickStackType.Stack:
                    DrokkStoragePatches.ClientMoveQuickStack(center, containerEntities);
                    break;

                case QuickStackType.Restock:
                    DrokkStoragePatches.ClientMoveQuickRestock(center, containerEntities);
                    break;
            }
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    public override int GetLength()
    {
        return base.GetLength() + 1;
    }

    public override void write(PooledBinaryWriter _writer)
    {
        base.write(_writer);
        _writer.Write((byte)type);
    }

    public override void read(PooledBinaryReader _reader)
    {
        base.read(_reader);
        type = (QuickStackType)_reader.ReadByte();
    }
}

// Server -> client push of the authoritative gameplay settings.
//
// Every setting is read from each machine's own Config/settings.xml, but almost all of the
// enforcement for the craft/repair/reload/refuel pull happens CLIENT-side (ScanQuadrant, the
// transpiled count/removal helpers), while QuickStack/QuickRestock is gated server-side in
// IsContainerUnlocked. Left alone, that means a server admin editing settings.xml on the server
// changes nothing for connected players - they keep running their own file - and a player can
// re-enable a feature the server turned off just by editing their local copy. This packet makes
// the server's copy win for everything that affects gameplay; it's sent to each client as they
// spawn in.
//
// Deliberately NOT synced (per-machine preferences with no gameplay effect): Debug,
// LockModeIconVisible, LiveRecipeTracking, and the hotkey/colour settings.
public class NetPackageDrokkStorageConfig : NetPackage
{
    public override NetPackageDirection PackageDirection => NetPackageDirection.ToClient;
    public override bool AllowedBeforeAuth => false;

    protected float range;
    protected int stashDistance;
    protected bool craftFromContainersEnabled;
    protected bool enableForRepairAndUpgrade;
    protected bool enableForReload;
    protected bool enableForRefuel;
    protected bool allowLockedContainers;
    protected bool checkOwnership;
    protected bool multiViewersEnabled;
    protected bool pullFromVehicles;
    protected bool pullFromDrones;
    protected bool pullFromWorkstationOutputs;
    protected bool pullFromDewCollectors;

    public NetPackageDrokkStorageConfig Setup(DrokkStorageConfig _config)
    {
        range = _config.range;
        stashDistance = _config.stashDistance.x;
        craftFromContainersEnabled = _config.craftFromContainersEnabled;
        enableForRepairAndUpgrade = _config.enableForRepairAndUpgrade;
        enableForReload = _config.enableForReload;
        enableForRefuel = _config.enableForRefuel;
        allowLockedContainers = _config.allowLockedContainers;
        checkOwnership = _config.checkOwnership;
        multiViewersEnabled = _config.multiViewersEnabled;
        pullFromVehicles = _config.pullFromVehicles;
        pullFromDrones = _config.pullFromDrones;
        pullFromWorkstationOutputs = _config.pullFromWorkstationOutputs;
        pullFromDewCollectors = _config.pullFromDewCollectors;
        return this;
    }

    public override void ProcessPackage(World _world, GameManager _callbacks)
    {
        try
        {
            if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
                return;

            var cfg = DrokkStorage.config;
            Log.Out($" [DrokkStorage] Applying server settings (local settings.xml ignored for these): "
                + $"ContainerRange={range} (was {cfg.range}), QuickStackDistance={stashDistance} (was {cfg.stashDistance.x}), "
                + $"CraftFromContainersEnabled={craftFromContainersEnabled} (was {cfg.craftFromContainersEnabled}), "
                + $"EnableForRepairAndUpgrade={enableForRepairAndUpgrade} (was {cfg.enableForRepairAndUpgrade}), "
                + $"EnableForReload={enableForReload} (was {cfg.enableForReload}), "
                + $"EnableForRefuel={enableForRefuel} (was {cfg.enableForRefuel}), "
                + $"AllowLockedContainers={allowLockedContainers} (was {cfg.allowLockedContainers}), "
                + $"CheckOwnership={checkOwnership} (was {cfg.checkOwnership}), "
                + $"MultiViewersEnabled={multiViewersEnabled} (was {cfg.multiViewersEnabled}), "
                + $"PullFromVehicles={pullFromVehicles} (was {cfg.pullFromVehicles}), "
                + $"PullFromDrones={pullFromDrones} (was {cfg.pullFromDrones}), "
                + $"PullFromWorkstationOutputs={pullFromWorkstationOutputs} (was {cfg.pullFromWorkstationOutputs}), "
                + $"PullFromDewCollectors={pullFromDewCollectors} (was {cfg.pullFromDewCollectors})");

            cfg.range = range;
            cfg.stashDistance = new Vector3i(stashDistance, stashDistance, stashDistance);
            cfg.craftFromContainersEnabled = craftFromContainersEnabled;
            cfg.enableForRepairAndUpgrade = enableForRepairAndUpgrade;
            cfg.enableForReload = enableForReload;
            cfg.enableForRefuel = enableForRefuel;
            cfg.allowLockedContainers = allowLockedContainers;
            cfg.checkOwnership = checkOwnership;
            cfg.multiViewersEnabled = multiViewersEnabled;
            cfg.pullFromVehicles = pullFromVehicles;
            cfg.pullFromDrones = pullFromDrones;
            cfg.pullFromWorkstationOutputs = pullFromWorkstationOutputs;
            cfg.pullFromDewCollectors = pullFromDewCollectors;

            // The cached quadrant scans were built under the client's own settings - rebuild now
            // so the next crafting window doesn't show availability from sources that just stopped
            // qualifying (or miss ones that just started).
            DrokkStoragePatches.FullRescan();
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    public override int GetLength()
    {
        return 19;
    }

    public override void write(PooledBinaryWriter _writer)
    {
        base.write(_writer);
        _writer.Write(range);
        _writer.Write(stashDistance);
        _writer.Write(craftFromContainersEnabled);
        _writer.Write(enableForRepairAndUpgrade);
        _writer.Write(enableForReload);
        _writer.Write(enableForRefuel);
        _writer.Write(allowLockedContainers);
        _writer.Write(checkOwnership);
        _writer.Write(multiViewersEnabled);
        _writer.Write(pullFromVehicles);
        _writer.Write(pullFromDrones);
        _writer.Write(pullFromWorkstationOutputs);
        _writer.Write(pullFromDewCollectors);
    }

    public override void read(PooledBinaryReader _reader)
    {
        range = _reader.ReadSingle();
        stashDistance = _reader.ReadInt32();
        craftFromContainersEnabled = _reader.ReadBoolean();
        enableForRepairAndUpgrade = _reader.ReadBoolean();
        enableForReload = _reader.ReadBoolean();
        enableForRefuel = _reader.ReadBoolean();
        allowLockedContainers = _reader.ReadBoolean();
        checkOwnership = _reader.ReadBoolean();
        multiViewersEnabled = _reader.ReadBoolean();
        pullFromVehicles = _reader.ReadBoolean();
        pullFromDrones = _reader.ReadBoolean();
        pullFromWorkstationOutputs = _reader.ReadBoolean();
        pullFromDewCollectors = _reader.ReadBoolean();
    }
}

public class NetPackageFindOpenableContainers : NetPackage
{
    public override NetPackageDirection PackageDirection => NetPackageDirection.ToServer;
    public override bool AllowedBeforeAuth => false;
    protected int playerEntityId;
    protected QuickStackType type;

    public NetPackageFindOpenableContainers Setup(int _playerEntityId, QuickStackType _type)
    {
        playerEntityId = _playerEntityId;
        type = _type;
        return this;
    }

    public override void ProcessPackage(World _world, GameManager _callbacks)
    {
        if (!SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            return;

        try
        {
            var player = _world.GetEntity(playerEntityId) as EntityPlayer;

            if (player == null)
                return;

            if (type >= QuickStackType.Count || type < QuickStackType.Stack)
                return;

            var center = new Vector3i(player.position);

            List<Vector3i> openableEntities = new List<Vector3i>(1024);

            foreach (var pair in DrokkStoragePatches.FindNearbyLootContainers(center, playerEntityId))
            {
                openableEntities.Add(pair.Item1);
                // Reserve immediately: the client executes the actual move after this packet
                // round-trips back to them, so another player's request in that window must not
                // also see this container as available (see quickMoveReservations).
                DrokkStoragePatches.ReserveForQuickMove(center + pair.Item1, playerEntityId);
            }

            var cinfo = ConnectionManager.Instance.Clients.ForEntityId(playerEntityId);

            if (cinfo != null)
                cinfo.SendPackage(NetPackageManager.GetPackage<NetPackageDoQuickStack>().Setup(center, openableEntities, type));
        }
        catch (Exception e)
        {
            DrokkStorage.Dbgl(e.Message);
        }
    }

    public override int GetLength()
    {
        return 5;
    }

    public override void write(PooledBinaryWriter _writer)
    {
        base.write(_writer);
        _writer.Write(playerEntityId);
        _writer.Write((byte)type);
    }

    public override void read(PooledBinaryReader _reader)
    {
        playerEntityId = _reader.ReadInt32();
        type = (QuickStackType)_reader.ReadByte();
    }
}
