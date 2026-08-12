// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Persistence;
using Content.Server._NC.Medical.Pharmacology;
using Content.Server._NC.Medical.Transplantation;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Medical.Transplantation;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.Medical;

[TestFixture]
[TestOf(typeof(NCTransplantRejectionSystem))]
public sealed class NCTransplantRejectionSystemTest : GameTest
{
    private static readonly EntProtoId HumanMob = "MobHuman";

    [SidedDependency(Side.Server)] private NCAnatomySystem _anatomy = default!;
    [SidedDependency(Side.Server)] private NCOrganDetachmentSystem _detachment = default!;
    [SidedDependency(Side.Server)] private NCMedicalPersistenceSystem _persistence = default!;
    [SidedDependency(Side.Server)] private NCPharmacologySystem _pharmacology = default!;
    [SidedDependency(Side.Server)] private NCTransplantRejectionSystem _rejection = default!;

    public override PoolSettings PoolSettings => new()
    {
        Connected = false,
    };

    [Test]
    [RunOnSide(Side.Server)]
    public void AllograftRejectsDeterministicallyImmunosuppressionControlsAndStatePersists()
    {
        var donor = SSpawn(HumanMob);
        var recipient = SSpawn(HumanMob);
        // Player-character owners persist the numeric profile ID, not an arbitrary runtime UUID.
        const string donorKey = "81001";
        const string recipientKey = "81002";
        BindOwner(donor, donorKey);
        BindOwner(recipient, recipientKey);

        Assert.That(_detachment.TryDetachOrgan(recipient, "Heart", out _), Is.True);
        Assert.That(_detachment.TryDetachOrgan(donor, "Heart", out var graft), Is.True);
        Assert.That(_detachment.TryAttachOrgan(recipient, "Thorax", graft), Is.True);
        Assert.That(SEntMan.TryGetComponent<NCTransplantRejectionComponent>(graft, out var state), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(state!.DonorKey, Is.EqualTo(donorKey));
            Assert.That(state.RecipientKey, Is.EqualTo(recipientKey));
            Assert.That(state.Burden, Is.Zero);
        });

        Assert.That(_rejection.AdvanceBody(recipient, TimeSpan.FromHours(2)), Is.True);
        var untreatedBurden = state!.Burden;
        Assert.Multiple(() =>
        {
            Assert.That(untreatedBurden, Is.GreaterThan(0f));
            Assert.That(state.Stage, Is.GreaterThanOrEqualTo(NCTransplantRejectionStage.Active));
        });

        Assert.That(_pharmacology.ApplyDose(recipient, "NCDrugImmunosuppressant", 5f).Accepted, Is.True);
        Assert.That(_rejection.AdvanceBody(recipient, TimeSpan.FromMinutes(10)), Is.True);
        Assert.That(state.Burden, Is.LessThan(untreatedBurden));

        var snapshot = _persistence.SerializeMedicalState(recipient);
        Assert.That(snapshot, Is.Not.Null);
        var restored = SSpawn(HumanMob);
        BindOwner(restored, recipientKey);
        Assert.That(_persistence.RestoreMedicalState(restored, snapshot!), Is.True);
        Assert.That(_anatomy.TryGetOrgan(restored, "Heart", out var restoredHeart), Is.True);
        var restoredState = SComp<NCTransplantRejectionComponent>(restoredHeart);
        Assert.Multiple(() =>
        {
            Assert.That(restoredState.DonorKey, Is.EqualTo(donorKey));
            Assert.That(restoredState.RecipientKey, Is.EqualTo(recipientKey));
            Assert.That(restoredState.Burden, Is.EqualTo(state.Burden));
            Assert.That(restoredState.Stage, Is.EqualTo(state.Stage));
        });
    }

    [Test]
    [RunOnSide(Side.Server)]
    public void OwnReattachedOrganNeverCreatesRejectionState()
    {
        var body = SSpawn(HumanMob);
        BindOwner(body, "81003");
        Assert.That(_detachment.TryDetachOrgan(body, "Heart", out var heart), Is.True);
        Assert.That(_detachment.TryAttachOrgan(body, "Thorax", heart), Is.True);
        Assert.That(SEntMan.HasComponent<NCTransplantRejectionComponent>(heart), Is.False);
    }

    private void BindOwner(EntityUid body, string key)
    {
        var owner = SEntMan.EnsureComponent<NCMedicalPersistenceOwnerComponent>(body);
        owner.Kind = NCMedicalStableOwnerKind.PlayerCharacter;
        owner.Key = key;
    }
}
