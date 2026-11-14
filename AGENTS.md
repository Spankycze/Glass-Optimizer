# Spankyho Optimizer — aktivní pravidla OpenCode

- DeepSeek Stack project-local skills: use .opencode/skills/ds-* on demand for relevant implementation, verification, regression, UI and prompt-scoping work. Existing project rules and user instructions always take precedence over third-party skills. Git/GitHub/PR workflows are allowed when relevant to the task; do not add unrelated, ledger/PROGRESS/PLAN files, background jobs or parallel model runs; do not change providers/models automatically. Prefer one focused skill at a time and only when it improves the work.

**Používej aktuální stav projektu, ne historický release report.** Aktivní `NEXT_CHAT_HANDOFF.md` a `WORK_PROGRESS.md` jsou stručné souhrny (cílově do 6 KB); při předání přepiš zastaralé pasáže, nepřidávej celou historii. Instalovanou verzi určuj přes live gate.

Původní úplný historický soubor: `%USERPROFILE%\.config\opencode\agent-history\Spankyho_Optimizer.AGENTS.original-86bf0014.md`. Čti pouze při konkrétní potřebě staršího rozhodnutí či regrese; ne jako aktuální stav.

## Stručná bezpečnostní pravidla
- Před tvrzením o verzi spusť get-live-project-state.ps1 -AsJson. Zdroj, ověřené vydání a instalovaná verze jsou odlišné skutečnosti.
- Zachovej rozdělané změny i nastavení Windows, nerozšiřuj diagnostické UI; Debloater řeš až nakonec.
- Vyžaduj skutečné měření přínosů automatické optimalizace. Změny musí být bezpečné, vratné a ověřené; žádné plošné vypínání kritických služeb/driverů.
- Zachovej čtyři uživatelem vyžádané AC Never, nezasahuj svévolně do prohlížečů ani živého power plánu.
- Nepřidávej testovací soubory/cases pro běžné změny, využívej stávající cílené testy a neobcházej žádnou striktní release bránu.

## CRITICAL: Live version check before EVERY status, continuation, or release claim
- Canonical source is `V:\ChatGPT Projects\Spankyho Optimizer` on the `Spankyvole` device; the old `C:\Users\spnka\Desktop\ChatGPT Projects\Spankyho Optimizer` path is obsolete. Do not silently use a fallback project.
- FIRST execute `pwsh -NoProfile -File 'V:\ChatGPT Projects\Spankyho Optimizer\get-live-project-state.ps1' -AsJson` on Spankyvole. Use its `SourceVersion`, `VerifiedReleaseVersion` and `InstalledVersion` as THREE DIFFERENT facts; never infer installed version from build/checkpoint/source or installer filename. If the device cannot be reached, say that the current version is unverified; do NOT promote a historical value.
- Library notes, chat summaries, search snippets, old `.ai-state` snapshots, `PROJECT_STATE.md` and previous handoffs (especially dated 2026-10-07 and versions 15.x) are HISTORICAL ONLY. Never identify them as current status. When contradictory, trust freshly observed device values and disclose any unresolved difference.
- `CURRENT_STATE.md` is written at build time and can lag a later installation; refresh the live state before reporting. Preserve all existing dirty/untracked files and Windows settings.
## Historical releases are not active instructions.
- Consult the existing project release metadata, the newest verified handoff, and the original history at the path above ONLY when needed for an explicit regression. Never infer current installed/source version from this section.

