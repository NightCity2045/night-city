# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

import json
import unittest
from types import SimpleNamespace

from fastapi import HTTPException

from app import main


class _FakeResponses:
    def __init__(self, selected_capability: str):
        self._selected_capability = selected_capability

    async def create(self, **_kwargs):
        answer = {
            "selected_capability_id": self._selected_capability,
            "speech_act_key": "statement",
            "rendered_text": "Я выбираю безопасный вариант.",
            "summary_key": "safe-option",
            "confidence": 800,
            "intent": {
                "goalKey": "safe-option",
                "targetHandle": "",
                "requestedCapabilityIds": [self._selected_capability],
                "priority": 500,
                "expectedOutcomeKey": "safe-option-selected",
                "informationGapKeys": [],
                "arguments": [],
            },
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
    def __init__(self, selected_capability: str):
        self.responses = _FakeResponses(selected_capability)

    async def close(self):
        pass


class _FakeClientFactory:
    def __init__(self, selected_capability: str):
        self._selected_capability = selected_capability

    def __call__(self, *, api_key: str):
        if api_key != "test-key":
            raise AssertionError("unexpected provider credential")
        return _FakeClient(self._selected_capability)


class NeuralCognitionServiceTest(unittest.IsolatedAsyncioTestCase):
    def request(self) -> main.Request:
        return main.Request.model_validate({
            "requestId": "5912157d-3f44-4075-bf78-98df6cab44d4",
            "kind": "StrategicDecision",
            "priority": "P1",
            "actorId": {"value": "aa0d9698-39fd-4678-82a2-f736cf172995"},
            "sessionEpoch": {"value": "b13348b8-fd34-48c9-81a5-e2f4e81ed5a7"},
            "contextVersion": 7,
            "expiresAt": {"ticks": 100},
            "promptProfile": "NCLivingCityDefault",
            "facts": [{
                "handle": "fact-001",
                "domain": "core",
                "key": "actor.can-act",
                "subject": "actor:aa0d9698",
                "confidence": 1,
                "privacy": "Public",
            }],
            "allowedCapabilityIds": ["NCCognitionStrategicFallback"],
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

    def test_dialogue_request_accepts_persistent_memory_contract(self):
        request = self.request().model_dump(by_alias=True)
        request.update({
            "kind": "DialogueContinuation",
            "allowedCapabilityIds": ["social.dialogue.reply"],
            "dialogueInput": {
                "actorDisplayName": "Aoi Chen",
                "utterance": "Первый вариант: ты идёшь за мной.",
                "speakerHandle": "perceived-player",
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

        self.assertEqual(result["selectedCapabilityId"], "NCCognitionStrategicFallback")
        self.assertEqual(result["speechActKey"], "statement")
        self.assertEqual(result["inputTokens"], 40)
        self.assertEqual(result["providerRequestId"], "fake-provider-request")

    async def test_non_allowlisted_capability_is_rejected(self):
        old_factory = main.AsyncOpenAI
        main.AsyncOpenAI = _FakeClientFactory("NCCognitionRespondToThreat")
        try:
            with self.assertRaises(HTTPException) as raised:
                await main.cognition(self.request(), authorization="Bearer test-key")
        finally:
            main.AsyncOpenAI = old_factory

        self.assertEqual(raised.exception.status_code, 502)

    async def test_missing_bearer_credential_is_rejected(self):
        with self.assertRaises(HTTPException) as raised:
            await main.cognition(self.request(), authorization=None)

        self.assertEqual(raised.exception.status_code, 401)


if __name__ == "__main__":
    unittest.main()
