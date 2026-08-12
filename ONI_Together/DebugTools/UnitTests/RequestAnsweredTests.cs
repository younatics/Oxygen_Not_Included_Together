using System.Collections.Generic;
using ONI_Together.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Every request gets an answer, or the gap is reported.
    ///
    /// Silence is this codebase's most expensive habit. A handler that cannot act
    /// returns, the sender hears nothing, and nothing distinguishes that from a packet
    /// the network dropped. The resolve path had three such returns - no Pickupable, no
    /// PrimaryElement, an element with no substance - and the numbers said so plainly
    /// once they were put side by side: 118 requests out, 56 answers back, 62 into
    /// nothing. The client retried each of those to its limit and then recorded an id
    /// it would never resolve.
    ///
    /// That was found by reading two printed lines next to each other. This is the same
    /// comparison as a test, so it happens every run and covers every request type
    /// rather than the one I happened to look at. When a new request packet is added,
    /// it belongs in the table below - and if nobody adds it, the pairing test at the
    /// bottom says so.
    /// </summary>
    public static class RequestAnsweredTests
    {
        /// <summary>
        /// Requests this peer sends, and what an answer to them looks like.
        ///
        /// A request may be answered by more than one packet type: the resolver either
        /// gets the object spawned for it or is told the host cannot supply it, and
        /// both are answers.
        /// </summary>
        private static readonly (string Request, string[] Answers)[] Pairs =
        {
            ("EntityResolveRequestPacket", new[] { "WorldDamageSpawnResourcePacket", "EntityUnknownPacket" }),
            ("EntityPositionRequestPacket", new[] { "EntityPositionPacket" }),
            ("StructureStateRequestPacket", new[] { "StructureStatePacket" }),
            ("RequestOperationalStatePacket", new[] { "OperationalStatePacket" }),
            ("DamagedBuildingsQueryPacket", new[] { "BuildingDamagePacket" }),

            // Found by the coverage test below on its first run - six request types
            // nobody was watching. Filled in rather than exempted, because "we do not
            // check that one" is how the resolve path went 62 requests unanswered.
            ("ResearchRequestPacket", new[] { "ResearchStatePacket", "ResearchProgressPacket", "ResearchCompletePacket" }),
            ("WorldDataRequestPacket", new[] { "WorldDataPacket" }),
            ("AnimResyncRequestPacket", new[] { "AnimSyncPacket", "SymbolOverridePacket" }),
            ("SaveFileRequestPacket", new[] { "SaveFileChunkPacket", "TcpTransferStartPacket" }),
            ("TcpFallbackRequestPacket", new[] { "SaveFileChunkPacket" }),
        };

        /// <summary>
        /// Named like a request and is not one.
        ///
        /// GameStateRequestPacket is the host's answer, not a question - GameClient
        /// handles it in OnHostResponseReceived and validates the protocol version out
        /// of it. The name cost real time twice over: it hid the packet from this
        /// coverage check, and when the robustness sweep fed a synthetic one the client
        /// failed its own protocol validation and disconnected itself, which was read
        /// as a sync defect for most of a day.
        ///
        /// Exempted here rather than renamed because renaming a packet type changes the
        /// registry id and therefore the wire; that belongs in a deliberate change, not
        /// in a test fix. Written down so the next reader is not misled the same way.
        /// </summary>
        private static readonly HashSet<string> NamedRequestButIsAnAnswer = new HashSet<string>
        {
            "GameStateRequestPacket",
        };

        /// <summary>
        /// How far short an answer count may fall before it is a finding.
        ///
        /// Not zero tolerance. Answers to a broadcast request arrive once per host
        /// while the request goes out once per client, replies can be batched into a
        /// packet type that also carries unrelated traffic, and a run can end between a
        /// request and its answer. A tenth is far below any of those and far above the
        /// 47% shortfall the resolve path was running at.
        /// </summary>
        private const double MinAnsweredFraction = 0.10;

        [UnitTest(name: "Requests this peer sent were answered", category: "Packets")]
        public static UnitTestResult RequestsAreAnswered()
        {
            if (!MultiplayerSession.InSession)
                return UnitTestResult.Skip("not in a session");

            var findings = new List<string>();
            int checkedPairs = 0;

            foreach (var (request, answers) in Pairs)
            {
                if (!PacketTracker.TryGetCounts(request, out long sent, out _)) continue;
                if (sent == 0) continue;

                long answered = 0;
                foreach (var answer in answers)
                {
                    PacketTracker.TryGetCounts(answer, out _, out long received);
                    answered += received;
                }

                checkedPairs++;
                if (answered < sent * MinAnsweredFraction)
                {
                    findings.Add(
                        $"{request}: sent {sent}, answers received {answered} " +
                        $"({string.Join("/", answers)})");
                }
            }

            if (findings.Count > 0)
            {
                return UnitTestResult.Fail(
                    "requests are going unanswered, which is indistinguishable from packet loss at " +
                    "the sender and is how one id stayed unresolved in every clean run: " +
                    string.Join("; ", findings));
            }

            if (checkedPairs == 0)
                return UnitTestResult.Skip("this peer sent none of the tracked request types");

            return UnitTestResult.Pass($"{checkedPairs} request types, all answered");
        }

        [UnitTest(name: "Every request packet type is in the answered table", category: "Packets")]
        public static UnitTestResult AllRequestsAreListed()
        {
            // A guard on the table itself. The check above can only cover what it knows
            // about, so a new request packet would silently escape it - which is the
            // same failure mode one level up.
            var listed = new HashSet<string>();
            foreach (var (request, _) in Pairs) listed.Add(request);

            var unlisted = new List<string>();
            foreach (var type in System.Reflection.Assembly.GetExecutingAssembly().GetTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(Networking.Packets.Architecture.IPacket).IsAssignableFrom(type)) continue;

                // By name, because "asks for something" is not expressed in the type
                // system here. Renaming a request packet out of this convention would
                // hide it, which is worth knowing too.
                if (!type.Name.EndsWith("RequestPacket") && !type.Name.EndsWith("QueryPacket")) continue;
                if (listed.Contains(type.Name)) continue;
                if (NamedRequestButIsAnAnswer.Contains(type.Name)) continue;

                unlisted.Add(type.Name);
            }

            if (unlisted.Count > 0)
            {
                return UnitTestResult.Fail(
                    "these request types are not checked for answers, so a handler that drops them " +
                    "silently would not be noticed: " + string.Join(", ", unlisted));
            }

            return UnitTestResult.Pass($"{listed.Count} request types, all covered");
        }
    }
}
