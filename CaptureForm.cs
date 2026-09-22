using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Xml.Linq;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal sealed class CaptureForm : Form
{
    private const int NativeAccessViolationExitCode = unchecked((int)0xC0000005);
    private readonly ComboBox processes = new() { Width = 420, MaxDropDownItems = 24, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button chooseProcess = new() { Text = "Choose...", AutoSize = true };
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
    private readonly TextBox body = DetailBox();
    private readonly TextBox responseBody = DetailBox();
    private readonly DataGridView requestHeadersGrid = InspectorGrid("Key", "Value");
    private readonly DataGridView requestParamsGrid = InspectorGrid("Key", "Value");
    private readonly DataGridView requestCookiesGrid = InspectorGrid("Name", "Value");
    private readonly DataGridView responseHeadersGrid = InspectorGrid("Key", "Value");
    private readonly DataGridView responseCookiesGrid = InspectorGrid("Name", "Value", "Expires", "Max-Age", "Domain", "Path", "Secure", "HttpOnly", "SameSite");
    private readonly TextBox requestBodyJson = DetailBox();
    private readonly TextBox responseBodyJson = DetailBox();
    private readonly TextBox requestBodyXml = DetailBox();
    private readonly TextBox responseBodyXml = DetailBox();
    private readonly TextBox requestBodyJavaScript = DetailBox();
    private readonly TextBox responseBodyJavaScript = DetailBox();
    private readonly TextBox requestFormData = DetailBox();
    private readonly PictureBox responseImage = ImageBox();
    private readonly Label responseImageState = MediaState();
    private readonly TextBox requestHex = DetailBox();
    private readonly TextBox responseHex = DetailBox();
    private readonly WebView2 responseWeb = WebView();
    private readonly Label responseWebState = MediaState("Initializing WebView...");
    private Control responseImageView = null!;
    private Control responseWebView = null!;
    private readonly TextBox requestAuth = DetailBox();
    private readonly TextBox requestRaw = DetailBox();
    private readonly TextBox responseRaw = DetailBox();
    private readonly Label requestSummary = InspectorSummary();
    private readonly Label responseSummary = InspectorSummary();
    private readonly FlowLayoutPanel requestBadges = InspectorBadges();
    private readonly FlowLayoutPanel responseBadges = InspectorBadges();
    private TabPage requestHeadersTab = null!;
    private TabPage requestParamsTab = null!;
    private TabPage requestCookiesTab = null!;
    private TabPage responseHeadersTab = null!;
    private TabPage responseCookiesTab = null!;
    private readonly TextBox filter = new() { Width = 230, PlaceholderText = "Filter URL / host / method / status" };
    private readonly ComboBox statusFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label selection = new() { AutoSize = true, Text = "No session selected", Dock = DockStyle.Top, Padding = new Padding(8), AutoEllipsis = true, MaximumSize = new Size(0, 62) };
    private readonly TextBox timing = DetailBox();
    private readonly TextBox log = DetailBox();
    private readonly TabControl detailTabs = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, RequestEntry> requests = new();
    private CaptureJournal? journal;
    private readonly string journalDirectory;
    private readonly string webViewDataDirectory;
    private readonly LinkedList<string> cacheOrder = new();
    private long cachedBytes;
    private long nextRowNumber;
    private bool storageFailed;
    private const long CacheBudget = 32 * 1024 * 1024;
    private const int MaxCachedRequests = 1000;
    private const int MaxCachedEventChars = 256 * 1024;
    private const int MaxBodyPreviewBytes = 4 * 1024 * 1024;
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
        journalDirectory = testMode ? Path.Combine(Path.GetTempPath(), "IENetworkInspector-ui-test-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IENetworkInspector", "Captures");
        webViewDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IENetworkInspector", "WebView2");
        Text = "IE Network Inspector";
        Font = new Font("Tahoma", 9F);
        ClientSize = new Size(1380, 860);
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(240, 243, 247);
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var menu = new MenuStrip { Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden, BackColor = Color.White };
        var fileMenu = new ToolStripMenuItem("&File");
        var exportMenuItem = new ToolStripMenuItem("Export JSONL...");
        exportMenuItem.Click += (_, _) => Export();
        var exitMenuItem = new ToolStripMenuItem("Exit");
        exitMenuItem.Click += (_, _) => Close();
        fileMenu.DropDownItems.AddRange(new ToolStripItem[] { exportMenuItem, new ToolStripSeparator(), exitMenuItem });
        fileMenu.DropDownOpening += (_, _) => exportMenuItem.Enabled = export.Enabled;
        var captureMenu = new ToolStripMenuItem("&Capture");
        var refreshMenuItem = new ToolStripMenuItem("Refresh Processes");
        refreshMenuItem.Click += async (_, _) => await RefreshProcesses();
        var startMenuItem = new ToolStripMenuItem("Start Capture");
        startMenuItem.Click += async (_, _) => await StartCapture();
        var stopMenuItem = new ToolStripMenuItem("Stop Capture");
        stopMenuItem.Click += (_, _) => StopCapture();
        var clearMenuItem = new ToolStripMenuItem("Clear Sessions");
        clearMenuItem.Click += (_, _) => ClearCapture();
        captureMenu.DropDownItems.AddRange(new ToolStripItem[] { refreshMenuItem, startMenuItem, stopMenuItem, new ToolStripSeparator(), clearMenuItem });
        captureMenu.DropDownOpening += (_, _) =>
        {
            refreshMenuItem.Enabled = refresh.Enabled;
            startMenuItem.Enabled = start.Enabled;
            stopMenuItem.Enabled = stop.Enabled;
            clearMenuItem.Enabled = clear.Enabled;
        };
        var viewMenu = new ToolStripMenuItem("&View");
        var statisticsMenuItem = new ToolStripMenuItem("Statistics");
        statisticsMenuItem.Click += (_, _) => detailTabs.SelectedIndex = 0;
        var inspectorsMenuItem = new ToolStripMenuItem("Inspectors");
        inspectorsMenuItem.Click += (_, _) => detailTabs.SelectedIndex = 1;
        var logMenuItem = new ToolStripMenuItem("Log");
        logMenuItem.Click += (_, _) => detailTabs.SelectedIndex = 2;
        var extendedColumnsMenuItem = new ToolStripMenuItem("Extended Session Columns") { CheckOnClick = true };
        extendedColumnsMenuItem.CheckedChanged += (_, _) =>
        {
            foreach (var name in new[] { "duration", "type", "time" })
                if (grid.Columns[name] is { } column) column.Visible = extendedColumnsMenuItem.Checked;
        };
        viewMenu.DropDownItems.AddRange(new ToolStripItem[] { statisticsMenuItem, inspectorsMenuItem, logMenuItem, new ToolStripSeparator(), extendedColumnsMenuItem });
        var helpMenu = new ToolStripMenuItem("&Help");
        var aboutMenuItem = new ToolStripMenuItem("About IE Network Inspector");
        aboutMenuItem.Click += (_, _) => MessageBox.Show(this, "IE Network Inspector\r\nHTTP diagnostics for approved IE-mode traffic.", "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        helpMenu.DropDownItems.Add(aboutMenuItem);
        menu.Items.AddRange(new ToolStripItem[] { fileMenu, captureMenu, viewMenu, helpMenu });
        MainMenuStrip = menu;
        layout.Controls.Add(menu, 0, 0);

        var targetBar = Bar();
        targetBar.Margin = Padding.Empty;
        targetBar.Padding = new Padding(8, 5, 8, 5);
        targetBar.BackColor = Color.FromArgb(248, 249, 251);
        start.Text = "Start Capture";
        StyleCommandButton(start, Color.FromArgb(24, 115, 64));
        StyleCommandButton(stop, Color.FromArgb(155, 48, 48));
        StyleCommandButton(chooseProcess);
        StyleCommandButton(refresh);
        StyleCommandButton(clear);
        StyleCommandButton(export);
        targetBar.Controls.Add(start);
        targetBar.Controls.Add(stop);
        targetBar.Controls.Add(Separator());
        targetBar.Controls.Add(Caption("Process / PID"));
        targetBar.Controls.Add(processes);
        targetBar.Controls.Add(chooseProcess);
        targetBar.Controls.Add(refresh);
        targetBar.Controls.Add(Separator());
        targetBar.Controls.Add(clear);
        targetBar.Controls.Add(export);
        layout.Controls.Add(targetBar, 0, 1);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 5,
            BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(140, 157, 177),
            Size = new Size(1200, 600), Panel1MinSize = 320, Panel2MinSize = 420
        };
        var sessionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        sessionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sessionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sessionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = Padding.Empty, Padding = new Padding(4), BackColor = Color.FromArgb(52, 67, 83) };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusFilter.Font = Font;
        statusFilter.Items.AddRange(new object[] { "All statuses", "2xx", "3xx", "4xx / 5xx", "No response" });
        statusFilter.SelectedIndex = 0;
        SizeComboBoxToContent(statusFilter);
        filter.Dock = DockStyle.Fill;
        filter.MinimumSize = new Size(140, 0);
        filter.Margin = new Padding(4, 1, 5, 1);
        filter.PlaceholderText = "Filter sessions by URL, host, method, or status";
        statusFilter.Margin = new Padding(0, 1, 5, 1);
        var resetFilter = new Button { Text = "Reset", AutoSize = true, Margin = Padding.Empty };
        StyleCommandButton(resetFilter);
        resetFilter.Click += (_, _) => { filter.Clear(); statusFilter.SelectedIndex = 0; };
        filters.Controls.Add(filter, 0, 0);
        filters.Controls.Add(statusFilter, 1, 0);
        filters.Controls.Add(resetFilter, 2, 0);
        sessionLayout.Controls.Add(grid, 0, 0);
        sessionLayout.Controls.Add(filters, 0, 1);
        split.Panel1.Controls.Add(sessionLayout);

        var inspectors = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(500, 600), Panel1MinSize = 150, Panel2MinSize = 150, SplitterWidth = 5, BackColor = Color.FromArgb(140, 157, 177) };
        inspectors.Panel1.BackColor = Color.FromArgb(245, 247, 250);
        inspectors.Panel2.BackColor = Color.FromArgb(245, 247, 250);
        inspectors.Panel1.Controls.Add(BuildRequestInspector());
        inspectors.Panel2.Controls.Add(BuildResponseInspector());
        var inspectLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        inspectLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inspectLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        inspectLayout.Controls.Add(inspectors, 0, 0);
        AddTab(detailTabs, "Statistics", timing);
        AddTab(detailTabs, "Inspectors", inspectLayout);
        AddTab(detailTabs, "Log", log);
        detailTabs.SelectedIndex = 1;
        split.Panel2.Controls.Add(detailTabs);
        layout.Controls.Add(split, 0, 2);

        var statusBar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty, Padding = new Padding(7, 4, 7, 4), BackColor = Color.FromArgb(232, 237, 243) };
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        state.AutoSize = false;
        state.AutoEllipsis = true;
        state.Dock = DockStyle.Fill;
        state.TextAlign = ContentAlignment.MiddleLeft;
        state.MinimumSize = new Size(80, 24);
        counter.Margin = new Padding(8, 4, 0, 0);
        statusBar.Controls.Add(state, 0, 0);
        statusBar.Controls.Add(counter, 1, 0);
        layout.Controls.Add(statusBar, 0, 3);
        Controls.Add(layout);
        AddColumn("number", "#", 38);
        AddColumn("status", "Status Code", 92);
        AddColumn("method", "Method", 64);
        AddColumn("protocol", "Protocol", 68);
        AddColumn("host", "Host", 150);
        AddColumn("url", "URL", 240, true);
        AddColumn("duration", "Time ms", 78, visible: false);
        AddColumn("type", "Content-Type", 125, visible: false);
        AddColumn("time", "Started", 98, visible: false);
        grid.RowTemplate.Height = 23;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(230, 235, 241);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(35, 48, 62);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Tahoma", 8.5F);
        grid.ColumnHeadersHeight = 27;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(224, 229, 235);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 251);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(207, 225, 246);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(20, 48, 82);
        tips.SetToolTip(processes, "Select an IE candidate or enter the request process PID. Candidates do not identify tabs.");
        tips.SetToolTip(chooseProcess, "Open a table showing PID, architecture and detection details.");
        tips.SetToolTip(start, "Capture bodies until stopped. Events are saved locally. Use approved test traffic only.");
        tips.SetToolTip(export, "Export all persisted events. Paths, custom headers and bodies may still contain sensitive data.");
        refresh.Click += async (_, _) => await RefreshProcesses();
        chooseProcess.Click += async (_, _) => await ChooseProcess();
        start.Click += async (_, _) => await StartCapture();
        stop.Click += (_, _) => StopCapture();
        clear.Click += (_, _) => ClearCapture();
        export.Click += (_, _) => Export();
        grid.SelectionChanged += (_, _) => ShowDetails();
        processes.TextChanged += (_, _) => tips.SetToolTip(processes, processes.Text);
        processes.DropDown += (_, _) => ResizeProcessDropDown();
        filter.TextChanged += (_, _) => ApplyFilters();
        statusFilter.SelectedIndexChanged += (_, _) => ApplyFilters();
        timer.Tick += (_, _) => DrainMessages();
        Shown += async (_, _) =>
        {
            split.SplitterDistance = (int)(split.Width * 0.44);
            inspectors.SplitterDistance = inspectors.Height / 2;
            if (!testMode)
            {
                _ = InitializeWebView();
                await RefreshProcesses();
            }
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
            ClearImage(responseImage, responseImageState);
            timer.Dispose(); tips.Dispose(); journal?.Dispose();
            if (testMode && Directory.Exists(journalDirectory)) Directory.Delete(journalDirectory, true);
        };
    }

    private static TextBox DetailBox() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Consolas", 9.5F), Margin = Padding.Empty };
    private static Label InspectorSummary() => new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(117, 127, 142), Padding = new Padding(6, 0, 8, 0) };
    private static DataGridView InspectorGrid(params string[] columns)
    {
        var table = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(235, 238, 242), ColumnHeadersHeight = 30,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            EnableHeadersVisualStyles = false, AllowUserToResizeRows = false
        };
        table.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(247, 248, 250);
        table.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(46, 54, 66);
        table.DefaultCellStyle.SelectionBackColor = Color.FromArgb(224, 234, 249);
        table.DefaultCellStyle.SelectionForeColor = Color.FromArgb(28, 38, 52);
        table.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 249, 251);
        table.RowTemplate.Height = 28;
        table.Paint += (_, eventArgs) =>
        {
            if (table.Rows.Count != 0) return;
            TextRenderer.DrawText(eventArgs.Graphics, "No data to display.", table.Font, table.ClientRectangle,
                Color.FromArgb(117, 127, 142), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        for (var index = 0; index < columns.Length; index++)
            table.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "column" + index, HeaderText = columns[index],
                AutoSizeMode = index == columns.Length - 1 ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
                Width = columns.Length == 2 ? 220 : 120, MinimumWidth = 70, SortMode = DataGridViewColumnSortMode.NotSortable
            });
        return table;
    }
    private static PictureBox ImageBox() => new() { Dock = DockStyle.Fill, BackColor = Color.White, SizeMode = PictureBoxSizeMode.Zoom };
    private static Label MediaState(string text = "No image selected.") => new() { Dock = DockStyle.Fill, Text = text, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Padding = new Padding(12), ForeColor = Color.FromArgb(75, 87, 100), BackColor = Color.White };
    private static WebView2 WebView() => new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
    private static FlowLayoutPanel Bar() => new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 4), BackColor = Color.FromArgb(246, 248, 250), Padding = new Padding(3) };
    private static Panel Separator() => new() { Width = 1, Height = 24, BackColor = Color.FromArgb(190, 198, 207), Margin = new Padding(7, 2, 7, 2) };
    private static void StyleCommandButton(Button button, Color? foreground = null)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(173, 182, 192);
        button.BackColor = Color.White;
        button.ForeColor = foreground ?? Color.FromArgb(35, 47, 60);
        button.Padding = new Padding(4, 1, 4, 1);
        button.Margin = new Padding(2);
    }
    private Control BuildRequestInspector()
    {
        var tabs = InspectorTabs();
        requestHeadersTab = AddTab(tabs, "Headers", requestHeadersGrid);
        requestParamsTab = AddTab(tabs, "Params", requestParamsGrid);
        requestCookiesTab = AddTab(tabs, "Cookies", requestCookiesGrid);
        AddTab(tabs, "Raw", requestRaw);
        AddTab(tabs, "Body", BodyInspector(body, requestBodyJson, requestHex, requestBodyXml, requestBodyJavaScript, requestFormData));
        AddTab(tabs, "Auth", requestAuth);
        return Inspector("Request", requestSummary, requestBadges, tabs);
    }
    private Control BuildResponseInspector()
    {
        var preview = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        responseImageView = ImageViewer(responseImage, responseImageState);
        responseWebView = WebViewer(responseWeb, responseWebState);
        preview.Controls.Add(responseWebView);
        preview.Controls.Add(responseImageView);
        responseImageView.Visible = false;
        var tabs = InspectorTabs();
        responseHeadersTab = AddTab(tabs, "Headers", responseHeadersGrid);
        responseCookiesTab = AddTab(tabs, "Cookies", responseCookiesGrid);
        AddTab(tabs, "Raw", responseRaw);
        AddTab(tabs, "Preview", preview);
        AddTab(tabs, "Body", BodyInspector(responseBody, responseBodyJson, responseHex, responseBodyXml, responseBodyJavaScript));
        return Inspector("Response", responseSummary, responseBadges, tabs);
    }
    private static Control Inspector(string title, Label summary, FlowLayoutPanel badges, TabControl tabs)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty, BackColor = Color.White };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Color.FromArgb(117, 127, 142), Margin = new Padding(7, 9, 4, 0) }, 0, 0);
        header.Controls.Add(summary, 1, 0);
        header.Controls.Add(badges, 2, 0);
        panel.Controls.Add(header, 0, 0);
        panel.Controls.Add(tabs, 0, 1);
        return panel;
    }
    private static TabControl InspectorTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9F), DrawMode = TabDrawMode.OwnerDrawFixed, Padding = new Point(12, 4) };
        tabs.DrawItem += DrawInspectorTab;
        return tabs;
    }
    private static FlowLayoutPanel InspectorBadges() => new() { AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 5, 6, 4) };
    private static void SetBadges(FlowLayoutPanel panel, params (string Text, Color Color)[] badges)
    {
        panel.SuspendLayout();
        panel.Controls.Clear();
        foreach (var badge in badges.Where(item => !string.IsNullOrWhiteSpace(item.Text)))
            panel.Controls.Add(new Label
            {
                Text = badge.Text, AutoSize = true, ForeColor = badge.Color,
                BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(5, 1, 5, 1), Margin = new Padding(3, 0, 0, 0), Font = new Font("Segoe UI", 8F)
            });
        panel.ResumeLayout();
    }
    private static void DrawInspectorTab(object? sender, DrawItemEventArgs eventArgs)
    {
        if (sender is not TabControl tabs) return;
        var selected = eventArgs.Index == tabs.SelectedIndex;
        eventArgs.Graphics.FillRectangle(Brushes.White, eventArgs.Bounds);
        TextRenderer.DrawText(eventArgs.Graphics, tabs.TabPages[eventArgs.Index].Text, tabs.Font,
            eventArgs.Bounds, Color.FromArgb(35, 41, 51), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (selected) eventArgs.Graphics.FillRectangle(new SolidBrush(Color.FromArgb(56, 113, 224)),
            new Rectangle(eventArgs.Bounds.Left + 3, eventArgs.Bounds.Bottom - 3, eventArgs.Bounds.Width - 6, 3));
    }
    private static Control BodyInspector(TextBox text, TextBox json, TextBox hex, TextBox xml, TextBox javascript, TextBox? formData = null)
    {
        var tabs = InspectorTabs();
        AddTab(tabs, "Text", text);
        AddTab(tabs, "JSON", json);
        AddTab(tabs, "HEX", hex);
        AddTab(tabs, "MessagePack", EmptyState("MessagePack decoding is not available for this captured body."));
        AddTab(tabs, "Protobuf", EmptyState("A schema is required to decode Protocol Buffers."));
        if (formData is not null) AddTab(tabs, "Form-Data", formData);
        AddTab(tabs, "XML", xml);
        AddTab(tabs, "JavaScript", javascript);
        return tabs;
    }
    private static Control EmptyState(string text) => new Label { Dock = DockStyle.Fill, Text = text, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(117, 127, 142), BackColor = Color.White };
    private static Control ImageViewer(PictureBox image, Label state)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        panel.Controls.Add(image);
        panel.Controls.Add(state);
        return panel;
    }
    private static Control WebViewer(WebView2 webView, Label state)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        panel.Controls.Add(webView);
        panel.Controls.Add(state);
        return panel;
    }
    private async Task<bool> InitializeWebView()
    {
        CoreWebView2Environment environment;
        try { environment = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewDataDirectory).ConfigureAwait(false); }
        catch (Exception error)
        {
            await RunOnUiThread(() =>
            {
                responseWeb.Enabled = false;
                responseWebState.Text = "WebView2 Runtime could not be initialized. See Log for details.";
                responseWebState.Visible = true;
                AppendLog($"WebView environment failed: 0x{error.HResult:X8}: {error.Message}");
                return Task.FromResult(false);
            }).ConfigureAwait(false);
            return false;
        }
        return await RunOnUiThread(async () =>
        {
            try
            {
                await responseWeb.EnsureCoreWebView2Async(environment);
                var core = responseWeb.CoreWebView2;
                core.Settings.IsScriptEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.IsWebMessageEnabled = false;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, eventArgs) =>
                {
                    var uri = eventArgs.Request.Uri;
                    if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        || uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return;
                    eventArgs.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
                };
                core.NavigationStarting += (_, eventArgs) =>
                {
                    if (!eventArgs.Uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                        && !eventArgs.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)) eventArgs.Cancel = true;
                };
                responseWeb.NavigationCompleted += (_, eventArgs) =>
                {
                    responseWebState.Text = eventArgs.IsSuccess ? "" : $"WebView navigation failed: {eventArgs.WebErrorStatus}";
                    responseWebState.Visible = !eventArgs.IsSuccess;
                };
                RenderWebView(responseWeb, responseWebState, responseWeb.Tag as string ?? WebMessage("Select a session with a captured HTML body."));
                return true;
            }
            catch (Exception error)
            {
                responseWeb.Enabled = false;
                responseWebState.Text = "WebView2 could not be initialized. See Log for details.";
                responseWebState.Visible = true;
                AppendLog($"WebView initialization failed: 0x{error.HResult:X8}: {error.Message}");
                return false;
            }
        }).ConfigureAwait(false);
    }
    private Task<T> RunOnUiThread<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(new Action(async () =>
        {
            try { completion.SetResult(await action()); }
            catch (Exception error) { completion.SetException(error); }
        }));
        return completion.Task;
    }
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.FromArgb(48, 59, 70), Margin = new Padding(3, 7, 8, 3) };
    private static TabPage AddTab(TabControl tabs, string text, Control content)
    {
        var tab = new TabPage(text) { Padding = Padding.Empty, BackColor = Color.White };
        tab.Controls.Add(content);
        tabs.TabPages.Add(tab);
        return tab;
    }
    private void AddColumn(string name, string title, int width, bool fill = false, bool visible = true) => grid.Columns.Add(new DataGridViewTextBoxColumn
    {
        Name = name, HeaderText = title, Width = width, MinimumWidth = fill ? 130 : width,
        AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.ColumnHeader,
        SortMode = DataGridViewColumnSortMode.NotSortable, Visible = visible
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
        start.Enabled = chooseProcess.Enabled = refresh.Enabled = false;
        state.Text = "Finding processes...";
        try
        {
            using var listing = Process.Start(Command("--list")) ?? throw new InvalidOperationException("Cannot launch process enumeration.");
            var stdout = listing.StandardOutput.ReadToEndAsync();
            var stderr = listing.StandardError.ReadToEndAsync();
            await listing.WaitForExitAsync();
            var hadPrevious = TryGetProcessId(processes.Text, out var previous);
            processes.Items.Clear();
            foreach (var line in (await stdout).Split('\n'))
            {
                var fields = line.Trim().Split('\t');
                if (fields.Length >= 3 && uint.TryParse(fields[0], out _))
                    processes.Items.Add(FormatProcessItem(fields));
            }
            var match = processes.Items.Cast<string>().FirstOrDefault(item => hadPrevious && TryGetProcessId(item, out var candidateId) && candidateId == previous);
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
            start.Enabled = chooseProcess.Enabled = refresh.Enabled = true;
            if (closePending) Close();
        }
    }

    private async Task ChooseProcess()
    {
        if (worker is not null || refreshing) return;
        refreshing = true;
        processes.Enabled = chooseProcess.Enabled = refresh.Enabled = start.Enabled = false;
        state.Text = "Finding processes...";
        try
        {
            var candidates = await Task.Run(() => Program.FindIeProcesses(false));
            if (candidates.Count == 0)
            {
                MessageBox.Show(this, "No IE-mode candidate processes were found.", "Choose process", MessageBoxButtons.OK, MessageBoxIcon.Information);
                state.Text = "No IE processes found. Open an IE-mode page, then click Refresh.";
                return;
            }
            if (ShowProcessChooser(candidates) is not { } selected) return;
            var display = ProcessDisplayText(selected);
            if (!processes.Items.Contains(display)) processes.Items.Add(display);
            processes.SelectedItem = display;
            state.Text = $"Selected PID {selected.ProcessId} ({selected.Architecture}).";
        }
        catch (Exception error) { ReportError(error); }
        finally
        {
            refreshing = false;
            processes.Enabled = chooseProcess.Enabled = refresh.Enabled = start.Enabled = true;
            if (closePending) Close();
        }
    }

    private Program.IeCandidate? ShowProcessChooser(IReadOnlyList<Program.IeCandidate> candidates)
    {
        using var dialog = new Form
        {
            Text = "Choose IE-mode process",
            Font = Font,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(980, 430),
            MinimumSize = new Size(720, 360),
            ShowInTaskbar = false
        };
        var candidateGrid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
        };
        candidateGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "pid", HeaderText = "PID", Width = 85 });
        candidateGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "process", HeaderText = "Process", Width = 110 });
        candidateGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "arch", HeaderText = "Arch", Width = 75 });
        candidateGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Page title / URL", MinimumWidth = 250, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        candidateGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "detection", HeaderText = "Detection", Width = 230 });
        foreach (var candidate in candidates)
        {
            var rowIndex = candidateGrid.Rows.Add(candidate.ProcessId, candidate.Name, candidate.Architecture, candidate.WindowTitle, candidate.Reason);
            candidateGrid.Rows[rowIndex].Tag = candidate;
        }
        if (candidateGrid.Rows.Count != 0) candidateGrid.Rows[0].Selected = true;
        var select = new Button { Text = "Select", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        StyleCommandButton(select, Color.FromArgb(24, 86, 140));
        StyleCommandButton(cancel);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        actions.Controls.Add(cancel);
        actions.Controls.Add(select);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(candidateGrid, 0, 0);
        layout.Controls.Add(actions, 0, 1);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = select;
        dialog.CancelButton = cancel;
        candidateGrid.CellDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0) dialog.DialogResult = DialogResult.OK;
        };
        return dialog.ShowDialog(this) == DialogResult.OK && candidateGrid.SelectedRows.Count != 0
            ? candidateGrid.SelectedRows[0].Tag as Program.IeCandidate : null;
    }

    private static string ProcessDisplayText(Program.IeCandidate candidate)
    {
        var architecture = candidate.Architecture.ToLowerInvariant() switch
        {
            "x86" => "32bit",
            "x64" => "64bit",
            "arm" => "ARM 32bit",
            "arm64" => "ARM64 64bit",
            _ => "Unknown architecture"
        };
        var prefix = $"{candidate.ProcessId}\uFF08{candidate.Name} {architecture}\uFF09";
        return string.IsNullOrWhiteSpace(candidate.WindowTitle) ? prefix : $"{prefix} | {candidate.WindowTitle}";
    }

    private static bool TryGetProcessId(string text, out uint processId)
    {
        processId = 0;
        var prefix = text.Split('|', 2)[0].Trim();
        var open = prefix.IndexOfAny(new[] { '(', '\uFF08' });
        if (open >= 0)
        {
            var close = prefix[open] == '(' ? ')' : '\uFF09';
            if (!prefix.EndsWith(close) || string.IsNullOrWhiteSpace(prefix[(open + 1)..^1])) return false;
            prefix = prefix[..open].Trim();
        }
        return uint.TryParse(prefix, NumberStyles.None, CultureInfo.InvariantCulture, out processId) && processId != 0;
    }

    private void ResizeProcessDropDown()
    {
        var itemWidths = processes.Items.Cast<object>()
            .Select(item => TextRenderer.MeasureText(processes.GetItemText(item), processes.Font,
                Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);
        var textWidth = string.IsNullOrEmpty(processes.Text) ? 0
            : TextRenderer.MeasureText(processes.Text, processes.Font,
                Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        var contentWidth = Math.Max(textWidth, itemWidths.DefaultIfEmpty(0).Max())
            + SystemInformation.VerticalScrollBarWidth + 24;
        var availableWidth = Math.Max(processes.Width, Screen.FromControl(this).WorkingArea.Width - 32);
        processes.DropDownWidth = Math.Min(Math.Max(processes.Width, contentWidth), availableWidth);
    }

    private static void SizeComboBoxToContent(ComboBox comboBox)
    {
        var contentWidth = comboBox.Items.Cast<object>()
            .Select(item => TextRenderer.MeasureText(comboBox.GetItemText(item), comboBox.Font,
                Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width)
            .DefaultIfEmpty(0)
            .Max() + SystemInformation.VerticalScrollBarWidth + 18;
        comboBox.MinimumSize = new Size(contentWidth, 0);
        comboBox.Width = contentWidth;
        comboBox.DropDownWidth = comboBox.Width;
    }

    private async Task StartCapture()
    {
        if (worker is not null || refreshing) return;
        if (!TryGetProcessId(processes.Text, out var processId))
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
            var captureCommand = CaptureCommand(processId);
            EnsureJournal();
            AppendLog($"Capture worker: {captureCommand.FileName}");
            worker = Process.Start(captureCommand)
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
            var exitCode = worker.ExitCode;
            var nativeAccessViolation = IsNativeAccessViolation(exitCode);
            if (nativeAccessViolation)
                AppendLog("The capture worker terminated with native access violation 0xC0000005 inside Windows HttpDiagnosticProvider.Start(). This is not a managed exception or a normal permission error. Compare the logged target/provider architectures, run an elevated --probe-http-self test, and retry with a matching architecture build. If self-probe also crashes, report the OS build to Microsoft.");
            state.Text = storageFailed ? "Disk write failed. Capture stopped; data may be incomplete. See Log."
                : exitCode == 0 ? "Capture finished. Events saved to disk."
                : exitCode == 2 ? "No events. Check the PID and reproduce a request."
                : nativeAccessViolation ? "Windows HTTP diagnostics crashed (0xC0000005). See Log."
                : "Capture failed. See Log for HRESULT and permissions.";
            var failed = exitCode is not 0 and not 2;
            state.ForeColor = failed || storageFailed ? Color.Firebrick : Color.FromArgb(23, 97, 68);
            if (failed || storageFailed) detailTabs.SelectedIndex = 2;
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

    private static string FormatProcessItem(string[] fields) => ProcessDisplayText(new Program.IeCandidate(
        uint.Parse(fields[0], CultureInfo.InvariantCulture), fields[1], fields[2], fields.Length >= 4 ? fields[3] : "",
        fields.Length >= 5 ? fields[4] : ""));

    private static async Task Pump(StreamReader reader, bool isError, ChannelWriter<(bool, string)> writer)
    {
        while (await reader.ReadLineAsync() is { } line)
            await writer.WriteAsync((isError, line));
    }

    private static ProcessStartInfo CaptureCommand(uint processId) =>
        CaptureCommand(processId, Program.GetProcessArchitecture(processId));

    private static ProcessStartInfo CaptureCommand(uint processId, string targetArchitecture)
    {
        var currentArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        var path = WorkerRouting.SelectExecutable(AppContext.BaseDirectory, targetArchitecture, currentArchitecture, File.Exists);
        var arguments = new[] { "--worker", processId.ToString(CultureInfo.InvariantCulture), "--continuous" };
        if (path is null) return Command(arguments);
        WorkerRouting.ValidateExecutable(path, targetArchitecture);
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

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
            var retained = Math.Min(chunk.Length, Math.Max(0, MaxBodyPreviewBytes - (int)preview.Data.Length));
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
            {
                entry.Row.Cells["host"].Value = uri.Authority;
                entry.Row.Cells["protocol"].Value = uri.Scheme.ToUpperInvariant();
            }
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
            ClearInspectors();
            selection.Text = "No session selected"; shownEntry = null; shownRevision = -1;
            return;
        }
        if (ReferenceEquals(shownEntry, entry) && shownRevision == entry.Revision) return;
        shownEntry = entry;
        shownRevision = entry.Revision;
        selection.Text = $"#{entry.Row.Cells["number"].Value}  {entry.Row.Cells["method"].Value}  {entry.Row.Cells["url"].Value}";
        var requestHeaderCount = FillHeaderGrid(requestHeadersGrid, entry, "request");
        var responseHeaderCount = FillHeaderGrid(responseHeadersGrid, entry, "response");
        var parameterCount = FillParameterGrid(requestParamsGrid, entry);
        var requestCookieCount = FillCookieGrid(requestCookiesGrid, entry, "request");
        var responseCookieCount = FillCookieGrid(responseCookiesGrid, entry, "response");
        requestHeadersTab.Text = $"Headers ({requestHeaderCount})";
        requestParamsTab.Text = $"Params ({parameterCount})";
        requestCookiesTab.Text = $"Cookies ({requestCookieCount})";
        responseHeadersTab.Text = $"Headers ({responseHeaderCount})";
        responseCookiesTab.Text = $"Cookies ({responseCookieCount})";
        var url = entry.Row.Cells["url"].Value?.ToString() ?? "Request URL unavailable";
        var method = entry.Row.Cells["method"].Value?.ToString() ?? "";
        var protocol = entry.Row.Cells["protocol"].Value?.ToString() ?? "";
        var status = entry.Row.Cells["status"].Value?.ToString() ?? "Pending";
        requestSummary.Text = url;
        responseSummary.Text = url;
        SetBadges(requestBadges, (protocol, Color.FromArgb(102, 116, 138)), (method, Color.FromArgb(102, 116, 138)));
        var statusColor = int.TryParse(status, out var statusCode) && statusCode >= 400 ? Color.Firebrick
            : int.TryParse(status, out statusCode) && statusCode >= 300 ? Color.FromArgb(45, 105, 210) : Color.FromArgb(20, 137, 74);
        SetBadges(responseBadges, (protocol, Color.FromArgb(102, 116, 138)), (status, statusColor));
        requestAuth.Text = SpecialHeadersText(entry, "request", name => name.Contains("Authorization", StringComparison.OrdinalIgnoreCase), "No request authentication headers received.");
        requestRaw.Text = RawText(entry, "request");
        responseRaw.Text = RawText(entry, "response");
        timing.Text = Format(entry, "completed");
        body.Text = DecodedBodyText(entry, "request");
        responseBody.Text = DecodedBodyText(entry, "response");
        requestBodyJson.Text = FormattedBodyText(entry, "request", "json");
        responseBodyJson.Text = FormattedBodyText(entry, "response", "json");
        requestBodyXml.Text = FormattedBodyText(entry, "request", "xml");
        responseBodyXml.Text = FormattedBodyText(entry, "response", "xml");
        requestBodyJavaScript.Text = DecodedBodyText(entry, "request");
        responseBodyJavaScript.Text = DecodedBodyText(entry, "response");
        requestFormData.Text = ParameterText(entry);
        var imageAvailable = SetImage(responseImage, responseImageState, entry, "response");
        responseImageView.Visible = imageAvailable;
        responseWebView.Visible = !imageAvailable;
        requestHex.Text = HexText(entry, "request");
        responseHex.Text = HexText(entry, "response");
        RenderWebView(responseWeb, responseWebState, WebDocument(entry, "response"));
    }

    private void ClearInspectors()
    {
        foreach (var table in new[] { requestHeadersGrid, requestParamsGrid, requestCookiesGrid, responseHeadersGrid, responseCookiesGrid }) table.Rows.Clear();
        foreach (var box in new[] { body, responseBody, requestBodyJson, responseBodyJson, requestBodyXml, responseBodyXml,
            requestBodyJavaScript, responseBodyJavaScript, requestFormData,
            requestHex, responseHex, requestAuth,
            requestRaw, responseRaw, timing }) box.Clear();
        requestHeadersTab.Text = "Headers";
        requestParamsTab.Text = "Params";
        requestCookiesTab.Text = "Cookies";
        responseHeadersTab.Text = "Headers";
        responseCookiesTab.Text = "Cookies";
        requestSummary.Text = responseSummary.Text = "";
        requestBadges.Controls.Clear();
        responseBadges.Controls.Clear();
        ClearImage(responseImage, responseImageState);
        responseImageView.Visible = false;
        responseWebView.Visible = true;
        RenderWebView(responseWeb, responseWebState, WebMessage("Select a session with a captured HTML body."));
    }

    private static int FillHeaderGrid(DataGridView table, RequestEntry entry, string kind)
    {
        table.Rows.Clear();
        if (!entry.Events.TryGetValue(kind, out var record) || record.GetProperty("kind").GetString() == "disk-only") return 0;
        if (kind == "request")
        {
            var method = record.TryGetProperty("method", out var methodValue) ? methodValue.GetString() ?? "" : "";
            var urlText = record.TryGetProperty("url", out var urlValue) ? urlValue.GetString() ?? "" : "";
            if (Uri.TryCreate(urlText, UriKind.Absolute, out var uri))
            {
                table.Rows.Add(":method", method);
                table.Rows.Add(":path", uri.PathAndQuery);
                table.Rows.Add(":authority", uri.Authority);
                table.Rows.Add(":scheme", uri.Scheme);
            }
        }
        foreach (var (name, value) in Headers(record)) table.Rows.Add(name.ToLowerInvariant(), value);
        table.ClearSelection();
        table.Invalidate();
        return table.Rows.Count;
    }

    private static int FillParameterGrid(DataGridView table, RequestEntry entry)
    {
        table.Rows.Clear();
        if (!entry.Events.TryGetValue("request", out var record) || !record.TryGetProperty("url", out var urlValue)
            || !Uri.TryCreate(urlValue.GetString(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query)) return 0;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? "" : pair[(separator + 1)..];
            table.Rows.Add(DecodeQuery(name), DecodeQuery(value));
        }
        table.ClearSelection();
        table.Invalidate();
        return table.Rows.Count;
    }

    private static string DecodeQuery(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static int FillCookieGrid(DataGridView table, RequestEntry entry, string kind)
    {
        table.Rows.Clear();
        if (!entry.Events.TryGetValue(kind, out var record)) return 0;
        foreach (var (_, value) in Headers(record).Where(header => header.Name.Contains("Cookie", StringComparison.OrdinalIgnoreCase)))
        {
            if (kind == "request")
            {
                foreach (var cookie in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = cookie.IndexOf('=');
                    table.Rows.Add(separator < 0 ? cookie.Trim() : cookie[..separator].Trim(), separator < 0 ? "" : cookie[(separator + 1)..].Trim());
                }
                continue;
            }
            var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var pairSeparator = parts[0].IndexOf('=');
            var values = new string[9];
            values[0] = pairSeparator < 0 ? parts[0] : parts[0][..pairSeparator];
            values[1] = pairSeparator < 0 ? "" : parts[0][(pairSeparator + 1)..];
            foreach (var attribute in parts.Skip(1))
            {
                var attributeSeparator = attribute.IndexOf('=');
                var attributeName = attributeSeparator < 0 ? attribute : attribute[..attributeSeparator];
                var attributeValue = attributeSeparator < 0 ? "Yes" : attribute[(attributeSeparator + 1)..];
                var column = attributeName.ToLowerInvariant() switch { "expires" => 2, "max-age" => 3, "domain" => 4, "path" => 5, "secure" => 6, "httponly" => 7, "samesite" => 8, _ => -1 };
                if (column >= 0) values[column] = attributeValue;
            }
            table.Rows.Add(values);
        }
        table.ClearSelection();
        table.Invalidate();
        return table.Rows.Count;
    }

    private static IEnumerable<(string Name, string Value)> Headers(JsonElement record)
    {
        foreach (var group in new[] { "headers", "contentHeaders" })
            if (record.TryGetProperty(group, out var values) && values.ValueKind == JsonValueKind.Object)
                foreach (var header in values.EnumerateObject()) yield return (header.Name, header.Value.GetString() ?? "");
    }

    private static string ParameterText(RequestEntry entry)
    {
        if (!entry.Events.TryGetValue("request", out var record) || !record.TryGetProperty("url", out var urlValue)
            || !Uri.TryCreate(urlValue.GetString(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query)) return "No form or query parameters captured.";
        return string.Join(Environment.NewLine, uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(DecodeQuery));
    }

    private string DecodedBodyText(RequestEntry entry, string direction)
    {
        var bytes = BodyBytes(entry, direction);
        if (bytes is null || bytes.Length == 0) return MissingBodyMessage(entry, direction, "body inspection");
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return "Binary body. Use HEX or Preview."; }
    }

    private string FormattedBodyText(RequestEntry entry, string direction, string format)
    {
        var text = DecodedBodyText(entry, direction);
        if (format == "json")
        {
            try { using var document = JsonDocument.Parse(text); return JsonSerializer.Serialize(document.RootElement, pretty); }
            catch (JsonException) { return "Body is not valid JSON."; }
        }
        try { return XDocument.Parse(text).ToString(); }
        catch (Exception error) when (error is System.Xml.XmlException or InvalidOperationException) { return "Body is not valid XML."; }
    }

    private static string HeaderText(RequestEntry entry, string kind)
    {
        if (!entry.Events.TryGetValue(kind, out var record))
        {
            var message = kind == "request" ? "No request event received. Method and request headers are unknown." : "No response event received. Status and response headers are unknown.";
            if (entry.Events.ContainsKey("completed")) message += "\r\nOnly the completed lifecycle event was observed. Capture may have attached after this request began; missing request/response/body data cannot be reconstructed. Check Statistics and the journal.";
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

    private static string SpecialHeadersText(RequestEntry entry, string kind, Func<string, bool> matches, string emptyMessage)
    {
        if (!entry.Events.TryGetValue(kind, out var record) || record.GetProperty("kind").GetString() == "disk-only")
            return emptyMessage;
        var text = new StringBuilder();
        foreach (var group in new[] { "headers", "contentHeaders" })
            if (record.TryGetProperty(group, out var values) && values.ValueKind == JsonValueKind.Object)
                foreach (var header in values.EnumerateObject())
                    if (matches(header.Name)) text.AppendLine($"{header.Name}: {header.Value.GetString()}");
        if (text.Length == 0) return emptyMessage;
        return text.ToString();
    }

    private string RawText(RequestEntry entry, string kind) =>
        "Reconstructed from captured metadata; this is not the original wire byte stream.\r\n\r\n"
        + HeaderText(entry, kind) + "\r\n" + BodyText(entry, kind);

    private string SyntaxText(RequestEntry entry, string direction)
    {
        var bytes = BodyBytes(entry, direction);
        if (bytes is null || bytes.Length == 0) return BodyText(entry, direction);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return "Body is not valid UTF-8. Use HexView."; }
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, pretty);
        }
        catch (JsonException) { }
        try { return XDocument.Parse(text).ToString(); }
        catch (Exception error) when (error is System.Xml.XmlException or InvalidOperationException) { }
        return "No JSON or XML syntax detected.\r\n\r\n" + text;
    }

    private string HexText(RequestEntry entry, string direction)
    {
        var bytes = BodyBytes(entry, direction);
        if (bytes is null || bytes.Length == 0) return BodyText(entry, direction);
        const int displayLimit = 64 * 1024;
        var length = Math.Min(bytes.Length, displayLimit);
        var text = new StringBuilder(length * 4);
        text.AppendLine($"{bytes.Length} captured preview bytes; showing {length}.").AppendLine();
        for (var offset = 0; offset < length; offset += 16)
        {
            text.Append(offset.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (var index = 0; index < 16; index++)
                text.Append(offset + index < length ? bytes[offset + index].ToString("X2", CultureInfo.InvariantCulture) + " " : "   ");
            text.Append(" ");
            for (var index = 0; index < 16 && offset + index < length; index++)
            {
                var value = bytes[offset + index];
                text.Append(value is >= 32 and <= 126 ? (char)value : '.');
            }
            text.AppendLine();
        }
        if (bytes.Length > length) text.AppendLine().Append("Hex display truncated; the journal retains all persisted body chunks.");
        return text.ToString();
    }

    private static byte[]? BodyBytes(RequestEntry entry, string direction)
    {
        if (entry.BodyPreviews.TryGetValue(direction, out var preview)) return preview.Data.ToArray();
        if (!entry.Events.TryGetValue("body:" + direction, out var value)
            || !value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String) return null;
        try { return Convert.FromBase64String(data.GetString() ?? ""); }
        catch (FormatException) { return null; }
    }

    private static string? ContentType(RequestEntry entry, string direction)
    {
        var kind = direction == "request" ? "request" : "response";
        if (!entry.Events.TryGetValue(kind, out var record)) return null;
        foreach (var group in new[] { "contentHeaders", "headers" })
            if (record.TryGetProperty(group, out var values) && values.ValueKind == JsonValueKind.Object)
                foreach (var header in values.EnumerateObject())
                    if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) return header.Value.GetString();
        return null;
    }

    private static bool SetImage(PictureBox imageBox, Label state, RequestEntry entry, string direction)
    {
        ClearImage(imageBox, state);
        var bytes = BodyBytes(entry, direction);
        if (bytes is null || bytes.Length == 0)
        {
            state.Text = MissingBodyMessage(entry, direction, "image preview");
            return false;
        }
        if (entry.BodyPreviews.TryGetValue(direction, out var preview)
            && (!entry.Events.TryGetValue("body:" + direction, out var summary)
                || !summary.TryGetProperty("streamEnded", out var ended) || ended.ValueKind != JsonValueKind.True
                || preview.BytesSeen != preview.Data.Length))
        {
            state.Text = $"Image preview unavailable: body is incomplete ({preview.Data.Length} of {preview.BytesSeen} bytes retained).";
            return false;
        }
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var source = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
            imageBox.Image = new Bitmap(source);
            state.Text = $"{source.RawFormat} | {source.Width} x {source.Height} | {bytes.Length:N0} bytes | {ContentType(entry, direction) ?? "content type unavailable"}";
            state.Visible = false;
            return true;
        }
        catch (ArgumentException)
        {
            state.Text = $"Captured body is not a supported image ({ContentType(entry, direction) ?? "content type unavailable"}).";
            return false;
        }
    }

    private static void ClearImage(PictureBox imageBox, Label state)
    {
        var image = imageBox.Image;
        imageBox.Image = null;
        image?.Dispose();
        state.Text = "No image selected.";
        state.Visible = true;
    }

    private static string WebDocument(RequestEntry entry, string direction)
    {
        var contentType = ContentType(entry, direction);
        var bytes = BodyBytes(entry, direction);
        if (bytes is null || bytes.Length == 0) return WebMessage(MissingBodyMessage(entry, direction, "HTML preview"));
        try
        {
            var html = new UTF8Encoding(false, true).GetString(bytes);
            var trimmed = html.AsSpan().TrimStart();
            var declaredHtml = contentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true
                || contentType?.Contains("application/xhtml+xml", StringComparison.OrdinalIgnoreCase) == true;
            var looksLikeHtml = trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<head", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<body", StringComparison.OrdinalIgnoreCase);
            return declaredHtml || looksLikeHtml ? html
                : WebMessage($"WebView is available for captured HTML bodies. Content-Type: {contentType ?? "unavailable"}");
        }
        catch (DecoderFallbackException) { return WebMessage("The captured HTML body is not valid UTF-8. Use HexView."); }
    }

    private static string WebMessage(string message) =>
        "<!doctype html><meta charset=\"utf-8\"><style>body{font:14px Tahoma,sans-serif;color:#344354;background:#fff;padding:16px}p{margin:0}</style><p>"
        + WebUtility.HtmlEncode(message) + "</p>";

    private static string MissingBodyMessage(RequestEntry entry, string direction, string preview)
    {
        if (direction == "response" && entry.Events.TryGetValue("response", out var response)
            && response.TryGetProperty("status", out var status) && status.TryGetInt32(out var statusCode)
            && statusCode == 304)
            return $"304 Not Modified has no response body for {preview}. The browser reused its local cache. Start capture first, clear the browser cache, then hard-refresh to obtain a 200 response body.";
        return $"No captured body is available for {preview}.";
    }

    private static void RenderWebView(WebView2 webView, Label state, string document)
    {
        webView.Tag = document;
        if (webView.CoreWebView2 is null)
        {
            state.Text = "Initializing WebView...";
            state.Visible = true;
            return;
        }
        state.Text = "Loading captured HTML...";
        state.Visible = true;
        webView.NavigateToString(document);
    }

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
        if (!entry.Events.TryGetValue("body:" + direction, out var value)) return MissingBodyMessage(entry, direction, "body inspection") + "\r\n";
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
        ClearInspectors(); log.Clear();
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
        processes.Enabled = chooseProcess.Enabled = refresh.Enabled = start.Enabled = clear.Enabled = !capturing;
        stop.Enabled = capturing;
        export.Enabled = !capturing && journal?.Count > 0;
    }

    private static bool IsNativeAccessViolation(int exitCode) => exitCode == NativeAccessViolationExitCode;

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
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        if (!IsNativeAccessViolation(unchecked((int)0xC0000005)) || IsNativeAccessViolation(1))
            throw new InvalidOperationException("Native access-violation exit classification failed.");
        if (FormatProcessItem(new[] { "19612", "iexplore", "x86", "candidate", IeWindowTitles.FormatTitle("https://www.163.com/ - \u7f51\u6613 - Internet Explorer") })
            != "19612\uFF08iexplore 32bit\uFF09 | \u7f51\u6613 - https://www.163.com/"
            || FormatProcessItem(new[] { "123", "iexplore", "x86", "candidate", "" }) != "123\uFF08iexplore 32bit\uFF09"
            || FormatProcessItem(new[] { "123", "iexplore", "x64", "candidate" }) != "123\uFF08iexplore 64bit\uFF09")
            throw new InvalidOperationException("Process display title/fallback failed.");
        WorkerRouting.SelfTest();
        var command = CaptureCommand(123, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
        if (!command.ArgumentList.TakeLast(3).SequenceEqual(new[] { "--worker", "123", "--continuous" }))
            throw new InvalidOperationException("UI must start continuous body capture.");
        var bundledX86 = Path.Combine(AppContext.BaseDirectory, "workers", "x86", "IENetworkInspector.exe");
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64
            && File.Exists(bundledX86))
        {
            var routed = CaptureCommand(123, "x86");
            if (routed.FileName != bundledX86 || routed.UseShellExecute || !routed.RedirectStandardInput
                || !routed.RedirectStandardOutput || !routed.RedirectStandardError
                || !routed.ArgumentList.SequenceEqual(new[] { "--worker", "123", "--continuous" }))
                throw new InvalidOperationException("Bundled x86 worker routing/IPC contract failed.");
            try { WorkerRouting.ValidateExecutable(bundledX86, "x64"); throw new InvalidOperationException("Wrong worker bitness accepted."); }
            catch (BadImageFormatException) { }
        }
        if (ProcessDisplayText(new Program.IeCandidate(123, "iexplore", "x86", "test"))
            != "123\uFF08iexplore 32bit\uFF09")
            throw new InvalidOperationException("Process selector must display process architecture.");
        var titled = new Program.IeCandidate(19612, "iexplore", "x86", "test", "Page - https://example.test/");
        if (ProcessDisplayText(titled) != "19612\uFF08iexplore 32bit\uFF09 | Page - https://example.test/"
            || titled.Architecture != "x86")
            throw new InvalidOperationException("Window title display must retain architecture metadata.");
        foreach (var input in new[] { "19612", " 19612 ", "19612 | old title", "19612(iexplore 32bit) | Page", ProcessDisplayText(titled) })
            if (!TryGetProcessId(input, out var parsed) || parsed != 19612)
                throw new InvalidOperationException("Process ID parsing failed for a supported display format.");
        foreach (var invalid in new[] { "", "0", "-1", "19612oops", "19612(iexplore", "19612()", "4294967296" })
            if (TryGetProcessId(invalid, out _)) throw new InvalidOperationException("Invalid process ID accepted.");
        foreach (var architecture in new[] { "x86", "x64", "ARM", "ARM64", "Unknown" })
        {
            var display = ProcessDisplayText(new Program.IeCandidate(123, "iexplore", architecture, "test"));
            if (!TryGetProcessId(display, out var parsed) || parsed != 123)
                throw new InvalidOperationException("Architecture fallback PID parsing failed.");
        }
        using var form = new CaptureForm(true);
        form.Show();
        Application.DoEvents();
        form.processes.Items.Add(ProcessDisplayText(new Program.IeCandidate(123, "iexplore", "x86", "test")));
        form.ResizeProcessDropDown();
        if (form.processes.DropDownWidth != form.processes.Width)
            throw new InvalidOperationException("Short process entries should not widen the drop-down popup.");
        form.processes.Items.Add(ProcessDisplayText(new Program.IeCandidate(456, "iexplore", "x64", "test", new string('W', 160))));
        form.ResizeProcessDropDown();
        if (form.processes.DropDownWidth <= form.processes.Width
            || form.processes.DropDownWidth > Screen.FromControl(form).WorkingArea.Width - 32)
            throw new InvalidOperationException("Long process entries must widen the drop-down within the working area.");
        form.processes.Items.Clear();
        static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));
        var root = (TableLayoutPanel)form.Controls[0];
        if (root.RowCount != 4 || root.GetControlFromPosition(0, 0) is not MenuStrip
            || root.GetRow(form.processes.Parent!) != 1 || form.export.Enabled)
            throw new InvalidOperationException("Fiddler-style menu/toolbar layout or initial Export state failed.");
        if (form.detailTabs.TabPages.Count != 3 || form.detailTabs.TabPages[0].Text != "Statistics"
            || form.detailTabs.TabPages[1].Text != "Inspectors" || form.detailTabs.SelectedIndex != 1
            || form.grid.Columns["duration"]?.Visible != false || form.grid.Columns["type"]?.Visible != false
            || form.grid.Columns["time"]?.Visible != false)
            throw new InvalidOperationException("Inspector tabs or compact session columns are not in their expected default state.");
        if (form.grid.Columns["status"]?.HeaderText != "Status Code"
            || form.grid.Columns["status"]?.Width < TextRenderer.MeasureText("Status Code", form.grid.ColumnHeadersDefaultCellStyle.Font).Width)
            throw new InvalidOperationException("Status Code column header is missing or clipped.");
        var expectedRequestTabs = new[] { "Headers", "Params", "Cookies", "Raw", "Body", "Auth" };
        var expectedResponseTabs = new[] { "Headers", "Cookies", "Raw", "Preview", "Body" };
        var expectedRequestBodyTabs = new[] { "Text", "JSON", "HEX", "MessagePack", "Protobuf", "Form-Data", "XML", "JavaScript" };
        var expectedResponseBodyTabs = new[] { "Text", "JSON", "HEX", "MessagePack", "Protobuf", "XML", "JavaScript" };
        var inspectorTabs = Descendants(form).OfType<TabControl>().Where(tabs => !ReferenceEquals(tabs, form.detailTabs)).ToArray();
        if (!inspectorTabs.Any(tabs => tabs.TabPages.Cast<TabPage>().Select(tab => tab.Text).SequenceEqual(expectedRequestTabs))
            || !inspectorTabs.Any(tabs => tabs.TabPages.Cast<TabPage>().Select(tab => tab.Text).SequenceEqual(expectedResponseTabs))
            || !inspectorTabs.Any(tabs => tabs.TabPages.Cast<TabPage>().Select(tab => tab.Text).SequenceEqual(expectedRequestBodyTabs))
            || !inspectorTabs.Any(tabs => tabs.TabPages.Cast<TabPage>().Select(tab => tab.Text).SequenceEqual(expectedResponseBodyTabs)))
            throw new InvalidOperationException("Request/Response inspector tabs are incomplete or out of order.");
        var expectedStatusWidth = form.statusFilter.Items.Cast<object>()
            .Select(item => TextRenderer.MeasureText(form.statusFilter.GetItemText(item), form.statusFilter.Font,
                Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width)
            .Max() + SystemInformation.VerticalScrollBarWidth + 18;
        if (form.statusFilter.Width < expectedStatusWidth)
            throw new InvalidOperationException($"Status filter does not fit its longest item: actual={form.statusFilter.Width}, expected={expectedStatusWidth}.");
        if (Descendants(form).Any(control => control is NumericUpDown or CheckBox))
            throw new InvalidOperationException("Removed capture options are still visible.");
        if (form.chooseProcess.Parent != form.processes.Parent || form.clear.Parent != form.processes.Parent
            || form.export.Parent != form.processes.Parent
            || Descendants(form).Any(control => control.Text == "Open capture directory"))
            throw new InvalidOperationException("Capture actions must share the process toolbar without a folder button.");
        form.AddRecord("""{"kind":"body","activityId":"sample","direction":"request","encoding":"base64","bytes":11,"truncated":false,"streamEnded":true,"data":"eyJvayI6dHJ1ZX0="}""");
        form.AddRecord("""{"kind":"body","activityId":"sample","direction":"response","encoding":"base64","bytes":2,"truncated":false,"streamEnded":true,"data":"T0s="}""");
        form.AddRecord("""{"kind":"request","activityId":"sample","timestamp":"2026-09-17T09:00:00Z","method":"GET","url":"https://example.test/status?token=secret","headers":{"Accept":"application/json","Authorization":"Bearer secret","Cookie":"session=secret"},"contentHeaders":{"Content-Type":"application/json"}}""");
        form.AddRecord("""{"kind":"response","activityId":"sample","timestamp":"2026-09-17T09:00:00.010Z","status":200,"headers":{"Set-Cookie":"session=updated"},"contentHeaders":{"Content-Type":"text/plain"}}""");
        form.AddRecord("""{"kind":"completed","activityId":"sample","requestSentTimestamp":"2026-09-17T09:00:00Z","responseCompletedTimestamp":"2026-09-17T09:00:00.025Z"}""");
        form.grid.Rows[0].Selected = true;
        form.ShowDetails();
        if (form.requests.Count != 1 || form.journal?.Count != 5 || !form.responseBody.Text.Contains("OK")
            || form.grid.Rows[0].Cells["duration"].Value?.ToString() != "25.0"
            || form.grid.Rows[0].Cells["protocol"].Value?.ToString() != "HTTPS")
            throw new InvalidOperationException("UI event correlation/detail test failed.");
        if (form.requestHeadersTab.Text != "Headers (8)" || form.requestParamsTab.Text != "Params (1)"
            || form.requestCookiesTab.Text != "Cookies (1)" || form.responseCookiesTab.Text != "Cookies (1)")
            throw new InvalidOperationException("Inspector counts must appear only for the selected session.");
        using var missing = JsonDocument.Parse("""{"requestSentTimestamp":null,"responseCompletedTimestamp":null}""");
        if (Duration(missing.RootElement) is not null) throw new InvalidOperationException("Null timestamp accepted.");
        form.AddRecord("""{"kind":"response","activityId":"failed","timestamp":"2026-09-17T09:00:01Z","status":503,"headers":{},"contentHeaders":{"Content-Type":"application/json"}}""");
        form.AddRecord("""{"kind":"request","activityId":"failed","timestamp":"2026-09-17T09:00:01Z","method":"POST","url":"https://example.test/api/orders","headers":{"Content-Type":"application/json"},"contentHeaders":{}}""");
        form.AddRecord("""{"kind":"request","activityId":"pending","timestamp":"2026-09-17T09:00:02Z","method":"GET","url":"https://static.example.test/site.css","headers":{},"contentHeaders":{}}""");
        form.statusFilter.SelectedIndex = 3;
        if (form.grid.Rows[0].Visible || !form.requests["failed"].Row.Visible || form.requests["pending"].Row.Visible || form.requestHeadersGrid.Rows.Count != 0)
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
        if (!form.requestHeadersGrid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells[0].Value?.ToString() == "accept")
            || !form.responseHeadersGrid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells[0].Value?.ToString() == "set-cookie")
            || !form.requestBodyJson.Text.Contains("\"ok\": true") || !form.responseHex.Text.Contains("4F 4B")
            || !form.requestAuth.Text.Contains("Authorization: Bearer secret")
            || !form.requestCookiesGrid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells[0].Value?.ToString() == "session")
            || !form.responseCookiesGrid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells[0].Value?.ToString() == "session")
            || !form.requestRaw.Text.Contains("not the original wire byte stream")
            || form.requestParamsGrid.Rows.Count != 1 || !form.responseBody.Text.Contains("OK"))
            throw new InvalidOperationException("Split inspectors test failed.");
        using (var sampleImage = new Bitmap(2, 2))
        using (var imageStream = new MemoryStream())
        {
            sampleImage.SetPixel(0, 0, Color.Red);
            sampleImage.Save(imageStream, System.Drawing.Imaging.ImageFormat.Png);
            form.AddRecord(JsonSerializer.Serialize(new { kind = "body", activityId = "image-sample", direction = "response", encoding = "base64", bytes = imageStream.Length, truncated = false, streamEnded = true, data = Convert.ToBase64String(imageStream.ToArray()) }));
        }
        form.AddRecord("""{"kind":"response","activityId":"image-sample","timestamp":"2026-09-17T09:00:03Z","status":200,"headers":{},"contentHeaders":{"Content-Type":"image/png"}}""");
        form.requests["image-sample"].Row.Selected = true;
        form.ShowDetails();
        if (form.responseImage.Image?.Size != new Size(2, 2) || !form.responseImageState.Text.Contains("image/png"))
            throw new InvalidOperationException("Preview failed to decode the captured response image.");
        var html = "<!doctype html><html><body><h1>Captured preview</h1><script>window.externalCall=true</script></body></html>";
        form.AddRecord(JsonSerializer.Serialize(new { kind = "body", activityId = "html-sample", direction = "response", encoding = "base64", bytes = Encoding.UTF8.GetByteCount(html), truncated = false, streamEnded = true, data = Convert.ToBase64String(Encoding.UTF8.GetBytes(html)) }));
        form.AddRecord("""{"kind":"response","activityId":"html-sample","timestamp":"2026-09-17T09:00:04Z","status":200,"headers":{},"contentHeaders":{"Content-Type":"text/html; charset=utf-8"}}""");
        form.requests["html-sample"].Row.Selected = true;
        form.ShowDetails();
        if (form.responseWeb.Tag is not string webDocument || !webDocument.Contains("Captured preview") || !webDocument.Contains("<script>"))
            throw new InvalidOperationException("WebView did not receive the captured HTML body.");
        var webViewInitialization = form.InitializeWebView();
        var webViewTimeout = Stopwatch.StartNew();
        while ((!webViewInitialization.IsCompleted || form.responseWebState.Visible) && webViewTimeout.Elapsed < TimeSpan.FromSeconds(20))
            Application.DoEvents();
        var webViewsInitialized = webViewInitialization.IsCompleted && webViewInitialization.GetAwaiter().GetResult();
        if (!webViewsInitialized || form.responseWeb.CoreWebView2 is null
            || form.responseWeb.CoreWebView2.Settings.IsScriptEnabled || form.responseWebState.Visible)
            throw new InvalidOperationException($"WebView2 initialization or isolated HTML navigation failed: task={webViewInitialization.Status}, initialized={webViewsInitialized}, responseCore={form.responseWeb.CoreWebView2 is not null}, scripts={form.responseWeb.CoreWebView2?.Settings.IsScriptEnabled}, stateVisible={form.responseWebState.Visible}, state={form.responseWebState.Text}, log={form.log.Text.Trim()}");
        form.AddRecord("""{"kind":"request","activityId":"cached-image","timestamp":"2026-09-17T09:00:05Z","method":"GET","url":"https://static.example.test/icon.png","headers":{},"contentHeaders":{}}""");
        form.AddRecord("""{"kind":"response","activityId":"cached-image","timestamp":"2026-09-17T09:00:05.010Z","status":304,"headers":{},"contentHeaders":{}}""");
        form.requests["cached-image"].Row.Selected = true;
        form.ShowDetails();
        if (!form.responseImageState.Text.Contains("304 Not Modified")
            || form.responseWeb.Tag is not string cachedWebDocument || !cachedWebDocument.Contains("304 Not Modified"))
            throw new InvalidOperationException("304 bodyless-response guidance is missing from media inspectors.");
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
        if (form.start.Enabled || form.chooseProcess.Enabled || !form.stop.Enabled || form.export.Enabled)
            throw new InvalidOperationException("Capture control state test failed.");
        form.SetCapturing(false);
        var bodyChunk = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('a', 32768)));
        for (var sequence = 0; sequence < 130; sequence++)
            form.AddRecord(JsonSerializer.Serialize(new { kind = "body-chunk", activityId = "sample", direction = "response", sequence, data = bodyChunk }));
        if (form.requests["sample"].BodyPreviews["response"].Data.Length != MaxBodyPreviewBytes)
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
        if (form.grid.Rows.Count != 0 || form.export.Enabled || form.requestHeadersGrid.Rows.Count != 0)
            throw new InvalidOperationException("Clear state test failed.");
        form.Close();
        Console.WriteLine("PASS: UI, response Preview, body formats, cache eviction, complete disk export, retained journals and viewport snapshots. No capture started.");
        return 0;
    }
}