using ONI_Together.Networking.Components;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shared.Profiling;
using UnityEngine;


namespace ONI_Together.Networking
{
	public static class Extensions
	{
		public static NetworkIdentity GetNetIdentity(this MonoBehaviour behaviour)
		{
			using var _ = Profiler.Scope();

			if (behaviour.IsNullOrDestroyed() || behaviour.gameObject.IsNullOrDestroyed())
			{
				return null;
			}
			return behaviour.gameObject.GetNetIdentity();
		}
		public static NetworkIdentity GetNetIdentity(this GameObject go)
		{
			using var _ = Profiler.Scope();

			if (go.IsNullOrDestroyed())
			{
				return null;
			}

			if (go.TryGetComponent<NetworkIdentity>(out var identity))
			{
				// Having the component is not the same as having an address.
				// Registration can be skipped - the grid was not ready, an
				// eviction left it homeless - and nothing comes back to finish
				// the job, so the object keeps a NetId of 0 and every caller
				// that asks gets zero. A host logged 32 of those for pacu
				// juveniles alone, all of which had an identity attached.
				if (identity.NetId == 0)
					identity.RegisterIdentity();
				return identity;
			}

			// Attaching an identity here is a last resort, and it is worth
			// noticing when it happens. Only the peer that asks gets one: the
			// host asks because it is about to send something addressed by
			// NetId, and the other peer, holding the same object, never asks and
			// so never gives it an address. Every packet about that object is
			// then dropped on arrival.
			//
			// That is exactly how building sites went unsynced - 221 distinct
			// Constructables unresolvable on a client, 2523 times, while neither
			// peer had registered any of them. Anything that shows up in this
			// count wants attaching at spawn on both peers instead, the way
			// BuildingSpawnPatch does it.
			NetworkIdentity.NoteLazyIdentity(go);

			// Registered here, because nothing else will. A component added to
			// an object that has already spawned never receives OnSpawn, so the
			// identity sat there with NetId 0 forever - and every caller that
			// asked for an id got zero and gave up. That is where "no netId
			// found on" came from: a host logged 140 of them for pokeshells,
			// hatches and their young, all of which had an identity by then, all
			// of it unusable.
			var attached = go.AddComponent<NetworkIdentity>();
			attached.RegisterIdentity();
			return attached;
		}

		/// <summary>
		/// The identity this object already has, or null. Never attaches one.
		///
		/// Anything winding an object down wants this. Asking the creating
		/// version during cleanup attaches an identity to a dying object and
		/// then tries to register it, which throws - and an unregister path that
		/// creates the thing it is unregistering was never going to be right
		/// anyway.
		/// </summary>
		public static NetworkIdentity GetExistingNetIdentity(this GameObject go)
		{
			using var _ = Profiler.Scope();

			if (go.IsNullOrDestroyed())
				return null;

			return go.TryGetComponent<NetworkIdentity>(out var identity) ? identity : null;
		}

		public static bool TryGetNetIdentity(this GameObject go, out NetworkIdentity identity)
		{
			using var _ = Profiler.Scope();
			identity = GetNetIdentity(go);
			return identity != null;
		}

		public static int GetNetId(this MonoBehaviour behaviour)
		{
			using var _ = Profiler.Scope();

			if (!behaviour.IsNullOrDestroyed() && behaviour.gameObject.TryGetNetIdentity(out var identity))
			{
				return identity.NetId;
			}

			return 0;
		}

		// Used to replace CSteamID
        public static bool IsValid(this ulong value)
        {
	        using var _ = Profiler.Scope();

            return value != ulong.MaxValue && !value.Equals(value.Nil());
        }

		public static CSteamID AsCSteamID(this ulong value)
		{
			using var _ = Profiler.Scope();

			return new CSteamID(value);
		}

		public static ulong Nil(this ulong value)
		{
			using var _ = Profiler.Scope();

			return 0uL; // Stole this badboy from the steamworks api
        }
    }
}
