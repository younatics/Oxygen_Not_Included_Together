using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using Shared.Profiling;

namespace ONI_Together.Patches.Critters
{
	/// <summary>
	/// An egg hatches on the host, and only on the host.
	///
	/// The client's AI is switched off, so nothing it does was expected to create
	/// creatures - but incubation is not a chore. It is a state machine on the egg
	/// itself, it keeps running, and creation-time attribution on a live client
	/// caught it: "PacuBaby created on the client by IncubationMonitor.SpawnBaby
	/// &lt;- GenericInstance.ExecuteActions". Both peers hatch the same egg and each
	/// gets its own baby, with its own id that the other peer never issued.
	///
	/// This is the same shape as the ore that WorldDamagePatch stopped the client
	/// spawning, and it goes wrong the same way: the host announces its baby, the
	/// client already has one standing there, and from then on every packet about
	/// the real fish addresses an object this peer does not have.
	///
	/// Safe to block outright, which is why this path was taken first of the four
	/// the attribution named. SpawnBaby returns void, so there is no result a
	/// caller could dereference - EntitySplitter.Split and ComplexFabricator's
	/// product spawn both hand back an object that callers use, and blocking those
	/// the same way would trade a duplicate for a crash.
	///
	/// The host's baby reaches the client the way every other creature does, through
	/// CreatureSpawnPatch - which already knows about eggs.
	/// </summary>
	[HarmonyPatch(typeof(IncubationMonitor), nameof(IncubationMonitor.SpawnBaby))]
	public static class IncubationMonitor_SpawnBaby_Patch
	{
		/// <summary>Hatches suppressed on this client, reported in the health row.</summary>
		public static int Suppressed { get; private set; }

		public static bool Prefix()
		{
			using var _ = Profiler.Scope();

			// Outside a session this peer is playing alone and its own eggs are the only
			// ones there are.
			//
			// Except during a reconnect, which is not playing alone - it is a gap in the
			// middle of a session. A three-run batch shows it: the two ordinary runs end
			// with the peers holding the same 77 critters, and the reconnect run leaves
			// the client holding 81. The four extra are babies, and the client's log has
			// the sequence in nine milliseconds - the host's baby arrives and is named by
			// InstantiationsPacket, and then "client drew 'HatchHardBaby' with no id".
			// Both peers hatched the same egg because for that window this peer did not
			// consider itself in a session.
			//
			// HasCachedConnection is the difference between the two cases, and it is
			// already what the reconnect path itself uses to decide whether to rejoin
			// after the world finishes loading. A peer with a connection cached is coming
			// back; a peer without one is on its own.
			// A host always hatches its own, first and unconditionally. Written as its
			// own line rather than folded into a compound condition, because the
			// compound version admitted a host that had cached a connection earlier in
			// the process and would have stopped its eggs from hatching at all.
			if (MultiplayerSession.IsHost)
				return true;

			if (MultiplayerSession.IsClient && MultiplayerSession.InSession)
			{
				Suppressed++;
				return false;
			}

			// Between sessions with somewhere to go back to: the reconnect case.
			if (GameClient.HasCachedConnection())
			{
				Suppressed++;
				return false;
			}

			// Genuinely alone.
			return true;

			Suppressed++;
			return false;
		}
	}
}
