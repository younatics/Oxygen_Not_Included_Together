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

		// Announcing from here caught almost nothing: the Postfix runs before
		// OnSpawn assigns the id, and most objects never pass through
		// Util.KInstantiate anyway. NetworkIdentity announces at the point the
		// id is assigned instead, which every creation path reaches.
	}
}
