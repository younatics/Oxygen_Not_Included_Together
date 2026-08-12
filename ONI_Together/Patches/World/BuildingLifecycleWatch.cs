using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.World
{
	/// <summary>
	/// Records buildings appearing and disappearing, on both peers, keyed by cell.
	///
	/// The cross-peer comparison can now see that the two boxes hold different
	/// buildings, and it is not a small effect: after digging four cells the client
	/// held four Tiles at exactly those cells that the host did not have, damaged
	/// to 42/100 and 14/100 and never removed, while the host held Tiles at three
	/// other cells the client never received. The player on one side sees solid
	/// ground where the other sees a tunnel.
	///
	/// What the comparison cannot say is which peer acted. "The client has a
	/// building the host does not" is produced equally by the client creating one
	/// and by the host destroying one, and those are opposite bugs: the first is a
	/// local simulation that should not be running, the second is a removal that
	/// should have been replicated. Six runs of guessing between them from
	/// end-of-run snapshots got nowhere, because a snapshot has no direction.
	///
	/// So both ends are recorded as they happen, with the peer's role, and the two
	/// logs read side by side answer it in one run.
	///
	/// Loading a colony spawns four thousand buildings and none of that is
	/// interesting - it is the churn afterwards that matters - so nothing is
	/// recorded until the world has been still for a moment. The count of what was
	/// skipped is reported, because an instrument that quietly drops most of its
	/// input reads as "nothing happened".
	/// </summary>
	public static class BuildingLifecycleWatch
	{
		public const string Tag = "[BUILDLIFE]";

		/// <summary>
		/// Grace period after the first building spawns, covering the load burst.
		/// Unscaled: the load runs with the game paused.
		/// </summary>
		private const float SettleSeconds = 20f;

		private static float _armAt;
		private static bool _seenAny;

		public static int Created { get; private set; }
		public static int Destroyed { get; private set; }
		public static int SkippedDuringLoad { get; private set; }

		/// <summary>Removals announced to clients. Counted separately from the ones only recorded.</summary>
		public static int Announced { get; private set; }

		/// <summary>Creations announced with an id, and creations that had none to announce.</summary>
		public static int Named { get; private set; }
		public static int Unnamed { get; private set; }

		/// <summary>Bound the volume: a colony demolition must not turn into a per-cell log.</summary>
		private const int MaxRecorded = 400;
		private static int _recorded;

		public static void Reset()
		{
			_armAt = 0f;
			_seenAny = false;
			Created = 0;
			Destroyed = 0;
			SkippedDuringLoad = 0;
			Announced = 0;
			Named = 0;
			Unnamed = 0;
			_recorded = 0;
		}

		public static string Describe() =>
			$"created={Created} destroyed={Destroyed} skippedDuringLoad={SkippedDuringLoad} " +
			$"recorded={_recorded}/{MaxRecorded}";

		private static bool Armed()
		{
			if (!_seenAny)
			{
				_seenAny = true;
				_armAt = Time.unscaledTime + SettleSeconds;
				return false;
			}
			return Time.unscaledTime >= _armAt;
		}

		/// <summary>
		/// Set when the process is going away. Every building in the colony is
		/// cleaned up during shutdown and during a world unload, and announcing
		/// four thousand removals on the way out would tell a client to demolish
		/// the colony it is still playing.
		/// </summary>
		public static bool Quitting { get; private set; }

		public static void NoteQuitting() => Quitting = true;

		private static bool WorldTearingDown =>
			Quitting || Game.Instance == null || Game.Instance.IsLoading();

		internal static void Record(GameObject go, bool created)
		{
			if (go.IsNullOrDestroyed())
				return;

			// Only in a session: single-player churn is not a divergence.
			if (!MultiplayerSession.InSession)
				return;

			// The host names what it creates. A client draws the building itself and
			// waits to be told its id; without this it waits forever - 142 of 154
			// previews were never adopted in one measured session - and every packet
			// about any of them is a failed lookup.
			if (created && MultiplayerSession.IsHost && !WorldTearingDown)
			{
				var spawned = go.GetExistingNetIdentity();
				int spawnedId = spawned.IsNullOrDestroyed() ? 0 : spawned.NetId;
				if (spawnedId != 0)
				{
					Networking.PacketSender.SendToAllClients(
						new Networking.Packets.World.BuildingSpawnedPacket(
							spawnedId, Grid.PosToCell(go), go.PrefabID().ToString()),
						Networking.PacketSendMode.Reliable);
					Named++;
				}
				else
				{
					// Nothing to announce yet. Counted rather than ignored: if this
					// is where the ids are missing, the number says so.
					Unnamed++;
				}
			}

			// Tell the clients, before anything else, because this is the only
			// moment the object still knows where it is and what it was.
			if (!created && MultiplayerSession.IsHost && !WorldTearingDown)
			{
				// Not a pattern match on the component: a destroyed Unity object is
				// not null and is not caught by `is { }` either, and reading NetId
				// off one throws.
				var existing = go.GetExistingNetIdentity();
				int netId = existing.IsNullOrDestroyed() ? 0 : existing.NetId;

				Networking.PacketSender.SendToAllClients(
					new Networking.Packets.World.BuildingRemovedPacket(
						netId, Grid.PosToCell(go), go.PrefabID().ToString()),
					Networking.PacketSendMode.Reliable);
				Announced++;
			}

			if (!Armed())
			{
				SkippedDuringLoad++;
				return;
			}

			if (created) Created++; else Destroyed++;

			if (_recorded >= MaxRecorded)
				return;
			_recorded++;

			int cell = Grid.PosToCell(go);
			var identity = go.GetExistingNetIdentity();
			string role = MultiplayerSession.IsHost ? "host" : "client";

			DebugConsole.Log(
				$"{Tag} {(created ? "+" : "-")}|{go.PrefabID()}|{cell}|" +
				$"{(identity == null ? 0 : identity.NetId)}|{role}");
		}

		[HarmonyPatch(typeof(BuildingComplete), "OnSpawn")]
		public static class SpawnPatch
		{
			public static void Postfix(BuildingComplete __instance)
			{
				using var _ = Profiler.Scope();
				try { Record(__instance.gameObject, created: true); }
				catch (System.Exception ex) { DebugConsole.LogError($"[BuildingLifecycleWatch] {ex}"); }
			}
		}

		[HarmonyPatch(typeof(BuildingComplete), "OnCleanUp")]
		public static class CleanUpPatch
		{
			public static void Prefix(BuildingComplete __instance)
			{
				using var _ = Profiler.Scope();
				// Prefix: after OnCleanUp the cell and the identity may already be
				// unreadable, and a record that cannot say where is no record.
				try { Record(__instance.gameObject, created: false); }
				catch (System.Exception ex) { DebugConsole.LogError($"[BuildingLifecycleWatch] {ex}"); }
			}
		}
	}
}
