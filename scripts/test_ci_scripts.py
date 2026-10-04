#!/usr/bin/env python3
"""
Self-check of the shard scripts: `python3 scripts/test_ci_scripts.py` (stdlib only, no
server, a second). The compare job of the Parity workflow runs it, so a broken split or merge
fails a pull request before it can mislead a real run.
"""

import contextlib
import io
import os
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import merge_trx  # noqa: E402
import shard_filter  # noqa: E402

NS = merge_trx.TRX_NS
PREFIX = 'StratumParity.Scenarios.'

# The banner is localized, the four-space-indented test lines are not.
LISTING = '''Test run for /x/StratumParity.Scenarios.dll (.NETCoreApp,Version=v10.0)
VSTest version 18.0.2-dev (x64)

Les tests suivants sont disponibles :
    StratumParity.Scenarios.Alpha.One
    StratumParity.Scenarios.Alpha.Two
    StratumParity.Scenarios.AlphaExtra.One
    StratumParity.Scenarios.Beta.Theory(value: 1.5, name: "a.b")
  Determining projects to restore...
    StratumParity.Scenarios.Beta.Theory(value: 2.5, name: "c.d")
'''


def quiet(func, *args, **kwargs):
    """Run func, returning (exit code or None, stdout, stderr)."""
    out, err = io.StringIO(), io.StringIO()
    code = None
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        try:
            func(*args, **kwargs)
        except SystemExit as e:
            code = e.code
    return code, out.getvalue(), err.getvalue()


def write_trx(path, tests, outcome='Passed'):
    """A minimal TRX with the sections the merge touches."""
    results = ''.join(
        f'<UnitTestResult testName="{t}" outcome="{outcome}" duration="00:00:01" executionId="e-{t}" testId="i-{t}" />'
        for t in tests)
    defs = ''.join(f'<UnitTest name="{t}" id="i-{t}" />' for t in tests)
    entries = ''.join(f'<TestEntry testId="i-{t}" executionId="e-{t}" />' for t in tests)
    n = len(tests)
    path.write_text(
        f'<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="{NS}">'
        f'<Times start="s" finish="f" /><Results>{results}</Results>'
        f'<TestDefinitions>{defs}</TestDefinitions><TestEntries>{entries}</TestEntries>'
        f'<ResultSummary outcome="Completed"><Counters total="{n}" executed="{n}" passed="{n}" failed="0" />'
        f'</ResultSummary></TestRun>', encoding='utf-8')


class ShardFilterTests(unittest.TestCase):
    def test_parse_Should_GroupTestsByClass_When_BannerIsLocalizedAndNoiseIsInterleaved(self):
        classes = shard_filter.parse_tests(LISTING)
        self.assertEqual(sorted(classes), [PREFIX + 'Alpha', PREFIX + 'AlphaExtra', PREFIX + 'Beta'])
        self.assertEqual(len(classes[PREFIX + 'Beta']), 2)  # theory rows with dots in the arguments

    def test_parse_Should_Fail_When_ALineIsNotATestNameOrNothingIsListed(self):
        self.assertIsNotNone(quiet(shard_filter.parse_tests, '    not a test name\n')[0])
        self.assertIsNotNone(quiet(shard_filter.parse_tests, 'banner only\n')[0])

    def test_split_Should_PutEveryClassInExactlyOneShard_When_CountVaries(self):
        names = [f'{PREFIX}Class{i}' for i in range(60)]
        for count in range(1, 9):
            per_shard = [{c for c in names if shard_filter.shard_of(c, count) == k}
                         for k in range(1, count + 1)]
            self.assertEqual(sum(map(len, per_shard)), len(names))  # no class twice
            self.assertEqual(set().union(*per_shard), set(names))  # no class dropped

    def test_split_Should_BeAPureFunctionOfTheSimpleClassName(self):
        # Pinned values: changing the rule reshuffles every shard, so it must be deliberate.
        self.assertEqual(
            {name: shard_filter.shard_of(PREFIX + name, 3)
             for name in ('KnownDivergenceTests', 'ChunkPersistenceScenarios', 'PathfindingScenarios')},
            {'KnownDivergenceTests': 1, 'ChunkPersistenceScenarios': 2, 'PathfindingScenarios': 3})
        self.assertEqual(shard_filter.shard_of('Other.Namespace.PathfindingScenarios', 3), 3)

    def test_filter_Should_EndEachClassWithADot_When_ClassNamesShareAPrefix(self):
        expr = shard_filter.filter_for([PREFIX + 'Alpha', PREFIX + 'AlphaExtra'], skip_extended=False)
        self.assertEqual(expr, f'(FullyQualifiedName~{PREFIX}Alpha.|FullyQualifiedName~{PREFIX}AlphaExtra.)')
        self.assertTrue(shard_filter.filter_for([PREFIX + 'Alpha'], True).endswith('&Category!=Extended'))

    def test_parseShard_Should_RejectABadLabel(self):
        self.assertEqual(shard_filter.parse_shard('2of3'), (2, 3))
        for bad in ('0of3', '4of3', '2/3', 'x'):
            self.assertIsNotNone(quiet(shard_filter.parse_shard, bad)[0], bad)

    def test_verify_Should_FailOnlyWhenTheReportDiffersFromTheAssignment(self):
        with tempfile.TemporaryDirectory() as tmp:
            trx = Path(tmp) / 'r.trx'
            expected = {PREFIX + 'Alpha.One', PREFIX + 'Alpha.Two'}
            write_trx(trx, sorted(expected))
            self.assertIsNone(quiet(shard_filter.verify, trx, expected)[0])
            write_trx(trx, [PREFIX + 'Alpha.One'])
            self.assertIsNotNone(quiet(shard_filter.verify, trx, expected)[0])
            write_trx(trx, sorted(expected | {PREFIX + 'Beta.X'}))
            self.assertIsNotNone(quiet(shard_filter.verify, trx, expected)[0])


class MergeTrxTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(dir=os.getcwd())
        self.addCleanup(self.tmp.cleanup)
        self.dir = Path(self.tmp.name)

    def run_merge(self, *flavors, lenient=False):
        argv = ['merge_trx.py', *(['--lenient'] if lenient else []), str(self.dir), *flavors]
        old, sys.argv = sys.argv, argv
        try:
            return quiet(merge_trx.main)
        finally:
            sys.argv = old

    def test_merge_Should_UnionTheShards_When_AllArePresent(self):
        write_trx(self.dir / 'vanilla-1of2.trx', ['A.x', 'A.y'])
        write_trx(self.dir / 'vanilla-2of2.trx', ['B.z'])
        code, out, _ = self.run_merge('vanilla')
        self.assertEqual(code, 0, out)
        root = ET.parse(self.dir / 'vanilla.trx').getroot()
        q = merge_trx.q
        self.assertEqual(len(root.find(q('Results'))), 3)
        self.assertEqual(len(root.find(q('TestDefinitions'))), 3)
        self.assertEqual(len(root.find(q('TestEntries'))), 3)
        self.assertEqual(root.find(q('ResultSummary')).find(q('Counters')).get('total'), '3')

    def test_merge_Should_NotConfuseFlavors_When_OneNameStartsWithTheOther(self):
        write_trx(self.dir / 'stratum-1of1.trx', ['A.x'])
        write_trx(self.dir / 'stratum-indev-1of1.trx', ['A.x', 'A.y'])
        self.assertEqual(self.run_merge('stratum', 'stratum-indev')[0], 0)
        self.assertEqual(len(ET.parse(self.dir / 'stratum.trx').getroot().find(merge_trx.q('Results'))), 1)
        self.assertEqual(len(ET.parse(self.dir / 'stratum-indev.trx').getroot().find(merge_trx.q('Results'))), 2)

    def test_merge_Should_Fail_When_AShardIsMissingEmptyOrOverlapping(self):
        write_trx(self.dir / 'vanilla-1of3.trx', ['A.x'])
        write_trx(self.dir / 'vanilla-3of3.trx', ['C.x'])
        code, out, _ = self.run_merge('vanilla')
        self.assertEqual(code, 1)
        self.assertIn('2of3', out)
        (self.dir / 'vanilla-2of3.trx').write_text('')
        self.assertEqual(self.run_merge('vanilla')[0], 1)
        write_trx(self.dir / 'vanilla-2of3.trx', ['A.x'])  # A.x again: the shards overlap
        code, out, _ = self.run_merge('vanilla')
        self.assertEqual(code, 1)
        self.assertIn('overlap', out)

    def test_merge_Should_Fail_When_NoReportExistsForAFlavor(self):
        self.assertEqual(self.run_merge('vanilla')[0], 1)

    def test_lenient_Should_SkipTheFlavorAndSucceed_When_AShardIsMissing(self):
        write_trx(self.dir / 'vanilla-1of2.trx', ['A.x'])
        (self.dir / 'vanilla.trx').write_text('stale')
        code, out, _ = self.run_merge('vanilla', lenient=True)
        self.assertEqual(code, 0)
        self.assertIn('::warning::', out)
        self.assertFalse((self.dir / 'vanilla.trx').exists())  # history_append --missing-ok sees an empty side


if __name__ == '__main__':
    unittest.main()
