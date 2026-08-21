// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.LivingCity.Economy;

public sealed record NCEconomicReconciliationCursor(
    string? StockNodeAfter,
    string? CommodityBatchAfter,
    string? ItemAfter);

public sealed record NCEconomicReconciliationIssue(
    string AggregateType,
    string AggregateId,
    string Code,
    string RelatedId);

public sealed record NCEconomicReconciliationReport(
    int StockNodesScanned,
    int CommodityBatchesScanned,
    int ItemsScanned,
    int ReferenceChecks,
    bool ReferenceChecksTruncated,
    IReadOnlyList<NCEconomicReconciliationIssue> Issues,
    NCEconomicReconciliationCursor Continuation,
    bool HasMore);
