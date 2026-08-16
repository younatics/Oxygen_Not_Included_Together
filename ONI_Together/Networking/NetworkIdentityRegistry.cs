using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using System;
using System.Collections.Generic;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking
{
	public static class NetworkIdentityRegistry
	{
		private static readonly Dictionary<int, NetworkIdentity> identities = new Dictionary<int, NetworkIdentity>();
		private static readonly System.Random rng = new System.Random();

		private static int _lookupFailCount = 0;
		private static int _collisionCount = 0;
		private static float _lastFailLogTime = 0f;

		/// <summary>
		/// Ids handed from one live object to another. A high number is a thrash,
		/// not a sequence of unrelated events, so the total is the useful reading.
		/// </summary>
		private static int _reassignments = 0;
		public static int Reassignments => _reassignments;

		public static int Count => identities?.Count ?? 0;

		/// <summary>
		/// Read-only view of the failed-lookup counter so a test can assert on it.
		/// A packet addressed to a NetId this peer never registered is the visible
		/// end of a NetId disagreement, so this is the cheapest divergence gate
		/// there is - no second machine and no log parsing needed.
		/// </summary>
		public static int LookupFailCount => _lookupFailCount;

		/// <summary>
		/// Ids that failed a lookup and later resolved - the object was simply not
		/// here yet.
		///
		/// Read against LookupFailCount: what is left over is the half that never
		/// arrived, and that is the only half worth chasing.
		/// </summary>
		public static int LookupFailsResolvedLater => _lookupFailsResolvedLater;

		/// <summary>
		/// Distinct ids that have failed and not resolved since.
		///
		/// This is the number the split was for, and subtracting the two event counts
		/// did not produce it: one id failing eight thousand times counts eight
		/// thousand failures and at most a handful of resolutions, so the difference
		/// measured repetition rather than breadth. The first row of the first run
		/// came out at minus thirteen, which is what a wrong unit looks like.
		///
		/// Objects, not events. If this is small, the peers disagree about a handful
		/// of things however loudly the packets complain.
		/// </summary>
		public static int UnresolvedIdCount => _everFailed.Count;

		/// <summary>
		/// Every id currently unaccounted for, so the set can be compared against the
		/// host's table instead of guessed at.
		///
		/// The throttled log only prints a sample, and that sample is biased towards
		/// whatever fails most often - it said 63% of unresolved ids were objects the
		/// host no longer had, while the two registries differ by 113 out of 8,268.
		/// Those two cannot both describe the same thing, and the way to find out which
		/// is right is to dump the whole set rather than argue from the loud end of it.
		/// </summary>
		public static System.Collections.Generic.IEnumerable<int> UnresolvedIds => _everFailed;

		/// <summary>
		/// Misses the design expects, counted apart from the ones that mean something.
		///
		/// Spawns are culled to a client's viewport and removal notices are not, so a
		/// client is routinely told that an item it was never told about has been picked
		/// up. That is not a divergence - PendingRemovals already holds those notices for
		/// ten seconds in case the spawn is merely late, and drops them otherwise.
		///
		/// Counting them as failures buried the real number. Of a sample of unresolved
		/// ids, 63% were objects the host no longer had either, and the top two askers
		/// were the two removal packets. Meanwhile the registries differed by 113 out of
		/// 8,268 - which is the actual gap, twenty times smaller than the alarm.
		/// </summary>
		public static int ExpectedMisses => _expectedMisses;

		private static int _expectedMisses;

		/// <summary>
		/// Depth of "a miss here is normal" scopes. Nested so a handler can call
		/// another without either having to know.
		/// </summary>
		private static int _expectedMissDepth;

		public static System.IDisposable ExpectedMissScope() => new ExpectedMiss();

		private sealed class ExpectedMiss : System.IDisposable
		{
			public ExpectedMiss() { _expectedMissDepth++; }
			public void Dispose() { _expectedMissDepth--; }
		}

		private static int _lookupFailsResolvedLater;

		/// <summary>
		/// Ids seen to fail at least once, so a later success can be recognised.
		///
		/// Bounded by clearing with the session; it holds ids, not objects, so a
		/// stale entry costs one dictionary slot and can never destroy anything -
		/// unlike the pending-removal queues, where a stale entry used to kill the
		/// next object issued the same id.
		/// </summary>
		private static readonly System.Collections.Generic.HashSet<int> _everFailed =
			new System.Collections.Generic.HashSet<int>();

		/// <summary>
		/// Registrations refused because the id was already held by a different
		/// object. Each one is an object that exists but cannot be addressed.
		/// </summary>
		public static int CollisionCount => _collisionCount;

		public static int Register(NetworkIdentity entity)
		{
			using var _ = Profiler.Scope();

			int id, attempt = 0;
			do
			{
				id = Guid.NewGuid().GetHashCode() + attempt++;
			} while (identities.ContainsKey(id));

			identities[id] = entity;
			return id;
		}

		/// <summary>
		/// Free a slot, but only if <paramref name="owner"/> is the object that
		/// actually holds it.
		///
		/// This used to remove whatever sat under the id. Ids are not guaranteed
		/// unique per object, so a dying object could evict a live one that had
		/// been handed the same id - the live object then vanished from the
		/// registry while still on screen, and every packet addressed to it
		/// counted as a failed lookup. Checking the owner turns that into a
		/// no-op.
		/// </summary>
		public static void Unregister(int netId, NetworkIdentity owner)
		{
			using var _ = Profiler.Scope();

			if (identities.TryGetValue(netId, out var current) && !ReferenceEquals(current, owner))
				return;

			identities.Remove(netId);

			// Retired, not returned to the pool.
			//
			// The other peer may still be holding the object this id named. A live pair
			// showed the whole chain: the host dropped a resource as -885421119, told the
			// client, the drop was consumed, the id came free, and a MushBar then took it
			// - while the client still had the dead pile under that number. From then on
			// every packet about the host's food arrived addressed to the client's
			// rubble, and the id tables disagreed in a way no single-peer check can see.
			//
			// Two separate attempts to explain this by arithmetic were both wrong: the
			// walk was changed and the same ids came back, and a collision test over
			// every id every live object could take passes on both peers. It was never
			// about which number - it is about the number being handed out twice.
			//
			// Ids are 32-bit and a session retires a few thousand, so refusing to reuse
			// them costs a set and nothing else.
			if (netId != 0)
			{
				_retired.Add(netId);

				// Who had it, so the same object can take it back.
				//
				// Retiring ids outright was tried and reverted: objects unregister and
				// re-register during normal life - a cell change, a move into storage -
				// and refusing the id to its own former owner pushed them onto fresh
				// numbers, taking the host's collision count from 0 to 13.
				//
				// The dangerous case is narrower than "this id was used once". It is
				// "somebody else takes a number another peer still associates with the
				// dead object". Remembering the owner separates the two.
				if (owner != null && !owner.IsNullOrDestroyed())
					_retiredBy[netId] = owner.GetInstanceID();
				else
					_retiredBy.Remove(netId);
			}
		}

		/// <summary>
		/// Ids that have been used and freed. Never issued again this session.
		///
		/// Cleared with the session, along with the registry itself - a new session
		/// starts from a save both peers load identically, so nothing carries over.
		/// </summary>
		private static readonly System.Collections.Generic.HashSet<int> _retired =
			new System.Collections.Generic.HashSet<int>();

		public static int RetiredIdCount => _retired.Count;

		/// <summary>
		/// Failed lookups for an id this peer once held and let go, as against ids it never
		/// had. See the comment at the counting site for why the two are worth separating.
		/// </summary>
		public static int FailsOnRetired => _failsOnRetired;
		private static int _failsOnRetired;


		/// <summary>Whether this id is in use or was used earlier in this session.</summary>
		public static bool ExistsOrRetired(int netId) =>
			identities.ContainsKey(netId) || _retired.Contains(netId);

		private static readonly System.Collections.Generic.Dictionary<int, int> _retiredBy =
			new System.Collections.Generic.Dictionary<int, int>();

		/// <summary>
		/// Whether this id is unavailable to <paramref name="asker"/> specifically.
		///
		/// In use by someone else, or retired by a different object. An object may
		/// always reclaim the number it was using a moment ago - that is the ordinary
		/// unregister-and-register that normal play does constantly, and treating it as
		/// reuse is what broke the first attempt at this.
		/// </summary>
		public static bool IsTakenFrom(int netId, NetworkIdentity asker)
		{
			if (identities.TryGetValue(netId, out var held))
				return !ReferenceEquals(held, asker);

			if (!_retired.Contains(netId)) return false;

			// Retired: only its former owner may have it back.
			if (asker != null && !asker.IsNullOrDestroyed()
				&& _retiredBy.TryGetValue(netId, out int ownerId)
				&& ownerId == asker.GetInstanceID())
				return false;

			return true;
		}


		/// <summary>
		/// Claim <paramref name="netId"/> for <paramref name="entity"/>.
		/// Returns false if a different object already holds it.
		///
		/// The caller has to act on false. It used to be void, so a refused
		/// registration looked exactly like a successful one and the loser went
		/// on believing it was addressable.
		/// </summary>
		public static bool RegisterExisting(NetworkIdentity entity, int netId)
		{
			using var _ = Profiler.Scope();

			if (!identities.ContainsKey(netId))
			{
				File(netId, entity);
				return true;
			}

			if (ReferenceEquals(identities[netId], entity))
				return true;

			// A destroyed incumbent is not a collision, it is a stale entry.
			//
			// Building sites churn constantly - created, completed, destroyed -
			// and their slots stayed behind, so every new site "collided" with a
			// corpse. Worse, reporting it read that corpse's name, and a
			// destroyed Unity object is not caught by ?. - it throws. A host
			// logged 261 NullReferenceExceptions out of this one line.
			if (identities[netId].IsNullOrDestroyed())
			{
				File(netId, entity);
				return true;
			}

			// This warning was commented out, so a collision was invisible. The
			// loser keeps its NetId and is simply not in the registry, which
			// makes every packet addressed to it a failed lookup - and the
			// failure surfaces far from here, as an object that will not sync.
			//
			// It matters more since the workable hash stopped probing the
			// registry for a free slot: two workables that hash alike now
			// genuinely collide instead of being separated by arrival order.
			_collisionCount++;
			if (_collisionCount <= 5 || _collisionCount % 50 == 0)
			{
				DebugConsole.LogWarning(
					$"[NetEntityRegistry] NetId {netId} already held by " +
					$"'{SafeName(identities[netId])}'; '{SafeName(entity)}' must be renamed " +
					$"({_collisionCount} collisions so far)");
			}

			return false;
		}

		/// <summary>
		/// The first free id at or after <paramref name="from"/>.
		///
		/// Used to rehouse an object whose id is already taken. It walks upward
		/// so the result depends only on the starting point and on which ids are
		/// occupied - not on the order objects happened to arrive in.
		/// </summary>
		public static int FindFreeId(int from)
		{
			using var _ = Profiler.Scope();

			if (from == 0) from = 1;

			int id = from;
			for (int i = 0; i < 4096; i++)
			{
				if (id != 0 && !identities.ContainsKey(id))
					return id;
				id++;
			}
			return 0;
		}

		public static void RegisterOverride(NetworkIdentity entity, int netId)
		{
			using var _ = Profiler.Scope();

			// Zero is the sentinel for "no id" everywhere else in this codebase -
			// GetNetId returns it when there is no identity, and every lookup
			// failure counts it separately - so filing an object under it makes the
			// absence of an id resolve to a real object. A live client did exactly
			// that: "Registered overridden NetId 0 for SwampLily", after which any
			// packet carrying an unset id would have found that plant.
			if (netId == 0)
			{
				DebugConsole.LogWarning(
					$"[NetEntityRegistry] refusing to file '{SafeName(entity)}' under NetId 0; " +
					"zero means 'no id', and registering it would make every unset id resolve here");
				return;
			}

			if (identities.ContainsKey(netId))
			{
				var incumbent = identities[netId];

				// Overwriting used to be silent and unconditional, which left
				// whoever was there before holding an id that now resolves to
				// somebody else. A live client did exactly that with two
				// duplicants from one printing pod delivery and ended up with a
				// minion whose personality could not be resolved - and that
				// throws out of FaceGraph.ApplyShape inside World.LateUpdate,
				// every frame, which the game does not survive.
				//
				// The newcomer wins, because this is the host telling us what an
				// id means. The incumbent is moved rather than abandoned.
				if (!ReferenceEquals(incumbent, entity))
				{
					// Counted and bounded. Unbounded, this line alone accounted for
					// 3116 entries in one session's log while two ColdWheat plants
					// traded a single id back and forth several times a second - and
					// in a DEBUG build every one of them is string formatting done on
					// the packet path. The count is what says "thrash" rather than
					// "a reassignment happened"; the individual lines say nothing the
					// first five do not.
					_reassignments++;
					if (_reassignments <= 5 || _reassignments % 250 == 0)
					{
						DebugConsole.LogWarning(
							$"[NetEntityRegistry] NetId {netId} reassigned from '{SafeName(incumbent)}' " +
							$"to '{SafeName(entity)}' (reassignment #{_reassignments})");
					}

					File(netId, entity);
					if (incumbent != null && !incumbent.IsNullOrDestroyed())
						incumbent.RehouseAfterEviction(netId);
					return;
				}

				File(netId, entity);
			}
			else
			{
				File(netId, entity);
				DebugConsole.Log($"[NetEntityRegistry] Registered overridden NetId {netId} for {entity.name}");
			}
		}
		public static bool Exists(int netId) => identities.ContainsKey(netId);

		/// <summary>
		/// Whether this exact object is the one filed under <paramref name="netId"/>.
		///
		/// Exists() answers "is that id taken", which callers have been using as if
		/// it answered "did my registration take". Those differ precisely in the
		/// case that matters: the id is occupied by somebody else.
		/// </summary>
		public static bool Holds(int netId, NetworkIdentity entity) =>
			identities.TryGetValue(netId, out var held) && ReferenceEquals(held, entity);

		/// <summary>
		/// Files an object under an id and makes its own field agree, so the two
		/// can never drift apart.
		///
		/// They did drift. A live client held NetId 514590772 twice: a SandStone
		/// filed under 514590772 and believing 514590772, and a DreckoBaby filed
		/// under -366979648 and also believing 514590772. Everything the critter
		/// sent was stamped with the sandstone's id and applied to the sandstone,
		/// and the critter itself was reachable only at a number nobody used.
		///
		/// Which sequence produced that could not be settled by reading the code -
		/// eviction, rehousing and override all touch both the dictionary and the
		/// field, and every ordering I traced was self-consistent. So rather than
		/// keep guessing, the invariant moves here: one place writes both, the
		/// mismatch becomes unrepresentable, and the correction is logged so a
		/// caller that was getting it wrong still says so once.
		/// </summary>
		private static void File(int netId, NetworkIdentity entity)
		{
			identities[netId] = entity;

			if (entity.IsNullOrDestroyed() || entity.NetId == netId)
				return;

			_fieldCorrections++;
			if (_fieldCorrections <= 5 || _fieldCorrections % 100 == 0)
			{
				DebugConsole.LogWarning(
					$"[NetEntityRegistry] filing '{SafeName(entity)}' under {netId} while it believed " +
					$"{entity.NetId}; corrected (#{_fieldCorrections}). Anything it sends would have " +
					"carried the wrong id.");
			}
			entity.NetId = netId;
		}

		private static int _fieldCorrections;

		/// <summary>How many times an object's id had to be corrected to match where it was filed.</summary>
		public static int FieldCorrections => _fieldCorrections;

		/// <summary>
		/// A name that will not throw. Unity reports a destroyed object as null
		/// through operator==, but ?. does not use that operator - reading .name
		/// on a destroyed component throws, which is how a diagnostic line
		/// became the most frequent exception in a host's log.
		/// </summary>
		private static string SafeName(NetworkIdentity identity)
		{
			if (identity.IsNullOrDestroyed()) return "destroyed";

			// Name plus which object and which component, because the name alone
			// cannot answer the question these lines get read for.
			//
			// A live colony logged "NetId reassigned from ColdWheat to ColdWheat"
			// two hundred times in twenty minutes - three plants flipping between
			// an id and that id plus one, once per sweep, forever. Whether that is
			// one plant carrying two identity components or two plants sharing a
			// cell decides which bug it is, and both read identically here.
			return $"{identity.name}#{identity.gameObject.GetInstanceID()}/{identity.GetInstanceID()}";
		}



		/// <summary>
		/// Lookups for id 0, which is the "not registered" sentinel and can
		/// never resolve. Counted apart from real failures because they are a
		/// different bug: a sender that left a field unset, not two peers
		/// disagreeing about an object.
		/// </summary>
		public static int UnsetIdLookupCount => _unsetIdLookupCount;
		private static int _unsetIdLookupCount = 0;

		/// <summary>
		/// Failures grouped by who asked, so a count can be turned into a place
		/// to look. A bare "NetId not found" says an id is missing and nothing
		/// about which packet carried it, and chasing one of those without this
		/// took several rounds. Filled from the caller attributes below, which
		/// the compiler bakes in - the alternative, a stack trace per failure,
		/// is what once froze a host solid.
		/// </summary>
		private static readonly Dictionary<string, int> _failuresByCaller = new Dictionary<string, int>();

		public static IReadOnlyDictionary<string, int> FailuresByCaller => _failuresByCaller;

		/// <summary>Same grouping for id-0 packets: it names the sender that left the field unset.</summary>
		private static readonly Dictionary<string, int> _unsetIdByCaller = new Dictionary<string, int>();

		public static IReadOnlyDictionary<string, int> UnsetIdByCaller => _unsetIdByCaller;

		/// <summary>
		/// While positive, failed lookups are neither counted nor logged.
		///
		/// The suite has to ask the registry for ids that are not there - that is
		/// how you check a miss returns false instead of throwing - and those
		/// misses were landing in the same counters the suite then asserts on.
		/// Both of the host's "failed registry lookups" in a clean run came from
		/// two of its own tests: one probing NetId -1, one dispatching a packet
		/// for 424242. The gate was reporting the tests as a desync.
		///
		/// The runner holds this open around each test, so this is fixed for
		/// every test written later as well, not just the two that did it.
		/// </summary>
		private static int _diagnosticDepth;

		public static bool InDiagnosticScope => _diagnosticDepth > 0;

		public static void BeginDiagnosticScope() => _diagnosticDepth++;

		public static void EndDiagnosticScope()
		{
			if (_diagnosticDepth > 0) _diagnosticDepth--;
		}

		/// <summary>
		/// File plus method, because the method alone does not discriminate.
		///
		/// Almost every caller here is named OnDispatched - one per packet type -
		/// so a log line saying "from OnDispatched" names nothing. It cost a real
		/// investigation: a live session logged three packets arriving with no id
		/// set and the line could not say which packet, even though the file path
		/// had been captured all along and was being used for the grouped counts
		/// but not for the message.
		/// </summary>
		private static string Describe(string caller, string callerFile)
			=> string.IsNullOrEmpty(callerFile)
				? (caller ?? "unknown")
				: System.IO.Path.GetFileNameWithoutExtension(callerFile) + "." + (caller ?? "?");

		private static void Blame(Dictionary<string, int> counts, string caller, string callerFile)
		{
			string where = Describe(caller, callerFile);
			counts.TryGetValue(where, out int n);
			counts[where] = n + 1;
		}

		public static bool TryGet(int netId, out NetworkIdentity entity,
			[System.Runtime.CompilerServices.CallerMemberName] string caller = null,
			[System.Runtime.CompilerServices.CallerFilePath] string callerFile = null)
		{
			using var _ = Profiler.Scope();

			// A host logged 299 of these in one session, mixed in with genuine
			// disagreements in the same counter - and LookupFailCount is a test
			// gate, so an unset field was being reported as a desync. Nothing is
			// ever stored under 0, so this is not a miss, it is a malformed
			// packet.
			if (netId == 0)
			{
				entity = null;
				if (_diagnosticDepth == 0)
				{
					_unsetIdLookupCount++;
					Blame(_unsetIdByCaller, caller, callerFile);
					if (_unsetIdLookupCount <= 3 || _unsetIdLookupCount % 500 == 0)
					{
						DebugConsole.LogWarning(
							$"[Registry] lookup for NetId 0 (#{_unsetIdLookupCount}) from {Describe(caller, callerFile)} - a packet was sent with no id set");
					}
				}
				return false;
			}

			bool found = identities.TryGetValue(netId, out entity);

			// An id that failed before and resolves now was early, not wrong.
			//
			// These two are the same number today and they are not the same problem.
			// A work-progress update that arrives before the building finishes is
			// harmless: the next one lands, and the client's own dump ends the run
			// holding the host's id for that object. A pickup notice for an item this
			// peer never gets told about is permanent - nothing re-sends it.
			//
			// Reading them together sent me after three regressions that were not
			// there: the total moved for reasons that had nothing to do with anything
			// being wrong, and "4 failures" turned out to mean the id layer had been
			// crippled by an exception rather than that it was healthy. Splitting them
			// makes the harmful half small enough to name.
			if (found && _everFailed.Remove(netId))
				_lookupFailsResolvedLater++;

			// A miss inside an expected-miss scope is not a divergence signal. Counted
			// separately so the behaviour is visible rather than silently dropped.
			if (!found && _expectedMissDepth > 0)
			{
				_expectedMisses++;
				return false;
			}

			if (!found && _diagnosticDepth == 0)
			{
				_everFailed.Add(netId);
				_lookupFailCount++;

				// Of the failures, the ones for an id this peer once held.
				//
				// The census established that 47 of 50 persistent absences are objects the
				// client had and retired - gas piles it merged where the host did not.
				// That is a real divergence, but whether it is a divergence worth fixing
				// depends on what it costs, and nothing measured the cost: lookupFails
				// mixes "an id I never had" with "an id I merged away", and those are a
				// missing object and a bookkeeping difference respectively.
				//
				// If most failures are retired ids, the merge divergence is producing real
				// traffic that can never resolve, and the loose-matter work earns its
				// place. If they are not, it is cosmetic and stays where it is. Deciding
				// that from the split rather than from the row count is the whole point.
				//
				// Measured: 896 of 1,248 failures in a run, 72%.
				if (_retired.Contains(netId)) _failsOnRetired++;

				Blame(_failuresByCaller, caller, callerFile);

				// The same signal that counts the divergence can also close it.
				// A id that keeps arriving and resolves to nothing is an object
				// this peer is missing, and the host can send it. Bounded and
				// rate limited on the other side; this only names the id.
				//
				// Asked for retired ids too, and that is deliberate after trying the
				// opposite.
				//
				// 896 of 1,248 failed lookups in a run were for ids this peer had held and
				// released - for loose matter, piles it merged into a neighbour. The
				// reasoning was that the mass is already here inside another pile, so
				// asking the host to send the object back is wasted at best. Skipping
				// those requests was measured over three runs against five without:
				//
				//   suppression on    HOST-ONLY 108, 107, 117
				//   suppression off   HOST-ONLY  74,  68,  76,  89,  80
				//
				// No overlap. It cost about thirty ids the client ends the session without,
				// to save 1,862 requests and sixty spawns. The requests were not wasted
				// after all: some meaningful share of them was recovering objects this peer
				// genuinely needed, and "it retired the id" did not mean "it still has the
				// thing" as often as the argument assumed.
				//
				// The counters stay. failsRetired is what made the trade measurable, and it
				// is the number to watch if this is ever attempted again - the useful
				// version would tell apart a pile merged into a neighbour from an object
				// destroyed outright, which the retired set does not.
				Components.MissingEntityResolver.Report(netId);
				if (_lookupFailCount <= 3 || _lookupFailCount % 500 == 0 || Time.unscaledTime - _lastFailLogTime > 1f)
				{
					_lastFailLogTime = Time.unscaledTime;
					DebugConsole.LogWarning($"[Registry] Lookup failed (#{_lookupFailCount}): NetId {netId} not found, asked by {Describe(caller, callerFile)}. Count: {identities.Count}");
				}
			}
			
			if (entity.IsNullOrDestroyed() || entity.gameObject.IsNullOrDestroyed())
			{
				identities.Remove(netId);
				entity = null;
				return false;
			}
			
			return found;
		}

		// Forwards its own caller rather than letting the attributes name this
		// method, which would blame every failure in the mod on one helper.
		public static bool TryGetComponent<T>(int netId, out T component,
			[System.Runtime.CompilerServices.CallerMemberName] string caller = null,
			[System.Runtime.CompilerServices.CallerFilePath] string callerFile = null)
		{
			using var _ = Profiler.Scope();

			component = default(T);
			if (!TryGet(netId, out var ni, caller, callerFile))
				return false;
			if(ni.gameObject.IsNullOrDestroyed())
				return false;
			return ni.gameObject.TryGetComponent<T>(out component);
		}
		public static bool TryGetComponent<T>(NetworkIdentity ni, out T component)
		{
			using var _ = Profiler.Scope();

			component = default(T);
			if (ni.IsNullOrDestroyed() || ni.gameObject.IsNullOrDestroyed())
				return false;
			return ni.gameObject.TryGetComponent<T>(out component);
		}

		public static void Clear()
		{
			using var _ = Profiler.Scope();

			// Tell the objects, not just the dictionary.
			//
			// This cleared its own map and left every identity believing it was still
			// filed, because IsRegistered lives on the component. RegisterIdentity
			// returns immediately for anything that believes that, so after a reconnect
			// eight thousand objects were unaddressable and nothing could repair them -
			// measured on a client: registry 8085 before the drop, 41 after, failed
			// lookups 4,586 to 90,068, with the world fully intact.
			//
			// Their ids are left alone. The id came from a save both peers load the same
			// way, so re-filing under the same number restores the exact mapping.
			foreach (var identity in identities.Values)
			{
				if (identity.IsNullOrDestroyed()) continue;
				identity.ForgetRegistration();
			}

			identities.Clear();

			// Retired ids go with the session. A new one starts from a save both peers
			// load identically, so nothing an old session retired means anything now -
			// and keeping them would leak across joins.
			_retired.Clear();
			_retiredBy.Clear();

			_lookupFailCount = 0;
			_unsetIdLookupCount = 0;
			_failuresByCaller.Clear();
			_unsetIdByCaller.Clear();
			// Carried over from the previous session before, so a clean run
			// inherited the last one's collisions and the counter stopped
			// meaning "this session".
			_collisionCount = 0;
			// TODO Rope into 1
			GroundItemPickedUpPacket.ClearPending();
			StorageItemPacket.ClearPending();

			PlayAnimPacket.ClearState();
		}

		/// <summary>Objects re-filed after a session began with a world already loaded.</summary>
		public static int ReattachedOnJoin { get; private set; }

		/// <summary>How many sweeps have run - once per join, so a rising number is churn.</summary>
		public static int ReattachSweeps { get; private set; }

		/// <summary>
		/// Re-file every identity in the loaded world.
		///
		/// Registration happens in OnSpawn, and on a reconnect nothing spawns: the world
		/// is already there. So the objects sit with their ids and no entry in this
		/// registry, and every packet about them lands on nothing. The client reported
		/// "connected" and 90,068 failed lookups.
		///
		/// Deliberately a sweep over the scene rather than a hook in one of the several
		/// join paths. There are four places that clear this registry and more than one
		/// way to end up in a session, and picking one of them is how the last bug of
		/// this shape survived - the exclusion rule that was placed in OnSpawn while
		/// ninety-one callers went around it.
		///
		/// Cheap and once per join: one FindObjectsByType and a dictionary insert each.
		/// </summary>
		public static int ReattachAll()
		{
			using var _ = Profiler.Scope();

			int reattached = 0;
			foreach (var identity in UnityEngine.Object.FindObjectsByType<NetworkIdentity>(
						 FindObjectsInactive.Exclude, FindObjectsSortMode.None))
			{
				if (identity.IsNullOrDestroyed() || identity.gameObject.IsNullOrDestroyed()) continue;
				if (Exists(identity.NetId) && Holds(identity.NetId, identity)) continue;

				identity.RegisterIdentity();
				if (identity.NetId != 0) reattached++;
			}

			ReattachedOnJoin += reattached;
			ReattachSweeps++;
			return reattached;
		}

		public static IEnumerable<NetworkIdentity> AllIdentities => identities.Values;

		/// <summary>
		/// Keys as well as values, so a diagnostic can say which id an object is
		/// filed under rather than only which id it believes it has.
		///
		/// Those two can disagree, and the difference is the whole question when a
		/// duplicate turns up: one object filed twice under different keys and one
		/// id claimed by two objects both read as "NetId X has 2 identities", and
		/// they are different bugs.
		/// </summary>
		public static IEnumerable<KeyValuePair<int, NetworkIdentity>> AllEntries => identities;
	}
}

