using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// The host finished a building; give the copy here its id.
	///
	/// A client draws a building the moment it is placed so the game stays
	/// responsive, and deliberately does not mint an id for it: the host has not
	/// issued one, and an id invented locally is an address the host cannot use.
	/// The object stays a "client preview" until the host names it.
	///
	/// Almost nothing ever named one. A client measured 154 previews created and
	/// 12 adopted - 142 objects the client had drawn, was simulating, and could not
	/// be addressed by, for the rest of the session. Every packet about any of them
	/// is a failed lookup, and that run logged 691 misses.
	///
	/// It read as something worse than it is. The cross-peer comparison reported
	/// "host-only Tile at cell 44877 - the client never built or received it", and
	/// that looked like a building missing from one peer entirely. The lifecycle
	/// trace showed both peers creating it in the same second: "+|Tile|44877|
	/// 1174980794|host" against "+|Tile|44877|0|client". The building was there.
	/// Only its name was missing, and the comparison reads the registry, so an
	/// object with no id is indistinguishable from an object that does not exist.
	///
	/// This adopts; it never creates. The client already makes the object, so there
	/// is nothing to build and no risk of two copies. If the named cell holds
	/// nothing, or holds a different prefab, nothing happens - the same discipline
	/// as the removal packet, for the same reason: an id that means two things must
	/// not be allowed to act on the wrong object.
	/// </summary>
	public class BuildingSpawnedPacket : IPacket
	{
		public int NetId;
		public int Cell;
		public string Prefab;

		public BuildingSpawnedPacket() { }

		public BuildingSpawnedPacket(int netId, int cell, string prefab)
		{
			NetId = netId;
			Cell = cell;
			Prefab = prefab ?? string.Empty;
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(NetId);
			writer.Write(Cell);
			writer.Write(Prefab ?? string.Empty);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			NetId = reader.ReadInt32();
			Cell = reader.ReadInt32();
			Prefab = reader.ReadString();
		}

		public static int Adopted { get; private set; }
		public static int AlreadyCorrect { get; private set; }
		public static int NotPresent { get; private set; }
		public static int Refused { get; private set; }
		public static int Renamed { get; private set; }

		public static void ResetForNewSession()
		{
			Adopted = 0;
			AlreadyCorrect = 0;
			NotPresent = 0;
			Refused = 0;
			Renamed = 0;
		}

		public static string Describe() =>
			$"adopted={Adopted} alreadyCorrect={AlreadyCorrect} renamed={Renamed} " +
			$"notPresent={NotPresent} refused={Refused}";

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost)
				return;

			// An id of zero is what this packet exists to fix; it cannot also be
			// the answer.
			if (NetId == 0 || !Grid.IsValidCell(Cell))
				return;

			var target = Grid.Objects[Cell, (int)ObjectLayer.Building];
			if (target.IsNullOrDestroyed())
			{
				NotPresent++;
				return;
			}

			string localPrefab = target.PrefabID().ToString();
			if (localPrefab != Prefab)
			{
				Refused++;
				ThrottledLog.Warn(
					$"[BuildingSpawned] not naming cell {Cell}: the host says '{Prefab}' is there, " +
					$"this peer has '{localPrefab}'. Naming it would point NetId {NetId} at the " +
					"wrong object.");
				return;
			}

			var identity = target.AddOrGet<NetworkIdentity>();
			if (identity.NetId == NetId)
			{
				AlreadyCorrect++;
				return;
			}

			// Worth counting apart: adopting an unnamed object is the case this was
			// written for, while taking an id away from one that already had a
			// different one means the two peers had disagreed about it - the host
			// wins, but the disagreement is the interesting part.
			if (identity.NetId == 0) Adopted++; else Renamed++;

			identity.OverrideNetId(NetId);
		}
	}
}
