@echo off
if not defined ComSpec set "ComSpec=cmd.exe"
if /I "%~1"=="--keep" goto :__main
if /I "%~1"=="-y" goto :__main
if /I "%~1"=="--yes" goto :__main
"%ComSpec%" /k call "%~f0" --keep %*
exit /b %ERRORLEVEL%

:__main
setlocal EnableExtensions
title v9 MultiRLBX - Publish to GitHub

set "SCRIPT_DIR=%~dp0"
set "BRANCH=main"
set "DEFAULT_REPO=https://github.com/nielopek364-netizen/v9-MultiRLBX.git"
set "LOG_FILE=%SCRIPT_DIR%publish-github.log"
set "TMP_OUT=%SCRIPT_DIR%.v9_cmd_out.tmp"
set "WRITE_TEST=%SCRIPT_DIR%.v9_write_test.tmp"
set "AUTO_YES=0"
if /I "%~1"=="-y" set "AUTO_YES=1"
if /I "%~1"=="--yes" set "AUTO_YES=1"
if /I "%~2"=="-y" set "AUTO_YES=1"
if /I "%~2"=="--yes" set "AUTO_YES=1"

echo ============================================================
echo   v9 MultiRLBX - GitHub Publish Tool
echo ============================================================
echo.

pushd "%SCRIPT_DIR%" >nul 2>&1
if not errorlevel 1 goto :check_zip
echo [ERROR] Nie udalo sie wejsc do folderu projektu:
echo         "%SCRIPT_DIR%"
goto :fail_pause

:check_zip
echo "%SCRIPT_DIR%" | findstr /I /C:"\AppData\Local\Temp\Temp" >nul 2>&1
if errorlevel 1 goto :check_writable
echo [ERROR] Wyglada na to, ze uruchamiasz skrypt prosto z archiwum ZIP.
echo         Najpierw wypakuj caly ZIP do zwyklego folderu, np. C:\v9-MultiRLBX
goto :fail_pause

:check_writable
type nul > "%WRITE_TEST%" 2>nul
if not errorlevel 1 goto :write_ok
echo [ERROR] Odmowa dostepu do zapisu w folderze:
echo         "%SCRIPT_DIR%"
echo.
echo Rozwiazanie:
echo   1. Kliknij pobrany plik ZIP prawym przyciskiem -^> Wlasciwosci -^> zaznacz "Odblokuj".
echo   2. Wypakuj ZIP do zwyklego folderu, np. C:\v9-MultiRLBX, poza Program Files i OneDrive.
echo   3. Jesli Windows Defender pokazuje popup, zezwol na dostep do folderu.
goto :fail_pause

:write_ok
del /q "%WRITE_TEST%" >nul 2>&1
> "%LOG_FILE%" echo === v9 MultiRLBX Publish Log (%DATE% %TIME%) ===
>> "%LOG_FILE%" echo Folder: "%SCRIPT_DIR%"

rem Add common Git and GitHub CLI install paths locally
set "PATH=%ProgramFiles%\Git\cmd;%ProgramFiles%\Git\bin;%ProgramFiles(x86)%\Git\cmd;%LocalAppData%\Programs\Git\cmd;%ProgramFiles%\GitHub CLI;%LocalAppData%\Programs\GitHub CLI;%UserProfile%\scoop\shims;%PATH%"

git --version > "%TMP_OUT%" 2>&1
if not errorlevel 1 goto :git_found
if exist "%TMP_OUT%" type "%TMP_OUT%"
if exist "%TMP_OUT%" type "%TMP_OUT%" >> "%LOG_FILE%"
echo [ERROR] Nie znaleziono programu Git (git.exe).
echo         Pobierz i zainstaluj Git for Windows: https://git-scm.com/download/win
echo         Po instalacji uruchom ten skrypt ponownie.
goto :fail_pause