## PEVNÝ POŽADAVEK UŽIVATELE — ŽÁDNÉ DIAGNOSTICKÉ NOVINKY V UI
- Uživatel výslovně zakázal přidávat **jakékoli diagnostické panely, nové diagnostické funkce nebo informační/metrické bloky do aplikace**. Nenabízet ani nepřidávat žádné diagnostiky. Nově připravený `HW diagnostika` blok ve `MainWindow.PowerPlan.cs` byl okamžitě zrušen. Prováděj pouze skutečné produkční funkční/bezpečnostní/výkonnostní opravy a interní validaci stávajícími testy. **Debloater až úplně nakonec** — podle aktuální priority uživatele. Čtyři požadované hodnoty Never zachovat beze změny.
## PEVNÝ POŽADAVEK UŽIVATELE — Spankyho Performance časovače (2026-10-25)
- **Nikdy bez výslovného pokynu neodstraňovat ani nenahrazovat** powercfg nastavení `SUB_DISK DISKIDLE 0`, `SUB_VIDEO VIDEOIDLE 0`, `SUB_SLEEP STANDBYIDLE 0` a `SUB_SLEEP HIBERNATEIDLE 0` na AC. Uživatel výslovně chce všechny čtyři hodnoty `Never` v `PerformanceProfilesService` (apply, read-back, UI popis) **i** v `PowerPlanGuardService.BuildGuardScript` (čtyři `call :ensure`). Předchozí pokus je odstranit byl chybný a byl vrácen; nevracet se k němu. Ostatní úpravy Debloateru a EPP zachovat samostatně. Bez přímého požadavku nikdy neměnit živý power plán Windows.
## Active watch and build-speed rules
- Preserve automatic source-save checks and SHA-256 no-op caching in devtools/auto-watch-check.ps1; missing inputs fail closed and input hashes must be rechecked after tests. Maintain existing 9-source/116-test CoreTests allowlist and separate Quality checks. Compile-only is not release certification. Do not add test cases, duplicate watchers, startup tasks or weaken strict release gates. For measured historical timings and source allowlists consult the inactive history at %USERPROFILE%\.config\opencode\agent-history\Spankyho_Optimizer.watch-performance-history-20261025.md only when relevant.

