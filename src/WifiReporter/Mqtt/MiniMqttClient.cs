using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace WifiReporter.Mqtt;

public sealed class MqttOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 1883;
    public required string ClientId { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? WillTopic { get; init; }
    public string? WillPayload { get; init; }
    public bool WillRetain { get; init; } = true;
    public ushort KeepAliveSeconds { get; init; } = 60;
}

/// <summary>
/// Minimal MQTT 3.1.1 client: CONNECT (with credentials and last will), PUBLISH at QoS 0,
/// keep-alive pings and DISCONNECT. That is all this app needs, so no external library.
/// </summary>
public sealed class MiniMqttClient : IAsyncDisposable
{
    private readonly MqttOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _loopCts;
    private Task? _readLoop;
    private Task? _pingLoop;
    private long _lastReceived;

    public MiniMqttClient(MqttOptions options) => _options = options;

    public bool IsConnected { get; private set; }

    /// <summary>Why the last connection ended on its own (not by Disconnect), for the log.</summary>
    public string? LostReason { get; private set; }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await CloseAsync().ConfigureAwait(false);

        var tcp = new TcpClient { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(_options.Host, _options.Port, timeout.Token).ConfigureAwait(false);
        }

        _tcp = tcp;
        _stream = tcp.GetStream();

        await WritePacketAsync(BuildConnect(), ct).ConfigureAwait(false);

        // CONNACK: 0x20 0x02 <flags> <return code>
        var connack = new byte[4];
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await _stream.ReadExactlyAsync(connack, timeout.Token).ConfigureAwait(false);
        }

        if (connack[0] != 0x20 || connack[1] != 0x02)
            throw new IOException($"Unexpected reply to CONNECT (0x{connack[0]:x2})");
        if (connack[3] != 0)
            throw new IOException($"MQTT broker refused the connection: {ReturnCodeText(connack[3])}");

        IsConnected = true;
        LostReason = null;
        _lastReceived = Environment.TickCount64;
        // Independent of the caller's token: the loops live until DisconnectAsync/CloseAsync,
        // so a clean DISCONNECT can still be sent after the app is asked to stop.
        _loopCts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_loopCts.Token));
        _pingLoop = Task.Run(() => PingLoopAsync(_loopCts.Token));
    }

    public async Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        if (!IsConnected)
            throw new IOException("MQTT client is not connected");

        var topicBytes = EncodeString(topic);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var body = new byte[topicBytes.Length + payloadBytes.Length];
        topicBytes.CopyTo(body, 0);
        payloadBytes.CopyTo(body, topicBytes.Length);

        var header = (byte)(0x30 | (retain ? 0x01 : 0x00)); // PUBLISH, QoS 0
        await WritePacketAsync(Frame(header, body), ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        if (IsConnected)
        {
            try
            {
                await WritePacketAsync(new byte[] { 0xE0, 0x00 }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }

        await CloseAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private async Task CloseAsync()
    {
        IsConnected = false;
        _loopCts?.Cancel();
        _stream?.Dispose();
        _tcp?.Dispose();
        foreach (var t in new[] { _readLoop, _pingLoop })
        {
            if (t is null) continue;
            try { await t.ConfigureAwait(false); } catch { /* loop ended */ }
        }

        _loopCts?.Dispose();
        _loopCts = null;
        _readLoop = _pingLoop = null;
        _stream = null;
        _tcp = null;
    }

    private async Task WritePacketAsync(byte[] packet, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("MQTT client is not connected");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(packet, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Lost($"write failed: {ex.Message}");
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.KeepAliveSeconds / 2));
        try
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);

                // A PINGRESP comes back for every PINGREQ. Silence for 1.5x the keep-alive means the connection
                // is dead even though writes still succeed (they only fill the socket buffer); the broker
                // drops the session after the same time, so reconnecting right away loses nothing.
                var silent = (Environment.TickCount64 - Interlocked.Read(ref _lastReceived)) / 1000;
                if (silent > _options.KeepAliveSeconds * 3 / 2)
                {
                    Lost($"no reply from the broker for {silent}s");
                    return;
                }

                await WritePacketAsync(new byte[] { 0xC0, 0x00 }, ct).ConfigureAwait(false); // PINGREQ
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Lost($"ping failed: {ex.Message}");
        }
        catch
        {
            // loop stopped by CloseAsync
        }
    }

    /// <summary>Drains incoming packets (PINGRESP etc.) and detects a closed connection.</summary>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var stream = _stream!;
        var one = new byte[1];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0)
                    break; // broker closed the connection

                var length = await ReadRemainingLengthAsync(stream, ct).ConfigureAwait(false);
                if (length > 0)
                {
                    var skip = new byte[length];
                    await stream.ReadExactlyAsync(skip, ct).ConfigureAwait(false);
                }

                Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
            }

            if (!ct.IsCancellationRequested)
                Lost("closed by the broker");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Lost(ex.Message);
        }
        catch
        {
            // CloseAsync stopped the loop
        }
    }

    private void Lost(string reason)
    {
        if (!IsConnected) return;
        LostReason ??= reason;
        IsConnected = false;
    }

    private static async Task<int> ReadRemainingLengthAsync(Stream stream, CancellationToken ct)
    {
        var multiplier = 1;
        var value = 0;
        var buffer = new byte[1];
        for (var i = 0; i < 4; i++)
        {
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            value += (buffer[0] & 0x7F) * multiplier;
            if ((buffer[0] & 0x80) == 0)
                return value;
            multiplier *= 128;
        }

        throw new IOException("Malformed MQTT remaining length");
    }

    private byte[] BuildConnect()
    {
        using var body = new MemoryStream();
        body.Write(EncodeString("MQTT"));
        body.WriteByte(0x04); // protocol level 3.1.1

        byte flags = 0x02; // clean session
        var hasWill = !string.IsNullOrEmpty(_options.WillTopic);
        if (hasWill)
        {
            flags |= 0x04;
            if (_options.WillRetain) flags |= 0x20;
        }

        var hasUser = !string.IsNullOrEmpty(_options.Username);
        var hasPassword = hasUser && !string.IsNullOrEmpty(_options.Password);
        if (hasUser) flags |= 0x80;
        if (hasPassword) flags |= 0x40;
        body.WriteByte(flags);

        Span<byte> keepAlive = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(keepAlive, _options.KeepAliveSeconds);
        body.Write(keepAlive);

        body.Write(EncodeString(_options.ClientId));
        if (hasWill)
        {
            body.Write(EncodeString(_options.WillTopic!));
            body.Write(EncodeString(_options.WillPayload ?? ""));
        }

        if (hasUser) body.Write(EncodeString(_options.Username!));
        if (hasPassword) body.Write(EncodeString(_options.Password!));

        return Frame(0x10, body.ToArray());
    }

    private static byte[] Frame(byte header, byte[] body)
    {
        using var packet = new MemoryStream(body.Length + 5);
        packet.WriteByte(header);

        var length = body.Length;
        do
        {
            var digit = (byte)(length % 128);
            length /= 128;
            if (length > 0) digit |= 0x80;
            packet.WriteByte(digit);
        } while (length > 0);

        packet.Write(body);
        return packet.ToArray();
    }

    private static byte[] EncodeString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
            throw new ArgumentException("MQTT string too long");
        var result = new byte[bytes.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)bytes.Length);
        bytes.CopyTo(result, 2);
        return result;
    }

    private static string ReturnCodeText(byte code) => code switch
    {
        1 => "unacceptable protocol version",
        2 => "client identifier rejected",
        3 => "server unavailable",
        4 => "bad user name or password",
        5 => "not authorized",
        _ => $"return code {code}",
    };
}
