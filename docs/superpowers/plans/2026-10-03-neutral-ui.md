# Neutral Black UI Implementation Plan
Goal: Redesign non-Overview pages, sidebar and dialogs with restrained neutral depth.
Architecture: Preserve Overview/header appearance and hero geometry. Add cached neutral section brushes; scope the actual deferred titlebar construction to the original palette with one explicit call.
Tech stack: Existing C# .NET 10 / WinUI; no new dependencies.
Spec: User-approved prompt in this conversation, amended to remove mandatory blue.
Constraints: Original left-menu icons; unchanged actions, readback and rollback; no unmeasured speedup claims.
- [x] Capture before images and hash protected source/assets.
- [x] Add MainWindow.NeutralUi.cs with independent neutral palette and cached surfaces.
- [x] Scope shared construction styles and neutral control resources.
- [x] Compact navigation while preserving original icon assets and hero geometry.
- [x] Simplify section headers, cards, classic tweaks, network page and dialogs.
- [x] Fix and regression-test deferred header palette and cached badge refresh.
- [x] Pass build-fast, build-test, theme-render, capture, layout, size, scroll and release UI checks.
- [x] Compare before/after captures and audit protected hashes; only Overview source difference is the titlebar palette-scope call.
- [x] Measure seven paired WinUI/Avalonia prototype runs; retain WinUI for this workload.
- [x] Pass gated Release and compact installer builds for 15.4.603.
- [x] Verify Desktop installer SHA256 and retain one current Desktop setup.
Execution: Inline in existing user-authorized remote project with focused backups. Completed 2026-10-03; evidence in artifacts/ui-neutral-redesign and artifacts/ui-framework-probe.
