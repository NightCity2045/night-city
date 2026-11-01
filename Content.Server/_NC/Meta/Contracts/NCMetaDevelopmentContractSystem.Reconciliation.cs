// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Threading;
using Content.Meta.Commerce;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Contracts;

public sealed record NCMetaDevelopmentCatalog(Guid[] Contracts, long Revision);

public sealed partial class NCMetaDevelopmentContractSystem
{
    private const string CatalogType = "meta.development.catalog";
    private CancellationTokenSource _testLifetime = new();
    private bool _autoRunning;
    private float _autoDelay;
    private int _autoCursor;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnClosing);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnStarted);
    }
    public override void Shutdown()
    {
        _testLifetime.Cancel(); _testLifetime.Dispose(); _prompts.Clear(); _rates.Clear();
        base.Shutdown();
    }
    private void OnClosing(NCActiveWorldSessionClosingEvent args) { _testLifetime.Cancel(); _prompts.Clear(); }
    private void OnStarted(NCActiveWorldSessionStartedEvent args)
    {
        _testLifetime.Dispose(); _testLifetime = new(); _prompts.Clear(); _rates.Clear(); _autoCursor = 0;
    }
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _autoDelay -= frameTime;
        if (_autoRunning || _autoDelay > 0 || !_persistence.TryCaptureTime(out var epoch, out _)) return;
        _autoDelay = 1; _autoRunning = true;
        _ = ReconcileBatchAsync(epoch);
    }
    private async Task ReconcileBatchAsync(NCSessionEpoch epoch)
    {
        try
        {
            var catalog = await Read<NCMetaDevelopmentCatalog>(CatalogType, "active", value => value.Revision);
            if (catalog == null || catalog.Contracts.Length == 0) return;
            if (catalog.Contracts.Length > 1024) throw new System.IO.InvalidDataException("Development catalog limit.");
            var ids = await Main(() =>
            {
                if (!_persistence.TryCaptureTime(out var current, out _) || current != epoch) return Array.Empty<Guid>();
                var selected = Enumerable.Range(0, Math.Min(4, catalog.Contracts.Length))
                    .Select(index => catalog.Contracts[(_autoCursor + index) % catalog.Contracts.Length]).ToArray();
                _autoCursor = (_autoCursor + selected.Length) % catalog.Contracts.Length;
                return selected;
            });
            foreach (var id in ids) await TryReconcileAutomaticAsync(id, epoch);
        }
        catch (Exception exception) { Log.Error($"META development reconciliation rejected: {exception}"); }
        finally { await Main(() => { _autoRunning = false; return true; }); }
    }
    /// <summary>Trusted native recovery. The protected dual-consent mandate and receipt are reread, never supplied by a guest.</summary>
    public async Task<NCMetaDevelopmentOutcome> TryReconcileAutomaticAsync(Guid contractId, NCSessionEpoch epoch)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        if (mutation == null) return Fail("busy");
        var gate = _gates[(uint) contractId.GetHashCode() % (uint) _gates.Length];
        if (!await gate.WaitAsync(0)) return Fail("busy");
        try
        {
            var contract = await ReadContractAsync(contractId);
            if (contract == null || contract.Terms.Acceptance != MetaDevelopmentAcceptance.FixturesAutomatic ||
                contract.Phase != MetaDevelopmentPhase.Delivered) return Fail("action");
            var receipt = await Receipt(contract);
            if (receipt == null || !MetaDevelopmentPolicy.CanSettle(contract, receipt.Receipt, false)) return Fail("technical-required");
            return await Pay(null, NCMetaPersistenceSystem.StableGuid("development-auto:" + receipt.Receipt.Id),
                NCMetaPersistenceSystem.Fingerprint("automatic", contract.Id.ToString("D"), receipt.Receipt.Id.ToString("D")),
                contract, 0, contract.Terms.Price, true, false, automaticEpoch: epoch);
        }
        finally { gate.Release(); }
    }
    // Automatic execution is a native city-service mandate signed by BOTH parties before funding.
    // It is not an org employee, VM principal or fabricated player. Target proof and the exact
    // contract/receipt CAS still enter the same SQL money/entitlement transaction.
    private NCMetaAuthorityConstraints? AutomaticConstraints(MetaDevelopmentContract contract, NCSessionEpoch? epoch)
    {
        if (epoch == null || !_persistence.TryCaptureTime(out var current, out _) || current != epoch ||
            contract.Phase != MetaDevelopmentPhase.Delivered || contract.Terms.Acceptance != MetaDevelopmentAcceptance.FixturesAutomatic ||
            !MetaDevelopmentPolicy.BothConsented(contract)) return null;
        var devices = new List<NCMetaDeviceAuthorityProof>();
        foreach (var target in contract.Terms.Targets)
        {
            if (!_devices.TryResolve(target.DeviceId, out var physical) || !physical.Ready || physical.Owner != contract.Terms.Customer ||
                physical.AuthorityEpoch != target.AuthorityEpoch || !_devices.IsPowered(physical.Entity) || !_marketplace.HasMarketplaceRoute(physical.Entity)) return null;
            devices.Add(new(physical.Id, physical.Owner, physical.AuthorityEpoch));
        }
        return devices.Count is >= 1 and <= 3 ? new(null, devices.ToArray()) : null;
    }
    private async Task<NCPersistentAggregateWrite?> PrepareCatalogRemoval(Guid id)
    {
        var current = await Read<NCMetaDevelopmentCatalog>(CatalogType, "active", value => value.Revision);
        if (current == null || !current.Contracts.Contains(id)) return null;
        return Write(CatalogType, "active", current.Revision,
            current with { Contracts = current.Contracts.Where(value => value != id).ToArray(), Revision = current.Revision + 1 });
    }
}
