@echo off
rem ===============================================================
rem  PrecioPixel - analisis de rendimiento EN TU PC
rem  Mide el scraping de todos los juegos del REPO (Firestore) en modo
rem  secuencial y con 1, 2, 4, 8, 16 y 32 juegos a la vez.
rem  Tarda cerca de una hora (la corrida secuencial es la mas larga).
rem  Resultado: web\data\rendimiento-pc.json (la pagina de rendimiento
rem  lo muestra junto al de GitHub) y el registro en salida-rendimiento.txt
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
set "FIREBASE_PROJECT_ID=%PROYECTO%"
set "GOOGLE_APPLICATION_CREDENTIALS=%CLAVE%"
cd /d "%~dp0scraper"

echo Compilando...
> "%~dp0salida-rendimiento.txt" echo ==== COMPILACION ====
dotnet build -c Release >> "%~dp0salida-rendimiento.txt" 2>&1
if errorlevel 1 (
  echo  *** NO COMPILO. El detalle quedo en salida-rendimiento.txt ***
  pause
  exit /b 1
)
echo Midiendo... tarda cerca de una hora. No cierres esta ventana ni apagues la computadora.
>> "%~dp0salida-rendimiento.txt" echo ==== RENDIMIENTO ====
dotnet run -c Release --no-build -- rendimiento --configs secuencial,1,2,4,8,16,32 --salida "%~dp0web\data\rendimiento-pc.json" >> "%~dp0salida-rendimiento.txt" 2>&1
>> "%~dp0salida-rendimiento.txt" echo ==== LISTO ====
echo.
echo Listo. Resultados en web\data\rendimiento-pc.json y el registro en salida-rendimiento.txt
pause
