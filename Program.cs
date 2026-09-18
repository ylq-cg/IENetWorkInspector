using System.Collections.Concurrent;
using System.Reflection.PortableExecutable;
using System.Security.Principal;
using System.Text.Json;
using Windows.Foundation.Metadata;
using Windows.Storage.Streams;
using Windows.System.Diagnostics;
using Windows.Web.Http;
using Windows.Web.Http.Diagnostics;

internal static class Program
{
    private const int MaxEvents = 2000;
    private static readonly object OutputLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--ui-self-test" }))
        {
            ApplicationConfiguration.Initialize();
            return CaptureForm.RunSelfTest();
        }
        if (args.Length == 0 || args.SequenceEqual(new[] { "--ui" }))
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new CaptureForm());
            return 0;
        }
        return RunCli(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunCli(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine("IeNetworkDemo <pid> [--seconds 30] [--body-bytes 0]\n" +
                "IeNetworkDemo --ui (also the default with no arguments)\n" +
                "IeNetworkDemo --list\n" +
                "IeNetworkDemo --diagnose\n" +
                "IeNetworkDemo --probe-http-self\n" +
                "IeNetworkDemo --auto [--seconds 30] [--body-bytes 0]\n" +
                "IeNetworkDemo --self-test\n" +
                "JSON Lines to stdout; status to stderr. Ctrl+C stops capture.\n" +
                "Limits: 1-300 seconds, 0-65536 bytes/body, 8 body readers, 2000 events.\n" +
                "Bodies are opt-in. Only use approved test traffic; output may contain sensitive data.");
            return 0;
        }
        if (args.SequenceEqual(new[] { "--self-test" }))
        {
            SelfTest();
            return 0;
        }

        try
        {
            var worker = args[0] == "--worker";
            if (worker)
            {
                args = args.Skip(1).ToArray();
                if (args.Length == 0) throw new ArgumentException("Worker requires a PID.");
            }
            if (args.SequenceEqual(new[] { "--probe-http-self" }))
            {
                PrintDiagnostics();
                var selfProvider = HttpDiagnosticProvider.CreateFromProcessDiagnosticInfo(ProcessDiagnosticInfo.GetForCurrentProcess());
                Console.WriteLine($"HTTP API probe: own PID {Environment.ProcessId}; no event subscriptions or HTTP requests.");
                selfProvider.Start();
                try { Console.WriteLine("HttpDiagnosticProvider.Start(self): succeeded."); }
                finally
                {
                    selfProvider.Stop();
                    Console.WriteLine("HttpDiagnosticProvider.Stop(self): succeeded.");
                }
                return 0;
            }
            if (args.SequenceEqual(new[] { "--diagnose" }))
            {
                PrintDiagnostics();
                return 0;
            }
            if (args.SequenceEqual(new[] { "--list" }))
            {
                var candidates = FindIeProcesses();
                PrintCandidates(candidates, Console.Out);
                return candidates.Count == 0 ? 2 : 0;
            }
            if (args[0] == "--auto")
            {
                var resolvedArgs = (string[])args.Clone();
                resolvedArgs[0] = "1";
                Parse(resolvedArgs);
                var candidates = FindIeProcesses();
                PrintCandidates(candidates, Console.Error);
                if (candidates.Count == 0) return 2;
                var selectedPid = candidates[0].ProcessId;
                if (candidates.Count > 1)
                {
                    Console.Error.Write("Select a listed PID (Enter to cancel): ");
                    var answer = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(answer)) return 2;
                    if (!uint.TryParse(answer, out selectedPid) || !candidates.Any(candidate => candidate.ProcessId == selectedPid))
                        throw new ArgumentException("Select a PID from the displayed candidate list.");
                }
                Console.Error.WriteLine($"Selected PID {selectedPid}. Candidate detection does not prove this process issues the page's requests.");
                resolvedArgs[0] = selectedPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                args = resolvedArgs;
            }
            var options = Parse(args);
            PrintDiagnostics(Console.Error);
            if (!ApiInformation.IsTypePresent("Windows.Web.Http.Diagnostics.HttpDiagnosticProvider"))
                throw new InvalidOperationException("HttpDiagnosticProvider is not available on this OS.");
            var process = ProcessDiagnosticInfo.GetForProcesses()
                .FirstOrDefault(candidate => candidate.ProcessId == options.ProcessId)
                ?? throw new InvalidOperationException("PID not found or not accessible; it may have exited. Try --list or --auto to find IE candidates again.");
            using var cancellation = new CancellationTokenSource();
            if (worker)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        while (true)
                        {
                            var command = Console.ReadLine();
                            if (command is null || command.Equals("stop", StringComparison.OrdinalIgnoreCase))
                            {
                                cancellation.Cancel();
                                break;
                            }
                        }
                    }
                    catch (ObjectDisposedException) { }
                });
            }
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                return await Capture(process, options, cancellation);
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Failed: {error.GetType().Name}, HRESULT=0x{error.HResult:X8}: {error.Message}");
            if (error.HResult == unchecked((int)0x80070005))
                Console.Error.WriteLine("Access denied despite the requested administrator token. Run --diagnose to check the effective token and API availability.");
            return 1;
        }
    }

    private static void PrintDiagnostics(TextWriter? output = null)
    {
        output ??= Console.Out;
        output.WriteLine($"Demo build: metadata-v2; assembly={typeof(Program).Assembly.Location}; module={typeof(Program).Assembly.ManifestModule.ModuleVersionId}");
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        output.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        output.WriteLine($"Process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        output.WriteLine($"Enabled administrator role: {principal.IsInRole(WindowsBuiltInRole.Administrator)}");
        output.WriteLine($"Enabled Performance Log Users role: {principal.IsInRole(new SecurityIdentifier("S-1-5-32-559"))}");
        output.WriteLine($"HttpDiagnosticProvider type present: {ApiInformation.IsTypePresent("Windows.Web.Http.Diagnostics.HttpDiagnosticProvider")}");
        output.WriteLine("The token/API check above is read-only; the selected capture/probe may start ETW afterward. Enabled roles do not prove provider/target access.");
    }

    private static bool IsBrowserCandidate(string name) =>
        name.Equals("iexplore", StringComparison.OrdinalIgnoreCase)
        || name.Equals("msedge", StringComparison.OrdinalIgnoreCase);

    internal static List<IeCandidate> FindIeExecutableProcesses()
    {
        var candidates = new List<IeCandidate>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("iexplore"))
        {
            using (process)
            {
                try { candidates.Add(new IeCandidate((uint)process.Id, "iexplore", ProcessArchitecture(process), "New IE executable detected before module inspection")); }
                catch (InvalidOperationException) { }
            }
        }
        return candidates;
    }

    private static string ProcessArchitecture(System.Diagnostics.Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(path)) return "Unknown";
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            return reader.PEHeaders.CoffHeader.Machine switch
            {
                Machine.I386 => "x86",
                Machine.Amd64 => "x64",
                Machine.Arm64 => "ARM64",
                Machine.Arm or Machine.ArmThumb2 => "ARM",
                var machine => $"0x{(ushort)machine:X4}"
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException or BadImageFormatException)
        {
            return "Unknown";
        }
    }

    internal static List<IeCandidate> FindIeProcesses(bool reportSkipped = true)
    {
        var candidates = new List<IeCandidate>();
        var skipped = 0;
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (!IsBrowserCandidate(name)) continue;
                    if (name.Equals("iexplore", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(new IeCandidate((uint)process.Id, name, ProcessArchitecture(process), "IE executable; may be broker or content process"));
                    }
                    else if (process.Modules.Cast<System.Diagnostics.ProcessModule>()
                        .Any(module => module.ModuleName.Equals("mshtml.dll", StringComparison.OrdinalIgnoreCase)))
                    {
                        candidates.Add(new IeCandidate((uint)process.Id, name, ProcessArchitecture(process), "MSHTML module loaded"));
                    }
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception
                    or InvalidOperationException or NotSupportedException)
                {
                    skipped++;
                }
            }
        }
        if (reportSkipped && skipped != 0)
            Console.Error.WriteLine($"Skipped {skipped} inaccessible/exited process inspections. Enumeration may be incomplete despite the administrator token.");
        return candidates.OrderBy(candidate => candidate.ProcessId).ToList();
    }

    private static void PrintCandidates(IReadOnlyList<IeCandidate> candidates, TextWriter output)
    {
        output.WriteLine("PID\tPROCESS\tARCH\tDETECTION");
        foreach (var candidate in candidates)
            output.WriteLine($"{candidate.ProcessId}\t{candidate.Name}\t{candidate.Architecture}\t{candidate.Reason}");
        if (candidates.Count == 0)
            output.WriteLine("No IE candidates found. Open a page in Edge IE mode first. Module access or architecture restrictions can hide candidates; an explicit PID is still supported.");
        else
            output.WriteLine("Candidates only, not tab identification or ETW access verification. No URLs or page content were inspected.");
    }

    private static async Task<int> Capture(ProcessDiagnosticInfo process, Options options, CancellationTokenSource cancellation)
    {
        var provider = HttpDiagnosticProvider.CreateFromProcessDiagnosticInfo(process);
        var gate = new object();
        using var readers = new SemaphoreSlim(8);
        var bodyTasks = new List<Task>();
        var accepting = true;
        long eventCount = 0;
        long bodyCount = 0;
        var eventErrors = 0;
        var outputErrors = new ConcurrentQueue<Exception>();

        void OnEvent(Action action)
        {
            lock (gate)
            {
                if (!accepting || (!options.Continuous && eventCount >= MaxEvents)) return;
                eventCount++;
                try { action(); }
                catch (Exception error)
                {
                    eventErrors++;
                    Console.Error.WriteLine($"Event processing failed: 0x{error.HResult:X8}");
                    cancellation.Cancel();
                }
                if (!options.Continuous && eventCount >= MaxEvents) cancellation.Cancel();
            }
        }

        void ReadBody(Guid activityId, string direction, IHttpContent? content)
        {
            if (options.BodyBytes == 0 || content is null) return;
            if (!readers.Wait(0))
            {
                Write(new { kind = "body", activityId, direction, state = "skipped-concurrency-limit" });
                return;
            }
            bodyTasks.RemoveAll(task => task.IsCompleted);
            bodyCount++;
            bodyTasks.Add(ReadBodyAsync(activityId, direction, content, options.BodyBytes,
                readers, cancellation.Token, outputErrors));
        }

        provider.RequestSent += (_, eventArgs) => OnEvent(() =>
        {
            ReadBody(eventArgs.ActivityId, "request", eventArgs.Message.Content);
            Write(new { kind = "request", eventArgs.ActivityId, eventArgs.Timestamp,
                method = eventArgs.Message.Method.Method, url = CaptureUrl(eventArgs.Message.RequestUri),
                headers = CaptureHeaders(eventArgs.Message.Headers),
                contentHeaders = CaptureHeaders(eventArgs.Message.Content?.Headers) });
        });
        provider.ResponseReceived += (_, eventArgs) => OnEvent(() =>
        {
            ReadBody(eventArgs.ActivityId, "response", eventArgs.Message.Content);
            Write(new { kind = "response", eventArgs.ActivityId, eventArgs.Timestamp,
                status = (int)eventArgs.Message.StatusCode,
                headers = CaptureHeaders(eventArgs.Message.Headers),
                contentHeaders = CaptureHeaders(eventArgs.Message.Content?.Headers) });
        });
        provider.RequestResponseCompleted += (_, eventArgs) => OnEvent(() =>
        {
            var timing = eventArgs.Timestamps;
            Write(new { kind = "completed", eventArgs.ActivityId,
                url = CaptureUrl(eventArgs.RequestedUri), eventArgs.ProcessId,
                timing.CacheCheckedTimestamp, timing.ConnectionInitiatedTimestamp,
                timing.NameResolvedTimestamp, timing.SslNegotiatedTimestamp,
                timing.ConnectionCompletedTimestamp, timing.RequestSentTimestamp,
                timing.RequestCompletedTimestamp, timing.ResponseReceivedTimestamp,
                timing.ResponseCompletedTimestamp });
        });

        var started = false;
        Exception? stopError = null;
        try
        {
            Console.Error.WriteLine(options.Continuous
                ? $"Starting PID {options.ProcessId}; manual stop; body capture enabled without a configured size/time limit. No proxy or certificate changes."
                : $"Starting PID {options.ProcessId}; {options.Seconds}s; body limit {options.BodyBytes} bytes. No proxy or certificate changes.");
            provider.Start();
            started = true;
            Console.Error.WriteLine("Start returned successfully. Reproduce the request now; this does not prove events are being delivered.");
            try { await Task.Delay(options.Continuous ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(options.Seconds), cancellation.Token); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            lock (gate) { accepting = false; }
            cancellation.Cancel();
            if (started)
            {
                try { provider.Stop(); }
                catch (Exception error) { stopError = error; Console.Error.WriteLine($"Stop failed: 0x{error.HResult:X8}: {error.Message}"); }
            }
            await Task.WhenAll(bodyTasks);
            var status = !started ? "Capture did not start; Stop was skipped."
                : stopError is null ? "Stopped." : "Stop failed; cleanup is not confirmed.";
            Console.Error.WriteLine($"{status} Events={eventCount}; body operations={bodyCount}; event errors={eventErrors}; output errors={outputErrors.Count}. No automatic reattach or historical backfill.");
        }
        if (stopError is not null || eventErrors != 0 || !outputErrors.IsEmpty) return 1;
        if (eventCount == 0)
        {
            Console.Error.WriteLine("No events received. Verify target PID, fresh WinINet traffic, permissions and provider availability. Empty capture is not success.");
            return 2;
        }
        return 0;
    }

    private static async Task ReadBodyAsync(Guid activityId, string direction, IHttpContent content,
        int limit, SemaphoreSlim readers, CancellationToken captureToken, ConcurrentQueue<Exception> outputErrors)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(captureToken);
        if (limit >= 0) timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var input = await content.ReadAsInputStreamAsync().AsTask(timeout.Token);
            using var stream = input.AsStreamForRead();
            if (limit < 0)
            {
                var result = await StreamBody(stream, timeout.Token, (sequence, bytes) =>
                    Write(new { kind = "body-chunk", activityId, direction, sequence, encoding = "base64", bytes = bytes.Length, data = Convert.ToBase64String(bytes.Span) }));
                Write(new { kind = "body", activityId, direction, encoding = "base64-chunks",
                    bytes = result.Bytes, chunks = result.Chunks, truncated = false, streamEnded = true });
                return;
            }
            var (payload, reachedEnd) = await ReadPayload(stream, limit, timeout.Token);
            var length = limit < 0 ? payload.Length : Math.Min(payload.Length, limit);
            Write(new { kind = "body", activityId, direction, encoding = "base64",
                bytes = length, truncated = limit >= 0 && payload.Length > limit,
                streamEnded = reachedEnd, data = Convert.ToBase64String(payload, 0, length) });
        }
        catch (Exception error)
        {
            try { Write(new { kind = "body", activityId, direction,
                state = error is OperationCanceledException ? "cancelled-or-timeout" : "unavailable",
                hresult = $"0x{error.HResult:X8}" }); }
            catch (Exception outputError) { outputErrors.Enqueue(outputError); }
        }
        finally { readers.Release(); }
    }

    private static string CaptureUrl(Uri? uri) => uri?.AbsoluteUri ?? "";

    private static Dictionary<string, string> CaptureHeaders(IEnumerable<KeyValuePair<string, string>>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null) return result;
        foreach (var header in headers) result[header.Key] = header.Value;
        return result;
    }

    private static void Write<T>(T value)
    {
        lock (OutputLock) { Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions)); }
    }

    private static Options Parse(string[] args)
    {
        if (!uint.TryParse(args[0], out var processId) || processId == 0)
            throw new ArgumentException("First argument must be a positive PID.");
        if (args.Length == 2 && args[1] == "--continuous") return new Options(processId, 0, -1, true);
        var seconds = 30;
        var bodyBytes = 0;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var value))
                throw new ArgumentException("Options require an integer value.");
            switch (args[index])
            {
                case "--seconds": seconds = value; break;
                case "--body-bytes": bodyBytes = value; break;
                default: throw new ArgumentException($"Unknown option: {args[index]}");
            }
        }
        if (seconds is < 1 or > 300 || bodyBytes is < 0 or > 65536)
            throw new ArgumentException("Seconds must be 1-300; body-bytes must be 0-65536.");
        return new Options(processId, seconds, bodyBytes);
    }

    private static void SelfTest()
    {
        CaptureJournal.SelfTest();
        var continuous = Parse(new[] { "123", "--continuous" });
        if (!continuous.Continuous || continuous.BodyBytes != -1 || continuous.Seconds != 0)
            throw new Exception("Continuous capture configuration test failed.");
        var sample = new byte[100000];
        Random.Shared.NextBytes(sample);
        using (var source = new MemoryStream(sample))
        {
            using var reconstructed = new MemoryStream();
            var sequenceExpected = 0;
            var streamed = StreamBody(source, CancellationToken.None, (sequence, chunk) =>
            {
                if (sequence != sequenceExpected++ || chunk.Length > 32768) throw new Exception("Body chunk order/size failed.");
                reconstructed.Write(chunk.Span);
            }).GetAwaiter().GetResult();
            if (streamed.Bytes != sample.Length || streamed.Chunks != sequenceExpected || !reconstructed.ToArray().SequenceEqual(sample))
                throw new Exception("Streamed body reconstruction failed.");
            source.Position = 0;
            var full = ReadPayload(source, -1, CancellationToken.None).GetAwaiter().GetResult();
            if (!full.Ended || !full.Payload.SequenceEqual(sample)) throw new Exception("Unlimited body test failed.");
            source.Position = 0;
            var capped = ReadPayload(source, 16384, CancellationToken.None).GetAwaiter().GetResult();
            if (capped.Ended || capped.Payload.Length != 16385) throw new Exception("CLI body limit test failed.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { StreamBody(source, cancelled.Token, (_, _) => { }).GetAwaiter().GetResult(); throw new Exception("Chunk cancellation ignored."); }
            catch (OperationCanceledException) { }
            try { ReadPayload(source, -1, cancelled.Token).GetAwaiter().GetResult(); throw new Exception("Body cancellation ignored."); }
            catch (OperationCanceledException) { }
        }
        if (typeof(Program).Assembly.GetTypes().SelectMany(type => type.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            .Any(method => (method.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) != 0))
            throw new Exception("Unexpected direct native import in demo assembly; review public API usage.");
        if (!IsBrowserCandidate("IEXPLORE") || !IsBrowserCandidate("msedge")
            || IsBrowserCandidate("msedgewebview2") || IsBrowserCandidate("notepad"))
            throw new Exception("Browser candidate filter test failed.");
        using (var currentProcess = System.Diagnostics.Process.GetCurrentProcess())
        {
            var architecture = ProcessArchitecture(currentProcess);
            if (architecture is not ("x86" or "x64" or "ARM" or "ARM64"))
                throw new Exception($"Process architecture detection failed: {architecture}.");
        }
        if (Parse(new[] { "123", "--seconds", "1" }) != new Options(123, 1, 0))
            throw new Exception("Options test failed.");
        foreach (var invalid in new[] { new[] { "0" }, new[] { "123", "--seconds", "0" },
            new[] { "123", "--body-bytes", "65537" }, new[] { "123", "--unknown", "1" } })
        {
            try { Parse(invalid); }
            catch (ArgumentException) { continue; }
            throw new Exception("Invalid options accepted.");
        }
        var longHeader = new string('x', 1024);
        var headers = CaptureHeaders(new Dictionary<string, string> { ["Cookie"] = "session=secret", ["Authorization"] = "Bearer secret", ["X-Long"] = longHeader });
        if (headers["Cookie"] != "session=secret" || headers["Authorization"] != "Bearer secret" || headers["X-Long"] != longHeader)
            throw new Exception("Full header capture test failed.");
        if (CaptureUrl(new Uri("https://user:secret@example.test/path?token=secret#fragment")) != "https://user:secret@example.test/path?token=secret#fragment")
            throw new Exception("URL test failed.");
        if (JsonSerializer.Serialize(new { ActivityId = "test" }, JsonOptions) != "{\"activityId\":\"test\"}")
            throw new Exception("JSON field naming test failed.");
        Console.WriteLine("PASS: candidate filter, options, bounds, full headers and URL capture. No capture was started.");
    }

    internal sealed record IeCandidate(uint ProcessId, string Name, string Architecture, string Reason);
    private static async Task<(long Bytes, long Chunks)> StreamBody(Stream input, CancellationToken cancellation, Action<long, ReadOnlyMemory<byte>> emit)
    {
        var buffer = new byte[32768];
        long bytes = 0;
        long sequence = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = await input.ReadAsync(buffer, cancellation);
            if (count == 0) return (bytes, sequence);
            emit(sequence++, buffer.AsMemory(0, count));
            bytes += count;
        }
    }

    private static async Task<(byte[] Payload, bool Ended)> ReadPayload(Stream input, int limit, CancellationToken cancellation)
    {
        using var collected = new MemoryStream();
        var buffer = new byte[8192];
        while (limit < 0 || collected.Length < (long)limit + 1)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = limit < 0 ? buffer.Length : (int)Math.Min(buffer.Length, (long)limit + 1 - collected.Length);
            var read = await input.ReadAsync(buffer.AsMemory(0, count), cancellation);
            if (read == 0) return (collected.ToArray(), true);
            collected.Write(buffer, 0, read);
        }
        return (collected.ToArray(), false);
    }

    private sealed record Options(uint ProcessId, int Seconds, int BodyBytes, bool Continuous = false);
}