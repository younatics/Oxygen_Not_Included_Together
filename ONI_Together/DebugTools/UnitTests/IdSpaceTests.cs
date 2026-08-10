using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Do the three id functions stay out of each other's way?
    ///
    /// This is asked because they did not. Registering building sites gave them
    /// the same cell, prefab tag and object layer as the finished building, and
    /// the building hash is exactly those three things - so every completed
    /// building collided with its own scaffold, 694 tiles in one session. The
    /// existing tests all passed, because the collisions were being repaired
    /// correctly; nothing was asking whether they should have happened at all.
    ///
    /// Run over the live world rather than on constructed inputs, because the
    /// question is about the objects the colony actually contains.
    /// </summary>
    public static class IdSpaceTests
    {
        private struct Holder
        {
            public int NetId;
            public string Name;
            public string Kind;
        }

        [UnitTest(name: "A building site and its building hold different ids", category: "NetId")]
        public static UnitTestResult SitesAndBuildingsDiffer()
        {
            var byId = new Dictionary<int, Holder>();
            int sites = 0;

            foreach (var building in UnityEngine.Object.FindObjectsByType<Building>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (building == null) continue;

                int id = NetIdHelper.GetDeterministicBuildingId(building.gameObject);
                if (id == 0) continue;

                bool isSite = building is BuildingUnderConstruction;
                if (isSite) sites++;

                var holder = new Holder
                {
                    NetId = id,
                    Name = building.gameObject.PrefabID().ToString(),
                    Kind = isSite ? "site" : "complete",
                };

                if (byId.TryGetValue(id, out var incumbent))
                {
                    // Same id from two different objects is the failure. Two
                    // reads of the same object are not.
                    if (incumbent.Kind != holder.Kind || incumbent.Name != holder.Name)
                    {
                        return UnitTestResult.Fail(
                            $"id {id} is produced by both {incumbent.Kind} '{incumbent.Name}' and " +
                            $"{holder.Kind} '{holder.Name}' - one of them cannot be addressed");
                    }
                }
                byId[id] = holder;
            }

            if (byId.Count == 0)
                return UnitTestResult.Skip("no buildings - no colony loaded");

            return UnitTestResult.Pass($"{byId.Count} buildings ({sites} under construction), all distinct");
        }

        [UnitTest(name: "The three id functions do not overlap", category: "NetId")]
        public static UnitTestResult HashFamiliesDoNotOverlap()
        {
            // Buildings, workables and entities are hashed by three different
            // functions. Nothing forces their outputs apart, so an overlap shows
            // up as one object silently unaddressable - the same failure the
            // site collision produced, from a different direction.
            var seen = new Dictionary<int, string>();
            int checked_ = 0;

            foreach (var identity in NetworkIdentityRegistry.AllIdentities.ToList())
            {
                if (identity == null || identity.gameObject == null) continue;
                if (identity.NetId == 0) continue;

                checked_++;
                string who = identity.gameObject.PrefabID().ToString();
                if (seen.TryGetValue(identity.NetId, out var other) && other != who)
                {
                    return UnitTestResult.Fail(
                        $"NetId {identity.NetId} is held by both '{other}' and '{who}'");
                }
                seen[identity.NetId] = who;
            }

            if (checked_ == 0)
                return UnitTestResult.Skip("registry is empty");

            return UnitTestResult.Pass($"{checked_} registered identities, no id held by two prefabs");
        }
    }
}
