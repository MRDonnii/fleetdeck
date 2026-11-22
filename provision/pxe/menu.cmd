@echo off
rem WinSetup - menu i netvaerksinstallationen (WinPE).
rem Ligger paa sharet (WinSetup\PXE\menu.cmd) og hentes hver gang, saa den kan rettes uden at bygge boot.wim om.
rem Z: er sharet paa serveren (logget ind i WinPE, se pxe-build.ps1 -Server/-Share). Fleetdeck skriver PXE\deploy\*.
if not defined W set "W=Z:\WinSetup"
set "DEPLOYDIR=%W%\PXE\deploy"
if not defined OUTXML set "OUTXML=X:\unattend.xml"
set "DEF_USER="
set "DEF_PROFILENAME="
if exist "%DEPLOYDIR%\defaults.cmd" call "%DEPLOYDIR%\defaults.cmd"
rem Sprog, tastatur og tidszone til svarfilen (Fleetdeck: Indstillinger). Uden defaults.cmd: de tidligere faste danske vaerdier.
if not defined DEF_LANG set "DEF_LANG=da-DK"
if not defined DEF_INPUTLOCALE set "DEF_INPUTLOCALE=0406:00000406"
if not defined DEF_TIMEZONE set "DEF_TIMEZONE=Romance Standard Time"
rem D_WINDIR: mappen med setup.exe (win11, win10, ws2025). Udrulninger fra Fleetdeck kan saette den og D_XML (faerdig svarfil).
set "D_WINDIR=win11"
set "D_XML="
set "D_TITLE="
if not exist "%W%\USB\autounattend-pxe.xml" (echo   Fandt ikke %W%\USB\autounattend-pxe.xml & goto :eof)

rem ---- netkortets MAC-adresse, og er der en udrulning klar i Fleetdeck?
set "MAC="
set "DFILE="
rem (WinPE har ikke findstr: alle linjer fra ipconfig kontrolleres i :checkmac)
for /f "tokens=2 delims=:" %%a in ('ipconfig /all') do call :checkmac "%%a"
if not defined MAC set "MAC=ukendt"
if defined DFILE goto deployment
goto menu

:checkmac
set "M=%~1"
if not defined M goto :eof
if "%M:~0,1%"==" " set "M=%M:~1%"
if not defined M goto :eof
if "%M:~16,1%"=="" goto :eof
if not "%M:~17,1%"=="" goto :eof
if not "%M:~2,1%%M:~5,1%%M:~8,1%%M:~11,1%%M:~14,1%"=="-----" goto :eof
if not defined MAC set "MAC=%M%"
if not defined DFILE if exist "%DEPLOYDIR%\%M%.cmd" (set "DFILE=%DEPLOYDIR%\%M%.cmd" & set "MAC=%M%")
goto :eof

:header
cls
echo.
echo   ============================================================
echo    WinSetup  -  installation fra %WS_NAME%
echo   ============================================================
echo.
goto :eof

rem ---- udrulning lagt klar i Fleetdeck (Udrulning eller "Geninstaller fra netvaerket")
:deployment
set "D_ID="
set "D_MODE=managed"
set "D_PROFILE="
set "D_PROFILENAME="
set "PCNAME="
set "WUSER="
set "D_WINDIR=win11"
set "D_XML="
set "D_TITLE="
call "%DFILE%"
call :header
echo   Fleetdeck har gjort en installation klar til denne pc (%MAC%):
echo.
if defined D_XML goto deployxml
if /i "%D_MODE%"=="clean" echo     Windows 11 - helt ren (ingen programmer og ingen styring)
if /i "%D_MODE%"=="managed" echo     Windows 11 med mine ting (programmer, filer og baggrund)
if /i "%D_MODE%"=="profile" echo     Windows 11 med profilen "%D_PROFILENAME%"
echo     Pc-navn: %PCNAME%      Windows-bruger: %WUSER%
:deployask
echo.
set "OK="
set /p OK=  Tryk Enter for at starte, eller M for menuen:
if /i "%OK%"=="m" (set "D_XML=" & set "D_WINDIR=win11" & goto menu)
set "D_DEPLOY=%D_ID%"
>"%DEPLOYDIR%\%MAC%.used" echo %D_ID%
goto install

