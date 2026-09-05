# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

"""Fast CI preflight for authored Living City content; it never changes repository files."""

from __future__ import annotations
import argparse
import pathlib
import re
import sys

TYPE = re.compile(r"^\s*-\s+type:\s*([A-Za-z0-9_-]+)\s*$")
ID = re.compile(r"^\s+id:\s*([^#\s]+)")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=pathlib.Path, default=pathlib.Path(__file__).parents[3])
    args = parser.parse_args()
    root = args.root.resolve()
    prototype_root = root / "Resources" / "Prototypes" / "_NC"
    errors: list[str] = []
    identities: dict[tuple[str, str], pathlib.Path] = {}
    for path in sorted(prototype_root.rglob("*.yml")):
        current_type: str | None = None
        for number, line in enumerate(path.read_text("utf-8").splitlines(), 1):
            if match := TYPE.match(line):
                current_type = match.group(1)
            elif current_type and (match := ID.match(line)):
                key = (current_type, match.group(1))
                if previous := identities.get(key):
                    errors.append(f"duplicate {key[0]}/{key[1]}: {previous} and {path}:{number}")
                identities[key] = path
                current_type = None
    required = ["LivingCity/cognition.yml", "LivingCity/economic_assets.yml", "LivingCity/market.yml",
                "LivingCity/fixers.yml", "LivingCity/city_services.yml", "LivingCity/neural_cognition.yml",
                "LivingCity/observability.yml"]
    for relative in required:
        if not (prototype_root / relative).is_file():
            errors.append(f"missing required prototype file: {relative}")
    for path in (root / "Content.Server" / "_NC" / "LivingCity").rglob("*.cs"):
        text = path.read_text("utf-8")
        # Admin/outbox timestamps are operational wall-clock metadata, not simulation deadlines.
        if "Admin" not in path.parts and ("DateTime.UtcNow" in text or "DateTime.Now" in text):
            errors.append(f"wall-clock gameplay dependency: {path.relative_to(root)}")
        if "GetAllComponents" in text and "Update(float" in text:
            errors.append(f"possible per-tick ECS scan: {path.relative_to(root)}")
    for error in errors:
        print(f"ERROR: {error}")
    print(f"audited {len(identities)} prototype identities; errors={len(errors)}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
