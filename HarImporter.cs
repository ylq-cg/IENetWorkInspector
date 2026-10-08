using System.Text;
using System.Text.Json;

internal sealed record HarImportResult(IReadOnlyList<string> Records, int Entries);

internal static class HarImporter
{
    public static HarImportResult Read(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(input);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("log", out var log) || log.ValueKind != JsonValueKind.Object
                || !log.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The file is not a HAR document with a log.entries array.");

            var records = new List<string>();
            var activityIds = new HashSet<string>(StringComparer.Ordinal);
            var entryIndex = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                entryIndex++;
                if (entry.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"HAR entry {entryIndex} is not an object.");
                var request = Object(entry, "request")
                    ?? throw new InvalidDataException($"HAR entry {entryIndex} has no request object.");
                var url = String(request, "url");
                if (string.IsNullOrWhiteSpace(url))
                    throw new InvalidDataException($"HAR entry {entryIndex} has no request URL.");
                var activityId = String(entry, "_activityId") ?? $"har-{entryIndex}";
                if (!activityIds.Add(activityId))
                {
                    activityId = activityId + "-" + entryIndex;
                    activityIds.Add(activityId);
                }
                var started = DateTime(entry, "startedDateTime");
                var requestHeaders = Headers(request, true);
                var requestContentHeaders = ContentHeaders(requestHeaders);
                records.Add(JsonSerializer.Serialize(new
                {
                    kind = "request",
                    activityId,
                    timestamp = started,
                    method = String(request, "method") ?? "UNKNOWN",
                    url,
                    headers = requestHeaders,
                    contentHeaders = requestContentHeaders
                }));
                if (BodyRecord(activityId, "request", Object(request, "postData")) is { } requestBody)
                    records.Add(requestBody);

                var response = Object(entry, "response");
                if (response is JsonElement responseValue)
                {
                    var responseHeaders = Headers(responseValue, false);
                    var content = Object(responseValue, "content");
                    var responseContentHeaders = ContentHeaders(responseHeaders);
                    if (String(content, "mimeType") is { Length: > 0 } mimeType)
                        responseContentHeaders.TryAdd("Content-Type", mimeType);
                    records.Add(JsonSerializer.Serialize(new
                    {
                        kind = "response",
                        activityId,
                        timestamp = ResponseTimestamp(entry, started),
                        status = Int32(responseValue, "status") ?? 0,
                        headers = responseHeaders,
                        contentHeaders = responseContentHeaders
                    }));
                    if (BodyRecord(activityId, "response", content) is { } responseBody)
                        records.Add(responseBody);
                }

                var totalMilliseconds = Number(entry, "time");
                var completed = started is not null && totalMilliseconds is >= 0
                    ? started.Value.AddMilliseconds(totalMilliseconds.Value) : (DateTimeOffset?)null;
                records.Add(JsonSerializer.Serialize(new
                {
                    kind = "completed",
                    activityId,
                    url,
                    requestSentTimestamp = started,
                    requestCompletedTimestamp = (DateTimeOffset?)null,
                    responseReceivedTimestamp = ResponseTimestamp(entry, started),
                    responseCompletedTimestamp = completed
                }));
            }
            if (entryIndex == 0) throw new InvalidDataException("The HAR file contains no entries.");
            return new HarImportResult(records, entryIndex);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid HAR JSON: " + error.Message, error);
        }
    }

    public static void ExportJsonl(string harPath, string destination)
    {
        if (string.Equals(Path.GetFullPath(harPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a JSONL destination different from the HAR source.");
        var import = Read(harPath);
        var temporary = Path.GetFullPath(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new StreamWriter(output, new UTF8Encoding(false), 65536))
            {
                foreach (var record in import.Records) writer.WriteLine(record);
                writer.Flush();
                output.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static Dictionary<string, string> Headers(JsonElement message, bool request)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (message.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
        {
            foreach (var header in headers.EnumerateArray())
            {
                var name = String(header, "name");
                if (string.IsNullOrEmpty(name)) continue;
                Add(result, name, String(header, "value") ?? "");
            }
        }
        if (message.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array)
        {
            var values = cookies.EnumerateArray()
                .Select(cookie => Cookie(cookie, request))
                .Where(value => !string.IsNullOrEmpty(value))
                .ToArray();
            var headerName = request ? "Cookie" : "Set-Cookie";
            if (values.Length > 0 && !result.ContainsKey(headerName))
                result[headerName] = string.Join(request ? "; " : "\r\n", values);
        }
        return result;
    }

    private static string Cookie(JsonElement cookie, bool request)
    {
        var name = String(cookie, "name");
        if (string.IsNullOrEmpty(name)) return "";
        var value = name + "=" + (String(cookie, "value") ?? "");
        if (request) return value;
        foreach (var attribute in new[] { "path", "domain", "expires", "sameSite" })
            if (String(cookie, attribute) is { Length: > 0 } attributeValue)
                value += "; " + char.ToUpperInvariant(attribute[0]) + attribute[1..] + "=" + attributeValue;
        if (Boolean(cookie, "httpOnly") == true) value += "; HttpOnly";
        if (Boolean(cookie, "secure") == true) value += "; Secure";
        return value;
    }

    private static void Add(Dictionary<string, string> headers, string name, string value)
    {
        if (headers.TryGetValue(name, out var existing)) headers[name] = existing + "\r\n" + value;
        else headers[name] = value;
    }

    private static Dictionary<string, string> ContentHeaders(Dictionary<string, string> headers)
    {
        var content = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in headers.Keys.Where(name => name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            content[name] = headers[name];
            headers.Remove(name);
        }
        return content;
    }

    private static string? BodyRecord(string activityId, string direction, JsonElement? payload)
    {
        if (payload is not JsonElement value || String(value, "text") is not { } text) return null;
        byte[] bytes;
        var encoding = String(value, "encoding") ?? String(value, "_encoding");
        try { bytes = encoding?.Equals("base64", StringComparison.OrdinalIgnoreCase) == true ? Convert.FromBase64String(text) : Encoding.UTF8.GetBytes(text); }
        catch (FormatException error) { throw new InvalidDataException($"HAR {direction} body contains invalid Base64.", error); }
        return JsonSerializer.Serialize(new
        {
            kind = "body",
            activityId,
            direction,
            encoding = "base64",
            bytes = bytes.Length,
            truncated = false,
            streamEnded = true,
            data = Convert.ToBase64String(bytes)
        });
    }

    private static DateTimeOffset? ResponseTimestamp(JsonElement entry, DateTimeOffset? started)
    {
        if (started is null) return null;
        var total = Number(entry, "time");
        var receive = Number(Object(entry, "timings"), "receive");
        if (total is >= 0 && receive is >= 0 && receive <= total)
            return started.Value.AddMilliseconds(total.Value - receive.Value);
        return started;
    }

    private static JsonElement? Object(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? String(JsonElement? parent, string name) => parent is JsonElement value
        && value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static int? Int32(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var property) && property.TryGetInt32(out var number) ? number : null;

    private static double? Number(JsonElement? parent, string name) => parent is JsonElement value
        && value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.TryGetDouble(out var number) ? number : null;

    private static bool? Boolean(JsonElement parent, string name) => parent.TryGetProperty(name, out var property)
        && property.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.GetBoolean() : null;

    private static DateTimeOffset? DateTime(JsonElement parent, string name) => parent.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String && property.TryGetDateTimeOffset(out var timestamp) ? timestamp : null;
}