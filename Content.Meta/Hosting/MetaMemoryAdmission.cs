// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Hosting;

/// <summary>Strict virtual hardware admission. Default guest shares leave native buffer headroom inside physical RAM.</summary>
public static class MetaMemoryAdmission
{
    public static bool TryPlan(long physicalBytes, int maximumProcesses, long artifactBytes, long minimumGuestBytes,
        long requestedGuestBytes, long alreadyReservedBytes, out long guestBytes)
    {
        guestBytes = 0;
        if (physicalBytes <= 0 || maximumProcesses is < 1 or > 256 || artifactBytes < 0 || minimumGuestBytes < 0 ||
            requestedGuestBytes < 0 || alreadyReservedBytes < 0 || alreadyReservedBytes > physicalBytes) return false;
        var slot = physicalBytes / maximumProcesses;
        var nativeHeadroom = Math.Min(65536L, Math.Max(24576L, slot / 8));
        var planned = requestedGuestBytes == 0 ? slot - artifactBytes - nativeHeadroom : requestedGuestBytes;
        if (planned < Math.Max(32768L, minimumGuestBytes) || artifactBytes > physicalBytes - alreadyReservedBytes ||
            planned > physicalBytes - alreadyReservedBytes - artifactBytes) return false;
        guestBytes = planned; return true;
    }
}
