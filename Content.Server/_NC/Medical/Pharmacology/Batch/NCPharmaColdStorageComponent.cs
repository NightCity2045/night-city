// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Marks a storage that keeps what is inside cold, so unstable batches stop degrading there. A storage with a
/// power receiver only counts while it is powered: a dead refrigerator is only a box.
/// </summary>
[RegisterComponent]
public sealed partial class NCPharmaColdStorageComponent : Component;