rem ---- udrulning med egen svarfil (tomme felter spoerges paa maskinen)
:deployxml
echo     %D_TITLE%
if defined PCNAME (echo     Pc-navn: %PCNAME%) else (echo     Pc-navn: vaelges af Windows)
if defined WUSER (echo     Bruger: %WUSER%) else (echo     Bruger: spoerges paa maskinen)
goto deployask

rem ---- menu
:menu
call :header
echo   Hvad skal installeres paa denne pc?   (netkort %MAC%)
echo.
echo     1  Windows 11 - helt ren (kun Windows: ingen programmer og ingen styring)
echo     2  Windows 11 med mine ting (programmer, filer og baggrund - %DEF_PROFILENAME%)
echo     3  Windows 11 - vaelg en profil fra Fleetdeck
echo     4  Ubuntu Linux
echo     0  Genstart
echo.
set "CH="
set /p CH=  Valg [2]:
if "%CH%"=="" set "CH=2"
set "D_DEPLOY="
set "D_PROFILE="
set "D_PROFILENAME="
if "%CH%"=="1" (set "D_MODE=clean" & goto askname)
if "%CH%"=="2" (set "D_MODE=managed" & goto askname)
if "%CH%"=="3" goto pickprofile
if "%CH%"=="4" goto ubuntu
if "%CH%"=="0" (wpeutil reboot & goto :eof)
goto menu

:pickprofile
echo.
if not exist "%DEPLOYDIR%\profiles.txt" (echo   Ingen profiler fundet. & pause & goto menu)
echo   Profiler i Fleetdeck:
for /f "usebackq tokens=1,* delims=|" %%a in ("%DEPLOYDIR%\profiles.txt") do echo     %%a  %%b
echo.
set "D_PROFILE="
set /p D_PROFILE=  Skriv profilens nummer (Enter = tilbage):
if not defined D_PROFILE goto menu
set "D_PROFILENAME="
for /f "usebackq tokens=1,* delims=|" %%a in ("%DEPLOYDIR%\profiles.txt") do if "%%a"=="%D_PROFILE%" set "D_PROFILENAME=%%b"
if not defined D_PROFILENAME (echo   Ukendt nummer. & goto pickprofile)
set "D_MODE=profile"
goto askname

:ubuntu
call :header
echo   Ubuntu startes via Fleetdeck, saa pc'en faar den rigtige Linux-bootfil:
echo.
echo     1. Aabn Fleetdeck - Udrulning - Ny udrulning - vaelg Ubuntu.
echo     2. Skriv denne MAC-adresse:  %MAC%
echo     3. Genstart pc'en og boot fra netvaerket igen.
echo.
echo   Saa starter Ubuntu-installationen af sig selv.
echo.
pause
goto menu

rem ---- navn og bruger
:askname
set "M2=%MAC:-=%"
set "DEFNAME=PC-%M2:~-6%"
if "%MAC%"=="ukendt" set "DEFNAME=NY-PC"
echo.
echo   Bruger du samme pc-navn som foer (fx STUE-PC), genkender Fleetdeck pc'en.
set "PCNAME=%DEFNAME%"
set /p PCNAME=  Pc-navn (max 15 tegn: A-Z, 0-9 og -) [%DEFNAME%]:
call :validname || goto askname
:askuser
set "WUSER=%DEF_USER%"
set /p WUSER=  Windows-bruger [%DEF_USER%]:
call :validuser || goto askuser
echo.
if /i "%D_MODE%"=="clean" echo   Windows 11 - helt ren
if /i "%D_MODE%"=="managed" echo   Windows 11 med mine ting
if /i "%D_MODE%"=="profile" echo   Windows 11 med profilen "%D_PROFILENAME%"
echo   Pc-navn: %PCNAME%      Windows-bruger: %WUSER%
set "OK="
set /p OK=  Er det rigtigt? (J/n):
if /i "%OK%"=="n" goto menu
goto install

