using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using System.IO;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// Synchronizes building assignments (Outhouse, Lavatory, Triage Cot, Massage Table, etc.)
	/// Uses NetIDs for duplicants to ensure consistent assignment across host and clients.
	/// </summary>
	public class AssignmentPacket : IPacket
	{
		public int BuildingNetId;       // NetID of the building being assigned
		public int Cell;                // Cell location for fallback lookup
		public int AssigneeNetId;       // NetID of the duplicant being assigned (-1 for unassign)
		public string GroupId = "";     // For assignment groups like "public"

		/// <summary>
		/// The prefab the sender was assigning, so the cell fallback can tell whether the
		/// thing it found is the thing this packet is about. Zero from an older peer.
		/// </summary>
		public int PrefabHash;

		/// <summary>
		/// Cell fallbacks refused because the object at that cell is not what the sender
		/// was assigning. Each one would have been an id landing on the wrong object.
		/// </summary>
		public static int CellFallbackRefused { get; private set; }

		public static bool IsApplying = false;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(BuildingNetId);
			writer.Write(Cell);
			writer.Write(AssigneeNetId);
			writer.Write(GroupId ?? "");
			writer.Write(PrefabHash);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			BuildingNetId = reader.ReadInt32();
			Cell = reader.ReadInt32();
			AssigneeNetId = reader.ReadInt32();
			GroupId = reader.ReadString();
			PrefabHash = reader.ReadInt32();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			DebugConsole.Log($"[AssignmentPacket] Received: BuildingNetId={BuildingNetId}, Cell={Cell}, AssigneeNetId={AssigneeNetId}, GroupId={GroupId}");

			NetworkIdentity buildingIdentity = null;

			// Try to find by NetID first
			if (!NetworkIdentityRegistry.TryGet(BuildingNetId, out buildingIdentity) || buildingIdentity == null)
			{
				// Fallback: find building by cell
				if (Grid.IsValidCell(Cell))
				{
					GameObject buildingGO = Grid.Objects[Cell, (int)ObjectLayer.Building];

					// Is it the thing this packet is about, or just the thing standing
					// there?
					//
					// The fallback took whatever building occupied the cell and gave it
					// the packet's id. A twenty-five minute run caught what that costs:
					// the host's -142907511 is an Atmo_Suit - assignables are not only
					// buildings, a duplicant is assigned to a suit - and the suit sits
					// inside a locker at cell 51566. The client could not resolve the id,
					// found the SuitLockerComplete at that cell and renamed it, so one
					// number meant a suit on one peer and a locker on the other. It is
					// the only id the two peers disagreed about in the whole run.
					//
					// The same shape as the resolve reply that turned eggs into Creature
					// piles, and it takes the same answer: carry what the sender was
					// looking at and refuse when it does not match. Position is a hint
					// for finding a candidate, never a licence to rename it.
					if (buildingGO != null && PrefabHash != 0)
					{
						int localHash = buildingGO.TryGetComponent<KPrefabID>(out var kpid)
							? kpid.PrefabTag.GetHashCode()
							: 0;
						if (localHash != PrefabHash)
						{
							CellFallbackRefused++;
							DebugTools.ThrottledLog.Warn(
								$"[AssignmentPacket] refusing to put NetId {BuildingNetId} on " +
								$"'{buildingGO.PrefabID()}' at cell {Cell} - the sender was " +
								"assigning something else, so this id means two different " +
								"things on the two peers");
							buildingGO = null;
						}
					}

					if (buildingGO != null)
					{
						// AddOrGet returns the component the building already has, and
						// writing NetId on it moved the field without moving the
						// registry entry. That is how two objects came to claim one
						// id: a SuitMarker sat filed under its own 755485301 while
						// its field said 1776225777, which an Iron was legitimately
						// filed under. Everything the marker sent was stamped with
						// the Iron's address and applied to the Iron, and the marker
						// itself was reachable only at a number nobody used.
						//
						// OverrideNetId moves both together - it unregisters the old
						// id, takes the new one, and rehouses whoever held it.
						buildingIdentity = buildingGO.AddOrGet<NetworkIdentity>();
						buildingIdentity.OverrideNetId(BuildingNetId);
						DebugConsole.Log($"[AssignmentPacket] Resolved building by cell {Cell}, assigned NetId {BuildingNetId}");
					}
				}
			}

			if (buildingIdentity == null || buildingIdentity.gameObject == null)
			{
				DebugConsole.LogWarning($"[AssignmentPacket] Building NetId {BuildingNetId} at Cell {Cell} not found.");
				return;
			}

			var assignable = buildingIdentity.gameObject.GetComponent<Assignable>();
			if (assignable == null)
			{
				DebugConsole.LogWarning($"[AssignmentPacket] Building {buildingIdentity.name} has no Assignable component.");
				return;
			}

			try
			{
				IsApplying = true;
				ApplyAssignment(assignable);
			}
			finally
			{
				IsApplying = false;
			}

			// HOST RELAY
			if (MultiplayerSession.IsHost)
			{
				PacketSender.SendToAllClients(this);
				DebugConsole.Log($"[AssignmentPacket] Host relayed assignment to all clients.");
			}
		}

		private void ApplyAssignment(Assignable assignable)
		{
			using var _ = Profiler.Scope();

			// Unassign case
			if (AssigneeNetId == -1 && string.IsNullOrEmpty(GroupId))
			{
				assignable.Unassign();
				DebugConsole.Log($"[AssignmentPacket] Unassigned {assignable.name}");
				return;
			}

			// Assignment group (e.g., "public")
			if (!string.IsNullOrEmpty(GroupId))
			{
				if (Game.Instance.assignmentManager.assignment_groups.TryGetValue(GroupId, out var group))
				{
					assignable.Assign(group);
					DebugConsole.Log($"[AssignmentPacket] Assigned {assignable.name} to group '{GroupId}'");
				}
				else
				{
					DebugConsole.LogWarning($"[AssignmentPacket] Assignment group '{GroupId}' not found.");
				}
				return;
			}

			// Duplicant assignment - find by NetID
			if (!NetworkIdentityRegistry.TryGet(AssigneeNetId, out var dupeIdentity) || dupeIdentity == null)
			{
				DebugConsole.LogWarning($"[AssignmentPacket] Assignee NetId {AssigneeNetId} not found.");
				return;
			}

			// Get the IAssignableIdentity from the duplicant
			var minionIdentity = dupeIdentity.gameObject.GetComponent<MinionIdentity>();
			if (minionIdentity != null)
			{
				// MinionIdentity needs to go through its proxy for assignments
				var proxy = minionIdentity.GetSoleOwner()?.GetComponent<MinionAssignablesProxy>();
				if (proxy != null)
				{
					assignable.Assign(proxy);
					DebugConsole.Log($"[AssignmentPacket] Assigned {assignable.name} to {minionIdentity.name} via proxy");
					return;
				}

				// Try direct assignment if proxy not found
				assignable.Assign(minionIdentity);
				DebugConsole.Log($"[AssignmentPacket] Assigned {assignable.name} to {minionIdentity.name}");
				return;
			}

			// Try StoredMinionIdentity (for frozen duplicants, etc.)
			var storedIdentity = dupeIdentity.gameObject.GetComponent<StoredMinionIdentity>();
			if (storedIdentity != null)
			{
				assignable.Assign(storedIdentity);
				DebugConsole.Log($"[AssignmentPacket] Assigned {assignable.name} to stored minion");
				return;
			}

			DebugConsole.LogWarning($"[AssignmentPacket] Could not find assignable identity on NetId {AssigneeNetId}");
		}
	}
}
