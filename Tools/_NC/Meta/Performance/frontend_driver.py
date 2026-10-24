# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
"""Bounded orchestration of real native MUI actions. No native quotas or identities are created here."""
import math
import time
import uuid
from concurrent.futures import ThreadPoolExecutor
from run import data


def run_frontends(fleet, master, clients, load, fixture, ready, args, output, seal, wait_for):
    allocation = layout(args.clients, args.sessions, args.views_per_client)
    owners = admission_owners(ready, args.views_per_client)  # reject unsupported native cohort before opening anything
    sessions = []
    for participant, count in zip(clients, allocation):
        attached = data(master.call('meta_app_load_attach', {'loadId': load, 'userId': participant['connection']['userId']}))
        participant['views'] = []
        for ui_owner in owners[:count]:
            opened = data(master.call('meta_app_load_open', {'loadId': load, 'fixtureId': fixture, 'actor': attached['actor'], 'uiOwner': ui_owner}))
            if opened.get('userId') != participant['connection']['userId'] or opened.get('uiOwner') != ui_owner or opened.get('runtimeGeneration') != ready['runtimeGeneration'] or opened.get('host') != ready['host']:
                raise RuntimeError('Native admission identity differs from the actual connection/physical owner/backend.')
            frontend = wait_for(fleet, lambda: data(participant['cli'].call('meta_app_frontend_list', {'targetId': 'client'})),
                lambda value: any(item['sessionId'] == opened['sessionId'] and item['revision'] >= 0 and item['acknowledgedRevision'] >= 0 for item in value['sessions']),
                60, 'Native snapshot/reducer/ACK did not reach the actual frontend')
            view = {'index': len(sessions) + 1, 'clientIndex': participant['index'], 'sessionId': opened['sessionId'],
                'admission': opened, 'connection': participant['connection'], 'initialFrontend': frontend}
            sessions.append(view); participant['views'].append(view)
    if len({session['sessionId'] for session in sessions}) != args.sessions:
        raise RuntimeError('Native admission did not produce the exact requested distinct renderer sessions.')
    # Capture all existing renderers before choosing one common action epoch.
    drivers = []; offset = 0
    for participant in clients:
        driver = FrontendDriver(participant['cli'], participant['views'], args.seconds, args.action_hz, 0, offset, args.sessions)
        driver.capture(); drivers.append(driver); offset += len(participant['views'])
    start = time.monotonic() + 1
    for driver in drivers:
        driver.start = start; driver.deadline = start + args.seconds
        for session in driver.sessions:
            session['nextAction'] += start
    with ThreadPoolExecutor(max_workers=min(16, len(clients))) as executor:
        while time.monotonic() < start + args.seconds:
            fleet.check(); list(executor.map(lambda driver: driver.poll(), drivers)); time.sleep(.01)
        grace = time.monotonic() + 15
        while any(driver.pending for driver in drivers) and time.monotonic() < grace:
            fleet.check(); list(executor.map(lambda driver: driver.poll(allow_actions=False), drivers)); time.sleep(.05)
    drained = time.monotonic()
    complete = True; completed = 0
    for driver in drivers:
        for capture in driver.collect():
            seal(output / ('frontend-%02d.json' % capture['index']), capture | {'actionsSubmitted': capture['actions'], 'actionsCompleted': capture['completed']})
            complete &= capture['coverageComplete']; completed += capture['completed']
    return {'sessions': len(sessions), 'completed': completed, 'complete': complete,
        'controllerMutationsObserved': [driver.mutations for driver in drivers], 'measurementSecondsObserved': time.monotonic() - start,
        'timingObserved': {'actionWindowSeconds': args.seconds, 'completionDrainSeconds': max(0, drained - start - args.seconds),
            'collectionSeconds': time.monotonic() - drained, 'clock': 'external-controller-monotonic;not-nativeCPU'}}


def layout(clients, sessions, views_per_client):
    if any(type(value) is not int for value in (clients, sessions, views_per_client)) or not 1 <= clients <= 50 or not 1 <= views_per_client <= 4:
        raise ValueError('Use1..50 actual clients and1..4 native views/client.')
    if not clients <= sessions <= min(50, clients * views_per_client) or sessions <= (clients - 1) * views_per_client:
        raise ValueError('Fill each actual client in order, leaving only the final client partly filled.')
    return [min(views_per_client, sessions - index * views_per_client) for index in range(clients)]


def controller_operations(seconds, hz, views):
    if type(seconds) is not int or not 1 <= seconds <= 60 or not isinstance(hz, (int, float)) or isinstance(hz, bool) or not math.isfinite(hz) or not .1 <= hz <= 2 or type(views) is not int or not 1 <= views <= 4:
        raise ValueError('Bounded frontend duration/rate/views required.')
    return views * (math.ceil(seconds * hz) + 2)  # capture + each declared action + stop


