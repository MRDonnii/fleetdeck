using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Intel AMT over WS-MAN (port 16992, HTTP Digest, bruger "admin"): læs strømstatus og tænd/sluk/genstart direkte i hardwaren –
/// virker også, når Windows er hængt eller pc'en er slukket.
/// </summary>
public static partial class AmtClient
{
    // CIM-strømtilstande (RequestPowerStateChange)
    public const int On = 2, PowerCycle = 5, OffHard = 8, Reset = 10, OffSoft = 12;

    private const string Addressing = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private const string Wsman = "http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd";
    private const string Cim = "http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/";

    public static string StateName(int? s) => s switch
    {
        2 => "Tændt", 3 => "Slumrer (lys)", 4 => "Slumrer (dyb)", 6 => "Slukket (hårdt)", 7 => "Dvale", 8 => "Slukket", 9 => "Genstarter",
        10 => "Genstarter", 12 => "Slukket", 13 => "Slukket", null => "Ukendt", _ => $"Tilstand {s}",
    };

    private static HttpClient Client(string ip, string password)
    {
        var uri = new Uri($"http://{ip}:16992/wsman");
        var handler = new HttpClientHandler { Credentials = new CredentialCache { { uri, "Digest", new NetworkCredential("admin", password) } }, PreAuthenticate = false };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static string Envelope(string action, string resource, string body, string selectors = "") =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:a="{Addressing}" xmlns:w="{Wsman}">
        <s:Header>
        <a:Action s:mustUnderstand="true">{action}</a:Action>
        <a:To s:mustUnderstand="true">/wsman</a:To>
        <w:ResourceURI s:mustUnderstand="true">{resource}</w:ResourceURI>
        <a:MessageID s:mustUnderstand="true">uuid:{Guid.NewGuid()}</a:MessageID>
        <a:ReplyTo><a:Address>{Addressing}/role/anonymous</a:Address></a:ReplyTo>
        <w:OperationTimeout>PT60S</w:OperationTimeout>
        {selectors}
        </s:Header>
        <s:Body>{body}</s:Body>
        </s:Envelope>
        """;

    private static async Task<string> Post(string ip, string password, string xml, CancellationToken ct)
    {
        using var http = Client(ip, password);
        using var content = new StringContent(xml, Encoding.UTF8, "application/soap+xml");
        using var res = await http.PostAsync($"http://{ip}:16992/wsman", content, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("AMT afviste adgangskoden");
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"AMT svarede {(int)res.StatusCode}: {Fault(text)}");
        return text;
    }

    private static string Fault(string xml) => FaultText().Match(xml) is { Success: true } m ? m.Groups[1].Value : xml.Length > 200 ? xml[..200] : xml;

    /// <summary>Strømtilstanden (2 = tændt, 8 = slukket, …).</summary>
    public static async Task<int?> PowerState(string ip, string password, CancellationToken ct = default)
    {
        const string res = Cim + "CIM_AssociatedPowerManagementService";
        var en = await Post(ip, password, Envelope("http://schemas.xmlsoap.org/ws/2004/09/enumeration/Enumerate", res,
            """<Enumerate xmlns="http://schemas.xmlsoap.org/ws/2004/09/enumeration" />"""), ct);
        var ctx = EnumContext().Match(en);
        if (!ctx.Success) return null;
        var pull = await Post(ip, password, Envelope("http://schemas.xmlsoap.org/ws/2004/09/enumeration/Pull", res,
            $"""<Pull xmlns="http://schemas.xmlsoap.org/ws/2004/09/enumeration"><EnumerationContext>{ctx.Groups[1].Value}</EnumerationContext><MaxElements>99</MaxElements><MaxCharacters>99999</MaxCharacters></Pull>"""), ct);
        return PowerStateValue().Match(pull) is { Success: true } p ? int.Parse(p.Groups[1].Value) : null;
    }

    /// <summary>Tænd/sluk/genstart. Returnerer AMT's ReturnValue (0 = ok).</summary>
    public static async Task<int> SetPower(string ip, string password, int state, CancellationToken ct = default)
    {
        const string res = Cim + "CIM_PowerManagementService";
        var selectors = $"""
            <w:SelectorSet><w:Selector Name="CreationClassName">CIM_PowerManagementService</w:Selector><w:Selector Name="Name">Intel(r) AMT Power Management Service</w:Selector><w:Selector Name="SystemCreationClassName">CIM_ComputerSystem</w:Selector><w:Selector Name="SystemName">Intel(r) AMT</w:Selector></w:SelectorSet>
            """;
        var body = $"""
            <r:RequestPowerStateChange_INPUT xmlns:r="{res}">
            <r:PowerState>{state}</r:PowerState>
            <r:ManagedElement><a:Address>{Addressing}/role/anonymous</a:Address><a:ReferenceParameters><w:ResourceURI>{Cim}CIM_ComputerSystem</w:ResourceURI><w:SelectorSet><w:Selector Name="CreationClassName">CIM_ComputerSystem</w:Selector><w:Selector Name="Name">ManagedSystem</w:Selector></w:SelectorSet></a:ReferenceParameters></r:ManagedElement>
            </r:RequestPowerStateChange_INPUT>
            """;
        var xml = await Post(ip, password, Envelope(res + "/RequestPowerStateChange", res, body, selectors), ct);
        return ReturnValue().Match(xml) is { Success: true } m ? int.Parse(m.Groups[1].Value) : -1;
    }

    /// <summary>En stærk adgangskode, som AMT godtager (8–32 tegn med store/små bogstaver, tal og specialtegn).</summary>
    public static string NewPassword()
    {
        const string up = "ABCDEFGHJKLMNPQRSTUVWXYZ", lo = "abcdefghijkmnpqrstuvwxyz", di = "23456789", sp = "!#%*+-=?@";
        var all = up + lo + di + sp;
        var chars = new List<char>
        {
            up[System.Security.Cryptography.RandomNumberGenerator.GetInt32(up.Length)], lo[System.Security.Cryptography.RandomNumberGenerator.GetInt32(lo.Length)],
            di[System.Security.Cryptography.RandomNumberGenerator.GetInt32(di.Length)], sp[System.Security.Cryptography.RandomNumberGenerator.GetInt32(sp.Length)],
        };
        while (chars.Count < 20) chars.Add(all[System.Security.Cryptography.RandomNumberGenerator.GetInt32(all.Length)]);
        return new string(chars.OrderBy(_ => System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000)).ToArray());
    }

    [GeneratedRegex(@"<[^>]*EnumerationContext>([^<]+)<")] private static partial Regex EnumContext();
    [GeneratedRegex(@"<(?:\w+:)?PowerState>(\d+)<")] private static partial Regex PowerStateValue();
    [GeneratedRegex(@"<[^>]*ReturnValue>(\d+)<")] private static partial Regex ReturnValue();
    [GeneratedRegex(@"<[^>]*Text[^>]*>([^<]+)<")] private static partial Regex FaultText();
}
