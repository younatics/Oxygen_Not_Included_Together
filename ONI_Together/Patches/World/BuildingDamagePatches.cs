using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;

namespace ONI_Together.Patches.World
{
    /// <summary>
    /// The host decides what is broken.
    ///
    /// Both peers run the simulation, so both independently decide that a tile
    /// is overheating or that a machine has taken a hit - and the syncer only
    /// speaks when the host's own hit points change. Damage the client inflicts
    /// on itself is therefore invisible to the host: its number did not move, so
    /// it has nothing to say, and the client keeps a broken building the host
    /// considers whole. That is exactly the shape the measurements showed, with
    /// the client holding one more damaged building than the host and the
    /// disagreement surviving every sweep.
    ///
    /// Correcting it after the fact is the wrong place to fix it. The client
    /// should not be producing the damage at all, for the same reason its AI and
    /// its pathing are already suppressed: two simulations that both write
    /// diverge, however often you reconcile them.
    ///
    /// Damage the mod itself applies passes through. That is how the host's
    /// number reaches this peer, and blocking it would leave the client
    /// permanently undamaged instead of permanently over-damaged - a quieter bug
    /// and a worse one.
    /// </summary>
    [HarmonyPatch(typeof(BuildingHP), nameof(BuildingHP.OnDoBuildingDamage))]
    public static class BuildingHP_OnDoBuildingDamage_Patch
    {
        /// <summary>Marks the corrections this mod applies, so they are not mistaken for local simulation.</summary>
        public const string MultiplayerSource = "Multiplayer";

        private static long _suppressed;

        /// <summary>How much local damage has been refused, for the diagnostics to read.</summary>
        public static long SuppressedCount => _suppressed;

        public static void ResetForNewSession() => _suppressed = 0;

        /// <summary>Damage this patch let through because the mod asked for it.</summary>
        public static long AllowedCount { get; private set; }

        /// <summary>What the last few blocked events said their source was.</summary>
        public static string LastBlockedSource { get; private set; } = "none";

        public static string Describe() =>
            $"suppressed={_suppressed} allowed={AllowedCount} lastBlockedSource={LastBlockedSource}";

        /// <summary>
        /// True while this mod is applying the host's hit points.
        ///
        /// The source string was the original test and it never once matched.
        /// BoxingTrigger does not hand the event its payload directly - it wraps
        /// it, and the argument arriving here is a Boxed&lt;T&gt;, so
        /// "data is DamageSourceInfo" was false for every event in the session.
        /// Measured: allowed=0 against suppressed=205. This patch was not
        /// filtering local damage, it was blocking all damage including the
        /// corrections it exists to let through, which is why every corrective
        /// packet ran and changed nothing - applied=0, ineffective=26 - and why
        /// building damage never converged however many times I fixed the sender.
        ///
        /// A flag set around the call does not care how the payload is wrapped.
        /// The trigger is synchronous, so the window is exactly the one call.
        /// </summary>
        private static bool _applyingHostDamage;

        public static bool ApplyingHostDamage => _applyingHostDamage;

        /// <summary>Marks a block of code as "this damage came from the host, let it through".</summary>
        public static System.IDisposable HostDamageScope() => new Scope();

        private sealed class Scope : System.IDisposable
        {
            private readonly bool _previous;
            public Scope() { _previous = _applyingHostDamage; _applyingHostDamage = true; }
            public void Dispose() { _applyingHostDamage = _previous; }
        }

        public static bool Prefix(object data)
        {
            if (!MultiplayerSession.InSession || !MultiplayerSession.IsClient)
                return true;

            if (_applyingHostDamage)
            {
                AllowedCount++;
                return true;
            }

            // Kept as a second chance in case a caller sets the source without
            // going through the scope. It has never fired; see above.
            if (data is BuildingHP.DamageSourceInfo info && info.source == MultiplayerSource)
            {
                AllowedCount++;
                return true;
            }

            // Records what it saw, because "the correction ran and changed
            // nothing" has two very different causes - this patch refusing it,
            // or the game's own handler declining it - and they are
            // indistinguishable from the receiver's side.
            LastBlockedSource = data is BuildingHP.DamageSourceInfo blocked
                ? (blocked.source ?? "null") + $" dmg={blocked.damage}"
                : "not-a-DamageSourceInfo:" + (data?.GetType().Name ?? "null");

            // Counted rather than logged per event. A meteor shower or a
            // flooded room produces these by the hundred, and a line each would
            // bury the log the way per-cell stack traces once froze a host.
            _suppressed++;
            if (_suppressed <= 3 || _suppressed % 500 == 0)
            {
                DebugConsole.Log(
                    $"[BuildingDamage] ignoring locally simulated damage (#{_suppressed}) - the host decides what is broken");
            }

            return false;
        }
    }
}
