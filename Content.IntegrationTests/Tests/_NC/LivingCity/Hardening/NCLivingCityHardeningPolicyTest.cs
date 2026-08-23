// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server._NC.LivingCity.Ambient.Components;
using Content.Server._NC.LivingCity.Observability;
using Content.Server._NC.LivingCity.Simulation;
using Content.Shared._NC.LivingCity.Admin;
using Content.Shared._NC.LivingCity.Ambient;
using Content.Shared._NC.LivingCity.Observability;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._NC.LivingCity.Hardening;

[TestFixture]
[TestOf(typeof(NCAmbientCrowdProfilePrototype))]
[TestOf(typeof(NCLivingCityObservabilitySystem))]
public sealed class NCLivingCityHardeningPolicyTest : GameTest
{
    public override PoolSettings PoolSettings => new() { Connected = false };

    [Test]
    public void AmbientDisablementAndObservabilityPoliciesAreBoundedAndDataDriven()
    {
        var prototypes = Pair.Server.ResolveDependency<IPrototypeManager>();
        var ambient = prototypes.Index<NCAmbientCrowdPolicyPrototype>("NCDefaultAmbientCrowdPolicy");
        var observability = prototypes.Index<NCLivingCityObservabilityPolicyPrototype>(
            "NCDefaultLivingCityObservabilityPolicy");

        Assert.Multiple(() =>
        {
            Assert.That(ambient.Enabled, Is.False,
                "Persistent A-Life actors own visible population; the removed ambient silhouettes must stay off.");
            Assert.That(ambient.Profiles, Is.Empty);
            Assert.That(observability.FrameP95CriticalMilliseconds,
                Is.GreaterThan(observability.FrameP95WarningMilliseconds));
            Assert.That(observability.PersistenceP95CriticalMilliseconds,
                Is.GreaterThan(observability.PersistenceP95WarningMilliseconds));
            Assert.That(observability.MaximumRetainedAlerts, Is.InRange(1, 128));
        });
    }

    [Test]
    public void AmbientPhysicalIdentityCannotContainPersistentActorIdentity()
    {
        var fields = typeof(NCAmbientCrowdMemberComponent).GetFields();
        Assert.That(fields.Select(field => field.FieldType.FullName),
            Has.None.Contains("NCActorId"));
    }

    [Test]
    public void TelemetryPercentilesAndAlertThresholdsAreDeterministic()
    {
        var samples = new[] { 1f, 2f, 3f, 4f, 100f };
        var normal = NCLivingCityObservabilitySystem.ClassifyThreshold(10, 20, 40);
        var warning = NCLivingCityObservabilitySystem.ClassifyThreshold(25, 20, 40);
        var critical = NCLivingCityObservabilitySystem.ClassifyThreshold(40, 20, 40);

        Assert.Multiple(() =>
        {
            Assert.That(NCAILoadGovernorSystem.CalculatePercentile(samples, 0.50f), Is.EqualTo(3f));
            Assert.That(NCAILoadGovernorSystem.CalculatePercentile(samples, 0.95f), Is.EqualTo(100f));
            Assert.That(normal.Active, Is.False);
            Assert.That(warning.Severity, Is.EqualTo(NCLivingCityAlertSeverity.Warning));
            Assert.That(warning.Threshold, Is.EqualTo(20));
            Assert.That(critical.Severity, Is.EqualTo(NCLivingCityAlertSeverity.Critical));
            Assert.That(critical.Threshold, Is.EqualTo(40));
        });
    }

    [Test]
    public void F7RecoveryContractContainsOnlyTypedOperations()
    {
        Assert.That(Enum.GetValues<NCLivingCityAdminInfrastructureAction>(), Is.EquivalentTo(new[]
        {
            NCLivingCityAdminInfrastructureAction.RequeueOutboxMessage,
            NCLivingCityAdminInfrastructureAction.ReconcileEconomyPage,
            NCLivingCityAdminInfrastructureAction.RebuildDistrictSafety,
            NCLivingCityAdminInfrastructureAction.RecoverServiceDispatch,
            NCLivingCityAdminInfrastructureAction.ReloadProperty,
            NCLivingCityAdminInfrastructureAction.ResetNeuralCircuit,
        }));
    }
}
