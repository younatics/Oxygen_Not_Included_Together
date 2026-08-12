using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.World
{
	public static class BuildingConfigPatch
	{
		// Use flag from packet to prevent loops
		private static bool IgnoreEvents => BuildingConfigPacket.IsApplyingPacket;

		// Sync Logic Switches (User Toggles) - patch LogicSwitch.Toggle()
		// Toggle is called by ToggledByPlayer() when the player clicks the switch
		[HarmonyPatch(typeof(LogicSwitch), "Toggle")]
		public static class LogicSwitchTogglePatch
		{
			public static void Postfix(LogicSwitch __instance)
			{
				using var _ = Profiler.Scope();

				try
				{
					DebugConsole.Log($"[LogicSwitch] Toggle Postfix called on {__instance?.name ?? "null"}");

					if (IgnoreEvents)
					{
						DebugConsole.Log($"[LogicSwitch] Ignoring sync - IsApplyingPacket=true");
						return;
					}
					if (!MultiplayerSession.InSession)
					{
						DebugConsole.Log($"[LogicSwitch] Not in session, skipping");
						return;
					}

					// Read the current state after toggle was applied
					// Use Traverse to access the protected/private switchedOn field
					bool switchedOn = Traverse.Create(__instance).Field("switchedOn").GetValue<bool>();
					DebugConsole.Log($"[LogicSwitch] switchedOn = {switchedOn}");

					var identity = __instance.gameObject.AddOrGet<NetworkIdentity>();
					identity.RegisterIdentity();

					// Registering can legitimately fail to produce an id - the grid is
					// not ready, or every candidate slot is taken - and sending zero
					// puts a packet on the wire that no peer can apply.
					if (identity.NetId == 0)
					{
						DebugTools.ThrottledLog.Warn(
							$"[LogicSwitch] not sending state for '{__instance.gameObject.PrefabID()}': " +
							"registration produced no NetId");
						return;
					}

					var packet = new BuildingConfigPacket
					{
						NetId = identity.NetId,
						Cell = Grid.PosToCell(__instance.gameObject),
						ConfigHash = "LogicSwitchState".GetHashCode(),
						Value = switchedOn ? 1f : 0f,
						ConfigType = BuildingConfigType.Boolean
					};

					DebugConsole.Log($"[LogicSwitch] Sending state={switchedOn} for {__instance.name} (NetId={identity.NetId})");

					if (MultiplayerSession.IsHost)
						PacketSender.SendToAllClients(packet);
					else
						PacketSender.SendToHost(packet);
				}
				catch (System.Exception ex)
				{
					DebugConsole.Log($"[LogicSwitch] ERROR in Postfix: {ex.Message}");
				}
			}
		}

		// Sync Valve Flow
		[HarmonyPatch(typeof(Valve), "ChangeFlow")]
		public static class ValvePatch
		{
			public static void Postfix(Valve __instance, float amount)
			{
				using var _ = Profiler.Scope();

				if (IgnoreEvents) return;
				SyncBuildingConfig(__instance, "Rate", amount);
			}
		}

		/*
		// Sync Slider Side Screen (Batteries, Sensors, etc.)
		// [HarmonyPatch(typeof(SingleSliderSideScreen), "OnRelease")] // Method not found
		public static class SingleSliderSideScreenPatch
		{
				public static void Postfix(SingleSliderSideScreen __instance)
				{
						// ... implementation ...
				}
		}
		*/

		// Helper
		private static void SyncBuildingConfig(Component component, string configId, float value)
		{
			using var _ = Profiler.Scope();

			if (component == null) return;
			if (!MultiplayerSession.InSession) return;

			// Get NetId
			var identity = component.GetComponent<NetworkIdentity>()
						   ?? component.GetComponentInParent<NetworkIdentity>();
			int netId = identity.IsNullOrDestroyed() ? 0 : identity.NetId;

			// The same wrong sentinel as the priority patch had: -1 meant "no
			// identity component", and refusing only that let an object that HAS an
			// identity but no id yet send zero. Zero is not an address - the receiver
			// cannot resolve it, and its cell fallback then looks at cell 0, which is
			// a real cell in the corner of the map.
			//
			// A live client logged 12 refusals of exactly this shape
			// ("refusing to file 'GasPumpUnderConstruction' under NetId 0").
			if (netId == 0)
			{
				DebugTools.ThrottledLog.Warn(
					$"[BuildingConfig] not sending '{configId}' for " +
					$"'{component.gameObject.PrefabID()}': it has no NetId, so no peer could apply it");
				return;
			}

			{
				var packet = new BuildingConfigPacket
				{
					NetId = netId,
					// Was never set, so every one of these arrived claiming cell 0 and
					// the receiver's cell fallback aimed there.
					Cell = Grid.PosToCell(component.gameObject),
					ConfigHash = configId.GetHashCode(),
					Value = value
				};

				// If Host, broadcast to all.
				// If Client, send to Host.
				if (MultiplayerSession.IsHost)
				{
					PacketSender.SendToAllClients(packet);
				}
				else
				{
					PacketSender.SendToHost(packet);
				}
			}
		}
	}
}
