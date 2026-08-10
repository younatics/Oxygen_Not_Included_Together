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

		public static int Count => identities?.Count ?? 0;

		/// <summary>
		/// Read-only view of the failed-lookup counter so a test can assert on it.
		/// A packet addressed to a NetId this peer never registered is the visible
		/// end of a NetId disagreement, so this is the cheapest divergence gate
		/// there is - no second machine and no log parsing needed.
		/// </summary>
		public static int LookupFailCount => _lookupFailCount;

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
				identities[netId] = entity;
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
				identities[netId] = entity;
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
					DebugConsole.LogWarning(
						$"[NetEntityRegistry] NetId {netId} reassigned from '{SafeName(incumbent)}' " +
						$"to '{SafeName(entity)}'");

					identities[netId] = entity;
					if (incumbent != null && !incumbent.IsNullOrDestroyed())
						incumbent.RehouseAfterEviction(netId);
					return;
				}

				identities[netId] = entity;
			}
			else
			{
				identities.Add(netId, entity);
				DebugConsole.Log($"[NetEntityRegistry] Registered overridden NetId {netId} for {entity.name}");
			}
		}
		public static bool Exists(int netId) => identities.ContainsKey(netId);

		/// <summary>
		/// A name that will not throw. Unity reports a destroyed object as null
		/// through operator==, but ?. does not use that operator - reading .name
		/// on a destroyed component throws, which is how a diagnostic line
		/// became the most frequent exception in a host's log.
		/// </summary>
		private static string SafeName(NetworkIdentity identity)
			=> identity.IsNullOrDestroyed() ? "destroyed" : identity.name;



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
			if (!found && _diagnosticDepth == 0)
			{
				_lookupFailCount++;
				Blame(_failuresByCaller, caller, callerFile);

				// The same signal that counts the divergence can also close it.
				// A id that keeps arriving and resolves to nothing is an object
				// this peer is missing, and the host can send it. Bounded and
				// rate limited on the other side; this only names the id.
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

			identities.Clear();
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

		public static IEnumerable<NetworkIdentity> AllIdentities => identities.Values;
	}
}
