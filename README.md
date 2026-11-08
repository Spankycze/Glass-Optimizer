# Glass Optimizer

Glass Optimizer is a Windows utility with a compact interface and reversible automation controls.

## Verified release

[Glass Optimizer 16.323.0](https://github.com/Spankycze/Glass-Optimizer/releases/tag/v16.323.0)

The release contains only the verified compact installer, its SHA-256 checksum, and changelog. Source code is not included.

## Features

- **AI System Runtime:** local automatic management for selected Windows power-plan behavior, CPU process priorities, GPU game priorities, and selected background services or scheduled tasks. Each control has an enabled state and restore behavior.
- **Fan control:** the Ventilátory section appears and operates only when supported Lian Li UNI FAN SL-Infinity USB controller `0CF2:A102` is detected. Actions recheck hardware support.
- **AI Smart Runtime:** local fan telemetry logic uses temperature, package power, load, and RPM to estimate temperature changes, monitor anomalies, and adjust supported fan behavior. It does not require cloud processing.

## Screenshots

### Overview

![Glass Optimizer 16.323 overview](screenshots/glass-optimizer-overview.png)

### AI System Runtime

![Glass Optimizer AI System Runtime](screenshots/glass-optimizer-ai-runtime.png)

### Supported fan controls

![Glass Optimizer fan controls](screenshots/glass-optimizer-fans.png)

## Compatibility

Legacy internal installation, process, task, and registry identifiers remain unchanged for upgrade compatibility.