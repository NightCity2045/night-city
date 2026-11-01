# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
"""Ordinary controller correctness tests. Fake streams/clocks only; no native processes or workload."""
import io
import json
from pathlib import Path
import queue
import tempfile
import threading
import unittest
from unittest.mock import patch
import compare
from fleet import valid_frontend_budget, stage_binaries
from run import Cli, CliProtocolError


class FakeInput(io.StringIO):
    def close(self):
        self.saved = self.getvalue()
        super().close()


class FakeProcess:
    def __init__(self):
        self.stdin = FakeInput(); self.terminated = self.killed = 0
    def wait(self, timeout):
        return 0
    def terminate(self):
        self.terminated += 1
    def kill(self):
        self.killed += 1


class FakeLines:
    def __init__(self, values):
        self.values = list(values); self.reads = 0
    def get(self, timeout):
        self.reads += 1
        if not self.values:
            raise queue.Empty()
        value = self.values.pop(0)
        if isinstance(value, BaseException):
            raise value
        return json.dumps(value)


def controller(values):
    cli = Cli.__new__(Cli); cli.lock = threading.RLock(); cli.poll_interval = .01
    cli.poisoned = cli._closed = False; cli._pending_operation = None
    cli.process = FakeProcess(); cli.lines = FakeLines(values); cli.errors = []
    cli._clock = lambda: 0; cli._sleep = lambda duration: None
    return cli


class ControllerTests(unittest.TestCase):
    def test_local_timeout_closes_only_owned_cli_and_never_reuses_late_output(self):
        cli = controller([queue.Empty(), {'data': {'late': True}}])
        with self.assertRaises(CliProtocolError) as failed:
            cli.call('fixture_create', timeout=.01)
        self.assertTrue(failed.exception.indeterminate)
        self.assertEqual(len(failed.exception.operation_id), 32)
        written = [json.loads(line) for line in cli.process.stdin.saved.splitlines()]
        self.assertEqual([row['command'] for row in written], ['fixture_create', 'exit'])
        self.assertEqual(written[0]['arguments']['operationId'], failed.exception.operation_id)
        with self.assertRaises(CliProtocolError):
            cli.call('session_get')
        cli.close()
        self.assertEqual(cli.lines.reads, 1)
        self.assertEqual(len(cli.lines.values), 1)
        self.assertEqual(cli.process.stdin.saved.count('"exit"'), 1)

    def test_pending_operation_deadline_retains_original_id_without_status_retry(self):
        operation = {'id': 'known-operation', 'name': 'fixture_create', 'state': 'running', 'finishedAt': None}
        cli = controller([operation, {'data': {'late': True}}]); now = [0]
        cli._clock = lambda: now[0]
        cli._sleep = lambda duration: now.__setitem__(0, 2)
        with self.assertRaises(CliProtocolError) as failed:
            cli.call('fixture_create', timeout=1)
        self.assertEqual(failed.exception.operation_id, operation['id'])
        self.assertTrue(failed.exception.indeterminate)
        self.assertEqual(cli.lines.reads, 1)
        self.assertNotIn('operation_get', cli.process.stdin.saved)

    def test_explicit_remote_rejection_keeps_the_stream_synchronised(self):
        rejected = {'id': 'rejected-operation', 'name': 'fixture_create', 'state': 'failed', 'finishedAt': 'done', 'error': {'code': 'busy'}}
        cli = controller([rejected, {'data': {'source': 'server'}}])
        with self.assertRaisesRegex(RuntimeError, 'rejected-operation'):
            cli.call('fixture_create')
        self.assertFalse(cli.poisoned)
        self.assertEqual(cli.call('session_get')['data']['source'], 'server')
        cli.close()

    def test_incompatible_poll_identity_poison_prevents_followup_mutation(self):
        cli = controller([{'id': 'original', 'name': 'fixture_create', 'state': 'running', 'finishedAt': None},
            {'id': 'different', 'state': 'succeeded', 'finishedAt': 'done'}])
        with self.assertRaises(CliProtocolError):
            cli.call('fixture_create')
        self.assertTrue(cli.poisoned)
        with self.assertRaises(CliProtocolError):
            cli.call('fixture_destroy')
        self.assertNotIn('fixture_destroy', cli.process.stdin.saved)


class MetadataTests(unittest.TestCase):
    def test_worker_bundle_stays_outside_the_recursively_loaded_game_modules(self):
        scratch_root = Path(__file__).resolve().parents[4] / 'artifacts' / 'meta-performance-helper-temp'
        scratch_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch_root) as directory:
            self.assertTrue(Path(directory).resolve().is_relative_to(scratch_root.resolve()))
            root = Path(directory)
            source = root / 'verified' / 'Content.Server'
            source.mkdir(parents=True)
            (source / 'Content.Server.dll').write_bytes(b'server')
            (source / 'Content.Meta.dll').write_bytes(b'same-core')
            legacy = source / 'MetaCompilerWorker'
            legacy.mkdir()
            (legacy / 'Content.Meta.dll').write_bytes(b'legacy-duplicate-must-not-be-mounted')
            worker = source.parent / 'MetaCompilerWorker'
            worker.mkdir()
            names = ('Meta.CompilerWorker.dll', 'Meta.CompilerWorker.deps.json', 'Meta.CompilerWorker.runtimeconfig.json', 'Content.Meta.dll')
            for name in names:
                (worker / name).write_bytes(b'same-core' if name == 'Content.Meta.dll' else name.encode('ascii'))
            seed = root / 'seed' / 'Content.Server'
            stage_binaries(source, seed)
            game = root / 'owned-runtime' / 'bin' / 'Content.Server'
            stage_binaries(seed, game, link=True)
            self.assertEqual(list(game.rglob('Content.Meta.dll')), [game / 'Content.Meta.dll'])
            self.assertFalse((game / 'MetaCompilerWorker').exists())
            for name in names:
                self.assertEqual((game.parent / 'MetaCompilerWorker' / name).read_bytes(), (worker / name).read_bytes())

    def test_declared_frontend_duration_fits_retained_operation_budget(self):
        self.assertTrue(valid_frontend_budget(60, 2))
        self.assertTrue(valid_frontend_budget(30, 1))
        self.assertFalse(valid_frontend_budget(120, 2))
        self.assertFalse(valid_frontend_budget(0, 1))
        self.assertFalse(valid_frontend_budget(60, 3))

    def test_null_or_missing_cohort_identity_is_a_clean_comparison_rejection(self):
        scratch_root = Path(__file__).resolve().parents[4] / 'artifacts' / 'meta-performance-helper-temp'
        scratch_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch_root) as directory:
            self.assertTrue(Path(directory).resolve().is_relative_to(scratch_root.resolve()))
            left = Path(directory) / 'baseline'; right = Path(directory) / 'candidate'; left.mkdir(); right.mkdir()
            manifest = {'resourceProvenance': {'sha256': 'resources'}, 'buildIdentity': {'manifestSha256': 'a' * 64}}
            (left / 'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
            baseline = {'baseline': True, 'workload': 'A', 'mode': 'Normal', 'sourceHash': 'source', 'serverAssemblyHash': 'assembly'}
            candidate = baseline | {'baseline': False}
            for identity in (None, {}, {'manifestSha256': None}):
                with self.subTest(identity=identity):
                    (right / 'manifest.json').write_text(json.dumps(manifest | {'buildIdentity': identity}), encoding='utf-8')
                    with patch.object(compare, 'reduce', side_effect=[baseline, candidate]):
                        with self.assertRaisesRegex(ValueError, 'identical verified cohort'):
                            compare.compare(left, right)


if __name__ == '__main__':
    unittest.main()
