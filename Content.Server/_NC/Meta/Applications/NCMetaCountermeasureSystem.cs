// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Hosting;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Network;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.Temperature.Components;
using Content.Shared.Temperature.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Meta.Applications;

/// <summary>
/// The one native move a defender program has: a surge into the hardware of a device that keeps hitting its host.
/// "ICE" and "black ICE" are only names players give to such programs. What the program decides (whom to answer, how
/// hard, after how many tries) is its own code; what the world allows is fixed here: the target must be a device that
/// recently sent this very host a message and is still routable from it, the strike only damages and heats the target
/// device (never the person at its keyboard), and a host can strike only so often.
/// </summary>
public sealed class NCMetaCountermeasureSystem : EntitySystem, INCMetaDeviceAdapter
{
    public const long ContactWindowMilliseconds = 30_000;
    public const int MaximumPower = 10;
    private const float DamagePerPower = 2f;
    private const float HeatJoulesPerPower = 2_000f;
    private static readonly ProtoId<DamageTypePrototype> DamageType = "Shock";

    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaNetworkSystem _network = default!;
    [Dependency] private DamageableSystem _damage = default!;
    [Dependency] private SharedTemperatureSystem _temperature = default!;
    private readonly Dictionary<EntityUid, MetaRateLimit> _rates = [];

    public IEnumerable<string> Members => ["meta.security.Countermeasure.surge"];

    public bool CanBind(EntityUid device, string capability) =>
        capability == "meta.security.Countermeasure" && HasComp<NCMetaHostComponent>(device) && HasComp<NCMetaMessagePortComponent>(device);

    public override void Initialize()
    {
        base.Initialize();
        _runtime.RegisterAdapter(this);
    }

    public MetaValue Read(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        return process.CreateErrorResult(process.GetImport(invocation.Request.ImportId).ReturnType!.Value, "Unsupported", "Unsupported");
    }

    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        var type = process.GetImport(invocation.Request.ImportId).ReturnType!.Value;
        MetaHostResult Fail(string code) => MetaHostResult.Completed(process.CreateErrorResult(type, code, code));
        var address = process.GetString(invocation.Request.Arguments[0]);
        var power = invocation.Request.Arguments[1].AsInt();
        if (power < 1 || power > MaximumPower) return Fail("InvalidArgument");
        // The program names the sender the way a message does: by the reply address of its port.
        if (!_network.TryGetPort(address, out var target) || target == invocation.Device) return Fail("NotFound");
        if (!_network.CanRoute(invocation.Device, target) ||
            !_network.HasRecentContact(invocation.Device, target, ContactWindowMilliseconds)) return Fail("AccessDenied");
        if (!_rates.TryGetValue(invocation.Device, out var rate)) _rates[invocation.Device] = rate = new MetaRateLimit(3, 1);
        if (!rate.TryConsume(Environment.TickCount64)) return Fail("RateLimited");

        var strike = new DamageSpecifier();
        strike.DamageDict[DamageType] = power * DamagePerPower;
        if (TryComp<DamageableComponent>(target, out _)) _damage.TryChangeDamage(target, strike, origin: invocation.Device);
        if (TryComp<TemperatureComponent>(target, out var temperature)) _temperature.ChangeHeat((target, temperature), power * HeatJoulesPerPower);
        return MetaHostResult.Completed(process.CreateResult(type, MetaValue.Unit));
    }
}
