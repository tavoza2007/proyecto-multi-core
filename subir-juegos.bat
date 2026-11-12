@echo off
rem ===============================================================
rem  PrecioPixel - sube la lista de juegos a Firebase Firestore
rem  Busca sola la clave de Firebase (el .json "firebase-adminsdk")
rem  en Documentos o Descargas. La clave nunca se copia ni se muestra.
rem ===============================================================
chcp 65001 >nul
set "PROYECTO=multi-cor"
set "CLAVE="
for %%F in ("%USERPROFILE%\Documents\*firebase-adminsdk*.json" "%USERPROFILE%\Downloads\*firebase-adminsdk*.json") do if not defined CLAVE set "CLAVE=%%~fF"
if not defined CLAVE (
  echo No encontre la clave de Firebase en Documentos ni en Descargas.
  echo Es el archivo .json cuyo nombre contiene "firebase-adminsdk".
  pause
  exit /b 1
)
echo Proyecto: %PROYECTO%
echo Clave encontrada: %CLAVE%
set "FIREBASE_PROJECT_ID=%PROYECTO%"
set "GOOGLE_APPLICATION_CREDENTIALS=%CLAVE%"
cd /d "%~dp0scraper"

echo.
echo Subiendo los juegos a Firestore...
> "%~dp0salida-firebase.txt" echo ==== SEED ====
rem --sincronizar: desactiva en Firestore los juegos que ya no estan en seed\juegos.json
dotnet run -- seed --sincronizar >> "%~dp0salida-firebase.txt" 2>&1
type "%~dp0salida-firebase.txt"

echo.
choice /c SN /m "Hacer ahora tambien el scraping completo de los 260 juegos (tarda unos minutos)"
if errorlevel 2 goto fin
>> "%~dp0salida-firebase.txt" echo ==== SCRAPE ====
dotnet run -- scrape >> "%~dp0salida-firebase.txt" 2>&1
>> "%~dp0salida-firebase.txt" echo ==== LISTO ====

:fin
echo.
echo Listo. Todo quedo guardado en salida-firebase.txt
pause
