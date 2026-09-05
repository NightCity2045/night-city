// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Admin;

namespace Content.Client._NC.LivingCity.Admin;

/// <summary>Typed client boundary for the F7 random resident factory.</summary>
public sealed partial class NCLivingCityAdminClientSystem : EntitySystem
{
    public event Action<NCLivingCityAdminRandomResidentResult>? ResultReceived;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCLivingCityAdminRandomResidentResult>(OnResult);
    }

    public Guid RequestRandomResident()
    {
        var requestId = Guid.NewGuid();
        RaiseNetworkEvent(new NCLivingCityAdminRandomResidentRequest(requestId));
        return requestId;
    }

    private void OnResult(NCLivingCityAdminRandomResidentResult result) => ResultReceived?.Invoke(result);
}
