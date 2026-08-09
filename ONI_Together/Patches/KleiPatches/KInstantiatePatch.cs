using HarmonyLib;
using ONI_Together.Misc.World;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets;
using Shared.Profiling;
using UnityEngine;

[HarmonyPatch(typeof(Util), nameof(Util.KInstantiate),
		new[] {
				typeof(GameObject),
				typeof(Vector3),
				typeof(Quaternion),
				typeof(GameObject),
				typeof(string),
				typeof(bool),
				typeof(int)
		})]
public static class KInstantiatePatch
{
	public static bool Prefix(GameObject original, Vector3 position, Quaternion rotation, GameObject parent, string name, bool initialize_id, int gameLayer)
	{
		using var _ = Profiler.Scope();

		// Both branches returned true, so nothing was ever blocked despite the
		// comment. Blocking outright is not the answer either: the client would
		// stop drawing anything until the host answered, and a dig order would
		// feel broken.
		//
		// The object is allowed, but on a client it is marked as a local preview
		// so NetworkIdentity does not mint an id for it. An id the host never
		// issued is what makes the peers disagree - the client held objects the
		// host had no name for, and every packet about the real one missed.
		// The preview adopts the host's id when it arrives.
		if (MultiplayerSession.IsClient && MultiplayerSession.InSession)
			_nextIsClientPreview = true;

		return true;
	}

	/// <summary>
	/// Set for the duration of one KInstantiate on a client, read by
	/// NetworkIdentity.OnSpawn, which runs inside that call.
	/// </summary>
	private static bool _nextIsClientPreview;

	public static bool ConsumeClientPreviewFlag()
	{
		bool v = _nextIsClientPreview;
		_nextIsClientPreview = false;
		return v;
	}

	// Queue instantiation into batcher on host
	public static void Postfix(GameObject __result, GameObject original, Vector3 position, Quaternion rotation, GameObject parent, string name, bool initialize_id, int gameLayer)
	{
		using var _ = Profiler.Scope();

		if (__result == null || original == null)
			return;

		if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession)
			return;

		// Only what actually needs an address. Announcing everything a colony
		// instantiates would scale with the simulation; a measured run showed
		// 71% of what a client draws unnamed is element piles and the rest
		// items, plants and creatures, while buildings and conduits already
		// synchronise through their own paths.
		if (!NeedsNetworkIdentity(__result))
			return;

		if (!__result.TryGetComponent<NetworkIdentity>(out var identity))
			return;

		// KInstantiate's Postfix runs before OnSpawn, and OnSpawn is where
		// RegisterIdentity assigns the id - so reading NetId here always found
		// zero and the guard below silently dropped every announcement. Ask for
		// registration first. WorldDamagePatch already does this for the same
		// reason; the comment there calls it the host-side NetId=0 race.
		if (identity.NetId == 0)
			identity.RegisterIdentity();

		int netId = identity.NetId;
		if (netId == 0)
			return;   // still unnamed - the grid may not be up yet during load

		InstantiationBatcher.Queue(new InstantiationsPacket.InstantiationEntry
		{
			NetId = netId,
			PrefabName = original.name,
			Position = position,
			Rotation = rotation,
			ObjectName = name,
			InitializeId = initialize_id,
			GameLayer = gameLayer
		});
	}

	/// <summary>
	/// Objects the two peers have to agree on by name: anything that can be
	/// picked up, hauled or interacted with across the link. Buildings and
	/// conduits are excluded because they already replicate elsewhere.
	/// </summary>
	private static bool NeedsNetworkIdentity(GameObject go)
	{
		if (go == null) return false;
		if (!go.TryGetComponent<NetworkIdentity>(out _)) return false;
		if (go.TryGetComponent<Building>(out _)) return false;

		return go.TryGetComponent<Pickupable>(out _)
			|| go.TryGetComponent<Navigator>(out _);
	}
}
