#!/usr/bin/env python3
"""
Single-log NetId self-consistency check.

diff_logs.py needs two machines. This one needs one, and it proves a strictly
stronger statement about audit #3.

NetIdHelper.GetDeterministicWorkableId (NetIdHelper.cs:39) calls
GetDeterministicEntityId with useCell:false, so the cell is deliberately NOT in
the workable hash. Objects of the same prefab/element/mass/temperature therefore
collide on one base hash, and the collision loop

    while (NetworkIdentityRegistry.Exists(hash + breakoff)) breakoff++;

hands out hash+0, hash+1, hash+2 ... in *arrival order*. The id is decided by
registration order, not by object identity. Two peers that register in a
different order cannot agree - and entity creation is not replicated
(KInstantiatePatch's queue call is commented out), so their orders are
independent by construction.

Two things fall out of a single Player.log:

  A) the same (prefab, workable type, cell) re-registered with a DIFFERENT id
     within one run -> the id is not stable even locally
  B) a run of consecutive ids spanning MORE THAN ONE cell -> the cell does not
     enter the hash, so the ids came from the breakoff counter

Usage:
    python selfcheck_log.py <Player.log>
    python selfcheck_log.py <Player.log> --json report.json

Exit code 1 if either A or B is confirmed - usable as a regression gate on a
single machine.
"""

import argparse
import json
import os
import re
import sys
from collections import Counter, defaultdict

PREFIX = "[ONI_Together]"

# NetIdHelper.cs:47
RE_WORKABLE = re.compile(
    r"Registered workable (?P<prefab>\S+) with id: (?P<id>-?\d+) "
    r"for workable type (?P<wtype>\S+) at cell (?P<cell>\d+)"
)
# NetIdHelper.cs:78
RE_ENTITY = re.compile(r"Registered entity (?P<prefab>\S+) with id: (?P<id>-?\d+)")


def h(title):
    return f"\n{'=' * 72}\n{title}\n{'=' * 72}"


