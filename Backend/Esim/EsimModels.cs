using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EasyFM350.Wpf.Backend.Esim;

public sealed class LpacResult
{
    public int Code { get; private set; } = -1;
    public string Message { get; private set; } = "";
    public JsonNode? Data { get; private set; }
    public bool Ok => Code == 0;

    public string? ErrorDetail =>
        Data is JsonValue value && value.TryGetValue<string>(out var detail) ? detail : null;

    public static LpacResult Error(string message)
    {
        return new LpacResult { Message = message };
    }

    public static LpacResult Success()
    {
        return new LpacResult { Code = 0 };
    }

    public static LpacResult? TryParse(string line)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }

        if ((string?)root?["type"] != "lpa") return null;
        var payload = root?["payload"];
        if (payload == null) return null;
        return new LpacResult
        {
            Code = (int?)payload["code"] ?? -1,
            Message = (string?)payload["message"] ?? "",
            Data = payload["data"]
        };
    }
}

public sealed class EsimChipInfo
{
    public string? Eid { get; private set; }
    public string? DefaultSmdp { get; private set; }
    public string? ProfileVersion { get; private set; }
    public string? Firmware { get; private set; }
    public long? FreeMemory { get; private set; }

    public static EsimChipInfo FromJson(JsonNode? data)
    {
        var info = new EsimChipInfo();
        if (data == null) return info;
        info.Eid = (string?)data["eidValue"];
        info.DefaultSmdp = (string?)data["EuiccConfiguredAddresses"]?["defaultDpAddress"];
        var euiccInfo2 = data["EUICCInfo2"];
        info.ProfileVersion = (string?)euiccInfo2?["profileVersion"];
        info.Firmware = (string?)euiccInfo2?["euiccFirmwareVer"];
        if (euiccInfo2?["extCardResource"]?["freeNonVolatileMemory"] is JsonValue freeMemory
            && freeMemory.TryGetValue<long>(out var freeMemoryBytes))
            info.FreeMemory = freeMemoryBytes;

        return info;
    }
}

public sealed class EsimProfile
{
    public string Iccid { get; private set; } = "";
    public string? State { get; private set; }
    public string? Nickname { get; private set; }
    public string? ProviderName { get; private set; }
    public string? ProfileName { get; private set; }

    public bool Enabled => string.Equals(State, "enabled", StringComparison.OrdinalIgnoreCase);

    public string Title => Nickname ?? ProfileName ?? ProviderName ?? Iccid;

    public static List<EsimProfile> ListFromJson(JsonNode? data)
    {
        var list = new List<EsimProfile>();
        if (data is not JsonArray array) return list;
        foreach (var item in array)
        {
            if (item == null) continue;
            var iccid = (string?)item["iccid"];
            if (string.IsNullOrEmpty(iccid)) continue;
            list.Add(new EsimProfile
            {
                Iccid = iccid,
                State = (string?)item["profileState"],
                Nickname = (string?)item["profileNickname"],
                ProviderName = (string?)item["serviceProviderName"],
                ProfileName = (string?)item["profileName"]
            });
        }

        return list;
    }
}

public sealed class EsimNotification
{
    public int Seq { get; private set; }
    public string? Operation { get; private set; }
    public string? Address { get; private set; }
    public string? Iccid { get; private set; }

    public static List<EsimNotification> ListFromJson(JsonNode? data)
    {
        var list = new List<EsimNotification>();
        if (data is not JsonArray array) return list;
        foreach (var item in array)
        {
            if (item == null) continue;
            list.Add(new EsimNotification
            {
                Seq = (int?)item["seqNumber"] ?? 0,
                Operation = (string?)item["profileManagementOperation"],
                Address = (string?)item["notificationAddress"],
                Iccid = (string?)item["iccid"]
            });
        }

        return list;
    }
}