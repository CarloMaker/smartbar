@echo off
setlocal
rem publish.bat -- pubblica una versione di SmartBar su GitHub.
rem   1. chiede la versione, nella forma numero.numero.numero (per esempio 1.0.1);
rem   2. se nel repository ci sono modifiche non ancora salvate, le mette in un commit (chiede il messaggio);
rem   3. compila SmartBar.exe con dentro quel numero di versione: e' un file solo e si avvia anche senza .NET;
rem   4. mette il tag vX.Y.Z e manda su GitHub il ramo e il tag;
rem   5. crea la release su GitHub, con SmartBar.exe allegato e le note ricavate dai commit.
rem La prima volta serve collegare GitHub: lo script lo propone (apre il browser, una volta sola).
rem Se la parte su GitHub fallisce, si rilancia con lo stesso numero di versione: ritrova il tag e rifa la release.
rem L'eseguibile resta anche in artifacts\SmartBar-vX.Y.Z\ (la copia in dist non si tocca).

cd /d "%~dp0"
title SmartBar: pubblicazione

where dotnet >nul 2>nul
if errorlevel 1 goto :no_dotnet
where gh >nul 2>nul
if errorlevel 1 goto :no_gh
git rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 goto :no_git

echo === SmartBar: pubblicazione di una versione ===
echo.

rem --- il collegamento a GitHub: senza, la release non si puo' creare ---
rem (call davanti a gh e a dotnet: se fossero installati come file .cmd, senza call lo script non tornerebbe piu' qui)
call gh auth status >nul 2>nul
if not errorlevel 1 goto :modifiche
echo GitHub CLI non e' collegato all'account GitHub.
choice /c SN /n /m "Lo collego ora? Apre il browser e si fa una volta sola [S/N]: "
if not errorlevel 1 goto :annullata
if errorlevel 2 goto :annullata
call gh auth login --hostname github.com --git-protocol https --web
if errorlevel 1 goto :errore

:modifiche
set "dirty="
for /f %%i in ('git status --porcelain') do set dirty=1
if not defined dirty goto :versione
echo Modifiche non ancora salvate nel repository:
git status --short
set "commit="
set /p "commit=Messaggio del commit con cui salvarle (vuoto: annullo): "
if not defined commit goto :annullata
git add -A
if errorlevel 1 goto :errore
git commit -m "%commit%"
if errorlevel 1 goto :errore
echo.

:versione
set "last=nessuna"
for /f "delims=" %%i in ('git describe --tags --abbrev=0 2^>nul') do set "last=%%i"
echo Ultima versione con il tag: %last%
set "version="
set /p "version=Nuova versione, numero.numero.numero (vuoto: annullo): "
if not defined version goto :annullata
if /i "%version:~0,1%"=="v" set "version=%version:~1%"
echo %version%| findstr /r /c:"^[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >nul
if errorlevel 1 goto :versione_errata
set "tag=v%version%"

rem --- questo numero di versione ha gia' un tag? ---
set "tagged="
for /f %%i in ('git rev-list -n 1 %tag% 2^>nul') do set "tagged=%%i"
if defined tagged goto :tag_in_repo
git ls-remote --exit-code --tags origin "refs/tags/%tag%" >nul 2>nul
if errorlevel 128 goto :errore
if not errorlevel 2 goto :versione_usata
goto :compila

:tag_in_repo
for /f %%i in ('git rev-parse HEAD') do set "head=%%i"
if not "%tagged%"=="%head%" goto :versione_usata
echo Il tag %tag% c'e' gia' su questa versione del codice: si rifa solo la release.

:compila
set "out=%~dp0artifacts\SmartBar-%tag%"
if exist "%out%" rmdir /s /q "%out%"
echo.
echo --- Compilazione di SmartBar.exe, versione %version% ---
call dotnet publish src\SmartBar\SmartBar.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=%version% -o "%out%"
if errorlevel 1 goto :errore
if not exist "%out%\SmartBar.exe" goto :errore
for /f "delims=" %%i in ('powershell -NoProfile -Command "(Get-Item '%out%\SmartBar.exe').VersionInfo.ProductVersion" 2^>nul') do echo Compilato: SmartBar.exe, versione %%i
if defined tagged goto :push

git tag -a %tag% -m "SmartBar %tag%"
if errorlevel 1 goto :errore

:push
echo.
echo --- Manda su GitHub il ramo e il tag ---
git push origin HEAD
if errorlevel 1 goto :errore
git push origin %tag%
if errorlevel 1 goto :errore

echo.
echo --- Release su GitHub, con SmartBar.exe allegato ---
set "notes=%TEMP%\smartbar-release.txt"
call gh release create %tag% "%out%\SmartBar.exe" --title "SmartBar %tag%" --generate-notes > "%notes%"
if errorlevel 1 goto :errore
set "url="
for /f "usebackq delims=" %%i in ("%notes%") do set "url=%%i"
del "%notes%"
echo.
echo Fatto.
if defined url echo %url%
echo L'eseguibile sta anche in %out%
pause
exit /b 0

:versione_errata
echo La versione va scritta come numero.numero.numero, per esempio 1.0.1 (va bene anche v1.0.1).
echo.
goto :versione

:versione_usata
echo.
echo Fermato: la versione %version% ha gia' un tag su un'altra versione del codice. Usane un'altra.
pause
exit /b 1

:annullata
echo Annullato.
pause
exit /b 1

:errore
echo.
echo *** Fermato: l'ultimo comando ha dato errore (vedi sopra). ***
echo Se il tag e' gia' su GitHub ma la release manca, rilancia con lo stesso numero di versione.
pause
exit /b 1

:no_dotnet
echo Manca il .NET SDK (serve per compilare): https://dotnet.microsoft.com/download
pause
exit /b 1

:no_gh
echo Manca GitHub CLI (serve per la release): https://cli.github.com
pause
exit /b 1

:no_git
echo Questo script sta fuori da un repository git.
pause
exit /b 1
