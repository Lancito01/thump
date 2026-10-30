using System.Net;
using System.Net.Sockets;
using System.Text;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Thump.Hue;

/// <summary>
/// Streams colours to an Entertainment area using the Hue Entertainment API (HueStream v2 over DTLS 1.2 PSK, UDP 2100).
/// </summary>
public sealed class EntertainmentStreamer : IDisposable
{
    const int Port = 2100;
    const int MaxChannels = 20;

    readonly UdpClient _udp;
    readonly DtlsTransport _dtls;
    readonly byte[] _header;
    readonly byte[] _buffer;
    byte _sequence;

    EntertainmentStreamer(UdpClient udp, DtlsTransport dtls, string areaId)
    {
        _udp = udp;
        _dtls = dtls;
        var h = new List<byte>();
        h.AddRange(Encoding.ASCII.GetBytes("HueStream"));
        h.AddRange([0x02, 0x00]); // API version 2.0
        h.Add(0x00);              // sequence id (patched per frame)
        h.AddRange([0x00, 0x00]); // reserved
        h.Add(0x00);              // colour space: RGB
        h.Add(0x00);              // reserved
        h.AddRange(Encoding.ASCII.GetBytes(areaId)); // 36-char UUID
        _header = h.ToArray();
        _buffer = new byte[_header.Length + MaxChannels * 7];
    }

    public static EntertainmentStreamer Connect(string bridgeIp, string applicationId, string clientKeyHex, string areaId)
    {
        var udp = new UdpClient();
        udp.Connect(IPAddress.Parse(bridgeIp), Port);
        try
        {
            var client = new HuePskClient(applicationId, Convert.FromHexString(clientKeyHex));
            var dtls = new DtlsClientProtocol().Connect(client, new UdpDatagramTransport(udp));
            return new EntertainmentStreamer(udp, dtls, areaId);
        }
        catch (Exception e)
        {
            udp.Dispose();
            throw new InvalidOperationException(
                $"DTLS handshake with the bridge failed ({e.Message}). Check that nothing else (Hue Sync, another Thump) is streaming to this area, and try pairing again.", e);
        }
    }

    /// <summary>Sends one frame. Colours are linear 0..1 RGB per channel id.</summary>
    public void Send(IReadOnlyList<(int Channel, double R, double G, double B)> colors)
    {
        Array.Copy(_header, _buffer, _header.Length);
        _buffer[11] = _sequence++;
        var o = _header.Length;
        foreach (var (ch, r, g, b) in colors.Take(MaxChannels))
        {
            _buffer[o++] = (byte)ch;
            Write16(r, ref o);
            Write16(g, ref o);
            Write16(b, ref o);
        }
        _dtls.Send(_buffer, 0, o);
    }

    void Write16(double v, ref int o)
    {
        var u = (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535);
        _buffer[o++] = (byte)(u >> 8);
        _buffer[o++] = (byte)u;
    }

    public void Dispose()
    {
        try { _dtls.Close(); } catch { /* bridge may already be gone */ }
        _udp.Dispose();
    }

    sealed class HuePskClient(string identity, byte[] psk)
        : PskTlsClient(new BcTlsCrypto(new SecureRandom()), new BasicTlsPskIdentity(identity, psk))
    {
        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();
        protected override int[] GetSupportedCipherSuites() => [CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256];
        // Without this the handshake retries for a very long time when the bridge doesn't answer.
        public override int GetHandshakeTimeoutMillis() => 6000;
    }

    sealed class UdpDatagramTransport(UdpClient udp) : DatagramTransport
    {
        const int Mtu = 1400;

        public int GetReceiveLimit() => Mtu;
        public int GetSendLimit() => Mtu;

        public int Receive(byte[] buf, int off, int len, int waitMillis) => Receive(buf.AsSpan(off, len), waitMillis);

        public int Receive(Span<byte> buffer, int waitMillis)
        {
            udp.Client.ReceiveTimeout = Math.Max(1, waitMillis);
            try
            {
                return udp.Client.Receive(buffer);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                return -1;
            }
        }

        public void Send(byte[] buf, int off, int len) => udp.Client.Send(buf, off, len, SocketFlags.None);
        public void Send(ReadOnlySpan<byte> buffer) => udp.Client.Send(buffer);
        public void Close() => udp.Close();
    }
}
