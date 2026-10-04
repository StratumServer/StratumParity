#!/usr/bin/env python3
"""
Merge the shard TRX reports of each flavor into one `<flavor>.trx`.

Usage: python3 merge_trx.py [--lenient] RESULTS_DIR FLAVOR [FLAVOR ...]

Every shard job of a flavor uploads `<flavor>-<index>of<count>.trx`. For each flavor named,
this finds them, checks that all `count` shards are there and that no test ran in two of
them, and writes RESULTS_DIR/<flavor>.trx: every test result, definition and entry of every
shard, the counters summed. That file is what `atlas diff`, render_summary.py and
history_append.py read, so none of them knows about shards.

A flavor with a missing, empty or unreadable shard exits 1 and names it: the compare job
must not call a partial run a pass. With --lenient (the publish jobs) it is a warning and
that flavor's merged file is not written, so `history_append.py --missing-ok` records the
side as an empty suite: a red dashboard entry, same as a suite that never produced a report.

The merged file keeps the first shard's Times and run-level log (nothing downstream reads
them); each shard's own report stays in its uploaded artifact.
"""

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

# NOSONAR below: the XML namespace identifier mandated by the TRX schema, compared against
# the document and never fetched.
TRX_NS = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'  # NOSONAR
ET.register_namespace('', TRX_NS)


def q(tag):
    return f'{{{TRX_NS}}}{tag}'


class ShardError(Exception):
    pass


def find_shards(results, flavor):
    """The shard report paths of a flavor in index order, or a ShardError naming the gap."""
    pattern = re.compile(rf'{re.escape(flavor)}-(\d+)of(\d+)\.trx')
    found = {}
    for path in sorted(results.glob(f'{flavor}-*of*.trx')):
        m = pattern.fullmatch(path.name)
        if m:
            found[(int(m[1]), int(m[2]))] = path
    if not found:
        raise ShardError(f'no shard report ({flavor}-<index>of<count>.trx) found in {results}')
    counts = {count for _, count in found}
    if len(counts) > 1:
        raise ShardError(f'the shard reports disagree on the shard count: {sorted(counts)}')
    count = counts.pop()
    missing = [f'{i}of{count}' for i in range(1, count + 1)
               if (i, count) not in found or found[(i, count)].stat().st_size == 0]
    if missing:
        raise ShardError(f'shard(s) {", ".join(missing)} missing or empty: '
                         'that job died before uploading a report')
    return [found[(i, count)] for i in range(1, count + 1)]


def merge(paths, out):
    """Write the merged report of the shard reports `paths`; returns the test count."""
    roots = []
    for path in paths:
        try:
            roots.append(ET.parse(path).getroot())
        except ET.ParseError as e:
            raise ShardError(f'{path.name} is not a readable TRX: {e}') from e

    ran_in = {}
    for path, root in zip(paths, roots):
        for result in root.iter(q('UnitTestResult')):
            name = result.get('testName')
            if name in ran_in:
                raise ShardError(f'{name} ran in both {ran_in[name]} and {path.name}: '
                                 'the shards overlap')
            ran_in[name] = path.name

    base = roots[0]
    summary = base.find(q('ResultSummary'))
    counters = summary.find(q('Counters'))
    for root in roots[1:]:
        for section in ('Results', 'TestDefinitions', 'TestEntries'):
            base.find(q(section)).extend(list(root.find(q(section))))
        other = root.find(q('ResultSummary'))
        for key, value in other.find(q('Counters')).attrib.items():
            counters.set(key, str(int(counters.get(key, '0')) + int(value)))
        if summary.get('outcome') == 'Completed':
            summary.set('outcome', other.get('outcome'))
    ET.ElementTree(base).write(out, encoding='utf-8', xml_declaration=True)
    return len(ran_in)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('results_dir')
    parser.add_argument('flavors', nargs='+')
    parser.add_argument('--lenient', action='store_true',
                        help='warn and skip a flavor with a missing shard instead of failing')
    args = parser.parse_args()

    # The reports always live in the caller's working tree (the CI job downloads them to
    # ./results); refuse anything that resolves outside it, like the other scripts do.
    results = Path(args.results_dir).resolve()
    workdir = Path.cwd().resolve()
    if not results.is_relative_to(workdir):
        sys.exit(f'refusing to touch reports outside {workdir}: {results}')

    failed = False
    for flavor in args.flavors:
        out = results / f'{flavor}.trx'
        try:
            paths = find_shards(results, flavor)
            tests = merge(paths, out)
        except ShardError as e:
            if args.lenient:
                print(f'::warning::{flavor}: {e}; recording that flavor as an empty suite')
                out.unlink(missing_ok=True)
            else:
                print(f'::error::{flavor}: {e}')
                failed = True
            continue
        print(f'merged {flavor}: {len(paths)} shards, {tests} tests -> {out.name}')
    sys.exit(1 if failed else 0)


if __name__ == '__main__':
    main()
