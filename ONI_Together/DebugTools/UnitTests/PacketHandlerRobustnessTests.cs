using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Every handler, fed an address that resolves to nothing.
    ///
    /// This is the shape of almost every defect found in this work, and each one was
    /// found the same way - a player's client died and the log was read afterwards:
    ///
    ///   ScheduleAssignmentPacket   read GetSchedule() without checking it, so a
    ///                              duplicant not yet on a schedule threw
    ///   VitalStatsPacket           called Amounts.SetValue for an amount the target
    ///                              did not have, which throws rather than skipping
    ///   BuildingConfigPacket       accepted NetId 0 and then aimed its cell fallback
    ///                              at cell 0, a real cell in the corner of the map
    ///   PrioritizeStatePacket      sent NetId 0 because it tested for -1
    ///
    /// None needed a rare race to reproduce. Each needed one packet whose target was
    /// missing - which is the normal condition on a joining client, and common on any
    /// client, since 849 lookups failed in a three-minute run. A test that hands every
    /// handler an unresolvable target finds this class mechanically instead of one
    /// crash at a time.
    ///
    /// Safe by construction rather than by a list of hopes:
    ///
    ///   - ids are set to a value proven absent from the registry, so nothing is
    ///     found and no real object is touched;
    ///   - anything cell-shaped is set to -1, which fails Grid.IsValidCell, so the
    ///     fallbacks that resolve by position bail before acting. Zero would NOT be
    ///     safe: it is a valid cell.
    ///
    /// A handler that acts anyway is exactly what this is looking for.
    ///
    /// Both failure modes are watched. "Did it throw" is not enough - the printing-pod
    /// crashes returned normally while Klei logged an assert every frame - so Unity's
    /// error count is checked as well.
    /// </summary>
    public static class PacketHandlerRobustnessTests
    {
        /// <summary>
        /// Handlers left out, and why. Listed rather than silently skipped: a test
        /// that quietly covers half its subject reads as covering all of it.
        /// </summary>
        private static readonly Dictionary<string, string> Excluded = new Dictionary<string, string>
        {
            ["ChunkedPacket"] = "reassembles fragments and feeds PacketHandler recursively; covered by the Chunking tests",
            ["ChunkedPacketWrapper"] = "same reassembly path",
            ["SaveFileChunkPacket"] = "writes into the save transfer buffer; a stray chunk would corrupt a real transfer",
            ["SaveFileRequestPacket"] = "makes the host serialise and send the whole colony",
            ["HardSyncPacket"] = "tears the world down and rebuilds it",
            ["HardSyncCompletePacket"] = "part of the same teardown",
            ["DisconnectPacket"] = "ends the session under the rest of the suite",
            // Measured, not guessed. A synthetic one carries no protocol version, so
            // GameClient.OnHostResponseReceived fails TryValidateHostProtocol and the
            // client disconnects itself - which is correct behaviour towards a real
            // mismatched host and fatal when the sender is this suite.
            //
            // It cost most of a day of wrong conclusions. Every run where the client
            // ended up outside the session was blamed first on the client's own build
            // orders (the correlation was 7 for 7, including a controlled run with dig
            // and settle held equal) and the elevated failed lookups, the extra
            // client-only objects and three ids that meant different things on the two
            // peers were all read as sync defects. They were the tail of this: a client
            // playing on alone after the suite kicked it out, still simulating, still
            // making objects nobody had named.
            ["GameStateRequestPacket"] = "trips host protocol validation, and the client then disconnects itself",
            // The same class of packet - it decides whether this peer is admitted -
            // and there is nothing a synthetic one can prove about robustness that is
            // worth the risk of losing the session mid-suite.
            ["HandshakePacket"] = "session admission; a synthetic one can only end the session",
            ["TcpTransferStartPacket"] = "opens a socket to the host and starts a download",
            ["TcpFallbackRequestPacket"] = "asks the host to resend the colony over UDP",
            ["DigCompletePacket"] = "calls WorldDamage.DestroyCell, which acts on the cell without consulting an id",
            ["WorldDataPacket"] = "applies a whole-world snapshot",
            ["WorldDataRequestPacket"] = "makes the host build one",
        };

        private const string UnresolvableIdName = "packet-robustness-probe";

        /// <summary>
        /// An id nothing is filed under. Found rather than assumed: picking a constant
        /// and hoping it is free is how a test ends up addressing a real duplicant.
        /// </summary>
        private static int FindUnusedNetId()
        {
            for (int candidate = -999000; candidate > -999500; candidate--)
            {
                if (candidate != 0 && !NetworkIdentityRegistry.Exists(candidate))
                    return candidate;
            }
            return 0;
        }

        private static bool LooksLikeCell(string fieldName) =>
            fieldName.IndexOf("cell", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool LooksLikeId(string fieldName) =>
            fieldName.IndexOf("netid", StringComparison.OrdinalIgnoreCase) >= 0
            || fieldName.EndsWith("Id", StringComparison.Ordinal)
            || fieldName.EndsWith("ID", StringComparison.Ordinal);

        [UnitTest(name: "Every handler survives an address that resolves to nothing", category: "Handlers")]
        public static UnitTestResult HandlersSurviveMissingTargets()
        {
            if (Game.Instance == null)
                return UnitTestResult.Skip("no colony loaded");

            int missingId = FindUnusedNetId();
            if (missingId == 0)
                return UnitTestResult.Skip("could not find an unused NetId to probe with");

            var threw = new List<string>();
            var logged = new List<string>();
            int exercised = 0;
            var skipped = new List<string>();

            // Held for the whole pass: these packets will log lookup failures by
            // design, and those must not be counted against the session the way a
            // real miss is.
            NetworkIdentityRegistry.BeginDiagnosticScope();
            try
            {
                foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
                {
                    if (type.IsAbstract || type.IsInterface) continue;
                    if (!typeof(IPacket).IsAssignableFrom(type)) continue;
                    if (type.GetConstructor(Type.EmptyTypes) == null)
                    {
                        skipped.Add($"{type.Name} (no parameterless constructor)");
                        continue;
                    }
                    if (Excluded.TryGetValue(type.Name, out string why))
                    {
                        skipped.Add($"{type.Name} ({why})");
                        continue;
                    }

                    IPacket packet;
                    try { packet = (IPacket)Activator.CreateInstance(type); }
                    catch (Exception ex)
                    {
                        skipped.Add($"{type.Name} (could not construct: {ex.GetType().Name})");
                        continue;
                    }

                    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        try
                        {
                            if (field.FieldType == typeof(int))
                            {
                                if (LooksLikeCell(field.Name)) field.SetValue(packet, -1);
                                else if (LooksLikeId(field.Name)) field.SetValue(packet, missingId);
                            }
                            else if (field.FieldType == typeof(string) && field.GetValue(packet) == null)
                            {
                                field.SetValue(packet, UnresolvableIdName);
                            }
                        }
                        catch { /* a field that will not take a probe value is not the subject */ }
                    }

                    int errorsBefore = DebugConsole.UnityErrorsInTests;
                    exercised++;
                    try
                    {
                        packet.OnDispatched();
                    }
                    catch (Exception ex)
                    {
                        string top = (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "no stack";
                        threw.Add($"{type.Name}: {ex.GetType().Name} at {top}");
                        continue;
                    }

                    int newErrors = DebugConsole.UnityErrorsInTests - errorsBefore;
                    if (newErrors > 0)
                        logged.Add($"{type.Name}: {newErrors} Unity error(s)");
                }
            }
            finally
            {
                NetworkIdentityRegistry.EndDiagnosticScope();
            }

            // Always reported, pass or fail. The number exercised is the only thing
            // that says whether a green result means anything.
            DebugConsole.Log(
                $"[TEST] handler robustness: exercised {exercised}, skipped {skipped.Count} " +
                $"({string.Join("; ", skipped.Take(6))}{(skipped.Count > 6 ? "; ..." : "")})");

            if (threw.Count == 0 && logged.Count == 0)
            {
                return UnitTestResult.Pass(
                    $"{exercised} handlers took an unresolvable target without throwing or logging " +
                    $"({skipped.Count} excluded, listed in the log)");
            }

            var parts = new List<string>();
            if (threw.Count > 0)
                parts.Add($"{threw.Count} threw: {string.Join(" | ", threw.Take(5))}");
            if (logged.Count > 0)
                parts.Add($"{logged.Count} logged a Unity error: {string.Join(" | ", logged.Take(5))}");

            return UnitTestResult.Fail(
                string.Join(" ;; ", parts) +
                $" (of {exercised} exercised). A packet whose target is missing is the normal case on a " +
                "client, not an edge case.");
        }
    }
}
