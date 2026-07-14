using System.Runtime.InteropServices;

namespace SimpleRdpManager;

public static class RdpRegistration
{
    // Known RDP ActiveX CLSIDs, ordered by preference (newest first)
    private static readonly string[] KnownClsids =
    {
        // "Safe for scripting" variants (most commonly registered by Windows Update / RDCMan)
        "{C0EFA91A-EEB7-41C7-97FA-F0ED645EFB24}", // MsRdpClient10
        "{D09E23D6-CF5C-4DFA-A7E1-0BF95E5E7645}", // MsRdpClient9
        "{301B94BA-2FC8-4571-88CF-C505CFA7A4DC}", // MsRdpClient8
        "{A9D7038D-B5ED-472E-9C47-94EEA90A591A}", // MsRdpClient7

        // "NotSafeForScripting" variants
        "{A0B4DD6A-5DFF-4AE2-A8CF-281C4A9C5D1E}", // MsRdpClient10NotSafeForScripting
        "{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}", // MsRdpClient9NotSafeForScripting
        "{7A74D0E4-6F16-4E2C-923B-F3B3B4E0F0FD}", // MsRdpClient8NotSafeForScripting
        "{A9D7038D-B5ED-472E-9C47-94EEA90A76D9}", // MsRdpClient7NotSafeForScripting
    };

    /// <summary>
    /// Discovers the first registered RDP ActiveX CLSID on this system.
    /// Returns null if none found.
    /// </summary>
    public static string? DiscoverClsid()
    {
        foreach (var clsid in KnownClsids)
        {
            try
            {
                var type = Type.GetTypeFromCLSID(Guid.Parse(clsid), true);
                if (type != null)
                {
                    RdpLogger.Log($"[RdpRegistration] Found: {clsid}");
                    return clsid;
                }
            }
            catch
            {
                // Not registered, try next
            }
        }

        // Try ProgID approach as fallback
        var progIds = new[] 
        { 
            "MsRdpClient10.MsRdpClient.10",
            "MsRdpClient9.MsRdpClient.9",
            "MsRdpClient8.MsRdpClient.8",
            "MsRdpClient10NotSafeForScripting"
        };

        foreach (var progId in progIds)
        {
            try
            {
                var type = Type.GetTypeFromProgID(progId, true);
                if (type != null)
                {
                    RdpLogger.Log($"[RdpRegistration] Found ProgID: {progId} -> {type.GUID}");
                    return type.GUID.ToString("B").ToUpperInvariant();
                }
            }
            catch
            {
                // Not registered
            }
        }

        return null;
    }
}
