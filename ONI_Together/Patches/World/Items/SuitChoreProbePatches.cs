using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using Shared.Profiling;
using System.Collections.Generic;

namespace ONI_Together.Patches.World.Items
{
	/// <summary>
	/// Who raises a suit's priority reference count on a client, and never lowers it.
	///
	/// The last visible difference between the two peers is one row:
	///
	///     chore|Atmo_Suit#1776225778|waiting  host=0 client=1
	///
	/// `waiting` is Prioritizable.IsPrioritizable(), which is refCount > 0, so the client
	/// holds an errand against that suit that the host's duplicant has already taken.
	///
	/// One fix was already tried against this row and reverted. It cancelled the locker's
	/// ReturnSuitWorkable after replaying an equip, on the reasoning that EquipTo's last
	/// line is returnSuitWorkable.CreateChore(). It fired every run and the row did not
	/// move, because the row is keyed on the suit ITEM and the chore cancelled belonged to
	/// the LOCKER - two different objects, and the step of checking whose refCount it was
	/// had been skipped.
	///
	/// So this measures instead of guessing. It is a probe, not a fix: it prints the caller
	/// the first time each suit's count goes up on a client, and counts up and down so the
	/// row can be read against a number rather than against a hypothesis.
	///
	/// Cheap by construction - the prefab test is only reached in a session, on a client,
	/// and the stack is walked once per object.
	/// </summary>
	[HarmonyPatch]
	internal static class SuitChoreProbePatches
	{
		public static int SuitRefsAdded { get; private set; }
		public static int SuitRefsRemoved { get; private set; }

		private static readonly HashSet<int> _reported = new HashSet<int>();

		private static bool IsSuitOnClient(Prioritizable prioritizable)
		{
			if (!MultiplayerSession.InSession || MultiplayerSession.IsHost) return false;
			if (prioritizable.IsNullOrDestroyed() || prioritizable.gameObject.IsNullOrDestroyed()) return false;
			return prioritizable.gameObject.PrefabID().Name == "Atmo_Suit";
		}

		[HarmonyPatch(typeof(Prioritizable), nameof(Prioritizable.AddRef), new System.Type[0])]
		private static class Prioritizable_AddRef_Patch
		{
			private static void Postfix(Prioritizable __instance)
			{
				using var _ = Profiler.Scope();

				if (!IsSuitOnClient(__instance)) return;

				SuitRefsAdded++;

				// Once per object. The same technique that found the Creature piles and the
				// unnamed ground items: a count says how much, only the frames say who.
				int id = __instance.gameObject.GetInstanceID();
				if (!_reported.Add(id)) return;

				var trace = new System.Diagnostics.StackTrace(1, false);
				var frames = new List<string>();
				for (int i = 0; i < trace.FrameCount && frames.Count < 8; i++)
				{
					var m = trace.GetFrame(i)?.GetMethod();
					if (m?.DeclaringType == null) continue;
					string owner = m.DeclaringType.Name;
					if (owner == nameof(SuitChoreProbePatches) || owner.StartsWith("Prioritizable_")) continue;
					frames.Add($"{owner}.{m.Name}");
				}

				DebugConsole.LogWarning(
					$"[SuitChore] a client raised the errand count on '{__instance.gameObject.name}' " +
					$"(refCount now {__instance.refCount}), from: " + string.Join(" <- ", frames));
			}
		}

		[HarmonyPatch(typeof(Prioritizable), nameof(Prioritizable.RemoveRef), new System.Type[0])]
		private static class Prioritizable_RemoveRef_Patch
		{
			private static void Postfix(Prioritizable __instance)
			{
				using var _ = Profiler.Scope();

				if (!IsSuitOnClient(__instance)) return;

				SuitRefsRemoved++;
			}
		}
	}
}
