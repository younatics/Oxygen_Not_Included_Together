using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Trackers;
using ONI_Together.Networking.Transport;
using Shared.Profiling;
using System.Collections.Generic;
using UnityEngine;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking.Components
{
	public class PlantGrowthSyncer : MonoBehaviour
	{
		public static PlantGrowthSyncer Instance { get; private set; }

		public static bool IsApplyingState = false;

		private const float SYNC_INTERVAL = 5f;
		private const float INITIAL_DELAY = 7f;
		private const float LIVE_EVENT_DELAY = 2f;

		private float _lastSyncTime;

		// A plant snapshot is split across packets when the colony is large.
		/// <summary>
		/// Plants this peer built from a lifecycle event that have no Growing component.
		///
		/// Its own counter because it is the whole of the change: every other plant number
		/// in this mod is derived from Growing and reads zero while this case happens. If
		/// this stays zero the widened path never fired and any verdict about plants would
		/// be about nothing, which is how the last two attempts were judged.
		/// </summary>
		public static int PlantsWithoutGrowing { get; private set; }

		private int _plantSweepId;
		private readonly SweepAssembler<PlantData> _plantSweep = new SweepAssembler<PlantData>("Plants");
		private bool _initialized;
		private float _initializationTime;

		private void Awake()
		{
			using var _ = Profiler.Scope();

			Instance = this;
		}

		public static bool CanBroadcastLifecycleEvents =>
			Instance != null &&
			Instance._initialized &&
			Time.unscaledTime - Instance._initializationTime >= LIVE_EVENT_DELAY &&
			MultiplayerSession.InSession &&
			MultiplayerSession.IsHost &&
			MultiplayerSession.ConnectedPlayers.Count > 0 &&
			!GameServerHardSync.IsHardSyncInProgress;

		private void Update()
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession || !MultiplayerSession.IsHost)
				return;

			if (MultiplayerSession.ConnectedPlayers.Count == 0)
				return;

			if (!_initialized)
			{
				_initializationTime = Time.unscaledTime;
				_initialized = true;
				return;
			}

			if (Time.unscaledTime - _initializationTime < INITIAL_DELAY)
				return;

			if (Time.unscaledTime - _lastSyncTime <= SYNC_INTERVAL)
				return;

			_lastSyncTime = Time.unscaledTime;
			SendPlantStates();
		}

		public static void BroadcastPlantLifecycle(PlantLifecycleOperation operation, Growing growing, SingleEntityReceptacle receptacleOverride = null)
		{
			using var _ = Profiler.Scope();

			if (growing == null) return;
			BroadcastPlantLifecycle(operation, growing.gameObject, receptacleOverride);
		}

		/// <summary>
		/// The same event for a plant that has no Growing component.
		///
		/// Growing is not what makes something a plant. EntityTemplates.
		/// ExtendEntityToBasicPlant adds GameTags.Plant to every plant and adds Growing
		/// only to the ones that grow a crop, so a Wheezewort is a plant with no Growing -
		/// and a Wheezewort is exactly the plant that diverges. Sowing one during a run
		/// leaves the host with 19 and the client with 18 every time.
		///
		/// Every path that would have carried it required Growing: this broadcast, the
		/// PlantablePlot postfix, PlantTracker.AllPlants. Two earlier attempts at plants
		/// were aimed at Object.Instantiate and Grid.Objects registration, and a client
		/// probe has since shown both peers hold the object in Grid.Objects - that was not
		/// what stopped this.
		///
		/// The periodic sweep is deliberately NOT widened with it. That sweep reconciles
		/// by absence - it destroys any plant it holds that the packet does not list, and
		/// it once deleted 294 of a client's plants - and it walks PlantTracker.AllPlants,
		/// which is a HashSet&lt;Growing&gt;. Leaving it alone means a plant with no Growing
		/// can never appear in that walk and therefore can never be destroyed by it. The
		/// event path adds plants; only the sweep removes them by absence, and it still
		/// cannot see these.
		/// </summary>
		public static void BroadcastPlantLifecycle(PlantLifecycleOperation operation, GameObject plant, SingleEntityReceptacle receptacleOverride = null)
		{
			using var _ = Profiler.Scope();

			if (!CanBroadcastLifecycleEvents)
				return;

			if (!TryBuildPlantData(plant, out var data, receptacleOverride))
				return;

			PacketSender.SendToAllClients(new PlantLifecyclePacket
			{
				Operation = operation,
				Plant = data
			});
		}

		public static bool TryBuildPlantData(Growing growing, out PlantData data, SingleEntityReceptacle receptacleOverride = null)
		{
			data = default;
			if (growing == null) return false;
			return TryBuildPlantData(growing.gameObject, out data, receptacleOverride);
		}

		public static bool TryBuildPlantData(GameObject plant, out PlantData data, SingleEntityReceptacle receptacleOverride = null)
		{
			using var _ = Profiler.Scope();

			data = default;

			if (plant == null)
				return false;

			int cell = Grid.PosToCell(plant);
			if (!Grid.IsValidCell(cell))
				return false;

			if (!plant.TryGetComponent<KPrefabID>(out var kpid) || kpid == null)
				return false;

			// Growing is optional from here down. Where it is absent the growth fields
			// carry their defaults, which is the truth about a plant that does not grow -
			// not a gap in the data.
			plant.TryGetComponent<Growing>(out var growing);

			int plantNetId = EnsureIdentity(plant, 0);
			int receptacleNetId = 0;
			bool isWild = growing != null && growing.IsWildPlanted();

			var receptacle = receptacleOverride;
			if (receptacle == null && growing != null)
			{
				TryGetReceptacle(growing, out receptacle);
			}

			if (receptacle != null && receptacle.gameObject != null)
			{
				receptacleNetId = EnsureIdentity(receptacle.gameObject, 0);
				isWild = false;
			}

			bool isWilting = false;
			if (plant.TryGetComponent<WiltCondition>(out var wiltCondition) && wiltCondition != null)
			{
				isWilting = wiltCondition.IsWilting();
			}

			bool isHarvestReady = false;
			if (plant.TryGetComponent<HarvestDesignatable>(out var harvestDesignatable) && harvestDesignatable != null)
			{
				isHarvestReady = harvestDesignatable.CanBeHarvested();
			}

			data = new PlantData
			{
				PlantNetId = plantNetId,
				ReceptacleNetId = receptacleNetId,
				Cell = cell,
				PlantPrefabTag = kpid.PrefabTag.Name,
				Maturity = growing != null ? growing.PercentGrown() : 0f,
				IsWilting = isWilting,
				IsHarvestReady = isHarvestReady,
				IsWild = isWild
			};
			return true;
		}

		private void SendPlantStates()
		{
			using var _ = Profiler.Scope();

			var sw = System.Diagnostics.Stopwatch.StartNew();

			// Collected whole, then split into batches that each fit one
			// indivisible payload, with a sweep id so the receiver can wait for
			// all of them. 200 plants was 8612 B against a 1000 B limit and only
			// 22 fit - the 25-plant cliff the audit recorded as S4 is this
			// arithmetic. Cutting the snapshot without the sweep header would
			// have each batch delete the plants belonging to the others.
			int limit = TransportPacketSender.StrictestUnfragmentedPayloadBytes;
			var all = new List<PlantData>();
			lock (PlantTracker.AllPlants)
			{
				foreach (var growing in PlantTracker.AllPlants)
				{
					if (TryBuildPlantData(growing, out var data))
						all.Add(data);
				}
			}

			// Sized first so every batch can carry the true BatchCount; a
			// receiver cannot start a sweep it does not know the length of.
			var batches = new List<List<PlantData>>();
			var current = new List<PlantData>();
			int bytes = PlantGrowthStatePacket.HeaderBytes;
			foreach (var p in all)
			{
				int cost = PlantGrowthStatePacket.EntryBytes(p);
				if (current.Count > 0 && bytes + cost > limit)
				{
					batches.Add(current);
					current = new List<PlantData>();
					bytes = PlantGrowthStatePacket.HeaderBytes;
				}
				current.Add(p);
				bytes += cost;
			}
			batches.Add(current);   // always at least one, so an empty sweep still clears

			int sweepId = ++_plantSweepId;
			for (int i = 0; i < batches.Count; i++)
			{
				PacketSender.SendToAllClients(new PlantGrowthStatePacket
				{
					SweepId = sweepId,
					BatchIndex = i,
					BatchCount = batches.Count,
					Plants = batches[i]
				}, PacketSendMode.Unreliable);
			}

			sw.Stop();
			SyncStats.RecordSync(SyncStats.Plants, all.Count, all.Count * 43, sw.ElapsedMilliseconds);
		}

		public bool OnPlantLifecycleReceived(PlantLifecyclePacket packet)
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost || Grid.WidthInCells == 0)
				return false;

			try
			{
				IsApplyingState = true;
				return packet.Operation switch
				{
					PlantLifecycleOperation.Spawn => SpawnOrUpdatePlant(packet.Plant),
					PlantLifecycleOperation.Remove => RemovePlant(packet.Plant),
					_ => false
				};
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[PlantGrowthSyncer] Error applying lifecycle packet {packet.Operation}: {ex.Message}");
				return false;
			}
			finally
			{
				IsApplyingState = false;
			}
		}

		public void OnPlantStateReceived(PlantGrowthStatePacket packet)
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost || Grid.WidthInCells == 0)
				return;

			// Hold batches until the whole sweep is here. Reconciling on part of
			// a snapshot would destroy every plant that lives in a batch which
			// has not arrived.
			if (!_plantSweep.Accept(packet.SweepId, packet.BatchIndex, packet.BatchCount, packet.Plants, out var sweepPlants))
				return;

			try
			{
				IsApplyingState = true;

				var remoteByPlantId = new Dictionary<int, PlantData>();
				var remoteByReceptacleId = new Dictionary<int, PlantData>();
				var remoteByCell = new Dictionary<int, PlantData>();

				foreach (var plant in sweepPlants)
				{
					if (plant.PlantNetId != 0)
						remoteByPlantId[plant.PlantNetId] = plant;
					if (plant.ReceptacleNetId != 0)
						remoteByReceptacleId[plant.ReceptacleNetId] = plant;
					if (Grid.IsValidCell(plant.Cell))
						remoteByCell[plant.Cell] = plant;
				}

				// An empty sweep is not an instruction to destroy every plant.
				//
				// Reconciling by absence means the sweep is the whole truth, so a
				// sweep that arrives empty because the SENDER lost track of its
				// plants reads as "the colony has none" and the receiver dutifully
				// razes it. That happened: clearing the host's plant tracker on a
				// session boundary made it send 22 sweeps of nothing but header,
				// and a client destroyed 294 of its own plants.
				//
				// A colony really can reach zero plants, so this is not a refusal
				// - it is a refusal to do it silently and all at once. The sender
				// is the one that has to be fixed, and it cannot be fixed if the
				// damage is invisible.
				if (sweepPlants.Count == 0 && PlantTracker.AllPlants.Count > 0)
				{
					DebugConsole.LogError(
						$"[PlantGrowthSyncer] refusing an empty sweep while holding {PlantTracker.AllPlants.Count} " +
						"plants - the sender has lost its plant tracking, and applying this would destroy them all");
					return;
				}

				var matchedPlantIds = new HashSet<int>();
				var matchedReceptacleIds = new HashSet<int>();
				var matchedCells = new HashSet<int>();
				var toRemove = new List<Growing>();

				lock (PlantTracker.AllPlants)
				{
					foreach (var growing in PlantTracker.AllPlants)
					{
						if (growing == null)
							continue;

						if (TryFindMatchingRemote(growing, remoteByPlantId, remoteByReceptacleId, remoteByCell, out var remoteData))
						{
							if (remoteData.PlantNetId != 0)
								matchedPlantIds.Add(remoteData.PlantNetId);
							if (remoteData.ReceptacleNetId != 0)
								matchedReceptacleIds.Add(remoteData.ReceptacleNetId);
							if (Grid.IsValidCell(remoteData.Cell))
								matchedCells.Add(remoteData.Cell);

							ApplyPlantState(growing, remoteData);
							continue;
						}

						toRemove.Add(growing);
					}
				}

				foreach (var growing in toRemove)
				{
					if (growing == null || growing.gameObject == null)
						continue;

					DebugConsole.Log($"[PlantGrowthSyncer] Removing phantom plant at {Grid.PosToCell(growing)}");
					Util.KDestroyGameObject(growing.gameObject);
				}

				foreach (var plant in sweepPlants)
				{
					if (plant.PlantNetId != 0 && matchedPlantIds.Contains(plant.PlantNetId))
						continue;
					if (plant.PlantNetId == 0 && plant.ReceptacleNetId != 0 && matchedReceptacleIds.Contains(plant.ReceptacleNetId))
						continue;
					if (plant.PlantNetId == 0 && plant.ReceptacleNetId == 0 && matchedCells.Contains(plant.Cell))
						continue;

					SpawnOrUpdatePlant(plant);
				}
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[PlantGrowthSyncer] Error in OnPlantStateReceived: {ex.Message}");
			}
			finally
			{
				IsApplyingState = false;
			}
		}

		private static bool TryFindMatchingRemote(
			Growing growing,
			Dictionary<int, PlantData> remoteByPlantId,
			Dictionary<int, PlantData> remoteByReceptacleId,
			Dictionary<int, PlantData> remoteByCell,
			out PlantData data)
		{
			using var _ = Profiler.Scope();

			data = default;

			int plantNetId = GetExistingIdentityId(growing.gameObject);
			if (plantNetId != 0 && remoteByPlantId.TryGetValue(plantNetId, out data))
				return true;

			if (TryGetReceptacle(growing, out var receptacle))
			{
				int receptacleNetId = GetExistingIdentityId(receptacle.gameObject);
				if (receptacleNetId != 0 && remoteByReceptacleId.TryGetValue(receptacleNetId, out data))
					return true;
			}

			int cell = Grid.PosToCell(growing.gameObject);
			return Grid.IsValidCell(cell) && remoteByCell.TryGetValue(cell, out data);
		}

		private bool SpawnOrUpdatePlant(PlantData data)
		{
			using var _ = Profiler.Scope();

			if (!Grid.IsValidCell(data.Cell) || string.IsNullOrEmpty(data.PlantPrefabTag))
				return false;

			if (TryFindLocalPlant(data, out var existingPlant))
			{
				ApplyPlantState(existingPlant, data);
				return true;
			}

			var receptacle = ResolveReceptacle(data);
			if (receptacle != null && receptacle.Occupant != null && receptacle.Occupant.TryGetComponent<Growing>(out var occupantPlant))
			{
				bool samePlant = GetExistingIdentityId(occupantPlant.gameObject) == data.PlantNetId;
				if (!samePlant &&
					occupantPlant.TryGetComponent<KPrefabID>(out var occupantKpid) &&
					occupantKpid != null &&
					string.Equals(occupantKpid.PrefabTag.Name, data.PlantPrefabTag))
				{
					samePlant = true;
				}

				if (samePlant)
				{
					ApplyPlantState(occupantPlant, data);
					return true;
				}

				Util.KDestroyGameObject(receptacle.Occupant);
			}

			var prefab = Assets.GetPrefab(new Tag(data.PlantPrefabTag));
			if (prefab == null)
			{
				DebugConsole.LogWarning($"[PlantGrowthSyncer] Could not find prefab for plant '{data.PlantPrefabTag}'");
				return false;
			}

			Vector3 pos = Grid.CellToPosCBC(data.Cell, Grid.SceneLayer.BuildingFront);
			GameObject plantGo = Util.KInstantiate(prefab, pos);
			if (plantGo == null)
				return false;

			plantGo.SetActive(true);
			EnsureIdentity(plantGo, data.PlantNetId);

			if (receptacle is PlantablePlot plot)
			{
				plot.ReplacePlant(plantGo, true);

				if (plantGo.TryGetComponent<ReceptacleMonitor>(out var rm) && rm != null)
				{
					rm.SetReceptacle(plot);
				}
			}
			else if (receptacle != null)
			{
				receptacle.CancelActiveRequest();
				receptacle.ForceDeposit(plantGo);
			}

			if (!TryFindLocalPlant(data, out var spawnedPlant))
			{
				spawnedPlant = plantGo.GetComponent<Growing>();
			}

			if (spawnedPlant != null)
			{
				ApplyPlantState(spawnedPlant, data);
			}
			else
			{
				// A plant with no Growing is finished at this point, not failed.
				//
				// This used to return false and log "could not resolve Growing", throwing
				// away a plant that was already built and already attached to its plot two
				// statements above. There is no growth state to apply to a Wheezewort: it
				// does not grow, does not mature and cannot be harvested, so the fields the
				// apply would write are the defaults it already has.
				//
				// No tag test guards this. GameTags.Plant was tried and is not universal -
				// ColdBreatherConfig builds its prefab with CreatePlacedEntity and never
				// calls ExtendEntityToBasicPlant, so it carries no plant tag at all. The
				// host only sends this packet for a plot's own occupant, which is the same
				// question asked where it can be answered exactly.
				PlantsWithoutGrowing++;
			}

			DebugConsole.Log(receptacle != null
				? $"[PlantGrowthSyncer] Spawned planted crop '{data.PlantPrefabTag}' at cell {data.Cell} for receptacle {data.ReceptacleNetId}"
				: $"[PlantGrowthSyncer] Spawned wild plant '{data.PlantPrefabTag}' at cell {data.Cell}");

			return true;
		}

		private bool RemovePlant(PlantData data)
		{
			using var _ = Profiler.Scope();

			if (TryFindLocalPlant(data, out var growing) && growing != null && growing.gameObject != null)
			{
				Util.KDestroyGameObject(growing.gameObject);
				DebugConsole.Log($"[PlantGrowthSyncer] Removed plant '{data.PlantPrefabTag}' at cell {data.Cell}");
				return true;
			}

			var receptacle = ResolveReceptacle(data);
			if (receptacle?.Occupant != null)
			{
				Util.KDestroyGameObject(receptacle.Occupant);
				DebugConsole.Log($"[PlantGrowthSyncer] Removed receptacle occupant for plant '{data.PlantPrefabTag}' at cell {data.Cell}");
				return true;
			}

			return false;
		}

		private static bool TryFindLocalPlant(PlantData data, out Growing growing)
		{
			using var _ = Profiler.Scope();

			growing = null;

			if (data.PlantNetId != 0 && NetworkIdentityRegistry.TryGetComponent(data.PlantNetId, out Growing byId) && byId != null)
			{
				growing = byId;
				return true;
			}

			if (data.ReceptacleNetId != 0 &&
				NetworkIdentityRegistry.TryGet(data.ReceptacleNetId, out var receptacleIdentity) &&
				receptacleIdentity != null &&
				receptacleIdentity.gameObject.TryGetComponent<SingleEntityReceptacle>(out var receptacle) &&
				receptacle.Occupant != null &&
				receptacle.Occupant.TryGetComponent<Growing>(out var byReceptacle) &&
				byReceptacle != null)
			{
				growing = byReceptacle;
				return true;
			}

			lock (PlantTracker.AllPlants)
			{
				foreach (var trackedPlant in PlantTracker.AllPlants)
				{
					if (trackedPlant == null || trackedPlant.gameObject == null)
						continue;

					int cell = Grid.PosToCell(trackedPlant.gameObject);
					if (cell != data.Cell)
						continue;

					if (!trackedPlant.TryGetComponent<KPrefabID>(out var kpid) || kpid == null)
						continue;

					if (!string.Equals(kpid.PrefabTag.Name, data.PlantPrefabTag))
						continue;

					growing = trackedPlant;
					return true;
				}
			}

			return false;
		}

		private static SingleEntityReceptacle ResolveReceptacle(PlantData data)
		{
			using var _ = Profiler.Scope();

			if (data.ReceptacleNetId != 0 &&
				NetworkIdentityRegistry.TryGet(data.ReceptacleNetId, out var receptacleIdentity) &&
				receptacleIdentity != null &&
				receptacleIdentity.gameObject.TryGetComponent<SingleEntityReceptacle>(out var byId))
			{
				return byId;
			}

			if (!Grid.IsValidCell(data.Cell))
				return null;

			ObjectLayer[] layersToCheck =
			{
				ObjectLayer.Building,
				ObjectLayer.FoundationTile,
				ObjectLayer.Plants,
				ObjectLayer.AttachableBuilding,
			};

			foreach (var layer in layersToCheck)
			{
				var obj = Grid.Objects[data.Cell, (int)layer];
				if (obj != null && obj.TryGetComponent<SingleEntityReceptacle>(out var receptacle))
					return receptacle;
			}

			return null;
		}

		private static bool TryGetReceptacle(Growing growing, out SingleEntityReceptacle receptacle)
		{
			using var _ = Profiler.Scope();

			receptacle = null;
			if (growing == null || growing.gameObject == null)
				return false;

			if (growing.TryGetComponent<ReceptacleMonitor>(out var rm) && rm != null)
			{
				receptacle = rm.GetReceptacle();
				if (receptacle != null)
					return true;
			}

			receptacle = growing.GetComponentInParent<SingleEntityReceptacle>();
			return receptacle != null;
		}

		private static int EnsureIdentity(GameObject go, int targetNetId)
		{
			using var _ = Profiler.Scope();

			if (go == null)
				return 0;

			var identity = go.AddOrGet<NetworkIdentity>();
			if (identity.NetId == 0)
			{
				identity.RegisterIdentity();
			}

			if (targetNetId != 0 && identity.NetId != targetNetId)
			{
				// Try, but do not keep trying forever.
				//
				// This ran once per plant per sync tick, and when the target id
				// cannot be taken - another plant holds it - the override evicts
				// that one, it rehouses, and next tick the disagreement is back.
				// A real session shows the result: 1 to 4 of these a minute for
				// twenty minutes, then 25, then 256, then a flat 430 to 460 every
				// minute for the rest of the session, never coming down. 3116
				// reassignments for ColdWheat alone, 2384 for SwampLily, each one a
				// registry eviction, a rehouse and three formatted log lines. That
				// is the "it gets slower the longer we play" report, and the 4.8 MB
				// log it produced is most of why the tail of a dying session is hard
				// to read.
				//
				// After a few attempts the two peers genuinely disagree about which
				// object owns this id, and repeating the swap cannot settle it. Stop,
				// and say which pair gave up - a bounded loop that reports what it
				// abandoned, rather than an unbounded one that reports every attempt.
				_overrideAttempts.TryGetValue(targetNetId, out int attempts);
				if (attempts < MaxOverrideAttempts)
				{
					_overrideAttempts[targetNetId] = attempts + 1;
					identity.OverrideNetId(targetNetId);
				}
				else if (attempts == MaxOverrideAttempts)
				{
					_overrideAttempts[targetNetId] = attempts + 1;
					_abandonedIds++;
					DebugConsole.LogWarning(
						$"[PlantGrowth] giving up on naming '{go.PrefabID()}' NetId {targetNetId} after " +
						$"{MaxOverrideAttempts} attempts - it holds {identity.NetId} and something else " +
						$"will not release {targetNetId}. This plant stays unaddressable rather than " +
						$"trading the id every tick ({_abandonedIds} abandoned so far).");
				}
			}
			else if (targetNetId != 0)
			{
				// It agrees now, so a later disagreement gets a fresh set of tries.
				_overrideAttempts.Remove(targetNetId);
			}

			return identity.NetId;
		}

		/// <summary>How many times one id may be fought over before it is left alone.</summary>
		private const int MaxOverrideAttempts = 3;

		private static readonly Dictionary<int, int> _overrideAttempts = new Dictionary<int, int>();
		private static int _abandonedIds;

		/// <summary>Ids this peer stopped trying to assign. Reported, never silent.</summary>
		public static int AbandonedIds => _abandonedIds;

		public static void ResetIdentityAttempts()
		{
			_overrideAttempts.Clear();
			_abandonedIds = 0;
		}

		private static int GetExistingIdentityId(GameObject go)
		{
			using var _ = Profiler.Scope();

			if (go == null || !go.TryGetComponent<NetworkIdentity>(out var identity) || identity == null)
				return 0;

			return identity.NetId;
		}

		private static void ApplyPlantState(Growing growing, PlantData data)
		{
			using var _ = Profiler.Scope();

			if (growing == null || growing.gameObject == null)
				return;

			try
			{
				EnsureIdentity(growing.gameObject, data.PlantNetId);

				float currentMaturity = growing.PercentGrown();
				if (Mathf.Abs(currentMaturity - data.Maturity) > 0.001f)
				{
					growing.OverrideMaturityLevel(data.Maturity);
				}

				if (!data.IsWild && TryGetReceptacle(growing, out var receptacle) && receptacle is PlantablePlot plot)
				{
					if (growing.TryGetComponent<ReceptacleMonitor>(out var rm) && rm != null)
					{
						rm.SetReceptacle(plot);
					}
				}

				if (growing.TryGetComponent<WiltCondition>(out var wiltCondition) && wiltCondition != null)
				{
					if (data.IsWilting && !wiltCondition.IsWilting())
					{
						wiltCondition.DoWilt();
					}
					else if (!data.IsWilting && wiltCondition.IsWilting())
					{
						wiltCondition.DoRecover();
					}
				}

				if (growing.TryGetComponent<KBatchedAnimController>(out var kbac) && kbac != null)
				{
					kbac.SetVisiblity(true);
					kbac.forceRebuild = true;
				}
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[PlantGrowthSyncer] Error applying plant state at cell {data.Cell}: {ex.Message}");
			}
		}
	}
}
