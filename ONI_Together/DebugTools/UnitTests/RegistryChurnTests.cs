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
