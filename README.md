# TinkerCAN

Fast LIN and CAN fuzzing, probing, replay, and signal generation for real hardware work.

TinkerCAN was built out of a practical need: existing tools were too slow, too opaque, or too awkward when the job was to brute-force a bus, enumerate unknown IDs quickly, keep background traffic running, and mutate payloads in real time. The original `LINTest-M` software was useful as a reference, but it is poorly documented and not well suited for aggressive discovery workflows. TinkerCAN exists to make that work fast.

## Why This Exists

When you are exploring an undocumented device or reverse-engineering traffic, the bottlenecks are usually not the bus itself. The bottlenecks are the tools:

- slow brute-force workflows
- poor logging and replay
- no easy way to keep a constant signal alive while probing
- no practical payload mutation system
- unclear behavior and missing documentation
- too much clicking for basic enumeration

TinkerCAN was designed around those problems first.

The goal is simple:

- send frames quickly
- brute-force IDs quickly
- mutate payloads quickly
- keep background traffic running while you probe
- capture responses immediately
- replay what worked
- save the session and come back later

## What TinkerCAN Is

TinkerCAN is a Windows-based toolset with two applications:

- `gui/`: interactive desktop UI for fast bus exploration, brute-forcing, replay, logging, and saved configs
- `cli/`: LIN command-line utility for scripted probing and quick terminal-driven work

This repository intentionally does not include `LINTest-M`. That project was reference material during development, not part of TinkerCAN itself.

## Supported Hardware

### LIN

The LIN side targets the `LINTest-M` / `LINTest-MI` style USB LIN dongle commonly sold on AliExpress.

- USB serial transport at `460800` baud
- LIN bus baud typically configured in the `4800` to `20000` range
- 16-byte serial command/response protocol

### CAN

The CAN side targets any USB CAN interface that exposes an `SLCAN` serial interface.

That means TinkerCAN is not tied to one specific CAN adapter. If the adapter speaks SLCAN, the GUI can use it.

Supported CAN modes in the GUI:

- standard CAN
- extended CAN
- remote frames
- CAN FD
- CAN FD with BRS

## Core Workflows

TinkerCAN is built for workflows like these:

- enumerate unknown LIN IDs and see which ones respond
- brute-force CAN ID ranges and watch for acknowledgements or payload responses
- keep a constant frame on the bus while probing other IDs
- replay hits from brute-force results without re-entering them
- loop frames with live payload mutation
- build multi-signal schedules instead of sending one frame at a time
- save a working setup as JSON and reload it later

## GUI Features

### LIN

- connect to a `LINTest-M` dongle and set LIN bus baud
- send one frame once or on a timed loop
- maintain multi-row LIN schedules with independent intervals
- choose classic or enhanced checksum modes
- brute-force ID ranges and capture responses
- optionally broadcast one payload across all scanned IDs
- optionally keep a constant background LIN signal running during scans
- replay brute-force hits once or in a loop
- export brute-force results to CSV
- save and load JSON session configs

### CAN

- connect to any `SLCAN` adapter over serial
- configure nominal bitrate, FD bitrate, silent mode, and auto-retransmit
- send standard, extended, remote, FD, and FD+BRS frames
- run timed loop transmission
- build multi-row CAN schedules
- brute-force CAN ID ranges
- sweep payload bytes while scanning IDs
- keep a constant CAN frame running during brute-force scans
- replay brute-force hits once or in a loop
- export brute-force results to CSV

## Modifiers

One of the main reasons this tool is faster to work with than typical vendor utilities is the modifier system.

Modifiers let you mutate the payload before every transmit without rewriting the frame manually. They are available in both the LIN and CAN GUI paths.

Supported features:

- byte references such as `D0`, `D1`, `D2`
- operators: `+ - * / % & | ^ ~`
- parentheses
- hex values such as `0x55`
- single assignment: `D0 = expr`
- range assignment: `D[0..7] = expr`
- multiple statements on separate lines or separated by `,` or `;`
- comments with `//` or `#`

Statements are applied in order to the current working buffer.

### Modifier Examples

```text
// Rolling counter
D0 = D0 + 1

// Mirror low nibble into another byte
D1 = D0 & 0x0F

// Derive one byte from another
D5 = D3 & 0x0F

// Fill the whole payload
D[0..7] = 0x55

// Multi-step mutation
D0 = D0 + 1
D1 = (D1 + 0x10) & 0xFF
```

Typical uses:

- rolling counters
- nibble experiments
- quick payload fuzzing
- repeated mutation during timed loops
- generating families of related frames without retyping data

## Brute Force Behavior

### LIN brute force

The LIN brute-force tab is designed for fast enumeration.

- scans an ID range
- uses a configurable byte step
- can sweep payload values from `00` to `FF`
- can switch to broadcast mode and send one fixed payload across all IDs
- can run a constant background signal during the scan
- records response presence and response data
- supports replay and CSV export from the results grid

This is aimed directly at the "find the live IDs fast" workflow.

### CAN brute force

The CAN brute-force path is designed for quick bus discovery without a lot of setup friction.

- scans an ID range
- supports standard, extended, remote, FD, and FD+BRS frame types
- can treat `INC` or repeated payload bytes as a sweep pattern
- can hold a constant background frame while probing
- captures response payloads seen during the configured receive window
- supports replay and CSV export

This is useful when you need to walk the bus aggressively and identify which IDs or payload families cause activity.

## CLI Features

The CLI focuses on LIN workflows and is intended for terminal use, scripting, and repeatable experiments.

Available commands:

- `ports`
- `send`
- `read`
- `monitor`
- `mode`
- `brute`
- `sweep`
- `interactive`
- `pid`
- `checksum`
- `frame`

## CLI Examples

List serial ports:

```powershell
dotnet run --project .\cli -- ports
```

Send a LIN frame:

```powershell
dotnet run --project .\cli -- --port COM3 send --id 0x22 --data "01 02 03 04" --len 4 --cs v2
```

Read a slave response:

```powershell
dotnet run --project .\cli -- --port COM3 read --id 0x3C --len 8
```

Brute-force all LIN IDs and print only responders:

```powershell
dotnet run --project .\cli -- --port COM3 brute --type read --rx-only --delay 100
```

Sweep one byte through a value range:

```powershell
dotnet run --project .\cli -- --port COM3 sweep --id 0x22 --pos 0 --lo 0x00 --hi 0xFF --rx-only
```

Open the interactive shell:

```powershell
dotnet run --project .\cli -- --port COM3 interactive
```

## Build

```powershell
dotnet restore .\TinkerCAN.sln
dotnet build .\TinkerCAN.sln
```

## Run

```powershell
dotnet run --project .\gui
dotnet run --project .\cli -- --help
```

## Repository Layout

```text
TinkerCAN.sln
gui/
cli/
```
