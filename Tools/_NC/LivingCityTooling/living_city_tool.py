# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

"""Read-only Living City content reports, fixtures, neural evals and deterministic scenario simulation."""

from __future__ import annotations

import argparse
import collections
import hashlib
import json
import pathlib
import random
import re
import sys
from dataclasses import dataclass
from typing import Any, Iterable

TYPE = re.compile(r"^\s*-\s+type:\s*([A-Za-z0-9_-]+)\s*$")
ID = re.compile(r"^\s+id:\s*([^#\s]+)")
SCALAR = re.compile(r"^\s+([A-Za-z][A-Za-z0-9]*):\s*([^#]+?)\s*$")
ZONE_ID = re.compile(r"^  - id:\s*([0-9a-fA-F-]{36})\s*$")
ZONE_FIELD = re.compile(r'^    (kind|name|parent):\s*"?([^"#]+?)"?\s*$')
STABLE = re.compile(r"^[A-Za-z0-9_.:-]+$")


@dataclass(frozen=True)
class PrototypeBlock:
    type_name: str
    prototype_id: str
    path: pathlib.Path
    fields: dict[str, str]


def scan_prototypes(root: pathlib.Path) -> list[PrototypeBlock]:
    blocks: list[PrototypeBlock] = []
    for path in sorted((root / "Resources/Prototypes/_NC").rglob("*.yml")):
        current_type: str | None = None
        current_id: str | None = None
        fields: dict[str, str] = {}
        for line in path.read_text("utf-8").splitlines() + ["- type: __end"]:
            if match := TYPE.match(line):
                if current_type and current_id:
                    blocks.append(PrototypeBlock(current_type, current_id, path, fields))
                current_type, current_id, fields = match.group(1), None, {}
                continue
            if not current_type:
                continue
            if current_id is None and (match := ID.match(line)):
                current_id = match.group(1).strip('"')
            elif match := SCALAR.match(line):
                fields.setdefault(match.group(1), match.group(2).strip().strip('"'))
    return blocks


def authored_zones(root: pathlib.Path) -> list[dict[str, str]]:
    path = root / "Resources/Prototypes/_NC/City/city-zones.yml"
    zones: list[dict[str, str]] = []
    current: dict[str, str] | None = None
    if not path.is_file():
        return zones
    for line in path.read_text("utf-8").splitlines():
        if match := ZONE_ID.match(line):
            current = {"id": match.group(1).lower()}
            zones.append(current)
        elif current is not None and (match := ZONE_FIELD.match(line)):
            current[match.group(1)] = match.group(2).strip()
    return zones


def content_report(root: pathlib.Path) -> dict[str, Any]:
    blocks = scan_prototypes(root)
    by_type = collections.Counter(block.type_name for block in blocks)
    prices = []
    for block in blocks:
        raw = block.fields.get("basePrice", block.fields.get("baseUnitPrice"))
        if raw is not None:
            try:
                prices.append({"type": block.type_name, "id": block.prototype_id, "price": int(raw)})
            except ValueError:
                pass
    zones = authored_zones(root)
    ids = {zone["id"] for zone in zones}
    missing_parents = sorted(zone["id"] for zone in zones if zone.get("parent", "").lower() not in ids
                             and zone.get("parent"))
    return {
        "prototypeCount": len(blocks),
        "prototypeTypes": dict(sorted(by_type.items())),
        "economy": {
            "pricedDefinitions": len(prices),
            "minimumPrice": min((item["price"] for item in prices), default=0),
            "maximumPrice": max((item["price"] for item in prices), default=0),
            "definitions": prices,
        },
        "population": {
            "archetypes": by_type.get("ncCityActorArchetype", 0),
            "profiles": by_type.get("ncCityPopulationProfile", 0),
            "routineProfiles": by_type.get("ncCityRoutineProfile", 0),
        },
        "geography": {
            "authoredZones": len(zones),
            "kinds": dict(sorted(collections.Counter(zone.get("kind", "unknown") for zone in zones).items())),
            "missingParents": missing_parents,
            "note": "Portal and room reachability is generated from loaded maps and must be validated in F7.",
        },
    }


