using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Kasa.Kasa;

/// <summary>
/// KLAP, the protocol TP-Link moved Kasa plugs to in later firmware: the same JSON, AES-encrypted
/// and posted over HTTP port 80 after a two-step handshake that proves both sides know a hash of
/// the TP-Link account the plug is registered to. A port of python-kasa's KlapTransport, checked
/// byte for byte against it in the tests.
/// </summary>
/// <remarks>
/// The handshake tells us which account the plug expects: its first answer contains
/// sha256(our seed + auth hash), so we try each candidate (the configured account, Kasa's setup
/// account, no account) in both hash variants and keep the one that matches. Only when none does
/// is it a sign-in failure.
/// </remarks>
internal sealed class KlapTransport : IKasaTransport
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Renew a little before the plug's session ends (it says how long, usually a day).</summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(20);

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly Func<KasaCredentials?> _credentials;
    private readonly Func<byte[]> _newSeed;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Session? _session;

    public KlapTransport(IPAddress address, Func<KasaCredentials?> credentials)
        : this(new Uri($"http://{address}/app/"), credentials, new HttpClientHandler { UseCookies = false }, null)
    {
    }

    /// <summary>For tests: a fake device behind <paramref name="handler"/>, and fixed seeds.</summary>
    internal KlapTransport(Uri baseUri, Func<KasaCredentials?> credentials, HttpMessageHandler handler, Func<byte[]>? newSeed)
    {
        _base = baseUri;
        _credentials = credentials;
        _newSeed = newSeed ?? (() => RandomNumberGenerator.GetBytes(16));
        _http = new HttpClient(handler) { Timeout = Timeout };
    }

    public string Name => "KLAP";

    /// <summary>Which account the plug accepted at the last handshake, for the settings page and the probe.</summary>
    public string? SignedInAs { get; private set; }

    public async Task<JsonObject> SendAsync(JsonObject request, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is null || DateTimeOffset.UtcNow >= _session.Expires) _session = await HandshakeAsync(ct).ConfigureAwait(false);
            try
            {
                return await PostAsync(_session, request, ct).ConfigureAwait(false);
            }
            catch (StaleSessionException)
            {
                // The plug restarted or dropped our session: sign in again once.
                _session = await HandshakeAsync(ct).ConfigureAwait(false);
                return await PostAsync(_session, request, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _session = null;
            throw new KasaException("No answer", KasaFailure.Offline, ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---- handshake ----

    private async Task<Session> HandshakeAsync(CancellationToken ct)
    {
        var localSeed = _newSeed();
        using var reply1 = await _http.PostAsync(new Uri(_base, "handshake1"), Bytes(localSeed), ct).ConfigureAwait(false);
        if (reply1.StatusCode != HttpStatusCode.OK)
        {
            throw new KasaException($"Handshake refused ({(int)reply1.StatusCode})", KasaFailure.Protocol);
        }
        var body = await reply1.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (body.Length != 48) throw new KasaException($"Odd handshake reply ({body.Length} bytes)", KasaFailure.Protocol);

        var remoteSeed = body[..16];
        var serverHash = body[16..];
        var (cookie, lifetime) = ReadCookies(reply1.Headers);

        var match = Candidates().FirstOrDefault(c => Handshake1Hash(c.Version, localSeed, remoteSeed, c.AuthHash).AsSpan().SequenceEqual(serverHash));
        if (match.AuthHash is null)
        {
            throw new KasaException("Sign-in failed: check the TP-Link account in the plugin settings", KasaFailure.SignIn);
        }

        using var request2 = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "handshake2"))
        {
            Content = Bytes(Handshake2Hash(match.Version, localSeed, remoteSeed, match.AuthHash))
        };
        if (cookie is not null) request2.Headers.Add("Cookie", cookie);
        using var reply2 = await _http.SendAsync(request2, ct).ConfigureAwait(false);
        if (reply2.StatusCode != HttpStatusCode.OK)
        {
            throw new KasaException($"Sign-in failed at step 2 ({(int)reply2.StatusCode})", KasaFailure.SignIn);
        }

        SignedInAs = match.Label;
        return new Session(new KlapCipher(localSeed, remoteSeed, match.AuthHash), cookie, DateTimeOffset.UtcNow + lifetime - ExpiryMargin);
    }

    private IEnumerable<(int Version, byte[] AuthHash, string Label)> Candidates()
    {
        var accounts = new List<(KasaCredentials, string)>();
        if (_credentials() is { IsBlank: false } configured) accounts.Add((configured, configured.Username));
        accounts.Add((KasaCredentials.KasaSetup, "Kasa setup account"));
        accounts.Add((KasaCredentials.Blank, "no account"));
        foreach (var (account, label) in accounts)
        {
            // Kasa (IOT) plugs use version 1; version 2 is the Tapo variant, tried in case a firmware moves over.
            yield return (1, AuthHash(1, account), label);
            yield return (2, AuthHash(2, account), label);
        }
    }

    internal static byte[] AuthHash(int version, KasaCredentials account)
    {
        var user = Encoding.UTF8.GetBytes(account.Username);
        var password = Encoding.UTF8.GetBytes(account.Password);
        return version == 1
            ? MD5.HashData([.. MD5.HashData(user), .. MD5.HashData(password)])
            : SHA256.HashData([.. SHA1.HashData(user), .. SHA1.HashData(password)]);
    }

    internal static byte[] Handshake1Hash(int version, byte[] localSeed, byte[] remoteSeed, byte[] authHash) =>
        version == 1 ? SHA256.HashData([.. localSeed, .. authHash]) : SHA256.HashData([.. localSeed, .. remoteSeed, .. authHash]);

    internal static byte[] Handshake2Hash(int version, byte[] localSeed, byte[] remoteSeed, byte[] authHash) =>
        version == 1 ? SHA256.HashData([.. remoteSeed, .. authHash]) : SHA256.HashData([.. remoteSeed, .. localSeed, .. authHash]);

    /// <summary>TP_SESSIONID (sent back on every request) and TIMEOUT (the session's life in seconds).</summary>
    private static (string? Cookie, TimeSpan Lifetime) ReadCookies(HttpResponseHeaders headers)
    {
        string? session = null;
        var lifetime = TimeSpan.FromDays(1);
        if (!headers.TryGetValues("Set-Cookie", out var values)) return (null, lifetime);
        foreach (var part in values.SelectMany(v => v.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var (name, value) = (part[..eq], part[(eq + 1)..]);
            if (name.Equals("TP_SESSIONID", StringComparison.OrdinalIgnoreCase)) session = $"TP_SESSIONID={value}";
            else if (name.Equals("TIMEOUT", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var seconds) && seconds > 0)
                lifetime = TimeSpan.FromSeconds(seconds);
        }
        return (session, lifetime);
    }

    // ---- requests ----

    private sealed record Session(KlapCipher Cipher, string? Cookie, DateTimeOffset Expires);

    private sealed class StaleSessionException : Exception;

    private async Task<JsonObject> PostAsync(Session session, JsonObject request, CancellationToken ct)
    {
        var (payload, seq) = session.Cipher.Encrypt(request.ToJsonString());
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, $"request?seq={seq}")) { Content = Bytes(payload) };
        if (session.Cookie is not null) message.Headers.Add("Cookie", session.Cookie);
        using var reply = await _http.SendAsync(message, ct).ConfigureAwait(false);

        if (reply.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw new StaleSessionException();
        if (reply.StatusCode != HttpStatusCode.OK) throw new KasaException($"Request refused ({(int)reply.StatusCode})", KasaFailure.Protocol);

        var body = await reply.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        string text;
        try
        {
            text = session.Cipher.Decrypt(body, seq);
        }
        catch (CryptographicException)
        {
            throw new StaleSessionException();
        }
        return KasaJson.ParseObject(text);
    }

    private static ByteArrayContent Bytes(byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }
}

