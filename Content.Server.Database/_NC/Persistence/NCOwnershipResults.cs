// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using System;

namespace Content.Server.Database;

public sealed record NCOwnershipResult(
    bool Success,
    string? Error,
    Guid? EntityId);
