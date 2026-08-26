<!-- SPDX-License-Identifier: LicenseRef-OpenSpace-AgentPrompts-Restricted -->

# Path Similarity

## Rule

When creating fork/module-specific files, mirror the existing feature path as closely as practical.

## Example

- upstream-like behavior under `Content.Shared/<Feature>/...`
- Night City extension under `Content.Shared/_NC/<Feature>/...` when the behavior is original to this project

## Why

- keeps drift discoverable
- makes upstream rebases easier
- helps humans and agents find the fork/module delta quickly
