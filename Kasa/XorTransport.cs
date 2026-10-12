using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.Kasa.Kasa;

/// <summary>
/// The original Kasa protocol, still spoken by most KP105/KP303 firmware: JSON over TCP port 9999,
/// "encrypted" with an autokey XOR (each cipher byte is the key for the next) and prefixed with a
/// 4-byte big-endian length. Discovery uses the same cipher over UDP, without the length.
/// </summary>
internal sealed class XorTransport(IPAddress address, int port = XorTransport.DefaultPort) : IKasaTransport
{
    public const int DefaultPort = 9999;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);
    private const int MaxResponse = 64 * 1024;

    public string Name => "XOR";

    public async Task<JsonObject> SendAsync(JsonObject request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            // A fresh connection per request: plugs close idle sockets, and one-shot is what they handle best.
            using var client = new TcpClient(address.AddressFamily) { NoDelay = true };
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();

            await stream.WriteAsync(Frame(request.ToJsonString()), timeout.Token).ConfigureAwait(false);

            var header = new byte[4];
            await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length is <= 0 or > MaxResponse) throw new KasaException($"Odd reply length {length}", KasaFailure.Protocol);

            var body = new byte[length];
            await stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
            return KasaJson.ParseObject(Decrypt(body));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new KasaException("No answer", KasaFailure.Offline);
        }
        catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException)
        {
            throw new KasaException("No answer", KasaFailure.Offline, ex);
        }
    }

    /// <summary>The TCP form: 4-byte big-endian length, then the XOR-encrypted text.</summary>
    public static byte[] Frame(string json)
    {
        var body = Encrypt(json);
        var framed = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(framed, body.Length);
        body.CopyTo(framed, 4);
        return framed;
    }

    public static byte[] Encrypt(string text)
    {
        var plain = Encoding.UTF8.GetBytes(text);
        var key = (byte)171;
        for (var i = 0; i < plain.Length; i++)
        {
            key ^= plain[i];
            plain[i] = key;
        }
        return plain;
    }

    public static string Decrypt(ReadOnlySpan<byte> cipher)
    {
        var plain = new byte[cipher.Length];
        var key = (byte)171;
        for (var i = 0; i < cipher.Length; i++)
        {
            plain[i] = (byte)(key ^ cipher[i]);
            key = cipher[i];
        }
        return Encoding.UTF8.GetString(plain);
    }

    public void Dispose()
    {
    }
}
