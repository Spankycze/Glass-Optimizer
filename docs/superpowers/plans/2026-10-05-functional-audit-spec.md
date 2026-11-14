Pracuj na projektu **Spankyho Optimizer** jako seniorní .NET/Windows desktop developer, Windows internals engineer, performance engineer, QA engineer a UI/UX debugger zároveň.

## HLAVNÍ CÍL

Teď NECHCI přidávat další funkce ani dělat kosmetické experimenty.

Chci provést **kompletní funkční audit a opravu celé aplikace**, aby skutečně fungovalo úplně všechno, co už Spankyho Optimizer obsahuje.

Projdi celý projekt systematicky od UI až po backend a oprav všechny chyby, nefunkční části, slepé handlery, nedokončené implementace, regresní chyby a místa, kde UI tvrdí něco jiného než reálný stav systému.

Výsledek musí být aplikace, kde:

- funguje každý popup,
- funguje každý switch,
- funguje každé tlačítko,
- funguje Apply,
- funguje Recommended,
- funguje detekce aktivního stavu,
- funguje rollback,
- fungují všechny Windows tweaky,
- fungují všechny PowerShell/CMD operace,
- fungují všechny sekce,
- funguje navigace,
- funguje async práce,
- nezůstávají viset procesy,
- nic se nezasekává,
- nic nehází neobsloužené chyby,
- UI vždy odpovídá skutečnému stavu Windows.

# 1. KOMPLETNÍ AUDIT UI INTERAKCÍ

Projdi všechny XAML/UI soubory, code-behind, ViewModely, commandy a event handlery.

Najdi a oprav:

- tlačítka bez funkce,
- switche, které lze přepnout, ale nic neprovedou,
- switche, které po restartu ukazují špatný stav,
- nefunkční click handlery,
- špatné bindingy,
- chybějící command bindingy,
- commandy s chybným CanExecute,
- overlaye nebo Glass vrstvy blokující kliknutí,
- problémy s hit testingem,
- špatný ZIndex,
- neklikatelné prvky,
- dvojité spuštění stejné akce,
- závody mezi UI a backendem,
- chyby při rychlém opakovaném klikání,
- UI prvky, které zůstanou disabled,
- nekonečné loading stavy,
- špatný stav progress indikátorů.

Každá interaktivní věc v aplikaci musí mít dohledatelnou a funkční cestu:

UI → command/handler → backend → reálná systémová změna → ověření výsledku → aktualizace UI.

Pokud některý krok chybí, oprav ho.

# 2. OPRAV VŠECHNY POPUPY A DIALOGY

Projdi úplně všechny popupy, dialogy, overlays, progress windows, confirm dialogy, chybová okna a pomocná okna.

Oprav zejména:

- popup nejde zavřít,
- X nefunguje,
- Esc nefunguje,
- kliknutí mimo popup jej rozbije,
- popup se otevře pod hlavním oknem,
- popup se otevře mimo obrazovku,
- popup není vycentrovaný,
- popup zablokuje aplikaci navždy,
- černé/neprůhledné pozadí místo správného Glass efektu,
- nečitelný text,
- ořezaný obsah,
- špatné DPI scaling,
- rozbité rohy,
- špatné paddingy,
- chybné titlebary,
- nesprávný backdrop,
- nefunkční tlačítka uvnitř popupu,
- progress se nehýbe,
- progress ukazuje jinou operaci než backend,
- popup se zavře dříve než skončí operace,
- popup zůstane po dokončení otevřený,
- více instancí stejného popupu,
- memory leaky po opakovaném otevírání.

Používej jednotný popup systém místo náhodných různých implementací.

Popup musí mít:

- jasný lifecycle,
- cancellation,
- error handling,
- správnou async podporu,
- Close/X,
- Esc tam, kde dává smysl,
- focus management,
- žádné blokování UI threadu.

# 3. SWITCH AUDIT

Projdi KAŽDÝ switch/toggle v celé aplikaci.

