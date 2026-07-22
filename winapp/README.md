# Rodent

A lightweight, native **Windows** configuration app for Logitech mice and keyboards —
a leaner alternative to Logitech G HUB, built on the HID++ protocol.

Rodent is a ground-up C# reimplementation. It does **not** require Python, GTK, or
Logitech's own software; it talks to devices directly over Windows HID. The Solaar
project (Python, in the parent directory) is kept only as a protocol reference.

## Status

Working and verified on real hardware (Logitech G402).

- Device discovery over Windows HID (HID++ 2.0)
- Reads: device name, type, firmware version, battery (wireless devices)
- Configurable settings (read + write):
  - Sensitivity (DPI)
  - Report rate
  - Onboard memory profiles (host vs onboard mode)
- WPF GUI: device sidebar + data-driven settings cards, dark theme

Device and feature support grows incrementally. Adding a HID++ feature is a small,
self-contained change in `Rodent.Core`.

## Projects

| Project | What |
|---------|------|
| `Rodent.Core` | HID++ engine (device discovery, features, settings). Uses [HidSharp]. |
| `Rodent.App`  | WPF desktop GUI (.NET 8). |
| `Rodent.Probe`| Console harness for testing against real hardware. |

## Build & run

Requires the .NET 8 SDK.

```sh
dotnet run --project Rodent.App      # launch the GUI
dotnet run --project Rodent.Probe    # console: dump detected devices & settings
```

## Publish a single-file exe

```sh
dotnet publish Rodent.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true
```

## Architecture

Settings are **data-driven**: the engine exposes each capability as a generic
`Setting` (Toggle / Choice / Range) with `Read`/`Write` delegates. The GUI renders
a control purely from the setting's kind — no per-feature UI code. Adding a feature
means adding one builder method in `LogiDevice`; the GUI picks it up automatically.

[HidSharp]: https://www.nuget.org/packages/HidSharp
