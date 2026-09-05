// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Persistence;
using NUnit.Framework;

namespace Content.Tests.Server._NC.Medical;

[TestFixture]
public sealed class NCMedicalPersistenceLifecycleTest
{
    [Test]
    public void TerminatingActorCaptureNeverAddsRuntimeOwnerComponent()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NCMedicalPersistenceSystem.CanMutateActorCaptureIdentity(false, false), Is.True);
            Assert.That(NCMedicalPersistenceSystem.CanMutateActorCaptureIdentity(true, false), Is.False);
            Assert.That(NCMedicalPersistenceSystem.CanMutateActorCaptureIdentity(false, true), Is.False);
            Assert.That(NCMedicalPersistenceSystem.CanMutateActorCaptureIdentity(true, true), Is.False);
        });
    }
}
