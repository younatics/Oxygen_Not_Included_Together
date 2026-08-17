using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// AUDIT #3 - do the two peers address the same object by the same NetId?
    ///
    /// That is a cross-peer property, so no single-box assertion can settle it.
    /// What this class does instead is dump each peer's (path, prefab, cell) -> id
    /// table as [NETID] records and assert only the invariants that really are
    /// local. testing/netid_compare.py then compares two dumps exactly.
    ///
    /// An earlier version guessed instead: it flagged a run of consecutive ids
    /// covering several cells as proof that the cell was missing from the hash.
    /// That is true for the workable path, but NetworkIdentity.RegisterIdentity
    /// sends anything with a Building component to GetDeterministicBuildingId,
    /// which XORs the cell in - and XORing adjacent cells produces adjacent ids.
    /// So it reported every row of tiles as a bug. Heuristics that cannot tell a
    /// correct implementation from a broken one are worse than no test at all,
    /// hence the exact dump.
    /// </summary>
    public static class NetIdDeterminismTests
    {
        private struct Entry
        {
            public int NetId;
            public int Cell;
            public string Prefab;
            public string Kind;   // which NetIdHelper branch produced this id
        }

        /// <summary>
        /// Mirrors NetworkIdentity.RegisterIdentity's branch order. If that order
        /// ever changes this must follow, or the dump mislabels rows.
        /// </summary>
        private static string ClassifyKind(GameObject go)
        {
            // Mobility is tagged because it decides how to read a cell
            // mismatch. A building in a different cell on the two peers is a
            // bug; a critter or a hauled ore pile in a different cell is just
            // where it happens to be standing. Without the tag the comparer
            // cannot tell those apart, and neither can a reader.
            string prefix = IsMobile(go) ? "mobile/" : "";

            if (go.TryGetComponent<Building>(out _)) return prefix + "building";
            if (go.TryGetComponent<Workable>(out var w)) return prefix + "workable:" + w.GetType().Name;
            return prefix + "entity";
        }

        /// <summary>
        /// Anything that walks. Its cell is where it happened to be standing
        /// when the dump ran, so cell-based invariants do not apply to it.
        /// </summary>
        private static bool IsMobile(GameObject go)
            => go.TryGetComponent<Navigator>(out _) || go.TryGetComponent<MinionIdentity>(out _);

        private static List<Entry> Collect(bool staticOnly = false)
        {
            var list = new List<Entry>();
            foreach (var identity in NetworkIdentityRegistry.AllIdentities)
            {
                if (identity == null || identity.gameObject == null) continue;

                var go = identity.gameObject;
                if (staticOnly && IsMobile(go)) continue;

                int cell = Grid.PosToCell(go);
                if (!Grid.IsValidCell(cell)) continue;

                list.Add(new Entry
                {
                    NetId = identity.NetId,
                    Cell = cell,
                    Prefab = go.PrefabID().ToString(),
                    Kind = ClassifyKind(go)
                });
            }
            return list;
        }

        /// <summary>
        /// Objects whose PrefabID names no prefab at all.
        ///
        /// Three rows in the last run had the host holding PuftEgg, BasicPlantBar and
        /// BasicPlantFood while the client held something called "Creature" at the same
        /// cell carrying the same NetId. The ids agree, so this is not an addressing
        /// failure - it is one peer reading a different name off the same object. And
        /// "Creature" is not a prefab in this game; it is GameTags.Creature, so a
        /// KPrefabID is reporting a generic tag as its identity.
        ///
        /// Which matters beyond a cosmetic dump, because that string is an input to the
        /// deterministic id: NetIdHelper hashes the prefab name, so an object whose name
        /// reads differently on the two peers computes a different id on each, and the
        /// only reason these three still line up is that they were named by the host
        /// rather than computed. The next one to be computed locally will not.
        ///
        /// Assets.GetPrefab is the test, not a list of known-bad names: any PrefabID
        /// that resolves to no prefab is the same defect whatever it is called. Unity's
        /// object name is printed beside it because an instantiated prefab keeps it -
        /// "PuftEgg(Clone)" against a PrefabID of "Creature" says the tag was overwritten
        /// on a real egg, and a name of "Creature" says something built it that way.
        /// The two have different causes and nothing so far distinguishes them.
        /// </summary>
        private static void DumpNamesThatAreNotPrefabs()
        {
            int reported = 0;

            foreach (var identity in NetworkIdentityRegistry.AllIdentities)
            {
                if (identity == null || identity.gameObject == null) continue;
                if (reported >= 20) break;

                var go = identity.gameObject;
                string prefabId = go.PrefabID().ToString();
                if (string.IsNullOrEmpty(prefabId)) continue;
                if (Assets.GetPrefab(new Tag(prefabId)) != null) continue;

                reported++;
                string tags = "none";
                if (go.TryGetComponent<KPrefabID>(out var kpid) && kpid.Tags != null)
                    tags = string.Join(",", kpid.Tags.Take(8).Select(t => t.ToString()));

                DebugConsole.LogWarning(
                    $"[NETID-ALIAS] netId={identity.NetId} cell={Grid.PosToCell(go)} " +
                    $"prefabId='{prefabId}' unityName='{go.name}' tags=[{tags}] - " +
                    "this PrefabID names no prefab, and it is an input to the id hash");
            }

            if (reported > 0)
                DebugConsole.LogWarning($"[NETID-ALIAS] {reported} object(s) carry a PrefabID that is not a prefab");
        }

        /// <summary>
        /// Loose items the host named and never told anybody about.
        ///
        /// Fifteen objects ended a run in that state - Dirt, Sand, Cuprite, a hatch egg -
        /// and the only evidence was that the host log had no registration line for them,
        /// which does not mean what it looks like: the registration message goes through
        /// NetIdHelper.Note and that is silent whenever the caller asks for quiet.
        ///
        /// So the object carries the answer now and this prints it. The set matters
        /// because an unannounced object is one the client can never be told about by any
        /// path except a lookup failure and a resolve request, which is repair rather
        /// than replication - and it only fires if a packet happens to be addressed to it.
        ///
        /// Host only. A client announces nothing, so every one of its objects would
        /// qualify and the row would say nothing at all.
        /// </summary>
        private static void DumpNeverAnnounced()
        {
            if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession) return;

            int reported = 0, total = 0;
            foreach (var identity in NetworkIdentityRegistry.AllIdentities)
            {
                if (identity == null || identity.gameObject == null) continue;
                if (identity.NetId == 0 || identity.WasAnnounced) continue;

                var go = identity.gameObject;

                // Only what replication is supposed to carry. Buildings and duplicants
                // travel by other paths on purpose, and listing them here would bury the
                // handful this is looking for under thousands that are fine.
                if (!go.TryGetComponent<Pickupable>(out var pickup)) continue;
                if (pickup.storage != null) continue;

                total++;
                if (reported++ >= 25) continue;

                DebugConsole.Log(
                    $"[NETID-UNANNOUNCED] {go.PrefabID()}|{Grid.PosToCell(go)}|{identity.NetId}");
            }

            if (total > 0)
                DebugConsole.Log($"[NETID-UNANNOUNCED] total {total} loose item(s) never announced");
        }

        [UnitTest(name: "Dump the NetId table for cross-peer comparison", category: "NetId")]
        public static UnitTestResult DumpNetIdTable()
        {
            var entries = Collect();
            if (entries.Count == 0)
                return UnitTestResult.Skip("registry is empty - no colony loaded");

            // Sorted so two dumps line up without the comparer having to sort.
            foreach (var e in entries.OrderBy(e => e.Kind).ThenBy(e => e.Prefab).ThenBy(e => e.Cell))
                DebugConsole.Log($"[NETID] {e.Kind}|{e.Prefab}|{e.Cell}|{e.NetId}");

            DumpNamesThatAreNotPrefabs();
            DumpNeverAnnounced();

            // And what each one would compute for itself, for the ones that are not
            // holding that value.
            //
            // The dump above says which object holds which id on each peer, which is
            // enough to find ids meaning different things and not enough to say why.
            // This is the comparison that ended the critter thread in one run after six
            // rounds of guessing - a peer whose base hash matches the other's is
            // agreeing and being overridden, and one whose base hash differs disagrees
            // about the object or its cell. Those need opposite fixes.
            //
            // Only objects whose id is not what they would compute. An id that already
            // equals its own computation explains nothing and there are nine thousand of
            // them; the ones that differ are the whole population of interest and there
            // are few enough to read.
            int offBase = 0;
            foreach (var identity in NetworkIdentityRegistry.AllIdentities)
            {
                if (identity == null || identity.gameObject.IsNullOrDestroyed()) continue;

                var go = identity.gameObject;
                int cell = Grid.PosToCell(go);
                if (!Grid.IsValidCell(cell)) continue;

                int computed = Networking.NetIdHelper.GetDeterministicIdFor(go, quiet: true);
                if (computed == identity.NetId) continue;

                offBase++;
                if (offBase <= 40)
                {
                    DebugConsole.Log(
                        $"[IDOFF] {identity.NetId}|{go.PrefabID()}|{cell}|{computed}|" +
                        Networking.NetIdHelper.LastIdInputs);
                }
            }

            DebugConsole.Log($"[IDOFF] total {offBase} identities are not on the id they would compute");

            return UnitTestResult.Pass($"dumped {entries.Count} identities");
        }

        [UnitTest(name: "One id per object in a cell", category: "NetId")]
        public static UnitTestResult OneIdPerLocation()
        {
            // Static objects only. Three duplicants standing in one cell hold
            // three ids, which is correct and which this reported as a bug -
            // the same mistake as the consecutive-id heuristic it replaced:
            // an invariant applied to things it was never about.
            var entries = Collect(staticOnly: true);
            if (entries.Count == 0)
                return UnitTestResult.Skip("registry is empty - no colony loaded");

            // And the third time the same mistake was made, with the same shape.
            //
            // "One id per (prefab, kind, cell)" claimed that a cell holds at most
            // one object of a given prefab. That is true of buildings and false
            // of everything you can pick up: two piles of sand sit in one cell
            // whenever they were not allowed to merge, each rightly holding its
            // own id. This failed on both peers, every run, naming two Sand ids
            // in cell 45142 as "two hashes collided".
            //
            // They had not collided. Both peers reported the same pair, in the
            // same cell, across six consecutive runs - NetId is [Serialize]d, so
            // those two came out of the save file, and no hash the mod computes
            // today produced them. Two Clay piles in that same cell hold
            // consecutive ids, which is the breakoff probe doing exactly its job.
            //
            // What is worth asserting is what a bad id actually looks like: two
            // objects sharing one. More ids than objects is impossible here, one
            // id per object is the healthy case, and fewer ids than objects means
            // something is addressable only as something else.
            var offenders = new List<string>();

            foreach (var group in entries.GroupBy(e => new { e.Prefab, e.Kind, e.Cell }))
            {
                int objects = group.Count();
                int ids = group.Select(e => e.NetId).Distinct().Count();
                if (ids < objects)
                {
                    offenders.Add(
                        $"{group.Key.Prefab} ({group.Key.Kind}) at cell {group.Key.Cell}: " +
                        $"{objects} objects share {ids} id(s)");
                }
            }

            if (offenders.Count > 0)
            {
                return UnitTestResult.Fail(
                    "objects that cannot be addressed separately: " + string.Join("; ", offenders.Take(5)) +
                    ". Anything sent to the shared id reaches only one of them.");
            }

            return UnitTestResult.Pass($"every object in every cell owns its own id across {entries.Count} identities");
        }

        [UnitTest(name: "No two objects share a NetId", category: "NetId")]
        public static UnitTestResult NoSharedIds()
        {
            var entries = Collect();
            if (entries.Count == 0)
                return UnitTestResult.Skip("registry is empty - no colony loaded");

            // RegisterExisting silently skips an id that is already taken, so the
            // loser keeps a NetId that resolves to somebody else's object.
            var collision = entries
                .GroupBy(e => e.NetId)
                .FirstOrDefault(g => g.Select(e => new { e.Prefab, e.Cell }).Distinct().Count() > 1);

            if (collision != null)
            {
                var where = string.Join(", ", collision.Select(e => $"{e.Prefab}@{e.Cell}"));
                return UnitTestResult.Fail($"NetId {collision.Key} is claimed by several objects: {where}");
            }

            return UnitTestResult.Pass($"no shared ids across {entries.Count} identities");
        }

        [UnitTest(name: "Every critter has an identity", category: "NetId")]
        public static UnitTestResult CrittersAreIdentified()
        {
            // A creature with NetId 0 cannot be spoken about at all: not its
            // position, not its animation, not the fact that it was moved. A
            // host logged "no netId found on" sixteen times for pokeshells and
            // juveniles, and moving one into a ranch never reached the client
            // because there was no address to send it under.
            //
            // Counted over the live world rather than asserted at spawn,
            // because the failure was never a broken spawn path - it was a
            // creature that arrived through a path nobody had hooked.
            var missing = new Dictionary<string, int>();
            int total = 0;

            foreach (var creature in UnityEngine.Object.FindObjectsByType<KPrefabID>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (creature == null) continue;
                var go = creature.gameObject;
                if (!go.HasTag(GameTags.Creature) || go.HasTag(GameTags.BaseMinion))
                    continue;

                total++;
                var identity = go.GetComponent<NetworkIdentity>();

                // Every creature's id inputs, on both peers, so they can be diffed.
                //
                // Six rounds have changed when the id is computed and none has compared
                // what it is computed from. The line is prefab, cell, workable type, the
                // base hash of those three, and the value after the free-slot walk - so
                // a peer that disagrees about the object shows a different base hash and
                // one that agrees but was pushed off shows the same base and a different
                // final. Those need opposite fixes and have been indistinguishable.
                //
                // Emitted for all of them, not only the ones with no id: the animal that
                // fails is a different one each run, so the comparison has to be able to
                // look at whichever it turns out to be.
                int cell = Grid.PosToCell(go);
                if (Grid.IsValidCell(cell))
                {
                    Networking.NetIdHelper.GetDeterministicIdFor(go, quiet: true);
                    DebugConsole.Log(
                        $"[IDINPUT] {go.PrefabID()}|{cell}|{(identity == null ? 0 : identity.NetId)}|" +
                        Networking.NetIdHelper.LastIdInputs);
                }

                if (identity != null && identity.NetId != 0)
                    continue;

                string name = go.PrefabID().ToString();
                missing.TryGetValue(name, out int n);
                missing[name] = n + 1;
            }

            if (total == 0)
                return UnitTestResult.Skip("no creatures in this world");

            if (missing.Count > 0)
            {
                var worst = missing.OrderByDescending(kvp => kvp.Value)
                                   .Take(5)
                                   .Select(kvp => $"{kvp.Key} x{kvp.Value}");
                int sum = missing.Values.Sum();
                return UnitTestResult.Fail(
                    $"{sum} of {total} creatures have no NetId: {string.Join(", ", worst)}. " +
                    "The host cannot report their position or actions, so they will not move on a client.");
            }

            return UnitTestResult.Pass($"all {total} creatures carry an id");
        }

        [UnitTest(name: "Nothing is rehoused in bulk", category: "NetId")]
        public static UnitTestResult RehousingIsRare()
        {
            // Rehousing exists to repair a duplicate that came out of a save. It
            // is a repair, so it should be rare - and when it stops being rare
            // it is telling you the id function is wrong, not that the colony is
            // damaged.
            //
            // That is how registering building sites went wrong: a site and the
            // building it becomes share a cell, a prefab tag and an object
            // layer, so they hashed identically and every completed building
            // collided with its own scaffold. A host rehoused 694 tiles, 537
            // wires and 512 ladders in one session, and each replacement id came
            // from walking the registry - which two peers have no reason to
            // agree about.
            int collisions = NetworkIdentityRegistry.CollisionCount;
            if (NetworkIdentityRegistry.Count == 0)
                return UnitTestResult.Skip("registry is empty - no colony loaded");

            // A handful is a repaired save. A percentage of the colony is a
            // broken hash.
            int budget = System.Math.Max(20, NetworkIdentityRegistry.Count / 50);
            if (collisions > budget)
            {
                return UnitTestResult.Fail(
                    $"{collisions} id collisions against {NetworkIdentityRegistry.Count} identities. " +
                    "That is not a damaged save being repaired, it is two kinds of object hashing alike - " +
                    "check what shares a cell, a prefab and a layer.");
            }

            return UnitTestResult.Pass($"{collisions} collisions across {NetworkIdentityRegistry.Count} identities");
        }

        [UnitTest(name: "No identity was minted just to send a packet", category: "NetId")]
        public static UnitTestResult NoLazyIdentities()
        {
            // A lazily attached identity is one-sided by construction. The peer
            // about to send asks for one and gets it; the peer holding the same
            // object never asks, so it has no address to resolve the packet
            // against, and everything sent about that object is dropped.
            //
            // Building sites were exactly this: 221 distinct Constructables
            // unresolvable on a client, 2523 times, while neither peer had ever
            // registered one. Anything listed here wants attaching at spawn on
            // both peers, the way BuildingSpawnPatch does it.
            var lazy = NetworkIdentity.LazyIdentities;
            if (lazy.Count == 0)
                return UnitTestResult.Pass("every identity was attached at spawn");

            var worst = lazy.OrderByDescending(kvp => kvp.Value)
                            .Take(5)
                            .Select(kvp => $"{kvp.Key} x{kvp.Value}");
            return UnitTestResult.Fail(
                $"{lazy.Count} prefabs only got an identity when a packet needed one: {string.Join(", ", worst)}");
        }

        [UnitTest(name: "No registry lookup failures", category: "NetId")]
        public static UnitTestResult NoLookupFailures()
        {
            // Downstream symptom, and the cheapest divergence signal there is:
            // a packet arrived for a NetId this peer never registered.
            if (NetworkIdentityRegistry.Count == 0)
                return UnitTestResult.Skip("registry is empty - no colony loaded");

            // Reported separately, because they are different bugs. A lookup
            // for id 0 is a sender that left a field unset; a lookup for a real
            // id that is not here is two peers disagreeing about an object.
            // Both, not the first one found. Returning early on the id-0 case hid
            // the real failures behind it for a whole run - the client reported
            // one malformed packet and said nothing about its thirty-nine
            // genuine misses, which were the more interesting number.
            var problems = new List<string>();

            int unset = NetworkIdentityRegistry.UnsetIdLookupCount;
            if (unset > 0)
                problems.Add(
                    $"{unset} packets arrived carrying NetId 0 - a sender is not filling the id in" +
                    Blame(NetworkIdentityRegistry.UnsetIdByCaller));

            // A miss is not automatically a divergence.
            //
            // The resolver re-checks every id that failed, a fraction of a
            // second later, and asks the host about the ones that are still
            // missing. Measured on a live session: 412 of them were already in
            // the registry by the time it looked, and not one had to be asked
            // about. So these are packets that arrive just ahead of the object
            // they name - an ordering race that closes itself - and counting
            // them as "this peer never registered it" made a healthy session
            // read as a desync, which is the whole value of this gate.
            //
            // What still deserves to fail is an id the host was asked about and
            // could not supply. That is a real gap.
            var resolver = Networking.Components.MissingEntityResolver.Instance;
            int persistent = resolver.IsNullOrDestroyed() ? -1 : resolver.GaveUpOn;
            int fails = NetworkIdentityRegistry.LookupFailCount;

            // Sorted before it is judged. This used to fail on the whole given-up count,
            // and measurement of two runs showed 59 of the 80 were Pickupable gas and
            // liquid piles this peer had received and then merged away itself - the debris
            // item, already known, already judged not worth forcing - while 7 were objects
            // that had arrived by the time anyone looked. Failing on all of it every run is
            // how a real gap would go unnoticed.
            if (!resolver.IsNullOrDestroyed()) resolver.ClassifyGaveUp();

            if (persistent > 0 && resolver.GaveUpNeverHeld > 0)
            {
                problems.Add(
                    $"{resolver.GaveUpNeverHeld} NetIds could not be resolved even after asking the host, and " +
                    $"this peer has never held them - these are objects it is genuinely missing " +
                    $"({fails} misses in total, {resolver.GaveUpAfterRetiring} more that it held and let go, " +
                    $"{resolver.GaveUpButPresent} that have since arrived)" +
                    Blame(NetworkIdentityRegistry.FailuresByCaller));
            }
            else if (persistent < 0 && fails > 0)
            {
                // No resolver, so there is nothing to tell transient from real.
                problems.Add(
                    $"{fails} failed registry lookups and no resolver to classify them" +
                    Blame(NetworkIdentityRegistry.FailuresByCaller));
            }

            if (problems.Count > 0)
                return UnitTestResult.Fail(string.Join(" ;; ", problems));

            string transient = fails > 0 ? $", {fails} transient misses that resolved on their own" : "";

            // A pass that stays quiet about the sorted-out ids would be the same mistake in
            // the other direction - the number would stop being reported and the debris
            // item would lose its only continuous measurement.
            string sorted = resolver.IsNullOrDestroyed() || persistent <= 0
                ? ""
                : $", {resolver.GaveUpAfterRetiring} held-then-released and {resolver.GaveUpButPresent} late arrivals set aside";

            return UnitTestResult.Pass($"no unresolved ids; registry holds {NetworkIdentityRegistry.Count}{transient}{sorted}");
        }

        /// <summary>
        /// Turns a count into somewhere to look. The registry records which
        /// method asked for each id it could not find, so the failure message
        /// can name it - a bare number told me an id was missing and nothing
        /// about which packet carried it, and that gap cost several rounds.
        /// </summary>
        private static string Blame(IReadOnlyDictionary<string, int> byCaller)
        {
            if (byCaller == null || byCaller.Count == 0) return string.Empty;

            var worst = byCaller.OrderByDescending(kv => kv.Value).Take(4)
                                .Select(kv => $"{kv.Key} x{kv.Value}");
            return ". Asked by: " + string.Join(", ", worst);
        }
    }
}
