using System.Collections.Generic;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.KleiPatches
{
	/// <summary>
	/// Says who takes an item out of a container on a client.
	///
	/// One container has disagreed on every run of this investigation - a MicrobeMusher
	/// holding BasicPlantFood on the host and none on the client - and five explanations
	/// have now been eliminated by counters rather than argument:
	///
	///   the hash round trip      GetHash and GetHashCode read the same field
	///   the prefab lookup        storeNoPrefab 0, storeMade 44 to 68
	///   the container refusing   storeRefused 0
	///   the ingredient dump      blocked, fired 36 to 61 a run, no change
	///   the order machinery      blocked, fired 248 times, no change
	///
	/// And the packet demonstrably lands: storeNotLanded is 0, checked by reading the
	/// container back the instant after applying. So the item is put in correctly and
	/// something takes it out afterwards, and naming a sixth candidate would be a sixth
	/// guess after five wrong ones.
	///
	/// This is the same instrument that settled where client-side objects were coming
	/// from - KInstantiatePatch attributes creation by walking the stack - pointed at
	/// removal instead. It answers with a caller rather than a hypothesis.
	///
	/// Diagnostic only: it never changes what happens. One line per prefab per session,
	/// because per-cell logging at storage rates is what once froze a host.
	/// </summary>
	[HarmonyPatch(typeof(Storage), nameof(Storage.Remove), new[] { typeof(GameObject), typeof(bool) })]
	public static class StorageRemovePatch
	{
		private static readonly HashSet<string> _logged = new HashSet<string>();

		/// <summary>Removals attributed, so a silent zero is distinguishable.</summary>
		public static int RemovalsSeen { get; private set; }

		public static void Prefix(Storage __instance, GameObject go)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession || !MultiplayerSession.IsClient) return;
			if (__instance.IsNullOrDestroyed() || go.IsNullOrDestroyed()) return;

			RemovalsSeen++;

			// Keyed by what came out of what, so the musher's food is one line and the
			// rest of the colony's traffic does not bury it.
			string key = $"{__instance.gameObject.PrefabID()}/{go.PrefabID()}";
			if (!_logged.Add(key)) return;

			var trace = new System.Diagnostics.StackTrace(1, false);
			var frames = new List<string>();
			for (int i = 0; i < trace.FrameCount && frames.Count < 6; i++)
			{
				var m = trace.GetFrame(i)?.GetMethod();
				if (m?.DeclaringType == null) continue;
				string owner = m.DeclaringType.Name;
				if (owner == nameof(StorageRemovePatch)) continue;
				frames.Add($"{owner}.{m.Name}");
			}

			DebugConsole.LogWarning(
				$"[StorageRemove] '{go.PrefabID()}' taken out of " +
				$"'{__instance.gameObject.PrefabID()}' on the client by: " +
				string.Join(" <- ", frames));
		}
	}
}
