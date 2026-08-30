// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Housing;
using Content.Shared._NC.LivingCity.Housing;

namespace Content.Server._NC.LivingCity.Housing;

public sealed record NCPropertyRuntimeMutationResult(
    NCPropertyMutationResult Result,
    NCPropertyAggregateRecord? Property,
    NCPropertyAccessGrantSnapshot? Grant,
    long? Balance);
