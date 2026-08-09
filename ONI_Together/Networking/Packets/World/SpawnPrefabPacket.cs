using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World;

public class SpawnPrefabPacket : IPacket
{
    public int NetId;
    public int Hash;
    public Vector3 Position;
    public bool IsActive = true;

    public bool HasElementData = false;
    public float Mass;
    public float Temperature;
    public byte DiseaseIndex;
    public int DiseaseCount;

    public SpawnPrefabPacket(int netId, int hash, Vector3 position)
    {
        NetId = netId;
        Hash = hash;
        Position = position;
        HasElementData = false;
    }
    
    public SpawnPrefabPacket(int netId, int hash, Vector3 position, float mass, float temperature, byte diseaseIndex, int diseaseCount)
    {
        NetId = netId;
        Hash = hash;
        Position = position;
        HasElementData = true;
        Mass = mass;
        Temperature = temperature;
        DiseaseIndex = diseaseIndex;
        DiseaseCount = diseaseCount;
    }
    
    public void Serialize(BinaryWriter writer)
    {
        writer.Write(NetId);
        writer.Write(Hash);
        writer.Write(Position);
        writer.Write(IsActive);
        writer.Write(HasElementData);
        if (!HasElementData) return;
        
        writer.Write(Mass);
        writer.Write(Temperature);
        writer.Write(DiseaseIndex);
        writer.Write(DiseaseCount);
    }

    public void Deserialize(BinaryReader reader)
    {
        NetId = reader.ReadInt32();
        Hash = reader.ReadInt32();
        Position = reader.ReadVector3();
        IsActive = reader.ReadBoolean();
        HasElementData = reader.ReadBoolean();
        if (!HasElementData) return;
        
        Mass = reader.ReadSingle();
        Temperature = reader.ReadSingle();
        DiseaseIndex =  reader.ReadByte();
        DiseaseCount = reader.ReadInt32();
    }

    public void OnDispatched()
    {
        if (MultiplayerSession.IsHost) return;

        // Nothing may be built before there is a world to build it in.
        //
        // A joining client turns packet processing on while it is still in the
        // menu, because the save file transfer needs it, and the host starts
        // sending spawns immediately. Instantiating then runs OnPrefabInit
        // against an empty Grid: Pickupable and the infrared visualizer both
        // divide by Grid.WidthInCells, which is zero until a world is loaded.
        // A live client logged fifteen of those in the eight seconds between
        // connecting and loading, and the session did not survive the minute.
        if (Grid.WidthInCells == 0 || !Grid.IsValidCell(Grid.PosToCell(Position)))
        {
            DebugConsole.LogWarning(
                $"[SpawnPrefab] ignoring spawn of {Hash} at {Position}: no world loaded yet");
            return;
        }

        GameObject go;
        if (HasElementData)
        {
            var element = ElementLoader.GetElement(new Tag(Hash));
            if (element == null) return;
            go = element.substance.SpawnResource(Position, Mass, Temperature, DiseaseIndex, DiseaseCount);
        }
        else
        {
            var prefab = Assets.GetPrefab(new Tag(Hash));
            if (prefab == null) return;
            go = Util.KInstantiate(prefab, Position);
            go.SetActive(IsActive);
        }
        
        // SpawnResource returns null when the element cannot be placed where it
        // was asked for, and this went straight on to dereference it - so a
        // refused spawn became a NullReferenceException out of a packet handler
        // rather than a dropped packet.
        if (go == null)
        {
            DebugConsole.LogWarning($"[SpawnPrefab] {Hash} could not be spawned at {Position}");
            return;
        }

        go.AddOrGet<NetworkIdentity>().OverrideNetId(NetId);
        
        // Race condition guard: Was this prefab already picked up / stored before the packet arrived?
        if (GroundItemPickedUpPacket.TryConsumePending(NetId) || StorageItemPacket.TryConsumePending(NetId))
        {
            Util.KDestroyGameObject(go);
        }
    }
}