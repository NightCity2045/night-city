// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using Content.Shared.CCVar;
using Content.Shared.Localizations;
using Robust.Client.UserInterface.Controllers;
using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Client._NC.Localization;

/// <summary>
/// Applies the archived language CVar at runtime and refreshes controls that opt into relocalization.
/// </summary>
public sealed partial class NCLocalizationUIController : UIController
{
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private ContentLocalizationManager _contentLocalization = default!;
    [Dependency] private ILocalizationManager _localization = default!;

    private bool _normalizingCulture;

    public override void Initialize()
    {
        _config.OnValueChanged(CVars.LocCultureName, OnCultureChanged, invokeImmediately: true);
    }

    private void OnCultureChanged(string cultureName)
    {
        if (_normalizingCulture)
            return;

        var validated = ContentLocalizationManager.ValidateCultureName(cultureName) ??
                        ContentLocalizationManager.DefaultCultureName;

        try
        {
            var culture = CultureInfo.GetCultureInfo(validated);
            _contentLocalization.EnsureCulturePrepared(culture);
            _localization.SetCulture(culture);
            _localization.ReloadLocalizations();
            _config.SetCVar(CCVars.NCClientCulture, culture.Name);

            if (!string.Equals(cultureName, culture.Name, StringComparison.OrdinalIgnoreCase))
            {
                _normalizingCulture = true;
                _config.SetCVar(CVars.LocCultureName, culture.Name);
                _normalizingCulture = false;
            }

        }
        catch (Exception exception)
        {
            Log.Error($"Failed to apply client culture '{cultureName}': {exception}");
        }
    }
}