## CRITICAL: STOP TEST BLOAT — user directive 2026-10-25
- User explicitly objects to thousands of tests and wants NO continual addition of test files/theory cases. Keep tools/CoreTests/CoreTests.csproj as a fixed 9-source compile allowlist, measured **116/116 passing** versus former **3,372 cases** (96.6% fewer). Of the 116, **24/24** are mandatory readonly release policy and **92/92** are the latest power-plan safety regressions; QualityTests **43/43** is separate. Do not expand default compilation, add test files or repeat combinatorial cases casually; modify/consolidate existing cases only when materially necessary, and remeasure test count. The 255+ untracked other CoreTests files are intentionally preserved as working tree data; don't delete/reset them. Existing mandatory release gating criteria remain untouched.
- devtools/auto-watch-check.ps1 skips excluded legacy files and runs targeted suites only; never launch the old 3,372-case CoreTests suite. One focused watch check previously passed in 1.8s. Ordinary successful build-fast.ps1 starts the singleton watcher automatically, no manual commands or Windows autostart. Background checks skip when this project's strict release runs, so they do not compete with release compilers.
## Mandatory safety and UX
- Chat + Desktop Commander Remote ONLY, no Work. Never restart Windows, Remote Desktop Commander, Kilo, Chrome, Firefox or change browser profiles/AutoPokračuj. Do not manually restart installed Optimizer or RuntimeHosts except as part of authorized official new-version installer; user's standing approval to install verified releases applies. GitHub integration, status/diff, branches, scoped commits, PRs and authorized pushes/releases are allowed. Destructive checkout/reset/stash, force deletion and overwriting unrelated dirty Git work remain disallowed.
- Do not perform live NIC, DNS, Green Ethernet, Windows registry, driver or service changes. Preserve Windows defaults and user settings. No RAM cleanup/optimization in this Optimizer; RAM Guard remains an independent project.
- Maintain approved native Dark Glass UI and dimensions; never unsolicited UI redesign. For UI/installer mockups, icons, screenshots, SVG/PNG and typography automatically load global skill `ui-graphics-toolkit` and use installed Playwright/Sharp/Resvg/Inkscape/ImageMagick as appropriate. Use `py -3.14` for Python, not the WindowsApps python alias. Do not add duplicate controls or extra scrollbars. User prefers simple, modern Czech UX and concise Czech reports of REAL code changes.
- Automated AI tweaking must be conservative, reversible, auditable, bounded and measured. Provide rollback/cancellation and read-back for mutating paths. No unsupported claims of CPU/RAM/FPS gain.
- Build/test meaningful software changes, not just discussion. Keep only the latest verified Compact Desktop installer. STANDING USER AUTHORIZATION (2026-10-25): always INSTALL every new fully verified strict release via SHA-verified official Compact --silent-install, through normal administrator UAC. Check installer exit, Program Files DLL version and HKLM uninstall version afterward; report failures explicitly. Installation may stop/restart Optimizer-owned processes and provision bundled features, but do not kill unrelated programs or bypass UAC. No simultaneous heavy builds.
## Ponytail principles - lean coding and review
- For coding, review and refactoring use the smallest complete change that solves the real task. Prefer reusing proven existing code over new abstractions, wrappers, agents or dependencies.
- Inspect only task-relevant files and diffs. Remove unnecessary code only when its lack of use is verified; never discard unfinished work, requested features or user data.
- Preserve the authoritative project safety, power-plan, security and strict release gates. Verify changes with existing targeted checks; do not trade away functionality or evidence for a smaller diff.
- Ponytail Full is available through the installed OpenCode plugin (@dietrichgebert/ponytail; use the version currently loaded by OpenCode). These project-local guidance rules complement the plugin and remain authoritative for safety and release checks; do not look for ../Ponytail DC/RULES.md or start an extra process.
## Rust native-core tooling
- For tasks touching native/Spanky.NativeCore, use the installed rust-analyzer LSP (OpenCode lsp enabled), rustfmt, Cargo and Clippy. Run targeted cargo check and cargo clippy only when code changes warrant it; preserve the existing strict build/release gates.
- cargo-audit 0.22.2 is available for dependency-security checks. Use it only for relevant dependency/security tasks; do not trigger remote advisory updates, broad dependency upgrades or extra persistent MCP servers for unrelated work. Do not automatically reformat untouched Rust files.
## Efficient repo workflow
- Read this short AGENTS.md, the live result of get-live-project-state.ps1, and the CURRENT concise NEXT_CHAT_HANDOFF.md + WORK_PROGRESS.md (each should stay under about 6 KB). Maintain the work heading with the exact current source version for the strict release preflight; keep the freshest update at the top of WORK_PROGRESS.md and the latest state in NEXT_CHAT_HANDOFF.md. When a handoff becomes long again, shorten/replace stale summaries instead of appending history. Read older release artefacts only for targeted regressions. Historical backup archives, old build outputs and caches were intentionally deleted on the user's 2026-10-25 explicit maximal-cleanup request. They must NOT be assumed to exist or recreated just as backups; use current source and necessary restore/build on demand.
- Prefer PROJECT_INDEX.json, atlas-map.md, smart-context.ps1, repository symbols, focused ripgrep and diff-first inspection; NEVER dump large one-line JSON/logs or recursively read whole project. Do not read a full report when a small grep is enough.
- Check live Directory.Build.props, .ai-state/CHECKPOINT.json, current installer/version and existing source changes before any release; Git inspection is allowed. Use existing build-fast.ps1, build-test.ps1, dev-cycle.ps1, compiled smokes, and strict build-release.ps1 when applicable, without bypassing existing gates.
- Use ai-workflow.ps1 / TASK_STATE.json for bounded progress tracking, report real test result and next step. Preserve staged and unstaged changes, do not erase concurrent work.
- Keep active handoff/progress updates SHORT (roughly 1-3 KB each); avoid accumulating old logs, backup directories or snapshots. User requested NO backup archives, but clarified that REQUIRED DEVELOPMENT DEPENDENCIES MUST STAY installed: preserve project node_modules, NuGet restore assets, working bin/obj build outputs and graphics assets. Do not delete these again merely to save disk space. Keep essential current project data and version-control metadata, not duplicate backup copies.
## Canonical active references
- NEXT_CHAT_HANDOFF.md: current release/source and immediate safe next step.
- WORK_PROGRESS.md: latest source work, validation, still-open items.
- Historical backup archives intentionally deleted at the user's explicit request; do not claim these files are available.

