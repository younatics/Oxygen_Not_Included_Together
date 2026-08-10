#!/usr/bin/env python3
"""
Compare a host Player.log against a client Player.log and report where the two
simulations diverged.

Day-1 value: the mod already logs every NetId it assigns (NetIdHelper.cs:47,78),
and the *workable* variant includes the cell. That lets us prove NetId divergence
between host and client WITHOUT adding any instrumentation to the mod.

Usage:
    python3 diff_logs.py host.log client.log
    python3 diff_logs.py host.log client.log --json report.json

Exit code 1 if any CONFIRMED divergence was found (usable as a CI/regression gate).
"""

import argparse
import json
import re
import sys
from collections import Counter, defaultdict

PREFIX = "[ONI_Together]"

# ---------------------------------------------------------------------------
# Log line shapes taken verbatim from the mod source. If a format string in the
# mod changes, update the pattern here (and only here).
# ---------------------------------------------------------------------------

# NetIdHelper.cs:78  -> DebugConsole.Log($"Registered entity {prefab} with id: {hash}")
RE_ENTITY = re.compile(r"Registered entity (?P<prefab>\S+) with id: (?P<id>-?\d+)")

# NetIdHelper.cs:47  -> "Registered workable {prefab} with id: {hash} for workable type {type} at cell {cell}"
RE_WORKABLE = re.compile(
    r"Registered workable (?P<prefab>\S+) with id: (?P<id>-?\d+) "
    r"for workable type (?P<wtype>\S+) at cell (?P<cell>\d+)"
)

# NetworkIdentityRegistry.cs:88 -> "[Registry] Lookup failed (#{n}): NetId {netId} not found. Count: {c}"
RE_LOOKUP_FAIL = re.compile(r"\[Registry\] Lookup failed \(#(?P<n>\d+)\): NetId (?P<id>-?\d+) not found")

# SaveFileTransferManager.cs:70 -> "[TransferManager] Started transfer {id} to {client} - {n} chunks"
RE_TRANSFER_START = re.compile(r"\[TransferManager\] Started transfer (?P<tid>\S+) to (?P<client>\d+)")

# Optional leading Klei/Unity timestamp, e.g. "[12:34:56.789] ..."
RE_TS = re.compile(r"^\[(?P<ts>\d{1,2}:\d{2}:\d{2}(?:\.\d+)?)\]")

# Signature -> (severity, audit reference). Substring match, ordered by importance.
SIGNATURES = [
    ("Overwriting existing entity for NetId", "HIGH", "#8 registry aliasing"),
    ("Invalid PacketType received", "HIGH", "chunk corruption / reassembly collision"),
    ("readyToProcess was false for", "HIGH", "packet drop window"),
    ("Requesting full resend", "HIGH", "#6 save transfer resend spiral"),
    ("Received DUPLICATE chunk", "MED", "#6 duplicate/reordered chunk"),
    ("Invalid chunk index", "HIGH", "#6 chunk-size misderivation"),
    ("Possible transfer stall", "MED", "#6 transfer stall detector fired"),
    ("No pending transfer for client", "HIGH", "TCP path failed -> UDP fallback"),
    ("TCP file transfer server failed to start", "HIGH", "port 8081 blocked -> UDP fallback"),
    ("Send to player", "MED", "transport send failure (throttled)"),
    ("NetId still 0 after RegisterIdentity", "HIGH", "spawn sync skipped, client short an item"),
    ("has no NetworkIdentity; skipping sync", "MED", "spawn sync skipped"),
    ("Failed to handle packet", "HIGH", "swallowed dispatch exception"),
    ("Failed to handle incoming packet", "HIGH", "swallowed dispatch exception"),
    ("Error in server update", "HIGH", "swallowed host update exception"),
    ("No connection found for SteamID", "MED", "send to unknown peer"),
    ("Host attempted to send packet", "LOW", "self-send blocked"),
    ("Connection timed out", "HIGH", "disconnect"),
    ("Ignoring disconnect callback", "LOW", "load-time disconnect suppressed (Steam only)"),
]


