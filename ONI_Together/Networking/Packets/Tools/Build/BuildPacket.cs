using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Steamworks;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shared.Profiling;
using UnityEngine;
using static PathFinder;
using static STRINGS.MISC;

namespace ONI_Together.Networking.Packets.Tools.Build
{
    public class BuildPacket : IPacket
    {
        private const int MaxMaterialTagCount = 64;

        private string PrefabID;
        private int Cell;
        private Orientation Orientation;
        private List<string> MaterialTags = new List<string>();
        private PrioritySetting Priority;
        private ObjectLayer ObjectLayer;
        private bool InstantBuild;

        public BuildPacket()
        {
        }

        public BuildPacket(string prefabID, int cell, Orientation orientation, IEnumerable<Tag> materials, ObjectLayer objectLayer, bool instantBuild = false)
        {
            using var _ = Profiler.Scope();

            // An empty id is a build order nobody can carry out.
            //
            // Four orders reached a client as "Unknown building def: " with nothing
            // after the colon. The order was sent, received and discarded, and no
            // count anywhere said so - the client simply did not get the building.
            // Named at the sending end, because that is where it can still be fixed
            // and where the def is still in hand.
            if (string.IsNullOrEmpty(prefabID))
            {
                DebugConsole.LogError(
                    $"[BuildPacket] refusing to announce a build at cell {cell} with an empty prefab id - " +
                    "the receiver cannot resolve it and would drop the order silently");
            }

            PrefabID = prefabID;
            Cell = cell;
            Orientation = orientation;
            MaterialTags = materials.Select(t => t.ToString()).ToList();
            InstantBuild = instantBuild;

            // The priority is a nicety; the build order is not.
            //
            // PlanScreen.Instance existing does not mean it can answer: the priority
            // it reports comes from a screen that is only set up once the build menu
            // has been opened, and asking before then throws. The existing null check
            // guards the wrong thing - the instance is there, its innards are not.
            //
            // Caught rather than pre-checked, because what is null inside PlanScreen
            // is Klei's business and a future build may move it. Losing the whole
            // announcement over a priority number is the one outcome that must not
            // happen: the order still has to reach the other peer.
            //
            // Found by a scenario that ordered a build without touching the UI, which
            // is also what any headless or automated caller looks like.
            try
            {
                if (PlanScreen.Instance)
                    // PlanScreen.Instance was dereferenced with no guard, so a build order that
            // did not come from a player clicking the plan screen either threw or left
            // the priority unset. See PriorityWire.
            Priority = PriorityWire.SampleBuilding();
            }
            catch (System.Exception ex)
            {
                DebugConsole.LogWarning(
                    $"[BuildPacket] no build priority available ({ex.GetType().Name}); " +
                    "sending the order without one");
            }

            ObjectLayer = objectLayer;
        }

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();

            writer.Write(PrefabID);
            writer.Write(Cell);
            writer.Write((int)Orientation);
            writer.Write(MaterialTags.Count);
            foreach (var tag in MaterialTags)
                writer.Write(tag);

            PriorityWire.Write(writer, Priority);

            writer.Write((int)ObjectLayer);
            writer.Write(InstantBuild);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();

            PrefabID = reader.ReadString();
            Cell = reader.ReadInt32();
            Orientation = (Orientation)reader.ReadInt32();
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxMaterialTagCount)
            {
                DebugConsole.LogWarning($"[BuildPacket] Invalid material tag count: {count}");
                Cell = Grid.InvalidCell;
                MaterialTags = [];
                return;
            }
            MaterialTags = new List<string>();
            for (int i = 0; i < count; i++)
                MaterialTags.Add(reader.ReadString());

            Priority = PriorityWire.Read(reader);
            ObjectLayer = (ObjectLayer)reader.ReadInt32();
            InstantBuild = reader.ReadBoolean();
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (!Grid.IsValidCell(Cell))
            {
                DebugConsole.LogWarning($"[BuildPacket] Invalid cell: {Cell}");
                return;
            }

            var def = Assets.GetBuildingDef(PrefabID);
            if (def == null)
            {
                DebugConsole.LogWarning($"[BuildPacket] Unknown building def: {PrefabID}");
                return;
            }

            var selected_elements = MaterialTags.Select(t => TagManager.Create(t)).ToList();
            Vector3 pos = Grid.CellToPosCBC(Cell, Grid.SceneLayer.Building);

            GameObject builtItem;
            if (InstantBuild)
                builtItem = InstantBuildBuilding(def, selected_elements, pos);
            else
                builtItem = QueueBuild(def, selected_elements, pos);

            if (builtItem == null && def.ReplacementLayer != ObjectLayer.NumLayers)
                builtItem = HandleReplacementInstant(def, pos, selected_elements) ?? HandleReplacementQueued(def, pos, selected_elements);

