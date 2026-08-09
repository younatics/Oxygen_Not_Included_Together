using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ONI_Together.DebugTools;
using UnityEngine;
using static LogicGateVisualizer;

namespace ONI_Together.Misc
{
    public static class BuildingUtils
    {
        public static bool ValidCell(GameObject visualizer, BuildingDef def, int cell, Orientation orientation)
        {
            if (Grid.IsValidCell(cell)
                && Grid.IsVisible(cell))
            {
                bool IsValidPlaceLocation = def.IsValidPlaceLocation(visualizer, cell, orientation, out string failReason);
                bool IgnorableFailReason =
                    failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_WALL
                    || failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_CORNER
                    || failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_CORNER_FLOOR
                    || (failReason == global::STRINGS.UI.TOOLTIPS.HELP_BUILDLOCATION_BACK_WALL_REQUIRED);

                bool validCell = (IsValidPlaceLocation || IgnorableFailReason);
                bool replacement = false;
                return (validCell || replacement);
            }

            return false;
        }

        public static void EncodeStorageContents(Storage storage, Dictionary<string, Variant> optionalValues, string keyPrefix = "")
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(storage.capacityKg);

            var validItems = new List<GameObject>();
            for (int i = 0; i < storage.items.Count; i++)
            {
                var go = storage.items[i];
                if (go == null) continue;
                var pe = go.GetComponent<PrimaryElement>();
                if (pe == null || pe.Mass <= 0f) continue;
                if (!go.TryGetComponent<KPrefabID>(out _)) continue;
                validItems.Add(go);
            }

            writer.Write(validItems.Count);
            foreach (var go in validItems)
            {
                var pe = go.GetComponent<PrimaryElement>();
                var prefabID = go.GetComponent<KPrefabID>();
                writer.Write(prefabID.PrefabTag.GetHashCode());
                writer.Write(pe.Mass);
                writer.Write(pe.Temperature);
                writer.Write(pe.DiseaseIdx);
                writer.Write(pe.DiseaseCount);
            }

            optionalValues[keyPrefix + "stor"] = ms.ToArray();
        }

        public static void RebuildStorageFromData(Storage storage, Dictionary<string, Variant> data, string keyPrefix = "", string diseaseReason = "Multiplayer Sync")
        {
            if (storage == null) return;

            if (data.TryGetValue(keyPrefix + "stor", out var blobVar) && blobVar.ByteArray != null)
            {
                RebuildFromBlob(storage, blobVar.ByteArray, diseaseReason);
                return;
            }
            
            DebugConsole.LogError($"[Storage/RebuildStorageFromData] Failed to rebuild storage from data! Key: {keyPrefix + "stor"} not found!");
        }

        private struct StoredItem
        {
            public int Hash;
            public float Mass;
            public float Temperature;
            public byte DiseaseIdx;
            public int DiseaseCount;
        }

        private static readonly List<StoredItem> _incoming = new List<StoredItem>();

        private static void RebuildFromBlob(Storage storage, byte[] blob, string diseaseReason)
        {
            using var ms = new MemoryStream(blob);
            using var reader = new BinaryReader(ms);

            float capacityKg = reader.ReadSingle();
            int count = reader.ReadInt32();

            _incoming.Clear();
            for (int i = 0; i < count; i++)
            {
                _incoming.Add(new StoredItem
                {
                    Hash = reader.ReadInt32(),
                    Mass = reader.ReadSingle(),
                    Temperature = reader.ReadSingle(),
                    DiseaseIdx = reader.ReadByte(),
                    DiseaseCount = reader.ReadInt32(),
                });
            }

            // Update in place when the same things are still in there.
            //
            // This used to clear the storage and build it again on every packet.
            // Structure state arrives twice a second and a toilet's water mass
            // drifts continuously, so "changed" was true almost every time - and
            // each rebuild destroyed every stored object and created new ones,
            // which minted new NetIds. One live client registered the same water
            // pile at the same cell 2109 times in 29 minutes, two ids
            // alternating every half second, and did it for six cells at once:
            // 14655 of its 19106 workable registrations were this.
            if (TryUpdateInPlace(storage, diseaseReason))
                return;

            ClearStorage(storage);
            if (count == 0) return;

            for (int i = 0; i < _incoming.Count; i++)
            {
                int hash = _incoming[i].Hash;
                float mass = _incoming[i].Mass;
                float temperature = _incoming[i].Temperature;
                byte diseaseIdx = _incoming[i].DiseaseIdx;
                int diseaseCount = _incoming[i].DiseaseCount;
                if (mass <= 0f) continue;

                Tag tag = new Tag(hash);
                Element elementByHash = ElementLoader.GetElement(tag);
                if (elementByHash != null)
                {
                    storage.AddElement(elementByHash.id, mass, temperature, diseaseIdx, diseaseCount);
                }
                else
                {
                    var item = Assets.GetPrefab(tag);
                    if (item == null) continue;

                    var scrapObject = GameUtil.KInstantiate(item, storage.transform.position, Grid.SceneLayer.Ore);
                    if (scrapObject.TryGetComponent<PrimaryElement>(out var pe))
                    {
                        pe.Mass = mass;
                        pe.Temperature = temperature;
                        if (diseaseIdx != byte.MaxValue)
                            pe.AddDisease(diseaseIdx, diseaseCount, diseaseReason);
                    }
                    scrapObject.SetActive(true);
                    storage.Store(scrapObject, true, true);
                }
            }
        }
        
