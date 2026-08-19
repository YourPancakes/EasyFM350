using System;

namespace EasyFM350.Wpf.Backend.Modem;

internal static class ModemCommands
{
    public static string DefinePdp(int contextId, string pdpType, string apn)
    {
        RequireContextId(contextId);
        pdpType = PdpProtocol.ToModemValue(pdpType);
        RequireSafe(apn, nameof(apn));
        return "AT+CGDCONT=" + contextId + ",\"" + pdpType + "\",\"" + apn + "\"";
    }

    public static string SetAuthentication(int contextId, int authentication, string user, string password)
    {
        RequireContextId(contextId);
        if (authentication < 0 || authentication > 2) throw new ArgumentOutOfRangeException(nameof(authentication));
        if (authentication == 0) return "AT+CGAUTH=" + contextId + ",0";
        RequireSafe(user, nameof(user));
        RequireSafe(password, nameof(password));
        return "AT+CGAUTH=" + contextId + "," + authentication + ",\"" + user + "\",\"" + password + "\"";
    }

    public static string ActivatePdp(int contextId, bool active)
    {
        RequireContextId(contextId);
        return "AT+CGACT=" + (active ? "1" : "0") + "," + contextId;
    }

    public static string ReadPdpAddress(int contextId)
    {
        RequireContextId(contextId);
        return "AT+CGPADDR=" + contextId;
    }

    public static string ReadDynamicPdp(int contextId)
    {
        RequireContextId(contextId);
        return "AT+CGCONTRDP=" + contextId;
    }

    public static string ReadDns(int contextId)
    {
        RequireContextId(contextId);
        return "AT+GTDNS=" + contextId;
    }

    private static void RequireContextId(int contextId)
    {
        if (contextId < 1) throw new ArgumentOutOfRangeException(nameof(contextId));
    }

    private static void RequireSafe(string value, string parameterName)
    {
        if (!AtInput.IsSafeValue(value)) throw new ArgumentException("Unsafe AT value.", parameterName);
    }
}