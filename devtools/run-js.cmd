@echo off
setlocal
rem Prefer Bun for JS developer tools; fall back to Node only when Bun is absent.
rem Override for parity testing: set PROJECT_JS_RUNTIME=node (or bun).
rem A failed Bun process is a real failure: NEVER mask it by running Node.
if "%~1"=="" (
  echo Usage: run-js.cmd ^<javascript-file^> [arguments...] 1>&2
  exit /b 64
)
if /I "%PROJECT_JS_RUNTIME%"=="node" goto node
if /I "%PROJECT_JS_RUNTIME%"=="bun" goto bun
where bun.cmd >nul 2>&1
if errorlevel 1 goto node
:bun
bun.cmd %*
exit /b %ERRORLEVEL%
:node
node.exe %*
exit /b %ERRORLEVEL%
