using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Social;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;
using UnityEngine;
using static STRINGS.UI.CLUSTERMAP;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// Packet to spawn entities (duplicants or items) on clients with matching NetIds.
	/// Sent from host when an entity is spawned (e.g., from Telepad).
	/// </summary>
	public class TelepadEntitySpawnPacket : IPacket
	{
		public ImmigrantOptionEntry EntityData;
		public int NetId;

		// Position
		public Vector3 Pos;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(NetId);
			EntityData.Serialize(writer);
			writer.Write(Pos);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			NetId = reader.ReadInt32();
			EntityData = ImmigrantOptionEntry.Deserialize(reader);
			Pos = reader.ReadVector3();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			DebugConsole.Log($"[EntitySpawnPacket] OnDispatched called - NetId {NetId}, IsDuplicant={EntityData.IsDuplicant}, IsHost={MultiplayerSession.IsHost}");

			// Only clients should process this
			if (MultiplayerSession.IsHost) return;

			DebugConsole.Log($"[EntitySpawnPacket] Client: Received spawn for NetId {NetId}, IsDuplicant={EntityData.IsDuplicant}, ItemID: {EntityData.GetId()}");

			try
			{
				var deliverable = EntityData.ToGameDeliverable();
				if (deliverable is not MinionStartingStats)
				{
					///move care packages a bit to the left to be centered
					Pos.x -= 0.5f;
				}
				// Deliver instantiates a prefab, which runs OnPrefabInit against
				// the Grid. Without a world that is a divide by zero, and the
				// try/catch around this only turned it into a duplicant the
				// client silently never received.
				if (Grid.WidthInCells == 0)
				{
					DebugConsole.LogWarning("[EntitySpawnPacket] ignoring delivery: no world loaded yet");
					return;
				}

				GameObject entity = deliverable.Deliver(Pos);
				if (entity == null)
				{
					DebugConsole.LogWarning($"[EntitySpawnPacket] delivery produced nothing at {Pos}");
					return;
				}

				// A duplicant the renderer cannot draw must not be left standing.
				//
				// FaceGraph.ApplyShape reads the personality to build a face, and
				// with none it throws - inside SymbolOverrideController, inside
				// KAnimBatch.UpdateDirty, inside World.LateUpdate. That is the
				// render loop, so it throws every frame from then on and the game
				// does not recover. A live client logged 148 of them in the
				// seconds before it closed itself.
				//
				// Losing one duplicant is recoverable; the host still has it and
				// the next sync can bring it back. A dead render loop is not.
				if (EntityData.IsDuplicant &&
				    entity.TryGetComponent<MinionIdentity>(out var minionIdentity) &&
				    Db.Get().Personalities.Get(minionIdentity.personalityResourceId) == null)
				{
					DebugConsole.LogError(
						$"[EntitySpawnPacket] '{EntityData.Name}' arrived with no resolvable personality " +
						"and was discarded - drawing it would throw out of World.LateUpdate every frame");
					Util.KDestroyGameObject(entity);
					return;
				}

				///duplicants from the printer are assigned an extra skill point, this is skipped over with a direct delivery
				if (entity.TryGetComponent<MinionResume>(out var res))
					res.ForceAddSkillPoint();

				NetworkIdentity identity = entity.AddOrGet<NetworkIdentity>();
				identity.OverrideNetId(NetId);
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[EntitySpawnPacket] Failed to spawn: {ex}");
			}
		}
	}
}
