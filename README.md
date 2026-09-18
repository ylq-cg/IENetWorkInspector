# IE Network Inspector

A Windows desktop tool for investigating HTTP traffic from Internet Explorer
mode processes using the public `Windows.Web.Http.Diagnostics.HttpDiagnosticProvider`
API. It includes a session list, request/response inspectors, filtering, automatic
local persistence, JSONL export, and a command-line interface.

This is an experimental, independent tool, not an official Microsoft product
or a supported replacement for F12. It does not restore legacy F12 components,
install a service, configure a proxy, trust certificates, or change security policy.
The inspector layout is inspired by familiar network debugging tools; this
project is not affiliated with Fiddler or its publisher.

> **Privacy:** The desktop UI captures available request/response bodies by
> default and automatically writes events to disk. Use only traffic you are
> authorized to inspect. URLs and HTTP headers are stored without redaction,
> including cookies, authorization values, query strings and other secrets.

## Features

- Enumerate IE candidate processes or enter a target PID manually.
- Show each candidate process's executable architecture (`x86`, `x64`, ARM).
- Inspect sessions by method, host, URL, status, content type and request timing.
- Separate request and response Headers, Body and JSON views.
- Filter cached sessions by text, status class, or missing responses.
- Capture until manually stopped, with streamed body chunks and bounded previews.
- Persist every received event locally and export the full journal, including
  records evicted from the live grid.
- Inspect effective permissions and test HTTP API startup from the command line.

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11, with the HTTP
  diagnostics API available. The project targets `net9.0-windows10.0.19041.0`;
  this target is not a claim that every Windows release has been tested.
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) to build and use
  the launch script; the .NET 9 Windows Desktop Runtime to run a built application.
  .NET 9 is an older runtime target; review its support lifecycle before deployment.
- Microsoft Edge WebView2 Runtime for the isolated captured-HTML preview. It is
  normally installed with Microsoft Edge on supported Windows systems.
- An existing IE-mode test page and access to the process that issues its requests.
- Administrator approval at startup. The executable declares
  `requireAdministrator`, so Windows displays a UAC prompt when the current
  process is not elevated. Elevation alone does not guarantee access to every
  process or provider.

## Quick start

Clone the repository and launch from Command Prompt:

```cmd
git clone https://github.com/ylq-cg/IENetWorkInspector.git
cd IENetWorkInspector
Open-UI.cmd
```

The script builds the project and starts the newly built UI. Windows requests
administrator approval before the application opens. If a previous instance
locks the output files, save your results and close it before retrying.

1. For an existing IE-mode page, click **Refresh**, select its process, then
  click **Start Capture**. For a browser that is not open yet, click
  **Auto Capture** first and then open the IE-mode page.
2. Wait for the status to show **Capturing**.
3. Refresh the page or reproduce the operation under investigation.
4. Select a session to inspect its request, response and timing.
5. Click **Stop**, then **Export JSONL** to copy all persisted events.

Selecting an IE candidate is a heuristic, not proof that it handles the page's
network requests. No website or traffic capture starts automatically on UI launch.

## Avoid cached 304 responses

An HTTP `304 Not Modified` response intentionally contains no response body, so
ImageView, WebView and body inspectors cannot reconstruct the cached resource.
This application uses `HttpDiagnosticProvider`, a passive diagnostics API: it
cannot disable the browser cache, remove conditional headers such as
`If-None-Match` or `If-Modified-Since`, or turn a `304` into a `200`.

For the most reliable first-load capture without IEChooser:

1. Close every Microsoft Edge and Internet Explorer window and allow their
  background processes to exit. A running browser can retain an in-memory cache.
2. Start IE Network Inspector and approve the UAC prompt.
3. Click **Clear IE Cache** and confirm. This invokes the Windows Internet Options
  cache-only cleanup for the current user; it does not select cookies, history
  or saved passwords.
