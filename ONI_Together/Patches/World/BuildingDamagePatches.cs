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

        /// <summary>
        /// Every way BuildingHP can lose hit points, named once.
        ///
        /// One tile keeps taking damage on the client while the host holds it at
        /// full health, and it gets worse run to run - 68 of 100, then 7 of 100 -
        /// with the suppression patch active and refusing 188 events. So the
        /// damage is arriving by some route that is not OnDoBuildingDamage, and
        /// three guesses at a field name earlier in this work were enough to stop
        /// guessing at API names too. This lists what exists so the next run says
        /// which method to patch.
        /// </summary>
        private static string _damageApi;

        public static string Describe()
        {
            if (_damageApi == null)
            {
                var names = new System.Collections.Generic.List<string>();
                const System.Reflection.BindingFlags any =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;

                foreach (var m in typeof(BuildingHP).GetMethods(any))
                {
                    string n = m.Name;
                    if (n.IndexOf("amage", System.StringComparison.Ordinal) >= 0 ||
                        n.IndexOf("epair", System.StringComparison.Ordinal) >= 0 ||
                        n.IndexOf("estroy", System.StringComparison.Ordinal) >= 0 ||
                        n.IndexOf("elt", System.StringComparison.Ordinal) >= 0)
                    {
                        names.Add(n);
                    }
                }
                _damageApi = string.Join(",", names);
            }

            return $"suppressed={_suppressed} direct={DirectSuppressed} allowed={AllowedCount} " +
                   $"lastBlockedSource={LastBlockedSource}";
        }

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

        /// <summary>Conduit damage events seen on this peer, so a burst has a record.</summary>
        public static int ConduitDamageSeen { get; private set; }

        public static bool Prefix(object data, BuildingHP __instance)
        {
            // A burst leaves no trace in the log, which is why "the host's pipes burst"
            // could not be investigated at all: ONI raises it as a notification, and
            // notifications are not written to Player.log. So the damage event says it
            // instead, on whichever peer takes it, with what the pipe was holding.
            //
            // Logged before the client guard below, because the report was about the
            // host - and the host returns from this method immediately.
            if (!__instance.IsNullOrDestroyed() && __instance.gameObject != null
                && __instance.TryGetComponent<Conduit>(out var conduit) && !conduit.IsNullOrDestroyed())
            {
                ConduitDamageSeen++;

                int cell = Grid.PosToCell(__instance.gameObject);
                string contents = "unknown";
                try
                {
                    var flow = Conduit.GetFlowManager(conduit.ConduitType);
                    if (flow != null)
                    {
                        var c = flow.GetContents(cell);
                        contents = $"{ElementLoader.FindElementByHash(c.element)?.tag.Name ?? "?"} {c.mass:0.###}kg {c.temperature:0}K";
                    }
                }
                catch (System.Exception ex)
                {
                    // Reading the flow manager must never be what breaks a damage event.
                    contents = $"unreadable ({ex.GetType().Name})";
                }

                DebugConsole.LogWarning(
                    $"[ConduitDamage] {(MultiplayerSession.IsHost ? "host" : "client")} " +
                    $"'{__instance.gameObject.name}' at cell {cell} took damage, holding {contents} " +
                    $"(hp {__instance.HitPoints}/{__instance.MaxHitPoints}, event #{ConduitDamageSeen})");
            }

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

        /// <summary>Damage that reached DoDamage without passing the event, and was refused.</summary>
        public static long DirectSuppressed { get; private set; }

        public static void NoteDirectSuppressed() => DirectSuppressed++;
    }

    /// <summary>
    /// The narrow point where hit points actually come off.
    ///
    /// Patching the event handler was not enough. One tile kept taking damage on
    /// the client while the host held it at full health, getting worse run to run
    /// - 68 of 100, then 7 of 100 - with the event patch active and refusing 188
    /// events. So something reduces hit points without raising the event, and
    /// enumerating BuildingHP's methods rather than guessing at names showed what:
    /// DoDamage, which the event handler itself calls and which anything else can
    /// call directly.
    ///
    /// Blocking here covers both routes at once. The mod's own corrections pass
    /// because the scope flag is held across the whole trigger, so it is still set
    /// by the time execution reaches this method.
    /// </summary>
    [HarmonyPatch(typeof(BuildingHP), "DoDamage")]
    public static class BuildingHP_DoDamage_Patch
    {
        public static bool Prefix()
        {
            if (!MultiplayerSession.InSession || !MultiplayerSession.IsClient)
                return true;

            if (BuildingHP_OnDoBuildingDamage_Patch.ApplyingHostDamage)
                return true;

            BuildingHP_OnDoBuildingDamage_Patch.NoteDirectSuppressed();
            return false;
        }
    }
}
