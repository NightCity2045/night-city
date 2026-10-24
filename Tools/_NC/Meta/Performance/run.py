# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
"""Own an isolated diagnostic environment, run one real workload, export bounded raw pages.
Does not build, install tools, fabricate sessions or infer an unmeasured result.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone


def digest(data):
    return hashlib.sha256(data).hexdigest()


def provenance(root, folders):
    files = []
    allowed = {'.cs', '.csproj', '.props', '.targets', '.json', '.meta', '.mui', '.mstyle', '.yml', '.ftl', '.xml', '.py'}
    for folder in folders:
        path = root / folder
        if not path.exists():
            continue
        for file in sorted(path.rglob('*')):
            if not file.is_file() or file.suffix.lower() not in allowed or any(x in {'bin', 'obj', '.git'} for x in file.relative_to(root).parts):
                continue
            files.append({'path': file.relative_to(root).as_posix(), 'sha256': digest(file.read_bytes())})
    files.sort(key=lambda x: x['path'])
    return {'sha256': digest(json.dumps(files, separators=(',', ':')).encode()), 'files': files}


class CliProtocolError(RuntimeError):
    def __init__(self, message, operation_id=None, indeterminate=False):
        self.operation_id = operation_id
        self.indeterminate = indeterminate
        super().__init__(json.dumps({'code': 'local_protocol_lost', 'message': message,
            'pendingOperationId': operation_id, 'outcome': 'indeterminate' if indeterminate else 'unavailable',
            'cleanup': 'owned-cli-exit;no-mutation-retry;late-output-not-reused'}))


class Cli:
    MUTATIONS = frozenset({'environment_start', 'environment_stop', 'fixture_create', 'fixture_spawn', 'fixture_attach',
        'fixture_destroy', 'fixture_reset', 'action_execute', 'meta_performance_start', 'meta_performance_stop',
        'meta_app_load_prepare', 'meta_app_load_attach', 'meta_app_load_open', 'meta_app_load_stop',
        'meta_app_frontend_capture', 'meta_app_frontend_action', 'meta_app_frontend_stop'})

    def __init__(self, root, assembly, environment=None, poll_interval=.25):
        self.lock = threading.RLock()
        self.poll_interval = poll_interval
        self._clock = time.monotonic
        self._sleep = time.sleep
        self.poisoned = False
        self._closed = False
        self._pending_operation = None
        self.process = subprocess.Popen(['dotnet', str(assembly), 'session', '--workspace', str(root)], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8',
            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0), env=environment)
        self.lines = queue.Queue(maxsize=64)
        self.errors = []
        def read():
            for line in self.process.stdout:
                self.lines.put(line)
            self.lines.put(None)
        def read_errors():
            for line in self.process.stderr:
                self.errors.append(line.rstrip()[:2048])
                if len(self.errors) > 100:
                    del self.errors[0]
        threading.Thread(target=read, daemon=True).start()
        threading.Thread(target=read_errors, daemon=True).start()

    def _exchange(self, command, arguments, timeout):
        self.process.stdin.write(json.dumps({'command': command, 'arguments': arguments}, separators=(',', ':')) + '\n')
        self.process.stdin.flush()
        line = self.lines.get(timeout=timeout)
        if line is None:
            raise EOFError('Diagnostic CLI exited: ' + '\n'.join(self.errors[-10:]))
        return json.loads(line)

    def call(self, command, arguments=None, timeout=120):
        with self.lock:
            if self.poisoned or self._closed:
                raise CliProtocolError('The CLI stream is closed after protocol loss; late output cannot be reused.', self._pending_operation, self._pending_operation is not None)
            arguments = dict(arguments or {})
            if command in self.MUTATIONS:
                import uuid
                arguments.setdefault('operationId', uuid.uuid4().hex)
            self._pending_operation = arguments.get('operationId') if command in self.MUTATIONS else None
            deadline = self._clock() + timeout
            try:
                result = self._exchange(command, arguments, timeout)
                if isinstance(result, dict) and 'state' in result and 'id' in result and 'name' in result and command != 'operation_get':
                    self._pending_operation = result['id']
                    while result.get('finishedAt') is None:
                        remaining = deadline - self._clock()
                        if remaining <= 0:
                            raise TimeoutError('The pending operation exceeded the local deadline.')
                        self._sleep(min(self.poll_interval, remaining))
                        remaining = deadline - self._clock()
                        if remaining <= 0:
                            raise TimeoutError('The pending operation exceeded the local deadline.')
                        result = self._exchange('operation_get', {'operationId': self._pending_operation}, min(30, remaining))
                        if not isinstance(result, dict) or result.get('id') != self._pending_operation or 'state' not in result:
                            raise ValueError('The pending operation returned incompatible identity metadata.')
                    if result['state'] != 'succeeded':
                        raise RuntimeError(json.dumps({'operationId': result['id'], 'state': result['state'], 'error': result.get('error')}))
                    result = result.get('result')
                elif isinstance(result, dict) and result.get('error'):
                    raise RuntimeError(json.dumps(result['error']))
                elif command in self.MUTATIONS:
                    raise ValueError('The mutation response did not contain an operation identity.')
                self._pending_operation = None
                return result
            except (queue.Empty, TimeoutError, EOFError, OSError, ValueError) as error:
                pending = self._pending_operation
                self.poisoned = True
                self._exit_owned_cli()
                raise CliProtocolError(type(error).__name__ + ':' + str(error), pending, pending is not None) from error

    def _exit_owned_cli(self):
        if self._closed:
            return
        self._closed = True
        # Exit is local lifecycle control, not a repeated native mutation. Do not read another
        # stdout line or send status/cleanup mutations after a desynchronised response stream.
        try:
            self.process.stdin.write('{"command":"exit"}\n')
            self.process.stdin.flush()
            self.process.stdin.close()
            self.process.wait(timeout=30)
        except (OSError, ValueError, subprocess.TimeoutExpired):
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)

    def close(self):
        with self.lock:
            self._exit_owned_cli()


def data(response):
    if isinstance(response, dict) and 'data' in response:
        if response.get('error'):
            raise RuntimeError(json.dumps(response['error']))
        return response['data']
    return response


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--workspace', type=Path, default=Path(__file__).resolve().parents[4])
    parser.add_argument('--cli', type=Path, required=True, help='Already built SS14 MCP diagnostic CLI DLL.')
    parser.add_argument('--workload', choices=['A', 'B', 'C', 'C-resident'], required=True)
    parser.add_argument('--mode', choices=['Normal', 'Burst', 'Heavy', 'Hostile'], default='Normal')
    parser.add_argument('--seed', type=int, default=90210)
    parser.add_argument('--warmup', type=int, default=30)
    parser.add_argument('--seconds', type=int, default=60)
    parser.add_argument('--baseline', action='store_true')
    parser.add_argument('--build-id', help='Select the verified immutable private cohort for the child diagnostic CLI.')
    parser.add_argument('--target', help='Use an already registered server target from SS14_MCP_SERVER instead of starting one.')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--prepare-timeout', type=int, default=3600)
    args = parser.parse_args()
    if not 0 <= args.warmup <= 120 or not 1 <= args.seconds <= 60 or not 60 <= args.prepare_timeout <= 7200:
        parser.error('Warmup0..120, capture1..60, prepare timeout60..7200 seconds.')
    root = args.workspace.resolve(); cli_path = args.cli.resolve()
    if not cli_path.is_file():
        parser.error('Build the diagnostics CLI in the coordinated build slot first.')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    live_sources = provenance(root, ['Content.Meta', 'Content.Server/_NC/Meta', 'Content.Shared/_NC/Meta', 'Content.Client/_NC/Meta',
        'Content.Server/_NC/Diagnostics', 'Content.Client/_NC/Diagnostics', 'Tools/_NC/Meta', 'Tools/_NC/SS14Mcp'])
    build_identity = None
    if args.build_id:
        import uuid
        if uuid.UUID(args.build_id).hex != args.build_id:
            parser.error('Select a canonical verified private acceptance UUID.')
        cohort = root / 'artifacts' / 'meta-acceptance' / args.build_id
        encoded_build = (cohort / 'build-manifest.json').read_bytes(); build = json.loads(encoded_build)
        if build['buildId'] != args.build_id or not build['resources']['capturedBeforeBuild']:
            parser.error('Use a completed matching acceptance manifest with pre-build Resources.')
        server = cohort / 'bin' / 'Content.Server' / 'Content.Server.dll'
        build_identity = {'buildId': args.build_id, 'manifestSha256': digest(encoded_build),
            'serverAssemblySha256': digest(server.read_bytes()), 'assemblies': build['assemblies'],
            'compiledSourceInventoryAvailable': 'sourceInventory' in build, 'sourceInventory': build.get('sourceInventory'),
            'identityScope': 'selected-cohort-manifest-and-assembly-bytes;live-source-is-not-compiled-source-proof'}
        resources = provenance(root, [(cohort / 'Resources').relative_to(root)])
    else:
        resources = provenance(root, ['Resources/Meta', 'Resources/Prototypes/_NC/Meta', 'Resources/Locale/en-US/_NC/meta', 'Resources/Locale/ru-RU/_NC/meta'])
    manifest = {'schemaVersion': 1, 'startedAt': datetime.now(timezone.utc).isoformat(), 'requested': vars(args) | {'workspace': str(root), 'cli': str(cli_path), 'output': str(output)},
        'buildIdentity': build_identity, 'liveWorkspaceAtLaunch': {'sourceInventory': live_sources, 'verifiedAgainstCompilation': False},
        'resourceProvenance': resources, 'resourceIdentityScope': 'frozen-selected-cohort' if build_identity else 'live-development-unverified', 'measurementResults': None,
        'coverage': {'mainThreadTiming': 'native-elapsed-spans-not-CPU', 'actors': '40-native-physical-test-robots',
            'frontend50Sessions': 'not-covered-by-this-run', 'externalCPU': 'not-collected-by-this-run', 'fullEngineTickRaw': 'not-collected-by-this-run'}}
    child_environment = os.environ.copy()
    if args.build_id:
        if len(args.build_id) > 100 or not all(x.isascii() and (x.isalnum() or x in '-_') for x in args.build_id):
            parser.error('Build ID must be a bounded private cohort identifier.')
        child_environment['SS14_MCP_BUILD_ID'] = args.build_id
    cli = Cli(root, cli_path, child_environment); capture = None; environment = None
    try:
        if args.target:
            target = args.target
        else:
            environment = cli.call('environment_start', {'clients': 0, 'graphics': False}, timeout=600)
            target = next(name for name in environment['targets'] if name.endswith('-server'))
        manifest['environment'] = environment
        session = cli.call('session_get', {'targetId': target})
        manifest['session'] = session
        # A native round can finish starting after the HTTP bridge has become ready. Retry only
        # the known rejected/not-admitted start, never an unknown mutation response.
        deadline = time.monotonic() + 60
        while True:
            try:
                started = data(cli.call('meta_performance_start', {'targetId': target, 'workload': args.workload, 'mode': args.mode,
                    'seed': args.seed, 'warmupSeconds': args.warmup, 'captureSeconds': args.seconds, 'baseline': args.baseline,
                    'sourceHash': '', 'resourceHash': resources['sha256']}))
                break
            except RuntimeError as error:
                if '"code": "busy"' not in str(error) or time.monotonic() >= deadline:
                    raise
                time.sleep(1)
        capture = started['captureId']; manifest['captureId'] = capture
        deadline = time.monotonic() + args.prepare_timeout + args.warmup + args.seconds + 60
        while True:
            status = data(cli.call('meta_performance_status', {'targetId': target, 'captureId': capture}))
            (output / 'status.json').write_text(json.dumps(status, indent=2), encoding='utf-8')
            stage = status['run']['stage']
            if stage in {'Finished', 'Failed', 'Stopped'}:
                break
            if time.monotonic() >= deadline:
                raise TimeoutError('Actual native workload did not complete; inspect retained capture ID.')
            time.sleep(1)
        manifest['finalStatus'] = status
        if build_identity and status['run']['serverAssemblyHash'] != build_identity['serverAssemblySha256']:
            raise RuntimeError('The actually loaded server assembly differs from the selected verified cohort.')
        if stage != 'Finished':
            raise RuntimeError('Native workload failed: ' + status['run']['error'])
        info = status['run']['capture']; limit = info['reservedSamples']; offset = 0
        with (output / 'samples.jsonl').open('w', encoding='utf-8') as raw:
            while offset < limit:
                page = data(cli.call('meta_performance_read', {'targetId': target, 'captureId': capture, 'offset': offset, 'count': 512}))['page']
                if page['nextOffset'] <= offset:
                    raise RuntimeError('Raw sample page did not advance; capture coverage is incomplete.')
                for sample in page['samples']:
                    raw.write(json.dumps(sample, separators=(',', ':')) + '\n')
                offset = page['nextOffset']
        manifest['measurementResults'] = {'status': 'recorded', 'completeRawCoverage': info['droppedSamples'] == 0, 'rawSha256': digest((output / 'samples.jsonl').read_bytes())}
    except BaseException as error:
        manifest['failure'] = type(error).__name__ + ':' + str(error)
        raise
    finally:
        if capture:
            try:
                cli.call('meta_performance_stop', {'targetId': target, 'captureId': capture, 'cleanup': True})
                deadline = time.monotonic() + 180
                while time.monotonic() < deadline:
                    cleanup = data(cli.call('meta_performance_status', {'targetId': target, 'captureId': capture}))
                    if cleanup['run']['stage'] in {'Stopped', 'Failed'}:
                        manifest['cleanupStatus'] = cleanup['run']['stage']
                        break
                    time.sleep(.5)
                else:
                    manifest['cleanupFailure'] = 'ArchiveDeadlineExceeded'
            except Exception as error: manifest['cleanupFailure'] = type(error).__name__
        if environment:
            try: cli.call('environment_stop', {'environmentId': environment['id']})
            except Exception as error: manifest['stopFailure'] = type(error).__name__
        cli.close()
        manifest['finishedAt'] = datetime.now(timezone.utc).isoformat()
        encoded = json.dumps(manifest, indent=2, default=str).encode('utf-8')
        (output / 'manifest.json').write_bytes(encoded)
        (output / 'manifest.sha256').write_text(digest(encoded) + '\n', encoding='ascii')


if __name__ == '__main__':
    main()