Pro každý switch ověř:

1. Jak se zjišťuje aktuální stav Windows.
2. Co přesně se stane při ON.
3. Co přesně se stane při OFF.
4. Jestli operace opravdu proběhla.
5. Jestli se výsledek následně znovu načte.
6. Jestli UI ukazuje skutečný stav, ne pouze poslední kliknutí uživatele.
7. Jestli stav přežije restart aplikace.
8. Jestli lze tweak bezpečně vrátit.

Nikdy nepovažuj switch za aktivní jen podle uloženého boolu v konfiguraci.

Stav musí být pokud možno detekován přímo ze systému:

- Registry,
- Services,
- Scheduled Tasks,
- PowerCfg,
- Windows Features,
- BCD,
- Explorer settings,
- Defender,
- NIC properties,
- GPU nastavení,
- Firefox preference,
- soubory,
- procesy,
- služby,
- další skutečné zdroje podle konkrétního tweaku.

# 4. AUDIT VŠECH TWEAKŮ

Projdi úplně všechny registry tweak definice, PowerShell skripty, CMD příkazy, WinAPI operace a další systémové změny.

Najdi:

- neexistující registry klíče,
- zastaralé cesty,
- špatné datové typy Registry hodnot,
- chybné DWORD/QWORD/String hodnoty,
- příkazy platné jen pro starší Windows,
- příkazy, které selhávají na Windows 11 26H2,
- tweaky vyžadující admina bez správné elevace,
- příkazy s chybným quotingem,
- problémy s cestami obsahujícími mezery,
- špatnou architekturu x86/x64,
- PowerShell syntax chyby,
- příkazy ignorující exit code,
- skripty, které hlásí úspěch i při chybě,
- slepé fire-and-forget procesy,
- tweaky aplikované vícekrát,
- konfliktní tweaky,
- tweaky, jejichž ON a OFF nejsou skutečné protiklady,
- rollback nastavující nesprávné default hodnoty.

Každý tweak musí mít jednoznačně definované:

- Detect(),
- Apply(),
- Rollback(),
- Verify().

Kde to dává smysl, převeď tweak systém na jednotný data-driven registry model místo stovek rozdílných improvizovaných handlerů.

# 5. APPLY / RECOMMENDED

Zkontroluj všechny:

- Apply,
- Apply All,
- Recommended,
- Restore,
- Reset,
- Default,
- Optimize,
- Fix,
- Clean

akce.

Oprav situace, kdy:

- tlačítko nic nedělá,
- část tweaků se přeskočí,
- jeden error ukončí celou dávku,
- progress nesedí,
- tweaky se provedou dvakrát,
- UI zamrzne,
- aplikace čeká na PowerShell,
- child proces nikdy neskončí,
- chyba se ztratí bez upozornění,
- výsledek ukazuje Success i při Failure.

Batch operace musí:

- mít cancellation,
- oddělený error handling pro jednotlivé položky,
- pokračovat bezpečně po chybě tam, kde to lze,
- na konci znovu detekovat skutečný stav,
- aktualizovat UI jen podle ověřeného výsledku.

# 6. POWERSHELL / CMD ENGINE

Projdi centrálně všechny spouštěné procesy.

Odstraň duplikované vlastní implementace Process.Start tam, kde mají být přes společný executor.

Executor musí řešit:

- hidden window,
- UTF-8,
- stdout,
- stderr,
- exit code,
- timeout,
- cancellation,
- admin elevation,
- quoting,
- argument escaping,
- deadlock prevention,
- cleanup procesu,
- cleanup child procesů,
- žádná náhodná CMD/PowerShell okna.

Nikdy nečekej synchronně na dlouhé operace na UI threadu.

Zakázat vzory typu:

.Wait()
.Result
Thread.Sleep()

na UI cestě, pokud blokují UI.

Používej správný async/await.

# 7. ERROR HANDLING

Najdi všechny:

