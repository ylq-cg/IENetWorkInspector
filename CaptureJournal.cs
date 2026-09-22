using System.Text;

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
        if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a destination different from the active journal.");
        var position = stream.Position;
        var temporary = Path.GetFullPath(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Position = 0;
                stream.CopyTo(output);
                output.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            stream.Position = position;
            if (File.Exists(temporary)) File.Delete(temporary);
        }
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
                try { journal.Export(path); throw new Exception("Journal self-overwrite allowed."); }
                catch (IOException) { }
            }
            if (File.ReadLines(path).Count() != 2) throw new Exception("Journal was not retained after close.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}