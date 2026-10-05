# Spankyho Optimizer Agent Rules

- Work through normal ChatGPT + DC Remote; do not use Work mode.
- Read PROJECT_STATE.md first, then SESSION_HANDOFF.md; use WORK_PROGRESS.md only for needed detail.
- Use PROJECT_INDEX.json for code navigation before broad recursive scans.
- Use TASK_STATE.json / CHECKPOINT.json through ai-workflow.ps1 for multi-step work.
- Load only the skill relevant to the current task from skills/.
- For verbose commands use dc-smart.ps1.
- Measure before optimizing; keep only measurable improvements.
- Prefer safe C#/.NET/native read paths over new PowerShell/WMI subprocesses when practical.
- Every mutation path needs timeout/cancellation/read-back/rollback where relevant.
- Validate significant changes with build-fast, targeted smoke and build-test.
- Update PERFORMANCE_BASELINE.md only from fresh measured runs.
- Keep releases gated by build-release.ps1 and leave only the current Desktop installer.