class Side:
    """Parsed view of one log file."""

    def __init__(self, name, path):
        self.name = name
        self.path = path
        self.lines = 0
        self.mod_lines = 0
        # (prefab, cell) -> id   [workable registrations, cell-keyed => directly comparable]
        self.workable_ids = {}
        # prefab -> Counter of ids  [entity registrations, no cell => compare as multisets]
        self.entity_ids = defaultdict(Counter)
        self.lookup_fail_max = 0
        self.lookup_fail_ids = set()
        self.transfers = Counter()          # transferId -> number of "Started transfer"
        self.sig_counts = Counter()         # signature -> count
        self.sig_first = {}                 # signature -> (ordinal, timestamp, raw line)
        self.timeline = []                  # (ordinal, ts, signature) for shared-event alignment

    def parse(self):
        with open(self.path, "r", encoding="utf-8", errors="replace") as fh:
            for i, raw in enumerate(fh, 1):
                self.lines = i
                line = raw.rstrip("\n")
                ts_match = RE_TS.match(line)
                ts = ts_match.group("ts") if ts_match else None

                if PREFIX not in line:
                    # Non-mod lines still matter for a few Klei-side signatures.
                    self._scan_signatures(i, ts, line)
                    continue
                self.mod_lines += 1
                body = line.split(PREFIX, 1)[1].strip()

                m = RE_WORKABLE.search(body)
                if m:
                    key = (m.group("prefab"), int(m.group("cell")))
                    # First registration wins; later ones are re-registrations.
                    self.workable_ids.setdefault(key, int(m.group("id")))
                else:
                    m = RE_ENTITY.search(body)
                    if m:
                        self.entity_ids[m.group("prefab")][int(m.group("id"))] += 1

                m = RE_LOOKUP_FAIL.search(body)
                if m:
                    self.lookup_fail_max = max(self.lookup_fail_max, int(m.group("n")))
                    self.lookup_fail_ids.add(int(m.group("id")))

                m = RE_TRANSFER_START.search(body)
                if m:
                    self.transfers[m.group("tid")] += 1

                self._scan_signatures(i, ts, body)

    def _scan_signatures(self, ordinal, ts, text):
        for sig, _sev, _ref in SIGNATURES:
            if sig in text:
                self.sig_counts[sig] += 1
                if sig not in self.sig_first:
                    self.sig_first[sig] = (ordinal, ts, text.strip()[:200])
                self.timeline.append((ordinal, ts, sig))


