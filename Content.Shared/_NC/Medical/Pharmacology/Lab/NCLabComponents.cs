// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Pharmacology.Lab;

public enum NCLabSampleKind : byte
{
    Blood,
    WoundSwab,
}

/// <summary>
/// A physical sample taken from a patient. Only what anyone can see is networked: what it is and whom it came
/// from. What it contains stays on the server until a laboratory method reads it.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCLabSampleComponent : Component
{
    [DataField, AutoNetworkedField]
    public NCLabSampleKind Kind;

    [DataField, AutoNetworkedField]
    public string Source = string.Empty;
}

/// <summary>
/// An empty or filled bag of donated blood. The group is what the donor really had when the bag was filled;
/// a bag that spent too long warm is spoiled and cannot be used.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCBloodBagComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool Filled;

    [DataField, AutoNetworkedField]
    public bool Spoiled;

    [DataField, AutoNetworkedField]
    public string Group = string.Empty;

    [DataField, AutoNetworkedField]
    public string Donor = string.Empty;

    /// <summary>Volume drawn into the bag when it was filled.</summary>
    [DataField]
    public float Volume = 450f;

    /// <summary>Blood volume a donor must keep, as a share of nominal, for a draw to be allowed.</summary>
    [DataField]
    public float DonorFloorFraction = 0.75f;

    /// <summary>Seconds a filled bag survives outside a working cold storage.</summary>
    [DataField]
    public float ShelfLifeSeconds = 1200f;

    public float ExposedSeconds;

    /// <summary>What an empty bag turns into when a donor fills it.</summary>
    [DataField]
    public EntProtoId? FilledPrototype;
}

[Serializable, NetSerializable]
public sealed partial class NCLabSampleDoAfterEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public sealed partial class NCLabAnalyzeDoAfterEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public sealed partial class NCBloodDrawDoAfterEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public sealed partial class NCPharmaPurifyDoAfterEvent : SimpleDoAfterEvent;

public sealed class NCLabExamineSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCLabSampleComponent, ExaminedEvent>(OnSampleExamined);
        SubscribeLocalEvent<NCBloodBagComponent, ExaminedEvent>(OnBagExamined);
    }

    private void OnSampleExamined(Entity<NCLabSampleComponent> sample, ref ExaminedEvent args)
    {
        var kind = sample.Comp.Kind == NCLabSampleKind.Blood ? "nc-lab-sample-blood" : "nc-lab-sample-swab";
        args.PushMarkup(Loc.GetString("nc-lab-sample-examine",
            ("kind", Loc.GetString(kind)), ("source", sample.Comp.Source)));
    }

    private void OnBagExamined(Entity<NCBloodBagComponent> bag, ref ExaminedEvent args)
    {
        if (!bag.Comp.Filled)
        {
            args.PushMarkup(Loc.GetString("nc-lab-bag-empty"));
            return;
        }

        args.PushMarkup(Loc.GetString("nc-lab-bag-filled",
            ("group", Loc.GetString("nc-lab-blood-group-" + bag.Comp.Group.ToLowerInvariant())),
            ("donor", bag.Comp.Donor)));
        if (bag.Comp.Spoiled)
            args.PushMarkup(Loc.GetString("nc-lab-bag-spoiled"));
    }
}
