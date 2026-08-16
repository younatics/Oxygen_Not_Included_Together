using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.World.Suits
{
	/// <summary>
	/// Telling the client that a suit changed hands.
	///
	/// SuitLocker.EquipTo and UnequipFrom are the game's own entry points, confirmed
	/// against Assembly-CSharp rather than guessed - the last three attempts in this
	/// repository that guessed at a game API name were all wrong, and one of them killed
	/// the client twice. The duplicant behind an Equipment is reached the same way the
	/// game reaches it, through MinionAssignablesProxy.
	/// </summary>
	[HarmonyPatch]
	internal static class SuitLockerPatches
	{
		public static int Announced { get; private set; }
		public static int NoMinionId { get; private set; }
		public static int NoLockerId { get; private set; }

		private static void Announce(SuitLocker locker, Equipment equipment, bool equip)
		{
			if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession) return;
			if (!MultiplayerSession.SessionHasPlayers) return;
			if (SuitEquipPacket.IsApplying) return;
			if (locker == null || equipment == null) return;

			if (!locker.gameObject.TryGetNetIdentity(out var lockerIdentity) || lockerIdentity.NetId == 0)
			{
				NoLockerId++;
				return;
			}

			// Equipment does not sit on the duplicant. It lives on the assignables proxy,
			// and the proxy is what knows which minion it belongs to - the same hop the
			// game makes internally. Reading it wrong would address the wrong duplicant on
			// the client, which is worse than not sending at all, so a failure to resolve
			// is counted and dropped.
			GameObject minion = equipment.GetComponent<MinionAssignablesProxy>()?.GetTargetGameObject();
			if (minion == null || !minion.TryGetNetIdentity(out var minionIdentity) || minionIdentity.NetId == 0)
			{
				NoMinionId++;
				return;
			}

			PacketSender.SendToAllClients(new SuitEquipPacket
			{
				LockerNetId = lockerIdentity.NetId,
				MinionNetId = minionIdentity.NetId,
				Equip = equip,
			});

			Announced++;
		}

		// The argument types are spelled out rather than left to name matching. An
		// ambiguous or missing target throws while Harmony is patching, and that does not
		// fail quietly for one method - it takes down every patch in the class with it.
		// That happened today to the cost instrumentation, where one abstract Update left
		// the whole set unmeasured and the numbers read as though the work had become free.
		[HarmonyPatch(typeof(SuitLocker), nameof(SuitLocker.EquipTo), new[] { typeof(Equipment) })]
		private static class SuitLocker_EquipTo_Patch
		{
			private static void Postfix(SuitLocker __instance, Equipment equipment)
			{
				using var _ = Profiler.Scope();

				Announce(__instance, equipment, equip: true);
			}
		}

		[HarmonyPatch(typeof(SuitLocker), nameof(SuitLocker.UnequipFrom), new[] { typeof(Equipment) })]
		private static class SuitLocker_UnequipFrom_Patch
		{
			private static void Postfix(SuitLocker __instance, Equipment equipment)
			{
				using var _ = Profiler.Scope();

				Announce(__instance, equipment, equip: false);
			}
		}
	}
}
