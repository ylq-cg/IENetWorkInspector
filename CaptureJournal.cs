using System.Text;
using System.Text.Json;

internal sealed class CaptureJournal : IDisposable
{
    private readonly FileStream stream;
    private bool faulted;
    public string FilePath { get; }
    public long Count { get; private set; }
    public long Bytes { get; private set; }

    public CaptureJournal(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
        stream = new FileStream(FilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            65536, FileOptions.SequentialScan);
    }

    public void Append(string line)
    {
        if (faulted) throw new IOException("The capture journal is faulted; recording cannot continue.");
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        var checkpoint = stream.Position;
        try
        {
            stream.Write(bytes);
            stream.Flush();
            Bytes += bytes.Length;
            Count++;
        }
        catch
        {
            faulted = true;
            try { stream.SetLength(checkpoint); stream.Position = checkpoint; stream.Flush(); }
            catch (IOException) { }
            throw;
        }
    }

    public void Export(string destination)
    {
        stream.Flush();
        ExportFile(FilePath, destination);
    }

    internal static void ExportFile(string source, string destination)
    {
        if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a destination different from the active journal.");
        var temporary = Path.GetFullPath(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void ExportHar(string destination)
    {
        stream.Flush();
        HarExporter.Export(FilePath, destination);
    }

    public void Dispose() => stream.Dispose();

    public static void SelfTest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IENetworkInspector-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            using (var journal = new CaptureJournal(directory))
            {
                path = journal.FilePath;
                journal.Append("{\"text\":\"test\"}");
                journal.Append("{\"body\":\"" + new string('x', 100000) + "\"}");
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(input);
                var expected = reader.ReadToEnd();
                if (journal.Count != 2 || expected.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != 2)
                    throw new Exception("Journal visibility/count test failed.");
                var destination = Path.Combine(directory, "export.jsonl");
                journal.Export(destination);
                if (expected != File.ReadAllText(destination))
                    throw new Exception("Journal export mismatch.");
                journal.Append("""{"kind":"request","activityId":"har","timestamp":"2026-09-17T09:00:00Z","method":"POST","url":"https://example.test/api?q=one","headers":{"Accept":"application/json"},"contentHeaders":{"Content-Type":"application/json"}}""");
                journal.Append("""{"kind":"body","activityId":"har","direction":"request","encoding":"base64","bytes":11,"truncated":false,"streamEnded":true,"data":"eyJvayI6dHJ1ZX0="}""");
                journal.Append("""{"kind":"response","activityId":"har","timestamp":"2026-09-17T09:00:00.010Z","status":200,"headers":{},"contentHeaders":{"Content-Type":"text/plain"}}""");
                journal.Append("""{"kind":"completed","activityId":"har","requestSentTimestamp":"2026-09-17T09:00:00Z","requestCompletedTimestamp":"2026-09-17T09:00:00.005Z","responseReceivedTimestamp":"2026-09-17T09:00:00.010Z","responseCompletedTimestamp":"2026-09-17T09:00:00.025Z"}""");
                var harDestination = Path.Combine(directory, "export.har");
                journal.ExportHar(harDestination);
                using var har = JsonDocument.Parse(File.ReadAllText(harDestination));
                var entry = har.RootElement.GetProperty("log").GetProperty("entries")[0];
                if (entry.GetProperty("request").GetProperty("method").GetString() != "POST"
                    || entry.GetProperty("request").GetProperty("postData").GetProperty("text").GetString() != "{\"ok\":true}"
                    || entry.GetProperty("response").GetProperty("status").GetInt32() != 200
                    || entry.GetProperty("time").GetDouble() != 25)
                    throw new Exception("HAR export structure/content test failed.");
                try { journal.Export(path); throw new Exception("Journal self-overwrite allowed."); }
                catch (IOException) { }
            }
            if (File.ReadLines(path).Count() != 6) throw new Exception("Journal was not retained after close.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}