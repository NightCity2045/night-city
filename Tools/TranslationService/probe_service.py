from __future__ import annotations

import json
import os
import statistics
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass


@dataclass(frozen=True)
class ProbeCase:
    text: str
    source_language: str
    target_language: str


DEFAULT_CASES = [
    ProbeCase("Привет, чумба", "RU", "EN"),
    ProbeCase("Нужен риппердок возле рынка", "RU", "EN"),
    ProbeCase("Нетраннер в локальной сети", "RU", "EN"),
    ProbeCase("Фиксер ждёт у бара", "RU", "EN"),
    ProbeCase("МаксТак замечен в боевой зоне", "RU", "EN"),
    ProbeCase("Травма Тим летит к месту перестрелки", "RU", "EN"),
    ProbeCase("Добро пожаловать в Найт-Сити, чумба. Готов заработать немного эдди?", "RU", "EN"),

    ProbeCase("Hey, choom", "EN", "RU"),
    ProbeCase("I need a ripperdoc near the market", "EN", "RU"),
    ProbeCase("Netrunner in the local NET", "EN", "RU"),
    ProbeCase("The fixer is waiting at the bar", "EN", "RU"),
    ProbeCase("MaxTac spotted in the Combat Zone", "EN", "RU"),
    ProbeCase("Trauma Team is inbound to the shootout", "EN", "RU"),
    ProbeCase("Welcome to Night City, choom. Ready to earn some eddies?", "EN", "RU"),
]


def send_request(base_url: str, api_key: str | None, case: ProbeCase) -> tuple[str, float]:
    payload = json.dumps(
        {
            "Text": case.text,
            "SourceLanguage": case.source_language,
            "TargetLanguages": [case.target_language],
            "Channel": "Local",
        }
    ).encode("utf-8")

    request = urllib.request.Request(
        f"{base_url.rstrip('/')}/translate",
        data=payload,
        method="POST",
        headers={"Content-Type": "application/json; charset=utf-8"},
    )

    if api_key:
        request.add_header("X-Api-Key", api_key)

    started = time.perf_counter()
    with urllib.request.urlopen(request, timeout=15) as response:
        body = json.loads(response.read().decode("utf-8"))
    elapsed_ms = (time.perf_counter() - started) * 1000.0

    translations = body.get("Translations") or body.get("translations") or {}
    translated = translations.get(case.target_language, "")
    return translated, elapsed_ms


def main() -> int:
    base_url = os.getenv("NC_TRANSLATION_SERVICE_URL", "http://127.0.0.1:8090")
    api_key = os.getenv("NC_TRANSLATION_API_KEY") or None

    results = []
    for case in DEFAULT_CASES:
        try:
            translated, elapsed_ms = send_request(base_url, api_key, case)
        except urllib.error.HTTPError as error:
            print(f"HTTP {error.code} for {case.source_language}->{case.target_language}: {case.text}", file=sys.stderr)
            print(error.read().decode("utf-8", errors="replace"), file=sys.stderr)
            return 1

        results.append(elapsed_ms)
        print(f"[{case.source_language}->{case.target_language}] {case.text}")
        print(f"  -> {translated}")
        print(f"  {elapsed_ms:.1f} ms")

    print()
    print(
        "Latency summary: "
        f"min={min(results):.1f} ms, avg={statistics.mean(results):.1f} ms, "
        f"p95={statistics.quantiles(results, n=20, method='inclusive')[18]:.1f} ms, max={max(results):.1f} ms"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())