        /// <summary>
        /// True if the storage already holds exactly these things, in which case
        /// only their mass, temperature and disease need correcting - no object
        /// is destroyed and none is created, so nothing takes a new NetId.
        ///
        /// Composition is compared by prefab tag and order, which is how the
        /// blob is written, so a genuine change - something added, removed or
        /// swapped - still falls through to the full rebuild.
        /// </summary>
        private static bool TryUpdateInPlace(Storage storage, string diseaseReason)
        {
            var items = storage.items;
            if (items == null) return false;

            // Entries with no mass are skipped by the rebuild, so they must be
            // skipped here too or the counts would never line up.
            int expected = 0;
            for (int i = 0; i < _incoming.Count; i++)
            {
                if (_incoming[i].Mass > 0f) expected++;
            }
            if (items.Count != expected) return false;

            int slot = 0;
            for (int i = 0; i < _incoming.Count; i++)
            {
                if (_incoming[i].Mass <= 0f) continue;

                var go = items[slot];
                if (go == null) return false;
                if (!go.TryGetComponent<PrimaryElement>(out var pe)) return false;
                // The same accessor the encoder used. Tag exposes GetHash() and
                // GetHashCode() and they are not the same number, so comparing
                // one against the other never matched and the storage was torn
                // down and rebuilt on every packet regardless.
                if (!go.TryGetComponent<KPrefabID>(out var storedPrefab)) return false;
                if (storedPrefab.PrefabTag.GetHashCode() != _incoming[i].Hash) return false;

                pe.Mass = _incoming[i].Mass;
                pe.Temperature = _incoming[i].Temperature;
                if (_incoming[i].DiseaseIdx != byte.MaxValue && pe.DiseaseIdx != _incoming[i].DiseaseIdx)
                    pe.AddDisease(_incoming[i].DiseaseIdx, _incoming[i].DiseaseCount, diseaseReason);

                slot++;
            }

            return true;
        }

        private static void ClearStorage(Storage storage)
        {
            for (int i = storage.items.Count - 1; i >= 0; i--)
                storage.items[i].DeleteObject();
            storage.items.Clear();
        }
        
        // UP = Utility Path
        private const int UP_FIRST_CELL_BITS = 22;
        private const int UP_SEG_BITS = 4;
        private const int UP_SEG_COUNT_BITS = 2;
        private const int UP_MAX_SEGMENTS = 2;
        private const int UP_MAX_LEN_PER_SEG = 4;
        private const int UP_MAX_CELLS_PER_CHUNK = 1 + UP_MAX_SEGMENTS * UP_MAX_LEN_PER_SEG; // 9
        
        // Derived bit masks/shifts
        private const int UP_FIRST_CELL_MASK = (1 << UP_FIRST_CELL_BITS) - 1;
        private const int UP_SEGMENTS_BITS = UP_SEG_BITS * UP_MAX_SEGMENTS;
        private const int UP_SEGMENTS_MASK = (1 << UP_SEGMENTS_BITS) - 1;
        private const int UP_SEGMENTS_SHIFT = UP_FIRST_CELL_BITS;
        private const int UP_SEG_COUNT_MASK = (1 << UP_SEG_COUNT_BITS) - 1;
        private const int UP_SEG_COUNT_SHIFT = UP_FIRST_CELL_BITS + UP_SEGMENTS_BITS;
        
