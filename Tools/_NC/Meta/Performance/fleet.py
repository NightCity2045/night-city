# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
"""Bounded Windows native-client fleet. Owns real processes/connections/BUI sessions only.
Startup and fixture provisioning precede measurement. Never builds or fabricates a player session.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import threading
import time
import uuid
from run import Cli, data, digest
from frontend_driver import controller_operations, layout, run_frontends


def clean_environment():
    return {key: value for key, value in os.environ.items() if not key.startswith(('SS14_MCP_', 'ROBUST_AUTH_', 'NC_DIAGNOSTICS_'))
        and key not in {'OPENAI_API_KEY', 'ANTHROPIC_API_KEY'}}


def seal(path, value):
    encoded = json.dumps(value, indent=2, default=str).encode('utf-8')
    path.write_bytes(encoded)
    path.with_suffix(path.suffix + '.sha256').write_text(digest(encoded) + '\n', encoding='ascii')


def valid_frontend_budget(seconds, action_hz):
    # Core keeps at most128 operation records for10min. Capture+stop consume two;
    # keep a margin while retaining the declared native action rate.
    return 1 <= seconds <= 60 and .1 <= action_hz <= 2 and math.ceil(seconds * action_hz) + 2 <= 128


def inventory(root, maximum=300000):
    values = []
    for path in sorted(root.rglob('*')):
        if path.is_symlink():
            raise ValueError('Immutable inputs cannot contain symbolic links.')
        if path.is_file():
            if len(values) >= maximum or path.stat().st_size > 256 * 1024 * 1024:
                raise ValueError('Immutable input inventory limit exceeded.')
            values.append({'path': path.relative_to(root).as_posix(), 'sha256': digest(path.read_bytes())})
    return {'files': values, 'sha256': digest(json.dumps(values, separators=(',', ':')).encode())}


def stage_binaries(source, destination, link=False):
    destination.mkdir(parents=True, exist_ok=False)
    for file in source.rglob('*'):
        relative = file.relative_to(source)
        if not file.is_file() or not (len(relative.parts) == 1 and file.suffix in {'.dll', '.json', '.exe', '.so', '.dylib', '.txt'} or relative.parts[0] == 'runtimes'):
            continue
        if file.is_symlink():
            raise ValueError('Binary input must be a concrete immutable bundle.')
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        if link:
            os.link(file, target)  # Link only from our private seed copy, never a shared build output.
        else:
            shutil.copyfile(file, target)
    if (source / 'Content.Server.dll').is_file():
        worker_source = source.parent / 'MetaCompilerWorker'
        worker_target = destination.parent / 'MetaCompilerWorker'
        worker_target.mkdir(parents=True, exist_ok=False)
        for name in ('Meta.CompilerWorker.dll', 'Meta.CompilerWorker.deps.json', 'Meta.CompilerWorker.runtimeconfig.json', 'Content.Meta.dll'):
            file = worker_source / name
            if file.is_symlink() or not file.is_file():
                raise ValueError('The canonical sibling worker bundle is missing or linked.')
            if link:
                os.link(file, worker_target / name)
            else:
                shutil.copyfile(file, worker_target / name)


def verify_cohort(root, build_id, server, client, resources):
    if uuid.UUID(build_id).hex != build_id:
        raise ValueError('Build ID must be the canonical immutable acceptance UUID.')
    cohort = root / 'artifacts' / 'meta-acceptance' / build_id
    manifest_path = cohort / 'build-manifest.json'
    if manifest_path.stat().st_size > 128 * 1024:
        raise ValueError('Acceptance manifest exceeds its bound.')
    encoded = manifest_path.read_bytes(); manifest = json.loads(encoded)
    tests = cohort / 'bin' / 'Content.IntegrationTests'
    version = manifest['version']
    if type(version) is not int or version not in (1, 2) or manifest['buildId'] != build_id or Path(manifest['testDirectory']).resolve() != tests.resolve():
        raise ValueError('Acceptance manifest identity/path mismatch.')
    if len(manifest['assemblies']) < 8 or len(manifest['assemblies']) > 32:
        raise ValueError('Acceptance assembly inventory is incomplete or oversized.')
    hashes = {}
    for item in manifest['assemblies']:
        name = item['name']
        inventory_root = cohort / 'bin' if version == 2 and name.startswith('MetaCompilerWorker/') else tests
        file = (inventory_root / name).resolve()
        if name in hashes or not file.is_relative_to(inventory_root.resolve()) or digest(file.read_bytes()) != item['sha256']:
            raise ValueError('A verified acceptance assembly changed or escaped its inventory.')
        hashes[name] = item['sha256']
    for project, name in (('Content.Server', 'Content.Server.dll'), ('Content.Client', 'Content.Client.dll'),
        ('Content.Server', 'Content.Shared.dll'), ('Content.Client', 'Content.Shared.dll'), ('Content.Server', 'Content.Meta.dll'),
        ('Content.Client', 'Content.Meta.dll'), ('Content.Server', 'Content.Server.Database.dll'),
        ('Content.Server', 'Content.Shared.Database.dll'), ('Content.Client', 'Content.Shared.Database.dll')):
        if digest((cohort / 'bin' / project / name).read_bytes()) != hashes[name]:
            raise ValueError('Acceptance runtime dependencies differ from the verified graph.')
    for name in ('Meta.CompilerWorker.dll', 'Meta.CompilerWorker.deps.json', 'Meta.CompilerWorker.runtimeconfig.json', 'Content.Meta.dll'):
        worker = cohort / 'bin' / 'MetaCompilerWorker' if version == 2 else cohort / 'bin' / 'Content.Server' / 'MetaCompilerWorker'
        if digest((worker / name).read_bytes()) != hashes['MetaCompilerWorker/' + name]:
            raise ValueError('Acceptance worker dependency differs from the verified graph.')
    if hashes['Content.Meta.dll'] != hashes['MetaCompilerWorker/Content.Meta.dll']:
        raise ValueError('Runtime and worker META cores differ.')
    snapshot = manifest['resources']
    if not snapshot['capturedBeforeBuild'] or snapshot['buildId'] != build_id or Path(snapshot['directory']).resolve() != resources or resources != (cohort / 'Resources').resolve():
        raise ValueError('Use the matching resources captured before this graph compilation.')
    if server != (cohort / 'bin' / 'Content.Server' / 'Content.Server.dll').resolve() or client != (cohort / 'bin' / 'Content.Client' / 'Content.Client.dll').resolve():
        raise ValueError('Explicit game assemblies must name the selected verified private cohort.')
    return {'buildId': build_id, 'manifestSha256': digest(encoded), 'assemblyHashes': hashes,
        'compiledSourceInventoryAvailable': 'sourceInventory' in manifest, 'sourceInventory': manifest.get('sourceInventory'),
        'identityScope': 'actual-selected-cohort-assemblies-and-frozen-resources;no-live-source-compilation-claim'}


class ProcessMemory(ctypes.Structure):
    _fields_ = [('cb', wintypes.DWORD), ('faults', wintypes.DWORD)] + [(name, ctypes.c_size_t) for name in
        ('peakWorkingSet', 'workingSet', 'peakPaged', 'paged', 'peakNonPaged', 'nonPaged', 'pagefile', 'peakPagefile', 'private')]


class SystemMemory(ctypes.Structure):
    _fields_ = [('length', wintypes.DWORD), ('load', wintypes.DWORD)] + [(name, ctypes.c_ulonglong) for name in
        ('totalPhysical', 'availablePhysical', 'totalPage', 'availablePage', 'totalVirtual', 'availableVirtual', 'availableExtended')]


class Fleet:
    def __init__(self, root, output, total_limit, client_limit, reserve):
        self.root = root; self.output = output
        self.total_limit = total_limit; self.client_limit = client_limit; self.reserve = reserve
        self.processes = []; self.clis = []; self.gate = threading.Lock(); self.stopping = threading.Event()
        self.abort = threading.Event(); self.failure = None; self.memory = []; self.initial = []
        self.kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        self.psapi = ctypes.WinDLL('psapi', use_last_error=True)
        self.psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessMemory), wintypes.DWORD]
        self.kernel.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
        self.kernel.GlobalMemoryStatusEx.argtypes = [ctypes.POINTER(SystemMemory)]
        self.kernel.GetCurrentProcess.restype = wintypes.HANDLE
        self.owner_ticks = self.started(self.kernel.GetCurrentProcess())
        self.monitor = threading.Thread(target=self.watch, daemon=True); self.monitor.start()

    def started(self, handle):
        created, exited, kernel, user = (wintypes.FILETIME() for _ in range(4))
        if not self.kernel.GetProcessTimes(handle, ctypes.byref(created), ctypes.byref(exited), ctypes.byref(kernel), ctypes.byref(user)):
            raise ctypes.WinError(ctypes.get_last_error())
        return (created.dwHighDateTime << 32 | created.dwLowDateTime) + 504911232000000000

    def check(self):
        if self.abort.is_set():
            raise RuntimeError(self.failure or 'Owned fleet aborted.')

    def launch(self, name, arguments, variables):
        self.check()
        environment = clean_environment() | variables | {'NC_DIAGNOSTICS_OWNER_PID': str(os.getpid()),
            'NC_DIAGNOSTICS_OWNER_START_TICKS': str(self.owner_ticks), 'NC_DIAGNOSTICS_ISOLATED': '1'}
        log = (self.output / (name + '.log')).open('wb')
        process = subprocess.Popen(['dotnet', *map(str, arguments)], cwd=self.root, env=environment, stdin=subprocess.PIPE,
            stdout=log, stderr=subprocess.STDOUT, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        with self.gate:
            self.processes.append((name, process, log))
            self.initial.append({'name': name, 'pid': process.pid, 'startUtcTicks': self.started(process._handle),
                'assembly': str(arguments[0]), 'assemblySha256': digest(Path(arguments[0]).read_bytes())})
            seal(self.output / 'processes.json', {'ownerPid': os.getpid(), 'ownerStartUtcTicks': self.owner_ticks, 'processes': self.initial})
            if self.abort.is_set() and process.poll() is None:
                process.terminate()
        return process

    def watch(self):
        while not self.stopping.wait(1):
            try:
                with self.gate:
                    owned = list(self.processes)
                total = 0; rows = []
                for name, process, _ in owned:
                    if process.poll() is not None:
                        raise RuntimeError(name + ' exited before fleet cleanup.')
                    counters = ProcessMemory(); counters.cb = ctypes.sizeof(counters)
                    if not self.psapi.GetProcessMemoryInfo(process._handle, ctypes.byref(counters), counters.cb):
                        raise ctypes.WinError(ctypes.get_last_error())
                    total += counters.workingSet
                    rows.append({'name': name, 'pid': process.pid, 'rss': counters.workingSet, 'private': counters.private})
                    if name.startswith('client') and counters.workingSet > self.client_limit:
                        raise MemoryError(name + ' exceeded the declared per-client RSS limit.')
                available = SystemMemory(); available.length = ctypes.sizeof(available)
                if not self.kernel.GlobalMemoryStatusEx(ctypes.byref(available)):
                    raise ctypes.WinError(ctypes.get_last_error())
                if total > self.total_limit or available.availablePhysical < self.reserve:
                    raise MemoryError('Declared fleet aggregate RSS/free physical-memory floor reached.')
                sample = {'monotonicNs': time.perf_counter_ns(), 'totalOwnedGameRss': total,
                    'availablePhysical': available.availablePhysical, 'processes': rows}
                if len(self.memory) < 14400:
                    self.memory.append(sample)
                else:
                    raise RuntimeError('Bounded four-hour fleet monitor exhausted.')
            except Exception as error:
                self.failure = type(error).__name__ + ':' + str(error); self.abort.set()
                # Kill only handles this Fleet created. No PID search, global process name or foreign session.
                for _, process, _ in list(self.processes):
                    if process.poll() is None:
                        process.terminate()
                return

    def close(self):
        self.stopping.set(); self.monitor.join(timeout=3)
        for cli in self.clis:
            cli.close()
        for _, process, log in reversed(self.processes):
            if process.poll() is None:
                try:
                    process.stdin.write(b'shutdown\n'); process.stdin.flush(); process.wait(timeout=5)
                except (OSError, subprocess.TimeoutExpired):
                    process.kill(); process.wait(timeout=5)
            log.close()
        seal(self.output / 'memory.json', {'unit': 'bytes', 'coverage': 'owned-game-process-working-sets-and-private-bytes;not-only-META;one-second-sampling', 'samples': self.memory})


def wait_for(fleet, action, predicate, timeout, description):
    deadline = time.monotonic() + timeout; last = None
    while time.monotonic() < deadline:
        fleet.check()
        try:
            last = action()
            if predicate(last):
                return last
        except (RuntimeError, OSError, TimeoutError) as error:
            last = type(error).__name__
        time.sleep(.5)
    raise TimeoutError(description + '; last response=' + str(last)[:2048])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--workspace', type=Path, default=Path(__file__).resolve().parents[4])
    parser.add_argument('--cli', type=Path, required=True)
    parser.add_argument('--server', type=Path, required=True, help='Immutable private Content.Server.dll bundle, already built.')
    parser.add_argument('--client', type=Path, required=True, help='Matching immutable private Content.Client.dll bundle.')
    parser.add_argument('--resources', type=Path, required=True, help='Already captured immutable Resources snapshot under workspace/artifacts.')
    parser.add_argument('--engine-resources', type=Path, required=True)
    parser.add_argument('--build-id', required=True)
    parser.add_argument('--clients', type=int, default=13)
    parser.add_argument('--sessions', type=int, default=50)
    parser.add_argument('--views-per-client', type=int, default=4)
    parser.add_argument('--startup-parallel', type=int, default=2)
    parser.add_argument('--startup-timeout', type=int, default=600)
    parser.add_argument('--seconds', type=int, default=28)
    parser.add_argument('--action-hz', type=float, default=1)
    parser.add_argument('--max-total-rss-gib', type=float, default=16)
    parser.add_argument('--max-client-rss-gib', type=float, default=1)
    parser.add_argument('--free-reserve-gib', type=float, default=2)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if os.name != 'nt':
        parser.error('The owned RSS/parent-lifetime supervisor currently targets Windows native clients.')
    try:
        allocation = layout(args.clients, args.sessions, args.views_per_client)
        if max(controller_operations(args.seconds, args.action_hz, views) for views in allocation) > 128:
            raise ValueError('Capture/actions/stops exceed128 retained operations/client; use28s at1Hz for four views.')
    except ValueError as error:
        parser.error(str(error))
    if not 2 <= args.startup_parallel <= 4 or not 60 <= args.startup_timeout <= 1200:
        parser.error('Use2..4 startup workers and60..1200s startup timeout.')
    if not 1 <= args.max_total_rss_gib <= 64 or not .25 <= args.max_client_rss_gib <= 4 or not 1 <= args.free_reserve_gib <= 16:
        parser.error('Declare finite RSS ceilings and a physical memory reserve.')
    root = args.workspace.resolve(); output = args.output.resolve(); resources = args.resources.resolve(); engine = args.engine_resources.resolve()
    if not output.is_relative_to(root / 'artifacts') or not resources.is_relative_to(root / 'artifacts') or not resources.is_dir():
        parser.error('Output and immutable resource snapshot must stay under workspace/artifacts.')
    for path in (args.cli, args.server, args.client):
        if not path.resolve().is_relative_to(root) or not path.is_file():
            parser.error('Use concrete already built assemblies within this workspace.')
    output.mkdir(parents=True, exist_ok=False)
    manifest = {'schemaVersion': 1, 'startedAt': datetime.now(timezone.utc).isoformat(), 'requested': vars(args),
        'coverage': 'real-native-headless-clients;ordinary-auth,mind,ticker,BUI,MUI-reducer,typed-actions,ACKs;not-graphics/RSI-performance',
        'viewAllocation': allocation, 'controllerOperationsPerClient': [controller_operations(args.seconds, args.action_hz, count) for count in allocation],
        'identityMeaning': 'actual connected users and separate renderer sessions;13 users are not50 users', 'result': None}
    fleet = Fleet(root, output, int(args.max_total_rss_gib * 2**30), int(args.max_client_rss_gib * 2**30), int(args.free_reserve_gib * 2**30))
    master = None; fixture = None; load = None; clients = []
    try:
        manifest['cohort'] = verify_cohort(root, args.build_id, args.server.resolve(), args.client.resolve(), resources)
        manifest['resources'] = inventory(resources); manifest['engineResources'] = inventory(engine)
        seed = output / 'client-seed'; stage_binaries(args.client.resolve().parent, seed)
        server_dir = output / 'server' / 'bin' / 'Content.Server'; stage_binaries(args.server.resolve().parent, server_dir)
        manifest['clientBinaries'] = inventory(seed, 10000); manifest['serverBinaries'] = inventory(server_dir, 10000)
        with socket.socket() as probe:
            probe.bind(('127.0.0.1', 0)); port = probe.getsockname()[1]
        config = output / 'server.toml'
        config.write_text(f'''[net]\nport={port}\nbindto="127.0.0.1"\nupnp=false\nmax_connections=64\n[status]\nenabled=true\nbind="127.0.0.1:{port}"\n[auth]\nmode=2\n[hub]\nadvertise=false\n[game]\nhostname="Isolated META frontend load"\ndummyticker=false\ndefaultpreset="Sandbox"\nmap="Empty"\nmap_rotation=false\nlobbyenabled=false\nauto_pause_empty=false\n[database]\nengine="sqlite"\nsqlite_dbpath="preferences.db"\n[nc_neural]\nenabled=false\n[config]\npreset_development=false\npreset_debug=false\n''', encoding='utf-8')
        for name in ('server', 'client-seed'):
            for directory in (output / name / 'Resources', output / name / 'RobustToolbox' / 'Resources'):
                directory.mkdir(parents=True, exist_ok=True)
        server_token = secrets.token_hex(32)
        fleet.launch('server', [server_dir / 'Content.Server.dll', '--config-file', config, '--data-dir', output / 'server-data',
            '--mount-dir', engine, '--mount-dir', resources], {'NC_DIAGNOSTICS_TOKEN': server_token, 'NC_DIAGNOSTICS_PROFILE': 'fixture-admin'})
        cli_environment = clean_environment() | {'SS14_MCP_SERVER': f'http://127.0.0.1:{port}/', 'SS14_MCP_SERVER_TOKEN': server_token}
        if args.build_id:
            cli_environment['SS14_MCP_BUILD_ID'] = args.build_id
        master = Cli(root, args.cli.resolve(), cli_environment); fleet.clis.append(master)
        server_session = wait_for(fleet, lambda: data(master.call('session_get', timeout=30)), lambda value: bool(value.get('instanceId')), args.startup_timeout, 'Native server bridge did not become ready')
        manifest['serverSession'] = server_session
        # Each bounded CLI owns only one actual client target plus this explicit server. Native
        # game RSS is measured separately from controller overhead; controller CPU is not server CPU.
        def start_client(index):
            directory = output / ('client%02d' % index); assembly = directory / 'bin' / 'Content.Client'
            stage_binaries(seed, assembly, link=True)
            (directory / 'Resources').mkdir(); (directory / 'RobustToolbox' / 'Resources').mkdir(parents=True)
            pipe = 'meta-load-' + secrets.token_hex(16); token = secrets.token_hex(32)
            game = fleet.launch('client%02d' % index, [assembly / 'Content.Client.dll', '--self-contained', '--connect', '--connect-address',
                f'127.0.0.1:{port}', '--username', 'MetaLoad%02d' % index, '--mount-dir', engine, '--mount-dir', resources,
                '--headless', '--cvar', 'res.texturepreloadingenabled=false'],
                {'NC_DIAGNOSTICS_TOKEN': token, 'NC_DIAGNOSTICS_PROFILE': 'playtest', 'NC_DIAGNOSTICS_PIPE': pipe})
            environment = clean_environment() | {'SS14_MCP_CLIENT_PIPE': pipe, 'SS14_MCP_CLIENT_TOKEN': token}
            controller = Cli(root, args.cli.resolve(), environment, poll_interval=.01)
            with fleet.gate:
                fleet.clis.append(controller)
            session = wait_for(fleet, lambda: data(controller.call('session_get', {'targetId': 'client'}, timeout=30)),
                lambda value: bool(value.get('userId')) and value.get('lastRealTick', 0) > 0, args.startup_timeout, 'Actual native client connection did not become ready')
            return {'index': index, 'cli': controller, 'pid': game.pid, 'connection': session, 'sessionId': None, 'actions': 0, 'completed': 0, 'lastRequest': None, 'nextAction': 0}
        with ThreadPoolExecutor(max_workers=args.startup_parallel) as executor:
            for participant in executor.map(start_client, range(1, args.clients + 1)):
                clients.append(participant); fleet.check()
        if len({participant['connection']['userId'] for participant in clients}) != args.clients:
            raise RuntimeError('Native authentication did not produce the requested distinct real users.')
        fixture = data(master.call('fixture_create'))['fixtureId']
        host = data(master.call('fixture_spawn', {'fixtureId': fixture, 'prototype': 'NCMetaPerformanceHost', 'x': 0, 'y': 0}))['netEntity']
        owner = data(master.call('fixture_spawn', {'fixtureId': fixture, 'prototype': 'MobHuman', 'x': .75, 'y': 0}))['netEntity']
        master.call('fixture_attach', {'fixtureId': fixture, 'actor': owner, 'userId': clients[0]['connection']['userId']})
        # A mutation with an uncertain response is never blindly replayed. Native rejection is
        # retained as a real failed attempt; inspect its owned fixture before another run.
        prepared = data(master.call('meta_app_load_prepare', {'fixtureId': fixture, 'actor': owner, 'host': host}))
        load = prepared['loadId']
        ready = wait_for(fleet, lambda: data(master.call('meta_app_load_status', {'loadId': load})),
            lambda value: value['stage'] in {'Ready', 'Failed'}, args.startup_timeout, 'Actual source→worker→native installation did not become ready')
        if ready['stage'] != 'Ready':
            raise RuntimeError('Actual app preparation failed: ' + ready['error'])
        manifest['appAdmission'] = ready
        observed = run_frontends(fleet, master, clients, load, fixture, ready, args, output, seal, wait_for)
        manifest.update({key: value for key, value in observed.items() if key.endswith('Observed')})
        if not observed['complete']:
            raise RuntimeError('Requested native frontend coverage is incomplete; all collected raw observations were retained.')
        if inventory(resources)['sha256'] != manifest['resources']['sha256'] or inventory(engine)['sha256'] != manifest['engineResources']['sha256']:
            raise RuntimeError('Immutable resource input changed during the actual run.')
        if inventory(seed, 10000)['sha256'] != manifest['clientBinaries']['sha256'] or inventory(server_dir, 10000)['sha256'] != manifest['serverBinaries']['sha256']:
            raise RuntimeError('Owned immutable binary copies changed during the actual run.')
        manifest['result'] = {'status': 'recorded', 'actualDistinctUsers': args.clients, 'actualNativeFrontendSessions': observed['sessions'],
            'completedNativeActions': observed['completed'], 'fiftySessionGateCovered': observed['sessions'] == 50,
            'cpuAttribution': 'separate-external-trace-required', 'latencies': 'raw-client-monotonic-action-to-result;not-server-only-CPU'}
    except BaseException as error:
        manifest['failure'] = type(error).__name__ + ':' + str(error)
        raise
    finally:
        if master:
            if load:
                try: master.call('meta_app_load_stop', {'loadId': load})
                except Exception as error: manifest['loadCleanupError'] = type(error).__name__
            if fixture:
                try: master.call('fixture_destroy', {'fixtureId': fixture})
                except Exception as error: manifest['fixtureCleanupError'] = type(error).__name__
        fleet.close(); manifest['monitorFailure'] = fleet.failure; manifest['finishedAt'] = datetime.now(timezone.utc).isoformat()
        seal(output / 'manifest.json', manifest)


if __name__ == '__main__':
    main()
