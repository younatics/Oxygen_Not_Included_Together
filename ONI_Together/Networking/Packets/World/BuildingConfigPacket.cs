using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World.Handlers;
using ONI_Together.DebugTools;
using System.IO;
using UnityEngine;
using HarmonyLib;
using Shared.Profiling;
using System.Security.Principal;
using System.Threading.Tasks;
using ONI_Together.Misc;

namespace ONI_Together.Networking.Packets.World
{
	public enum BuildingConfigType : byte
	{
		Float = 0,      // Standard float value (valve flow, thresholds)
		Boolean = 1,    // Checkbox values
		SliderIndex = 2, // Slider with index (for multi-slider controls)
		RecipeQueue = 3, // Fabricator recipe queue (ConfigHash = recipe ID hash, Value = count)
		String = 4       // String value (tag names, text fields)
	}

	public class BuildingConfigPacket : IPacket
	{
		/// <summary>
		/// Cell-fallback renames refused because the building there already answers to a
		/// different number. Each one is a pair of objects that would have started
		/// meaning the same thing on this peer and different things across the session.
		/// </summary>
		public static int RenamesRefusedByCell { get; private set; }

		private ulong Sender; // Who triggered this
		public int NetId;
		public int Cell; // Deterministic location-based identification
		public int ConfigHash; // Hash of the property name (e.g. "Threshold", "Logic")
		public float Value;
		public BuildingConfigType ConfigType = BuildingConfigType.Float;
		public int SliderIndex = 0; // For ISliderControl multi-sliders
		public string StringValue = ""; // For tag names and text fields

		public static bool IsApplyingPacket = false;
        
		// Delay refreshing because things like storage lockers cause lag
		private static float _lastRefreshTime = -999f;
        private const float REFRESH_COOLDOWN = 0.1f; // ~30 frames at 60fps, consistent regardless of FPS

        public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			Sender = NetworkConfig.GetLocalID();
			writer.Write(Sender);
			writer.Write(NetId);
			writer.Write(Cell);
			writer.Write(ConfigHash);
			writer.Write(Value);
			writer.Write((byte)ConfigType);
			writer.Write(SliderIndex);
			writer.Write(StringValue ?? "");
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			Sender = reader.ReadUInt64();
			NetId = reader.ReadInt32();
			Cell = reader.ReadInt32();
			ConfigHash = reader.ReadInt32();
			Value = reader.ReadSingle();
			ConfigType = (BuildingConfigType)reader.ReadByte();
			SliderIndex = reader.ReadInt32();
			StringValue = reader.ReadString();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			// We sent this ignore
			if (Sender == NetworkConfig.GetLocalID())
				return;

			//DebugConsole.Log($"[BuildingConfigPacket] Received a config update packet. NetId={NetId}, Cell={Cell}");

			// An unset id cannot be resolved and must not be guessed at.
			//
			// The cell fallback below is a reasonable second try for a real id whose
			// object this peer has not registered. It is not a reasonable way to
			// handle zero: zero arrives with no cell either, so the fallback aimed at
			// cell 0 and would have named whatever stands in the corner of the map.
			// The senders no longer send it; this refuses it if one ever does again.
			if (NetId == 0)
			{
				DebugTools.ThrottledLog.Warn(
					"[BuildingConfigPacket] refusing a config change carrying NetId 0 - " +
					"the sender did not fill in the id, and there is nothing to apply it to");
				return;
			}