        /// <summary>
        /// Encodes a utility build path into an array of 32-bit chunks, each packing up to 9 cells.
        /// Bits 0-21: firstCell index. Bits 22-29: up to 2 direction-run segments (4-bit each:
        /// 2-bit direction + 2-bit run length-1). Bits 30-31: segment count.
        /// </summary>
        public static uint[] EncodeUtilityPath(List<BaseUtilityBuildTool.PathNode> path)
        {
            if (path == null || path.Count <= 1)
                return null;

            List<uint> chunks = new List<uint>();
            int pos = 0;
            int count = path.Count;

            while (pos < count)
            {
                int chunkEnd = pos + UP_MAX_CELLS_PER_CHUNK;
                if (chunkEnd > count)
                    chunkEnd = count;

                int chunkSize = chunkEnd - pos;
                if (chunkSize <= 1)
                    break;

                int firstCell = path[pos].cell;
                uint data = (uint)(firstCell & UP_FIRST_CELL_MASK);

                int segmentsPacked = 0;
                int segmentCount = 0;
                int i = pos + 1;

                while (i < chunkEnd && segmentCount < UP_MAX_SEGMENTS)
                {
                    int from = path[i - 1].cell;
                    int to = path[i].cell;
                    UtilityConnections dir = UtilityConnectionsExtensions.DirectionFromToCell(from, to);
                    if (dir == (UtilityConnections)0)
                        break;

                    int dirIndex;
                    if (dir == UtilityConnections.Right) dirIndex = 0;
                    else if (dir == UtilityConnections.Up) dirIndex = 1;
                    else if (dir == UtilityConnections.Left) dirIndex = 2;
                    else dirIndex = 3;

                    int len = 1;
                    i++;
                    while (i < chunkEnd && len < UP_MAX_LEN_PER_SEG)
                    {
                        int prev = path[i - 1].cell;
                        int curr = path[i].cell;
                        if (UtilityConnectionsExtensions.DirectionFromToCell(prev, curr) != dir)
                            break;
                        len++;
                        i++;
                    }

                    int seg = (dirIndex & UP_SEG_COUNT_MASK) | (((len - 1) & UP_SEG_COUNT_MASK) << UP_SEG_COUNT_BITS);
                    segmentsPacked |= seg << (segmentCount * UP_MAX_LEN_PER_SEG);
                    segmentCount++;
                }

                data |= (uint)(segmentsPacked & UP_SEGMENTS_MASK) << UP_SEGMENTS_SHIFT;
                data |= (uint)(segmentCount & UP_SEG_COUNT_MASK) << UP_SEG_COUNT_SHIFT;

                chunks.Add(data);
                pos = i;
            }

            return chunks.ToArray();
        }
        
        /// <summary>
        /// Decodes an array of 9-cell chunk uints back into a flat int[] of Grid cell indices.
        /// Each chunk is decoded via DecodeChunk and concatenated in order.
        /// </summary>
        public static int[] DecodeUtilityPath(uint[] pathData)
        {
            if (pathData == null || pathData.Length == 0)
                return null;

            List<int> cells = new List<int>(pathData.Length * UP_MAX_CELLS_PER_CHUNK);

            foreach (uint chunk in pathData)
            {
                if (chunk == 0)
                    continue;

                int[] chunkCells = DecodeUtilityPathChunk(chunk);
                if (chunkCells != null)
                    cells.AddRange(chunkCells);
            }

            return cells.ToArray();
        }

