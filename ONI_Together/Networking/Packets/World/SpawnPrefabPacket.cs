using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World;

public class SpawnPrefabPacket : IPacket
{
    /// <summary>
    /// Spawn announcements for something this peer already had. Each one is a duplicate
    /// object that used to be created, with an id the registry then held twice.
    /// </summary>
    public static int SpawnsAlreadyHere { get; private set; }

    public int NetId;
    public int Hash;
    public Vector3 Position;
    public bool IsActive = true;

    public bool HasElementData = false;
    public float Mass;
    public float Temperature;
    public byte DiseaseIndex;
    public int DiseaseCount;

    /// <summary>
    /// Required, and missing until now: the receiver builds a packet with
    /// Activator.CreateInstance and then deserialises into it, so a type with only
    /// parameterised constructors cannot be received at all.
    ///
    /// Every one of these threw on arrival - "Default constructor not found for type
    /// SpawnPrefabPacket" - which is why announcing critters from the host had no
    /// effect: critterSent read 5 to 8 and not one of them was ever applied. Nothing
    /// sent this packet before that, so the defect was real and invisible.
    ///
    /// PacketHandlerRobustnessTests builds every registered packet the same way, which
    /// is the check that would have caught it; this type reached the registry without
    /// passing through it.
    /// </summary>
    public SpawnPrefabPacket() { }

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

        // Already here? Then this is a repeat, not a spawn.
        //
        // Nothing checked, so a second announcement of the same object - a resend, or a
        // host that announces something this peer already built - made a duplicate
        // carrying an id the registry then had two claimants for. This peer already ends
        // a run with more creatures than the host, and a spawn handler with no
        // idempotence is the shape that produces exactly that.
        //
        // It also has to hold before creatures can be announced at all, which is the
        // change this guard was written for.
        if (NetworkIdentityRegistry.TryGet(NetId, out var already)
            && already != null && !already.gameObject.IsNullOrDestroyed())
        {
            SpawnsAlreadyHere++;
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