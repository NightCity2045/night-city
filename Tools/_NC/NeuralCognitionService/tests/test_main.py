# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

import json
import unittest
from types import SimpleNamespace

from fastapi import HTTPException

from app import main


class _FakeResponses:
    def __init__(self, selected_capability: str, target_handle: str = "", should_speak: bool = True,
                 confidence: int = 800, intent_target_handle: str | None = None):
        self._selected_capability = selected_capability
        self._target_handle = target_handle
        self._should_speak = should_speak
        self._confidence = confidence
        self._intent_target_handle = target_handle if intent_target_handle is None else intent_target_handle

    async def create(self, **_kwargs):
        decision_mode = "act" if self._should_speak else "wait"
        commands = [{
            "capabilityId": self._selected_capability,
            "goalKey": "execute",
            "targetHandle": self._target_handle,
            "priority": 500,
            "expectedOutcomeKey": "safe-option-selected",
            "informationGapKeys": [],
            "arguments": [],
        }] if decision_mode == "act" else []
        answer = {
            "decision_mode": decision_mode,
            "intent": {
                "key": "safe-option",
                "targetHandle": self._intent_target_handle,
                "priority": 500,
            } if decision_mode == "act" else None,
            "sense_requests": [],
            "speech_acts": [{
                "actKey": "statement",
                "text": "Я выбираю безопасный вариант.",
            }] if self._should_speak else [],
            "commands": commands,
            "wake": {
                "on": ["command-result"],
                "afterSeconds": 60,
            },
            "memory_suggestions": [],
            "reason_tags": ["safe-option"],
            "summary_key": "safe-option",
            "confidence": self._confidence,
            "disclosed_fact_handles": [],
            "withheld_fact_handles": [],
        }
        return SimpleNamespace(
            output_text=json.dumps(answer),
            output=[],
            status="completed",
            incomplete_details=None,
            error=None,
            usage=SimpleNamespace(input_tokens=40, output_tokens=12),
            id="fake-provider-request",
        )


class _FakeClient:
    def __init__(self, selected_capability: str, target_handle: str = "", should_speak: bool = True,
                 confidence: int = 800, intent_target_handle: str | None = None):
        self.responses = _FakeResponses(
            selected_capability, target_handle, should_speak, confidence, intent_target_handle)

    async def close(self):
        pass


class _FakeClientFactory:
    def __init__(self, selected_capability: str, target_handle: str = "", should_speak: bool = True,
                 confidence: int = 800, intent_target_handle: str | None = None):
        self._selected_capability = selected_capability
        self._target_handle = target_handle
        self._should_speak = should_speak
        self._confidence = confidence
        self._intent_target_handle = intent_target_handle

    def __call__(self, *, api_key: str):
        if api_key != "test-key":
            raise AssertionError("unexpected provider credential")
        return _FakeClient(
            self._selected_capability,
            self._target_handle,
            self._should_speak,
            self._confidence,
            self._intent_target_handle)


