using ONI_Together.DebugTools;
using ONI_Together.Misc.World;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Trackers;
using ONI_Together.Patches.Navigation;

namespace ONI_Together.Networking
{
	/// <summary>
	/// Everything that must not outlive a session, in one place.
	///
	/// A mod like this is full of static state - registries, subscription sets,
	/// in-flight transfers, "am I busy" flags - and a session boundary is not a
	/// process boundary. A player disconnects and rejoins, and the host runs a
	/// hard sync several times an evening, tearing the world down and building
	/// it again each time. Anything left behind produces a bug that only appears
	/// on the second or third session, which is the hardest kind to reproduce
	/// and the easiest to blame on something else.
	///
	/// The worst of what was found here: a hard sync interrupted mid-flight left
	/// its "in progress" flag set, and every later hard sync in that process
	/// refused to start, logging only "already in progress". A partial save
	/// download stayed keyed by file name, so the next sync merged fresh chunks
	/// into the previous session's half-finished buffer. Subscription sets kept
	/// net ids that, after the next load, belonged to entirely different objects.
	///
	/// Each step is guarded on its own, because one type failing to resolve must
	/// not stop the rest of the cleanup.
	/// </summary>
	public static class SessionTeardown
	{
		public static void ClearAll()
		{
			Step("hard sync", GameServerHardSync.ResetForNewSession);
			Step("save assembler", SaveChunkAssembler.ResetForNewSession);
			Step("packet handler", PacketHandler.ResetForNewSession);
			Step("packet sender", PacketSender.ResetForNewSession);
			Step("identity statics", NetworkIdentity.ResetForNewSession);
			Step("anim syncers", AnimSyncCoordinator.PruneDestroyed);
			Step("status subscriptions", StatusBroadcaster.ResetForNewSession);
			Step("chore subscriptions", DuplicantChoreBroadcaster.ResetForNewSession);
			Step("navigator overrides", NavigatorExtensions.ResetForNewSession);
			// Dummy move targets and the navigator subscriptions that drive them.
			// One per moving duplicant now, but they are scene objects and live
			// handlers, and leaving them across sessions is what made the previous
			// version degrade the longer it ran.
			Step("navigator paths", Packets.Core.NavigatorPathPacket.ResetForNewSession);
			// Session state, not world state: this records what clients have been
			// told about each building's damage, and a new client has been told
			// nothing. Keeping it would leave the next session convinced it had
			// already sent damage that the new peer never received - the same
			// shape of bug as recording a send that never happened.
			Step("building damage", ClearDamageMemory);
			Step("damage packet counters", BuildingDamagePacket.ResetForNewSession);
			Step("removal packet counters", Packets.World.BuildingRemovedPacket.ResetForNewSession);
			Step("spawn-naming counters", Packets.World.BuildingSpawnedPacket.ResetForNewSession);
			Step("death packet counters", Packets.DuplicantActions.DuplicantDeathPacket.ResetForNewSession);
			Step("plant naming attempts", Components.PlantGrowthSyncer.ResetIdentityAttempts);
			Step("preview index", NetworkIdentity.ResetPreviewIndex);
			Step("rename refusals", NetworkIdentity.ResetRenameRefusals);
			Step("instantiation adoptions", Packets.InstantiationsPacket.ResetForNewSession);
			Step("building lifecycle counters", Patches.World.BuildingLifecycleWatch.Reset);
			Step("malformed-count counter", Packets.Architecture.PacketList.ResetForNewSession);
			Step("payload size records", PacketSender.ResetPayloadSizes);
			Step("missing-entity queue", ClearResolverQueue);
			Step("damage watcher", ClearDamageWatcher);
			// Pruned, never cleared. These track WORLD objects, and the world
			// outlives the session - hosting starts by calling Clear() with a
			// colony fully loaded, and nothing re-adds a plant that is already
			// standing because they only register on Growing.OnSpawn.
			//
			// Wiping them was a real and destructive mistake: the host's plant
			// sweep went out empty, 22 packets of nothing but header, and the
			// client reconciles that sweep by absence - so it destroyed 294 of
			// its own plants as phantoms. Session state and world state are not
			// the same thing, and only the first belongs here.
			Step("world trackers", PruneWorldTrackers);
		}

		private static void ClearDamageWatcher()
		{
			var watcher = ClientDamageWatcher.Instance;
			if (watcher.IsNullOrDestroyed()) return;
			watcher.ResetForNewSession();
		}

		private static void ClearResolverQueue()
		{
			var resolver = MissingEntityResolver.Instance;
			if (resolver.IsNullOrDestroyed()) return;
			resolver.ResetForNewSession();
		}

		private static void ClearDamageMemory()
		{
			var syncer = BuildingDamageSyncer.Instance;
			if (syncer.IsNullOrDestroyed()) return;
			syncer.Reset();
		}

		/// <summary>
		/// Drop entries whose objects are gone, keep the ones still standing. A
		/// destroyed Unity object stays in a HashSet because the set keys on the
		/// reference, not on the fake-null the == operator reports.
		/// </summary>
		private static void PruneWorldTrackers()
		{
			MopTracker.MopPlacers.RemoveWhere(go => go.IsNullOrDestroyed());
			DisinfectTracker.Disinfectables.RemoveWhere(d => d.IsNullOrDestroyed());
			lock (PlantTracker.AllPlants)
			{
				PlantTracker.AllPlants.RemoveWhere(g => g.IsNullOrDestroyed());
			}
		}

		private static void Step(string what, System.Action action)
		{
			try { action(); }
			catch (System.Exception ex)
			{
				DebugConsole.LogWarning($"[SessionTeardown] could not clear {what}: {ex.Message}");
			}
		}
	}
}