- prázdné catch bloky,
- catch(Exception) bez reakce,
- ignorované Task exception,
- async void mimo UI eventy,
- fire-and-forget tasky bez error handlingu,
- nepozorované procesní chyby,
- null reference rizika,
- ObjectDisposedException,
- collection modified chyby,
- cross-thread UI access,
- race conditions.

Aplikace nesmí spadnout kvůli jedné neúspěšné optimalizaci.

Chyba konkrétního tweaku se musí izolovat.

# 8. NAVIGACE A NAČÍTÁNÍ SEKCÍ

Projdi všechny sekce levého menu.

Ověř minimálně:

- Overview,
- Power Plan,
- System Tweaks,
- Startup,
- Storage,
- Network,
- Firefox,
- Services,
- Task Scheduler,
- Drivers,
- Graphics,
- Games Tweaks,
- Cleaner,
- Debloater,
- Defender,
- Privacy & Security,
- AI Remover,
- Spotify,
- Windows Modernizer,
- Cursors,
- všechny další aktuálně přítomné sekce.

Každá se musí:

- otevřít napoprvé,
- otevřít opakovaně,
- neztrácet stav,
- neblokovat UI,
- nevytvářet duplicitní event subscription,
- nevytvářet memory leak,
- nezpůsobovat lag při navigaci.

Najdi hlavně regresi typu Firefox section not loading a podobné problémy.

# 9. DETEKCE STAVU

Kompletně oprav systém aktivních tweaků.

„Aktivní tweaky“ nesmí být založené na tom, co si aplikace myslí, že uživatel aplikoval.

Musí vycházet z reálného systému.

Po startu aplikace:

- paralelně a efektivně načti stav potřebných tweaků,
- nezablokuj Overview,
- nezpůsobuj blikání,
- nečekej zbytečně na pomalé PowerShell procesy,
- používej cache pouze tam, kde je bezpečná,
- postupně aktualizuj UI bez layout thrashingu.

# 10. ROLLBACK

Otestuj rollback každého tweaku.

Nikdy automaticky nepředpokládej, že Windows default je konkrétní hodnota.

Rozlišuj:

- odstranění hodnoty,
- obnovení původní hodnoty,
- Windows default,
- user-defined původní hodnotu.

Kde je potřeba, před prvním Apply zaznamenej původní stav a umožni ho obnovit.

# 11. THREADING A FREEZY

Najdi důvody všech freeze, zaseknutí a nereagujícího UI.

Hledej:

- sync I/O,
- sync registry crawling,
- pomalé WMI,
- dlouhý PowerShell na UI threadu,
- opakované Process.Start,
- zbytečné Task.Run,
- deadlocky,
- dispatcher misuse,
- nekonečné retry,
- timer overlap,
- současné spuštění stejného auditu vícekrát.

Dlouhé úlohy nesmí blokovat UI.

Zároveň nepřeháněj paralelizaci tak, aby aplikace sama spotřebovávala zbytečně CPU/RAM.

# 12. INSTANCE A PROCESY

Oprav:

- starou visící instanci,
- dvě spuštěné instance,
- zombie child procesy,
- PowerShell po zavření aplikace,
- pomocné procesy, které zůstanou na pozadí.

Použij spolehlivý single-instance mechanismus.

Nová instance nesmí náhodně zabíjet sama sebe nebo správnou aktuální instanci.

# 13. OVERVIEW

Oprav načítání Overview tak, aby se zobrazilo okamžitě a až potom se doplňovala dynamická data.

Žádné:

- prázdné Overview po startu,
- pozdní objevení headeru,
- blikání,
- skákání layoutu,
- GPU gauge, který se neaktualizuje,
- aktivní tweaky 0 kvůli chybě detekce,
- ořezané texty.

# 14. WINDOWS VERSION / COMPATIBILITY

Projekt testuj primárně proti aktuálnímu Windows 11 a podporuj současný systém projektu.

U systémových tweaků rozlišuj:

- dostupnost feature,
- konkrétní Windows build,
- edici Windows,
- admin oprávnění,
- hardware capability.

