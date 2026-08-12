using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// Host -> clients: this ground item is gone.
	///
	/// Carried the NetId alone, and that was the last thing keeping the two peers
	/// disagreeing. An id is not always enough: the client may hold the same physical
	/// pile under a different number, and then the removal cannot be applied, the
	/// client keeps a pile the host has destroyed, and the host later hands that number
	/// to something new. Traced end to end on a live pair - the host dropped a resource
	/// as -885421119, the pile was consumed, a MushBar took the number, and the client
	/// was still holding the dead pile under it. From then on every packet about the
	/// food arrived addressed to rubble.
	///
	/// Three separate attempts to fix that from the id side all failed and one made
	/// things worse, because the id was never the problem: the removal simply had no
	/// second way to find its target. So the packet now says where the item was and
	/// what it was, and the client can match on that when the number misses.
	///
	/// Twelve bytes instead of four. The batch size is cut to match, so the packet on
	/// the wire stays the size it was - Riptide's payload limit is 1000 bytes and
	/// several bugs in this project came from packets that quietly grew past it.
	/// </summary>
	public class GroundItemPickedUpPacket : IPacket
	{
		private static readonly PendingRemovals Pending = new PendingRemovals("PendingPickup");

		public int NetId;

		/// <summary>Where the item was when the host destroyed it.</summary>
		public int Cell;

		/// <summary>What it was, so a match by place cannot remove the wrong thing.</summary>
		public int PrefabHash;

		/// <summary>Removals applied by position after the id missed.</summary>
		public static int MatchedByCell { get; private set; }

		/// <summary>Removals that found nothing either way.</summary>
		public static int Unmatched { get; private set; }

		public static bool TryConsumePending(int netId) => Pending.TryConsume(netId);
		public static void ClearPending() => Pending.Clear();

		public GroundItemPickedUpPacket() { }

		public GroundItemPickedUpPacket(GameObject item, int netId)
		{
			using var _ = Profiler.Scope();

			NetId = netId;
			Cell = item != null && !item.IsNullOrDestroyed() ? Grid.PosToCell(item) : Grid.InvalidCell;
			PrefabHash = item != null && !item.IsNullOrDestroyed() && item.TryGetComponent<KPrefabID>(out var kpid)
				? kpid.PrefabTag.GetHash()
				: 0;
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			writer.Write(NetId);
			writer.Write(Cell);
			writer.Write(PrefabHash);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			NetId = reader.ReadInt32();
			Cell = reader.ReadInt32();
			PrefabHash = reader.ReadInt32();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			// A miss here is the normal case, not a divergence.
			//
			// Spawns are culled to this peer's viewport and removals are not, so most
			// pickup notices name an item this client was never told existed. The queue
			// below is the handling; counting it as a failed lookup as well made the
			// divergence number twenty times larger than the real gap and sent three
			// separate fixes after noise.
			Pickupable pickupable;
			using (NetworkIdentityRegistry.ExpectedMissScope())
				NetworkIdentityRegistry.TryGetComponent(NetId, out pickupable);

			if (!pickupable.IsNullOrDestroyed())
			{
				Util.KDestroyGameObject(pickupable.gameObject);
				return;
			}

			// The id missed. Look where the host said it was.
			//
			// Measured: 0 matches out of 151. The diagnostic said why - the client has
			// nothing at those cells at all, and the cells come in consecutive runs with
			// one prefab, which is a row of ore dropped along a dig. The coordinate is
			// right; the piles were never sent.
			//
			// That is the design, written down in PendingRemovals: spawns are culled to
			// a client's viewport and removal notices are not, so a client is routinely
			// told about the removal of something it was never told about. This fallback
			// was aimed at a problem that is not there, and the extra eight bytes an item
			// buys nothing. Kept only as the diagnostic that established that.
			var byPlace = FindAtCell();
			if (!byPlace.IsNullOrDestroyed())
			{
				MatchedByCell++;
				ThrottledLog.Info(
					$"[GroundItemPickedUpPacket] NetId {NetId} missed; removed the matching item at cell {Cell} instead");
				Util.KDestroyGameObject(byPlace.gameObject);
				return;
			}

			// Neither. Say what was actually at that cell, because the fallback matched
			// nothing at all - 0 of 151 - and "it did not match" has two very different
			// causes. Either the cell is wrong (the host takes the position at pickup
			// time, by which point the item may already be in the carrier's hands, so
			// the coordinate is the duplicant's and not the pile's), or the cell is
			// right and this peer simply never had that pile. The first is a bug in this
			// packet; the second moves the whole problem to spawn replication.
			Unmatched++;
			if (Unmatched <= 12 && Grid.IsValidCell(Cell))
			{
				var names = new List<string>();
				var head = Grid.Objects[Cell, (int)ObjectLayer.Pickupables];
				for (var item = head.IsNullOrDestroyed() ? null : head.GetComponent<ObjectLayerListItem>();
					 item != null && names.Count < 4;
					 item = item.nextItem)
				{
					if (item.gameObject.IsNullOrDestroyed()) continue;
					names.Add(item.gameObject.PrefabID().ToString());
				}

				DebugConsole.LogWarning(
					$"[GroundItemPickedUpPacket] no match for NetId {NetId} at cell {Cell} " +
					$"(host said prefabHash {PrefabHash}); this peer has [{string.Join(", ", names)}] there");
			}
			Pending.Queue(NetId);
			ThrottledLog.Warn("[GroundItemPickedUpPacket] pickup arrived for an item this peer does not have");
		}

		/// <summary>
		/// The item the host meant, found by where it was and what it was.
		///
		/// Both have to match. Position alone would remove whatever happens to be
		/// standing there - and a cell routinely holds several different piles, so that
		/// is not a theoretical concern.
		/// </summary>
		private Pickupable FindAtCell()
		{
			if (PrefabHash == 0 || !Grid.IsValidCell(Cell)) return null;

			var objects = Grid.Objects[Cell, (int)ObjectLayer.Pickupables];
			if (objects == null || objects.IsNullOrDestroyed()) return null;

			var scene = objects.GetComponent<ObjectLayerListItem>();
			for (var item = scene; item != null; item = item.nextItem)
			{
				var go = item.gameObject;
				if (go.IsNullOrDestroyed()) continue;
				if (!go.TryGetComponent<Pickupable>(out var candidate) || candidate.IsNullOrDestroyed()) continue;
				if (!go.TryGetComponent<KPrefabID>(out var kpid) || kpid.IsNullOrDestroyed()) continue;
				if (kpid.PrefabTag.GetHash() != PrefabHash) continue;

				return candidate;
			}

			return null;
		}
	}
}
