@echo off
rem ===============================================================
rem  PrecioPixel - subir los cambios de esta carpeta a GitHub
rem  1) Compila el scraper: si no compila, no sube nada.
rem  2) Mueve los workflows que esten en tools\ a .github\workflows\
rem  3) git add / commit / pull --rebase / push con el git de MSYS2
rem  Todo queda registrado en salida-subir.txt
rem ===============================================================
chcp 65001 >nul
cd /d "%~dp0"
set "LOG=%~dp0salida-subir.txt"
set "MENSAJE=Guardar en Firestore con reintentos y corridas cada 2 horas"

echo Compilando...
> "%LOG%" echo ==== COMPILACION ====
pushd scraper
dotnet build >> "%LOG%" 2>&1
if errorlevel 1 goto nocompila
popd

if exist "tools\scraper.yml" move /Y "tools\scraper.yml" ".github\workflows\scraper.yml" >> "%LOG%" 2>&1
if exist "tools\rendimiento.yml" move /Y "tools\rendimiento.yml" ".github\workflows\rendimiento.yml" >> "%LOG%" 2>&1

set "BASH="
for %%B in ("C:\msys64\usr\bin\bash.exe" "C:\tools\msys64\usr\bin\bash.exe" "%USERPROFILE%\msys64\usr\bin\bash.exe" "C:\Program Files\Git\bin\bash.exe") do if not defined BASH if exist %%B set "BASH=%%~B"
if not defined BASH goto nobash
>> "%LOG%" echo ==== GIT (%BASH%) ====
set "CHERE_INVOKING=1"
set "MSYSTEM=UCRT64"
echo Subiendo a GitHub...
"%BASH%" -lc "git add . && git add -f .github/workflows && (git diff --cached --quiet || git commit -m '%MENSAJE%') && git pull --rebase && git push" >> "%LOG%" 2>&1
if errorlevel 1 goto errorgit
>> "%LOG%" echo ==== LISTO ====
echo Listo: los cambios ya estan en GitHub.
timeout /t 8 >nul
exit /b 0

:nocompila
popd
>> "%LOG%" echo ==== NO COMPILO: no se subio nada ====
echo NO COMPILO. No se subio nada. El detalle quedo en salida-subir.txt
pause
exit /b 1

:nobash
>> "%LOG%" echo ==== No encontre MSYS2 ni Git Bash ====
echo No encontre MSYS2 ni Git Bash.
pause
exit /b 1

:errorgit
>> "%LOG%" echo ==== ERROR EN GIT ====
echo Hubo un error con git. El detalle quedo en salida-subir.txt
pause
exit /b 1
