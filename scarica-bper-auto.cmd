@echo off
REM Scarico BPER semi-automatico (login+2FA a mano, poi clic e date automatici).
REM Richiede: npm i -D playwright  &&  npx playwright install chromium
cd /d "%~dp0"
node scarica-bper-auto.js
pause
