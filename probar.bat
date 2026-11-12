@echo off
rem ===============================================================
rem  PrecioPixel - prueba rapida del scraper
rem  Compila y corre el scraping de los primeros 15 juegos del repo
rem  local. Todo lo que muestra queda guardado en salida.txt.
rem ===============================================================
chcp 65001 >nul
cd /d "%~dp0scraper"
echo Compilando y ejecutando... (tarda unos minutos, no cierres esta ventana)
> "%~dp0salida.txt" echo ==== COMPILACION ====
dotnet build >> "%~dp0salida.txt" 2>&1
>> "%~dp0salida.txt" echo ==== SCRAPING ====
dotnet run --no-build -- scrape --local --limite 15 --verbose --sin-previos >> "%~dp0salida.txt" 2>&1
>> "%~dp0salida.txt" echo ==== LISTO ====
echo.
echo Listo. El resultado quedo en salida.txt
pause
