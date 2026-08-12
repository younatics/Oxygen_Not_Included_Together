using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.DuplicantActions;
using Shared.Profiling;

namespace ONI_Together.Patches.World
{
	/// <summary>
	/// Announce a duplicant's death, because nothing did.
	///
	/// The colonist counts drift apart and stay apart: the host buries someone and
	/// the client keeps them, because the only thing that could kill a client's
	/// duplicant was health arriving through VitalStatsPacket, and that is precisely
	/// what fails around a death - 39 exceptions in the twenty seconds either side
	/// of one, writing amounts onto a duplicant that no longer had them.
	///
	/// Patched at DeathMonitor.Instance.Kill rather than by subscribing to the Died
	/// event on every duplicant. A subscription per duplicant is a subscription to
	/// unsubscribe, and the last leak of that shape in this codebase - two handlers
	/// added per navigation packet, never removed - grew without bound for a whole
	/// session. One patch has nothing to clean up.
	///
	/// If the target method ever moves, this class fails to apply on its own and
	/// says so by name in the log; the rest of the mod is unaffected.
	/// </summary>
	[HarmonyPatch(typeof(DeathMonitor.Instance), nameof(DeathMonitor.Instance.Kill))]
	public static class DuplicantDeathPatch
	{
		public static void Postfix(DeathMonitor.Instance __instance, Death death)
		{
			using var _ = Profiler.Scope();

			try
			{
				if (!MultiplayerSession.InSession || !MultiplayerSession.IsHost)
					return;

				// Do not announce a death that arrived from the network. The client
				// never sends these, but an echo would be announced forever.
				if (DuplicantDeathPacket.Applying)
					return;

				var go = __instance?.gameObject;
				if (go.IsNullOrDestroyed())
					return;

				// Duplicants only. Critters die constantly and their own paths cover
				// them; this is about the colonist count.
				if (!go.HasTag(GameTags.BaseMinion))
					return;

				int netId = go.GetExistingNetIdentity() is var id && !id.IsNullOrDestroyed() ? id.NetId : 0;
				if (netId == 0)
				{
					DebugConsole.LogWarning(
						$"[DuplicantDeath] '{go.GetProperName()}' died with no NetId, so the clients " +
						"cannot be told which duplicant it was and their counts will stay wrong");
					return;
				}

				PacketSender.SendToAllClients(
					new DuplicantDeathPacket(netId, death?.Id ?? string.Empty),
					PacketSendMode.Reliable);

				DebugConsole.Log(
					$"[DuplicantDeath] announced the death of NetId {netId} " +
					$"('{go.GetProperName()}', {death?.Id ?? "unknown cause"})");
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[DuplicantDeathPatch] {ex}");
			}
		}
	}
}
