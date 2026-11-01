# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
"""Reduce sealed observations from actual native frontends. No synthetic session substitution."""
import argparse
import hashlib
import json
from pathlib import Path
from reduce import quantiles


def read_sealed(path):
    encoded = path.read_bytes()
    if hashlib.sha256(encoded).hexdigest() != path.with_suffix(path.suffix + '.sha256').read_text().strip():
        raise ValueError('Frontend capture seal mismatch: ' + path.name)
    return json.loads(encoded)


def reduce_frontends(folder):
    manifest = read_sealed(folder / 'manifest.json')
    if manifest.get('failure') or not manifest.get('result') or manifest.get('monitorFailure'):
        return {'status': 'failed-or-not-measured', 'failure': manifest.get('failure') or manifest.get('monitorFailure'), 'fiftyFrontendSessionsAccepted': False}
    requested_users = manifest['requested']['clients']; requested = manifest['requested'].get('sessions', requested_users)
    users = set(); sessions = set(); elapsed = []; rows = []; by_user = {}; owners = set(); owner_sessions = set()
    complete = True; submitted = completed = body_full = body_patch = controls = 0
    for index in range(1, requested + 1):
        capture = read_sealed(folder / ('frontend-%02d.json' % index)); state = capture['session']; frequency = state['frequency']
        if frequency <= 0:
            raise ValueError('Invalid native Stopwatch frequency.')
        users.add(capture['connection']['userId']); sessions.add(state['sessionId'])
        user = capture['connection']['userId']; by_user[user] = by_user.get(user, 0) + 1
        if 'admission' in capture:
            admission = capture['admission']; owners.add(admission['uiOwner'])
            pair = (user, admission['uiOwner'])
            if pair in owner_sessions: complete = False
            owner_sessions.add(pair)
            if admission['sessionId'] != state['sessionId'] or admission['userId'] != user or admission['runtimeGeneration'] != manifest['appAdmission']['runtimeGeneration'] or admission['host'] != manifest['appAdmission']['host']:
                complete = False
        elif manifest['requested'].get('views_per_client', 1) > 1:
            complete = False
        latencies = [sample['latencyTicks'] * 1000 / frequency for sample in capture['samples'] if sample['kind'] == 'action-completed' and sample['latencyTicks'] > 0]
        observed_ids = {sample['requestId'] for sample in capture['samples'] if sample['kind'] == 'action-completed' and sample['latencyTicks'] > 0}
        if not capture.get('coverageComplete', True) or state['nativeControls'] <= 0 or state['droppedSamples'] or len(capture['samples']) != state['samples'] or len(observed_ids) != capture['actionsCompleted'] or capture['actionsCompleted'] != capture['actionsSubmitted'] or state['acknowledgedRevision'] < state['revision']:
            complete = False
        elapsed.extend(latencies); submitted += capture['actionsSubmitted']; completed += capture['actionsCompleted']
        body_full += state['fullBytes']; body_patch += state['patchBytes']; controls += state['nativeControls']
        rows.append({'index': index, 'nativeSessionId': state['sessionId'], 'nativeControlCount': state['nativeControls'],
            'submittedActions': capture['actionsSubmitted'], 'completedActions': capture['actionsCompleted'], 'actionToResultMs': quantiles(latencies),
            'revision': state['revision'], 'acknowledgedRevision': state['acknowledgedRevision'], 'droppedSamples': state['droppedSamples']})
    expected_allocation = manifest.get('viewAllocation', [1] * requested_users)
    complete = complete and len(users) == requested_users and len(sessions) == requested and sorted(by_user.values()) == sorted(expected_allocation) and all(count <= 4 for count in by_user.values())
    return {'schemaVersion': 1, 'status': 'recorded' if complete else 'recorded-incomplete-coverage',
        'actualDistinctNativeUsers': len(users), 'actualNativeFrontendSessions': len(sessions), 'rawCoverageComplete': complete,
        'actualPhysicalUiOwners': len(owners), 'identityMeaning': 'connected users counted separately from actual native renderer sessions',
        'fiftyFrontendSessionsAccepted': complete and requested == 50, 'submittedActions': submitted, 'completedActions': completed,
        'mergedClientActionToCompletedResultMs': quantiles(elapsed) if complete else None, 'nativeControlCount': controls,
        'stateJsonBodyBytes': {'full': body_full, 'patch': body_patch}, 'perFrontend': rows,
        'coverage': 'actual-native-clients-and-BUI-reducers;latency-local-to-each-client-clock;state-JSON-body-bytes-not-socket-bytes',
        'finalPerformanceAcceptance': None, 'reason': 'This proves only actual frontend/session/action transport coverage. Server CPU/elapsed/queues/fan-out and repeated balanced workload evidence are separate measurements.'}


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('folder', type=Path); args = parser.parse_args(); folder = args.folder.resolve()
    result = reduce_frontends(folder); encoded = json.dumps(result, indent=2).encode('utf-8')
    (folder / 'frontend-summary.json').write_bytes(encoded)
    (folder / 'frontend-summary.json.sha256').write_text(hashlib.sha256(encoded).hexdigest() + '\n', encoding='ascii')
    print(json.dumps({'status': result['status'], 'fiftyFrontendSessionsAccepted': result['fiftyFrontendSessionsAccepted']}))


if __name__ == '__main__':
    main()