def longest_consecutive_run(ids):
    """Longest ascending run of ids incrementing by exactly 1."""
    ids = sorted(set(ids))
    best, run = [], [ids[0]]
    for a, b in zip(ids, ids[1:]):
        if b == a + 1:
            run.append(b)
        else:
            if len(run) > len(best):
                best = run
            run = [b]
    return run if len(run) > len(best) else best


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("log")
    ap.add_argument("--json", help="also write a machine-readable report here")
    ap.add_argument("--max-examples", type=int, default=8)
    args = ap.parse_args()

    workables = []   # (prefab, wtype, cell, id) in log order
    entities = []    # (prefab, id) in log order
    try:
        with open(args.log, "r", encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if PREFIX not in line:
                    continue
                m = RE_WORKABLE.search(line)
                if m:
                    workables.append((m.group("prefab"), m.group("wtype"),
                                      int(m.group("cell")), int(m.group("id"))))
                    continue
                m = RE_ENTITY.search(line)
                if m:
                    entities.append((m.group("prefab"), int(m.group("id"))))
    except OSError as exc:
        print(f"error: cannot read {args.log}: {exc}", file=sys.stderr)
        return 2

    report = {"log": args.log, "confirmed": []}
    confirmed = False

    print(h("SUMMARY"))
    print(f"  workable registrations : {len(workables)}")
    print(f"  entity   registrations : {len(entities)}")
    if not workables:
        print("\n  !! No 'Registered workable' lines. Either the mod did not load, or no")
        print("     session ran. Host a LAN game and dig a few tiles, then re-run.")
        return 0

    # --- A. id instability for one key ------------------------------------
    print(h("A. SAME (prefab, type, cell) -> DIFFERENT id  (id is not stable)"))
    by_key = defaultdict(list)
    for prefab, wtype, cell, nid in workables:
        by_key[(prefab, wtype, cell)].append(nid)
    unstable = {k: v for k, v in by_key.items() if len(set(v)) > 1}
    rate = 100.0 * len(unstable) / len(by_key)
    print(f"  distinct keys                        : {len(by_key)}")
    print(f"  keys that got more than one id       : {len(unstable)}  ({rate:.1f}%)")
    if unstable:
        confirmed = True
        report["confirmed"].append({"finding": "netid_unstable_within_run",
                                    "keys": len(by_key), "unstable": len(unstable)})
        print("\n  >>> CONFIRMED: one object was addressed by several different NetIds")
        print("      over the run. Packets sent under an older id hit nothing.\n")
        for k, v in list(unstable.items())[: args.max_examples]:
            ids = sorted(set(v))
            print(f"      {k[0][:24]:<25} {k[1][:14]:<15} cell {k[2]:<7} ids={ids[:6]}")

    # --- B. cell absent from the hash -------------------------------------
    print(h("B. CONSECUTIVE ids ACROSS DIFFERENT CELLS  (id comes from arrival order)"))
    groups = defaultdict(list)
    for prefab, wtype, cell, nid in workables:
        groups[(prefab, wtype)].append((cell, nid))

    rows = []
    for (prefab, wtype), items in groups.items():
        ids = [n for _, n in items]
        if len(set(ids)) < 2:
            continue
        run = longest_consecutive_run(ids)
        if len(run) < 2:
            continue
        cell_of = {n: c for c, n in items}
        spanned = len({cell_of[n] for n in run if n in cell_of})
        rows.append((len(run), spanned, prefab, wtype, run[0], run[-1]))
    rows.sort(reverse=True)

    order_assigned = [r for r in rows if r[1] > 1]
    print(f"  prefab/type groups examined                     : {len(groups)}")
    print(f"  groups whose consecutive id-run spans >1 cell   : {len(order_assigned)}")
    if order_assigned:
        confirmed = True
        report["confirmed"].append({"finding": "netid_order_assigned",
                                    "groups": len(order_assigned),
                                    "widest_run": order_assigned[0][0]})
        print("\n  >>> CONFIRMED: the cell does not enter the workable hash. Ids are handed")
        print("      out by the breakoff counter in registration order, so two peers with")
        print("      different spawn order cannot agree on any of them.\n")
        print(f"      {'run':>5} {'cells':>6}  prefab / workable type              id range")
        print(f"      {'-'*5} {'-'*6}  {'-'*35}")
        for runlen, cells, prefab, wtype, lo, hi in order_assigned[: args.max_examples]:
            print(f"      {runlen:>5} {cells:>6}  {prefab[:24]:<25} {wtype[:12]:<13} {lo} .. {hi}")

    # --- C. entity id collisions ------------------------------------------
    print(h("C. ENTITY ID COLLISIONS  (breakoff did not separate them)"))
    ent_counts = Counter(n for _, n in entities)
    dupes = {k: v for k, v in ent_counts.items() if v > 1}
    print(f"  entity ids issued more than once : {len(dupes)}")
    for nid, n in sorted(dupes.items(), key=lambda kv: -kv[1])[: args.max_examples]:
        prefabs = sorted({p for p, i in entities if i == nid})
        print(f"      id {nid:<13} issued {n}x   prefabs={prefabs[:4]}")
    if dupes:
        confirmed = True
        report["confirmed"].append({"finding": "entity_id_collision", "ids": len(dupes)})
        print("\n  >>> CONFIRMED: GetDeterministicEntityId's breakoff loop only advances while")
        print("      NetworkIdentityRegistry.Exists() is true. Repeated ids mean the previous")
        print("      holder was gone from the registry - so several live objects share one id.")

    print(h("VERDICT"))
    if confirmed:
        print("  CONFIRMED from a single log. Audit #3 does not need a second machine to")
        print("  reproduce; the two-box run in SCENARIOS.md S1 measures how bad it gets.")
        print("  Exit code 1.")
    else:
        print("  Nothing confirmed from this log. Exit code 0.")

    if args.json:
        report["counts"] = {"workables": len(workables), "entities": len(entities),
                            "keys": len(by_key), "unstable_keys": len(unstable),
                            "order_assigned_groups": len(order_assigned)}
        parent = os.path.dirname(os.path.abspath(args.json))
        os.makedirs(parent, exist_ok=True)
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(report, fh, indent=2)
        print(f"\n  wrote {args.json}")

    return 1 if confirmed else 0


if __name__ == "__main__":
    sys.exit(main())
