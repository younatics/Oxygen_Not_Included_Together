using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Core;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	/// <summary>
	/// States the size of everything this mod holds, once a minute, on both peers.
	///
	/// "It gets slower the longer we play" is the hardest report to act on, because
	/// by the time it is noticed the evidence is a feeling. Every measurement so
	/// far has been taken from outside the process - private bytes over a share -
	/// which says memory is growing but not which table is growing, and a colony
	/// legitimately grows too. The two are indistinguishable from outside.
	///
	/// So the tables report themselves. A row per minute turns a fourteen-minute
	/// session into a growth curve, and the one that climbs without bound names the
	/// leak. It is also the only evidence available when a client stops responding
	/// and Windows closes it: the last row before the silence says what was large.
	///
	/// Cheap on purpose - counts already maintained, no scans - because an
	/// instrument that costs frames while measuring slowness proves nothing.
	/// </summary>
	public class SessionHealthLog : MonoBehaviour
	{
		private const float IntervalSeconds = 60f;

		private float _next;
		private int _rows;

		/// <summary>Frame time, averaged over the interval rather than sampled once.</summary>
		private float _frameAccum;
		private int _frames;

		/// <summary>Last row's cumulative failure count, so this row can report its own share.</summary>
		private int _lastLookupFails;

		private float _worstFrame;
		private int _stutters;
		private int _hitches;

		/// <summary>Below 30 fps for that frame - the point where movement stops looking continuous.</summary>
		private const float StutterSeconds = 1f / 30f;

		/// <summary>A quarter of a second. Not a dropped frame, a pause the player notices.</summary>
		private const float HitchSeconds = 0.25f;

		private void Update()
		{
			using var _ = Profiler.Scope();

			// A loaded colony is measurable whether or not anyone is connected.
			//
			// Reporting only in-session left the most important number unmeasured: how
			// fast this colony runs with no multiplayer at all. Without it, "the host
			// is at 77 ms and the client at 16.7" reads as a hosting cost - and it
			// cannot, because the mod switches the client's AI off. The client is not
			// running the same simulation, so it is not a control.
			//
			// role=solo rows are that control. If a colony runs at 77 ms alone, there
			// is nothing here to fix; if it runs at 20, the difference is the thing to
			// find. Measured before optimising anything, because the two candidates
			// that looked obvious in the log turned out to be 0.8 ms a frame between
			// them.
			if (Game.Instance == null)
			{
				_next = Time.unscaledTime + IntervalSeconds;
				_frameAccum = 0f;
				_frames = 0;
				return;
			}

			// An average cannot answer "is it smooth". A colony at a steady 17 ms and
			// one that alternates 8 ms and 26 ms average the same, and only the second
			// one is unpleasant to play. The worst frame and the number of frames that
			// missed 30 fps say which of the two this is.
			float dt = Time.unscaledDeltaTime;
			_frameAccum += dt;
			_frames++;
			if (dt > _worstFrame) _worstFrame = dt;
			if (dt > StutterSeconds) _stutters++;
			if (dt > HitchSeconds) _hitches++;

			if (Time.unscaledTime < _next)
				return;
			_next = Time.unscaledTime + IntervalSeconds;

			// Kept before the reset, so the row can say what it was measured over.
			int frames0 = _frames;

			float msPerFrame = _frames > 0 ? (_frameAccum / _frames) * 1000f : 0f;
			float worstMs = _worstFrame * 1000f;
			int stutters = _stutters;
			int hitches = _hitches;
			_frameAccum = 0f;
			_frames = 0;
			_worstFrame = 0f;
			_stutters = 0;
			_hitches = 0;

			// Per-row deltas, because totals cannot be compared between builds.
			//
			// Failed lookups read 4-6 in one build and 3,943 in the next, and that was
			// taken as a hundredfold regression twice, with a wrong fix each time. In
			// the same pair of builds the host's frame time went from 76 ms to 17.6, so
			// the second build simulated roughly four times as much colony in the same
			// wall clock: four times the hauling, the digging and the packets, and
			// therefore four times the chances to miss. The rate may not have moved at
			// all.
			//
			// A total answers "how much has gone wrong since the session began", which
			// is not the question when comparing two runs of different speeds. The delta
			// per row, next to the frames it happened over, is.
			// Once a minute, on the row that will report it. Costs one
			// FindObjectsByType over Buildings; the sites it then walks are a handful.
			GhostSiteScan.Scan();

			// Once a minute is enough to notice a registry that has been emptied.
			//
			// A reconnect wipes the registry and nothing re-files the world, because
			// registration happens at spawn and on a reconnect nothing spawns. Rather
			// than hook one of the several join paths - the mistake that let the last
			// bug of this shape past ninety-one callers - this notices the state
			// directly: in a session, with a colony loaded, and almost nothing filed.
			//
			// The threshold is deliberately crude. A real colony files thousands; the
			// broken state measured 31 against 8085. Anything in between does not happen.
			if (MultiplayerSession.InSession
				&& NetworkIdentityRegistry.Count < 100
				&& UnityEngine.Object.FindObjectsByType<Components.NetworkIdentity>(
					   FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length > 100)
			{
				// Read before the sweep. Subtracting afterwards printed "-17 entries",
				// which is the kind of number that gets quoted back as a finding.
				int heldBefore = NetworkIdentityRegistry.Count;
				int reattached = NetworkIdentityRegistry.ReattachAll();
				DebugConsole.LogWarning(
					$"[REATTACH] the registry held {heldBefore} entries for a loaded colony - " +
					$"re-filed {reattached} object(s) under the ids they already had");
			}
			if (GhostSiteScan.GhostSites > 0)
			{
				DebugConsole.LogWarning(
					$"[GHOST] {GhostSiteScan.GhostSites} cell(s) hold a building and its own " +
					$"construction site at once: {string.Join(", ", GhostSiteScan.Examples)}");
			}

			int lookupFails = NetworkIdentityRegistry.LookupFailCount;
			int lookupFailsDelta = lookupFails - _lastLookupFails;
			_lastLookupFails = lookupFails;

			var watcher = ClientDamageWatcher.Instance;
			var resolver = MissingEntityResolver.Instance;

			DebugConsole.Log(
				$"[HEALTH] row={++_rows}" +
				$"|role={(!MultiplayerSession.InSession ? "solo" : MultiplayerSession.IsHost ? "host" : "client")}" +
				$"|frameMs={msPerFrame:0.0}" +
				// Smoothness, not throughput. worstMs is the longest single frame in
				// the minute; stutters are frames under 30 fps; hitches are pauses past
				// a quarter second, which is the kind a player reports as "it froze".
				$"|worstMs={worstMs:0}" +
				$"|stutters={stutters}" +
				$"|hitches={hitches}" +
				// The number the Latency test fails on, next to the two things that
				// could be producing it.
				//
				// 261 ms is not a LAN round trip, and reading it as one sent the
				// investigation nowhere three times. Riptide measures this at the
				// application layer: a heartbeat is answered inside the other peer's
				// game loop, so the host's 76 ms frame is inside every sample, and so
				// is whatever queueing 1,100 packets a second produces. Logged beside
				// frameMs and the send counts in cost[] so the next run says which -
				// if rttMs tracks the host's frame time it is not the network, and if
				// it tracks the send count it is ours to fix.
				$"|rttMs={(MultiplayerSession.InSession && !MultiplayerSession.IsHost && NetworkConfig.TransportClient != null ? NetworkConfig.TransportClient.GetPing() : -1)}" +
				$"|registry={NetworkIdentityRegistry.Count}" +
				$"|lookupFails={lookupFails}" +
				// This minute's share, and the frames it happened over. Comparable
				// between builds in a way the total is not.
				$"|lookupFailsPerRow={lookupFailsDelta}" +
				// Of those failures, how many were just early. What is left is the
				// half that never arrived - the only half worth chasing.
				$"|failsResolvedLater={NetworkIdentityRegistry.LookupFailsResolvedLater}" +
				// Distinct objects currently unaccounted for. Events say how loudly the
				// packets complain; this says how many things they are complaining about.
				$"|unresolvedIds={NetworkIdentityRegistry.UnresolvedIdCount}" +
				// Misses the design expects - removal notices for items this peer was
				// never told about. Separated so they stop inflating the number above.
				$"|expectedMisses={NetworkIdentityRegistry.ExpectedMisses}" +
				// Ids used and never reissued. Each one is a number the other peer might
				// still be holding for a dead object.
				$"|retiredIds={NetworkIdentityRegistry.RetiredIdCount}" +
				// Removals the id could not find and position could. Each one is a pile
				// the client used to keep forever after the host destroyed it.
				$"|removedByCell={Packets.World.GroundItemPickedUpPacket.MatchedByCell}" +
				$"|removalsUnmatched={Packets.World.GroundItemPickedUpPacket.Unmatched}" +
				$"|framesThisRow={frames0}" +
				$"|collisions={NetworkIdentityRegistry.CollisionCount}" +
				$"|fieldFixes={NetworkIdentityRegistry.FieldCorrections}" +
				$"|navPending={NavigatorPathPacket.PendingCount}" +
				$"|chunkSets={ChunkedPacket.PendingSetCount}" +
				$"|resolverQueue={(resolver.IsNullOrDestroyed() ? -1 : resolver.PendingCount)}" +
				$"|hpTracked={(watcher.IsNullOrDestroyed() ? -1 : watcher.TrackedCount)}" +
				$"|statusSubs={StatusBroadcaster.SubscribedNetIds.Count}" +
				$"|choreSubs={DuplicantChoreBroadcaster.SubscribedNetIds.Count}" +
				$"|plants={Trackers.PlantTracker.AllPlants.Count}" +
				// The colonist count, from the game's own live list. Reported by both
				// peers so "the duplicant totals do not match" stops being something
				// only a player looking at two screens can see: the last row of each
				// log answers it, with a timestamp.
				$"|dupes={(global::Components.LiveMinionIdentities == null ? -1 : global::Components.LiveMinionIdentities.Count)}" +
				$"|deathsApplied={Packets.DuplicantActions.DuplicantDeathPacket.Applied}" +
				// The number that says whether the host is naming what the client
				// draws. It was 154 created against 12 adopted, and every unadopted
				// one is an object nothing can address.
				$"|previews={NetworkIdentity.PreviewsCreated}/{NetworkIdentity.PreviewsAdopted}" +
				$"|namedOnSpawn={Patches.World.BuildingLifecycleWatch.Named}" +
				$"|unnamedOnSpawn={Patches.World.BuildingLifecycleWatch.Unnamed}" +
				$"|adoptedByPacket={Packets.World.BuildingSpawnedPacket.Adopted}" +
				// Whether an arriving announcement names what the client already drew
				// instead of adding a second object beside it. Read against previews:
				// if previews climb and this stays flat, the matching is not working.
				// Scaffolding refused an address - locators, placers, proxies, FX.
				// Reported because an exclusion that silently grows is how a real
				// object stops replicating without anyone noticing.
				$"|localOnly={NetworkIdentity.LocalOnlySkipped}" +
				// Gas and liquid chunks refused an address. Read against the cross-peer
				// disagreement count: those were all gases and liquids.
				$"|ephemeral={NetworkIdentity.EphemeralSkipped}" +
				// Senders that wanted the address of a refused object. Every one of
				// these is a packet carrying NetId 0, which lands on nothing. Read the
				// [RefusedAsk] lines for which objects and which senders.
				$"|refusedAsked={NetworkIdentity.AddressAskedAfterRefusal}" +
				// Storages given a syncer, and those left alone. Storage syncing used to
				// reach four hand-listed building types, which is why 96 containers held
				// different masses on the two peers. skipConduit is the pipe machinery
				// that must stay out of it.
				$"|storeSync={Patches.World.Storage_OnSpawn_AttachSyncer_Patch.Attached}" +
				$"|skipConduit={Patches.World.Storage_OnSpawn_AttachSyncer_Patch.SkippedConduit}" +
				// Storage corrections that changed masses versus ones that destroyed and
				// recreated every object in the container. Rebuilds far outnumbering
				// in-place corrections is the object churn that once produced 14655
				// spurious registrations - it means the match rule is broken, not that
				// the colony is busy.
				$"|storeInPlace={Misc.BuildingUtils.StorageUpdatedInPlace}" +
				$"|storeRebuild={Misc.BuildingUtils.StorageRebuilt}" +
				// Machine working buffers left alone. The sender never describes a
				// massless entry, so deleting one was this peer's pump losing what it
				// dispenses from.
				$"|storeKept={Misc.BuildingUtils.MasslessEntriesPreserved}" +
				// Keyframes: structure states sent because the clock came due, not
				// because anything changed. Zero on a client. The only thing that can
				// repair a container both peers have stopped touching.
				$"|resyncs={StructureStateSyncers.StructureSyncerBase.ResyncsForced}" +
				// Buildings holding more than one container - fabricators have three.
				// Only the first was ever replicated, which is why fabricators were the
				// last containers still disagreeing.
				$"|multiStore={StructureStateSyncers.StorageStateSyncer.MultiStorageBuildings}" +
				// Objects re-filed after a session began with a world already loaded.
				// Zero on a session that was never interrupted; on a reconnect it should
				// be most of the colony, and sweeps should stay at one per join.
				$"|reattached={NetworkIdentityRegistry.ReattachedOnJoin}" +
				$"|reattachSweeps={NetworkIdentityRegistry.ReattachSweeps}" +
				// Player-set building flags: how many buildings carry one, and how many
				// had drifted far enough that a keyframe had to correct them. Watched at
				// zero means the syncer is not attached and the number beside it means
				// nothing - the shape of four earlier misreadings.
				$"|flagWatched={StructureStateSyncers.BuildingFlagsSyncer.Watched}" +
				$"|flagRepaired={StructureStateSyncers.BuildingFlagsSyncer.FlagsRepaired}" +
				// Previews that took no id. Zero on a host; on a client it must track
				// previews, because a local object holding a number out of the shared
				// space is how one id came to mean two different things.
				$"|previewsNoId={NetworkIdentity.PreviewsWithoutId}" +
				// Ids a client refused to invent for objects the host never named.
				// Zero on a host. On a client this is the class of object that used to
				// take a name out of the host's space and mean something else with it.
				$"|mintsRefused={NetworkIdentity.ClientMintsRefused}" +
				// Renames refused because the object already had a name. Read beside
				// lookupFails: allowing these is what made one pile cost eight thousand.
				$"|adoptRefused={NetworkIdentity.AdoptionsRefusedNamed}" +
				// Scaffolds cleared, and scaffolds in the same cell that belong to
				// another building and were left alone. The second number is the one
				// that used to be destruction.
				$"|scaffoldsCleared={Packets.Tools.Build.BuildCompletePacket.LeftoverScaffoldsCleared}" +
				$"|scaffoldsSpared={Packets.Tools.Build.BuildCompletePacket.ScaffoldsLeftAlone}" +
				// Cells holding a finished building and that same building's unfinished
				// site at once - the reported "host built it, client says scheduled",
				// judged from this peer alone. sites= is the activity number beside it:
				// ghosts=0 with sites=0 means nothing was under construction, which is
				// how three earlier zeroes were misread as fixes.
				$"|ghostSites={GhostSiteScan.GhostSites}" +
				$"|ghostWorst={GhostSiteScan.GhostSitesWorst}" +
				$"|sites={GhostSiteScan.SitesScanned}" +
				// Convergences a client declined. Read against idMoves on the same row:
				// a client that still moves ids is still renaming what the host named.
				$"|convergesRefused={NetworkIdentity.ClientConvergesRefused}" +
				// Reservations refused to the wrong object. Every one of these would
				// have been an id that meant something else on the other peer.
				$"|resProtected={NetworkIdentity.ReservationsProtected}" +
				// Eggs the client did not hatch for itself. Expected to be zero on a
				// host and to match the host's hatch count on a client; a client that
				// stays at zero while critters appear means the block is not firing.
				$"|hatchesBlocked={Patches.Critters.IncubationMonitor_SpawnBaby_Patch.Suppressed}" +
				// Fabricator products the client did not make for itself. Zero on a
				// host; on a client it should track the host's production.
				$"|productsBlocked={Patches.World.Buildings.ComplexFabricator_Patches.ClientProductsBlocked}" +
				// Objects the host holds and this peer does not, and which cannot be
				// sent as loose items. Used to be silence, counted as a failed lookup
				// and indistinguishable from a lost packet.
				$"|heldNotSent={Packets.World.EntityUnknownPacket.HeldButNotSpawnableCount}" +
				$"|instAdopted={Packets.InstantiationsPacket.AdoptedInstead}" +
				$"|byCell={NetworkIdentity.PreviewsAdoptedByCell}" +
				$"|staleIndex={NetworkIdentity.PreviewsStale}" +
				// Expected to stay at zero. Anything else means a sender is still
				// announcing colonists as prefabs, which kills a client.
				$"|minionsRefused={Packets.InstantiationsPacket.RefusedMinions}" +
				// Zero once the naming loop is fixed. Rising means it still happens and
				// is only bounded.
				$"|renamesRefused={NetworkIdentity.RenamesRefused}" +
				// Four counters that existed and were read by nobody. Each was added
				// because the number matters and then never wired to anything, which
				// makes them worse than absent: the code looks instrumented and the
				// runs report nothing. Either they belong here or they should be
				// deleted, and each of these names a distinct way the ids go wrong.
				$"|idMoves={NetworkIdentity.IdChanges}" +
				$"|reassignments={NetworkIdentityRegistry.Reassignments}" +
				$"|plantIdsAbandoned={PlantGrowthSyncer.AbandonedIds}" +
				$"|prioritiesDropped={Patches.World.PrioritizablePatch.Unaddressable}" +
				// Priorities the game would have refused, corrected on arrival, and sends
				// where the tool menu had nothing to say. Both were silently zero before -
				// the receiver pushed a zero into its own priority screen and ran the tool.
				$"|prioFixed={PriorityWire.Corrected}" +
				$"|prioDefaulted={PriorityWire.SentDefault}" +
				$"|removalsApplied={Packets.World.BuildingRemovedPacket.Applied}" +
				$"|gcMB={System.GC.GetTotalMemory(false) / (1024 * 1024)}" +
				// Where the frame time goes, next to the frame time itself. Drained
				// each row, so this describes the minute above it.
				$"|cost[{DebugTools.SyncerCostWatch.DrainTop(8)}]" + $"|gap[{NetworkIdentity.DescribePreviewGap()}]");
		}
	}
}



