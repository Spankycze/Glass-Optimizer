# Functional Audit Implementation Plan

> **For agentic workers:** Use systematic-debugging, test-driven-development and verification-before-completion. Independent demonstrated process/UI/NIC defects use dispatching-parallel-agents; integration remains in this session.

**Goal:** Audit every existing interaction and correct demonstrated functional, state, rollback and lifecycle defects without adding product features.
**Architecture:** Generate a source inventory with Roslyn and runtime catalog contracts; follow each mutation through detect/apply/read-back/revert. Isolate tests using disposable processes, HKCU keys and memory backends. Preserve approved Glass21 and all pre-existing edits.
**Tech Stack:** .NET 10, WinUI 3, native Windows APIs, registry, PowerShell, xUnit, Roslyn.
**Spec:** 2026-10-05-functional-audit-spec.md

## Global Constraints
- No new features or cosmetic experiments; batch meaningful fixes before app builds.
- Never claim success from cached bools or from UI-only smoke.
- Preserve current user's values, registry kinds and key/value absence during rollback.
- No unbounded child processes; cancellation, exit code and actual read-back govern UI results.
- Keep release gated by build-release.ps1; perform clean Release and fresh EXE/startup/instance/navigation checks.
- Ruling: execute continuously without a plan approval pause, as explicitly instructed by the user.
- Ruling: destructive cleanup/removal and security/network changes are exercised in isolation unless a recoverable live fixture exists; disclose untested live paths in coverage.

## Review Focus
- Cancellation after mutation begins: bounded cleanup, real state reconciliation and truthful transaction status.
- Corrupt/missing original backup: fail before overwriting evidence or reporting successful restore.
- Detect concurrent with Apply/Restore: no stale state published during mutation.
- Completed popup with disposed CTS: X/Escape must still close without error.
- NIC mixed IPv4/IPv6 and adapter changes: restore exact original adapter/property values.

## Tasks
- [ ] Inventory: tools/FunctionalAudit and artifacts/functional-audit. Export every control/event/binding/dialog/process site, containing method, source line and callees; review candidates and distinguish static coverage from runtime proof.
- [ ] Process executor: ProcessRunner.cs / UnelevatedProcessRunner.cs. RED disposable process tests for cancellation and descendant cleanup; implement bounded waits/cleanup; GREEN scoped tests. Inspect all call sites for ownership/exit-code/quoting, retain intentional detached launches.
- [ ] Tweak state/rollback: TweakEngine.cs / TweakFactory.cs / RegistryBackupStore.cs. CoreTests/TweakFunctionalAuditTests.cs RED stale detect, simultaneous restore/detect, corrupt backup tests; fix shared operation serialization, invalid state handling and fail-closed original restoration; test original value kinds and exact absence.
- [ ] Popup/UI: cursor operation single-flight and progress CTS lifecycle, Firefox truthful completion. Isolated regression tests and actual UI gates for focus/X/Escape/repetition/navigation.
- [ ] NIC: NativeNicTuningService.cs. Read-only WMI/provider reproduction; supported advanced-property setter, bounded executor, exact per-protocol/per-adapter rollback. Isolated RED/GREEN backend contracts; document any remaining live mutation verification.
- [ ] Section/backend sweep: all CatalogService.EnumerateTweakSources and Sections, dynamic/non-tweak mutation inventory. Review no-op delegates, optimistic success, swallowed errors, sync UI waits, feature/capability/admin gates and duplicate subscriptions; add narrow reproduction tests for each confirmed defect.
- [ ] Admin/instance: audit requireAdministrator manifest versus direct HKLM/service/BCD operations, elevated worker contracts and fan session bridge; verify single instance and no self-termination. Do not switch manifest without proving privileged paths still function.
- [ ] Integration: run Core/Quality/Architecture/Behavior/Concurrency/FaultInjection suites, catalog/read-only probes, all-section repeated navigation and popup/error/rollback gates. Review fresh failures and repair root causes.
- [ ] Release: clean outputs through established build contract, build/test significant batch and full gated Release, verify installed payload/fan health if installation follows existing authorized workflow, save screenshots and explicit coverage limitations.

## Evidence Ledger
- RED engine contracts: 3 failed / 1 passed in artifacts/functional-audit/engine-red.log (stale IsLoaded, overlapping restore/detect, corrupt backup swallowed).
- Agents independently reproduce cursor concurrent Apply, disposed cancellation close, shell/unelevated orphan processes and NIC projection/provider/rollback defects.
