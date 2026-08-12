using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.World;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Can a wrong id delete a building?
    ///
    /// Host destruction is replicated now, which closed a permanent divergence: a
    /// dig ran four tiles to zero hit points on the host and the client kept them
    /// at 29, 26 and 9 out of 100 forever, because the host cannot correct a
    /// building it no longer has. After the fix the two peers agree on 4015
    /// buildings each and the four phantoms are gone.
    ///
    /// The cost of that is a packet whose whole job is to destroy something in
    /// another player's colony, addressed by an id - on a codebase where a
    /// GasConduit and a Clay claim the same id on every single run. If a
    /// mis-resolved id can reach the destroy call, the fix for a cosmetic
    /// disagreement becomes a building deleted out of a working base, which is far
    /// worse than the bug it replaced.
    ///
    /// So the refusal is the part that gets tested. The positive path is already
    /// proven end to end by the two-box comparison; nothing here destroys anything,
    /// which is also why it is safe to run against a live colony.
    /// </summary>
    public static class BuildingRemovalTests
    {
        private static GameObject FindRegisteredBuilding(out int netId)
        {
            netId = 0;
            foreach (var kvp in NetworkIdentityRegistry.AllEntries)
            {
                var identity = kvp.Value;
                if (identity.IsNullOrDestroyed() || identity.gameObject.IsNullOrDestroyed())
                    continue;
                if (identity.gameObject.GetComponent<BuildingComplete>() == null)
                    continue;
                if (!Grid.IsValidCell(Grid.PosToCell(identity.gameObject)))
                    continue;

                netId = kvp.Key;
                return identity.gameObject;
            }
            return null;
        }

        [UnitTest(name: "A removal naming the wrong prefab destroys nothing", category: "Removal")]
        public static UnitTestResult WrongPrefabIsRefused()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");
            if (MultiplayerSession.IsHost)
                return UnitTestResult.Skip("the handler is client-side; the host ignores these by design");

            var target = FindRegisteredBuilding(out int netId);
            if (target == null)
                return UnitTestResult.Skip("no registered BuildingComplete to aim at");

            string realPrefab = target.PrefabID().ToString();
            int cell = Grid.PosToCell(target);
            int refusedBefore = BuildingRemovedPacket.TestRefused;

            // Same id and cell, a prefab this object is not. That is what a
            // mis-resolved id looks like from the receiver's side.
            new BuildingRemovedPacket(netId, cell, realPrefab + "_NotThisPrefab").OnDispatched();

            if (target.IsNullOrDestroyed())
            {
                return UnitTestResult.Fail(
                    $"'{realPrefab}' at cell {cell} was destroyed by a removal naming a different " +
                    "prefab - a mixed-up id can delete a building");
            }
            if (BuildingRemovedPacket.TestRefused <= refusedBefore)
            {
                return UnitTestResult.Fail(
                    "the building survived but nothing was counted as refused, so the mismatch " +
                    "was not noticed - it survived for some other reason and the guard is untested");
            }

            return UnitTestResult.Pass($"refused a mismatched removal for '{realPrefab}' at cell {cell}");
        }

        [UnitTest(name: "A removal naming the wrong cell destroys nothing", category: "Removal")]
        public static UnitTestResult WrongCellIsRefused()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");
            if (MultiplayerSession.IsHost)
                return UnitTestResult.Skip("the handler is client-side; the host ignores these by design");

            var target = FindRegisteredBuilding(out int netId);
            if (target == null)
                return UnitTestResult.Skip("no registered BuildingComplete to aim at");

            string realPrefab = target.PrefabID().ToString();
            int cell = Grid.PosToCell(target);

            // A cell far enough away to be a different one, still on the grid.
            int elsewhere = cell + 1;
            if (!Grid.IsValidCell(elsewhere) || elsewhere == cell)
                return UnitTestResult.Skip("could not pick a second valid cell");

            int refusedBefore = BuildingRemovedPacket.TestRefused;
            int notPresentBefore = BuildingRemovedPacket.TestNotPresent;

            new BuildingRemovedPacket(netId, elsewhere, realPrefab).OnDispatched();

            if (target.IsNullOrDestroyed())
            {
                return UnitTestResult.Fail(
                    $"'{realPrefab}' at cell {cell} was destroyed by a removal naming cell {elsewhere}");
            }

            // Either outcome is correct: the id resolves to an object whose cell
            // disagrees (refused), or the named cell holds nothing (not present).
            // What must not happen is a destruction.
            bool noticed = BuildingRemovedPacket.TestRefused > refusedBefore
                        || BuildingRemovedPacket.TestNotPresent > notPresentBefore;
            if (!noticed)
            {
                return UnitTestResult.Fail(
                    "the building survived but the packet counted neither a refusal nor an absence, " +
                    "so nothing proves the cell was checked");
            }

            return UnitTestResult.Pass($"refused a removal aimed at cell {elsewhere} for '{realPrefab}'");
        }

        /// <summary>
        /// The host must never act on these. It is the only peer that sends them,
        /// and a host that also applied them would destroy its own building twice -
        /// once for real and once from an echo.
        /// </summary>
        [UnitTest(name: "The host ignores building removals", category: "Removal")]
        public static UnitTestResult HostIgnoresRemovals()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");
            if (!MultiplayerSession.IsHost)
                return UnitTestResult.Skip("not the host");

            var target = FindRegisteredBuilding(out int netId);
            if (target == null)
                return UnitTestResult.Skip("no registered BuildingComplete to aim at");

            string prefab = target.PrefabID().ToString();
            int cell = Grid.PosToCell(target);

            // Everything correct, which is the dangerous case: if the host applied
            // it, this would destroy a real building.
            new BuildingRemovedPacket(netId, cell, prefab).OnDispatched();

            if (target.IsNullOrDestroyed())
                return UnitTestResult.Fail($"the host destroyed its own '{prefab}' at cell {cell}");

            return UnitTestResult.Pass($"host ignored a fully matching removal for '{prefab}'");
        }
    }
}
