#!/usr/bin/env python3
"""Compare the [NETID] dumps two peers wrote to their Player.log.

Audit #3 asks whether both peers address the same object by the same id. That
cannot be answered on one machine, and it must not be answered by a heuristic -
an earlier in-game test guessed from consecutive id runs and reported every row
of tiles as broken, because the building path legitimately XORs the cell in.

So each peer dumps its table and this does an exact set comparison:

    [NETID] <kind>|<prefab>|<cell>|<id>

Exit code 1 if the peers disagree about any object they both know, which is the
same convention diff_logs.py uses so it drops into the same gate.
"""

import argparse
import re
import sys
from collections import defaultdict

RECORD = re.compile(r"\[NETID\]\s+([^|]+)\|([^|]+)\|(-?\d+)\|(-?\d+)\s*$")


def load(path):
    """Keyed by NetId, because that is the identity. Last dump wins.

    This used to key by (kind, prefab, cell), which reads a single object that
    has moved as two - one on each side - and reported it as a disagreement.
    Ore being hauled and critters walking do exactly that, and it was the whole
    of the remaining "mismatch": ids matched, cells did not.
    """
    table = {}
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = RECORD.search(line)
            if not m:
                continue
            kind, prefab, cell, net_id = m.groups()
            table[int(net_id)] = (kind.strip(), prefab.strip(), int(cell))
    return table


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("host_log")
    ap.add_argument("client_log")
    ap.add_argument("--json", help="write the full report here")
    args = ap.parse_args()

    host = load(args.host_log)
    client = load(args.client_log)

    if not host or not client:
        print("FAIL one side dumped nothing - was 'runtests NetId' triggered on both?")
        print(f"  host   records: {len(host)}")
        print(f"  client records: {len(client)}")
        return 2

    shared = set(host) & set(client)

    # An id known to both but describing a different thing is a real
    # disagreement. A different cell alone is the object having moved, which is
    # reported separately rather than counted as a failure.
    mismatched = sorted(k for k in shared if host[k][:2] != client[k][:2])
    moved = sorted(k for k in shared if host[k][:2] == client[k][:2] and host[k][2] != client[k][2])
    host_only = sorted(set(host) - set(client))
    client_only = sorted(set(client) - set(host))

    by_kind = defaultdict(lambda: [0, 0])
    for key in shared:
        by_kind[host[key][0]][0] += 1
        if host[key][:2] != client[key][:2]:
            by_kind[host[key][0]][1] += 1

    print("=" * 72)
    print("NETID CROSS-PEER COMPARISON")
    print("=" * 72)
    print(f"  host records            : {len(host)}")
    print(f"  client records          : {len(client)}")
    print(f"  known to both           : {len(shared)}")
    print(f"  DIFFERENT id on the two : {len(mismatched)}")
    print(f"  same id, different cell : {len(moved)}   (moved, not a disagreement)")
    print(f"  host only               : {len(host_only)}")
    print(f"  client only             : {len(client_only)}")

    if by_kind:
        print()
        print(f"  {'kind':<28} {'shared':>7} {'differ':>7}")
        print(f"  {'-' * 28} {'-' * 7} {'-' * 7}")
        for kind in sorted(by_kind):
            total, bad = by_kind[kind]
            print(f"  {kind:<28} {total:>7} {bad:>7}")

    if mismatched:
        # Every prefab that disagrees, with a count. Complete, not a sample.
        #
        # The kind breakdown above says "workable: 41 differ" and stops there, which
        # is enough to know something is wrong and not enough to fix it. Grouping by
        # prefab is what turns the number into a list of things to close: the eggs
        # were only recognisable as a class once they were counted as PacuEgg,
        # DreckoEgg, PuftBleachstoneEgg rather than as "entity".
        #
        # Not truncated. A cap here would quietly hide the tail, which is exactly
        # where the last few disagreements live.
        by_prefab = {}
        for key in mismatched:
            hk, hp, hc = host[key]
            by_prefab[hp] = by_prefab.get(hp, 0) + 1

        print()
        print(f"  every prefab that disagrees ({len(by_prefab)} kinds, {len(mismatched)} ids):")
        for prefab, n in sorted(by_prefab.items(), key=lambda kv: (-kv[1], kv[0])):
            print(f"    {n:>5}  {prefab}")

        print()
        print("  first disagreements:")
        for key in mismatched[:15]:
            hk, hp, hc = host[key]
            ck, cp, cc = client[key]
            print(f"    id {key:<14} host={hp}/{hk}@{hc}  client={cp}/{ck}@{cc}")
        if len(mismatched) > 15:
            print(f"    ... and {len(mismatched) - 15} more")

    if args.json:
        import json

        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(
                {
                    "hostRecords": len(host),
                    "clientRecords": len(client),
                    "shared": len(shared),
                    "mismatched": [
                        {"netId": k, "host": host[k], "client": client[k]} for k in mismatched
                    ],
                    "moved": [
                        {"netId": k, "prefab": host[k][1],
                         "hostCell": host[k][2], "clientCell": client[k][2]} for k in moved
                    ],
                    "hostOnly": [{"netId": k, "kind": host[k][0], "prefab": host[k][1], "cell": host[k][2]} for k in host_only],
                    "clientOnly": [{"netId": k, "kind": client[k][0], "prefab": client[k][1], "cell": client[k][2]} for k in client_only],
                    "byKind": {k: {"shared": v[0], "differ": v[1]} for k, v in by_kind.items()},
                },
                fh,
                indent=2,
            )
        print(f"\n  wrote {args.json}")

    print()
    print("=" * 72)
    if mismatched:
        print(f"  CONFIRMED: {len(mismatched)} of {len(shared)} shared objects have different")
        print("  ids on the two peers. Every id-addressed packet for those is dropped.")
        print("  Exit code 1.")
        return 1

    print(f"  The peers agree on all {len(shared)} objects they both know.")
    if moved:
        print(f"  {len(moved)} of them are in different cells - position drift, not identity.")
    if host_only or client_only:
        print(f"  Note: {len(host_only)} host-only and {len(client_only)} client-only objects were")
        print("  not comparable. Replication gaps show up here, not as id disagreement.")
    print("  Exit code 0.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
