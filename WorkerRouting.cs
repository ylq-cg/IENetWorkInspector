using System.Reflection.PortableExecutable;

internal static class WorkerRouting
{
    internal static string? SelectExecutable(string directory, string targetArchitecture,
        string currentArchitecture, Func<string, bool> exists)
    {
        var target = targetArchitecture.ToLowerInvariant();
        if (target is not ("x86" or "x64"))
            throw new PlatformNotSupportedException($"Cannot select a worker for target architecture '{targetArchitecture}'. Refresh the process list and verify access to the target.");
        if (target.Equals(currentArchitecture, StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.Combine(directory, "workers", target, "IENetworkInspector.exe");
        if (!exists(path))
            throw new FileNotFoundException($"The {target} capture worker is missing. Build the unified package with Build-Unified.cmd and keep its workers folder alongside the application.", path);
        return path;
    }

    internal static void ValidateExecutable(string path, string targetArchitecture)
    {
        using var input = File.OpenRead(path);
        using var image = new PEReader(input);
        var expected = targetArchitecture.Equals("x86", StringComparison.OrdinalIgnoreCase) ? Machine.I386 : Machine.Amd64;
        if (image.PEHeaders.CoffHeader.Machine != expected)
            throw new BadImageFormatException($"Worker architecture does not match {targetArchitecture}: {path}");
    }

    internal static void SelfTest()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Inspector-routing-test"));
        foreach (var target in new[] { "x86", "x64" })
        {
            if (SelectExecutable(root, target, target.ToUpperInvariant(), _ => false) is not null)
                throw new Exception("Same-architecture capture must use the current executable.");
            var current = target == "x86" ? "x64" : "x86";
            var expected = Path.Combine(root, "workers", target, "IENetworkInspector.exe");
            if (SelectExecutable(root, target, current, path => path == expected) != expected)
                throw new Exception("Cross-architecture routing failed.");
            try { SelectExecutable(root, target, current, _ => false); throw new Exception("Missing worker accepted."); }
            catch (FileNotFoundException) { }
        }
        foreach (var unknown in new[] { "Unknown", "ARM64", "", "../x86" })
        {
            try { SelectExecutable(root, unknown, "x64", _ => true); throw new Exception("Unsupported target accepted."); }
            catch (PlatformNotSupportedException) { }
        }
    }
}