def audit(root: pathlib.Path) -> list[str]:
    errors: list[str] = []
    seen: dict[tuple[str, str], pathlib.Path] = {}
    blocks = scan_prototypes(root)
    for block in blocks:
        key = (block.type_name, block.prototype_id)
        if key in seen:
            errors.append(f"duplicate {key[0]}/{key[1]}: {seen[key]} and {block.path}")
        seen[key] = block.path
    required = {
        "ncAILoadGovernorPolicy", "ncAmbientCrowdPolicy", "ncCityConnectivityRules",
        "ncCityRelevancePolicy", "ncEconomicAssetPolicy", "ncLivingCityObservabilityPolicy",
        "ncNeuralPolicy",
    }
    missing = sorted(required - {block.type_name for block in blocks})
    errors.extend(f"missing required prototype type: {value}" for value in missing)
    report = content_report(root)
    errors.extend(f"zone references missing parent: {value}" for value in report["geography"]["missingParents"])
    for path in (root / "Content.Server/_NC/LivingCity").rglob("*.cs"):
        text = path.read_text("utf-8")
        if "Admin" not in path.parts and ("DateTime.UtcNow" in text or "DateTime.Now" in text):
            errors.append(f"wall-clock gameplay dependency: {path.relative_to(root)}")
        if "GetAllComponents" in text and "Update(float" in text:
            errors.append(f"possible per-tick ECS scan: {path.relative_to(root)}")
    return errors


def transfer(accounts: dict[str, int], source: str, target: str, amount: int,
             counters: collections.Counter[str]) -> None:
    if amount <= 0 or accounts.get(source, 0) < amount:
        counters["rejectedTransfers"] += 1
        return
    before = sum(accounts.values())
    accounts[source] -= amount
    accounts[target] = accounts.get(target, 0) + amount
    if sum(accounts.values()) != before:
        raise RuntimeError("ledger money-supply invariant failed")
    counters["transactions"] += 1
    counters["transactionVolume"] += amount


def simulate(fixture: dict[str, Any]) -> dict[str, Any]:
    if fixture.get("schemaVersion") != 1:
        raise ValueError("fixture schemaVersion must be 1")
    step = int(fixture["stepMinutes"])
    duration = int(fixture["durationMinutes"])
    if step <= 0 or duration <= 0 or duration % step:
        raise ValueError("durationMinutes must be a positive multiple of stepMinutes")
    randomizer = random.Random(int(fixture["seed"]))
    accounts = {key: int(value) for key, value in fixture["accounts"].items()}
    initial_supply = sum(accounts.values())
    actors = sorted(fixture["actors"], key=lambda actor: actor["id"])
    counters: collections.Counter[str] = collections.Counter()
    locations = {actor["id"]: actor["homeZone"] for actor in actors}
    peak_materialized = 0
    peak_travel = 0
    incidents = fixture.get("incidents", [])
    focus = sorted(fixture.get("playerFocus", []), key=lambda value: value["atMinute"])
    focus_zone = focus[0]["zone"] if focus else ""
    focus_index = 0
    paid_shifts: set[tuple[str, int]] = set()
    paid_meals: set[tuple[str, int]] = set()

    for minute in range(0, duration, step):
        while focus_index + 1 < len(focus) and focus[focus_index + 1]["atMinute"] <= minute:
            focus_index += 1
            focus_zone = focus[focus_index]["zone"]
        travel_count = 0
        for actor in actors:
            day, local = divmod(minute, 1440)
            start, end = int(actor["workStartMinute"]), int(actor["workEndMinute"])
            travel = int(actor["travelMinutes"])
            if start - travel <= local < start or end <= local < end + travel:
                locations[actor["id"]] = "travel"
                travel_count += 1
            elif start <= local < end:
                locations[actor["id"]] = actor["workZone"]
            else:
                locations[actor["id"]] = actor["homeZone"]
            shift_key = (actor["id"], day)
            if local >= end and shift_key not in paid_shifts:
                transfer(accounts, actor["employerAccount"], actor["account"], int(actor["wage"]), counters)
                paid_shifts.add(shift_key)
            if local >= int(actor["mealMinute"]) and shift_key not in paid_meals:
                transfer(accounts, actor["account"], fixture["foodVendorAccount"], int(actor["mealCost"]), counters)
                paid_meals.add(shift_key)
        active_incidents = sum(1 for incident in incidents
                               if incident["atMinute"] <= minute < incident["atMinute"] + incident["durationMinutes"])
        # A seeded tie-breaker makes overload ordering deterministic without making outcomes wall-clock dependent.
        relevant = [actor["id"] for actor in actors if locations[actor["id"]] == focus_zone]
        randomizer.shuffle(relevant)
        materialized = min(int(fixture["maximumMaterialized"]), len(relevant) + active_incidents)
        peak_materialized = max(peak_materialized, materialized)
        peak_travel = max(peak_travel, travel_count)
        counters["incidentTicks"] += active_incidents

    result = {
        "schemaVersion": 1,
        "seed": fixture["seed"],
        "steps": duration // step,
        "actors": len(actors),
        "finalLocations": dict(sorted(locations.items())),
        "finalBalances": dict(sorted(accounts.items())),
        "initialMoneySupply": initial_supply,
        "finalMoneySupply": sum(accounts.values()),
        "transactions": counters["transactions"],
        "transactionVolume": counters["transactionVolume"],
        "rejectedTransfers": counters["rejectedTransfers"],
        "peakTravelQueue": peak_travel,
        "peakMaterialized": peak_materialized,
        "incidentTicks": counters["incidentTicks"],
    }
    encoded = json.dumps(result, sort_keys=True, separators=(",", ":")).encode()
    result["checksum"] = hashlib.sha256(encoded).hexdigest()
    return result


