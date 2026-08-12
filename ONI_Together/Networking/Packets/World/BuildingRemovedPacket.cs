using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// The host destroyed a building; remove the copy here.
	///
	/// Deconstruction replicated. Destruction did not. A dig ran the four tiles at
	/// cells 48746-48749 down to zero hit points on the host, which destroyed them
	/// - the lifecycle trace shows all four as "-|Tile|48746|1228387077|host" - and
	/// the client has no removal record for any of them. It kept four tiles at
	/// 29, 26 and 9 hit points out of 100, and nothing would ever take them away:
	/// the host cannot correct a building it no longer has, so the last damage
	/// value it ever sent is the value the client keeps forever. One player sees a
	/// tunnel, the other sees solid ground, and they disagree about it permanently.
	/// This reproduced identically on two runs, on the same cells.
	///
	/// Sent per destruction rather than reconciled by absence. A periodic "these
	/// are the buildings I have, drop the rest" sweep is the shape that once
	/// deleted 294 plants off a client, because anything not yet replicated reads
	/// as deleted. One packet for one event cannot do that.
	///
	/// The cell and the prefab travel with the id and are checked before anything
	/// is destroyed. Ids do still get mixed up on this codebase - the same run had
	/// a Clay and a GasConduit claiming one - and "destroy whatever this id
	/// resolves to" would turn an addressing mistake into a building deleted out
	/// of a colony. If the three do not agree, nothing is destroyed and the
	/// disagreement is logged with both names.
	/// </summary>
	public class BuildingRemovedPacket : IPacket
	{
		public int NetId;
		public int Cell;
		public string Prefab;

		public BuildingRemovedPacket() { }

		public BuildingRemovedPacket(int netId, int cell, string prefab)
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

		/// <summary>
		/// Counted separately for real traffic and for the suite.
		///
		/// The Removal tests fire deliberately wrong removals to prove the guard
		/// holds, and if those landed in the same counters the run report would
		/// show refusals that gameplay never produced. That mistake has already
		/// been made twice in this work - once it attributed two host failures to
		/// the game when the tests had caused them - so the split is here from the
		/// start rather than after the next misreading.
		/// </summary>
		public static int Applied { get; private set; }
		public static int NotPresent { get; private set; }
		public static int Refused { get; private set; }

		public static int TestApplied { get; private set; }
		public static int TestNotPresent { get; private set; }
		public static int TestRefused { get; private set; }

		private static void CountApplied()
		{
			if (NetworkIdentityRegistry.InDiagnosticScope) TestApplied++; else Applied++;
		}

		private static void CountNotPresent()
		{
			if (NetworkIdentityRegistry.InDiagnosticScope) TestNotPresent++; else NotPresent++;
		}

		private static void CountRefused()
		{
			if (NetworkIdentityRegistry.InDiagnosticScope) TestRefused++; else Refused++;
		}

		public static void ResetForNewSession()
		{
			Applied = 0;
			NotPresent = 0;
			Refused = 0;
			TestApplied = 0;
			TestNotPresent = 0;
			TestRefused = 0;
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost)
				return;

			if (!Grid.IsValidCell(Cell))
				return;

			GameObject target = null;

			if (NetworkIdentityRegistry.TryGet(NetId, out var identity) && identity != null
				&& !identity.gameObject.IsNullOrDestroyed())
			{
				target = identity.gameObject;
			}
			else
			{
				// No identity for it here. The building may still exist - plenty of
				// them are never registered - so fall back to the cell, which is
				// the part of the address that does not depend on hashing.
				target = Grid.Objects[Cell, (int)ObjectLayer.Building];
			}

			if (target.IsNullOrDestroyed())
			{
				CountNotPresent();
				return;
			}

			string localPrefab = target.PrefabID().ToString();
			int localCell = Grid.PosToCell(target);

			if (localPrefab != Prefab || localCell != Cell)
			{
				CountRefused();
				ThrottledLog.Warn(
					$"[BuildingRemoved] refusing to destroy: NetId {NetId} names '{Prefab}' at cell {Cell} " +
					$"but resolves here to '{localPrefab}' at cell {localCell}. " +
					"An id that means two things must not be allowed to delete a building.");
				return;
			}

			CountApplied();
			Util.KDestroyGameObject(target);
		}
	}
}
