using System;
using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking.Packets.Social;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Does building a printing-pod choice actually work?
    ///
    /// This path was changed twice on reasoning alone and killed a client both
    /// times, because nothing here was ever executed except by a person clicking
    /// the printing pod - and the failure only appears on a client, in a session,
    /// with a real colony.
    ///
    /// First it deferred the stats by a frame, so the container's portrait duplicant
    /// spawned with none: "Could not find Personality: 0x0" out of
    /// Accessorizer.OnSpawn, once per frame, until ONI raised it as an error and the
    /// game closed itself. Then it built the container inactive so nothing could
    /// spawn early, and SetMinion applied traits to the MinionSelectPreview *prefab*
    /// instead of an instance: "Tried adding a trait on a prefab", then a
    /// NullReferenceException out of Klei.AI.Modifier.AddTo.
    ///
    /// Both are reachable without a human: the container can be created and fed the
    /// same way the patch does it, and both failures are observable - one as a Unity
    /// error, one as a thrown exception. So this runs it.
    ///
    /// Cleans up after itself. A test that leaves a duplicant portrait in the scene
    /// would be indistinguishable from the bug it is checking for.
    /// </summary>
    public static class ImmigrantScreenTests
    {
        /// <summary>
        /// A choice as it arrives over the wire: serialized on the host, read back
        /// here. Building it by hand would test a struct rather than the path.
        /// </summary>
        /// <summary>
        /// ImmigrantOptionEntry is a struct, so "did this work" has to be a separate
        /// answer from the value itself.
        /// </summary>
        private static bool TryRoundTripDuplicantOption(out ImmigrantOptionEntry entry, out string reason)
        {
            entry = default;
            reason = null;

            var personality = Db.Get().Personalities?.resources?.FirstOrDefault();
            if (personality == null)
            {
                reason = "no personalities in the database";
                return false;
            }

            var stats = new MinionStartingStats(personality);
            var built = ImmigrantOptionEntry.FromGameDeliverable(stats);

            using var stream = new System.IO.MemoryStream();
            using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.Default, leaveOpen: true))
                built.Serialize(writer);
            stream.Position = 0;
            using var reader = new System.IO.BinaryReader(stream);
            entry = ImmigrantOptionEntry.Deserialize(reader);
            return true;
        }

        [UnitTest(name: "A duplicant choice survives the wire with a personality", category: "Immigrant")]
        public static UnitTestResult OptionCarriesPersonality()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");

            if (!TryRoundTripDuplicantOption(out var entry, out string why))
                return UnitTestResult.Skip(why);

            if (!(entry.ToGameDeliverable() is MinionStartingStats stats))
                return UnitTestResult.Fail("a duplicant choice did not come back as MinionStartingStats");

            if (stats.personality == null)
            {
                return UnitTestResult.Fail(
                    "the choice arrived with no personality. The portrait reads it during OnSpawn, " +
                    "so this is the 'Could not find Personality: 0x0' that closes a client.");
            }

            return UnitTestResult.Pass($"choice arrived as '{stats.personality.Id}'");
        }

        [UnitTest(name: "A choice container can be built and filled without error", category: "Immigrant")]
        public static UnitTestResult ContainerBuildsCleanly()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");

            var screen = ImmigrantScreen.instance;
            if (screen == null || screen.containerPrefab == null || screen.containerParent == null)
                return UnitTestResult.Skip("the immigrant screen has never been opened, so its prefabs are not loaded");

            // The screen has to have been initialised, not merely to exist.
            //
            // SetMinion asks its controller IsSelected(deliverable) on its first line,
            // and that reads a list the screen only builds in
            // InitializeImmigrantScreen. Calling it against an uninitialised screen
            // throws a NullReferenceException from inside Klei code - which this test
            // duly reported as "this is what the printing pod does on every open".
            // It is not: the real path runs inside Initialize, where the list exists.
            //
            // That was a false positive one stack frame away from being believed, and
            // it is the reason the failure message carries the stack now. A test that
            // cannot tell its own broken setup from the defect it is looking for is
            // worse than no test.
            if (screen.selectedDeliverables == null)
            {
                return UnitTestResult.Skip(
                    "the immigrant screen exists but has never been initialised - open the printing " +
                    "pod once and this becomes a real check. Running it now would only prove that " +
                    "SetMinion needs an initialised controller.");
            }

            if (!TryRoundTripDuplicantOption(out var entry, out string why))
                return UnitTestResult.Skip(why);
            if (!(entry.ToGameDeliverable() is MinionStartingStats stats))
                return UnitTestResult.Skip("choice did not become MinionStartingStats");

            // Both known failures announce themselves through Unity's error path
            // rather than by returning anything, so watch for it.
            int errorsBefore = DebugConsole.UnityErrorsInTests;

            CharacterContainer container = null;
            try
            {
                // Exactly what the patch does, in the same order. If this ever stops
                // matching ApplyOptionsToScreen, the test stops being about it.
                container = Util.KInstantiateUI<CharacterContainer>(
                    screen.containerPrefab.gameObject, screen.containerParent, force_active: true);
                container.SetController(screen);
                container.SetReshufflingState(false);

                // The precondition, asserted rather than waited for.
                //
                // This test used to only catch the exception, and it could not catch this
                // one: it runs against a screen that is already shown, so the container
                // inherited an active parent and survived. In the real path the screen is
                // mid-Initialize, the parent is not active yet, and the same call produced
                // an inactive container - which sends SetMinion into ApplyTraits against a
                // prefab and closed the host.
                //
                // Whether the container is live is the thing SetMinion requires, so that
                // is what gets checked. An assertion on the precondition holds in both
                // paths; waiting for the crash only holds in the one that crashes.
                if (!container.gameObject.activeInHierarchy)
                {
                    Cleanup(container);
                    return UnitTestResult.Fail(
                        "the choice container is not active before SetMinion - traits would " +
                        "be applied to a prefab, which is the crash that closed the host. " +
                        "KInstantiateUI needs force_active: true here.");
                }

                container.SetMinion(stats);
            }
            catch (Exception ex)
            {
                Cleanup(container);

                // With the stack, or this is a red light with no address.
                //
                // The first version of this test reported only the type and message,
                // which cannot distinguish "the path is broken" from "this test set it
                // up wrong" - and both are plausible, because the real path runs inside
                // ImmigrantScreen.Initialize and this one does not.
                string where = ex.StackTrace ?? "no stack";
                var frames = where.Split('\n');
                string top = string.Join(" | ", frames.Take(4).Select(f => f.Trim()));

                return UnitTestResult.Fail(
                    $"building a choice container threw: {ex.GetType().Name}: {ex.Message} :: {top}");
            }

            int newErrors = DebugConsole.UnityErrorsInTests - errorsBefore;
            Cleanup(container);

            if (newErrors > 0)
            {
                return UnitTestResult.Fail(
                    $"building a choice container produced {newErrors} Unity error(s). ONI turns these " +
                    "into an error report and the game closes itself; the last two forms were " +
                    "'Could not find Personality: 0x0' and 'Tried adding a trait on a prefab'.");
            }

            return UnitTestResult.Pass("container built, controller set and minion applied with no errors");
        }

        private static void Cleanup(CharacterContainer container)
        {
            if (container.IsNullOrDestroyed() || container.gameObject.IsNullOrDestroyed())
                return;
            try { Util.KDestroyGameObject(container.gameObject); }
            catch (Exception ex)
            {
                DebugConsole.LogWarning($"[ImmigrantScreenTests] could not clean up the test container: {ex.Message}");
            }
        }
    }
}
