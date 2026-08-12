using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shared.Profiling;
using UnityEngine;
using ONI_Together.Networking.Components;
using Rendering;

namespace ONI_Together.Networking.Packets.Tools.Build
{
    public class BuildCompletePacket : IPacket
    {
        private const int MaxMaterialTagCount = 64;

        /// <summary>
        /// Scaffolds this peer had to clear itself after a building finished.
        ///
        /// Every one of these was a client showing a tile as still scheduled while
        /// standing on the finished thing. Zero means the finish path is finding the
        /// scaffold where it looks for it.
        /// </summary>
        public static int LeftoverScaffoldsCleared { get; private set; }

        /// <summary>
        /// Scaffolds found in the cell that belong to a different building.
        ///
        /// Left alone, and counted, because deleting them is what the first version did:
        /// one cell holds a tile and a wire, and finishing one destroyed the other's
        /// construction site on the client.
        /// </summary>
        public static int ScaffoldsLeftAlone { get; private set; }

        public int Cell;
        public string PrefabID;
        public Orientation Orientation;
        public List<string> MaterialTags = new List<string>();
        public float Temperature;
        public string FacadeID = "DEFAULT_FACADE";
        public int WorkerNetId;

        // Utility buildings
        public UtilityConnections UtilityConnectionFlags;

        public ObjectLayer ObjectLayer;

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();

            writer.Write(Cell);
            writer.Write(PrefabID);
            writer.Write((int)Orientation);
            writer.Write(Temperature);
            writer.Write(FacadeID);

            writer.Write(MaterialTags.Count);
            foreach (var tag in MaterialTags)
                writer.Write(tag);

            // Write connection flags
            writer.Write((int)UtilityConnectionFlags);

            writer.Write((int)ObjectLayer);

            writer.Write(WorkerNetId);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();

            Cell = reader.ReadInt32();
            PrefabID = reader.ReadString();
            Orientation = (Orientation)reader.ReadInt32();
            Temperature = reader.ReadSingle();
            FacadeID = reader.ReadString();

            int count = reader.ReadInt32();
            if (count < 0 || count > MaxMaterialTagCount)
            {
                DebugConsole.LogWarning($"[BuildCompletePacket] Invalid material tag count: {count}");
                Cell = Grid.InvalidCell;
                MaterialTags = [];
                return;
            }
            MaterialTags = new List<string>(count);
            for (int i = 0; i < count; i++)
                MaterialTags.Add(reader.ReadString());

            UtilityConnectionFlags = (UtilityConnections)reader.ReadInt32();
            ObjectLayer = (ObjectLayer)reader.ReadInt32();

            WorkerNetId = reader.ReadInt32();
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (!Grid.IsValidCell(Cell))
            {
                DebugConsole.LogWarning($"[BuildCompletePacket] Invalid cell: {Cell}");
                return;
            }

            var def = Assets.GetBuildingDef(PrefabID);
            if (def == null)
            {
                DebugConsole.LogWarning($"[BuildCompletePacket] Unknown building def: {PrefabID}");
                return;
			}

			var tags = MaterialTags.Select(t => new Tag(t)).ToList();

            if (tags.Count == 0)
            {
                DebugConsole.LogWarning($"[BuildCompletePacket] No materials provided for {PrefabID} at cell {Cell}, using SandStone as fallback.");
                tags.Add(SimHashes.SandStone.CreateTag());
            }


			bool isBridge = def.BuildingComplete.GetComponent<ConduitBridgeBase>() || def.BuildingComplete.GetComponent<WireUtilityNetworkLink>() || def.BuildingComplete.GetComponent<LogicUtilityNetworkLink>() || PrefabID == ContactConductivePipeBridgeConfig.ID;
			int layerIndex = (int)ObjectLayer;
            // Destroy ghost/constructable if it still exists
            GameObject existing = Grid.Objects[Cell, layerIndex];

            if(existing == null && isBridge)
            {
                bool vertical = Orientation == Orientation.R90 || Orientation == Orientation.R270;
                //todo: account for other width bridges; get the offsets from bridge width instead
                int firstToCheck = vertical ? Grid.CellAbove(Cell) : Grid.CellLeft(Cell);
                int secondToCheck = vertical ? Grid.CellBelow(Cell) : Grid.CellRight(Cell);

				existing = Grid.Objects[firstToCheck, layerIndex];
                if(existing == null)
					existing = Grid.Objects[secondToCheck, layerIndex];
			}

