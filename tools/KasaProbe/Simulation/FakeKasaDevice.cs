using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.Kasa.Kasa;

namespace LoupixDeck.Plugin.Kasa.Simulation;

/// <summary>
/// A pretend KP105 or KP303 that answers the requests the plugin sends, written from the device's
/// side of each protocol: the JSON commands, the XOR framing and the KLAP handshake and session.
/// Used by the tests (in memory) and by <c>kasa-probe simulate</c> (on real sockets, so the plugin
/// can be tried in LoupixDeck without hardware).
/// </summary>
internal sealed class FakeKasaDevice
{
    private readonly Lock _lock = new();
    private readonly JsonObject _sysinfo;
    private readonly byte[] _authHash;
    private (byte[] Local, byte[] Remote, string Cookie)? _pending;
    private (KlapCipher Cipher, string Cookie)? _session;

    /// <param name="account">The account a KLAP device was set up with; ignored by XOR devices.</param>
    public FakeKasaDevice(JsonObject sysinfo, KasaCredentials? account = null)
    {
        _sysinfo = sysinfo;
        _authHash = KlapTransport.AuthHash(1, account ?? KasaCredentials.KasaSetup);
    }

    public int Requests { get; private set; }

    /// <summary>Makes the next KLAP request fail with 403, as a plug does after a restart.</summary>
    public bool DropSession { get; set; }

    public JsonObject SysInfo
    {
        get
        {
            lock (_lock) return (JsonObject)_sysinfo.DeepClone();
        }
    }

    public static JsonObject Kp105(string alias = "Desk lamp", bool on = false) => new()
    {
        ["sw_ver"] = "1.0.7 Build 210506 Rel.154915", ["hw_ver"] = "1.0", ["model"] = "KP105(UK)",
        ["type"] = "IOT.SMARTPLUGSWITCH", ["mac"] = "1C:3B:F3:00:01:05",
        ["deviceId"] = "8006A1B2C3D4E5F60718293A4B5C6D7E8F901050", ["alias"] = alias,
        ["relay_state"] = on ? 1 : 0, ["on_time"] = 0, ["led_off"] = 0, ["feature"] = "TIM", ["err_code"] = 0
    };

    /// <param name="shortChildIds">Some firmware lists sockets as "00", "01"… instead of the full id.</param>
    public static JsonObject Kp303(string alias = "Bench strip", bool shortChildIds = false)
    {
        const string id = "8006F0E1D2C3B4A5968778695A4B3C2D1E0F3030";
        var children = new JsonArray();
        string[] names = ["Monitors", "Soldering iron", "Fan"];
        for (var i = 0; i < 3; i++)
        {
            children.Add(new JsonObject
            {
                ["id"] = shortChildIds ? $"{i:00}" : $"{id}{i:00}", ["state"] = i == 0 ? 1 : 0, ["alias"] = names[i],
                ["on_time"] = 0, ["next_action"] = new JsonObject { ["type"] = -1 }
            });
        }
        return new JsonObject
        {
            ["sw_ver"] = "1.0.3 Build 191105 Rel.113122", ["hw_ver"] = "2.0", ["model"] = "KP303(UK)",
            ["mic_type"] = "IOT.SMARTPLUGSWITCH", ["mac"] = "1C:3B:F3:00:03:03", ["deviceId"] = id, ["alias"] = alias,
            ["led_off"] = 0, ["children"] = children, ["child_num"] = 3, ["feature"] = "TIM", ["err_code"] = 0
        };
    }

    // ---- the commands ----

    public JsonObject Handle(JsonObject request)
    {
        lock (_lock)
        {
            Requests++;
            var system = request["system"] as JsonObject;
            var childIds = (request["context"]?["child_ids"] as JsonArray)?.Select(n => n?.ToString()).ToList();

            if (system?["get_sysinfo"] is not null)
                return Reply("get_sysinfo", (JsonObject)_sysinfo.DeepClone());

            if (system?["set_relay_state"]?["state"] is JsonValue state)
            {
                var on = state.GetValue<int>();
                if (_sysinfo["children"] is JsonArray children)
                {
                    var matched = 0;
                    foreach (var child in children.OfType<JsonObject>())
                    {
                        var full = child["id"]!.ToString().Length <= 2 ? _sysinfo["deviceId"] + child["id"]!.ToString() : child["id"]!.ToString();
                        if (childIds is null || childIds.Contains(full) || childIds.Contains(child["id"]!.ToString()))
                        {
                            child["state"] = on;
                            matched++;
                        }
                    }
                    if (matched == 0) return Error("set_relay_state", -14, "entry not exist");
                }
                else
                {
                    _sysinfo["relay_state"] = on;
                }
                return Reply("set_relay_state", new JsonObject());
            }

            if (system?["set_led_off"]?["off"] is JsonValue off)
            {
                _sysinfo["led_off"] = off.GetValue<int>();
                return Reply("set_led_off", new JsonObject());
            }

            return new JsonObject { ["system"] = new JsonObject { ["err_code"] = -1, ["err_msg"] = "module not support" } };
        }
    }

