using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.World
{
	[HarmonyPatch(typeof(Substance), nameof(Substance.SpawnResource))]
	public static class Substance_SpawnResource_Patch
	{
		/// <summary>
		/// Loose matter a client made for itself, not because a packet asked for it.
		///
		/// The last unpaired objects between the two peers are each simulation's own
		/// output - the client's copies come from LiquidSourceManager.CreateChunk and
		/// from Substance.SpawnResource under Game.StepTheSim - and the obvious remedy
		/// is the one the dig path already uses: let the host own the matter and have
		/// the client wait to be told.
		///
		/// It is also the remedy that can delete a colony's water if it is wrong, so the
		/// size of it gets measured before anything is blocked. Thirty a run is a
		/// proportionate thing to suppress; thousands would mean this is the client's
		/// entire liquid and gas simulation and blocking it would be reckless.
		///
		/// Counted, not blocked. The next run turns this into a decision.
		/// </summary>
		public static int ClientSpawnedLocally { get; private set; }
		public static int ClientSpawnedForPacket { get; private set; }

		/// <summary>
		/// Set while one of this mod's packet handlers is creating matter the host asked
		/// for. Those calls are the whole point and must never be counted as local.
		/// </summary>
		private sealed class HostAuthoredScope : System.IDisposable
		{
			public void Dispose() => _hostAuthoredDepth = System.Math.Max(0, _hostAuthoredDepth - 1);
		}

		private static int _hostAuthoredDepth;

		internal static System.IDisposable HostAuthored()
		{
			_hostAuthoredDepth++;
			return new HostAuthoredScope();
		}

		/// <summary>
		/// The host owns loose matter, so a client does not make its own.
		///
		/// This is the rule WorldDamagePatch already applies to dug ore, in the same
		/// words: a client that spawns its own copy ends up with two, and the local one
		/// carries an id the host never issued, so every packet addressed to the real
		/// one is a failed lookup.
		///
		/// Extended here only after the size was measured, because returning null into
		/// the game's own code is the kind of change that empties a colony's water if it
		/// is wrong. It is 25 objects a run against 21 the host asked for - the same
		/// order as the 28 unpaired items the comparison reports - so it is a
		/// proportionate thing to suppress rather than the client's entire liquid and
		/// gas simulation.
		///
		/// Nothing is lost. The host's simulation makes the same matter, announces it,
		/// and the client builds it from that announcement holding the host's id.
		///
		/// Tried, measured, reverted - and the measurement is the point of writing it
		/// down. Refusing the spawn produced 12,124 errors on the client in one run:
		/// the game's own callers dereference what SpawnResource returns and cannot take
		/// a null. The dig path gets away with the same rule because it owns its call
		/// site and simply does not call it; this one is reached from inside the
		/// simulation, where there is no such choice.
		///
		/// So the residue stays, and it is small and understood: about 25 loose objects
		/// a run that each peer's simulation made for itself, out of some nine thousand.
		/// Closing it needs the client's copy to be matched to the host's announcement
		/// rather than either copy being suppressed, and the adoption measurements say
		/// that is not a matter of search radius - of 215 failed adoptions, 133 had no
		/// candidate of that prefab anywhere on the map and 68 had one only far away.
		/// </summary>
		public static void Prefix()
		{
			if (!MultiplayerSession.IsClient || !MultiplayerSession.InSession) return;

			if (_hostAuthoredDepth > 0) ClientSpawnedForPacket++;
			else ClientSpawnedLocally++;
		}

		public static void Postfix(GameObject __result)
		{
			using var _ = Profiler.Scope();

			if (__result == null)
				return;

			NetworkIdentity identity = __result.AddOrGet<NetworkIdentity>();
			identity.RegisterIdentity();
		}
	}

}
