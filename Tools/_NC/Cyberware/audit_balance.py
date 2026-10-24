# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
"""Export prototype-derived resource bounds. This does not simulate gameplay or measure server performance."""

import argparse
import csv
import hashlib
import json
from pathlib import Path

import yaml


class ContentLoader(yaml.SafeLoader):
    """Read SS14 declarative type tags as inert mappings; never import or construct Python objects."""


def read_type(loader, _name, node):
    if isinstance(node, yaml.MappingNode):
        return loader.construct_mapping(node, deep=True)
    if isinstance(node, yaml.SequenceNode):
        return loader.construct_sequence(node, deep=True)
    return loader.construct_scalar(node)


ContentLoader.add_multi_constructor("!type:", read_type)


def audit(root: Path):
    sources = {}
    profiles = {}
    abilities = {}
    items = []
    folder = root / "Resources/Prototypes/_NC/Medical/Cyberware"
    for path in sorted(folder.rglob("*.yml")):
        raw = path.read_bytes()
        sources[path.relative_to(root).as_posix()] = hashlib.sha256(raw).hexdigest()
        for definition in yaml.load(raw, Loader=ContentLoader) or []:
            kind = definition.get("type")
            if kind == "ncCyberware":
                profiles[definition["id"]] = definition
            elif kind == "ncCyberwareAbility":
                abilities[definition["id"]] = definition
            elif kind == "entity":
                items.append(definition)

    rows = []
    for name, profile in sorted(profiles.items()):
        powered = profile.get("requiresPower", True)
        idle = float(profile.get("idlePowerPerSecond", 0)) if powered else 0.0
        peak = idle + float(profile.get("loadedPowerPerSecond", 0)) if powered else 0.0
        passive = float(profile.get("passiveCoolingPerSecond", 0))
        idle_heat = float(profile.get("idleHeatPerSecond", 0)) - passive
        loaded_heat = idle_heat + float(profile.get("loadedHeatPerSecond", 0))
        rows.append(dict(profile=name, idle_watts=idle, peak_watts=peak,
                         idle_net_heat_per_second=idle_heat, peak_net_heat_per_second=loaded_heat,
                         seconds_to_shutdown_from_cold=(float(profile["heatShutdownThreshold"]) / loaded_heat
                                                        if loaded_heat > 0 else None),
                         power_priority=profile.get("powerPriority", "Professional")))

    bursts = []
    for item in items:
        components = {value["type"]: value for value in item.get("components", [])}
        hardware = components.get("NCCyberware", {}).get("profile")
        ability_id = components.get("NCCyberwareAbility", {}).get("profile")
        if hardware not in profiles or ability_id not in abilities:
            continue
        ability = abilities[ability_id]
        if ability.get("kind") != "MotorBurst":
            continue
        profile = profiles[hardware]
        duration = float(ability.get("durationSeconds", 6))
        recovery = float(ability.get("cooldownSeconds", 30))
        cycle = duration + recovery
        idle = float(profile.get("idlePowerPerSecond", 0))
        loaded = float(profile.get("loadedPowerPerSecond", 0))
        start = float(ability.get("startupEnergy", 0))
        bursts.append(dict(item=item["id"], profile=hardware, ability=ability_id,
                           active_seconds=duration, recovery_seconds=recovery,
                           burst_joules=start + (idle + loaded) * duration,
                           full_cycle_joules=start + idle * cycle + loaded * duration,
                           cycle_average_watts=(start + idle * cycle + loaded * duration) / cycle,
                           active_net_heat=(float(profile.get("idleHeatPerSecond", 0)) +
                                            float(profile.get("loadedHeatPerSecond", 0)) -
                                            float(profile.get("passiveCoolingPerSecond", 0))) * duration))

    if not rows or not bursts:
        raise ValueError("Expected cyberware profiles and at least one physical motor-burst item")
    return dict(schema=1, evidence="prototype-calculation-only", sources=sources,
                assumptions=["Unmodified intact hardware, no EMP, medicine or combat; full load held constant for peak bounds.",
                             "Cold shutdown time ignores derating, load cancellation and physiological failure.",
                             "Burst calculations include startup and idle energy but exclude other body consumers.",
                             "These values are not observed runtime duration, CPU cost or combat results."],
                profiles=rows, motor_bursts=bursts)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[3]
    result = audit(root)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "prototype-balance.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    with (args.output / "prototype-balance.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(result["profiles"][0]))
        writer.writeheader()
        writer.writerows(result["profiles"])
    print(f"Calculated {len(result['profiles'])} profiles and {len(result['motor_bursts'])} motor burst(s); no runtime measurement.")
    for burst in result["motor_bursts"]:
        print(f"{burst['profile']}: {burst['burst_joules']:.2f} J per burst, "
              f"{burst['full_cycle_joules']:.2f} J per cycle, {burst['cycle_average_watts']:.3f} W cycle average")


if __name__ == "__main__":
    main()
