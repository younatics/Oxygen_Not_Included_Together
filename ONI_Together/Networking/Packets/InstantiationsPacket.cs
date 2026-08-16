using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets
{
	public class InstantiationsPacket : IPacket
	{
		private const int MaxCompressedBytes = 16 * 1024 * 1024;
		private const int MaxInstantiationCount = 8192;

		public List<InstantiationEntry> Entries = new List<InstantiationEntry>();

		public struct InstantiationEntry
		{
			/// <summary>
			/// The id the host gave this object.
			///
			/// Without it the client instantiates the object and then names it
			/// itself, which is the divergence this packet exists to prevent -
			/// and is very likely why announcing spawns was left commented out:
			/// it could not have worked.
			/// </summary>
			public int NetId;

			public string PrefabName;
			public Vector3 Position;
			public Quaternion Rotation;
			public string ObjectName;
			public bool InitializeId;
			public int GameLayer;
		}

		public void Serialize(BinaryWriter w)
		{
			using var _ = Profiler.Scope();

			using (var ms = new MemoryStream())
			{
				using (var deflate = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
				{
					using (var tempWriter = new BinaryWriter(deflate))
					{
						tempWriter.Write(Entries.Count);
						foreach (var e in Entries)
						{
							tempWriter.Write(e.NetId);
							tempWriter.Write(e.PrefabName ?? "");
							tempWriter.Write(e.Position.x); tempWriter.Write(e.Position.y); tempWriter.Write(e.Position.z);
							tempWriter.Write(e.Rotation.x); tempWriter.Write(e.Rotation.y); tempWriter.Write(e.Rotation.z); tempWriter.Write(e.Rotation.w);
							tempWriter.Write(e.ObjectName ?? "");
							tempWriter.Write(e.InitializeId);
							tempWriter.Write(e.GameLayer);
						}
					}
				}

				byte[] compressed = ms.ToArray();
				w.Write(compressed.Length);
				w.Write(compressed);
			}
		}

		public void Deserialize(BinaryReader r)
		{
			using var _ = Profiler.Scope();

			int compressedLength = r.ReadInt32();
			if (compressedLength < 0 || compressedLength > MaxCompressedBytes)
			{
				DebugConsole.LogWarning($"[InstantiationsPacket] Invalid compressed payload length: {compressedLength}");
				Entries = [];
				return;
			}
			byte[] compressedData = r.ReadBytes(compressedLength);

			using (var ms = new MemoryStream(compressedData))
			{
				using (var deflate = new DeflateStream(ms, CompressionMode.Decompress))
				{
					using (var tempReader = new BinaryReader(deflate))
					{
						int count = tempReader.ReadInt32();
						if (count < 0 || count > MaxInstantiationCount)
						{
							DebugConsole.LogWarning($"[InstantiationsPacket] Invalid instantiation count: {count}");
							Entries = [];
							return;
						}
						Entries = new List<InstantiationEntry>(count);

						for (int i = 0; i < count; i++)
						{
							Entries.Add(new InstantiationEntry
							{
								NetId = tempReader.ReadInt32(),
								PrefabName = tempReader.ReadString(),
								Position = new Vector3(tempReader.ReadSingle(), tempReader.ReadSingle(), tempReader.ReadSingle()),
								Rotation = new Quaternion(tempReader.ReadSingle(), tempReader.ReadSingle(), tempReader.ReadSingle(), tempReader.ReadSingle()),
								ObjectName = tempReader.ReadString(),
								InitializeId = tempReader.ReadBoolean(),
								GameLayer = tempReader.ReadInt32()
							});
						}
					}
				}
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost) return;

			// A joining client enables packet processing while still in the menu
			// so the save transfer can run, and the host starts sending spawns
			// straight away. Instantiating with no world runs OnPrefabInit
			// against an empty Grid, and Pickupable divides by Grid.WidthInCells
			// - zero until a world exists.
			if (Grid.WidthInCells == 0)
			{
				DebugConsole.LogWarning(
					$"[InstantiationsPacket] ignoring {Entries.Count} spawns: no world loaded yet");
				return;
			}

			foreach (var e in Entries)
				Instantiate(e);
		}

		/// <summary>
		/// Announcements satisfied by naming an object the client had already drawn,
		/// rather than adding a second one. The pair of numbers - this against the
		/// preview count - is how to tell whether the matching is working.
		/// </summary>
		public static int AdoptedInstead => _adoptedInstead;

		/// <summary>
		/// Duplicant prefabs refused. Zero is the expected reading once the sender is
		/// fixed; a non-zero one means another sender is still announcing colonists.
		/// </summary>
		public static int RefusedMinions => _refusedMinions;

		/// <summary>
		/// Announcements that ended with this peer holding the host's number, and
		/// announcements whose number was thrown away because no identity could be
		/// attached. The second was the silent hole: it read 0 forever by not existing,
		/// while the branch it describes was dropping names on every loose item the
		/// client's own simulation had also made.
		/// </summary>
		public static int NamedOnArrival => _namedOnArrival;

		/// <summary>
		/// Announcements for an object this peer already holds under that id. Non-zero is
		/// normal after a reconnect and each one is a duplicate that used to be created.
		/// </summary>
		public static int AlreadyHere => _alreadyHere;
		public static int NamesDropped => _namesDropped;

		private static int _adoptedInstead;
		private static int _refusedMinions;
		private static int _namedOnArrival;
		private static int _namesDropped;
		private static int _alreadyHere;

		/// <summary>
		/// Announcements held back waiting for this peer's own copy, and how they ended:
		/// adopted by an object that turned up during the wait, or built anyway when it
		/// did not. The pair is the whole measurement - if late adoptions stay near zero
		/// the wait is buying nothing and should come out.
		/// </summary>
		public static int DeferredCount => _deferredCount;
		public static int AdoptedLate => _adoptedLate;
		public static int BuiltLate => _builtLate;

		private static int _deferredCount;
		private static int _adoptedLate;
		private static int _builtLate;

		/// <summary>
		/// A second. Long enough for the client's own simulation to produce the matching
		/// object - the two peers run the same step and are within a sync period of each
		/// other - and short enough that a pile appearing late is not something a player
		/// can see.
		/// </summary>
		private const float DeferSeconds = 1.0f;

		private struct HeldEntry
		{
			public InstantiationEntry Entry;
			public float Deadline;
		}

		private static readonly List<HeldEntry> _deferred = new List<HeldEntry>();

		/// <summary>
		/// Retry the held announcements. Called every frame on a client.
		/// </summary>
		public static void PumpDeferred()
		{
			if (_deferred.Count == 0) return;

			float now = Time.unscaledTime;
			for (int i = _deferred.Count - 1; i >= 0; i--)
			{
				var held = _deferred[i];
				var e = held.Entry;

				// Somebody else named it in the meantime - a resolve reply, a spawn
				// packet. Then there is nothing left to do.
				if (e.NetId != 0
					&& NetworkIdentityRegistry.TryGet(e.NetId, out var already)
					&& already != null && !already.gameObject.IsNullOrDestroyed())
				{
					_deferred.RemoveAt(i);
					_alreadyHere++;
					continue;
				}

				int cell = Grid.IsValidCell(Grid.PosToCell(e.Position)) ? Grid.PosToCell(e.Position) : -1;
				if (cell >= 0 && Components.NetworkIdentity.TryAdoptPreview(cell, e.PrefabName, e.NetId))
				{
					_deferred.RemoveAt(i);
					_adoptedLate++;
					continue;
				}

				if (now >= held.Deadline)
				{
					_deferred.RemoveAt(i);
					_builtLate++;
					Instantiate(e, deferrable: false);
				}
			}
		}

		public static void ClearDeferred() => _deferred.Clear();

		public static void ResetForNewSession()
		{
			_adoptedInstead = 0;
			_refusedMinions = 0;
			_namedOnArrival = 0;
			_namesDropped = 0;
			_alreadyHere = 0;
			_deferredCount = 0;
			_adoptedLate = 0;
			_builtLate = 0;
			_deferred.Clear();
		}

		/// <summary>
		/// Planting ghosts this peer removed to make room for the plant that replaced them.
		/// Zero beside a non-zero plant announcement count means the ghost was not found and
		/// the client is about to hold two objects in one cell - which is the state that
		/// killed it last time.
		/// </summary>
		public static int PlantingGhostsCleared => _ghostsCleared;
		private static int _ghostsCleared;

		/// <summary>
		/// Entries that reached Instantiate at all, counted before any branch can decline
		/// them. Read against the host's annSent: a large gap means announcements are being
		/// lost between the sender and this method, and every counter below it is measuring
		/// the wrong half of the problem.
		/// </summary>
		public static int ArrivedAtInstantiate { get; private set; }

		private static void RemovePlantingGhost(int cell, string prefabName)
		{
			if (string.IsNullOrEmpty(prefabName)) return;

			string ghostName = prefabName + "_preview";

			// Every layer, and the name is what makes that safe.
			//
			// The first version named two layers - Building and Plants - because a plant
			// ghost "must" be on one of them. It is on neither: ghostsCleared read 0 for a
			// whole run while the client's own dump listed ColdBreather_preview at that very
			// cell, so the removal never fired and the plant was built beside the ghost
			// exactly as before. Guessing which layer an object sits on is the same mistake
			// as guessing a game API, and this file's own history has three of those.
			//
			// Walking every layer is what the leftover-scaffold path did when it deleted a
			// neighbouring building's construction site - but the danger there was the
			// MATCH, not the walk: it compared loosely and hit a different building's
			// scaffold. This compares against one exact prefab name, "<Plant>_preview",
			// which belongs to nothing else in the game. Broad walk, narrow match.
			for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
			{
				var occupant = Grid.Objects[cell, layer];
				if (occupant == null || occupant.IsNullOrDestroyed()) continue;
				if (occupant.PrefabID().Name != ghostName) continue;

				Grid.Objects[cell, layer] = null;
				occupant.DeleteObject();
				_ghostsCleared++;

				DebugTools.ThrottledLog.Warn(
					$"[Instantiations] removed the planting ghost '{ghostName}' at cell {cell} " +
					"before building the plant that replaced it");
			}
		}

		private void Instantiate(InstantiationEntry e) => Instantiate(e, deferrable: false);

		private static void Instantiate(InstantiationEntry e, bool deferrable)
		{
			using var _ = Profiler.Scope();

			// Say that this entry arrived, before any branch can swallow it.
			//
			// A sown plant is announced by the host - "[Announce] sent
			// ColdBreather#-2145270536" - the packets arrive intact, 225 sent against 225
			// received, and the client ends the run without the plant and without a single
			// line mentioning that id. Every branch below either logs or counts, and all of
			// their counters read zero for it: instRepeat 0, instHeld 0, instLate 0,
			// instBuiltLate 0, instDropped 0, no "Missing prefab" from this file, no
			// adoption trace.
			//
			// So the question left is whether the entry reaches this method at all, and no
			// existing counter can answer it - they all sit after a decision. This one sits
			// before every decision, which is the only place that separates "it never
			// arrived" from "it arrived and something declined it silently".
			//
			// Throttled: 225 packets a run carry a couple of hundred entries, and the last
			// time this file logged per-entry the tail of a dying log was nothing else.
			ArrivedAtInstantiate++;
			DebugTools.ThrottledLog.Info(
				$"[Instantiations] entry arrived: '{e.PrefabName}' NetId {e.NetId} " +
				$"cell {(Grid.IsValidCell(Grid.PosToCell(e.Position)) ? Grid.PosToCell(e.Position) : -1)}");

			GameObject prefab = Assets.GetPrefab(e.PrefabName);
			if (prefab == null)
			{
				DebugConsole.LogWarning($"[InstantiationsPacket] Missing prefab '{e.PrefabName}'");
				return;
			}

			// Refuse duplicants here as well as at the sender.
			//
			// A colonist cannot be rebuilt from a prefab name: personality, traits,
			// aptitudes and name are not in the prefab, and the result is a duplicant
			// with personality 0x0. The renderer reads the personality inside
			// World.LateUpdate, so it throws every frame and the game closes itself -
			// three client sessions ended that way, each one with "Could not find
			// Personality: 0x0" repeating in the last seconds.
			//
			// The sender no longer announces them. This is the second layer, because
			// the cost of one more sender ever reaching this line is a dead client,
			// and refusing it only costs a colonist the telepad path can deliver
			// properly.
			if (prefab.HasTag(GameTags.BaseMinion))
			{
				_refusedMinions++;
				DebugTools.ThrottledLog.Warn(
					$"[InstantiationsPacket] refusing to build a duplicant from the prefab name " +
					$"'{e.PrefabName}' (NetId {e.NetId}): it would have no personality and drawing it " +
					"throws every frame. Duplicants replicate through the telepad path.");
				return;
			}

			// Already holding this id? Then this is a repeat, not a spawn.
			//
			// SpawnPrefabPacket has had this check since the day announcing critters
			// started working, and this path never got it. A reconnect is where it costs:
			// the client rebuilds its world, the host announces again, and every
			// announcement for an object the client already has becomes a second copy.
			//
			// Measured across a three-run batch. The two ordinary runs end with both
			// peers holding 77 critters; the reconnect run leaves the client with 80,
			// and the extras include an adult Pacu, which no hatch can explain - only a
			// second copy of one the client already had.
			//
			// Checked before the preview match rather than after, because a client that
			// already holds the id needs neither branch.
			if (e.NetId != 0
				&& NetworkIdentityRegistry.TryGet(e.NetId, out var already)
				&& already != null && !already.gameObject.IsNullOrDestroyed())
			{
				_alreadyHere++;
				return;
			}

			// Name what is already here before building another one.
			//
			// The client drew this object itself the moment it was ordered, and left
			// it unnamed on purpose. Instantiating regardless produced two objects
			// for one thing: the named copy from this packet and the client's own
			// with no id. 577 previews created against 38 adopted in eleven minutes,
			// with failed lookups rising from 270 to 3925 over the same period, is
			// what that costs - the host talks about its item, the client is holding
			// a different one, and every packet about it misses.
			int cell = Grid.IsValidCell(Grid.PosToCell(e.Position)) ? Grid.PosToCell(e.Position) : -1;
			if (cell >= 0 && Components.NetworkIdentity.TryAdoptPreview(cell, e.PrefabName, e.NetId))
			{
				_adoptedInstead++;
				return;
			}

			// A planting ghost standing where the finished plant is about to go.
			//
			// Adoption above cannot take it: the ghost's prefab is "<Plant>_preview" and the
			// announcement names "<Plant>", so the names never match - and they should not,
			// because a preview is a placement marker with a Storage, not a plant. Naming it
			// as the plant would leave every packet about the plant landing on the wrong
			// kind of object, which is a trap this repository has already paid for once.
			//
			// So the ghost is removed rather than renamed, and then the plant is built. The
			// first attempt at replicating plants skipped this: the client created the plant
			// beside its own leftover preview, one of the two was destroyed a few minutes
			// later, and the teardown threw from Unity's LateUpdate 159 times in one run
			// while the plant's id read UNRESOLVED. Two objects in one cell was the part
			// that was wrong, not building the plant.
			//
			// Copied from BuildCompletePacket's leftover-scaffold path rather than invented:
			// clear the grid slot first, then DeleteObject. That path also records why the
			// match has to be narrow - sweeping every layer once deleted a neighbouring
			// building's construction site - so this matches on the exact preview name for
			// the prefab being announced and nothing else.
			if (cell >= 0) RemovePlantingGhost(cell, e.PrefabName);

			// Nothing here yet to name - so wait a moment before building a second one.
			//
			// This is the last unpaired category: loose matter each peer's own simulation
			// makes. Measured rather than guessed at, the reason adoption fails is
			// timing, not distance. When an announcement finds no candidate, the distance
			// to the nearest unnamed preview of that prefab was recorded: of 215
			// failures, 133 had no candidate of that kind anywhere on the map. The
			// client's copy does not exist yet when the host's announcement lands.
			//
			// Widening the search was the other idea and the numbers killed it - 68 of
			// those failures had a candidate only far away, which is a different object,
			// and matching it is how an announcement meant for one pile once renamed
			// another and produced eight thousand failed lookups.
			//
			// So the announcement waits instead. Each frame it retries the adoption it
			// just failed, and after the deadline it builds, which is exactly what it
			// does today - only later. The cost is a fraction of a second before a pile
			// appears on the client; the gain is that the client's own copy gets the
			// host's name instead of standing beside it forever.
			// Tried, measured, and taken out - the numbers are in the counter names.
			//
			// Holding the announcement for a second and retrying the adoption each frame
			// bought 21 late adoptions out of 259 held, built the other 202 anyway, and
			// left the client with MORE unpaired objects than before (168 against about
			// 100). The premise was right - the client's copy does arrive after the
			// announcement - and a second is evidently not when it arrives, while the
			// delay itself widens the window in which the two peers hold different
			// worlds.
			//
			// The counters stay so the next attempt is judged the same way: instHeld
			// against instLate is the whole verdict, and this one failed it.

			GameObject obj = Object.Instantiate(prefab, e.Position, e.Rotation);
			if (obj == null)
			{
				DebugConsole.LogWarning($"[InstantiationsPacket] Failed to instantiate prefab '{e.PrefabName}'");
				return;
			}

			if (e.GameLayer != 0)
				obj.SetLayerRecursively(e.GameLayer);

			obj.name = e.ObjectName ?? prefab.name;

			KPrefabID id = obj.GetComponent<KPrefabID>();
			if (id != null)
			{
				if (e.InitializeId)
				{
					id.InstanceID = KPrefabID.GetUniqueID();
					KPrefabIDTracker.Get().Register(id);
				}

				id.InitializeTags(force_initialize: true);

				KPrefabID source = prefab.GetComponent<KPrefabID>();
				if (source != null)
				{
					id.CopyTags(source);
					id.CopyInitFunctions(source);
				}

				id.RunInstantiateFn();
			}

			// Take the host's name for it. Instantiating without this leaves the
			// client holding an object the host cannot address, which is the
			// divergence this packet is meant to close rather than widen.
			// AddOrGet, not TryGetComponent, and this is the whole defect.
			//
			// The object here was made a line ago with Object.Instantiate and has not
			// been activated yet, so its OnSpawn has not run - and OnSpawn is where this
			// mod attaches NetworkIdentity. TryGetComponent therefore found nothing on
			// exactly the objects that most needed naming, and the branch fell through
			// in silence: no warning, no counter, nothing in the log at all.
			//
			// Measured end to end rather than reasoned about. The host announced
			// BasicPlantFood#-558423651, PlantFiber#915634576 and
			// BasicPlantFood#-1775399734; the transport counters read sent 221 and
			// received 221, so every announcement arrived; and the client's log does not
			// contain those ids anywhere. Every other path through this method logs
			// something, which left exactly one branch that could swallow an
			// announcement without a trace - the one looking for a component that was
			// never going to be there yet.
			//
			// It produces both halves of the loose-item gap at once. The host holds a
			// name for an object the client's registry has never heard of, and the client
			// holds its own copy with no name, because when the object finally activates,
			// its OnSpawn asks for an id and a client is not allowed to mint one.
			//
			// SpawnPrefabPacket has used AddOrGet all along and does not have this
			// problem. Copying the pattern that already works instead of inventing a
			// second one is this repository's own rule, written down after three
			// compile failures spent guessing at game API.
			if (e.NetId != 0)
			{
				var identity = obj.AddOrGet<Components.NetworkIdentity>();
				if (identity == null)
				{
					_namesDropped++;
					DebugTools.ThrottledLog.Warn(
						$"[InstantiationsPacket] could not attach an identity to " +
						$"'{e.PrefabName}', so NetId {e.NetId} is unclaimed on this peer");
				}
				else
				{
					identity.OverrideNetId(e.NetId);
					_namedOnArrival++;
				}
			}

			// After the name, so the object's own OnSpawn finds an id already on it and
			// registers under the host's number instead of asking for one and being
			// refused.
			obj.SetActive(true);
		}
	}
}
