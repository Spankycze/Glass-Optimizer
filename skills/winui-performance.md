# WinUI Performance Skill
- Measure UI-thread phases separately; investigate >5–10 ms, prioritize >20–30 ms.
- Move IO/system queries off UI thread; do not move equivalent work into startup.
- Reuse stable page/cache state only with explicit invalidation.
- Watch event handlers, DispatcherQueue callbacks, timers and retained visual trees.
- Validate cold + warm navigation and soak memory/handles.
