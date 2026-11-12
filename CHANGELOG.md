# Changelog

## 17.11.0

- Hardened GitHub release publishing to keep exactly the Compact installer and changelog assets.
- Updated the software updater to verify the installer through GitHub's asset SHA-256 digest.
- Preserved verified download, installation and rollback checks.

## 17.10.0

- Added verified update-popup progress milestones for download, verification and silent-installer handoff.
- Added driver installer completion read-back before reporting a successful update.
- Preserved retry, cancellation, stale-progress and popup lifecycle protections.

## 17.9.0

- Added verified update-popup progress milestones for download, verification and silent-installer handoff.
- Added driver installer completion read-back before reporting a successful update.
- Preserved retry, cancellation, stale-progress and popup lifecycle protections.

## 17.8.0

- Removed automatic game GPU priority and its runtime policy, service, UI toggle and smoke coverage.
- Preserved read-only Windows `GPU Priority` monitoring and all other guarded AI workload behavior.
- Preserved rollback protection and the four AC `Never` power-plan values.

## 17.7.0

- Fixed tray double-click activation so it opens the main application Overview through the existing single-instance handoff.
- Hardened fallback resolution of the installed main application path when the tray agent runs outside the application directory.

## 17.5.0

- Added automatic Czech or English selection from the Windows UI language when no saved language override exists.
- Preserved manual language selection and unified glass update, tray and system dialog styling.
- Fixed tray single-instance activation so the existing main window is restored and brought to the foreground.

## 17.4.0

- Refined the Overview into compact glass panels with live hardware values and charts.
- Improved shared glass surfaces, navigation visuals, popup interiors and sidebar icon clarity.
- Hardened progress-operation cleanup, cancellation and stale-result handling.
- Preserved guarded Windows changes, rollback behavior and the four AC `Never` power-plan values.

## 17.3.0

- Continued reliability, lifecycle and release validation improvements.

## 17.2.0

- Improved UI navigation, page caching, lifecycle handling and runtime smoke coverage.
- Hardened adaptive power, telemetry, AI workload and measured optimization paths with safer read-back and recovery behavior.
- Improved Fan Agent, tray popup, hardware telemetry, startup and worker reliability.
- Removed obsolete runtime, voice and legacy UI components while preserving supported features and rollback safety.
- Preserved the power-plan safety guard for the AC `Never` values.

## 17.0.0

- Updated the application major version to `17.0.0`.
- The `Glass Optimizer` name in the window title and Overview no longer displays a version number.
- Improved keyboard navigation and control activation, including verification of the actual foreground window.
- Hardened changelog validation during Overview refresh so edit history cannot overwrite released entries.
- Preserved the power-plan safety guard for the AC `Never` values.

## 16.340.0

- Fixed debounce timer lifetime when the main window closes.
- Fixed round-trip handling for the light, system and dark themes.
- Improved smoke validation for keyboard navigation and control activation.
- Updated changelog smoke validation to accept the latest released version below the current working version.
- Preserved the power-plan safety guard for the AC `Never` values.

## 16.339.0

- Increased the CPU Focus tray popup to `480×300` so complete fan and mode descriptions fit.
- Updated placement and pixel smoke validation for the new dimensions at 1×, 2× and 3× DPI.
- Preserved the Lian Li fan hardware gate for `0CF2:A102` and the unsupported-hardware fallback.

## 16.338.0

- Improved text readability in the CPU Focus tray popup.
- Increased and brightened the CPU, FAN, RPM and mode labels.
- Preserved the `0CF2:A102` hardware restriction and unsupported-hardware fallback.

## 16.337.0

- Changed the tray popup to the CPU Focus layout: no product header, dominant CPU temperature, compact fan panel and mode status.
- Shortened the fan label to fit the compact card while preserving the unsupported-hardware fallback.
- Verified tray popup rendering and the release service smoke check.

## 16.336.0

- Changed the tray popup to the CPU Focus layout with a dominant CPU temperature and compact fan card.
- Preserved the `0CF2:A102` Lian Li gate, unsupported-hardware fallback and GDI/DPI smoke coverage.

## 16.335.0

- Redesigned the tray popup with a layered Glass layout and clearer information hierarchy.
- Preserved the Lian Li `0CF2:A102` hardware gate and unsupported-hardware fallback.
- Verified GDI rendering at 1×, 2× and 3× DPI and popup resource stability.

## 16.334.0

- Added a quiet check for official GitHub releases approximately every five hours.
- Updates download only after a second SHA-256 verification and use the existing elevated installation flow.
- Added Czech / English language selection in Settings; Czech remains the default.
- Expanded English text for animation and Glass/Solid Dark theme settings.
- Preserved the Fan section restriction to the supported Lian Li `0CF2:A102` controller.