:git_found
if exist "%TMP_OUT%" type "%TMP_OUT%" >> "%LOG_FILE%"
del /q "%TMP_OUT%" >nul 2>&1

set "VERSION=1.0.0"
if exist "%SCRIPT_DIR%VERSION" set /p VERSION=<"%SCRIPT_DIR%VERSION"
set "VERSION=%VERSION: =%"
if "%VERSION%"=="" set "VERSION=1.0.0"

for /f "delims=" %%R in ('git remote get-url origin 2^>nul') do set "DEFAULT_REPO=%%R"

set "REPO_URL="
if "%AUTO_YES%"=="1" goto :use_default_repo
set /p "REPO_URL=GitHub repo URL [%DEFAULT_REPO%]: "
:use_default_repo
if "%REPO_URL%"=="" set "REPO_URL=%DEFAULT_REPO%"
>> "%LOG_FILE%" echo Target repo: "%REPO_URL%"

set "DEFAULT_MSG=Release v9 MultiRLBX v%VERSION%"
set "COMMIT_MSG="
if "%AUTO_YES%"=="1" goto :use_default_msg
set /p "COMMIT_MSG=Commit message [%DEFAULT_MSG%]: "
:use_default_msg
if "%COMMIT_MSG%"=="" set "COMMIT_MSG=%DEFAULT_MSG%"

if exist "%SCRIPT_DIR%.git" goto :git_already_init
echo.
echo ==^> Inicjalizacja repozytorium Git (%BRANCH%)...
git init -b %BRANCH% > "%TMP_OUT%" 2>&1
if not errorlevel 1 goto :init_done
git init > "%TMP_OUT%" 2>&1
if errorlevel 1 goto :git_cmd_failed
git checkout -B %BRANCH% >> "%TMP_OUT%" 2>&1
if errorlevel 1 goto :git_cmd_failed
goto :init_done

:git_already_init
git checkout -B %BRANCH% > "%TMP_OUT%" 2>&1
if errorlevel 1 goto :git_cmd_failed

:init_done
if exist "%TMP_OUT%" type "%TMP_OUT%" >> "%LOG_FILE%"
del /q "%TMP_OUT%" >nul 2>&1

set "GIT_USER="
for /f "delims=" %%U in ('git config user.name 2^>nul') do set "GIT_USER=%%U"
if not "%GIT_USER%"=="" goto :check_email
if "%AUTO_YES%"=="1" goto :default_git_user
set /p "GIT_USER=Git user.name [v9pv]: "
:default_git_user
if "%GIT_USER%"=="" set "GIT_USER=v9pv"
git config user.name "%GIT_USER%" > "%TMP_OUT%" 2>&1
if errorlevel 1 goto :git_cmd_failed
if exist "%TMP_OUT%" type "%TMP_OUT%" >> "%LOG_FILE%"
del /q "%TMP_OUT%" >nul 2>&1

:check_email
set "GIT_EMAIL="
for /f "delims=" %%E in ('git config user.email 2^>nul') do set "GIT_EMAIL=%%E"
if not "%GIT_EMAIL%"=="" goto :setup_remote
if "%AUTO_YES%"=="1" goto :default_git_email
set /p "GIT_EMAIL=Git user.email [v9pv@users.noreply.github.com]: "
:default_git_email
if "%GIT_EMAIL%"=="" set "GIT_EMAIL=v9pv@users.noreply.github.com"
git config user.email "%GIT_EMAIL%" > "%TMP_OUT%" 2>&1
if errorlevel 1 goto :git_cmd_failed
if exist "%TMP_OUT%" type "%TMP_OUT%" >> "%LOG_FILE%"
del /q "%TMP_OUT%" >nul 2>&1

:setup_remote
git remote get-url origin >nul 2>&1
if errorlevel 1 goto :add_remote
git remote set-url origin "%REPO_URL%" > "%TMP_OUT%" 2>&1
if errorlevel 1 goto :git_cmd_failed
goto :remote_done

