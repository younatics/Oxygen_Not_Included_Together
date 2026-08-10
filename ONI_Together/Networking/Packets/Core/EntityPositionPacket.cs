using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using System.IO;
using Shared.Profiling;
using UnityEngine;
using Shared.Interfaces.Networking;

public class EntityPositionPacket : IPacket, IViewportCullable
{
	public int NetId;
	public Vector3 Position;
	public bool FlipX;
	public bool FlipY;
	public NavType NavType;
	public long Timestamp;

    public int GetViewportCell()
    {
		var cell = Grid.PosToCell(Position);
		return cell;
    }
	
    public void Serialize(BinaryWriter writer)
	{
		using var _ = Profiler.Scope();

		writer.Write(NetId);
		writer.Write(Position);
		writer.Write(FlipX);
		writer.Write(FlipY);
		writer.Write((byte)NavType);
		writer.Write(Timestamp);
	}

	public void Deserialize(BinaryReader reader)
	{
		using var _ = Profiler.Scope();

		NetId = reader.ReadInt32();
		Position = reader.ReadVector3();
		FlipX = reader.ReadBoolean();
		FlipY = reader.ReadBoolean();
		NavType = (NavType)reader.ReadByte();
		Timestamp = reader.ReadInt64();
	}

	public void OnDispatched()
	{
		using var _ = Profiler.Scope();

		if (MultiplayerSession.IsHost) return;

		if (NetworkIdentityRegistry.TryGet(NetId, out var entity))
		{
			// The registry-miss branch below reports itself; this one did not,
			// so an entity that is registered but never got a handler was frozen
			// at its spawn position and invisible to every diagnostic.
			EntityPositionHandler handler = entity.GetComponent<EntityPositionHandler>();
			if (!handler)
			{
				ThrottledLog.Warn($"[Packets] '{entity.name}' has no position handler; it cannot follow the host");
				return;
			}

			if (handler.serverTimestamp > Timestamp)
				return;

            handler.serverPosition = Position;
            handler.serverTimestamp = Timestamp;
            handler.serverFlipX = FlipX;
			handler.serverFlipY = FlipY;
			handler.serverNavType = NavType;
        }
		else
		{
			// Position arrives many times a second for an entity this peer does
			// not have, so this was one line per packet: 5881 of them for a
			// single hatch, and 27909 across 26 ids in one session.
			ThrottledLog.Warn($"[Packets] Could not find entity with NetId {NetId}");
		}
	}
}
