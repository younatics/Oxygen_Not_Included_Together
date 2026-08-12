using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ONI_Together.DebugTools
{
	/// <summary>
	/// How long each syncer's tick actually takes, on the peer that is running it.
	///
	/// The host runs at 76-79 ms a frame while the client, on the same colony,
	/// holds a flat 16.7 - and 780 frames a minute on the host miss 30 fps, so this
	/// is a sustained load rather than an occasional freeze. Turning the profiler
	/// off changed nothing (77 to 76), which rules out the instrumentation and
	/// leaves real work.
	///
	/// Two candidates are visible in the log - the anim syncer sending 1,903
	/// interval updates in 30 seconds, and the conduit syncer scanning 827 pipes a
	/// tick - and picking between them by reading would be guessing. Every cause
	/// chosen that way in this work has been wrong; the ones that were measured
	/// were right the first time.
	///
	/// Measured with a timestamp difference around each tick, accumulated per type,
	/// and read out once a minute next to the frame time it is supposed to explain.
	/// A Stopwatch tick is a couple of nanoseconds, so this does not become the
	/// thing it measures - which was the specific failure of the DEBUG profiler.
	/// </summary>
	public static class SyncerCostWatch
	{
		private sealed class Cost
		{
			public long Ticks;
			public int Calls;
		}

		private static readonly Dictionary<string, Cost> _costs = new Dictionary<string, Cost>();

		/// <summary>
		/// The components whose Update is worth timing: everything that sweeps the
		/// world or sends on an interval. Named rather than discovered, because
		/// timing every MonoBehaviour in the mod would bury the answer.
		/// </summary>
		private static readonly string[] Watched =
		{
			"AnimSyncCoordinator",
			"ConduitFlowSyncer",
			"WorldStateSyncer",
			"PlantGrowthSyncer",
			"BuildingDamageSyncer",
			"LogicStateSyncer",
			"MissingEntityResolver",
			"ClientDamageWatcher",
			"BulkPacketMonitor",
			"CursorManager",
		};

		public static void Note(string name, long elapsedTicks)
		{
			if (!_costs.TryGetValue(name, out var cost))
			{
				cost = new Cost();
				_costs[name] = cost;
			}
			cost.Ticks += elapsedTicks;
			cost.Calls++;
		}

		/// <summary>
		/// The most expensive ticks since the last read, as
		/// "name=totalMs/calls", and then cleared - so each health row describes its
		/// own minute rather than the whole session.
		/// </summary>
		public static string DrainTop(int count = 4)
		{
			if (_costs.Count == 0) return "none";

			var parts = _costs
				.OrderByDescending(kv => kv.Value.Ticks)
				.Take(count)
				.Select(kv => string.Format(
					"{0}={1:0}ms/{2}",
					kv.Key,
					kv.Value.Ticks * 1000.0 / Stopwatch.Frequency,
					kv.Value.Calls))
				.ToList();

			_costs.Clear();
			return string.Join(",", parts);
		}

		public static void Reset() => _costs.Clear();

		/// <summary>
		/// The hot paths that are not Update methods.
		///
		/// The first measurement ruled the syncers out: on a host at 78 ms a frame
		/// they accounted for 3 ms between them, and the two candidates the log made
		/// obvious - 1,903 anim sends in 30 seconds, 827 pipes scanned a tick - came
		/// to 0.64 and 0.13 ms a frame. The remaining 75 ms is somewhere that does not
		/// tick on a timer.
		///
		/// These are the mod's patches on methods the game itself calls constantly,
		/// and they only do work on a host - which matches a host at 78 ms against a
		/// client at 16.7 on the same colony. Timed the same way, by name, so the next
		/// reading either finds the cost here or rules this out too.
		/// </summary>
		private static readonly (string Type, string Method)[] WatchedStatics =
		{
			("KAnimControllerBase_Play_Patch", "Prefix"),
			("KAnimControllerBase_PlayRange_Patch", "Prefix"),
			("KAnimControllerBase_Queue_Patch", "Prefix"),
			("KAnimControllerBase_Patches", "SendAnimPacketToClients"),
			("PacketSender", "SendToAllClients"),
			("PacketSender", "SendToHost"),
			("CreatureSpawnPatch", "Postfix"),
			("BuildingSpawnPatch", "Postfix"),
			("WorkablePatch", "Postfix"),
			("NetworkIdentity", "RegisterIdentity"),
			("NetworkIdentityRegistry", "TryGet"),
		};

		[HarmonyPatch]
		public static class StaticTimingPatch
		{
			public static IEnumerable<MethodBase> TargetMethods()
			{
				foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
				{
					foreach (var (typeName, methodName) in WatchedStatics)
					{
						if (type.Name != typeName) continue;

						// First overload only: timing every overload separately would
						// split one cost across several names and hide it.
						var m = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
											  | BindingFlags.Static | BindingFlags.Instance
											  | BindingFlags.DeclaredOnly)
									.FirstOrDefault(x => x.Name == methodName && !x.IsAbstract);
						if (m != null) yield return m;
					}
				}
			}

			public static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

			public static void Postfix(MethodBase __originalMethod, long __state)
			{
				Note($"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}",
					 Stopwatch.GetTimestamp() - __state);
			}
		}

		[HarmonyPatch]
		public static class UpdateTimingPatch
		{
			/// <summary>
			/// Every watched type's Update, found by name.
			///
			/// TargetMethods rather than one attribute per type: the list above is the
			/// thing to edit when a new syncer appears, and a type that has no Update
			/// is skipped rather than failing the whole patch class.
			/// </summary>
			public static IEnumerable<MethodBase> TargetMethods()
			{
				foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
				{
					if (Array.IndexOf(Watched, type.Name) < 0) continue;

					var update = AccessTools.Method(type, "Update");
					if (update == null || update.DeclaringType != type) continue;
					yield return update;
				}
			}

			public static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

			public static void Postfix(object __instance, long __state)
			{
				Note(__instance.GetType().Name, Stopwatch.GetTimestamp() - __state);
			}
		}
	}
}
