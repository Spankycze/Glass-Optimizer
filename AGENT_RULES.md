# Spankyho Optimizer Agent Rules

- MANDATORY freshness gate: before stating a version or resuming work, run `pwsh -NoProfile -File 'V:\ChatGPT Projects\Spankyho Optimizer\get-live-project-state.ps1' -AsJson` on Spankyvole. Source, verified release, and installed version are distinct; a historical Library/chat note, build-time CURRENT_STATE.md, or old C: project path can NEVER override live V: checks. When offline say 'not verified', not an old number.

- Work through normal ChatGPT + DC Remote; do not use Work mode.
- First run the live version gate on Spankyvole; then read AGENTS.md and RESUME.md. Consult the newest NEXT_CHAT_HANDOFF.md entry if needed. PROJECT_STATE.md and SESSION_HANDOFF.md can contain historical status, so use them only as archives, never as active version authority.
- Use PROJECT_INDEX.json for code navigation before broad recursive scans.
- Use TASK_STATE.json / CHECKPOINT.json through ai-workflow.ps1 for multi-step work.
- Load only the skill relevant to the current task from skills/.
- For verbose commands use dc-smart.ps1 in Quiet mode by default.
- Keep ChatGPT/DC output tiny: never stream full build/test logs into chat; write them to disk and return only status + up to 8 important lines on failure.
- Prefer short operations and targeted reads; avoid long polling/streaming responses that can trigger ChatGPT stream recovery timeouts.
- Measure before optimizing; keep only measurable improvements.
- Prefer safe C#/.NET/native read paths over new PowerShell/WMI subprocesses when practical.
- Every mutation path needs timeout/cancellation/read-back/rollback where relevant.
- Validate significant changes with build-fast, targeted smoke and build-test.
- Update PERFORMANCE_BASELINE.md only from fresh measured runs.
- Keep releases gated by build-release.ps1 and leave only the current Desktop installer.