4. Click **Auto Capture** before opening the browser.
5. Open the target page in Edge IE mode. The application detects a new
  `iexplore.exe` every 20 ms, with a 100 ms MSHTML-in-Edge fallback, and starts
  capture automatically.
6. Wait for **Capturing**, then navigate or hard-refresh with `Ctrl+F5`.

Auto Capture reduces the process-start race but cannot guarantee that the very
first request is observed. A session containing only a `completed` event means
capture attached after that request had already started; missing request,
response and body events cannot be reconstructed.

For browser-controlled cache disabling, use the Windows IEChooser/F12 tool:

1. Open the target page in Edge IE mode.
2. Press `Win+R` and run:

  ```text
  C:\Windows\System32\F12\IEChooser.exe
  ```

  On 64-bit Windows, `C:\Windows\SysWOW64\F12\IEChooser.exe` may also be
  available.
3. Select the target IE-mode page in IEChooser.
4. Open its Network tool and enable **Always refresh from server** / disable
  cache, then keep IEChooser attached.
5. Start capture in IE Network Inspector and refresh the page.

IEChooser can control the IE engine's cache behavior because it is an internal
browser debugging tool. This project does not call private F12 interfaces and
does not reproduce that switch. If the server, an enterprise proxy or a CDN still
returns cached content, use a test URL with a unique query parameter when allowed.

## Public APIs

Application-facing calls use documented Windows SDK or public .NET/WinForms APIs.
The project builds with the standard .NET SDK and Windows SDK projection
(`Microsoft.Windows.SDK.NET.Ref`), without private SDKs, private COM interfaces,
system-source libraries, or a Visual Studio collector installation.

