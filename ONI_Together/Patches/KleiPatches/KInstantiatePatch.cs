using System.Collections.Generic;
using HarmonyLib;
using ONI_Together.DebugTools;
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
		{
			_nextIsClientPreview = true;

			// Recorded here, because here is the only place the caller still exists.
			//
			// The first attempt took a stack inside NetworkIdentity.OnSpawn and got
			// "KMonoBehaviour.Spawn <- KMonoBehaviour.Start" for every prefab: ONI
			// schedules spawning, so by the time OnSpawn runs the code that created
			// the object has long returned. KInstantiate's prefix runs inside the
			// creating call, so the frames above it are the answer.
			//
			// Once per prefab. The question is which code paths create objects on a
			// client that the host has no counterpart for - the host announces two
			// Sandstone while the client holds two more it cannot name - and that is a
			// question about kinds, not instances.
			// Not "name": that is this method's own parameter, and shadowing it
			// compiles on some toolchains and not others.
			string prefabName = StripClone(original == null ? "?" : original.name);
			if (!_originLogged.Contains(prefabName))
			{
				_originLogged.Add(prefabName);

				var trace = new System.Diagnostics.StackTrace(1, false);
				var frames = new List<string>();
				for (int i = 0; i < trace.FrameCount && frames.Count < 6; i++)
				{
					var m = trace.GetFrame(i)?.GetMethod();
					if (m?.DeclaringType == null) continue;
					string owner = m.DeclaringType.Name;
					if (owner == nameof(KInstantiatePatch) || owner == "Util") continue;
					frames.Add($"{owner}.{m.Name}");
				}

				DebugConsole.LogWarning(
					$"[ClientSpawn] '{prefabName}' created on the client by: {string.Join(" <- ", frames)}");
			}
		}

		return true;
	}

	/// <summary>
	/// Set for the duration of one KInstantiate on a client, read by
	/// NetworkIdentity.OnSpawn, which runs inside that call.
	/// </summary>
	private static bool _nextIsClientPreview;

	/// <summary>Prefabs already reported, so one dig does not produce a hundred lines.</summary>
	private static readonly HashSet<string> _originLogged = new HashSet<string>();

	/// <summary>Unity names clones "Sandstone(Clone)"; the kind is what matters here.</summary>
	private static string StripClone(string name)
	{
		int open = name.IndexOf('(');
		return open < 0 ? name : name.Substring(0, open);
	}

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