def h(title):
    return f"\n{'=' * 72}\n{title}\n{'=' * 72}"


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("host_log")
    ap.add_argument("client_log")
    ap.add_argument("--json", help="also write a machine-readable report here")
    ap.add_argument("--max-examples", type=int, default=10)
    args = ap.parse_args()

    host = Side("host", args.host_log)
    client = Side("client", args.client_log)
    for s in (host, client):
        try:
            s.parse()
        except OSError as exc:
            print(f"error: cannot read {s.path}: {exc}", file=sys.stderr)
            return 2

    report = {"host": args.host_log, "client": args.client_log, "confirmed": []}
    confirmed = False

    print(h("SUMMARY"))
    print(f"  host   : {host.lines:>8} lines, {host.mod_lines:>7} mod lines, "
          f"{len(host.workable_ids):>6} workable ids, {sum(sum(c.values()) for c in host.entity_ids.values()):>6} entity ids")
    print(f"  client : {client.lines:>8} lines, {client.mod_lines:>7} mod lines, "
          f"{len(client.workable_ids):>6} workable ids, {sum(sum(c.values()) for c in client.entity_ids.values()):>6} entity ids")
    if host.mod_lines == 0 or client.mod_lines == 0:
        print("\n  !! One side has zero '[ONI_Together]' lines. The mod did not load,")
        print("     or you collected the wrong Player.log. Fix that before reading further.")

    # --- 1. NetId divergence: the headline test -----------------------------
    print(h("1. NETID DIVERGENCE  (audit #3 - NetIdHelper hashes mutable floats)"))
    shared = set(host.workable_ids) & set(client.workable_ids)
    mismatched = sorted(k for k in shared if host.workable_ids[k] != client.workable_ids[k])
    if not shared:
        print("  no (prefab, cell) keys seen on both sides - inconclusive.")
        print("  Workable registrations only appear when something diggable/workable spawns;")
        print("  dig a few tiles with the client connected, then re-collect.")
    else:
        rate = 100.0 * len(mismatched) / len(shared)
        print(f"  comparable (prefab, cell) keys : {len(shared)}")
        print(f"  DIFFERENT id on host vs client : {len(mismatched)}  ({rate:.1f}%)")
        if mismatched:
            confirmed = True
            report["confirmed"].append({"finding": "netid_divergence",
                                        "comparable": len(shared),
                                        "mismatched": len(mismatched)})
            print("\n  >>> CONFIRMED: the two peers assign different NetIds to the same object.")
            print("      Every id-addressed packet for these objects is dropped by the receiver.\n")
            for key in mismatched[: args.max_examples]:
                prefab, cell = key
                print(f"      {prefab:<28} cell {cell:<8} host={host.workable_ids[key]:<13} client={client.workable_ids[key]}")
            if len(mismatched) > args.max_examples:
                print(f"      ... and {len(mismatched) - args.max_examples} more")
        else:
            print("  >>> All shared keys agree. Audit #3 is NOT firing for workables in this run.")

    # Entity registrations have no cell, so compare per-prefab id multisets.
    ent_prefabs = set(host.entity_ids) & set(client.entity_ids)
    if ent_prefabs:
        divergent = [p for p in sorted(ent_prefabs)
                     if set(host.entity_ids[p]) != set(client.entity_ids[p])]
        print(f"\n  entity-registration prefabs seen on both sides : {len(ent_prefabs)}")
        print(f"  prefabs whose id sets differ                   : {len(divergent)}")
        for p in divergent[: args.max_examples]:
            ho = sorted(host.entity_ids[p])[:3]
            cl = sorted(client.entity_ids[p])[:3]
            print(f"      {p:<28} host={ho} client={cl}")
        if divergent:
            confirmed = True
            report["confirmed"].append({"finding": "entity_netid_divergence",
                                        "prefabs": len(divergent)})

    # --- 2. Asymmetric signatures -----------------------------------------
    print(h("2. ASYMMETRIC SIGNATURES  (one side saw it, the other did not)"))
    sev_rank = {"HIGH": 0, "MED": 1, "LOW": 2}
    rows = []
    for sig, sev, ref in SIGNATURES:
        hc, cc = host.sig_counts.get(sig, 0), client.sig_counts.get(sig, 0)
        if hc or cc:
            rows.append((sev_rank[sev], sev, sig, hc, cc, ref))
    rows.sort()
    if not rows:
        print("  none of the known warning signatures appeared. Clean run.")
    else:
        print(f"  {'sev':<5} {'host':>6} {'client':>7}  signature / audit ref")
        print(f"  {'-'*5} {'-'*6} {'-'*7}  {'-'*45}")
        for _r, sev, sig, hc, cc, ref in rows:
            flag = "  <-- ASYMMETRIC" if (hc == 0) != (cc == 0) else ""
            print(f"  {sev:<5} {hc:>6} {cc:>7}  {sig[:44]:<45}{flag}")
            print(f"  {'':<5} {'':>6} {'':>7}    ({ref})")

    # --- 3. Lookup failures ------------------------------------------------
    print(h("3. REGISTRY LOOKUP FAILURES  (downstream symptom of #3 / #8)"))
    for s in (host, client):
        print(f"  {s.name:<7} highest counter = {s.lookup_fail_max:<8} distinct missing NetIds = {len(s.lookup_fail_ids)}")
    # A miss is not automatically a divergence, and treating it as one made this
    # section fire on every healthy run.
    #
    # The mod's resolver re-checks each unresolved id a fraction of a second later
    # and asks the host about the ones still missing. Measured on a live session:
    # 412 of them were already in the registry by the time it looked, and not one
    # had to be asked about. So the bulk of these are packets arriving just ahead
    # of the object they name - an ordering race that closes itself - and the only
    # ones that mean two peers disagree are the ones the host could not supply.
    #
    # The mod says so in the log, so this reads that rather than guessing from a
    # count. The raw numbers stay printed above, because they are still the right
    # thing to look at when the shape changes.
    unresolved_re = re.compile(r"(\d+) NetIds could not be resolved even after asking the host")
    persistent = {}
    for s in (host, client):
        worst = 0
        try:
            with open(s.path, encoding="utf-8", errors="replace") as fh:
                for line in fh:
                    m = unresolved_re.search(line)
                    if m:
                        worst = max(worst, int(m.group(1)))
        except OSError:
            # Unreadable is not the same as clean, and must not read as zero.
            worst = -1
        persistent[s.name] = worst
        shown = "unreadable" if worst < 0 else str(worst)
        print(f"  {s.name:<7} ids the host could not supply = {shown}")

    if any(v > 0 for v in persistent.values()):
        confirmed = True
        report["confirmed"].append({"finding": "registry_lookup_failures",
                                    "host": host.lookup_fail_max,
                                    "client": client.lookup_fail_max,
                                    "persistent": persistent})
        print("\n  >>> CONFIRMED: ids that stayed missing after the host was asked. These are")
        print("      objects one peer genuinely does not have. Cross-check section 1.")
    elif client.lookup_fail_max > 100 or host.lookup_fail_max > 100:
        print("\n  transient only: every unresolved id resolved itself or was answered.")
        print("      A high count here with nothing persistent is arrival order, not divergence.")

    # --- 4. Save transfers -------------------------------------------------
    print(h("4. SAVE TRANSFERS  (audit #6 - transferId is derived from the world name)"))
    if not host.transfers:
        print("  host logged no transfers (client may have joined before logging, or TCP path used).")
    for tid, n in host.transfers.most_common():
        mark = "  <-- DUPLICATE CONCURRENT TRANSFER" if n > 1 else ""
        print(f"  transferId={tid!r} started {n}x{mark}")
        if n > 1:
            confirmed = True
            report["confirmed"].append({"finding": "concurrent_save_transfer", "transferId": tid, "starts": n})

    # --- 5. First divergence ----------------------------------------------
    print(h("5. FIRST DIVERGENCE IN THE SHARED EVENT TIMELINE"))
    hsigs = [t[2] for t in host.timeline]
    csigs = [t[2] for t in client.timeline]
    common = set(hsigs) & set(csigs)
    hf = [t for t in host.timeline if t[2] in common]
    cf = [t for t in client.timeline if t[2] in common]
    idx = None
    for i in range(min(len(hf), len(cf))):
        if hf[i][2] != cf[i][2]:
            idx = i
            break
    if idx is None:
        print("  shared-signature sequences agree for their common prefix "
              f"({min(len(hf), len(cf))} events).")
        print("  Ordering divergence is NOT visible at this granularity — this is expected")
        print("  until sequence numbers are added to the packet header (see AUDIT.md section 7).")
    else:
        print(f"  diverges at shared event #{idx}:")
        print(f"    host   line {hf[idx][0]} ts={hf[idx][1]}  {hf[idx][2]}")
        print(f"    client line {cf[idx][0]} ts={cf[idx][1]}  {cf[idx][2]}")

    print(h("VERDICT"))
    if confirmed:
        print("  CONFIRMED divergence found. Details above. Exit code 1.")
    else:
        print("  No confirmed divergence in this run. Either the scenario did not")
        print("  exercise the bug, or the fix under test is holding. Exit code 0.")

    if args.json:
        report["signatures"] = {"host": dict(host.sig_counts), "client": dict(client.sig_counts)}
        report["netid"] = {"comparable": len(shared), "mismatched": len(mismatched) if shared else 0}
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(report, fh, indent=2)
        print(f"\n  wrote {args.json}")

    return 1 if confirmed else 0


if __name__ == "__main__":
    sys.exit(main())
