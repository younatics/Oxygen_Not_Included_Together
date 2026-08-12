using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Ids that will collide later, found now.
    ///
    /// The suite already checks that no two registered objects share an id. That check
    /// passes while the peers disagree, because the two objects involved are never
    /// registered at the same moment: an item is named one way on the ground and
    /// another way inside a container, and the collision is between one object's
    /// ground-name and another object's container-name.
    ///
    /// Traced in a live pair: the host's MushBar, stored in a refrigerator at cell
    /// 47726, holds -1305005561 - the number the host had earlier given to a Creature
    /// pile at that cell and announced to the client. The client still held the pile,
    /// so one id meant food on one machine and rubble on the other, and every packet
    /// about the food landed on the rubble. Two such ids recurred across separate runs,
    /// identical values both times, which is what a deterministic hash collision looks
    /// like as opposed to bad luck.
    ///
    /// So this removes the time axis: every id every object could take, compared
    /// against every other. A collision here is one that will happen, whether or not
    /// this run happened to arrange it.
    /// </summary>
    public static class HashCollisionTests
    {
        [UnitTest(name: "No object's possible ids collide with another's", category: "NetId")]
        public static UnitTestResult PossibleIdsDoNotCollide()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");

            // owner of each candidate id, and how the id was arrived at
            var claimed = new Dictionary<int, string>();
            var clashes = new List<string>();
            int considered = 0;

            foreach (var pickupable in Object.FindObjectsByType<Pickupable>(FindObjectsSortMode.None))
            {
                if (pickupable.IsNullOrDestroyed() || pickupable.gameObject.IsNullOrDestroyed()) continue;

                NetIdHelper.BaseHashes(pickupable.gameObject, out int loose, out int stored);
                if (loose == 0 && stored == 0) continue;

                considered++;
                string who = pickupable.gameObject.PrefabID().ToString();
                int cell = Grid.PosToCell(pickupable.gameObject);

                foreach (var (id, form) in new[] { (loose, "loose"), (stored, "stored") })
                {
                    if (id == 0) continue;

                    string mine = $"{who}@{cell} as {form}";
                    if (claimed.TryGetValue(id, out string theirs))
                    {
                        // The same object claiming the same id twice is not a clash.
                        if (theirs == mine) continue;

                        if (clashes.Count < 8)
                            clashes.Add($"{id}: {theirs} vs {mine}");
                    }
                    else
                    {
                        claimed[id] = mine;
                    }
                }
            }

            if (clashes.Count > 0)
            {
                return UnitTestResult.Fail(
                    $"{clashes.Count}+ ids can be claimed by two different objects, which is how one " +
                    "number comes to mean different things on the two peers: " + string.Join("; ", clashes));
            }

            return UnitTestResult.Pass(
                $"{considered} carryables, {claimed.Count} distinct possible ids, none shared");
        }
    }
}
