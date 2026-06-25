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
