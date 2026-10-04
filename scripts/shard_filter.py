#!/usr/bin/env python3
"""
Split the scenario suite across CI shards by test class, and print the
`dotnet test --filter` expression that runs one shard.

Usage:
  python3 shard_filter.py --shard 2of3 --project scenarios/StratumParity.Scenarios \
      [--skip-extended] [--verify results/vanilla-2of3.trx | --classes]
  python3 shard_filter.py --shard 2of3 --tests list.txt    # a saved --list-tests output

The tests are read from the built assembly (`dotnet test --no-build --list-tests`), so a
new test class lands in a shard with no edit here or in a workflow. A class belongs to
shard crc32(simple class name) % count: a pure function of its name, so adding or removing
a class never moves another one, and every leg computes the same split without talking to
the others. Changing the count (the `shard` list in the workflows) reshuffles everything,
which is fine: the shards are independent.

--skip-extended leaves out the tests tagged [Trait("Category", "Extended")], the tier only
the weekly Parity sweep and the indev scout run.
--verify TRX checks, after the shard ran, that its report holds exactly the tests this
shard was assigned: a filter that quietly matched less than the listing would otherwise
lose coverage without failing anything.
--classes prints the shard's classes instead of the filter (debugging).
"""

import argparse
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
import zlib

EXTENDED_EXCLUDE = 'Category!=Extended'

# NOSONAR below: the XML namespace identifier mandated by the TRX schema, compared against
# the document and never fetched.
TRX_NS = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'  # NOSONAR

# `dotnet test --list-tests` prints a localized banner, then one test per line indented by
# exactly four spaces: Namespace.Class.Method, plus the argument list of a theory row.
# Matching on the indentation instead of the banner text keeps this locale-proof.
TEST_RE = re.compile(r'^    ((?:[A-Za-z_][\w+]*\.)*[A-Za-z_][\w+]*)\.([A-Za-z_]\w*)(?:\(.*\))?\s*$')


def parse_tests(text):
    """class FQN -> the full names of its listed tests, from a --list-tests output."""
    classes = {}
    for line in text.splitlines():
        if not re.match(r'^    \S', line):
            continue
        m = TEST_RE.match(line)
        if not m:
            sys.exit(f'cannot read a test name from the listing: {line.strip()!r}')
        classes.setdefault(m.group(1), []).append(line.strip())
    if not classes:
        sys.exit('the listing holds no tests (wrong project, or the build is missing?)')
    return classes


def shard_of(class_fqn, count):
    """The 1-based shard a class runs in."""
    return zlib.crc32(class_fqn.rsplit('.', 1)[-1].encode()) % count + 1


def parse_shard(label):
    m = re.fullmatch(r'(\d+)of(\d+)', label)
    if not m or not 1 <= int(m[1]) <= int(m[2]):
        sys.exit(f"bad --shard {label!r}: expected '<index>of<count>' with 1 <= index <= count, e.g. 2of3")
    return int(m[1]), int(m[2])


def filter_for(classes, skip_extended):
    # The trailing dot ends the class name, so a class never also pulls in another one
    # whose name merely starts the same way.
    expr = '(' + '|'.join(f'FullyQualifiedName~{c}.' for c in sorted(classes)) + ')'
    return f'{expr}&{EXTENDED_EXCLUDE}' if skip_extended else expr


def list_tests(project, skip_extended):
    cmd = ['dotnet', 'test', project, '-c', 'Release', '--no-build', '--list-tests']
    if skip_extended:
        cmd += ['--filter', EXTENDED_EXCLUDE]
    run = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', check=False)
    if run.returncode != 0:
        sys.exit(f'{" ".join(cmd)} failed ({run.returncode}):\n{run.stdout}{run.stderr}')
    return run.stdout


def verify(trx, expected):
    try:
        root = ET.parse(trx).getroot()
    except (OSError, ET.ParseError) as e:
        sys.exit(f'cannot read TRX {trx}: {e}')
    got = [r.get('testName') for r in root.iter(f'{{{TRX_NS}}}UnitTestResult')]
    missing, unexpected = expected - set(got), set(got) - expected
    for name in sorted(missing):
        print(f'::error::assigned to this shard but absent from the report: {name}', file=sys.stderr)
    for name in sorted(unexpected):
        print(f'::error::in the report but not assigned to this shard: {name}', file=sys.stderr)
    if missing or unexpected or len(got) != len(expected):
        sys.exit(f'shard coverage check failed: {len(got)} results for {len(expected)} assigned tests')
    print(f'shard coverage ok: {len(got)} results, exactly the {len(expected)} assigned tests', file=sys.stderr)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--shard', required=True, help="'<index>of<count>', e.g. 2of3")
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument('--project', help='test project to list (needs a prior Release build)')
    source.add_argument('--tests', help='read a saved --list-tests output instead')
    parser.add_argument('--skip-extended', action='store_true')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--verify', metavar='TRX')
    mode.add_argument('--classes', action='store_true')
    args = parser.parse_args()

    index, count = parse_shard(args.shard)
    if args.tests:
        with open(args.tests, encoding='utf-8') as fh:
            text = fh.read()
    else:
        text = list_tests(args.project, args.skip_extended)
    classes = parse_tests(text)

    mine = {c: t for c, t in classes.items() if shard_of(c, count) == index}
    if not mine:
        sys.exit(f'shard {args.shard} has no class ({len(classes)} classes in total): '
                 'lower the shard count, or the legs are running empty')
    assigned = {t for tests in mine.values() for t in tests}
    print(f'shard {args.shard}: {len(mine)} of {len(classes)} classes, '
          f'{len(assigned)} of {sum(map(len, classes.values()))} tests', file=sys.stderr)

    if args.verify:
        verify(args.verify, assigned)
    elif args.classes:
        print('\n'.join(sorted(mine)))
    else:
        print(filter_for(mine, args.skip_extended))


if __name__ == '__main__':
    main()
