using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace InfiniTranseon.PlatformSpike;

/// <summary>
/// --pipe: an in-process mock EngineHost named-pipe server plus client. Uses a unique pipe
/// name and PipeOptions.CurrentUserOnly (current-user-only security descriptor; .NET named
/// pipe servers additionally set PIPE_REJECT_REMOTE_CLIENTS by default). Runs a versioned
/// JSON handshake with nonce echo, demonstrates version-mismatch rejection, and reports
/// round-trip latency statistics over 100 messages. Fully self-contained; no shared contracts.
/// </summary>
internal static class PipeProbe
{
    private const string ProtocolName = "infini-platform-spike";
    private const int SupportedVersion = 1;
    private const int PingCount = 100;

    internal static int Run()
    {
        string pipeName = $"InfiniTranseon.PlatformSpike.{Guid.NewGuid():N}";
        Console.WriteLine($"probe=pipe status=starting pipeName={pipeName} protocol={ProtocolName} version={SupportedVersion}");

        using var serverReady = new ManualResetEventSlim(false);
        int serverResult = ExitCodes.Success;

        var serverTask = Task.Run(() =>
        {
            try
            {
                RunServer(pipeName, serverReady);
            }
            catch (Exception ex)
            {
                serverResult = ExitCodes.PipeError;
                Console.Error.WriteLine($"probe=pipe status=error side=server message=\"{ex.Message}\"");
            }
        });

        if (!serverReady.Wait(TimeSpan.FromSeconds(5)))
        {
            Console.Error.WriteLine("probe=pipe status=error side=client message=\"server did not become ready\"");
            return ExitCodes.PipeError;
        }

        int clientResult;
        try
        {
            clientResult = RunClient(pipeName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"probe=pipe status=error side=client message=\"{ex.Message}\"");
            clientResult = ExitCodes.PipeError;
        }

        serverTask.Wait(TimeSpan.FromSeconds(5));

        int result = clientResult != ExitCodes.Success ? clientResult : serverResult;
        Console.WriteLine($"probe=pipe status=stopped result={result}");
        return result;
    }

    // ----- Mock EngineHost server -----
    private static void RunServer(string pipeName, ManualResetEventSlim serverReady)
    {
        // Connection A: successful handshake followed by the ping/echo latency loop.
        using (var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            serverReady.Set();
            server.WaitForConnection();
            HandleHandshakeAndPings(server);
        }

        // Connection B: version-mismatch rejection.
        using (var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            server.WaitForConnection();
            HandleHandshakeAndPings(server);
        }
    }

    private static void HandleHandshakeAndPings(NamedPipeServerStream server)
    {
        byte[]? request = ReadFrame(server);
        if (request is null)
        {
            return;
        }

        using JsonDocument doc = JsonDocument.Parse(request);
        JsonElement root = doc.RootElement;
        string protocol = root.GetProperty("protocol").GetString() ?? string.Empty;
        int version = root.GetProperty("version").GetInt32();
        string nonce = root.GetProperty("nonce").GetString() ?? string.Empty;

        bool accepted = protocol == ProtocolName && version == SupportedVersion;
        if (!accepted)
        {
            string reason = protocol != ProtocolName ? "protocol-mismatch" : "version-mismatch";
            WriteFrame(server, Utf8(
                $"{{\"accepted\":false,\"reason\":\"{reason}\",\"serverVersion\":{SupportedVersion}}}"));
            return;
        }

        WriteFrame(server, Utf8(
            $"{{\"accepted\":true,\"serverVersion\":{SupportedVersion},\"nonce\":\"{nonce}\"}}"));

        // Echo ping frames until the client disconnects.
        while (true)
        {
            byte[]? ping = ReadFrame(server);
            if (ping is null)
            {
                break;
            }

            WriteFrame(server, ping);
        }
    }

