using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Recomputing an object's id must return the id it already has.
    ///
    /// "Deterministic" was only ever true for an object that did not yet hold the
    /// answer. Every probe in NetIdHelper asked "is this id taken" without asking
    /// by whom, so an object recomputing an id it already held saw itself, called
    /// the slot occupied, and walked one past its own correct address.
    ///
    /// The eggs are where it showed. A live run has both halves in one pair of
    /// logs: host PacuEgg at cell 61849 converges 1538373406 -> -455343586, and the
    /// client's egg - already holding -455343586 because the host said so -
    /// converges -455343586 -> -455343585. The peer that was right is the one that
    /// moved, which is why the disagreement was always exactly one and why its
    /// direction swapped between runs.
    ///
    /// Idempotence is the property that was missing, so it is the property tested:
    /// ask twice, get the same answer.
    /// </summary>
    public static class DeterministicIdTests
    {
        private static List<NetworkIdentity> LiveIdentities(int max)
        {
            return Object.FindObjectsByType<NetworkIdentity>(FindObjectsSortMode.None)
                // A non-zero id is the observable form of "registered" - the flag
                // itself is private, and asking for it from a test would mean
                // widening the type's surface to suit the test.
                .Where(n => !n.IsNullOrDestroyed()
                         && n.NetId != 0
                         && !n.IsClientPreview)
                .Take(max)
                .ToList();
        }

        [UnitTest(name: "Recomputing an id returns the id the object already holds", category: "NetId")]
        public static UnitTestResult RecomputeIsIdempotent()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");

            // A sample rather than the whole registry: eight thousand identities
            // would make this the most expensive thing in the run, and the defect
            // is not rare - it fires for every object standing on its own id.
            var sample = LiveIdentities(400);
            if (sample.Count == 0)
                return UnitTestResult.Skip("no registered identities to check");

            var moved = new List<string>();
            int checkedCount = 0;

            foreach (var identity in sample)
            {
                int recomputed = NetIdHelper.GetDeterministicIdFor(identity.gameObject);

                // Zero means "this kind has no deterministic id" - stored items with
                // no container, objects off the grid. Not a disagreement.
                if (recomputed == 0) continue;

                // An id is issued once and kept. A rock that has been carried, a wire
                // that has finished construction and a plant that has been renamed all
                // hash somewhere else now than when they were named, and holding the
                // original is exactly what makes the peers agree - so "recomputes to
                // something else" is not by itself a fault. The first version of this
                // test said it was and failed on six objects that were all correct.
                //
                // The fault is narrower: the hash still lands precisely where the
                // object is sitting, and the walk pushes it off its own slot anyway.
                // Only those cases are counted, which is why the pre-walk hash has to
                // be read rather than inferred.
                int baseHash = NetIdHelper.LastBaseHash;
                if (baseHash != identity.NetId) continue;

                checkedCount++;
                if (recomputed != identity.NetId)
                {
                    moved.Add($"{identity.gameObject.PrefabID()}@{Grid.PosToCell(identity.gameObject)} " +
                              $"hashes to {baseHash}, which it already holds, but was walked to {recomputed}");
                    if (moved.Count >= 6) break;
                }
            }

            if (moved.Count > 0)
            {
                return UnitTestResult.Fail(
                    $"{moved.Count} objects were walked off the id their own hash implies, which is how " +
                    "the two peers end up exactly one apart: " + string.Join("; ", moved));
            }

            if (checkedCount == 0)
            {
                return UnitTestResult.Skip(
                    $"none of {sample.Count} sampled objects still hash to the id they hold, so this run " +
                    "cannot tell whether the self-slot exemption works");
            }

            return UnitTestResult.Pass(
                $"{checkedCount} objects sit on the id their hash implies and none were walked off it");
        }

        [UnitTest(name: "A free slot is still taken when nobody holds it", category: "NetId")]
        public static UnitTestResult UnheldSlotsAreStillClaimed()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");

            // The other half of the fix, and the one a careless version would break:
            // exempting "me" must not turn into exempting everybody. Two live
            // objects must still be separated, or the probe has stopped doing the
            // job it was added for - two iron piles in one cell hashing alike, the
            // second refused registration and left with no address at all.
            var sample = LiveIdentities(400);
            var byId = new Dictionary<int, string>();
            var shared = new List<string>();

            foreach (var identity in sample)
            {
                string who = $"{identity.gameObject.PrefabID()}#{identity.gameObject.GetInstanceID()}";
                if (byId.TryGetValue(identity.NetId, out var other))
                {
                    shared.Add($"{identity.NetId}: {other} and {who}");
                    if (shared.Count >= 4) break;
                }
                else
                {
                    byId[identity.NetId] = who;
                }
            }

            if (shared.Count > 0)
            {
                return UnitTestResult.Fail(
                    "two live objects share one id, so the probe is no longer separating them: " +
                    string.Join("; ", shared));
            }

            return UnitTestResult.Pass($"{sample.Count} identities hold {byId.Count} distinct ids");
        }
    }
}
