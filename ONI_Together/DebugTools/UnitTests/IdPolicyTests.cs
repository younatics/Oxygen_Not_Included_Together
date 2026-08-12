using System.Collections.Generic;
using ONI_Together.Networking.Components;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// The id rules, stated as cases rather than discovered by playing.
    ///
    /// Every bug this policy exists to prevent was found the same way: two peers
    /// played for four minutes, their id tables were compared afterwards, and the
    /// disagreement was traced back through a log. That works and it costs a run per
    /// question. These cases cost nothing and they can be exhaustive, because the
    /// decision is a pure function of six values.
    ///
    /// Each test names the live failure it stands for. A case that cannot be traced
    /// to something that actually went wrong is a case nobody will maintain.
    /// </summary>
    public static class IdPolicyTests
    {
        private static IdSituation Situation(
            bool inSession = true, bool isHost = false, bool preview = false,
            int current = 0, int reserved = 0, int deterministic = 12345,
            bool neededWalk = false, bool announced = false)
            => new IdSituation(inSession, isHost, preview, current, reserved, deterministic, neededWalk, announced);

        [UnitTest(name: "A named object is never renamed by the policy", category: "IdPolicy")]
        public static UnitTestResult NamedObjectsAreLeftAlone()
        {
            // Ids are serialised, so every object in a loaded colony arrives named.
            // Renaming any of them is how the registry ends up filed under one number
            // while the object believes another - the field-versus-key drift three
            // separate tests used to report as "two objects share a NetId".
            foreach (var s in new[]
            {
                Situation(current: 777),
                Situation(current: 777, isHost: true),
                Situation(current: 777, reserved: 999),
                Situation(current: 777, preview: true, neededWalk: true),
            })
            {
                var action = IdPolicy.Decide(s);
                if (action != IdAction.Keep)
                    return UnitTestResult.Fail($"an object holding 777 was told to {action}");
            }

            return UnitTestResult.Pass("a held id always wins");
        }

        [UnitTest(name: "A host reservation outranks the hash", category: "IdPolicy")]
        public static UnitTestResult ReservationWins()
        {
            // WorldDamageSpawnResourcePacket claims the id before the object exists.
            // Letting the object mint first and correcting it afterwards left it in the
            // registry under a foreign id for a moment, and every other object in that
            // cell then had to step over the wrong slot - an Iron pile came out two
            // away from the host's.
            if (IdPolicy.Decide(Situation(reserved: 4242)) != IdAction.TakeReservation)
                return UnitTestResult.Fail("a client ignored a reservation");

            if (IdPolicy.Decide(Situation(reserved: 4242, preview: true)) != IdAction.TakeReservation)
                return UnitTestResult.Fail("a preview ignored a reservation - a named spawn is not nameless");

            if (IdPolicy.Decide(Situation(reserved: 4242, isHost: true)) != IdAction.TakeReservation)
                return UnitTestResult.Fail("a host ignored its own reservation");

            return UnitTestResult.Pass("reservations are honoured on both peers");
        }

        [UnitTest(name: "A client never takes an id that needed a walk", category: "IdPolicy")]
        public static UnitTestResult ClientRefusesWalkedIds()
        {
            // The walk can only see this peer's table, so a walked id is one the two
            // peers have no reason to agree on. Measured: three ids where the host held
            // BasicPlantFood or BasicPlantBar and the client held a Creature pile.
            var walked = Situation(neededWalk: true);
            if (IdPolicy.Decide(walked) != IdAction.WaitForHost)
                return UnitTestResult.Fail("a client took an id it had to walk to");

            // And the host may, because whatever it lands on becomes the truth.
            var hostWalked = Situation(isHost: true, neededWalk: true);
            if (IdPolicy.Decide(hostWalked) != IdAction.TakeDeterministicHash)
                return UnitTestResult.Fail("the host refused to walk, so new objects would go unnamed");

            return UnitTestResult.Pass("the walk is host-only");
        }

        [UnitTest(name: "A client computes no id of its own", category: "IdPolicy")]
        public static UnitTestResult ClientComputesNothing()
        {
            // Letting the client compute was tried twice and measured worse both times:
            // any pure hash took failed lookups from about one a minute to 765-2,161,
            // and restricting it to kinds the host does not announce was worse again,
            // because an announceable kind can still contain objects nobody announces -
            // a Cuprite pile out of the save file waited forever.
            //
            // The known cost of this rule is written down rather than traded away: an
            // object the host never announces has no address on a client, which is why
            // repair state does not replicate. Fixing that belongs on the announcement
            // side.
            if (IdPolicy.Decide(Situation()) != IdAction.WaitForHost)
                return UnitTestResult.Fail("a client computed its own id");

            return UnitTestResult.Pass("a client waits; see the open note on unannounced objects");
        }

        [UnitTest(name: "A client waits when the host is going to announce", category: "IdPolicy")]
        public static UnitTestResult AnnouncedObjectsWait()
        {
            // The other half of the same rule, and the half whose absence cost the most.
            // An ore pile is announced, so a name is coming; naming it locally first
            // makes it unadoptable, because adoption only takes nameless candidates.
            // Failed lookups on the client went from 5 to 3,943 that way - the host
            // talking about ids nothing on the client held any more.
            if (IdPolicy.Decide(Situation(announced: true)) != IdAction.WaitForHost)
                return UnitTestResult.Fail("a client named an object the host was about to name");

            // A reservation still wins: that IS the host naming it, now.
            if (IdPolicy.Decide(Situation(announced: true, reserved: 31337)) != IdAction.TakeReservation)
                return UnitTestResult.Fail("a reservation lost to the announcement rule");

            // And the host itself is unaffected - it is the one doing the announcing.
            if (IdPolicy.Decide(Situation(announced: true, isHost: true)) != IdAction.TakeDeterministicHash)
                return UnitTestResult.Fail("the host stopped naming objects it announces");

            return UnitTestResult.Pass("clients wait either way; reservations and the host are unaffected");
        }

        [UnitTest(name: "A preview waits to be named", category: "IdPolicy")]
        public static UnitTestResult PreviewsWait()
        {
            // A preview is found for adoption by cell and prefab, not by id, so it
            // needs no address - and taking one out of the host's space is what made a
            // local object collide with a real one.
            if (IdPolicy.Decide(Situation(preview: true)) != IdAction.WaitForHost)
                return UnitTestResult.Fail("a preview minted an id");

            return UnitTestResult.Pass("previews stay nameless until named");
        }

        [UnitTest(name: "Playing alone still names everything", category: "IdPolicy")]
        public static UnitTestResult SoloNamesEverything()
        {
            // Not in a session there is nobody to disagree with, and an unnamed object
            // is a lookup failure waiting for the next time a session starts.
            if (IdPolicy.Decide(Situation(inSession: false, neededWalk: true)) != IdAction.TakeDeterministicHash)
                return UnitTestResult.Fail("a solo game left an object unnamed");

            return UnitTestResult.Pass("solo play walks freely");
        }

        [UnitTest(name: "No id to compute means no id is invented", category: "IdPolicy")]
        public static UnitTestResult NoHashMeansNoId()
        {
            // Zero comes back for kinds with no deterministic id - a stored item with
            // no container, an object off the grid. Inventing something for those is
            // how ids stopped being a function of the object.
            foreach (var s in new[]
            {
                Situation(deterministic: 0),
                Situation(deterministic: 0, isHost: true),
                Situation(deterministic: 0, inSession: false),
            })
            {
                if (IdPolicy.Decide(s) != IdAction.WaitForHost)
                    return UnitTestResult.Fail("an id was invented for a kind that has none");
            }

            return UnitTestResult.Pass("no hash, no name");
        }

        [UnitTest(name: "Every situation has exactly one answer", category: "IdPolicy")]
        public static UnitTestResult DecisionIsTotal()
        {
            // The whole point of moving this into one function: enumerate it. Six
            // booleans and three id slots is small enough to walk completely, so
            // "some combination falls through" stops being possible to wonder about.
            var seen = new Dictionary<IdAction, int>();
            int cases = 0;

            foreach (bool inSession in new[] { false, true })
            foreach (bool isHost in new[] { false, true })
            foreach (bool preview in new[] { false, true })
            foreach (int current in new[] { 0, 555 })
            foreach (int reserved in new[] { 0, 666 })
            foreach (int deterministic in new[] { 0, 777 })
            foreach (bool walked in new[] { false, true })
            foreach (bool announced in new[] { false, true })
            {
                var action = IdPolicy.Decide(new IdSituation(
                    inSession, isHost, preview, current, reserved, deterministic, walked, announced));

                cases++;
                seen.TryGetValue(action, out int n);
                seen[action] = n + 1;

                // A held id must never be discarded, whatever else is true.
                if (current != 0 && action != IdAction.Keep)
                    return UnitTestResult.Fail($"held id discarded in a {action} case");

                // And nothing may claim an id of zero.
                if (action == IdAction.TakeReservation && reserved == 0)
                    return UnitTestResult.Fail("took a reservation of zero");
                if (action == IdAction.TakeDeterministicHash && deterministic == 0)
                    return UnitTestResult.Fail("took a hash of zero");
            }

            var summary = new List<string>();
            foreach (var kvp in seen) summary.Add($"{kvp.Key}={kvp.Value}");
            return UnitTestResult.Pass($"{cases} situations, all answered: {string.Join(" ", summary)}");
        }
    }
}

