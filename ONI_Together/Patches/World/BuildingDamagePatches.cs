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

        public static bool Prefix(object data)
        {
            if (!MultiplayerSession.InSession || !MultiplayerSession.IsClient)
                return true;

            if (data is BuildingHP.DamageSourceInfo info && info.source == MultiplayerSource)
                return true;

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