            SetPriority(builtItem);
            DebugConsole.Log("[BuildPacket] Built item " + def);
        }

        private GameObject QueueBuild(BuildingDef def, List<Tag> selected_elements, Vector3 pos)
        {
            GameObject visualizer = Util.KInstantiate(def.BuildingPreview, pos);
            return def.TryPlace(visualizer, pos, Orientation, selected_elements, "DEFAULT_FACADE");
        }

        private GameObject HandleReplacementQueued(BuildingDef def, Vector3 pos, List<Tag> selected_elements)
        {
            GameObject replacementCandidate = def.GetReplacementCandidate(Cell);
            if (replacementCandidate == null || def.IsReplacementLayerOccupied(Cell))
                return null;

            BuildingComplete component = replacementCandidate.GetComponent<BuildingComplete>();
            if (component == null || !component.Def.Replaceable || !def.CanReplace(replacementCandidate))
                return null;

            // Fetched from a component that was checked one line earlier and
            // then dereferenced without a check of its own.
            if (!replacementCandidate.TryGetComponent<PrimaryElement>(out var replacementElement))
                return null;
            Tag tag = replacementElement.Element.tag;
            if (tag.GetHash() == (int)SimHashes.StableSnow)
                tag = SimHashes.Snow.CreateTag();
            if (component.Def == def && selected_elements[0] == tag)
                return null;

            GameObject visualizer = Util.KInstantiate(def.BuildingPreview, pos);
            GameObject builtItem = def.TryReplaceTile(visualizer, pos, Orientation, selected_elements, "DEFAULT_FACADE");
            Grid.Objects[Cell, (int)def.ReplacementLayer] = builtItem;
            return builtItem;
        }

        private GameObject InstantBuildBuilding(BuildingDef def, List<Tag> selected_elements, Vector3 pos)
        {
            if (!def.IsValidBuildLocation(null, pos, Orientation) || !def.IsValidPlaceLocation(null, pos, Orientation, out _))
                return null;

            if (def.ObjectLayer == ObjectLayer.Building)
            {
                def.RunOnArea(Cell, Orientation, offset_cell =>
                {
                    if (Uprootable.CanUproot(Grid.Objects[offset_cell, (int)def.ObjectLayer], out var uprootable))
                        uprootable.CompleteWork(null);
                });
            }
            else if (def.ObjectLayer == ObjectLayer.Backwall)
            {
                def.RunOnArea(Cell, Orientation, offset_cell =>
                {
                    if (BackwallManager.HasBackwall(offset_cell))
                        SimMessages.Dig(offset_cell, -1, skipEvent: true, backwall: true);
                });
            }

            float temp = Mathf.Min(def.Temperature, ElementLoader.GetMinMeltingPointAmongElements(selected_elements) - 10f);
            return def.Build(Cell, Orientation, null, selected_elements, temp, "DEFAULT_FACADE", playsound: false, GameClock.Instance.GetTime());
        }

        private GameObject HandleReplacementInstant(BuildingDef def, Vector3 pos, List<Tag> selected_elements)
        {
            if (!InstantBuild)
                return null;

            GameObject replacementCandidate = def.GetReplacementCandidate(Cell);
            if (replacementCandidate == null || def.IsReplacementLayerOccupied(Cell))
                return null;

            BuildingComplete component = replacementCandidate.GetComponent<BuildingComplete>();
            if (component == null || !component.Def.Replaceable || !def.CanReplace(replacementCandidate))
                return null;

            // Fetched from a component that was checked one line earlier and
            // then dereferenced without a check of its own.
            if (!replacementCandidate.TryGetComponent<PrimaryElement>(out var replacementElement))
                return null;
            Tag tag = replacementElement.Element.tag;
            if (tag.GetHash() == (int)SimHashes.StableSnow)
                tag = SimHashes.Snow.CreateTag();
            if (component.Def == def && selected_elements[0] == tag)
                return null;

            if (!def.IsValidBuildLocation(null, pos, Orientation, replace_tile: true) ||
                !def.IsValidPlaceLocation(null, pos, Orientation, replace_tile: true, out _))
                return null;

            float temp = Mathf.Min(def.Temperature, ElementLoader.GetMinMeltingPointAmongElements(selected_elements) - 10f);
            return def.Build(Cell, Orientation, null, selected_elements, temp, "DEFAULT_FACADE", playsound: false, GameClock.Instance.GetTime());
        }

        private void SetPriority(GameObject gameObject)
        {
            if (gameObject == null)
                return;

            // A GameObject destroyed earlier in this handler is not caught by
            // ?., and GetComponent on it throws.
            if (gameObject.IsNullOrDestroyed()) return;
            Prioritizable prioritizable = gameObject.GetComponent<Prioritizable>();
            prioritizable?.SetMasterPriority(Priority);
        }

    }
}