			if (!NetworkIdentityRegistry.TryGet(NetId, out var identity) || identity == null)
			{
				// Attempt to find building by cell
				if (Grid.IsValidCell(Cell))
				{
					// For multi-layered buildings, we might need a more specific search, but usually
					// we just look for BuildingComplete components.
					GameObject buildingGO = Grid.Objects[Cell, (int)ObjectLayer.Building];

					// A building that already answers to a different number is not this
					// one, and position is not a reason to rename it.
					//
					// The same fallback in AssignmentPacket did this and the cost showed
					// up in live play: eight removals refused on a client because the id
					// the host meant resolved to something else here - five of them a
					// SuitLocker, the rest a Ladder and Tiles. The refusals are the
					// safety net doing its job, and what it is catching is a rename that
					// happened earlier, here or somewhere like here.
					//
					// Once one number means two things the two peers cannot agree about
					// either object again, so this is checked before the rename rather
					// than repaired afterwards.
					if (buildingGO != null)
					{
						identity = buildingGO.GetComponent<NetworkIdentity>();
						if (identity != null && identity.NetId != 0 && identity.NetId != NetId)
						{
							RenamesRefusedByCell++;
							DebugConsole.LogWarning(
								$"[BuildingConfigPacket] not renaming '{buildingGO.PrefabID()}' at cell {Cell} " +
								$"from NetId {identity.NetId} to {NetId} - it is already addressed as " +
								"something else, so this packet is about a different object");
							return;
						}

						if (identity)
						{
							identity.OverrideNetId(NetId); // Override properly from the host
						}
						else
						{
							// Same route as the branch above. Writing the field and
							// then registering files the object under whatever it
							// hashes to, not under the id the host just named - the
							// field and the key drift apart and two objects end up
							// claiming one address. OverrideNetId moves both.
							identity = buildingGO.AddOrGet<NetworkIdentity>();
							identity.OverrideNetId(NetId);
						}

                        //DebugConsole.Log($"[BuildingConfigPacket] Resolved missing identity for {buildingGO.name} at cell {Cell}. Assigned NetId: {NetId}");
					}
				}
			}

			if (identity != null)
			{
				try
				{
					IsApplyingPacket = true;
					ApplyConfig(identity.gameObject);
				}
				finally
				{
                    RefreshSideScreenIfOpen(identity.gameObject);
                    Task.Run( async () =>
                    {
	                    await Task.Delay( 15 );
	                    IsApplyingPacket = false;
                    } );
				}

                // HOST RELAY: If host received this from a client, re-broadcast to all other clients
                if (MultiplayerSession.IsHost)
				{
					PacketSender.SendToAllClients(this);
					//DebugConsole.Log($"[BuildingConfigPacket] Host relayed config to all clients: NetId={NetId}, ConfigHash={ConfigHash}");
				}
			}
			else
			{
				DebugConsole.LogWarning($"[BuildingConfigPacket] FAILED to resolve entity for NetId {NetId} at Cell {Cell}");
			}
		}

        /// <summary>
        /// Applies the configuration to the target building.
        /// All handlers are now in the BuildingConfigHandlerRegistry.
        /// </summary>
        private void ApplyConfig(GameObject go)
		{
			using var _ = Profiler.Scope();

			if (go == null) return;

            // All handlers are now in the registry
            if (BuildingConfigHandlerRegistry.TryHandle(go, this))
			{
				DebugConsole.Log($"[BuildingConfigPacket] Handled by registry for {go.name}");
                return;
			}

			// Log unhandled configs for debugging
			DebugConsole.LogWarning($"[BuildingConfigPacket] Unhandled config: Hash={ConfigHash}, Type={ConfigType}, Value={Value}, String={StringValue} on {go.name}");
		}

        private void RefreshSideScreenIfOpen(GameObject go)
        {
            using var _ = Profiler.Scope();
            if (go == null) return;

            if (Time.unscaledTime - _lastRefreshTime < REFRESH_COOLDOWN) return;
            _lastRefreshTime = Time.unscaledTime;

            try
            {
                if (go.TryGetComponent<KSelectable>(out var selectable) && SelectTool.Instance.selected == selectable)
                {
                    SelectTool.Instance.Select(null, true);
                    SelectTool.Instance.Select(selectable, true);
                }
            }
            catch (System.Exception e)
            {
                DebugConsole.Log($"[BuildingConfigPacket] UI refresh failed: {e.Message}");
            }
        }
    }
}
