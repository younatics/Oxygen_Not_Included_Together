using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// What happens to the registry when objects come and go.
    ///
    /// Ids are not guaranteed unique per object, so two objects can end up
    /// holding the same NetId. The dangerous case is a dying one: Unregister
    /// used to remove whatever sat under the id, so the corpse evicted a live
    /// object that happened to share it. The live object then vanished from the
    /// registry while still on screen, and every packet addressed to it counted
    /// as a failed lookup - a symptom that surfaces nowhere near the cause.
    ///
    /// These run against probe objects, never against live entries. A test that
    /// removes a real identity to see what happens would break the session it is
    /// meant to be measuring, and these are run mid-session on purpose.
    /// </summary>
    public static class RegistryChurnTests
    {
        /// <summary>
        /// A NetworkIdentity on an inactive GameObject: AddComponent on an
        /// inactive object does not run Awake or OnSpawn, so the probe never
        /// registers itself, never picks up a cell, and cannot disturb anything.
        /// </summary>
        private static NetworkIdentity NewProbe(string name)
        {
            var go = new GameObject(name);
            go.SetActive(false);
            return go.AddComponent<NetworkIdentity>();
        }

        private static void Destroy(NetworkIdentity probe)
        {
            if (probe != null && probe.gameObject != null)
                Object.DestroyImmediate(probe.gameObject);
        }

        /// <summary>An id no live object holds, so the probes cannot collide with the colony.</summary>
        private static bool TryFindFreeId(out int id)
        {
            for (id = int.MinValue + 1; id < int.MinValue + 4096; id++)
            {
                if (!NetworkIdentityRegistry.Exists(id))
                    return true;
            }
            return false;
        }

        [UnitTest(name: "A dying object cannot evict a live one sharing its id", category: "Registry")]
        public static UnitTestResult UnregisterChecksOwner()
        {
            if (!TryFindFreeId(out int id))
                return UnitTestResult.Skip("no free probe id - registry is unexpectedly dense down there");

            var live = NewProbe("registry-churn-live");
            var corpse = NewProbe("registry-churn-corpse");
            try
            {
                NetworkIdentityRegistry.RegisterExisting(live, id);
                if (!NetworkIdentityRegistry.Exists(id))
                    return UnitTestResult.Fail("probe did not register - the rest of this test proves nothing");

                // The corpse shares the id but does not hold the slot.
                NetworkIdentityRegistry.Unregister(id, corpse);

                if (!NetworkIdentityRegistry.Exists(id))
                    return UnitTestResult.Fail(
                        "unregistering a non-owner removed the slot; a live object sharing an id with a " +
                        "dying one disappears from the registry and every packet for it fails lookup");

                // The owner may still free it.
                NetworkIdentityRegistry.Unregister(id, live);
                if (NetworkIdentityRegistry.Exists(id))
                    return UnitTestResult.Fail("the owner could not free its own slot - identities would leak");

                return UnitTestResult.Pass("only the owner frees the slot");
            }
            finally
            {
                NetworkIdentityRegistry.Unregister(id, live);
                Destroy(live);
                Destroy(corpse);
            }
        }

        [UnitTest(name: "A refused registration is counted, not silent", category: "Registry")]
        public static UnitTestResult CollisionIsCounted()
        {
            if (!TryFindFreeId(out int id))
                return UnitTestResult.Skip("no free probe id");

            var first = NewProbe("registry-churn-first");
            var second = NewProbe("registry-churn-second");
            try
            {
                int before = NetworkIdentityRegistry.CollisionCount;

                NetworkIdentityRegistry.RegisterExisting(first, id);
                NetworkIdentityRegistry.RegisterExisting(second, id);

                // The loser must not silently take the slot: whoever was
                // addressed by this id before the collision must still be
                // addressed by it after.
                if (!NetworkIdentityRegistry.TryGet(id, out var holder) || !ReferenceEquals(holder, first))
                    return UnitTestResult.Fail("the second registration took a slot it did not own");

                if (NetworkIdentityRegistry.CollisionCount <= before)
                    return UnitTestResult.Fail(
                        "the collision was not counted - an object that exists but cannot be addressed " +
                        "leaves no trace anywhere, which is how this class of bug stayed invisible");

                return UnitTestResult.Pass("the refused registration is counted and the holder is unchanged");
            }
            finally
            {
                NetworkIdentityRegistry.Unregister(id, first);
                Destroy(first);
                Destroy(second);
            }
        }

        [UnitTest(name: "A refused registration reports failure to its caller", category: "Registry")]
        public static UnitTestResult RefusedRegistrationIsVisible()
        {
            // RegisterExisting used to return void, so the loser of a collision
            // could not tell it had lost. NetworkIdentity marked itself
            // registered anyway and kept an id that resolved to somebody else -
            // and because NetId is [Serialize]d, that duplicate was written into
            // the save and came back on every load. A live colony had two
            // hydroponic farms, two swamp lilies and three hatches in that
            // state, none of them addressable, on both peers identically.
            if (!TryFindFreeId(out int id))
                return UnitTestResult.Skip("no free probe id");

            var holder = NewProbe("registry-refused-holder");
            var loser = NewProbe("registry-refused-loser");
            try
            {
                if (!NetworkIdentityRegistry.RegisterExisting(holder, id))
                    return UnitTestResult.Fail("claiming a free id reported failure");

                if (NetworkIdentityRegistry.RegisterExisting(loser, id))
                    return UnitTestResult.Fail(
                        "claiming an id another object holds reported success; the loser will believe " +
                        "it is addressable and will be saved holding a duplicate");

                return UnitTestResult.Pass("a refused registration returns false");
            }
            finally
            {
                NetworkIdentityRegistry.Unregister(id, holder);
                Destroy(holder);
                Destroy(loser);
            }
        }

        [UnitTest(name: "A free id can always be found past a taken one", category: "Registry")]
        public static UnitTestResult FreeIdIsFoundPastCollisions()
        {
            if (!TryFindFreeId(out int id))
                return UnitTestResult.Skip("no free probe id");

            var a = NewProbe("registry-freeid-a");
            var b = NewProbe("registry-freeid-b");
            try
            {
                NetworkIdentityRegistry.RegisterExisting(a, id);

                int free = NetworkIdentityRegistry.FindFreeId(id);
                if (free == 0)
                    return UnitTestResult.Fail("no free id found at all - a duplicate could not be repaired");
                if (free == id)
                    return UnitTestResult.Fail($"returned {free}, which is already held");
                if (NetworkIdentityRegistry.Exists(free))
                    return UnitTestResult.Fail($"returned {free}, which is occupied");

                // The walk has to depend only on where it started and on what is
                // occupied. If it depended on arrival order, two peers repairing
                // the same save would land on different ids and the repair would
                // trade one disagreement for another.
                int again = NetworkIdentityRegistry.FindFreeId(id);
                if (again != free)
                    return UnitTestResult.Fail($"not deterministic: {free} then {again}");

                // Never zero: zero means "unregistered" everywhere else.
                if (NetworkIdentityRegistry.FindFreeId(0) == 0)
                    return UnitTestResult.Fail("returned 0, which is the unregistered sentinel");

                return UnitTestResult.Pass($"{id} is taken, {free} is free, and the answer is stable");
            }
            finally
            {
                NetworkIdentityRegistry.Unregister(id, a);
                Destroy(a);
                Destroy(b);
            }
        }

        [UnitTest(name: "Re-registering the same object is not a collision", category: "Registry")]
        public static UnitTestResult ReRegisterIsIdempotent()
        {
            if (!TryFindFreeId(out int id))
                return UnitTestResult.Skip("no free probe id");

            var probe = NewProbe("registry-churn-idempotent");
            try
            {
                NetworkIdentityRegistry.RegisterExisting(probe, id);
                int before = NetworkIdentityRegistry.CollisionCount;
                NetworkIdentityRegistry.RegisterExisting(probe, id);

                // OnSpawn can run more than once for one object over a save load
                // or a hard sync. If that counted as a collision the counter
                // would fill with noise and stop being a usable signal.
                if (NetworkIdentityRegistry.CollisionCount != before)
                    return UnitTestResult.Fail("re-registering the same object counted as a collision");

                return UnitTestResult.Pass("re-registration is a no-op");
            }
            finally
            {
                NetworkIdentityRegistry.Unregister(id, probe);
                Destroy(probe);
            }
        }
    }
}