:add_remote
git remote add origin "%REPO_URL%" > "%TMP_OUT%" 2>&1
if errorlevel 1 goto :git_cmd_failed

:remote_done
if exist "%TMP_OUT%" type "%TMP_OUT%" >> "%LOG_FILE%"
del /q "%TMP_OUT%" >nul 2>&1
echo Remote origin -^> "%REPO_URL%"

set "GH_SLUG="
where gh >nul 2>&1
if errorlevel 1 goto :skip_gh_create
echo "%REPO_URL%" | findstr /I "github.com" >nul 2>&1
if errorlevel 1 goto :skip_gh_create

set "RAW_URL=%REPO_URL%"
set "RAW_URL=%RAW_URL:https://github.com/=%"
set "RAW_URL=%RAW_URL:http://github.com/=%"
set "RAW_URL=%RAW_URL:git@github.com:=%"
set "RAW_URL=%RAW_URL:.git=%"
set "GH_SLUG=%RAW_URL%"

if "%GH_SLUG%"=="" goto :skip_gh_create
gh auth status >nul 2>&1
if errorlevel 1 goto :skip_gh_create

gh repo view "%GH_SLUG%" >nul 2>&1
if not errorlevel 1 goto :skip_gh_create
echo ==^> Tworzenie repozytorium na GitHub: %GH_SLUG%...
gh repo create "%GH_SLUG%" --public --description "Lightweight C# 5 Roblox multi-instance launcher, Smart Eco optimizer, Game Picker and .v9m macro studio for Windows" > "%TMP_OUT%" 2>&1
if exist "%TMP_OUT%" type "%TMP_OUT%" >> "%LOG_FILE%"
del /q "%TMP_OUT%" >nul 2>&1

:skip_gh_create
echo.
echo ==^> Dodawanie plikow do commita...
git add -A > "%TMP_OUT%" 2>&1
if errorlevel 1 goto :git_cmd_failed
if exist "%TMP_OUT%" type "%TMP_OUT%" >> "%LOG_FILE%"
del /q "%TMP_OUT%" >nul 2>&1

git reset -q HEAD -- accounts.dat settings.json .env publish-github.log .v9_cmd_out.tmp .v9_write_test.tmp >nul 2>&1

git diff --cached --quiet
if not errorlevel 1 goto :nothing_new

echo ==^> Tworzenie commita: "%COMMIT_MSG%"
git commit -m "%COMMIT_MSG%" > "%TMP_OUT%" 2>&1
if errorlevel 1 goto :git_cmd_failed
if exist "%TMP_OUT%" type "%TMP_OUT%"
if exist "%TMP_OUT%" type "%TMP_OUT%" >> "%LOG_FILE%"
del /q "%TMP_OUT%" >nul 2>&1
goto :ask_tag

:nothing_new
git rev-parse --verify HEAD >nul 2>&1
if not errorlevel 1 goto :nothing_new_ok
echo [ERROR] Brak plikow do wyslania.
goto :fail_pause

:nothing_new_ok
echo Brak nowych zmian do zacommitowania - wysylanie istniejacej historii.

:ask_tag
set "TAG_NAME=v%VERSION%"
set "DO_TAG=T"
if "%AUTO_YES%"=="1" goto :do_tag_check
set /p "DO_TAG=Utworzyc i wyslac tag '%TAG_NAME%' (uruchomi GitHub Actions build + Release)? [T/n]: "
if "%DO_TAG%"=="" set "DO_TAG=T"

:do_tag_check
set "CREATE_TAG=0"
if /I "%DO_TAG%"=="T" set "CREATE_TAG=1"
if /I "%DO_TAG%"=="TAK" set "CREATE_TAG=1"
if /I "%DO_TAG%"=="Y" set "CREATE_TAG=1"
if /I "%DO_TAG%"=="YES" set "CREATE_TAG=1"

