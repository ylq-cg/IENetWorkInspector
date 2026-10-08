using System.Globalization;
using System.Text;
using System.Text.Json;

internal static class HarExporter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void Export(string jsonlPath, string destination)
    {
        if (string.Equals(Path.GetFullPath(jsonlPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a HAR destination different from the JSONL source.");
        var temporary = Path.GetFullPath(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan))
            using (var reader = new StreamReader(input, Encoding.UTF8, true, 65536))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                Convert(reader, output);
                output.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void Convert(TextReader reader, Stream output)
    {
        var sessions = new Dictionary<string, HarSession>(StringComparer.Ordinal);
        var ordered = new List<HarSession>();
        long lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement record;
            try
            {
                using var document = JsonDocument.Parse(line);
                record = document.RootElement.Clone();
            }
            catch (JsonException error)
            {
                throw new InvalidDataException($"Invalid JSONL at line {lineNumber}: {error.Message}", error);
            }
            if (record.ValueKind != JsonValueKind.Object
                || !record.TryGetProperty("activityId", out var activityValue)
                || activityValue.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(activityValue.GetString())
                || !record.TryGetProperty("kind", out var kindValue)
                || kindValue.ValueKind != JsonValueKind.String) continue;
            var activityId = activityValue.GetString()!;
            if (!sessions.TryGetValue(activityId, out var session))
            {
                session = new HarSession(activityId);
                sessions.Add(activityId, session);
                ordered.Add(session);
            }
            var kind = kindValue.GetString();
            switch (kind)
            {
                case "request": session.Request = record; break;
                case "response": session.Response = record; break;
                case "completed": session.Completed = record; break;
                case "body": Body(session, record).Summary = record; break;
                case "body-chunk":
                    var body = Body(session, record);
                    if (!record.TryGetProperty("sequence", out var sequenceValue) || !sequenceValue.TryGetInt64(out var sequence)
                        || !record.TryGetProperty("data", out var dataValue) || dataValue.ValueKind != JsonValueKind.String) break;
                    try { body.Chunks[sequence] = System.Convert.FromBase64String(dataValue.GetString() ?? ""); }
                    catch (FormatException) { body.InvalidBase64 = true; }
                    break;
            }
        }

        var exportable = ordered.Where(session => StartTime(session) is not null && !string.IsNullOrEmpty(Url(session))).ToArray();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WritePropertyName("log");
        writer.WriteStartObject();
        writer.WriteString("version", "1.2");
        writer.WritePropertyName("creator");
        writer.WriteStartObject();
        writer.WriteString("name", "IE Network Inspector");
        writer.WriteString("version", typeof(HarExporter).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        writer.WriteEndObject();
        if (exportable.Length != ordered.Count)
            writer.WriteString("comment", $"{ordered.Count - exportable.Length} partial session(s) without a URL or timestamp were omitted.");
        writer.WritePropertyName("entries");
        writer.WriteStartArray();
        foreach (var session in exportable) WriteEntry(writer, session);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
    }

    private static BodyCapture Body(HarSession session, JsonElement record)
    {
        var direction = String(record, "direction");
        return direction == "request" ? session.RequestBody : session.ResponseBody;
    }

    private static void WriteEntry(Utf8JsonWriter writer, HarSession session)
    {
        var requestBody = ReadBody(session.RequestBody);
        var responseBody = ReadBody(session.ResponseBody);
        var started = StartTime(session)!.Value;
        var requestHeaders = Headers(session.Request);
        var responseHeaders = Headers(session.Response);
        var warnings = new[] { requestBody.Warning, responseBody.Warning }.Where(value => !string.IsNullOrEmpty(value)).ToArray();

        writer.WriteStartObject();
        writer.WriteString("startedDateTime", started.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteNumber("time", TotalTime(session));
        writer.WritePropertyName("request");
        writer.WriteStartObject();
        writer.WriteString("method", String(session.Request, "method") ?? "UNKNOWN");
        writer.WriteString("url", Url(session));
        writer.WriteString("httpVersion", "");
        WriteNameValueArray(writer, "cookies", Array.Empty<(string, string)>());
        WriteNameValueArray(writer, "headers", requestHeaders);
        WriteQuery(writer, Url(session));
        writer.WriteNumber("headersSize", -1);
        writer.WriteNumber("bodySize", requestBody.Bytes?.LongLength ?? -1);
        if (requestBody.Bytes is not null) WritePostData(writer, requestBody, ContentType(session.Request));
        writer.WriteEndObject();

        writer.WritePropertyName("response");
        writer.WriteStartObject();
        writer.WriteNumber("status", Int32(session.Response, "status") ?? 0);
        writer.WriteString("statusText", "");
        writer.WriteString("httpVersion", "");
        WriteNameValueArray(writer, "cookies", Array.Empty<(string, string)>());
        WriteNameValueArray(writer, "headers", responseHeaders);
        writer.WritePropertyName("content");
        WriteContent(writer, responseBody, ContentType(session.Response));
        writer.WriteString("redirectURL", Header(responseHeaders, "Location") ?? "");
        writer.WriteNumber("headersSize", -1);
        writer.WriteNumber("bodySize", responseBody.Bytes?.LongLength ?? -1);
        writer.WriteEndObject();
        writer.WritePropertyName("cache");
        writer.WriteStartObject();
        writer.WriteEndObject();
        WriteTimings(writer, session);
        writer.WriteString("_activityId", session.ActivityId);
        if (warnings.Length > 0) writer.WriteString("_captureWarnings", string.Join(" ", warnings));
        writer.WriteEndObject();
    }

    private static void WritePostData(Utf8JsonWriter writer, BodyContent body, string? contentType)
    {
        writer.WritePropertyName("postData");
        writer.WriteStartObject();
        writer.WriteString("mimeType", contentType ?? "");
        var (text, encoding) = BodyText(body.Bytes!, contentType);
        writer.WriteString("text", text);
        if (encoding is not null) writer.WriteString("_encoding", encoding);
        if (body.Warning is not null) writer.WriteString("comment", body.Warning);
        writer.WriteEndObject();
    }

    private static void WriteContent(Utf8JsonWriter writer, BodyContent body, string? contentType)
    {
        writer.WriteStartObject();
        writer.WriteNumber("size", body.Bytes?.LongLength ?? -1);
        writer.WriteString("mimeType", contentType ?? "");
        if (body.Bytes is not null)
        {
            var (text, encoding) = BodyText(body.Bytes, contentType);
            writer.WriteString("text", text);
            if (encoding is not null) writer.WriteString("encoding", encoding);
        }
        if (body.Warning is not null) writer.WriteString("comment", body.Warning);
        writer.WriteEndObject();
    }

    private static (string Text, string? Encoding) BodyText(byte[] bytes, string? contentType)
    {
        var textual = contentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
            || contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
            || contentType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true
            || contentType?.Contains("javascript", StringComparison.OrdinalIgnoreCase) == true
            || contentType?.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) == true;
        if (textual)
        {
            try { return (StrictUtf8.GetString(bytes), null); }
            catch (DecoderFallbackException) { }
        }
        return (System.Convert.ToBase64String(bytes), "base64");
    }

    private static BodyContent ReadBody(BodyCapture capture)
    {
        if (capture.Summary is JsonElement summary
            && summary.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
        {
            try
            {
                var bytes = System.Convert.FromBase64String(data.GetString() ?? "");
                var complete = Boolean(summary, "streamEnded") == true && Boolean(summary, "truncated") != true;
                return new BodyContent(bytes, complete ? null : "Body capture is incomplete or truncated.");
            }
            catch (FormatException) { return new BodyContent(null, "Body data contains invalid Base64."); }
        }
        if (capture.Chunks.Count > 0)
        {
            using var output = new MemoryStream();
            long expected = 0;
            var missing = capture.InvalidBase64;
            foreach (var (sequence, chunk) in capture.Chunks)
            {
                if (sequence != expected) missing = true;
                expected = sequence + 1;
                output.Write(chunk);
            }
            var complete = capture.Summary is JsonElement chunkSummary
                && Boolean(chunkSummary, "streamEnded") == true
                && (Int64(chunkSummary, "chunks") is not { } count || count == capture.Chunks.Count);
            if (capture.Summary is JsonElement summaryValue && Int64(summaryValue, "chunks") is { } expectedCount && expectedCount != capture.Chunks.Count)
                missing = true;
            return new BodyContent(output.ToArray(), complete && !missing ? null : "Chunked body capture is incomplete.");
        }
        if (capture.Summary is JsonElement state && String(state, "state") is { } bodyState)
            return new BodyContent(null, "Body unavailable: " + bodyState + ".");
        return new BodyContent(null, null);
    }

    private static void WriteQuery(Utf8JsonWriter writer, string url)
    {
        writer.WritePropertyName("queryString");
        writer.WriteStartArray();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                writer.WriteStartObject();
                writer.WriteString("name", Decode(separator < 0 ? pair : pair[..separator]));
                writer.WriteString("value", Decode(separator < 0 ? "" : pair[(separator + 1)..]));
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
    }

    private static string Decode(string value)
    {
        try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
        catch (UriFormatException) { return value; }
    }

    private static void WriteNameValueArray(Utf8JsonWriter writer, string propertyName, IEnumerable<(string Name, string Value)> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var (name, value) in values)
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("value", value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteTimings(Utf8JsonWriter writer, HarSession session)
    {
        writer.WritePropertyName("timings");
        writer.WriteStartObject();
        writer.WriteNumber("blocked", -1);
        writer.WriteNumber("dns", Interval(session.Completed, "connectionInitiatedTimestamp", "nameResolvedTimestamp"));
        writer.WriteNumber("connect", Interval(session.Completed, "connectionInitiatedTimestamp", "connectionCompletedTimestamp"));
        writer.WriteNumber("send", Interval(session.Completed, "requestSentTimestamp", "requestCompletedTimestamp"));
        writer.WriteNumber("wait", Interval(session.Completed, "requestCompletedTimestamp", "responseReceivedTimestamp"));
        writer.WriteNumber("receive", Interval(session.Completed, "responseReceivedTimestamp", "responseCompletedTimestamp"));
        writer.WriteNumber("ssl", -1);
        writer.WriteEndObject();
    }

    private static double TotalTime(HarSession session)
    {
        var start = Timestamp(session.Completed, "requestSentTimestamp") ?? Timestamp(session.Request, "timestamp");
        var end = Timestamp(session.Completed, "responseCompletedTimestamp") ?? Timestamp(session.Response, "timestamp");
        return start is not null && end >= start ? (end.Value - start.Value).TotalMilliseconds : -1;
    }

    private static double Interval(JsonElement? record, string startName, string endName)
    {
        var start = Timestamp(record, startName);
        var end = Timestamp(record, endName);
        return start is not null && end >= start ? (end.Value - start.Value).TotalMilliseconds : -1;
    }

    private static DateTimeOffset? StartTime(HarSession session) => Timestamp(session.Request, "timestamp")
        ?? Timestamp(session.Completed, "requestSentTimestamp") ?? Timestamp(session.Response, "timestamp");

    private static DateTimeOffset? Timestamp(JsonElement? record, string name)
    {
        if (record is not JsonElement value || !value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String || !property.TryGetDateTimeOffset(out var timestamp)) return null;
        return timestamp;
    }

    private static string Url(HarSession session) => String(session.Request, "url") ?? String(session.Completed, "url") ?? "";

    private static List<(string Name, string Value)> Headers(JsonElement? record)
    {
        var result = new List<(string, string)>();
        if (record is not JsonElement value) return result;
        foreach (var group in new[] { "headers", "contentHeaders" })
            if (value.TryGetProperty(group, out var headers) && headers.ValueKind == JsonValueKind.Object)
                foreach (var header in headers.EnumerateObject())
                    result.Add((header.Name, header.Value.ValueKind == JsonValueKind.String ? header.Value.GetString() ?? "" : header.Value.ToString()));
        return result;
    }

    private static string? Header(IEnumerable<(string Name, string Value)> headers, string name) =>
        headers.FirstOrDefault(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? ContentType(JsonElement? record) => Header(Headers(record), "Content-Type");

    private static string? String(JsonElement? record, string name) => record is JsonElement value
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static int? Int32(JsonElement? record, string name) => record is JsonElement value
        && value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number) ? number : null;

    private static long? Int64(JsonElement? record, string name) => record is JsonElement value
        && value.TryGetProperty(name, out var property) && property.TryGetInt64(out var number) ? number : null;

    private static bool? Boolean(JsonElement? record, string name) => record is JsonElement value
        && value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.GetBoolean() : null;

    private sealed class HarSession(string activityId)
    {
        public string ActivityId { get; } = activityId;
        public JsonElement? Request { get; set; }
        public JsonElement? Response { get; set; }
        public JsonElement? Completed { get; set; }
        public BodyCapture RequestBody { get; } = new();
        public BodyCapture ResponseBody { get; } = new();
    }

    private sealed class BodyCapture
    {
        public JsonElement? Summary { get; set; }
        public SortedDictionary<long, byte[]> Chunks { get; } = new();
        public bool InvalidBase64 { get; set; }
    }

    private sealed record BodyContent(byte[]? Bytes, string? Warning);
}