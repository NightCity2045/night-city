// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Identity;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Persistence.Identity;

/// <summary>
/// Stable identity of a persistent non-player city actor. It never stores a runtime <see cref="EntityUid"/>.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCActorId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static NCActorId New()
    {
        return new NCActorId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>
/// Stable identity of a persistent strategic or social actor group. Groups are not runtime ECS entities.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCGroupId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static NCGroupId New()
    {
        return new NCGroupId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>
/// Stable identity of an organization that can own assets or enter agreements.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCOrganizationId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static NCOrganizationId New()
    {
        return new NCOrganizationId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>
/// Stable identity of a business, distinct from its owner and current operator.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCBusinessId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static NCBusinessId New()
    {
        return new NCBusinessId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>
/// Stable identity of a city service that participates in contracts, budgets, or obligations.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCCityServiceId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static NCCityServiceId New()
    {
        return new NCCityServiceId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>
/// Stable identity of an estate that temporarily owns assets and obligations after a death.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCEstateId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static NCEstateId New()
    {
        return new NCEstateId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>
/// Stable identity of one money account. An account is persistent and never represented by an ECS entity.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCAccountId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static NCAccountId New()
    {
        return new NCAccountId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>
/// Stable identity of one immutable ledger transaction.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCTransactionId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static NCTransactionId New()
    {
        return new NCTransactionId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>
/// Stable identity of a concrete employment relation. It is distinct from both employee and employer.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCEmploymentId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static NCEmploymentId New()
    {
        return new NCEmploymentId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>
/// Discriminates persistent participants that can fill roles such as owner, buyer, seller, or employer.
/// </summary>
[Serializable, NetSerializable]
public enum NCPartyKind : byte
{
    None,
    Character,
    Actor,
    Organization,
    Business,
    CityService,
    Estate,
}

/// <summary>
/// Typed persistent reference shared by ownership, transactions, contracts, and obligations.
/// Character rows use their existing integer identity; every other party uses a stable GUID.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCPartyRef(
    NCPartyKind Kind,
    NCCharacterId CharacterId,
    Guid PersistentId)
{
    public bool IsValid => Kind switch
    {
        NCPartyKind.Character => CharacterId.IsValid && PersistentId == Guid.Empty,
        NCPartyKind.Actor or
        NCPartyKind.Organization or
        NCPartyKind.Business or
        NCPartyKind.CityService or
        NCPartyKind.Estate => !CharacterId.IsValid && PersistentId != Guid.Empty,
        _ => false,
    };

    public static NCPartyRef ForCharacter(NCCharacterId characterId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId.Value);
        return new NCPartyRef(NCPartyKind.Character, characterId, Guid.Empty);
    }

    public static NCPartyRef ForActor(NCActorId actorId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(actorId.Value, Guid.Empty);
        return new NCPartyRef(NCPartyKind.Actor, default, actorId.Value);
    }

    public static NCPartyRef ForOrganization(NCOrganizationId organizationId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(organizationId.Value, Guid.Empty);
        return new NCPartyRef(NCPartyKind.Organization, default, organizationId.Value);
    }

    public static NCPartyRef ForBusiness(NCBusinessId businessId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(businessId.Value, Guid.Empty);
        return new NCPartyRef(NCPartyKind.Business, default, businessId.Value);
    }

    public static NCPartyRef ForCityService(NCCityServiceId serviceId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(serviceId.Value, Guid.Empty);
        return new NCPartyRef(NCPartyKind.CityService, default, serviceId.Value);
    }

    public static NCPartyRef ForEstate(NCEstateId estateId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(estateId.Value, Guid.Empty);
        return new NCPartyRef(NCPartyKind.Estate, default, estateId.Value);
    }

    public bool TryGetCharacter(out NCCharacterId characterId)
    {
        characterId = Kind == NCPartyKind.Character && IsValid ? CharacterId : default;
        return characterId.IsValid;
    }

    public bool TryGetActor(out NCActorId actorId)
    {
        actorId = Kind == NCPartyKind.Actor && IsValid ? new NCActorId(PersistentId) : default;
        return actorId.IsValid;
    }

    public bool TryGetOrganization(out NCOrganizationId organizationId)
    {
        organizationId = Kind == NCPartyKind.Organization && IsValid
            ? new NCOrganizationId(PersistentId)
            : default;
        return organizationId.IsValid;
    }

    public bool TryGetBusiness(out NCBusinessId businessId)
    {
        businessId = Kind == NCPartyKind.Business && IsValid ? new NCBusinessId(PersistentId) : default;
        return businessId.IsValid;
    }

    public bool TryGetCityService(out NCCityServiceId serviceId)
    {
        serviceId = Kind == NCPartyKind.CityService && IsValid
            ? new NCCityServiceId(PersistentId)
            : default;
        return serviceId.IsValid;
    }

    public bool TryGetEstate(out NCEstateId estateId)
    {
        estateId = Kind == NCPartyKind.Estate && IsValid ? new NCEstateId(PersistentId) : default;
        return estateId.IsValid;
    }

    public override string ToString()
    {
        return Kind == NCPartyKind.Character
            ? $"character:{CharacterId}"
            : $"{Kind.ToString().ToLowerInvariant()}:{PersistentId:D}";
    }

    /// <summary>
    /// Parses the canonical storage key emitted by <see cref="ToString"/>. Arbitrary untyped strings are rejected.
    /// </summary>
    public static bool TryParse(string value, out NCPartyRef party)
    {
        party = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var separator = value.IndexOf(':');
        if (separator < 1 || separator == value.Length - 1)
            return false;

        var kindValue = value[..separator];
        var identityValue = value[(separator + 1)..];
        if (kindValue.Equals("character", StringComparison.OrdinalIgnoreCase) &&
            NCCharacterId.TryParse(identityValue, out var characterId))
        {
            party = ForCharacter(characterId);
            return true;
        }

        if (!Guid.TryParse(identityValue, out var persistentId) || persistentId == Guid.Empty)
            return false;

        var kind = kindValue.ToLowerInvariant() switch
        {
            "actor" => NCPartyKind.Actor,
            "organization" => NCPartyKind.Organization,
            "business" => NCPartyKind.Business,
            "cityservice" => NCPartyKind.CityService,
            "estate" => NCPartyKind.Estate,
            _ => NCPartyKind.None,
        };
        if (kind == NCPartyKind.None)
            return false;

        party = new NCPartyRef(kind, default, persistentId);
        return party.IsValid;
    }
}
