# Devolutions.Terminal.Control

A self-contained, NativeAOT-friendly Avalonia terminal control (`TermControl`)
extracted from [Devolutions Terminal](https://github.com/Devolutions/devolutions-terminal).

This package bundles the VT engine, renderer, connection layer (ConPTY on
Windows, PTY on Linux/macOS), and Windows Terminal-compatible settings parser
that `TermControl` depends on, so a single package reference is enough to
embed a fully functional terminal in any Avalonia application.

## Getting started

```xml
<ItemGroup>
  <PackageReference Include="Devolutions.Terminal.Control" Version="*" />
</ItemGroup>
```

`TermControl` has no parameterless constructor usable from XAML (its
constructor takes an optional `ITerminalEngine`), so create and start it in
code-behind instead of declaring it as a XAML element:

```csharp
using Devolutions.Terminal;
using Devolutions.Terminal.Settings;

var terminal = new TermControl();
Content = terminal;
await terminal.StartAsync(new ProfileSettings(), columns: 120, rows: 30);
```

The `columns` and `rows` passed to `StartAsync` set both the engine and PTY
initially, even if the control was arranged at a different size before startup.
Subsequent layout passes resize both to fit the available space. You can use
`TermControl.MeasureCell(profile, displayScale)` to calculate an initial grid
for the profile and display; pending layout changes made while the connection
starts are applied once it is ready.

## Stream an asciicast recording

The control can stream a recording to a caller-supplied WebSocket push URI while
retaining the recording locally for save or replay:

```csharp
// The application owns endpoint discovery and authentication. For a DVLS
// recording push URL, the signed token is already present in the query string.
var recordingUri = new Uri(
    "wss://gateway.example/jet/jrec/push/session-id?token=signed-token");

await terminal.StartRecordingAsync(recordingUri);
// Terminal output is now sent as asciicast v2 JSONL text frames.
await terminal.StopRecordingAsync();
```

`StartRecordingAsync` preserves existing query parameters and adds
`fileType=asciicast`. It opens a plain WebSocket without a subprotocol, sends
the v2 header first, then streams output events with elapsed timestamps and
CRLF-normalized text. The terminal's visible screen is included as the initial
frame. Pass the optional `path` argument to save the same recording to disk
when it stops.

For an official asciinema server, create a live stream through its HTTP API and
pass the returned `ws_producer_url` with the desired format:

```csharp
var producerUri = new Uri(streamResponse.WsProducerUrl);
await terminal.StartRecordingAsync(producerUri, AsciicastFormat.V3);
// Terminal output is sent using the v3.asciicast WebSocket subprotocol.
await terminal.StopRecordingAsync();
```

This overload supports both `AsciicastFormat.V2` and `AsciicastFormat.V3`,
negotiates the corresponding `v2.asciicast` or `v3.asciicast` subprotocol, and
leaves the producer URI unchanged. V2 streams use elapsed timestamps; v3
streams use per-event time deltas as required by the asciicast v3 format.

Endpoint construction, token acquisition, and authentication remain the host
application's responsibility. `RecordingStreamCompletion` can be awaited to
confirm that queued frames were flushed, and `RecordingStreamError` exposes the
last transport failure.

See the [`samples/Devolutions.Terminal.Control.Sample`](https://github.com/Devolutions/devolutions-terminal/tree/main/samples/Devolutions.Terminal.Control.Sample)
project in the [Devolutions Terminal repository](https://github.com/Devolutions/devolutions-terminal)
for a full working app, plus the full source and documentation.