            if (existing != null)
            {
                //if (existing.TryGetComponent<Constructable>(out Constructable con))
                //{
                //    if (NetworkIdentityRegistry.TryGet(WorkerNetId, out var identity) &&
                //       identity.TryGetComponent<WorkerBase>(out var worker))
                //    {
                //        con.initialTemperature = Temperature;
                //        con.SelectedElementsTags = tags;
                //        con.FinishConstruction(UtilityConnectionFlags, worker);
                //    }
                //}
                //else
                {
                    // Clean up using ONI's proper lifecycle to ensure automation port visualizers
                    // are removed from LogicCircuitManager.uiVisElements synchronously.
                    // Object.Destroy() would defer cleanup, leaving stale port entries
                    // that block future building placement.
                    existing.DeleteObject();
                    Grid.Objects[Cell, layerIndex] = null;

                    var builtObj = def.Build(
                        Cell,
                        Orientation,
                        null,
                        tags,
                        Temperature,
                        FacadeID,
                        playsound: false,
                        GameClock.Instance.GetTime()
                    );

                    // Apply wire/pipe connections for utility buildings
                    if (builtObj != null && (int)UtilityConnectionFlags != 0)
                    {
                        ApplyUtilityConnections(builtObj, def);
                    }
                }
            }

            // Sweep the cell for a scaffold that survived, whatever layer it is on.
            //
            // The lookup above reads one slot - Grid.Objects[Cell, def.ObjectLayer] -
            // and if the scaffold is not in that slot it is never deleted, while
            // def.Build happily adds the finished building beside it. On the client
            // that reads as a tile which is both "scheduled for construction" and
            // already there, reported from a live session: the host had 9 Tile and 13
            // GasPermeableMembrane completions out, both tile-class buildings.
            //
            // Swept by cell across every layer rather than by guessing which one,
            // because the two guesses this bug has already survived were both about
            // where the object lives. Only objects that are still under construction
            // are touched, so a finished building can never be removed by this - which
            // is what makes it safe to run after the build rather than instead of it.
            int scaffoldsCleared = 0;
            for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
            {
                var leftover = Grid.Objects[Cell, layer];
                if (leftover == null || leftover.IsNullOrDestroyed()) continue;
                // Named rather than discarded: '_' is already the Profiler scope in
                // this method, and 'out _' collides with it.
                if (!leftover.TryGetComponent<BuildingUnderConstruction>(out var scaffold)) continue;
                if (scaffold.IsNullOrDestroyed()) continue;

                // Only this building's own scaffold.
                //
                // Sweeping every layer was meant to avoid guessing which one the
                // scaffold sits on. It also deleted other buildings' scaffolds: one cell
                // holds a tile on the foundation layer and a wire on the wire layer, so
                // finishing the wire destroyed the tile's construction site on the
                // client while the host still had it. Everything the host then said
                // about that tile missed - failed lookups on the client went from about
                // one a minute to several hundred, and the tile never appeared at all.
                //
                // The prefab is what makes this safe: the object being removed has to be
                // the unfinished version of the thing that just finished.
                string scaffoldPrefab = leftover.TryGetComponent<KPrefabID>(out var leftoverId) && !leftoverId.IsNullOrDestroyed()
                    ? leftoverId.PrefabTag.Name
                    : leftover.name;

                if (scaffoldPrefab != PrefabID)
                {
                    ScaffoldsLeftAlone++;
                    continue;
                }

                Grid.Objects[Cell, layer] = null;
                leftover.DeleteObject();
                scaffoldsCleared++;
            }

            if (scaffoldsCleared > 0)
            {
                LeftoverScaffoldsCleared += scaffoldsCleared;
                DebugConsole.LogWarning(
                    $"[BuildCompletePacket] cleared {scaffoldsCleared} leftover scaffold(s) at cell {Cell} " +
                    $"after finishing {PrefabID} - they were not on layer {ObjectLayer}, which is the only " +
                    "one the finish path checks");
            }

            DebugConsole.Log($"[BuildCompletePacket] Finalized {PrefabID} at cell {Cell}");
        }

        private void ApplyUtilityConnections(GameObject go, BuildingDef def)
        {
            // Neighbours are baked into the conduit managers
            if (go.TryGetComponent<KAnimGraphTileVisualizer>(out var vis))
            {
                vis.UpdateConnections(UtilityConnectionFlags);
                vis.Refresh();
            }
        }

        private void ApplyUtilityConnections(KAnimGraphTileVisualizer vis, UtilityConnections flags)
        {
            vis.UpdateConnections(flags);
            vis.Refresh();
        }
    }
}

