// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Text.Json;

namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    private sealed class InstallationRevisionException : Exception { }

    /// <summary>Exact native firmware retries are read before service-state or replacement-slot checks.</summary>
    public Task<NCMetaMutationOutcome?> ReadFirmwareInstallReplayAsync(Guid requestId, string hostId, string owner,
        Guid projectId, NCMetaRelease release, NCMetaBinding[] bindings, long memoryLimitBytes,
        Guid expectedInstallationId, long expectedInstallationRevision, Guid productId = default, string? consentFingerprint = null) =>
        ReadReplayAsync(requestId, owner, productId == Guid.Empty
                ? InstallationFingerprint(hostId, owner, projectId, release, bindings, true, "Firmware", memoryLimitBytes,
                    expectedInstallationId, expectedInstallationRevision, consentFingerprint)
                : LicensedInstallationFingerprint(hostId, owner, productId, release, bindings, "Firmware", true,
                    memoryLimitBytes, expectedInstallationId, expectedInstallationRevision, consentFingerprint),
            productId == Guid.Empty ? "meta.installation.activate" : "meta.installation.licensed");

    private static string InstallationFingerprint(string hostId, string owner, Guid projectId, NCMetaRelease release,
        NCMetaBinding[] bindings, bool autoStart, string slotKind, long? memoryLimitBytes,
        Guid? expectedInstallationId, long? expectedInstallationRevision, string? consentFingerprint)
    {
        // Keep the pre-slot-confirmation fingerprint for existing ordinary install receipts.
        var original = new[] { owner, hostId, projectId.ToString("D"), release.Id.ToString("D"), release.Digest,
            JsonSerializer.Serialize(bindings, Json), autoStart.ToString(), slotKind,
            memoryLimitBytes?.ToString(CultureInfo.InvariantCulture) ?? "" };
        return expectedInstallationId == null && expectedInstallationRevision == null && consentFingerprint == null ? Fingerprint(original) :
            Fingerprint([..original, expectedInstallationId?.ToString("D") ?? "",
                expectedInstallationRevision?.ToString(CultureInfo.InvariantCulture) ?? "", consentFingerprint ?? ""]);
    }

    private static string LicensedInstallationFingerprint(string hostId, string owner, Guid productId,
        NCMetaRelease release, NCMetaBinding[] bindings, string slotKind, bool autoStart, long? memoryLimitBytes,
        Guid? expectedInstallationId, long? expectedInstallationRevision, string? consentFingerprint)
    {
        var original = new[] { owner, hostId, productId.ToString("D"), release.Id.ToString("D"), release.Digest,
            JsonSerializer.Serialize(bindings, Json) };
        return slotKind == "User" && !autoStart && memoryLimitBytes == null && expectedInstallationId == null &&
               expectedInstallationRevision == null && consentFingerprint == null ? Fingerprint(original) :
            Fingerprint([..original, slotKind, autoStart.ToString(), memoryLimitBytes?.ToString(CultureInfo.InvariantCulture) ?? "",
                expectedInstallationId?.ToString("D") ?? "", expectedInstallationRevision?.ToString(CultureInfo.InvariantCulture) ?? "", consentFingerprint ?? ""]);
    }
}
