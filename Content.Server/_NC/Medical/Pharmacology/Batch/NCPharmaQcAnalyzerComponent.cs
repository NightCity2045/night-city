// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology.Batch;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// A laboratory analyzer. Every run uses up one dose of the inserted pack as the sample, so checking a batch
/// is never free and a fully used pack cannot be analyzed.
/// </summary>
[RegisterComponent, Access(typeof(NCPharmaQcSystem))]
public sealed partial class NCPharmaQcAnalyzerComponent : Component
{
    [DataField]
    public string SampleSlot = "sample";

    /// <summary>Time one test takes. A run lasts as long as all selected tests together.</summary>
    [DataField]
    public TimeSpan TimePerTest = TimeSpan.FromSeconds(6);

    [ViewVariables]
    public NCPharmaQcReport? LastReport;

    [ViewVariables]
    public NCPharmaLabel? LastLabel;

    [ViewVariables]
    public NCPharmaQcState LastBatchState;
}

/// <summary>Present only while a run is in progress.</summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCPharmaQcSystem))]
public sealed partial class NCPharmaQcRunComponent : Component
{
    public Guid BatchId;
    public NCPharmaQcTests Tests;
    public TimeSpan StartedAt;
    public TimeSpan FinishAt;
    public string Operator = string.Empty;
}
