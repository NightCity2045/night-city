# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

import json
import os
import unittest
from types import SimpleNamespace
from unittest.mock import patch

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
        }
        return SimpleNamespace(
            output_text=json.dumps(answer),
            usage=SimpleNamespace(input_tokens=40, output_tokens=12),
            id="fake-provider-request",
        )


class _FakeClient:
    def __init__(self, selected_capability: str):
        self.responses = _FakeResponses(selected_capability)


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
                "domain": "core",
                "key": "actor.can-act",
                "subject": "actor:aa0d9698",
                "confidence": 1,
                "privacy": "Public",
            }],
            "allowedCapabilityIds": ["NCCognitionStrategicFallback"],
            "topicKeys": ["safe-option"],
            "locale": "ru-RU",
        })

    async def test_allowlisted_structured_answer_is_returned_in_game_contract(self):
        old_client = main.client
        main.client = _FakeClient("NCCognitionStrategicFallback")
        try:
            with patch.dict(os.environ, {"OPENAI_API_KEY": "test-key"}):
                result = await main.cognition(self.request())
        finally:
            main.client = old_client

        self.assertEqual(result["selectedCapabilityId"], "NCCognitionStrategicFallback")
        self.assertEqual(result["speechActKey"], "statement")
        self.assertEqual(result["inputTokens"], 40)
        self.assertEqual(result["providerRequestId"], "fake-provider-request")

    async def test_non_allowlisted_capability_is_rejected(self):
        old_client = main.client
        main.client = _FakeClient("NCCognitionRespondToThreat")
        try:
            with patch.dict(os.environ, {"OPENAI_API_KEY": "test-key"}):
                with self.assertRaises(HTTPException) as raised:
                    await main.cognition(self.request())
        finally:
            main.client = old_client

        self.assertEqual(raised.exception.status_code, 502)


if __name__ == "__main__":
    unittest.main()