rem (WinPE har ikke findstr: tjek laengde og de vigtigste ulovlige tegn)
:validname
if "%PCNAME%"=="" exit /b 1
if not "%PCNAME:~15,1%"=="" (echo   For langt - hoejst 15 tegn. & exit /b 1)
if not "%PCNAME: =%"=="%PCNAME%" (echo   Ingen mellemrum i pc-navnet. & exit /b 1)
if not "%PCNAME:.=%"=="%PCNAME%" (echo   Brug kun A-Z, 0-9 og bindestreg. & exit /b 1)
if not "%PCNAME:_=%"=="%PCNAME%" (echo   Brug kun A-Z, 0-9 og bindestreg. & exit /b 1)
exit /b 0

:validuser
if "%WUSER%"=="" exit /b 1
if not "%WUSER:~20,1%"=="" (echo   For langt - hoejst 20 tegn. & exit /b 1)
exit /b 0

rem ---- svarfil og installation
:install
echo.
if not exist "%W%\PXE\%D_WINDIR%\setup.exe" (echo   Fandt ikke %W%\PXE\%D_WINDIR%\setup.exe & goto :eof)
if defined D_XML goto installxml
echo   Laver svarfilen ...
setlocal EnableDelayedExpansion
(for /f "usebackq delims=" %%L in ("%W%\USB\autounattend-pxe.xml") do (
  set "LINE=%%L"
  set "LINE=!LINE:__PCNAME__=%PCNAME%!"
  set "LINE=!LINE:__USER__=%WUSER%!"
  set "LINE=!LINE:__MODE__=%D_MODE%!"
  set "LINE=!LINE:__PROFILE__=%D_PROFILE%!"
  set "LINE=!LINE:__DEPLOY__=%D_DEPLOY%!"
  set "LINE=!LINE:__LANG__=%DEF_LANG%!"
  set "LINE=!LINE:__INPUTLOCALE__=%DEF_INPUTLOCALE%!"
  set "LINE=!LINE:__TIMEZONE__=%DEF_TIMEZONE%!"
  set "LINE=!LINE:__DISKCONFIG__=!"
  echo(!LINE!
)) > "%OUTXML%"
endlocal
"%SystemRoot%\System32\find.exe" "__PCNAME__" "%OUTXML%" >nul && (echo   Kunne ikke lave svarfilen. & goto :eof)
echo   Starter installationen af %PCNAME%. Den stopper kun ved valg af disk.
if defined WS_TEST (echo   TEST: setup.exe /unattend:%OUTXML% & goto :eof)
"%W%\PXE\win11\setup.exe" /unattend:"%OUTXML%"
goto :eof

rem Fleetdeck har lavet hele svarfilen (PXE\deploy\<MAC>.xml): kopier den og start den valgte Windows
:installxml
if not exist "%DEPLOYDIR%\%MAC%.xml" (echo   Fandt ikke svarfilen %DEPLOYDIR%\%MAC%.xml & goto :eof)
copy /y "%DEPLOYDIR%\%MAC%.xml" "%OUTXML%" >nul || (echo   Kunne ikke kopiere svarfilen. & goto :eof)
echo   Starter installationen. Det, der ikke er udfyldt i Fleetdeck, spoerges der om her.
if defined WS_TEST (echo   TEST: %D_WINDIR%\setup.exe /unattend:%OUTXML% & goto :eof)
"%W%\PXE\%D_WINDIR%\setup.exe" /unattend:"%OUTXML%"
goto :eof