class NeuralCognitionServiceTest(unittest.IsolatedAsyncioTestCase):
    @staticmethod
    def action(capability_id: str, operation: str = "holdposition", target: str = "none") -> dict:
        return {
            "capabilityId": capability_id,
            "domainKey": "core",
            "taskKindKey": "knownprocedure",
            "goalKeys": ["execute"],
            "actionIds": [f"test-{operation}"],
            "operationKeys": [operation],
            "targetKinds": [target],
        }

    def request(self) -> main.Request:
        return main.Request.model_validate({
            "schemaVersion": main.NEURAL_SCHEMA_VERSION,
            "requestId": "5912157d-3f44-4075-bf78-98df6cab44d4",
            "kind": "StrategicDecision",
            "priority": "P1",
            "actorId": {"value": "aa0d9698-39fd-4678-82a2-f736cf172995"},
            "sessionEpoch": {"value": "b13348b8-fd34-48c9-81a5-e2f4e81ed5a7"},
            "contextVersion": 7,
            "expiresAt": {"ticks": 100},
            "promptProfile": "NCLivingCityDefault",
            "promptStableKey": main.NPC_SYSTEM_PROMPT_STABLE_KEY,
            "promptVersion": main.NPC_SYSTEM_PROMPT_VERSION,
            "evaluationProfile": main.NPC_EVALUATION_PROFILE,
            "facts": [{
                "handle": "fact-001",
                "domain": "core",
                "key": "actor.can-act",
                "subject": "actor:aa0d9698",
                "confidence": 1,
                "privacy": "Public",
            }],
            "allowedCapabilityIds": ["NCCognitionStrategicFallback"],
            "availableActions": [self.action("NCCognitionStrategicFallback")],
            "topicKeys": ["safe-option"],
            "locale": "ru-RU",
            "autonomy": {
                "policyKey": "test-autonomy",
                "minimumIntentConfidence": 500,
                "maximumIntentPriority": 900,
                "targetedIntentsAllowed": True,
            },
            "disclosure": {
                "maximumPrivacy": "Public",
                "knownFactHandles": ["fact-001"],
                "allowedFactHandles": ["fact-001"],
                "forbiddenAssertionKeys": [],
                "forbiddenTopicKeys": [],
            },
        })

    def test_request_rejects_unknown_contract_or_prompt_identity(self):
        valid = self.request().model_dump(by_alias=True)
        invalid_values = {
            "schemaVersion": main.NEURAL_SCHEMA_VERSION + 1,
            "promptStableKey": "unknown-prompt",
            "promptVersion": main.NPC_SYSTEM_PROMPT_VERSION + 1,
            "evaluationProfile": "unknown-evaluation",
        }

        for field, value in invalid_values.items():
            with self.subTest(field=field):
                request = dict(valid)
                request[field] = value
                with self.assertRaises(main.ValidationError):
                    main.Request.model_validate(request)

    def test_current_intent_accepts_semantic_goal_and_target_from_game_contract(self):
        request = self.request().model_dump(by_alias=True)
        request["currentIntent"] = {
            "capabilityId": "NCCognitionHoldPosition",
            "currentStep": 0,
            "stepCount": 1,
            "expiresAtTicks": 200,
            "intentKey": "hold-position",
            "targetHandle": "",
        }

        current = main.Request.model_validate(request).current_intent

        self.assertEqual(current.intent_key, "hold-position")
        self.assertEqual(current.target_handle, "")

    async def test_request_validation_handler_is_version_compatible_and_redacts_input(self):
        error = main.RequestValidationError([{
            "type": "extra_forbidden",
            "loc": ("body", "currentIntent", "unknown"),
            "msg": "Extra inputs are not permitted",
            "input": "private-value",
        }])

        response = await main.request_validation_error(None, error)
        body = response.body.decode("utf-8")

        self.assertEqual(response.status_code, 422)
        self.assertIn("extra_forbidden@body.currentIntent.unknown", body)
        self.assertNotIn("private-value", body)

    def test_sense_request_normalizes_only_non_targeted_handles(self):
        request = main.SenseRequest.model_validate({"kind": "check-body", "targetHandle": "self"})

        self.assertEqual(request.target_handle, "")
        with self.assertRaises(main.ValidationError):
            main.SenseRequest.model_validate({"kind": "inspect-subject", "targetHandle": ""})

    def test_perception_deltas_cannot_make_departed_handles_targetable(self):
        request = self.request().model_dump(by_alias=True)
        request.update({
            "perceivedSubjects": [{
                "handle": "person-here",
                "displayName": "observable person",
                "kind": "neutral",
                "confidence": 1,
                "expiresAtTicks": 200,
                "identityKnown": False,
            }],
            "perceptionDeltas": [{
                "kind": "person-left",
                "subjectHandle": "person-gone",
                "relatedHandle": "",
                "previousKind": "neutral",
                "currentKind": "absent",
                "subjectTargetable": False,
                "relatedTargetable": False,
                "observedAtTicks": 100,
                "expiresAtTicks": 200,
            }, {
                "kind": "person-entered",
                "subjectHandle": "person-here",
                "relatedHandle": "",
                "previousKind": "absent",
                "currentKind": "neutral",
                "subjectTargetable": True,
                "relatedTargetable": False,
                "observedAtTicks": 100,
                "expiresAtTicks": 200,
            }],
        })

        parsed = main.Request.model_validate(request)
        self.assertFalse(parsed.perception_deltas[0].subject_targetable)

        request["perceptionDeltas"][0]["subjectTargetable"] = True
        with self.assertRaises(main.ValidationError):
            main.Request.model_validate(request)

    def test_known_identity_is_allowed_only_for_person_subjects(self):
        request = self.request().model_dump(by_alias=True)
        request["perceivedSubjects"] = [{
            "handle": "known-person",
            "displayName": "Averill Shaw",
            "kind": "neutral",
            "confidence": 1,
            "expiresAtTicks": 200,
            "identityKnown": True,
        }]
        parsed = main.Request.model_validate(request)
        self.assertTrue(parsed.perceived_subjects[0].identity_known)

        request["perceivedSubjects"][0]["kind"] = "item"
        with self.assertRaises(main.ValidationError):
            main.Request.model_validate(request)

    def test_dialogue_request_accepts_persistent_memory_contract(self):
        request = self.request().model_dump(by_alias=True)
        request.update({
            "kind": "DialogueContinuation",
            "allowedCapabilityIds": ["NCCognitionHoldPosition"],
            "availableActions": [self.action("NCCognitionHoldPosition")],
            "dialogueInput": {
                "actorDisplayName": "Aoi Chen",
                "utterance": "Первый вариант: ты идёшь за мной.",
                "speakerHandle": "perceived-player",
                "addressScope": "group",
                "audienceSize": 5,
                "history": [{
                    "speaker": "interlocutor",
                    "speechAct": "statement",
                    "utterance": "Сыграем в игру.",
                    "atTicks": 0,
                }],
                "earlierSummary": {
                    "earlierTurnCount": 2,
                    "topicKeys": ["following-request"],
                    "semanticSummaryKeys": ["game-proposed"],
                    "persistentMemories": [{
                        "lastActivityAtTicks": 0,
                        "topicKeys": ["prior-conversation"],
                        "semanticSummaryKeys": ["player-known"],
                        "summary": "The player previously introduced himself.",
                    }],
                },
            },
            "perceivedSubjects": [{
                "handle": "perceived-player",
                "displayName": "Raymond Feuer",
                "kind": "neutral",
                "confidence": 1,
                "expiresAtTicks": 100,
            }],
        })

        parsed = main.Request.model_validate(request)

        self.assertEqual(parsed.dialogue_input.earlier_summary.persistent_memories[0].summary,
                         "The player previously introduced himself.")
        self.assertEqual(parsed.dialogue_input.address_scope, "group")
        self.assertEqual(parsed.dialogue_input.audience_size, 5)

    def test_incomplete_provider_response_reports_token_limit(self):
        response = SimpleNamespace(
            status="incomplete",
            incomplete_details=SimpleNamespace(reason="max_output_tokens"),
            error=None,
            output=[],
            output_text="",
        )

        self.assertEqual(main._provider_response_failure(response),
                         "provider-incomplete-max_output_tokens")

    def test_provider_refusal_is_not_parsed_as_json(self):
        response = SimpleNamespace(
            status="completed",
            incomplete_details=None,
            error=None,
            output=[SimpleNamespace(content=[SimpleNamespace(type="refusal")])],
            output_text="",
        )

        self.assertEqual(main._provider_response_failure(response), "provider-refusal")

    async def test_allowlisted_structured_answer_is_returned_in_game_contract(self):
        old_factory = main.AsyncOpenAI
        main.AsyncOpenAI = _FakeClientFactory("NCCognitionStrategicFallback")
        try:
            result = await main.cognition(self.request(), authorization="Bearer test-key")
        finally:
            main.AsyncOpenAI = old_factory

        self.assertEqual(result["commands"][0]["capabilityId"], "NCCognitionStrategicFallback")
        self.assertEqual(result["schemaVersion"], main.NEURAL_SCHEMA_VERSION)
        self.assertEqual(result["promptStableKey"], main.NPC_SYSTEM_PROMPT_STABLE_KEY)
        self.assertEqual(result["promptVersion"], main.NPC_SYSTEM_PROMPT_VERSION)
        self.assertEqual(result["evaluationProfile"], main.NPC_EVALUATION_PROFILE)
        self.assertEqual(result["decisionMode"], "act")
        self.assertEqual(result["intent"]["key"], "safe-option")
        self.assertEqual(result["speechActKey"], "statement")
        self.assertTrue(result["shouldSpeak"])
        self.assertEqual(result["speechActs"][0]["actKey"], "statement")
        self.assertEqual(result["wake"]["afterSeconds"], 60)
        self.assertEqual(result["reasonTags"], ["safe-option"])
        self.assertEqual(result["inputTokens"], 40)
        self.assertEqual(result["providerRequestId"], "fake-provider-request")

    def test_active_perception_results_are_typed_and_bounded_to_current_handles(self):
        request = self.request().model_dump(by_alias=True)
        request.update({
            "senseDepth": 1,
            "perceivedSubjects": [{
                "handle": "visible-item",
                "displayName": "gaechniy klyuch",
                "kind": "item",
                "confidence": 1,
                "expiresAtTicks": 100,
            }],
            "senseResults": [{
                "kind": "inspect-object",
                "targetHandle": "visible-item",
                "status": "completed",
                "observedHandles": ["visible-item"],
                "body": None,
            }, {
                "kind": "check-body",
                "targetHandle": "",
                "status": "completed",
                "observedHandles": [],
                "body": {
                    "lifeState": "alive",
                    "hungerState": "fed",
                    "thirstState": "hydrated",
                    "mobilityState": "mobile",
                    "painState": "none",
                    "bleeding": False,
                },
            }],
        })

        parsed = main.Request.model_validate(request)

        self.assertEqual(parsed.sense_results[0].observed_handles, ["visible-item"])
        self.assertEqual(parsed.sense_results[1].body.life_state, "alive")

    def test_active_queries_stay_inside_request_scoped_knowledge(self):
        request = self.request().model_dump(by_alias=True)
        request.update({
            "senseDepth": 1,
            "facts": [{
                "handle": "fact-001",
                "domain": "memory",
                "key": "helped-before",
                "subject": "person-1",
                "confidence": 1,
                "privacy": "Personal",
            }],
            "disclosure": {
                "maximumPrivacy": "Personal",
                "knownFactHandles": ["fact-001"],
                "allowedFactHandles": ["fact-001"],
                "forbiddenAssertionKeys": [],
                "forbiddenTopicKeys": [],
            },
            "perceivedSubjects": [{
                "handle": "person-1",
                "displayName": "observable person",
                "kind": "neutral",
                "confidence": 1,
                "expiresAtTicks": 100,
            }, {
                "handle": "item-1",
                "displayName": "crowbar",
                "kind": "item",
                "confidence": 1,
                "expiresAtTicks": 100,
            }],
            "knownLocations": [{
                "handle": "station-destination:work",
                "displayName": "Cargo workplace",
                "kind": "assigned-workplace",
            }],
            "worldOpportunities": [{
                "opportunityId": "89a12444-8108-42c7-9c64-c2d99f5989dc",
                "kind": "nearby-actionable-affordance",
                "targetKind": "subject",
                "targetHandle": "item-1",
                "affordanceKeys": ["object.tool"],
                "requiredCapabilityIds": [],
                "reward": 350,
                "urgency": 200,
                "risk": 100,
                "createdAtTicks": 1,
                "expiresAtTicks": 100,
                "sourceKey": "affordance",
                "correlationId": "a7bcaf33-2194-4a02-8d26-a06a7d2132f0",
                "available": True,
                "unavailableReason": "",
            }],
            "senseResults": [{
                "kind": "query-nearby-affordances",
                "targetHandle": "",
                "status": "completed",
                "observedHandles": [],
                "body": None,
                "factHandles": [],
                "locationHandles": [],
                "opportunityTargetHandles": ["item-1"],
                "detailKeys": [],
            }, {
                "kind": "query-known-locations",
                "targetHandle": "",
                "status": "completed",
                "observedHandles": [],
                "body": None,
                "factHandles": [],
                "locationHandles": ["station-destination:work"],
                "opportunityTargetHandles": [],
                "detailKeys": [],
            }],
        })

        discovery = main.Request.model_validate(request)
        self.assertEqual(discovery.sense_results[0].opportunity_target_handles, ["item-1"])
        self.assertEqual(discovery.sense_results[1].location_handles, ["station-destination:work"])

        request["senseResults"] = [{
            "kind": "recall-memories",
            "targetHandle": "person-1",
            "status": "completed",
            "observedHandles": [],
            "body": None,
            "factHandles": ["fact-001"],
            "locationHandles": [],
            "opportunityTargetHandles": [],
            "detailKeys": [],
        }, {
            "kind": "examine-route",
            "targetHandle": "station-destination:work",
            "status": "completed",
            "observedHandles": [],
            "body": None,
            "factHandles": [],
            "locationHandles": ["station-destination:work"],
            "opportunityTargetHandles": [],
            "detailKeys": ["route.resolvable"],
        }]
        retrieval = main.Request.model_validate(request)

        self.assertEqual(retrieval.sense_results[0].fact_handles, ["fact-001"])
        self.assertEqual(retrieval.sense_results[1].detail_keys, ["route.resolvable"])

        request["senseResults"][1].update({
            "status": "unavailable",
            "locationHandles": [],
            "detailKeys": ["route.unavailable"],
        })
        unavailable = main.Request.model_validate(request)
        self.assertEqual(unavailable.sense_results[1].location_handles, [])

        request["senseResults"][1]["locationHandles"] = ["station-destination:work"]
        with self.assertRaises(ValueError):
            main.Request.model_validate(request)

        request["senseResults"][1].update({
            "status": "completed",
            "locationHandles": ["station-destination:work"],
            "detailKeys": ["route.resolvable"],
        })
        request["senseResults"][1]["status"] = "not-visible"
        with self.assertRaises(ValueError):
            main.Request.model_validate(request)

        request["senseResults"] = [{
            "kind": "check-body",
            "targetHandle": "",
            "status": "not-visible",
            "observedHandles": [],
            "body": None,
            "factHandles": [],
            "locationHandles": [],
            "opportunityTargetHandles": [],
            "detailKeys": [],
        }]
        with self.assertRaises(ValueError):
            main.Request.model_validate(request)

    def test_command_result_keeps_request_and_original_command_index(self):
        request = self.request().model_dump(by_alias=True)
        request["recentCommandResults"] = [{
            "requestId": "cb121756-b17f-4d5f-a77f-93a8f7c3493c",
            "capabilityId": "NCCognitionHoldPosition",
            "actionId": "human-hold-position",
            "state": "completed",
            "failureKind": "none",
            "failureReason": "",
            "updatedAtTicks": 100,
            "commandIndex": 3,
            "goalKey": "execute",
            "expectedOutcomeKey": "position-held",
        }]

        parsed = main.Request.model_validate(request)
        result = parsed.recent_command_results[0]

        self.assertEqual(result.request_id, "cb121756-b17f-4d5f-a77f-93a8f7c3493c")
        self.assertEqual(result.command_index, 3)
        self.assertEqual(result.capability_id, "NCCognitionHoldPosition")
        self.assertEqual(result.action_id, "human-hold-position")

        request["recentCommandResults"][0].update({
            "state": "skipped",
            "failureKind": "dependency",
            "failureReason": "batch-predecessor-failed",
        })
        self.assertEqual(main.Request.model_validate(request).recent_command_results[0].state, "skipped")

    def test_active_perception_result_cannot_expose_an_unperceived_handle(self):
        request = self.request().model_dump(by_alias=True)
        request.update({
            "senseDepth": 1,
            "senseResults": [{
                "kind": "look-around",
                "targetHandle": "",
                "status": "completed",
                "observedHandles": ["hidden-item"],
                "body": None,
            }],
        })

        with self.assertRaises(main.ValidationError):
            main.Request.model_validate(request)

    async def test_non_allowlisted_capability_replans_without_failing_transport(self):
        old_factory = main.AsyncOpenAI
        main.AsyncOpenAI = _FakeClientFactory("NCCognitionRespondToThreat")
        try:
            result = await main.cognition(self.request(), authorization="Bearer test-key")
        finally:
            main.AsyncOpenAI = old_factory

        self.assertEqual(result["decisionMode"], "wait")
        self.assertEqual(result["commands"], [])
        self.assertEqual(result["reasonTags"], ["command-batch-rejected"])
        self.assertEqual(result["wake"]["afterSeconds"], 1)

    async def test_departed_delta_target_is_discarded_without_failing_transport(self):
        request = self.request().model_dump(by_alias=True)
        request["perceptionDeltas"] = [{
            "kind": "person-left",
            "subjectHandle": "person-gone",
            "relatedHandle": "",
            "previousKind": "neutral",
            "currentKind": "absent",
            "subjectTargetable": False,
            "relatedTargetable": False,
            "observedAtTicks": 100,
            "expiresAtTicks": 200,
        }]
        parsed = main.Request.model_validate(request)
        old_factory = main.AsyncOpenAI
        main.AsyncOpenAI = _FakeClientFactory("NCCognitionStrategicFallback", "person-gone")
        try:
            result = await main.cognition(parsed, authorization="Bearer test-key")
        finally:
            main.AsyncOpenAI = old_factory

        self.assertEqual(result["decisionMode"], "wait")
        self.assertEqual(result["commands"], [])
        self.assertEqual(result["reasonTags"], ["command-batch-rejected"])

    async def test_low_self_reported_confidence_does_not_disable_a_valid_action(self):
        old_factory = main.AsyncOpenAI
        main.AsyncOpenAI = _FakeClientFactory("NCCognitionStrategicFallback", confidence=1)
        try:
            result = await main.cognition(self.request(), authorization="Bearer test-key")
        finally:
            main.AsyncOpenAI = old_factory

        self.assertEqual(result["confidence"], 1)
        self.assertEqual(result["commands"][0]["capabilityId"], "NCCognitionStrategicFallback")

    async def test_dialogue_action_may_target_another_perceived_person(self):
        request = self.request().model_dump(by_alias=True)
        request.update({
            "kind": "DialogueContinuation",
            "allowedCapabilityIds": ["NCCognitionApproachPerson"],
            "availableActions": [self.action("NCCognitionApproachPerson", "approachtarget", "person")],
            "dialogueInput": {
                "actorDisplayName": "James Magor",
                "utterance": "Подойди к Авериллу.",
                "speakerHandle": "perceived-player",
                "addressScope": "direct",
                "audienceSize": 1,
                "history": [],
            },
            "perceivedSubjects": [{
                "handle": "perceived-player",
                "displayName": "Urist McHands",
                "kind": "neutral",
                "confidence": 1,
                "expiresAtTicks": 100,
            }, {
                "handle": "perceived-body",
                "displayName": "Averill Shaw",
                "kind": "dead",
                "confidence": 1,
                "expiresAtTicks": 100,
            }],
        })
        old_factory = main.AsyncOpenAI
        main.AsyncOpenAI = _FakeClientFactory("NCCognitionApproachPerson", "perceived-body")
        try:
            result = await main.cognition(main.Request.model_validate(request), authorization="Bearer test-key")
        finally:
            main.AsyncOpenAI = old_factory

        self.assertEqual(result["commands"][0]["targetHandle"], "perceived-body")

    async def test_action_may_target_a_server_supplied_known_location(self):
        request = self.request().model_dump(by_alias=True)
        request.update({
            "allowedCapabilityIds": ["NCCognitionPatrolKnownZone"],
            "availableActions": [self.action("NCCognitionPatrolKnownZone", "patrolzone", "zone")],
            "knownLocations": [{
                "handle": "zone:59e721a026ef4976a735ca683c28584b",
                "displayName": "Watson",
                "kind": "district",
            }],
        })
        target = "zone:59e721a026ef4976a735ca683c28584b"
        old_factory = main.AsyncOpenAI
        main.AsyncOpenAI = _FakeClientFactory("NCCognitionPatrolKnownZone", target)
        try:
            result = await main.cognition(main.Request.model_validate(request), authorization="Bearer test-key")
        finally:
            main.AsyncOpenAI = old_factory

        self.assertEqual(result["commands"][0]["targetHandle"], target)

    async def test_station_move_action_may_target_a_server_supplied_known_location(self):
        request = self.request().model_dump(by_alias=True)
        request.update({
            "allowedCapabilityIds": ["NCCognitionMoveToStationDestination"],
            "availableActions": [self.action(
                "NCCognitionMoveToStationDestination",
                "movetostationdestination",
                "domainrecord")],
            "knownLocations": [{
                "handle": "station-destination:59e721a026ef4976a735ca683c28584b",
                "displayName": "Engineering workplace",
                "kind": "workplace",
            }],
        })
        target = "station-destination:59e721a026ef4976a735ca683c28584b"
        old_factory = main.AsyncOpenAI
        main.AsyncOpenAI = _FakeClientFactory("NCCognitionMoveToStationDestination", target)
        try:
            result = await main.cognition(main.Request.model_validate(request), authorization="Bearer test-key")
        finally:
            main.AsyncOpenAI = old_factory

        self.assertEqual(result["commands"][0]["targetHandle"], target)

    async def test_unknown_optional_intent_target_does_not_discard_valid_physical_command(self):
        request = self.request().model_dump(by_alias=True)
        request.update({
            "allowedCapabilityIds": ["NCCognitionMoveToStationDestination"],
            "availableActions": [self.action(
                "NCCognitionMoveToStationDestination",
                "movetostationdestination",
                "domainrecord")],
            "knownLocations": [{
                "handle": "station-destination:59e721a026ef4976a735ca683c28584b",
                "displayName": "Engineering workplace",
                "kind": "workplace",
            }],
        })
        target = "station-destination:59e721a026ef4976a735ca683c28584b"
        old_factory = main.AsyncOpenAI
        main.AsyncOpenAI = _FakeClientFactory(
            "NCCognitionMoveToStationDestination",
            target,
            intent_target_handle="stale-or-invented-target")
        try:
            result = await main.cognition(main.Request.model_validate(request), authorization="Bearer test-key")
        finally:
            main.AsyncOpenAI = old_factory

        self.assertEqual(result["commands"][0]["targetHandle"], target)
        self.assertIsNone(result["intent"])
        self.assertIn("intent-target-discarded", result["reasonTags"])

    async def test_group_listener_may_choose_silence(self):
        request = self.request().model_dump(by_alias=True)
        request.update({
            "kind": "DialogueContinuation",
            "allowedCapabilityIds": ["NCCognitionHoldPosition"],
            "availableActions": [self.action("NCCognitionHoldPosition")],
            "dialogueInput": {
                "actorDisplayName": "Aoi Chen",
                "utterance": "Эй, народ.",
                "speakerHandle": "perceived-player",
                "addressScope": "group",
                "audienceSize": 6,
                "history": [],
            },
            "perceivedSubjects": [{
                "handle": "perceived-player",
                "displayName": "Raymond Feuer",
                "kind": "neutral",
                "confidence": 1,
                "expiresAtTicks": 100,
            }],
        })
        old_factory = main.AsyncOpenAI
        main.AsyncOpenAI = _FakeClientFactory("NCCognitionHoldPosition", should_speak=False)
        try:
            result = await main.cognition(main.Request.model_validate(request), authorization="Bearer test-key")
        finally:
            main.AsyncOpenAI = old_factory

        self.assertFalse(result["shouldSpeak"])
        self.assertEqual(result["decisionMode"], "wait")
        self.assertEqual(result["commands"], [])
        self.assertEqual(result["speechActs"], [])

    async def test_missing_bearer_credential_is_rejected(self):
        with self.assertRaises(HTTPException) as raised:
            await main.cognition(self.request(), authorization=None)

        self.assertEqual(raised.exception.status_code, 401)


if __name__ == "__main__":
    unittest.main()
