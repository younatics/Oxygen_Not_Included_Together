using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;

namespace ONI_Together.Patches.World
{
	[HarmonyPatch(typeof(Prioritizable), "SetMasterPriority")]
	public static class PrioritizablePatch
	{
		public static void Postfix(Prioritizable __instance, PrioritySetting priority)
		{
			using var _ = Profiler.Scope();

			if (PrioritizeStatePacket.IsApplying) return;
			if (!MultiplayerSession.InSession) return;

			// Find NetId
			//
			// This tested the wrong thing. The sentinel for "no identity component"
			// was -1, and the check refused only that - so an object that HAS an
			// identity but has not been given an id yet passed straight through with
			// netId 0 and the packet went out addressed to nothing. A single run
			// measured 179 packets arriving with NetId 0 and 176 of them came from
			// here; the receiver cannot resolve any of them, and each one is a
			// warning on the far side.
			//
			// Zero is not an address. Refuse it, and say which prefab could not be
			// named - the packet is a symptom, the unnamed object is the cause, and
			// dropping it quietly would hide the second one.
			var identity = __instance.gameObject.GetExistingNetIdentity();
			int netId = identity.IsNullOrDestroyed() ? 0 : identity.NetId;

			if (netId == 0)
			{
				_unaddressable++;
				DebugTools.ThrottledLog.Warn(
					$"[Prioritizable] not sending a priority change for " +
					$"'{__instance.gameObject.PrefabID()}': it has no NetId, so nothing could apply it");
			}
			else
			{
				var packet = new PrioritizeStatePacket();
				packet.Priorities.Add(new PrioritizeStatePacket.PriorityData
				{
					NetId = netId,
					PriorityClass = (int)priority.priority_class,
					PriorityValue = priority.priority_value
				});

				if (MultiplayerSession.IsHost)
					PacketSender.SendToAllClients(packet);
				else
					PacketSender.SendToHost(packet);
			}
		}

		/// <summary>
		/// Priority changes dropped because the object had no id. Exposed so a
		/// silent drop cannot read as "everything replicated".
		/// </summary>
		public static int Unaddressable => _unaddressable;

		private static int _unaddressable;
	}
}
