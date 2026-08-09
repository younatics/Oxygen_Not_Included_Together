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
    """Last dump wins - a run may trigger the suite several times."""
    table = {}
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = RECORD.search(line)
            if not m:
                continue
            kind, prefab, cell, net_id = m.groups()
            table[(kind.strip(), prefab.strip(), int(cell))] = int(net_id)
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
    mismatched = sorted(k for k in shared if host[k] != client[k])
    host_only = sorted(set(host) - set(client))
    client_only = sorted(set(client) - set(host))

    by_kind = defaultdict(lambda: [0, 0])
    for key in shared:
        by_kind[key[0]][0] += 1
        if host[key] != client[key]:
            by_kind[key[0]][1] += 1

    print("=" * 72)
    print("NETID CROSS-PEER COMPARISON")
    print("=" * 72)
    print(f"  host records            : {len(host)}")
    print(f"  client records          : {len(client)}")
    print(f"  known to both           : {len(shared)}")
    print(f"  DIFFERENT id on the two : {len(mismatched)}")
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
        print()
        print("  first disagreements:")
        for key in mismatched[:15]:
            kind, prefab, cell = key
            print(f"    {prefab:<26} {kind:<22} cell {cell:<8} host={host[key]:<14} client={client[key]}")
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
                        {"kind": k[0], "prefab": k[1], "cell": k[2],
                         "host": host[k], "client": client[k]}
                        for k in mismatched
                    ],
                    "hostOnly": [{"kind": k[0], "prefab": k[1], "cell": k[2]} for k in host_only],
                    "clientOnly": [{"kind": k[0], "prefab": k[1], "cell": k[2]} for k in client_only],
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
    if host_only or client_only:
        print(f"  Note: {len(host_only)} host-only and {len(client_only)} client-only objects were")
        print("  not comparable. Replication gaps show up here, not as id disagreement.")
    print("  Exit code 0.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
