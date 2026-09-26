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

See the [`samples/Devolutions.Terminal.Control.Sample`](https://github.com/Devolutions/devolutions-terminal/tree/main/samples/Devolutions.Terminal.Control.Sample)
project in the [Devolutions Terminal repository](https://github.com/Devolutions/devolutions-terminal)
for a full working app, plus the full source and documentation.