    private static JsonObject Reply(string method, JsonObject result)
    {
        result["err_code"] = 0;
        return new JsonObject { ["system"] = new JsonObject { [method] = result } };
    }

    private static JsonObject Error(string method, int code, string message) =>
        new() { ["system"] = new JsonObject { [method] = new JsonObject { ["err_code"] = code, ["err_msg"] = message } } };

    // ---- XOR: framed TCP requests and UDP discovery ----

    public byte[] HandleXorFrame(byte[] body) => XorTransport.Frame(Handle(KasaJson.ParseObject(XorTransport.Decrypt(body))).ToJsonString());

    public byte[]? HandleXorDiscovery(byte[] datagram)
    {
        try
        {
            var request = KasaJson.ParseObject(XorTransport.Decrypt(datagram));
            return request["system"]?["get_sysinfo"] is null ? null : XorTransport.Encrypt(Handle(request).ToJsonString());
        }
        catch (KasaException)
        {
            return null;
        }
    }

    /// <summary>The port-20002 answer a KLAP device gives: a 16-byte header, then JSON saying how to talk to it.</summary>
    public byte[] ProbeReply(IPAddress address)
    {
        var json = new JsonObject
        {
            ["result"] = new JsonObject
            {
                // Real devices send a hash of the id here, not the id itself.
                ["device_id"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(_sysinfo["deviceId"]!.ToString()))),
                ["owner"] = "", ["device_type"] = "IOT.SMARTPLUGSWITCH", ["device_model"] = _sysinfo["model"]?.ToString(),
                ["ip"] = address.ToString(), ["mac"] = _sysinfo["mac"]?.ToString()?.Replace(':', '-'),
                ["factory_default"] = false,
                ["mgt_encrypt_schm"] = new JsonObject { ["is_support_https"] = false, ["encrypt_type"] = "KLAP", ["http_port"] = 80 }
            },
            ["error_code"] = 0
        };
        var body = Encoding.UTF8.GetBytes(json.ToJsonString());
        var reply = new byte[16 + body.Length];
        reply[0] = 2;
        reply[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4), (ushort)body.Length);
        body.CopyTo(reply, 16);
        return reply;
    }

    // ---- KLAP over HTTP ----

    public sealed record HttpReply(HttpStatusCode Status, byte[] Body, string? SetCookie = null);

    /// <param name="path">"handshake1", "handshake2" or "request".</param>
    public HttpReply HandleKlap(string path, int? seq, byte[] body, string? cookie)
    {
        lock (_lock)
        {
            switch (path)
            {
                case "handshake1" when body.Length == 16:
                {
                    var remote = RandomNumberGenerator.GetBytes(16);
                    var session = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
                    _pending = (body, remote, $"TP_SESSIONID={session}");
                    _session = null;
                    return new HttpReply(HttpStatusCode.OK, [.. remote, .. SHA256.HashData([.. body, .. _authHash])],
                        $"TP_SESSIONID={session};TIMEOUT=86400");
                }
                case "handshake2" when _pending is { } p && cookie == p.Cookie:
                {
                    // The client proves it has the same account hash; a wrong one gets a 403.
                    if (!body.AsSpan().SequenceEqual(SHA256.HashData([.. p.Remote, .. _authHash]))) return new HttpReply(HttpStatusCode.Forbidden, []);
                    _session = (new KlapCipher(p.Local, p.Remote, _authHash), p.Cookie);
                    _pending = null;
                    return new HttpReply(HttpStatusCode.OK, []);
                }
                case "request" when _session is { } s && cookie == s.Cookie && seq is { } n:
                {
                    if (DropSession)
                    {
                        DropSession = false;
                        _session = null;
                        return new HttpReply(HttpStatusCode.Forbidden, []);
                    }
                    var request = KasaJson.ParseObject(s.Cipher.Decrypt(body, n));
                    // Handle takes the lock again; Lock is re-entrant.
                    return new HttpReply(HttpStatusCode.OK, s.Cipher.EncryptAt(Handle(request).ToJsonString(), n));
                }
                default:
                    return new HttpReply(HttpStatusCode.Forbidden, []);
            }
        }
    }

    /// <summary>The fake device as an HttpClient handler, for tests that need no sockets.</summary>
    public HttpMessageHandler AsHandler() => new KlapHandler(this);

    private sealed class KlapHandler(FakeKasaDevice device) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var path = request.RequestUri!.AbsolutePath.Split('/').Last();
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["seq"];
            var cookie = request.Headers.TryGetValues("Cookie", out var values) ? values.FirstOrDefault() : null;
            var reply = device.HandleKlap(path, int.TryParse(query, out var seq) ? seq : null, body, cookie);
            var response = new HttpResponseMessage(reply.Status) { Content = new ByteArrayContent(reply.Body) };
            if (reply.SetCookie is not null) response.Headers.Add("Set-Cookie", reply.SetCookie);
            return response;
        }
    }
}
