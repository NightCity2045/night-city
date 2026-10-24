# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
"""Pure scheduling/identity checks; these do not claim native load measurement."""
import unittest
import uuid
from pathlib import Path
from unittest.mock import patch
from frontend_driver import FrontendDriver, admission_owners, controller_operations, layout
from reduce_frontend import reduce_frontends


class NativeClientStub:
    def __init__(self, ids):
        self.states = {value: {'sessionId': value, 'lastCompletedAction': None, 'lastActionSuccess': True} for value in ids}
        self.calls = []

    def call(self, command, args):
        self.calls.append((command, args))
        if command == 'meta_app_frontend_list':
            return {'data': {'sessions': list(self.states.values())}}
        if command == 'meta_app_frontend_action':
            return {'data': {'requestId': str(uuid.uuid4())}}
        return {'data': {}}


class FrontendDriverTests(unittest.TestCase):
    def make_driver(self, views=4):
        ids = [str(uuid.uuid4()) for _ in range(views)]
        client = NativeClientStub(ids); clock = [0.0]
        driver = FrontendDriver(client, [{'sessionId': value} for value in ids], 28, 1, 0, 0, views, lambda: clock[0])
        return client, clock, driver

    def test_fifty_views_are_thirteen_users_under_the_unchanged_four_view_cap(self):
        self.assertEqual(layout(13, 50, 4), [4] * 12 + [2])
        self.assertEqual(controller_operations(28, 1, 4), 120)
        self.assertGreater(controller_operations(60, 1, 4), 128)
        for values in ((12, 50, 4), (13, 50, 5), (13, 12, 4), (50, 50, 0)):
            with self.subTest(values=values), self.assertRaises(ValueError):
                layout(*values)

    def test_incompatible_native_cohort_is_not_silently_one_window_per_user(self):
        for status in ({}, {'frontendAdmissionVersion': 2, 'maximumViewsPerConnection': 4, 'physicalUiOwners': ['same'] * 4}):
            with self.assertRaises(ValueError):
                admission_owners(status, 4)
        owners = ['one', 'two', 'three', 'four']
        self.assertEqual(admission_owners({'frontendAdmissionVersion': 2, 'maximumViewsPerConnection': 4, 'physicalUiOwners': owners}, 2), owners[:2])

    def test_each_session_has_one_pending_action_and_neighbours_keep_progressing(self):
        client, clock, driver = self.make_driver()
        clock[0] = .9
        for _ in range(20): driver.poll()
        self.assertEqual([session['actions'] for session in driver.sessions], [1, 1, 1, 1])
        self.assertEqual(driver.mutations, 4)
        session = driver.sessions[2]
        client.states[session['sessionId']]['lastCompletedAction'] = session['lastRequest']
        clock[0] = 1.9; driver.poll()
        self.assertEqual(session['completed'], 1)
        self.assertEqual(session['actions'], 2)

    def test_late_schedule_skips_slots_without_a_catchup_burst(self):
        client, clock, driver = self.make_driver(1)
        clock[0] = 5.2; driver.poll()
        self.assertEqual(driver.sessions[0]['skippedSlots'], 5)
        self.assertEqual(driver.sessions[0]['actions'], 1)
        client.states[driver.sessions[0]['sessionId']]['lastCompletedAction'] = driver.sessions[0]['lastRequest']
        driver.poll()
        self.assertEqual(driver.sessions[0]['actions'], 1)

    def test_no_extra_actions_during_completion_grace_and_failed_results_are_real_failures(self):
        client, clock, driver = self.make_driver(1)
        driver.poll(); session = driver.sessions[0]
        client.states[session['sessionId']]['lastCompletedAction'] = session['lastRequest']
        client.states[session['sessionId']]['lastActionSuccess'] = False
        with self.assertRaisesRegex(RuntimeError, 'unsuccessfully'): driver.poll(False)
        client.states[session['sessionId']]['lastActionSuccess'] = True
        driver.poll(False)
        self.assertEqual(session['completed'], 1)
        self.assertEqual(session['actions'], 1)

    def test_closed_renderer_and_exhausted_operation_allowance_fail_closed(self):
        client, clock, driver = self.make_driver(1)
        client.states.clear()
        with self.assertRaisesRegex(RuntimeError, 'renderer closed'): driver.poll()
        driver.mutations = 128
        with self.assertRaisesRegex(RuntimeError, 'no reset or retry'): driver.mutate('meta_app_frontend_capture', {})

    def test_null_native_identity_and_nonfinite_rate_are_clean_rejections(self):
        with self.assertRaises(ValueError): FrontendDriver(None, [{'sessionId': None}], 28, 1, 0, 0, 1)
        with self.assertRaises(ValueError): controller_operations(28, float('nan'), 4)


class ReductionProtocolTests(unittest.TestCase):
    def fixture(self):
        # Constructed protocol records test only the reducer, never count as measured frontends.
        manifest = {'requested': {'clients': 13, 'sessions': 50, 'views_per_client': 4}, 'viewAllocation': [4] * 12 + [2],
            'result': {'status': 'recorded'}, 'appAdmission': {'host': 'host', 'runtimeGeneration': 'generation'}}
        records = {'manifest.json': manifest}
        for index in range(50):
            user, session = 'user' + str(index // 4), str(uuid.uuid4())
            records['frontend-%02d.json' % (index + 1)] = {'connection': {'userId': user},
                'admission': {'userId': user, 'sessionId': session, 'uiOwner': 'ui' + str(index % 4), 'host': 'host', 'runtimeGeneration': 'generation'},
                'session': {'sessionId': session, 'frequency': 1000, 'samples': 1, 'droppedSamples': 0, 'acknowledgedRevision': 1,
                    'revision': 1, 'nativeControls': 2, 'fullBytes': 10, 'patchBytes': 2}, 'coverageComplete': True,
                'samples': [{'kind': 'action-completed', 'requestId': str(uuid.uuid4()), 'latencyTicks': 10}], 'actionsCompleted': 1, 'actionsSubmitted': 1}
        return records

    def reduce(self, records):
        with patch('reduce_frontend.read_sealed', side_effect=lambda path: records[path.name]):
            return reduce_frontends(Path('not-a-native-run'))

    def test_protocol_counts_thirteen_users_and_fifty_distinct_renderer_sessions_separately(self):
        result = self.reduce(self.fixture())
        self.assertEqual(result['actualDistinctNativeUsers'], 13)
        self.assertEqual(result['actualNativeFrontendSessions'], 50)
        self.assertEqual(result['actualPhysicalUiOwners'], 4)
        self.assertTrue(result['rawCoverageComplete'])

    def test_same_user_cannot_count_duplicate_physical_owner_as_another_view(self):
        records = self.fixture(); records['frontend-02.json']['admission']['uiOwner'] = 'ui0'
        result = self.reduce(records)
        self.assertFalse(result['fiftyFrontendSessionsAccepted'])
        self.assertIsNone(result['mergedClientActionToCompletedResultMs'])

    def test_overflow_or_wrong_backend_never_produces_an_accepted_prefix_quantile(self):
        for field in ('overflow', 'backend'):
            records = self.fixture()
            if field == 'overflow': records['frontend-01.json']['session']['droppedSamples'] = 1
            else: records['frontend-01.json']['admission']['runtimeGeneration'] = 'another-generation'
            result = self.reduce(records)
            self.assertFalse(result['fiftyFrontendSessionsAccepted'])
            self.assertIsNone(result['mergedClientActionToCompletedResultMs'])


if __name__ == '__main__': unittest.main()