## OpenWork – jazyk a pracovní postup

- Se zadavatelem komunikuj výhradně česky. Kód, identifikátory, názvy knihoven a příkazy ponechávej v původním technickém zápisu.
- Dodržuj všechna stávající projektová pravidla v tomto souboru.
- Šetři tokeny: nejprve vyhledej relevantní soubory a používej cílené diffy, nezpracovávej bezdůvodně celý projekt.
- Navazuj automaticky dalšími bezpečnými kroky, dokud úloha není dokončená nebo není nutné rozhodnutí zadavatele.
- Opravy ověřuj dostupnými testy a nevydávej neověřené zrychlení za prokázané.
- Nevytvářej nevyžádané logy, diagnostické dumpy ani zálohy. Git a GitHub jsou povoleny pro práci na projektu (status, diff, větve, cílené commity, PR a vydání). Před zveřejněním ověř rozsah změn, licence a nepřítomnost tajných údajů; nepublikuj nesouvisející rozpracovanou práci.

<!-- OPENWORK_BOUNDED_CONTINUATION_V1 -->
### OpenWork: dokončení již schváleného úkolu
- U vícefázového úkolu pokračuj přirozeně dalšími schválenými kroky ve stejné relaci, dokud nesplníš zadání nebo nenarazíš na skutečnou překážku. Nevytvářej opakované zprávy „Pokračuj“, nové cíle ani další relace.
- Jestliže read-only nástroj vrátí jednoznačnou opravitelnou chybu (např. přesměrování identifikátoru dokumentace), oprav parametr a opakuj tento krok nejvýše jednou. Výsledky každého nástroje ověř před závěrem; netvrď úspěch po chybě.
- Při vyčerpání limitu API, nejasných oprávněních, požadavku na další placené volání mimo schválený rozsah, opakované chybě, rizikovém systémovém zásahu nebo nejasném cíli se zastav a uveď konkrétní překážku.
- Nespouštěj automaticky další nesouvisející úkoly, instalátory ani releasy. Respektuj stávající projektová pravidla o testech, soukromí, bez zbytečných souborů a nízké režii.
<!-- OPENWORK_NO_PROGRESS_GUARD_V1 -->
### Omezení opakování a spotřeby tokenů
- Každé volání nástroje musí přispět ke konkrétnímu ověřitelnému kroku. Pokud se nic nezměnilo, znovu nenačítej stejný soubor, neposílej identický příkaz ani neopakuj stejnou opravu bez nové hypotézy.
- Při delší práci po přibližně 20 nástrojových krocích vnitřně ověř dosavadní pokrok, další nutný krok a přínos pokračování. Neposílej prázdné zprávy „Pokračuji“, neotvírej nové relace, nevytvářej checkpoint soubory ani logy.
- Jednoduché opravitelné selhání zkus napravit nejvýše jednou stejným přístupem. Po druhém stejném neúspěchu změň přístup; pokud není bezpečná alternativa, zastav se a přesně popiš blokaci. Nikdy nehlásit Done bez skutečného ověření.
- Po chybě 401/403/429 či dosažení kvóty nedělej automatické další placené pokusy. Nepřepínej modely ani poskytovatele bez schválené strategie. Respektuj schválení oprávnění a všechny stávající rozpočtové limity, zvláště Mission budget u Coder Heaven.
- Zachovej autonomní pokračování uvnitř již schváleného úkolu, ale nezahajuj nesouvisející práci, publikování releasů nebo instalační operace. Sdílej jen konečný užitečný výsledek nebo skutečnou překážku.