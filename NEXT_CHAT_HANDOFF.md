# Glass Optimizer — předání, 13. 11. 2026

- Live stav: SOURCE `17.20.0`, VERIFIED `17.19.0`, INSTALLED `17.19.0.0` (vždy ověřit přes `get-live-project-state.ps1 -AsJson`, ne z tohoto souboru).
- 17.20.0 zdroj: partialy hotové (0 souborů nad 800 ř.), deduplikace 105 → 6 bloků (baseline 52), pracovní strom čistý (246 legacy CoreTests mimo compile allowlist smazáno, 116 testů zůstává). Větev `publish/17.2.0-production` je stále bez upstreamu a 82 ahead / 51 behind `origin/main`; release 17.20.0 neproběhl.
- Release `17.19.0`: strict `build-release` PASS (142,33 s), Compact 46,63 MB, SHA-256 `89B2E7726737A1D70843913CA6094D20F08EBF1895F9D993E1865DE2A358BA53`; `--silent-install` exit 0, HKLM `17.19.0`, Program Files DLL `17.19.0.0`.
- GitHub: release `v17.19.0` je latest s installerem, `README.md` a `CHANGELOG.md` (SHA assetů shodné s lokálními); `main` = commit `8d75e25` s README/CHANGELOG, tag `v17.19.0` na něm. URL: https://github.com/Spankycze/Glass-Optimizer/releases/tag/v17.19.0
- 17.19.0 opravil zastaralou kontrolu v `--visual-capture-smoke`: místo historické „15.4.“ porovnává čtyři viditelné řádky changelogu s bundlem; capture smoke prochází v obou tématech včetně Defender stránky.
- Tray feedback: každý výsledek akce je tmavý popup vpravo dole (`DesktopStatusNotificationWindow.cs`) — bez aktivace, mimo Alt+Tab, 7 s auto-hide, max 3 souběžné; inline `InfoBar` pryč.
- Defender: karta stavu ochrany (5 řádků z read-backu), jediná hero akce „Otevřít Windows Security“, sbalené Pokročilé moduly/akce; opraven ignorovaný restore point, neošetřené chyby disable/restore a zastaralé karty po změně Tamper Protection.
- Ověřeno: `build-fast`, `build-test`, UI holistic audit, Debug visual capture + layout + popup + `--release-ui-smoke`, Quality 43/43, readonly Core 24/24, instalovaná 17.19.0 visual layout + popup + visual capture smoke.
- Nejbližší bezpečný krok: sekce `## 17.20.0` je v changelogu otevřená jako placeholder — další změny zapisovat do ní, pak strict release a SHA-verified Compact instalace. Držet čtyři AC `Never`, neprůhledný tray a žádné diagnostické UI.
- Poznámka: `build-speed-audit` budget 12 s a jednorázové varování `smoke-service-bundle` 11 375 ms (limit 10 509 ms, release PASS).