        /// <summary>
        /// Encodes a utility build path into an array of 64-bit chunks. Lower 32 bits = path data
        /// (same as EncodeUtilityPath). Upper 32 bits = validity bitmask (bits 0-8 for up to 9 cells).
        /// </summary>
        public static ulong[] EncodeUtilityPathWithValidity(List<BaseUtilityBuildTool.PathNode> path)
        {
            if (path == null || path.Count <= 1)
                return null;

            List<ulong> chunks = new List<ulong>();
            int pos = 0;
            int count = path.Count;

            while (pos < count)
            {
                int chunkEnd = pos + UP_MAX_CELLS_PER_CHUNK;
                if (chunkEnd > count)
                    chunkEnd = count;

                uint validityMask = 0;
                for (int j = pos; j < chunkEnd; j++)
                {
                    if (path[j].valid)
                        validityMask |= 1u << (j - pos);
                }

                int firstCell = path[pos].cell;
                uint data = (uint)(firstCell & UP_FIRST_CELL_MASK);

                int segmentsPacked = 0;
                int segmentCount = 0;
                int i = pos + 1;

                while (i < chunkEnd && segmentCount < UP_MAX_SEGMENTS)
                {
                    int from = path[i - 1].cell;
                    int to = path[i].cell;
                    UtilityConnections dir = UtilityConnectionsExtensions.DirectionFromToCell(from, to);
                    if (dir == (UtilityConnections)0)
                        break;

                    int dirIndex;
                    if (dir == UtilityConnections.Right) dirIndex = 0;
                    else if (dir == UtilityConnections.Up) dirIndex = 1;
                    else if (dir == UtilityConnections.Left) dirIndex = 2;
                    else dirIndex = 3;

                    int len = 1;
                    i++;
                    while (i < chunkEnd && len < UP_MAX_LEN_PER_SEG)
                    {
                        int prev = path[i - 1].cell;
                        int curr = path[i].cell;
                        if (UtilityConnectionsExtensions.DirectionFromToCell(prev, curr) != dir)
                            break;
                        len++;
                        i++;
                    }

                    int seg = (dirIndex & UP_SEG_COUNT_MASK) | (((len - 1) & UP_SEG_COUNT_MASK) << UP_SEG_COUNT_BITS);
                    segmentsPacked |= seg << (segmentCount * UP_MAX_LEN_PER_SEG);
                    segmentCount++;
                }

                data |= (uint)(segmentsPacked & UP_SEGMENTS_MASK) << UP_SEGMENTS_SHIFT;
                data |= (uint)(segmentCount & UP_SEG_COUNT_MASK) << UP_SEG_COUNT_SHIFT;

                chunks.Add(((ulong)validityMask << 32) | data);
                pos = i;
            }

            return chunks.ToArray();
        }

        /// <summary>
        /// Decodes a single 32-bit chunk into an array of Grid cell indices.
        /// Bits 0–21: firstCell. Bits 22–29: up to two 4-bit direction-run segments
        /// (2-bit direction, 2-bit run length − 1). Bits 30–31: segment count.
        /// Reconstructs cells by walking from firstCell through each direction-run.
        /// Returns null if data is 0 or firstCell is invalid.
        /// </summary>
        public static int[] DecodeUtilityPathChunk(uint data)
        {
            if (data == 0)
                return null;

            int firstCell = (int)(data & ((1 << UP_FIRST_CELL_BITS) - 1));
            int segmentsPacked = (int)((data >> UP_FIRST_CELL_BITS) & ((1 << (UP_SEG_BITS * UP_MAX_SEGMENTS)) - 1));
            int segmentCount = (int)((data >> (UP_FIRST_CELL_BITS + UP_SEG_BITS * UP_MAX_SEGMENTS)) & ((1 << UP_SEG_COUNT_BITS) - 1));

            if (!Grid.IsValidCell(firstCell))
                return null;

            List<int> cells = new List<int>(UP_MAX_CELLS_PER_CHUNK);
            cells.Add(firstCell);
            int cell = firstCell;

            for (int s = 0; s < segmentCount && s < UP_MAX_SEGMENTS; s++)
            {
                int seg = (segmentsPacked >> (s * UP_SEG_BITS)) & 0xF;
                int dir = seg & 0x3;
                int len = ((seg >> 2) & 0x3) + 1;

                int delta;
                switch (dir)
                {
                    case 0: delta = 1; break;
                    case 1: delta = Grid.WidthInCells; break;
                    case 2: delta = -1; break;
                    case 3: delta = -Grid.WidthInCells; break;
                    default: continue;
                }

                for (int i = 0; i < len; i++)
                {
                    cell += delta;
                    if (!Grid.IsValidCell(cell))
                        break;
                    cells.Add(cell);
                }
            }

            return cells.ToArray();
        }

        /// <summary>
        /// Iterates every ILogicUIElement in uiVisElements and removes any whose
        /// cell has no Building component on any object layer. Called periodically
        /// to sweep up orphaned port entries that survive the normal cleanup path.
        /// </summary>
        public static void CleanupOrphanedLogicVisElements()
        {
            var mgr = Game.Instance.logicCircuitManager;
            var elems = mgr.GetVisElements();
            for (int i = elems.Count - 1; i >= 0; i--)
            {
                var elem = elems[i];
                int cell = elem.GetLogicUICell();
                if (!Grid.IsValidCell(cell))
                    continue;

                bool hasBuilding = false;
                foreach (var layer in Grid.ObjectLayers)
                {
                    if (layer.TryGetValue(cell, out var obj) && obj != null)
                    {
                        if (obj.GetComponent<Building>() != null)
                        {
                            hasBuilding = true;
                            break;
                        }
                    }
                }

                if (!hasBuilding)
                    mgr.RemoveVisElem(elem);
            }
        }

    }
}
