// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed class DiagnosticOperationFailedException(DiagnosticFault fault, JsonNode? result, string state = "failed") : Exception(fault.Message)
{
    public DiagnosticFault Fault { get; } = fault;
    public JsonNode? Result { get; } = result;
    public string State { get; } = state;
}
