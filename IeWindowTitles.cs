using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

internal static class IeWindowTitles
{
    internal sealed record WindowInfo(nint Handle, nint Parent, uint ProcessId, string ClassName, string Title);

    internal static Dictionary<uint, string> Read()
    {
        var windows = new Dictionary<nint, WindowInfo>();
        Exception? callbackError = null;
        WindowCallback visit = (handle, _) =>
        {
            try
            {
                if (windows.ContainsKey(handle)) return true;
                var className = new StringBuilder(256);
                if (GetClassNameW(handle, className, className.Capacity) == 0) return true;
                var name = className.ToString();
                var title = new StringBuilder(4096);
                if (name is "TabWindowClass" or "IEFrame") GetWindowTextW(handle, title, title.Capacity);
                GetWindowThreadProcessId(handle, out var processId);
                windows.Add(handle, new WindowInfo(handle, GetAncestor(handle, 1), processId, name, title.ToString()));
                return true;
            }
            catch (Exception error) { callbackError = error; return false; }
        };
        WindowCallback root = (handle, parameter) =>
        {
            if (!visit(handle, parameter)) return false;
            EnumChildWindows(handle, visit, nint.Zero);
            return callbackError is null;
        };
        var success = EnumWindows(root, nint.Zero);
        GC.KeepAlive(visit);
        GC.KeepAlive(root);
        if (callbackError is not null) throw new InvalidOperationException("Window enumeration callback failed.", callbackError);
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Associate(windows.Values);
    }

    internal static Dictionary<uint, string> Associate(IEnumerable<WindowInfo> source)
    {
        var windows = source.ToDictionary(window => window.Handle);
        var titles = new Dictionary<uint, SortedSet<string>>();
        foreach (var renderer in windows.Values.Where(window => window.ClassName == "Internet Explorer_Server" && window.ProcessId != 0))
        {
            var current = renderer;
            var visited = new HashSet<nint>();
            for (var depth = 0; depth < 64 && visited.Add(current.Handle); depth++)
            {
                if (current.ClassName is "TabWindowClass" or "IEFrame" && !string.IsNullOrWhiteSpace(current.Title))
                {
                    if (!titles.TryGetValue(renderer.ProcessId, out var values))
                        titles.Add(renderer.ProcessId, values = new SortedSet<string>(StringComparer.Ordinal));
                    values.Add(FormatTitle(current.Title));
                    break;
                }
                if (!windows.TryGetValue(current.Parent, out var parent)) break;
                current = parent;
            }
        }
        return titles.ToDictionary(pair => pair.Key, pair => string.Join(" / ", pair.Value));
    }

    internal static string FormatTitle(string title)
    {
        var clean = title.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        var separator = clean.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0 && Uri.TryCreate(clean[..separator], UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(clean[(separator + 3)..]))
            return RemoveBrowserSuffix(clean[(separator + 3)..]) + " - " + clean[..separator];
        return RemoveBrowserSuffix(clean);
    }

    private static string RemoveBrowserSuffix(string title)
    {
        const string suffix = " - Internet Explorer";
        return title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && title.Length > suffix.Length
            ? title[..^suffix.Length].TrimEnd() : title;
    }

    internal static void SelfTest()
    {
        const string raw = "https://www.163.com/ - \u7f51\u6613 - Internet Explorer";
        if (FormatTitle(raw) != "\u7f51\u6613 - https://www.163.com/"
            || FormatTitle("A - B") != "A - B" || FormatTitle("https://example.test/") != "https://example.test/"
            || FormatTitle("Page - Internet Explorer") != "Page"
            || FormatTitle("Internet Explorer - Guide") != "Internet Explorer - Guide"
            || FormatTitle("Internet Explorer") != "Internet Explorer"
            || FormatTitle("https://example.test/ - Internet Explorer - Guide - Internet Explorer") != "Internet Explorer - Guide - https://example.test/"
            || FormatTitle("Title\twith\nlines") != "Title with lines")
            throw new Exception("Window title formatting failed.");
        var titles = Associate(new[]
        {
            new WindowInfo(1, 0, 50, "IEFrame", "Outer title"),
            new WindowInfo(2, 1, 60, "TabWindowClass", raw),
            new WindowInfo(3, 2, 60, "Shell DocObject View", ""),
            new WindowInfo(4, 3, 70, "Internet Explorer_Server", ""),
            new WindowInfo(5, 3, 70, "Internet Explorer_Server", ""),
            new WindowInfo(6, 0, 80, "Internet Explorer_Server", ""),
            new WindowInfo(7, 8, 90, "Internet Explorer_Server", ""),
            new WindowInfo(8, 7, 90, "Shell DocObject View", ""),
            new WindowInfo(9, 1, 60, "TabWindowClass", "Second page"),
            new WindowInfo(10, 9, 70, "Internet Explorer_Server", "")
        });
        if (titles.Count != 1 || !titles[70].Contains(FormatTitle(raw)) || !titles[70].Contains("Second page")
            || titles[70].Contains("Outer title")) throw new Exception("Renderer PID/title association failed.");

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "EnumWindows", "EnumChildWindows", "GetClassNameW", "GetWindowTextW", "GetWindowThreadProcessId", "GetAncestor"
        };
        foreach (var method in typeof(Program).Assembly.GetTypes().SelectMany(type => type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)))
        {
            if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;
            var import = method.GetCustomAttribute<DllImportAttribute>();
            if (method.DeclaringType != typeof(IeWindowTitles) || import is null
                || !import.Value.Equals("user32.dll", StringComparison.OrdinalIgnoreCase)
                || !allowed.Contains(import.EntryPoint ?? method.Name))
                throw new Exception("Native import outside the reviewed public window API allowlist.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool WindowCallback(nint handle, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, WindowCallback callback, nint parameter);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetAncestor(nint handle, uint flags);
}