/// <summary>
/// The per-session cipher: AES-128-CBC with a key, IV and signing key derived from both seeds and
/// the auth hash. The IV's last 4 bytes are a sequence number that goes up by one per request and
/// travels in the URL; each request is prefixed with a SHA-256 signature over it.
/// </summary>
internal sealed class KlapCipher
{
    private readonly byte[] _key;
    private readonly byte[] _ivPrefix;
    private readonly byte[] _signKey;
    private int _seq;

    public KlapCipher(byte[] localSeed, byte[] remoteSeed, byte[] authHash)
    {
        _key = SHA256.HashData([.. "lsk"u8, .. localSeed, .. remoteSeed, .. authHash])[..16];
        var iv = SHA256.HashData([.. "iv"u8, .. localSeed, .. remoteSeed, .. authHash]);
        _ivPrefix = iv[..12];
        _seq = BinaryPrimitives.ReadInt32BigEndian(iv.AsSpan(28));
        _signKey = SHA256.HashData([.. "ldk"u8, .. localSeed, .. remoteSeed, .. authHash])[..28];
    }

    public (byte[] Payload, int Seq) Encrypt(string text)
    {
        var seq = unchecked(++_seq);
        return (EncryptAt(text, seq), seq);
    }

    /// <summary>Encrypts at a given sequence number: how a device answers request <paramref name="seq"/>.</summary>
    public byte[] EncryptAt(string text, int seq)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(text), Iv(seq), PaddingMode.PKCS7);
        var signature = SHA256.HashData([.. _signKey, .. SeqBytes(seq), .. cipher]);
        return [.. signature, .. cipher];
    }

    /// <summary>A reply: 32 bytes of signature (not checked, as python-kasa does not), then the cipher text.</summary>
    public string Decrypt(byte[] payload, int seq)
    {
        if (payload.Length < 48) throw new CryptographicException("Reply too short");
        using var aes = Aes.Create();
        aes.Key = _key;
        return Encoding.UTF8.GetString(aes.DecryptCbc(payload.AsSpan(32), Iv(seq), PaddingMode.PKCS7));
    }

    private byte[] Iv(int seq) => [.. _ivPrefix, .. SeqBytes(seq)];

    private static byte[] SeqBytes(int seq)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, seq);
        return bytes;
    }
}
