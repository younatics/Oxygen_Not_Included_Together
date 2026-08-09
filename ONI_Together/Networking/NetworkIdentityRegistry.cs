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


		public static void RegisterExisting(NetworkIdentity entity, int netId)
		{
			using var _ = Profiler.Scope();

			if (!identities.ContainsKey(netId))
			{
				identities[netId] = entity;
				return;
			}

			if (ReferenceEquals(identities[netId], entity))
				return;

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
					$"'{identities[netId]?.name ?? "null"}'; '{entity?.name ?? "null"}' is left unregistered " +
					$"({_collisionCount} collisions so far)");
			}
		}

		public static void RegisterOverride(NetworkIdentity entity, int netId)
		{
			using var _ = Profiler.Scope();

			if (identities.ContainsKey(netId))
			{
				DebugConsole.LogWarning($"[NetEntityRegistry] Overwriting existing entity for NetId {netId}");
				identities[netId] = entity;
			}
			else
			{
				identities.Add(netId, entity);
				DebugConsole.Log($"[NetEntityRegistry] Registered overridden NetId {netId} for {entity.name}");
			}
		}
		public static bool Exists(int netId) => identities.ContainsKey(netId);



		public static bool TryGet(int netId, out NetworkIdentity entity)
		{
			using var _ = Profiler.Scope();

			bool found = identities.TryGetValue(netId, out entity);
			if (!found)
			{
				_lookupFailCount++;
				if (_lookupFailCount <= 3 || _lookupFailCount % 500 == 0 || Time.unscaledTime - _lastFailLogTime > 1f)
				{
					_lastFailLogTime = Time.unscaledTime;
					DebugConsole.LogWarning($"[Registry] Lookup failed (#{_lookupFailCount}): NetId {netId} not found. Count: {identities.Count}");
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

		public static bool TryGetComponent<T>(int netId, out T component)
		{
			using var _ = Profiler.Scope();

			component = default(T);
			if (!TryGet(netId, out var ni))
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
