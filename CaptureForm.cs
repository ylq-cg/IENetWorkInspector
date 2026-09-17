using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

internal sealed class CaptureForm : Form
{
    private readonly ComboBox processes = new() { Width = 270, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Button start = new() { Text = "Start", AutoSize = true };
    private readonly Button stop = new() { Text = "Stop", AutoSize = true, Enabled = false };
    private readonly Button clear = new() { Text = "Clear", AutoSize = true };
    private readonly Button export = new() { Text = "Export JSONL", AutoSize = true, Enabled = false };
    private readonly Label state = new() { Text = "Ready", AutoSize = true, ForeColor = Color.FromArgb(23, 97, 68) };
    private readonly Label counter = new() { Text = "0 sessions / 0 events", AutoSize = true };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        BackgroundColor = Color.White, BorderStyle = BorderStyle.None, AutoGenerateColumns = false
    };
    private readonly TextBox headers = DetailBox();
    private readonly TextBox body = DetailBox();
    private readonly TextBox responseHeaders = DetailBox();
    private readonly TextBox responseBody = DetailBox();
    private readonly TextBox requestRaw = DetailBox();
    private readonly TextBox responseRaw = DetailBox();
    private readonly TextBox filter = new() { Width = 230, PlaceholderText = "Filter URL / host / method / status" };
    private readonly ComboBox statusFilter = new() { Width = 105, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label selection = new() { AutoSize = true, Text = "No session selected", Dock = DockStyle.Top, Padding = new Padding(8), AutoEllipsis = true, MaximumSize = new Size(0, 62) };
    private readonly TextBox timing = DetailBox();
    private readonly TextBox log = DetailBox();
    private readonly TabControl detailTabs = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, RequestEntry> requests = new();
    private CaptureJournal? journal;
    private readonly string journalDirectory;
    private readonly LinkedList<string> cacheOrder = new();
    private long cachedBytes;
    private long nextRowNumber;
    private bool storageFailed;
    private const long CacheBudget = 32 * 1024 * 1024;
    private const int MaxCachedRequests = 1000;
    private const int MaxCachedEventChars = 256 * 1024;
    private readonly JsonSerializerOptions pretty = new() { WriteIndented = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly ToolTip tips = new();
    private Channel<(bool IsError, string Line)>? messages;
    private Process? worker;
    private bool stopping;
    private bool closePending;
    private bool refreshing;
    private RequestEntry? shownEntry;
    private int shownRevision = -1;
    private DateTime captureStart;

    public CaptureForm(bool testMode = false)
    {
        journalDirectory = testMode ? Path.Combine(Path.GetTempPath(), "IeNetworkDemo-ui-test-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IeNetworkDemo", "Captures");
        Text = "IE Network Inspector - Experimental";
        Font = new Font("Tahoma", 9F);
        ClientSize = new Size(1380, 860);
        MinimumSize = new Size(880, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var targetBar = Bar();
        targetBar.Controls.Add(Caption("Process / PID"));
        targetBar.Controls.Add(processes);
        targetBar.Controls.Add(refresh);
        targetBar.Controls.Add(start);
        targetBar.Controls.Add(stop);
        targetBar.Controls.Add(clear);
        targetBar.Controls.Add(export);
        layout.Controls.Add(targetBar, 0, 0);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 7, Size = new Size(1200, 600), Panel1MinSize = 300, Panel2MinSize = 320 };
        var sessionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        sessionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sessionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sessionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var filters = Bar();
        statusFilter.Items.AddRange(new object[] { "All statuses", "2xx", "3xx", "4xx / 5xx", "No response" });
        statusFilter.SelectedIndex = 0;
        var resetFilter = new Button { Text = "Reset filters", AutoSize = true };
        resetFilter.Click += (_, _) => { filter.Clear(); statusFilter.SelectedIndex = 0; };
        filters.Controls.Add(filter);
        filters.Controls.Add(statusFilter);
        filters.Controls.Add(resetFilter);
        sessionLayout.Controls.Add(filters, 0, 0);
        sessionLayout.Controls.Add(grid, 0, 1);
        split.Panel1.Controls.Add(sessionLayout);
        var inspectors = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(500, 600), Panel1MinSize = 130, Panel2MinSize = 130, SplitterWidth = 6 };
        inspectors.Panel1.Controls.Add(Inspector("Request", headers, body, requestRaw));
        inspectors.Panel2.Controls.Add(Inspector("Response", responseHeaders, responseBody, responseRaw));
        var inspectLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        inspectLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inspectLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inspectLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        inspectLayout.Controls.Add(selection, 0, 0);
        inspectLayout.Controls.Add(inspectors, 0, 1);
        AddTab(detailTabs, "Inspectors", inspectLayout);
        AddTab(detailTabs, "Timing", timing);
        AddTab(detailTabs, "Log", log);
        split.Panel2.Controls.Add(detailTabs);
        layout.Controls.Add(split, 0, 1);
        var statusBar = Bar();
        statusBar.Controls.Add(state);
        statusBar.Controls.Add(counter);
        layout.Controls.Add(statusBar, 0, 2);
        Controls.Add(layout);
        AddColumn("number", "#", 42);
        AddColumn("status", "Result", 62);
        AddColumn("method", "Method", 68);
        AddColumn("host", "Host", 145);
        AddColumn("url", "URL", 240, true);
        AddColumn("duration", "Time ms", 85);
        AddColumn("type", "Content-Type", 135);
        AddColumn("time", "Started", 100);
        grid.RowTemplate.Height = 25;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(236, 240, 244);
        grid.ColumnHeadersHeight = 30;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(233, 236, 239);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(245, 248, 245);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(218, 235, 223);
        grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        tips.SetToolTip(processes, "Select an IE candidate or enter the request process PID. Candidates do not identify tabs.");
        tips.SetToolTip(start, "Capture bodies until stopped. Events are saved locally. Use approved test traffic only.");
        tips.SetToolTip(export, "Export all persisted events. Paths, custom headers and bodies may still contain sensitive data.");
        refresh.Click += async (_, _) => await RefreshProcesses();
        start.Click += async (_, _) => await StartCapture();
        stop.Click += (_, _) => StopCapture();
        clear.Click += (_, _) => ClearCapture();
        export.Click += (_, _) => Export();
        grid.SelectionChanged += (_, _) => ShowDetails();
        filter.TextChanged += (_, _) => ApplyFilters();
        statusFilter.SelectedIndexChanged += (_, _) => ApplyFilters();
        timer.Tick += (_, _) => DrainMessages();
        Shown += async (_, _) =>
        {
            split.SplitterDistance = (int)(split.Width * 0.54);
            inspectors.SplitterDistance = inspectors.Height / 2;
            if (!testMode) await RefreshProcesses();
        };
        FormClosing += (_, eventArgs) =>
        {
            if (worker is not null)
            {
                eventArgs.Cancel = true;
                closePending = true;
                StopCapture();
            }
            else if (refreshing)
            {
                eventArgs.Cancel = true;
                closePending = true;
            }
        };
        FormClosed += (_, _) =>
        {
            timer.Dispose(); tips.Dispose(); journal?.Dispose();
            if (testMode && Directory.Exists(journalDirectory)) Directory.Delete(journalDirectory, true);
        };
    }

    private static TextBox DetailBox() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Consolas", 10F) };
    private static FlowLayoutPanel Bar() => new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 4), BackColor = Color.FromArgb(246, 248, 250), Padding = new Padding(3) };
    private static Control Inspector(string title, TextBox headerBox, TextBox bodyBox, TextBox rawBox)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Tahoma", 10F, FontStyle.Bold), Padding = new Padding(5) }, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        AddTab(tabs, "Headers", headerBox);
        AddTab(tabs, "Body", bodyBox);
        AddTab(tabs, "JSON", rawBox);
        panel.Controls.Add(tabs, 0, 1);
        return panel;
    }
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 8, 3) };
    private static void AddTab(TabControl tabs, string text, Control content)
    {
        var tab = new TabPage(text) { Padding = new Padding(10) };
        tab.Controls.Add(content);
        tabs.TabPages.Add(tab);
    }
    private void AddColumn(string name, string title, int width, bool fill = false) => grid.Columns.Add(new DataGridViewTextBoxColumn
    {
        Name = name, HeaderText = title, Width = width, MinimumWidth = fill ? 130 : Math.Min(width, 60),
        AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
        SortMode = DataGridViewColumnSortMode.NotSortable
    });

    private static ProcessStartInfo Command(params string[] args)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var argument in args) info.ArgumentList.Add(argument);
        return info;
    }

    private async Task RefreshProcesses()
    {
        if (worker is not null || refreshing) return;
        refreshing = true;
        start.Enabled = refresh.Enabled = false;
        state.Text = "Finding processes...";
        try
        {
            using var listing = Process.Start(Command("--list")) ?? throw new InvalidOperationException("Cannot launch process enumeration.");
            var stdout = listing.StandardOutput.ReadToEndAsync();
            var stderr = listing.StandardError.ReadToEndAsync();
            await listing.WaitForExitAsync();
            var previous = processes.Text.Split('|')[0].Trim();
            processes.Items.Clear();
            foreach (var line in (await stdout).Split('\n'))
            {
                var fields = line.Trim().Split('\t');
                if (fields.Length >= 2 && uint.TryParse(fields[0], out _))
                    processes.Items.Add($"{fields[0]} | {fields[1]} | {(fields[1].Equals("iexplore", StringComparison.OrdinalIgnoreCase) ? "IE candidate" : "MSHTML loaded")}");
            }
            var match = processes.Items.Cast<string>().FirstOrDefault(item => item.StartsWith(previous + " |", StringComparison.Ordinal));
            if (match is not null) processes.SelectedItem = match;
            else if (processes.Items.Count == 1) processes.SelectedIndex = 0;
            else { processes.SelectedIndex = -1; processes.Text = ""; }
            AppendLog(await stderr);
            state.Text = listing.ExitCode == 1 ? "Process enumeration failed. See Log."
                : processes.Items.Count == 0 ? "No IE processes found. Enter a PID manually." : $"Found {processes.Items.Count} candidates. Select a target.";
        }
        catch (Exception error) { ReportError(error); }
        finally
        {
            refreshing = false;
            start.Enabled = refresh.Enabled = true;
            if (closePending) Close();
        }
    }

    private async Task StartCapture()
    {
        if (worker is not null || refreshing) return;
        if (!uint.TryParse(processes.Text.Split('|')[0].Trim(), out var processId) || processId == 0)
        {
            MessageBox.Show(this, "Select a candidate process or enter a valid PID.", "Target process", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (journal?.Count > 0 && MessageBox.Show(this, "Starting a new capture clears this view. Previous journals remain on disk. Continue?", "New capture", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        ClearCapture();
        messages = Channel.CreateBounded<(bool, string)>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var activeChannel = messages;
        stopping = false;
        captureStart = DateTime.UtcNow;
        try
        {
            EnsureJournal();
            worker = Process.Start(CaptureCommand(processId))
                ?? throw new InvalidOperationException("Cannot launch capture worker.");
            SetCapturing(true);
            state.ForeColor = Color.FromArgb(23, 97, 68);
            state.Text = "Starting capture...";
            timer.Start();
            var outputTask = Pump(worker.StandardOutput, false, activeChannel.Writer);
            var errorTask = Pump(worker.StandardError, true, activeChannel.Writer);
            await worker.WaitForExitAsync();
            await Task.WhenAll(outputTask, errorTask);
            DrainMessages(true);
            state.Text = storageFailed ? "Disk write failed. Capture stopped; data may be incomplete. See Log."
                : worker.ExitCode == 0 ? "Capture finished. Events saved to disk."
                : worker.ExitCode == 2 ? "No events. Check the PID and reproduce a request."
                : "Capture failed. See Log for HRESULT and permissions.";
            state.ForeColor = worker.ExitCode == 1 || storageFailed ? Color.Firebrick : Color.FromArgb(23, 97, 68);
            if (worker.ExitCode == 1 || storageFailed) detailTabs.SelectedIndex = 2;
        }
        catch (Exception error)
        {
            StopCapture();
            if (worker is not null)
            {
                try { await worker.WaitForExitAsync(); } catch (InvalidOperationException) { }
            }
            ReportError(error);
        }
        finally
        {
            timer.Stop();
            activeChannel.Writer.TryComplete();
            worker?.Dispose();
            worker = null;
            SetCapturing(false);
            if (closePending) Close();
        }
    }

    private static async Task Pump(StreamReader reader, bool isError, ChannelWriter<(bool, string)> writer)
    {
        while (await reader.ReadLineAsync() is { } line)
            await writer.WriteAsync((isError, line));
    }

    private static ProcessStartInfo CaptureCommand(uint processId) =>
        Command("--worker", processId.ToString(CultureInfo.InvariantCulture), "--continuous");

    private void StopCapture()
    {
        if (worker is null || stopping) return;
        stopping = true;
        stop.Enabled = false;
        state.Text = "Stopping capture and cleaning up...";
        try
        {
            worker.StandardInput.WriteLine("stop");
            worker.StandardInput.Flush();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            AppendLog("Stop request: " + error.Message);
        }
    }

    private void DrainMessages(bool all = false)
    {
        if (messages is null) return;
        var count = 0;
        while ((all || count < 150) && messages.Reader.TryRead(out var message))
        {
            count++;
            if (message.IsError)
            {
                AppendLog(message.Line);
                if (!stopping && message.Line.StartsWith("Start returned successfully", StringComparison.Ordinal)) state.Text = "Capturing";
            }
            else if (!storageFailed)
            {
                try { AddRecord(message.Line); }
                catch (JsonException error) { AppendLog("Invalid event JSON: " + error.Message); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    storageFailed = true;
                    StopCapture();
                    ReportError(error);
                    AppendLog("Journal write failed. Capture is stopping; subsequent queued events are not saved. Existing file retained: " + journal?.FilePath);
                }
            }
        }
        UpdateCounter();
        export.Enabled = worker is null && journal?.Count > 0;
        ShowDetails();
    }

    private void AddRecord(string line)
    {
        EnsureJournal();
        journal!.Append(line);
        using var document = JsonDocument.Parse(line);
        var record = document.RootElement;
        if (!record.TryGetProperty("activityId", out var activity)) return;
        var id = activity.GetString() ?? "";
        if (!requests.TryGetValue(id, out var entry))
        {
            var rowIndex = grid.Rows.Add();
            entry = new RequestEntry(grid.Rows[rowIndex]) { ActivityId = id };
            entry.Row.Cells["number"].Value = ++nextRowNumber;
            entry.Row.Cells["url"].Value = "[Request metadata unavailable]";
            entry.Row.Cells["url"].ToolTipText = "Partial events received without a request event. This is not an empty HTTP request.";
            entry.Row.Tag = entry;
            requests.Add(id, entry);
            entry.CacheNode = cacheOrder.AddLast(id);
        }
        else
        {
            cacheOrder.Remove(entry.CacheNode!);
            entry.CacheNode = cacheOrder.AddLast(id);
        }
        var kind = record.GetProperty("kind").GetString() ?? "";
        if (kind == "body-chunk")
        {
            var direction = record.GetProperty("direction").GetString() ?? "";
            if (!entry.BodyPreviews.TryGetValue(direction, out var preview))
            {
                preview = new BodyPreview();
                entry.BodyPreviews.Add(direction, preview);
            }
            var chunk = Convert.FromBase64String(record.GetProperty("data").GetString() ?? "");
            var sequence = record.GetProperty("sequence").GetInt64();
            if (sequence != preview.NextSequence) preview.MissingChunks = true;
            preview.NextSequence = sequence + 1;
            preview.BytesSeen += chunk.Length;
            var retained = Math.Min(chunk.Length, Math.Max(0, MaxCachedEventChars - (int)preview.Data.Length));
            preview.Data.Write(chunk, 0, retained);
            entry.CachedBytes += retained;
            cachedBytes += retained;
            entry.Revision++;
            ApplyFilter(entry);
            TrimCache();
            ShowDetails();
            return;
        }
        if (kind == "body") kind += ":" + record.GetProperty("direction").GetString();
        var cacheValue = line.Length <= MaxCachedEventChars ? record.Clone()
            : JsonSerializer.SerializeToElement(new { kind = "disk-only", state = "Event exceeds the preview limit. Full data is saved in the journal.", sourceKind = kind });
        if (entry.EventSizes.TryGetValue(kind, out var oldSize)) { cachedBytes -= oldSize; entry.CachedBytes -= oldSize; }
        var size = line.Length <= MaxCachedEventChars ? line.Length * 2L : 512;
        entry.EventSizes[kind] = size;
        entry.CachedBytes += size;
        cachedBytes += size;
        entry.Events[kind] = cacheValue;
        entry.Revision++;
        if (kind == "request")
        {
            entry.Row.Cells["url"].ToolTipText = "";
            entry.Row.Cells["method"].Value = record.GetProperty("method").GetString();
            entry.Row.Cells["url"].Value = record.GetProperty("url").GetString();
            if (Uri.TryCreate(record.GetProperty("url").GetString(), UriKind.Absolute, out var uri))
                entry.Row.Cells["host"].Value = uri.Authority;
            if (record.GetProperty("timestamp").TryGetDateTimeOffset(out var timestamp))
                entry.Row.Cells["time"].Value = timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        }
        if (kind == "response")
        {
            var status = record.GetProperty("status").GetInt32();
            entry.Row.Cells["status"].Value = status;
            entry.Row.DefaultCellStyle.ForeColor = status >= 400 ? Color.Firebrick : status >= 300 ? Color.FromArgb(135, 95, 15) : Color.FromArgb(25, 55, 75);
            foreach (var group in new[] { "headers", "contentHeaders" })
                if (record.TryGetProperty(group, out var headerGroup) && headerGroup.ValueKind == JsonValueKind.Object)
                    foreach (var header in headerGroup.EnumerateObject())
                        if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) entry.Row.Cells["type"].Value = header.Value.GetString();
        }
        if (kind == "completed")
        {
            if (Duration(record) is { } duration)
                entry.Row.Cells["duration"].Value = duration.ToString("F1", CultureInfo.InvariantCulture);
            if (!entry.Events.ContainsKey("request") && record.TryGetProperty("url", out var completedUrl)
                && completedUrl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(completedUrl.GetString()))
            {
                entry.Row.Cells["url"].Value = completedUrl.GetString();
                entry.Row.Cells["url"].ToolTipText = "URL from completed.RequestedUri. No request event: method and request headers are unknown.";
                if (Uri.TryCreate(completedUrl.GetString(), UriKind.Absolute, out var uri)) entry.Row.Cells["host"].Value = uri.Authority;
            }
        }
        ApplyFilter(entry);
        TrimCache();
        ShowDetails();
    }

    private void EnsureJournal()
    {
        if (journal is not null) return;
        journal = new CaptureJournal(journalDirectory);
        AppendLog("Capture journal: " + journal.FilePath);
    }

    private void TrimCache()
    {
        while (requests.Count > MaxCachedRequests || cachedBytes > CacheBudget)
        {
            var node = cacheOrder.First!;
            var expired = requests[node.Value];
            cachedBytes -= expired.CachedBytes;
            requests.Remove(node.Value);
            cacheOrder.RemoveFirst();
            grid.Rows.Remove(expired.Row);
            if (ReferenceEquals(shownEntry, expired)) { shownEntry = null; shownRevision = -1; }
        }
    }

    private bool MatchesFilter(RequestEntry entry)
    {
        var text = string.Join(" ", new[] { "url", "host", "method", "status", "type" }.Select(name => entry.Row.Cells[name].Value?.ToString()));
        if (!text.Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        var status = entry.Row.Cells["status"].Value as int?;
        return statusFilter.SelectedIndex switch
        {
            1 => status is >= 200 and < 300,
            2 => status is >= 300 and < 400,
            3 => status is >= 400 and < 600,
            4 => status is null,
            _ => true
        };
    }

    private void ApplyFilter(RequestEntry entry)
    {
        var visible = MatchesFilter(entry);
        if (!visible && grid.CurrentCell?.OwningRow == entry.Row) grid.CurrentCell = null;
        if (!visible) entry.Row.Selected = false;
        entry.Row.Visible = visible;
    }

    private void ApplyFilters()
    {
        foreach (var entry in requests.Values) ApplyFilter(entry);
        UpdateCounter();
        ShowDetails();
    }

    private void UpdateCounter() => counter.Text = $"{requests.Values.Count(entry => entry.Row.Visible)} / {requests.Count} cached sessions | {journal?.Count ?? 0} events saved | {(journal?.Bytes ?? 0) / 1048576.0:F1} MiB"
        + (worker is not null ? $" | {(DateTime.UtcNow - captureStart).TotalSeconds:F0} s" : "");

    internal static double? Duration(JsonElement record)
    {
        if (!record.TryGetProperty("requestSentTimestamp", out var sent) || !record.TryGetProperty("responseCompletedTimestamp", out var complete)
            || sent.ValueKind != JsonValueKind.String || complete.ValueKind != JsonValueKind.String
            || !sent.TryGetDateTimeOffset(out var startTime) || !complete.TryGetDateTimeOffset(out var endTime)
            || startTime.Year < 1970 || endTime < startTime) return null;
        return (endTime - startTime).TotalMilliseconds;
    }

    private void ShowDetails()
    {
        if (grid.SelectedRows.Count == 0 || !grid.SelectedRows[0].Visible || grid.SelectedRows[0].Tag is not RequestEntry entry)
        {
            headers.Clear(); responseHeaders.Clear(); body.Clear(); responseBody.Clear(); requestRaw.Clear(); responseRaw.Clear(); timing.Clear();
            selection.Text = "No session selected"; shownEntry = null; shownRevision = -1;
            return;
        }
        if (ReferenceEquals(shownEntry, entry) && shownRevision == entry.Revision) return;
        shownEntry = entry;
        shownRevision = entry.Revision;
        selection.Text = $"#{entry.Row.Cells["number"].Value}  {entry.Row.Cells["method"].Value}  {entry.Row.Cells["url"].Value}";
        headers.Text = HeaderText(entry, "request");
        responseHeaders.Text = HeaderText(entry, "response");
        requestRaw.Text = Format(entry, "request");
        responseRaw.Text = Format(entry, "response");
        timing.Text = Format(entry, "completed");
        body.Text = BodyText(entry, "request");
        responseBody.Text = BodyText(entry, "response");
    }

    private static string HeaderText(RequestEntry entry, string kind)
    {
        if (!entry.Events.TryGetValue(kind, out var record))
        {
            var message = kind == "request" ? "No request event received. Method and request headers are unknown." : "No response event received. Status and response headers are unknown.";
            if (entry.Events.ContainsKey("completed")) message += "\r\nA completed event was received. Missing data may not arrive; check Timing and the journal.";
            return message + "\r\nActivity ID: " + entry.ActivityId + "\r\nReceived events: "
                + string.Join(", ", entry.Events.Keys.Concat(entry.BodyPreviews.Keys.Select(direction => "body-chunk:" + direction)));
        }
        if (record.GetProperty("kind").GetString() == "disk-only") return "Event exceeds the preview limit. Full data is saved in the journal.";
        var text = new StringBuilder();
        text.AppendLine(kind == "request" ? $"{record.GetProperty("method").GetString()} {record.GetProperty("url").GetString()}" : $"Status: {record.GetProperty("status").GetInt32()}");
        foreach (var group in new[] { "headers", "contentHeaders" })
        {
            text.AppendLine();
            if (record.TryGetProperty(group, out var values) && values.ValueKind == JsonValueKind.Object)
                foreach (var header in values.EnumerateObject()) text.AppendLine($"{header.Name}: {header.Value.GetString()}");
        }
        return text.ToString();
    }

    private string Format(RequestEntry entry, string kind) => entry.Events.TryGetValue(kind, out var value)
        ? kind + Environment.NewLine + JsonSerializer.Serialize(value, pretty) + Environment.NewLine : kind + ": No data received" + Environment.NewLine;

    private string BodyText(RequestEntry entry, string direction)
    {
        if (entry.BodyPreviews.TryGetValue(direction, out var preview))
        {
            var completed = entry.Events.TryGetValue("body:" + direction, out var summary);
            var ended = completed && summary.TryGetProperty("streamEnded", out var endFlag) && endFlag.ValueKind == JsonValueKind.True;
            var status = ended ? "Body stream ended" : completed ? "Body read incomplete" : "Receiving body";
            var text = $"{direction} | {status}\r\nReceived {preview.BytesSeen} bytes; preview {preview.Data.Length} bytes\r\nBody chunks are saved in the journal. Export includes all persisted chunks.\r\n";
            if (preview.MissingChunks) text += "The preview has missing chunks, possibly after cache eviction. Check the journal.\r\n";
            if (completed && !ended) text += JsonSerializer.Serialize(summary, pretty) + Environment.NewLine;
            return text + "\r\nUTF-8 preview (partial characters or encoding mismatches may appear as replacement characters):\r\n" + Encoding.UTF8.GetString(preview.Data.GetBuffer(), 0, (int)preview.Data.Length);
        }
        if (!entry.Events.TryGetValue("body:" + direction, out var value)) return direction + ": Body not captured or not yet received\r\n";
        var description = direction + Environment.NewLine;
        if (value.TryGetProperty("data", out var data))
        {
            var truncated = value.TryGetProperty("truncated", out var flag) && flag.ValueKind == JsonValueKind.True;
            description += $"Captured {value.GetProperty("bytes").GetInt32()} bytes{(truncated ? " | Truncated" : "")}\r\n\r\n";
            try { return description + "UTF-8 preview (original bytes available in JSONL export)\r\n" + new UTF8Encoding(false, true).GetString(Convert.FromBase64String(data.GetString() ?? "")); }
            catch (DecoderFallbackException) { return description + "Not valid UTF-8. Base64:\r\n" + data.GetString(); }
            catch (FormatException) { return description + "Invalid Base64 data."; }
        }
        return description + JsonSerializer.Serialize(value, pretty);
    }

    private void ClearCapture()
    {
        journal?.Dispose(); journal = null;
        requests.Clear(); grid.Rows.Clear(); cacheOrder.Clear(); cachedBytes = 0; nextRowNumber = 0; storageFailed = false;
        shownEntry = null; shownRevision = -1;
        headers.Clear(); body.Clear(); timing.Clear(); log.Clear();
        responseHeaders.Clear(); responseBody.Clear(); requestRaw.Clear(); responseRaw.Clear();
        selection.Text = "No session selected";
        export.Enabled = false; counter.Text = "0 sessions / 0 events";
    }

    private void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON Lines (*.jsonl)|*.jsonl", FileName = $"ie-network-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { journal?.Export(dialog.FileName); state.Text = "All journal events exported"; }
        catch (Exception error) { ReportError(error); }
    }

    private void SetCapturing(bool capturing)
    {
        processes.Enabled = refresh.Enabled = start.Enabled = clear.Enabled = !capturing;
        stop.Enabled = capturing;
        export.Enabled = !capturing && journal?.Count > 0;
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (log.TextLength > 100000) log.Text = log.Text[^50000..];
        log.AppendText(text.TrimEnd() + Environment.NewLine);
    }

    private void ReportError(Exception error)
    {
        state.Text = "Operation failed. See Log.";
        state.ForeColor = Color.Firebrick;
        detailTabs.SelectedIndex = 2;
        AppendLog($"{error.GetType().Name} 0x{error.HResult:X8}: {error.Message}");
    }

    private sealed class RequestEntry(DataGridViewRow row)
    {
        public string ActivityId { get; set; } = "";
        public DataGridViewRow Row { get; } = row;
        public Dictionary<string, JsonElement> Events { get; } = new();
        public int Revision { get; set; }
        public LinkedListNode<string>? CacheNode { get; set; }
        public long CachedBytes { get; set; }
        public Dictionary<string, long> EventSizes { get; } = new();
        public Dictionary<string, BodyPreview> BodyPreviews { get; } = new();
    }

    private sealed class BodyPreview
    {
        public MemoryStream Data { get; } = new();
        public long BytesSeen { get; set; }
        public long NextSequence { get; set; }
        public bool MissingChunks { get; set; }
    }

    internal static int RunSelfTest()
    {
        var command = CaptureCommand(123);
        if (!command.ArgumentList.TakeLast(3).SequenceEqual(new[] { "--worker", "123", "--continuous" }))
            throw new InvalidOperationException("UI must start continuous body capture.");
        using var form = new CaptureForm(true);
        form.Show();
        Application.DoEvents();
        static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));
        var root = (TableLayoutPanel)form.Controls[0];
        if (root.RowCount != 3 || root.GetRow(form.processes.Parent!) != 0 || form.export.Enabled)
            throw new InvalidOperationException("The process toolbar must be the first row; Export must start disabled.");
        if (Descendants(form).Any(control => control is NumericUpDown or CheckBox))
            throw new InvalidOperationException("Removed capture options are still visible.");
        if (form.clear.Parent != form.processes.Parent || form.export.Parent != form.processes.Parent
            || Descendants(form).Any(control => control.Text == "Open capture directory"))
            throw new InvalidOperationException("Capture actions must share the process toolbar without a folder button.");
        form.AddRecord("""{"kind":"body","activityId":"sample","direction":"response","encoding":"base64","bytes":2,"truncated":false,"streamEnded":true,"data":"T0s="}""");
        form.AddRecord("""{"kind":"request","activityId":"sample","timestamp":"2026-09-17T09:00:00Z","method":"GET","url":"https://example.test/status","headers":{"Accept":"application/json"},"contentHeaders":{}}""");
        form.AddRecord("""{"kind":"response","activityId":"sample","timestamp":"2026-09-17T09:00:00.010Z","status":200,"headers":{"Content-Type":"text/plain"},"contentHeaders":{}}""");
        form.AddRecord("""{"kind":"completed","activityId":"sample","requestSentTimestamp":"2026-09-17T09:00:00Z","responseCompletedTimestamp":"2026-09-17T09:00:00.025Z"}""");
        form.grid.Rows[0].Selected = true;
        form.ShowDetails();
        if (form.requests.Count != 1 || form.journal?.Count != 4 || !form.responseBody.Text.Contains("OK")
            || form.grid.Rows[0].Cells["duration"].Value?.ToString() != "25.0")
            throw new InvalidOperationException("UI event correlation/detail test failed.");
        using var missing = JsonDocument.Parse("""{"requestSentTimestamp":null,"responseCompletedTimestamp":null}""");
        if (Duration(missing.RootElement) is not null) throw new InvalidOperationException("Null timestamp accepted.");
        form.AddRecord("""{"kind":"response","activityId":"failed","timestamp":"2026-09-17T09:00:01Z","status":503,"headers":{},"contentHeaders":{"Content-Type":"application/json"}}""");
        form.AddRecord("""{"kind":"request","activityId":"failed","timestamp":"2026-09-17T09:00:01Z","method":"POST","url":"https://example.test/api/orders","headers":{"Content-Type":"application/json"},"contentHeaders":{}}""");
        form.AddRecord("""{"kind":"request","activityId":"pending","timestamp":"2026-09-17T09:00:02Z","method":"GET","url":"https://static.example.test/site.css","headers":{},"contentHeaders":{}}""");
        form.statusFilter.SelectedIndex = 3;
        if (form.grid.Rows[0].Visible || !form.requests["failed"].Row.Visible || form.requests["pending"].Row.Visible || form.headers.TextLength != 0)
            throw new InvalidOperationException("Error filter or hidden selection clearing failed.");
        form.filter.Text = "not-found";
        if (form.requests.Values.Any(entry => entry.Row.Visible)) throw new InvalidOperationException("Text filter failed.");
        form.filter.Clear();
        form.statusFilter.SelectedIndex = 4;
        if (!form.requests["pending"].Row.Visible || form.requests["failed"].Row.Visible) throw new InvalidOperationException("Pending filter failed.");
        form.statusFilter.SelectedIndex = 0;
        form.filter.Text = "POST";
        if (!form.requests["failed"].Row.Visible || form.grid.Rows[0].Visible) throw new InvalidOperationException("Method filter failed.");
        form.filter.Clear();
        form.grid.Rows[0].Selected = true;
        form.ShowDetails();
        if (!form.headers.Text.Contains("Accept: application/json") || !form.responseHeaders.Text.Contains("Status: 200")
            || !form.requestRaw.Text.Contains("activityId") || !form.responseBody.Text.Contains("OK"))
            throw new InvalidOperationException("Split inspectors test failed.");
        form.AddRecord("""{"kind":"completed","activityId":"completion-only","url":"https://example.test/completed-only","processId":123,"requestSentTimestamp":null,"responseCompletedTimestamp":null}""");
        var completionOnly = form.requests["completion-only"];
        if (completionOnly.Row.Cells["url"].Value?.ToString() != "https://example.test/completed-only"
            || completionOnly.Row.Cells["host"].Value?.ToString() != "example.test"
            || completionOnly.Row.Cells["method"].Value is not null || completionOnly.Row.Cells["status"].Value is not null
            || !HeaderText(completionOnly, "request").Contains("completion-only"))
            throw new InvalidOperationException("Completion-only URL fallback or missing event disclosure failed.");
        form.AddRecord("""{"kind":"completed","activityId":"legacy-completion","requestSentTimestamp":null,"responseCompletedTimestamp":null}""");
        if (form.requests["legacy-completion"].Row.Cells["url"].Value?.ToString() != "[Request metadata unavailable]")
            throw new InvalidOperationException("Legacy completion placeholder missing.");
        form.AddRecord("""{"kind":"request","activityId":"completion-only","timestamp":"2026-09-17T09:00:00Z","method":"GET","url":"https://example.test/actual-request","headers":{},"contentHeaders":{}}""");
        form.AddRecord("""{"kind":"completed","activityId":"completion-only","url":"https://example.test/completed-only"}""");
        if (completionOnly.Row.Cells["url"].Value?.ToString() != "https://example.test/actual-request"
            || completionOnly.Row.Cells["url"].ToolTipText.Length != 0)
            throw new InvalidOperationException("Completed fallback overwrote real request data.");
        form.UpdateCounter();
        form.export.Enabled = true;
        form.state.Text = "UI test: no capture started";
        var uiText = Descendants(form).SelectMany(control => new[] { control.Text, form.tips.GetToolTip(control) })
            .Concat(form.statusFilter.Items.Cast<string>())
            .Concat(form.grid.Columns.Cast<DataGridViewColumn>().Select(column => column.HeaderText))
            .Append(form.filter.PlaceholderText).Append(form.Text);
        if (uiText.Any(text => text is not null && text.Any(character => character >= '\u4e00' && character <= '\u9fff')))
            throw new InvalidOperationException("Untranslated Chinese UI text remains.");
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        foreach (var size in new[] { new Size(1380, 860), new Size(880, 640) })
        {
            form.ClientSize = size;
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(directory, $"ui-test-{size.Width}.png"));
        }
        form.SetCapturing(true);
        if (form.start.Enabled || !form.stop.Enabled || form.export.Enabled)
            throw new InvalidOperationException("Capture control state test failed.");
        form.SetCapturing(false);
        var bodyChunk = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('a', 32768)));
        for (var sequence = 0; sequence < 10; sequence++)
            form.AddRecord(JsonSerializer.Serialize(new { kind = "body-chunk", activityId = "sample", direction = "response", sequence, data = bodyChunk }));
        if (form.requests["sample"].BodyPreviews["response"].Data.Length != MaxCachedEventChars)
            throw new InvalidOperationException("Body preview cache exceeded budget.");
        var persistedPath = form.journal!.FilePath;
        var persistedCount = form.journal.Count;
        var exportPath = Path.Combine(form.journalDirectory, "complete-export.jsonl");
        for (var index = 0; index < 1005; index++)
            form.AddRecord(JsonSerializer.Serialize(new { kind = "body", activityId = "evict-" + index, direction = "response", state = "test" }));
        if (form.requests.Count > MaxCachedRequests || form.cachedBytes > CacheBudget || form.requests.ContainsKey("sample"))
            throw new InvalidOperationException("UI cache eviction failed.");
        form.journal.Export(exportPath);
        if (File.ReadLines(exportPath).LongCount() != persistedCount + 1005 || !File.ReadLines(exportPath).Any(line => line.Contains("body-chunk")))
            throw new InvalidOperationException("Eviction lost persisted export records.");
        form.ClearCapture();
        if (!File.Exists(persistedPath)) throw new InvalidOperationException("Clear deleted persisted capture.");
        var largeEventText = new string('x', 240000);
        for (var index = 0; index < 90; index++)
            form.AddRecord(JsonSerializer.Serialize(new { kind = "body", activityId = "memory-" + index, direction = "response", state = largeEventText }));
        if (form.cachedBytes > CacheBudget || form.requests.Count >= 90 || form.journal?.Count != 90)
            throw new InvalidOperationException("Byte-budget eviction lost records or exceeded memory accounting budget.");
        form.ClearCapture();
        if (form.grid.Rows.Count != 0 || form.export.Enabled || form.headers.TextLength != 0)
            throw new InvalidOperationException("Clear state test failed.");
        form.Close();
        Console.WriteLine("PASS: UI, body chunk preview, 1000-row and 32MiB cache eviction, complete disk export, retained journals after clear, two viewport snapshots. No capture started.");
        return 0;
    }
}