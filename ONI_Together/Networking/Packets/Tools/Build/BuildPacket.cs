using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
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
    public class BuildPacket : IPacket, Shared.Interfaces.Networking.IReplayableOnRejoin
    {
        private const int MaxMaterialTagCount = 64;

        /// <summary>
        /// Build orders that were applied and produced nothing. Each one is a building
        /// the other peer has and this one does not, and until now each was logged as a
        /// success.
        /// </summary>
        public static int OrdersThatBuiltNothing { get; private set; }

        /// <summary>
        /// Construction sites given the host's address on arrival. Before this they were
        /// built and left nameless, which is not the same failure it looked like.
        /// </summary>
        public static int SitesNamedByHost { get; private set; }

        /// <summary>
        /// Orders that arrived carrying no address for their site. Expected from a
        /// client, since clients do not mint; from a host it means a site was announced
        /// before it had been filed.
        /// </summary>
        public static int SitesArrivingUnnamed { get; private set; }

        private string PrefabID;
        private int Cell;
        private Orientation Orientation;
        private List<string> MaterialTags = new List<string>();
        private PrioritySetting Priority;
        private ObjectLayer ObjectLayer;
        private bool InstantBuild;

        /// <summary>
        /// The address the sender's construction site holds, or zero.
        ///
        /// Without it a client builds the site and has nothing to file it under - it
        /// will not invent one, and refusing to is correct, since two peers inventing
        /// addresses independently is the divergence this whole registry exists to
        /// avoid. So the site stood in the colony with no address, invisible to every
        /// comparison in this project, and read for three rounds as a building the
        /// client never received.
        ///
        /// Measured: [UNFILED] WireUnderConstruction|4|46707,46452,46450,46449 on the
        /// client, at exactly the four cells the comparer called host-only, with the
        /// host holding all four filed.
        /// </summary>
        public int SiteNetId;

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

            // The address the sender's own copy has, so the receiver's copy can share
            // it. Zero when the sender has none, which is every client-issued order -
            // clients do not mint addresses.
            writer.Write(SiteNetId);
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
            SiteNetId = reader.ReadInt32();
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

            // The def's own scene layer, not Building.
            //
            // This was hardcoded, and a wire does not live on the Building layer. The
            // same hardcoding in the scenario's own build verb is what stopped it from
            // ever placing a wire, so this is the second copy of a defect already
            // measured once - the pattern this project's notes say to grep for before
            // fixing the instance in hand.
            Vector3 pos = Grid.CellToPosCBC(Cell, def.SceneLayer);

            GameObject builtItem;
            if (InstantBuild)
                builtItem = InstantBuildBuilding(def, selected_elements, pos);
            else
                builtItem = QueueBuild(def, selected_elements, pos);

            if (builtItem == null && def.ReplacementLayer != ObjectLayer.NumLayers)
                builtItem = HandleReplacementInstant(def, pos, selected_elements) ?? HandleReplacementQueued(def, pos, selected_elements);

            SetPriority(builtItem);

            // Only say it was built if something was.
            //
            // This line ran unconditionally, so a null result printed "[BuildPacket]
            // Built item Wire (BuildingDef)" - def.ToString(), which is why the message
            // looks like a success with a type name stuck on the end. A client logged
            // four of them in a run while creating none of the four sites, and the
            // cross-peer comparison found the host holding WireUnderConstruction at
            // 46449, 46450, 46452 and 46707 with nothing at those cells on the client.
            //
            // The log was the reason this looked like an architectural gap - a building
            // the client "has" cannot be a replication failure, so the search went to
            // why the host could not send an existing object instead of to why the
            // order that was sent did nothing. A success message that cannot fail is
            // worse than no message.
            if (builtItem == null)
            {
                OrdersThatBuiltNothing++;
                ThrottledLog.Warn(
                    $"[BuildPacket] {PrefabID} at cell {Cell}: the order was applied and " +
                    "produced no building - this peer is now missing what the other one has");
                return;
            }

            NameSiteFromSender(builtItem);
            DebugConsole.Log($"[BuildPacket] Built {PrefabID} at cell {Cell}");
        }

        /// <summary>
        /// Give the site the address the sender's copy has.
        ///
        /// OverrideNetId is the same call AssignmentPacket and BuildingConfigPacket use
        /// for this - it moves the registry entry and the component together, so the two
        /// cannot end up disagreeing about which number the object answers to.
        ///
        /// Only on a client. The host is where addresses come from, and a host adopting
        /// a client's number would be the peers deciding ids independently, which is the
        /// failure mode the registry exists to prevent.
        /// </summary>
        private void NameSiteFromSender(GameObject builtItem)
        {
            if (MultiplayerSession.IsHost) return;

            if (SiteNetId == 0)
            {
                SitesArrivingUnnamed++;
                return;
            }

            if (!builtItem.TryGetComponent<NetworkIdentity>(out var identity)
                || identity.IsNullOrDestroyed())
            {
                SitesArrivingUnnamed++;
                ThrottledLog.Warn(
                    $"[BuildPacket] {PrefabID} at cell {Cell} has no NetworkIdentity to " +
                    $"put the host's id {SiteNetId} on - it stays unaddressable here");
                return;
            }

            identity.OverrideNetId(SiteNetId);
            SitesNamedByHost++;
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