def admission_owners(status, count):
    owners = status.get('physicalUiOwners')
    if status.get('frontendAdmissionVersion') != 2 or status.get('maximumViewsPerConnection') != 4 or not isinstance(owners, list) or len(owners) != 4 or len(set(owners)) != 4 or any(not isinstance(owner, str) or not owner for owner in owners):
        raise ValueError('Selected native cohort lacks explicit four-physical-owner app admission v2; rebuild at the approved source boundary.')
    if not 1 <= count <= 4:
        raise ValueError('Normal native client view limit is4.')
    return owners[:count]


class FrontendDriver:
    def __init__(self, cli, sessions, seconds, hz, start, global_offset, total_sessions, clock=time.monotonic):
        if controller_operations(seconds, hz, len(sessions)) > 128:
            raise ValueError('Requested capture/actions/stops exceed retained128-operation controller budget.')
        ids = [session.get('sessionId') for session in sessions]
        if any(not isinstance(value, str) for value in ids) or len(set(ids)) != len(ids) or any(str(uuid.UUID(value)) != value for value in ids):
            raise ValueError('Require distinct canonical native session IDs.')
        self.cli, self.seconds, self.hz, self.start, self.clock = cli, seconds, hz, start, clock
        self.deadline, self.cursor, self.mutations = start + seconds, 0, 0
        self.sessions = [session | {'actions': 0, 'completed': 0, 'lastRequest': None, 'skippedSlots': 0,
            'nextAction': start + (global_offset + index) / total_sessions / hz} for index, session in enumerate(sessions)]

    def mutate(self, command, arguments):
        if self.mutations >= 128:
            raise RuntimeError('Bounded native driver exhausted its operation allowance; no reset or retry.')
        self.mutations += 1
        return data(self.cli.call(command, arguments))

    def capture(self):
        for session in self.sessions:
            self.mutate('meta_app_frontend_capture', {'targetId': 'client', 'sessionId': session['sessionId']})

    def poll(self, allow_actions=True):
        states = data(self.cli.call('meta_app_frontend_list', {'targetId': 'client'}))['sessions']
        indexed = {state['sessionId']: state for state in states}
        if len(states) > 4 or len(indexed) != len(states):
            raise RuntimeError('Native renderer inventory exceeds the normal four-view bound or repeats an identity.')
        for session in self.sessions:
            if session['sessionId'] not in indexed:
                raise RuntimeError('A real native renderer closed; capture is incomplete.')
            state = indexed[session['sessionId']]
            if session['lastRequest'] and state['lastCompletedAction'] == session['lastRequest']:
                if not state['lastActionSuccess']:
                    raise RuntimeError('Native action completed unsuccessfully.')
                session['completed'] += 1
                session['lastRequest'] = None
        now = self.clock()
        if not allow_actions or now >= self.deadline:
            return
        # At most one action per client/poll. Round robin prevents a slow form from
        # blocking its neighbours. A late clock skips slots rather than catching up.
        for offset in range(len(self.sessions)):
            index = (self.cursor + offset) % len(self.sessions)
            session = self.sessions[index]
            if session['lastRequest'] or now < session['nextAction']:
                continue
            submitted = self.mutate('meta_app_frontend_action', {'targetId': 'client', 'sessionId': session['sessionId'], 'action': 'step', 'json': '{}'})
            if not isinstance(submitted.get('requestId'), str) or str(uuid.UUID(submitted['requestId'])) != submitted['requestId']:
                raise ValueError('Native action result lacks a canonical request identity; no mutation retry.')
            session['lastRequest'] = submitted['requestId']
            session['actions'] += 1
            missed = max(0, math.floor((now - session['nextAction']) * self.hz))
            session['skippedSlots'] += missed
            session['nextAction'] += (missed + 1) / self.hz
            self.cursor = (index + 1) % len(self.sessions)
            return

    @property
    def pending(self):
        return any(session['lastRequest'] for session in self.sessions)

    def collect(self):
        for session in self.sessions:
            self.mutate('meta_app_frontend_stop', {'targetId': 'client', 'sessionId': session['sessionId']})
        result = []
        for session in self.sessions:
            arguments = {'targetId': 'client', 'sessionId': session['sessionId'], 'offset': 0, 'count': 512}
            first = data(self.cli.call('meta_app_frontend_read', arguments))
            state, samples = first['session'], first['samples']
            if state['sessionId'] != session['sessionId'] or not 0 <= state['samples'] <= 2048 or len(samples) > min(512, state['samples']):
                raise RuntimeError('Native capture identity/count mismatch.')
            while len(samples) < state['samples']:
                page = data(self.cli.call('meta_app_frontend_read', arguments | {'offset': len(samples)}))
                if not page['samples'] or len(page['samples']) > min(512, state['samples'] - len(samples)) or page['session']['samples'] != state['samples']:
                    raise RuntimeError('Stopped native raw capture did not advance consistently.')
                samples.extend(page['samples'])
            result.append(session | {'session': state, 'samples': samples,
                'coverageComplete': not session['lastRequest'] and session['completed'] == session['actions'] and
                    session['actions'] >= math.floor(self.seconds * self.hz) and not session['skippedSlots'] and
                    not state['droppedSamples'] and state['nativeControls'] > 0 and len(samples) == state['samples'] and
                    state['acknowledgedRevision'] >= state['revision']})
        return result