Nezobrazuj uživateli funkční switch pro tweak, který jeho systém objektivně nepodporuje.

# 15. SECURITY / ADMIN

Zkontroluj správnou elevaci.

Nechci, aby celá aplikace běžela jako admin bez důvodu, pokud lze admina vyžádat až pro konkrétní operaci.

Pokud tweak potřebuje admina:

- korektně elevate,
- počkej na výsledek,
- zjisti exit code,
- ověř výsledek.

# 16. PERFORMANCE AUDIT

Po opravě funkčnosti proveď výkonový audit samotné aplikace.

Priorita:

1. žádné crashe,
2. žádné deadlocky,
3. žádné freezes,
4. funkční tweaky,
5. funkční popupy,
6. správná detekce stavů,
7. až potom další performance optimalizace.

Zkontroluj:

- startup,
- cold navigation,
- warm navigation,
- popup open latency,
- switch latency,
- allocations,
- event subscription leaks,
- timers,
- dispatcher calls,
- registry access,
- process spawning,
- PowerShell overhead.

Neoptimalizuj naslepo.

# 17. REGRESNÍ TEST

Po opravách proveď vlastní kompletní regresní průchod.

Pro každou sekci:

- otevřít,
- zavřít/přejít jinam,
- znovu otevřít,
- použít jednotlivý switch,
- Apply,
- rollback,
- popup,
- error stav,
- opakovanou operaci.

Otestuj také:

- restart aplikace,
- restart Exploreru,
- operace bez admina,
- operace s adminem,
- rychlé klikání,
- otevření více popupů,
- zavření během běžící operace.

# 18. NEDĚLEJ FALEŠNÉ OPRAVY

Nestačí:

- změnit text,
- přidat try/catch,
- schovat error,
- hardcodovat switch na ON,
- zkrátit timeout,
- označit funkci za Success bez ověření,
- dočasně zakázat rozbitou funkci.

Najdi root cause.

# 19. PRACOVNÍ POSTUP

Neopravuj náhodně jednu věc po druhé.

Postupuj systematicky:

1. vytvoř mapu všech interaktivních funkcí,
2. najdi mrtvé handlery a broken bindings,
3. oprav centrální executory,
4. oprav tweak engine,
5. oprav detection/verification,
6. oprav rollback,
7. oprav popup systém,
8. oprav jednotlivé sekce,
9. proveď regresní audit,
10. buildni Release.

Při práci automaticky pokračuj dál bez čekání na moje „pokračuj“.

Dělej větší smysluplné dávky změn.

# 20. BUILD

Po dokončení:

- zvyš číslo buildu,
- proveď Clean + Release build,
- oprav všechny build warnings, které ukazují skutečný problém,
- spusť smoke test,
- ověř spuštění EXE,
- ověř single-instance,
- ověř navigaci,
- ověř základní tweaky,
- ověř popupy.

Nedělej nový build po každé malé změně. Buildni po větších logických blocích a finálně celý projekt.

# 21. DEFINITION OF DONE

Úkol NENÍ hotový jen proto, že projekt zkompiluje.

Hotový je až když:

- všechny sekce lze otevřít,
- všechna tlačítka reagují,
- všechny switche jsou funkční,
- Apply funguje,
- Recommended funguje,
- rollback funguje,
- popupy fungují,
- X a Esc fungují tam, kde mají,
- progress odpovídá realitě,
- stav tweaků odpovídá Windows,
- chyby nezamrazí aplikaci,
- nevisí child procesy,
- nejsou staré instance,
- po restartu aplikace se načte správný stav,
- neexistují známé mrtvé funkce maskované jen UI,
- Release build funguje.

Pokud při auditu najdeš další bug, který jsem explicitně nezmínil, rovnou ho oprav.

**Nezastavuj se po prvních nalezených chybách. Projdi celý Spankyho Optimizer. Prioritou je skutečná funkčnost a stabilita každé existující funkce.**