| Area | Public API used | Documentation |
| --- | --- | --- |
| HTTP capture | `HttpDiagnosticProvider.CreateFromProcessDiagnosticInfo`, `Start`, `Stop`, `RequestSent`, `ResponseReceived`, `RequestResponseCompleted` | [HttpDiagnosticProvider](https://learn.microsoft.com/en-us/uwp/api/windows.web.http.diagnostics.httpdiagnosticprovider) |
| Completion metadata | `ActivityId`, `RequestedUri`, `ProcessId`, `Timestamps` | [Completion event arguments](https://learn.microsoft.com/en-us/uwp/api/windows.web.http.diagnostics.httpdiagnosticproviderrequestresponsecompletedeventargs) |
| Request/response metadata | `Message`, `Timestamp`, `ActivityId`; public HTTP message properties and header collections | [Request event arguments](https://learn.microsoft.com/en-us/uwp/api/windows.web.http.diagnostics.httpdiagnosticproviderrequestsenteventargs), [response event arguments](https://learn.microsoft.com/en-us/uwp/api/windows.web.http.diagnostics.httpdiagnosticproviderresponsereceivedeventargs) |
| Timing | The nine connection/request/response timestamp properties | [HttpDiagnosticProviderRequestResponseTimestamps](https://learn.microsoft.com/en-us/uwp/api/windows.web.http.diagnostics.httpdiagnosticproviderrequestresponsetimestamps) |
| Process diagnostics | `ProcessDiagnosticInfo.GetForProcesses`, `GetForCurrentProcess`, `ProcessId` | [ProcessDiagnosticInfo](https://learn.microsoft.com/en-us/uwp/api/windows.system.diagnostics.processdiagnosticinfo) |
| API availability | `ApiInformation.IsTypePresent` | [ApiInformation](https://learn.microsoft.com/en-us/uwp/api/windows.foundation.metadata.apiinformation) |
| Bodies | `IHttpContent.ReadAsInputStreamAsync`, `IInputStream`, `AsTask`, `AsStreamForRead`, managed `Stream.ReadAsync` | [ReadAsInputStreamAsync](https://learn.microsoft.com/en-us/uwp/api/windows.web.http.ihttpcontent.readasinputstreamasync), [AsTask](https://learn.microsoft.com/en-us/dotnet/api/system.windowsruntimesystemextensions.astask), [AsStreamForRead](https://learn.microsoft.com/en-us/dotnet/api/system.io.windowsruntimestreamextensions.asstreamforread) |
| Process listing and worker | `Process.GetProcesses`, `ProcessName`, `Id`, `Modules`, `ProcessModule.ModuleName`, `Process.Start`, redirected streams, `WaitForExitAsync` | [Process](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process), [Modules](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.modules) |
| Permission diagnostics | `WindowsIdentity.GetCurrent`, `WindowsPrincipal.IsInRole`, `SecurityIdentifier`; documented Performance Log Users SID | [WindowsPrincipal](https://learn.microsoft.com/en-us/dotnet/api/system.security.principal.windowsprincipal), [well-known SIDs](https://learn.microsoft.com/en-us/windows/win32/secauthz/well-known-sids) |
| Desktop and persistence | Public WinForms controls/dialogs, System.Drawing, System.IO, System.Text.Json, Channels, Tasks and cancellation APIs | [.NET API reference](https://learn.microsoft.com/en-us/dotnet/api/), [WinForms](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/) |
| Build diagnostics | Public reflection over this demo's own assembly, path and module ID | [Assembly](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.assembly) |

`HttpDiagnosticsContract` is a documented Windows Desktop Extension SDK contract
(version 1, introduced in Windows 10). Its `Windows.*` namespace does not make
it a private OS interface. The framework's WinRT projection and the Windows
implementation may perform native/COM/ETW work internally; the demo consumes
the public contract rather than those implementation details.

There are no handwritten P/Invoke declarations or hardcoded system ETW provider
GUIDs/keywords in the current project. An offline self-test rejects direct native
imports in the application assembly. This is a conservative regression guard,
not a complete static proof or an audit of Microsoft runtime implementations.

Boundaries: recognizing `iexplore.exe` or `msedge.exe` with `mshtml.dll` is a
heuristic implemented using public process APIs, not an official IE-mode tab
discovery contract. Explicit PID entry remains available. Public API status
does not guarantee event completeness, permission to inspect every process,
compatibility with enterprise security software, ongoing runtime support or a
Microsoft product support commitment for this tool.

## Desktop UI

Double-click `Open-UI.cmd`, or run it from Command Prompt:

```cmd
cd IENetWorkInspector
Open-UI.cmd
```

The launcher builds into `bin\RequestMetadata` and opens that exact executable.
Each capture logs its assembly path and module ID for troubleshooting. If the
output is locked, the launcher stops after the build error instead of silently
starting a stale version. Save and close that window first.

No arguments also opens the UI. Refresh the process list, select an IE candidate
or type a PID, and click Start Capture. Reproduce the request after the status indicates
capture has started. Stop requests normal worker cleanup; closing the window
waits for cleanup rather than killing the worker. Windows requests administrator
approval before the UI starts, and its worker processes inherit that token.

Click **Choose...** to open a resizable process table with separate PID, process,
architecture and detection columns. Double-click a row to select it.

For a browser that is not running yet, click **Auto Capture** first and then open
the IE-mode page. The UI checks every 20 ms for a newly created `iexplore.exe`
and every 100 ms for an Edge process that loads MSHTML, then starts capture as
soon as it can attach. Existing candidates are ignored so
the new process can be identified unambiguously. This reduces startup loss but
cannot provide the zero-race guarantee of a system proxy.

The table correlates request, response, body and timing events by activity ID.
The Fiddler-style workspace has a menu and capture toolbar across the top,
sessions and a Quick filter bar on the left, and Statistics, Inspectors and Log
tabs on the right. Request and Response inspectors are stacked vertically.
Request provides Headers, TextView, SyntaxView, HexView, Auth, Cookies, Raw and
JSON. Response additionally provides ImageView and WebView because those views
render response resources rather than outbound request payloads. SyntaxView
formats captured JSON/XML bodies, ImageView decodes supported complete image
bodies, and HexView renders a bounded byte preview. WebView renders captured
HTML with scripts and external requests disabled. Auth/Cookies show captured
header values without redaction. Raw is
explicitly a reconstruction from public API metadata,
not original wire bytes. JSON shows the corresponding request/response event.
TextView shows a bounded UTF-8 preview (replacement characters can occur at chunk
boundaries or for non-UTF-8 encodings); it does not execute HTML.
The original body bytes remain in JSONL exports. Missing body events are not
presented as empty HTTP content. Drag the splitters to resize the panes.

Some activities deliver only `completed`, without request/response events. The
worker includes the completion event's full `RequestedUri` as `url` and
its `processId`. The grid uses this URL/host when no request event is available;
method, status and headers remain unknown, not guessed. A later request event
takes precedence. Rows without either URL source display a missing-information
placeholder. Inspectors show missing event types and the Activity ID. Old
journals that omitted completion URLs cannot recover them retroactively. This
fix does not establish why a given provider omitted request/response events.

Filter sessions by URL, host, method, status or content type and optionally by
2xx, 3xx, errors or pending responses. Filters only affect displayed rows;
export still includes ALL retained events, including filtered-out sessions.
The URL/text filter fills the available bar width, while the status selector is
sized from its longest item so labels such as **All statuses** remain visible.
The compact table shows session number, result, method, protocol, host and URL.
Use **View > Extended Session Columns** to also show duration, content type and
start time; narrow windows can scroll the columns horizontally.
ImageView, WebView and the other inspectors only display bodies for sessions
already present in the left list. Filter for `image`, `javascript` or a file name
to locate resource sessions. A missing row can mean the selected PID did not issue
the request, the browser served it from cache, or the diagnostics API emitted no event.
An HTTP `304 Not Modified` response has no response body; the browser uses its
local cached copy, which this process-scoped diagnostic stream does not expose.
Stop capture, click **Clear IE Cache**, start capture again, then hard-refresh to
obtain a `200` response if an ImageView/WebView body preview is required. The
button asks for confirmation and invokes the Windows Internet Options cache-only
cleanup for the current user; it does not select cookies, history or passwords.
Close every Edge/IE window and background process before clearing, then reopen
the browser. A running browser can retain validators in its in-memory cache.
Select a row for headers, body and statistics; the Log tab shows exact errors and
the worker's effective permissions. UI capture always includes available bodies;
there are no duration, body checkbox or body-size controls. Clicking Start begins
capture until you click Stop or close the window (errors can still terminate it).
The optional duration column measures request-sent to response-completed only when both
timestamps are valid; it is not total page-load or DNS-to-completion time.
Export copies ALL persisted JSON Lines after capture stops, including events
no longer cached in the UI. Starting again prompts before clearing the view.
Clear and starting a new capture retain previous journal files on disk.

The UI starts the same executable as a redirected CLI worker, inheriting its
permissions, and sends a stop command over stdin. UI mode uses `--continuous`:
no configured duration, event-count stop, body-size truncation, five-second body
timeout or 32 MiB retained-text stop. The transport queue remains bounded and
eight body reads may run concurrently; saturated reads are reported as skipped.
Completed read tasks are pruned. Continuous bodies are emitted as chunks of at
most 32 KiB, avoiding whole-body buffering in the worker. Stop cancels unfinished
reads; already emitted chunks remain available with an incomplete final state.
This does not guarantee complete capture or reliable 24x7 monitoring.

## Persistence and memory cache

Every UI event is appended to a unique UTF-8 JSONL journal under:

```text
%LOCALAPPDATA%\IeNetworkDemo\Captures
```

Clear, Clear IE Cache and Export are on the process-selection toolbar; there is
no directory button. Open the path above in File Explorer to access journals. Files are flushed
to the OS after each record, retained after Clear/new capture/window close,
and are not automatically deleted or rotated. Normal flushing is not a promise
of power-loss durability. Forced termination can lose queued/unflushed events
or leave an incomplete final record. The journal path is shown in the run log.

The live grid retains up to 1000 recently updated activities and approximately
32 MiB of accounted event/preview data, evicting the oldest cached activity.
Each body's preview retains at most 4 MiB; oversized non-chunk events remain
on disk with a placeholder in the inspector. These are cache limits, NOT capture
truncation limits. Total process memory can exceed the accounting budget due to
JSON objects, grid controls, strings, queues and OS buffers. Evicted activities
can reappear if later events arrive, but their in-memory details may be partial.
Filtering applies only to cached rows. Historical browsing/import is not yet
implemented; use the journals or full export for evicted data.

UI persistence uses a single writer. A disk/permission error requests a normal
capture stop and reports that subsequent queued events were not saved; the
existing file is retained. Export uses a temporary file then replaces the
destination only after copying completes, and rejects the active journal as a
destination. Monitor free space and manually delete retired journals after
closing their capture. No disk quota or automatic retention policy is imposed.

Journals are NOT encrypted by the demo and may include credentials, personal
information or business content. The directory inherits the current user's
local-app-data permissions; protect the files and apply an approved retention
policy. Persistence happens automatically, not only when Export is clicked.

### Continuous body event format

Continuous/UI mode emits `body-chunk` records containing `activityId`,
`direction`, zero-based `sequence`, `bytes`, `encoding: "base64"` and `data`.
Reconstruct by grouping activity ID and direction, verifying contiguous
sequence numbers, base64-decoding EACH chunk separately and concatenating the
decoded bytes. Do not concatenate base64 strings before decoding.
A final `body` record uses `encoding: "base64-chunks"`, the total `bytes` and
`chunks`, and `streamEnded: true` when the stream ends normally. Failed or
cancelled reads instead emit an error state; missing final records or sequence
gaps mean completeness is unknown. Older bounded CLI capture still emits a
single `body` record with inline base64 data. Export preserves this distinction.

## Build and verify

Run from the repository root:

```powershell
dotnet build -c Release
dotnet run -c Release --no-build -- --self-test
dotnet run -c Release --no-build -- --ui-self-test
dotnet run -c Release --no-build -- --help
```

`--self-test` checks options, full header/URL capture, body streaming/cancellation, native-import
absence, journal persistence and export. `--ui-self-test` uses synthetic events
to check filtering, missing metadata, inspectors, cache eviction and full export.
It briefly opens a window and writes two viewport screenshots beside the built
executable. Neither self-test captures network traffic; UI tests require an
interactive Windows desktop. These checks do not replace real-world validation.

For the commands below, `dotnet run` builds the default Debug configuration as
needed. The desktop launch script separately builds its own output directory.

## Diagnose access denied

Run this in the same terminal where capture failed:

```powershell
dotnet run -- --diagnose
```

This reports OS version, process architecture, enabled administrator and
Performance Log Users roles, and API type availability without starting ETW.
An enabled role does not prove access to every provider or target process.
A disabled administrator role can indicate that the executable manifest was
bypassed, for example by directly running the DLL. Normal executable launches
request elevation but do not change security policy. If the role is enabled but Start still fails,
the exact denied operation needs tracing; the HRESULT alone does not identify it.

To distinguish API startup failures from target-specific issues, test the
public HTTP API against only the demo process in the same terminal:

```powershell
dotnet run -- --probe-http-self
```

This creates a HttpDiagnosticProvider for the current process, calls Start and
then Stop if Start succeeds, without event subscriptions or HTTP requests.
It is an active API/session test, not just a read-only token query. Success does
not prove IE capture works; failure shows the problem also occurs without an IE
target. Keep the token and OS version consistent when comparing probe results.

## Capture approved test traffic

List IE candidates without starting capture:

```powershell
dotnet run -- --list
```

Automatically enumerate and select a target:

```powershell
dotnet run -- --auto --seconds 30 --body-bytes 16384 > capture.jsonl
```

With one candidate, capture starts automatically. With multiple candidates,
enter a listed PID at the prompt; Enter or end-of-input cancels. The candidate
list and prompt go to stderr, leaving stdout for JSON Lines. Do not redirect
stderr into the capture file. For unattended use, specify an explicit PID.

Detection includes `iexplore.exe` and `msedge.exe` processes with a loaded
`mshtml.dll`. IE candidates may be broker or content processes; this is not
tab identification or proof that a candidate issues requests. Access restrictions,
process exits or cross-bitness module inspection can make enumeration incomplete.
The application is already elevated before enumeration; no page-content inspection
is performed. Run enumeration again if
the page navigates to a new process. Other MSHTML hosts are not auto-selected.

Open the test site in Edge IE mode. Determine the PID that actually issues its
WinINet requests; do not assume it is the main Edge process. A PID is not a tab
filter: other traffic in the same process may also be captured. Replace `1234`:

```powershell
dotnet run -- 1234 --seconds 30 > capture.jsonl
```

Start the demo before reproducing requests. Ctrl+C stops early. The CLI default is
30 seconds, with bodies disabled. For controlled, non-sensitive GET/POST tests:

```powershell
dotnet run -- 1234 --seconds 30 --body-bytes 16384 > capture.jsonl
```

JSON Lines go to stdout and status messages to stderr. Match request, response,
completion and body records using `activityId`. All property names use camelCase.
Body records can arrive before their corresponding header record.
Bodies are base64, not necessarily text or decompressed. Truncation is reported.
Timestamps are the API's stage timestamps, not inferred latency measurements;
missing or default timestamps must not be treated as valid timing data.

Bounded CLI limits: 300 seconds, 2000 diagnostic callbacks, 64 KiB per body, 8 concurrent
body reads and 5 seconds per body. Saturated reads are skipped, not queued.
Stopping cancels pending reads; unavailable or incomplete bodies are not proof
of empty HTTP content. Limits do not bound all buffering inside Windows ETW.
For explicit manual-stop CLI capture with bodies enabled and no configured
duration/size limit, use `dotnet run -- 1234 --continuous`.

## Safety and interpretation

- Keep required enterprise security controls enabled. Compatibility with proxies,
  TLS inspection and endpoint security software must be validated in your environment.
- URLs, headers and captured bodies are not redacted. Cookies, authorization
  values, query strings, credentials and personal/business data may be written
  to journals and exports. Only use approved test traffic, protect exports and
  follow your retention policy.
- Access denied after elevation requires administrator review of
  ETW/process/provider permissions. The demo does not change group membership.
- A successful Start call does not prove event delivery or complete PID filtering.
  Validate against known server-side GET/POST records. No automatic reattachment,
  historical backfill, cross-process correlation or non-WinINet coverage is promised.
- In capture mode, exit 0 means some events were seen without a reported
  event/cleanup failure, not that capture was complete. Exit 2 means no events,
  no candidates, or cancelled selection. Exit 1 means an error. Listing, help
  and self-tests can exit 0 without starting capture.
- Normal stop calls Provider.Stop and waits for body operations. Forced process
  termination cannot guarantee cleanup. This experiment does not stop unrelated
  ETW sessions manually.

Recommended first test: an approved IE-mode page with known HTTPS GET and POST
payloads; compare URL, method, status, headers, body bytes and stage timestamps
while required security controls remain enabled. Repeat using the intended operator account.

## Project layout

| File | Purpose |
| --- | --- |
| `IeNetworkDemo.csproj` | Windows-targeted .NET / WinForms project |
| `Program.cs` | Entry points, process discovery, HTTP capture and CLI tests |
| `CaptureForm.cs` | Desktop inspectors, filtering, worker communication and UI tests |
| `CaptureJournal.cs` | Local JSONL journal and full-file export |
| `Open-UI.cmd` | Build-and-launch entry point for the desktop UI |

The repository intentionally excludes captures, exports, binaries, debug symbols
and build caches. Do not attach real traffic journals to public issues. Report
problems with the OS/runtime version, HRESULT, reproduction steps and sanitized
synthetic examples instead.