def neural_eval(path: pathlib.Path) -> tuple[int, list[str]]:
    failures: list[str] = []
    count = 0
    for number, line in enumerate(path.read_text("utf-8").splitlines(), 1):
        if not line.strip():
            continue
        count += 1
        case = json.loads(line)
        request, response = case["request"], case["response"]
        allowed = request.get("allowedCapabilityIds", [])
        facts = request.get("facts", [])
        accepted = (
            response.get("selectedCapabilityId") in allowed
            and response.get("contextVersion") == request.get("contextVersion")
            and 0 <= int(response.get("confidence", -1)) <= 1000
            and len(response.get("renderedText", "")) <= 512
            and all(STABLE.fullmatch(str(value)) for value in allowed)
            and all(0 <= float(fact.get("confidence", -1)) <= 1 for fact in facts)
        )
        if accepted != bool(case["expectedAccepted"]):
            failures.append(f"line {number} ({case.get('id', 'unnamed')}): expectedAccepted mismatch")
    return count, failures


def load_json(path: pathlib.Path) -> dict[str, Any]:
    value = json.loads(path.read_text("utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return value


def emit(value: Any, output: pathlib.Path | None) -> None:
    rendered = json.dumps(value, indent=2, sort_keys=True) + "\n"
    if output:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(rendered, "utf-8")
    else:
        print(rendered, end="")


def main() -> int:
    default_root = pathlib.Path(__file__).parents[3]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=pathlib.Path, default=default_root)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("audit")
    report_parser = commands.add_parser("report")
    report_parser.add_argument("--output", type=pathlib.Path)
    simulation = commands.add_parser("simulate")
    simulation.add_argument("--fixture", type=pathlib.Path,
                            default=pathlib.Path(__file__).with_name("fixtures") / "baseline_round.json")
    simulation.add_argument("--output", type=pathlib.Path)
    neural = commands.add_parser("neural-eval")
    neural.add_argument("--dataset", type=pathlib.Path,
                        default=pathlib.Path(__file__).with_name("neural_eval") / "baseline.jsonl")
    args = parser.parse_args()
    root = args.root.resolve()
    if args.command == "audit":
        errors = audit(root)
        for error in errors:
            print(f"ERROR: {error}")
        print(f"Living City audit errors={len(errors)}")
        return 1 if errors else 0
    if args.command == "report":
        emit(content_report(root), args.output)
        return 0
    if args.command == "simulate":
        emit(simulate(load_json(args.fixture)), args.output)
        return 0
    count, failures = neural_eval(args.dataset)
    for failure in failures:
        print(f"ERROR: {failure}")
    print(f"neural eval cases={count}; failures={len(failures)}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