    // ----- Client -----
    private static int RunClient(string pipeName)
    {
        // Successful handshake + latency measurement.
        using (var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            client.Connect(5000);

            string nonce = Guid.NewGuid().ToString("N");
            WriteFrame(client, Utf8(
                $"{{\"protocol\":\"{ProtocolName}\",\"version\":{SupportedVersion},\"nonce\":\"{nonce}\"}}"));

            byte[]? handshakeResponse = ReadFrame(client);
            if (handshakeResponse is null)
            {
                Console.Error.WriteLine("probe=pipe status=error side=client message=\"no handshake response\"");
                return ExitCodes.PipeError;
            }

            using (JsonDocument doc = JsonDocument.Parse(handshakeResponse))
            {
                JsonElement root = doc.RootElement;
                bool accepted = root.GetProperty("accepted").GetBoolean();
                string echoedNonce = root.TryGetProperty("nonce", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
                bool nonceEchoed = echoedNonce == nonce;
                Console.WriteLine($"probe=pipe handshake=good accepted={accepted} nonceEchoed={nonceEchoed}");
                if (!accepted || !nonceEchoed)
                {
                    return ExitCodes.PipeError;
                }
            }

            double[] latenciesMs = new double[PingCount];
            var sw = new Stopwatch();
            for (int i = 0; i < PingCount; i++)
            {
                sw.Restart();
                WriteFrame(client, Utf8($"{{\"seq\":{i},\"nonce\":\"{nonce}\"}}"));
                byte[]? echo = ReadFrame(client);
                sw.Stop();
                if (echo is null)
                {
                    Console.Error.WriteLine($"probe=pipe status=error side=client message=\"ping {i} lost\"");
                    return ExitCodes.PipeError;
                }

                latenciesMs[i] = sw.Elapsed.TotalMilliseconds;
            }

            PrintLatency(latenciesMs);
        }

        // Version-mismatch handshake must be rejected.
        using (var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            client.Connect(5000);
            string nonce = Guid.NewGuid().ToString("N");
            WriteFrame(client, Utf8(
                $"{{\"protocol\":\"{ProtocolName}\",\"version\":999,\"nonce\":\"{nonce}\"}}"));

            byte[]? response = ReadFrame(client);
            if (response is null)
            {
                Console.Error.WriteLine("probe=pipe status=error side=client message=\"no rejection response\"");
                return ExitCodes.PipeError;
            }

            using JsonDocument doc = JsonDocument.Parse(response);
            JsonElement root = doc.RootElement;
            bool accepted = root.GetProperty("accepted").GetBoolean();
            string reason = root.TryGetProperty("reason", out JsonElement r) ? r.GetString() ?? string.Empty : string.Empty;
            Console.WriteLine($"probe=pipe handshake=bad-version accepted={accepted} reason={reason} (expectedAccepted=False)");
            if (accepted)
            {
                return ExitCodes.PipeError;
            }
        }

        return ExitCodes.Success;
    }

    private static void PrintLatency(double[] samplesMs)
    {
        double[] sorted = (double[])samplesMs.Clone();
        Array.Sort(sorted);
        double min = sorted[0];
        double max = sorted[^1];
        double mean = samplesMs.Average();
        double p50 = Percentile(sorted, 0.50);
        double p95 = Percentile(sorted, 0.95);
        double p99 = Percentile(sorted, 0.99);
        Console.WriteLine(
            $"probe=pipe latency messages={samplesMs.Length} " +
            $"minMs={min:F3} p50Ms={p50:F3} meanMs={mean:F3} p95Ms={p95:F3} p99Ms={p99:F3} maxMs={max:F3}");
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        index = Math.Clamp(index, 0, sorted.Length - 1);
        return sorted[index];
    }

    // ----- Length-prefixed framing (4-byte little-endian length + UTF-8 payload) -----
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static void WriteFrame(Stream stream, byte[] payload)
    {
        Span<byte> header = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    private static byte[]? ReadFrame(Stream stream)
    {
        byte[] header = new byte[4];
        if (!ReadExact(stream, header, 4))
        {
            return null;
        }

        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > (1 << 20))
        {
            throw new InvalidOperationException($"frame length out of range: {length}");
        }

        byte[] payload = new byte[length];
        return ReadExact(stream, payload, length) ? payload : null;
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