if not "%CREATE_TAG%"=="1" goto :do_push
echo ==^> Tagowanie %TAG_NAME%...
git tag -f -a "%TAG_NAME%" -m "v9 MultiRLBX %TAG_NAME%" > "%TMP_OUT%" 2>&1
if errorlevel 1 goto :git_cmd_failed
if exist "%TMP_OUT%" type "%TMP_OUT%" >> "%LOG_FILE%"
del /q "%TMP_OUT%" >nul 2>&1

:do_push
echo.
echo ==^> Wysylanie galezi '%BRANCH%' na GitHub...
git push -u origin %BRANCH%
if not errorlevel 1 goto :push_tag
echo.
echo [ERROR] Wyslanie galezi '%BRANCH%' na GitHub nie powiodlo sie.
echo         - Upewnij sie, ze repozytorium istnieje na GitHub i jestes zalogowany w Git.
echo         - Jesli repozytorium ma juz README/LICENSE na GitHub, uruchom najpierw:
echo             git pull --rebase origin %BRANCH%
>> "%LOG_FILE%" echo [ERROR] git push -u origin %BRANCH% failed.
git status -sb >> "%LOG_FILE%" 2>&1
git remote -v >> "%LOG_FILE%" 2>&1
goto :fail_pause

:push_tag
if not "%CREATE_TAG%"=="1" goto :gh_meta
echo ==^> Wysylanie tagu %TAG_NAME%...
git push -f origin "%TAG_NAME%"
if not errorlevel 1 goto :gh_meta
echo.
echo [ERROR] Wyslanie tagu '%TAG_NAME%' na GitHub nie powiodlo sie.
>> "%LOG_FILE%" echo [ERROR] git push -f origin %TAG_NAME% failed.
goto :fail_pause

:gh_meta
if "%GH_SLUG%"=="" goto :done_banner
gh auth status >nul 2>&1
if errorlevel 1 goto :done_banner
echo ==^> Konfigurowanie opisu i tagow repozytorium na GitHub...
gh repo edit "%GH_SLUG%" --description "Lightweight C# 5 Roblox multi-instance launcher, Smart Eco optimizer, Game Picker and .v9m macro studio for Windows" --add-topic roblox,windows,csharp,dotnet,multi-instance,launcher,automation,macro >nul 2>&1

:done_banner
set "ACTIONS_URL=%REPO_URL:.git=%/actions"
echo.
echo ============================================================
echo   Gotowe! Projekt zostal wyslany na GitHub:
echo   "%REPO_URL%"
if "%CREATE_TAG%"=="1" echo   Release CI buduje EXE / ZIP / Setup tutaj: "%ACTIONS_URL%"
echo   Log zapisany w: publish-github.log
echo ============================================================
echo.
popd >nul 2>&1
if "%AUTO_YES%"=="1" exit /b 0
pause
exit 0

:git_cmd_failed
echo.
echo [ERROR] Polecenie Git zakonczylo sie bledem:
if exist "%TMP_OUT%" type "%TMP_OUT%"
if exist "%TMP_OUT%" type "%TMP_OUT%" >> "%LOG_FILE%"
del /q "%TMP_OUT%" >nul 2>&1
goto :fail_pause

:fail_pause
del /q "%TMP_OUT%" >nul 2>&1
del /q "%WRITE_TEST%" >nul 2>&1
echo.
echo ============================================================
echo   Wystapil blad. Okno NIE zostanie zamkniete automatycznie.
if exist "%LOG_FILE%" echo   Pelny log bledow: "%LOG_FILE%"
echo   Skopiuj tresc z tego okna lub z pliku publish-github.log.
echo ============================================================
echo.
popd >nul 2>&1
if "%AUTO_YES%"=="1" exit /b 1
pause
echo Okno pozostaje otwarte. Mozesz skopiowac blad lub zamknac okno recznie.
exit /b 1
