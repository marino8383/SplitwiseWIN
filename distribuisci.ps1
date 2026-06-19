# distribuisci.ps1
# Prepara un PACCHETTO PULITO da condividere (per un altro utente), SENZA incrementare la versione.
# (L'incremento build lo fa solo pubblica.ps1, così le due cose non si sovrappongono.)
# Pubblica la versione CORRENTE, rimuove eventuali appsettings.json/history.db e crea uno zip.

$ErrorActionPreference = 'Stop'

# ---------------- CONFIG ----------------
$Proj    = 'C:\GIT2\splitwise\SplitwiseUploader.csproj'
$DistDir = 'G:\Il mio Drive\Splitwise\dist'
# ----------------------------------------

$stamp = Get-Date -Format 'yyyyMMddHHmmss'

Write-Host "=== Pacchetto di distribuzione (senza incremento versione) ===" -ForegroundColor Cyan

# 1) leggi la versione corrente dal .csproj (NON la modifica)
$csproj = Get-Content -Raw -LiteralPath $Proj
$mv = [regex]::Match($csproj, '<Version>(\d+\.\d+\.\d+)</Version>')
$ver = if ($mv.Success) { $mv.Groups[1].Value } else { '0.0.0' }
Write-Host "Versione corrente: $ver"

# 2) publish in cartella temporanea
$tmp = Join-Path $env:TEMP "swdist_$stamp"
Write-Host "dotnet publish (Release) → $tmp"
dotnet publish "$Proj" -c Release -o "$tmp"
if ($LASTEXITCODE -ne 0) { Write-Host "PUBLISH FALLITA." -ForegroundColor Red; exit 1 }

# 3) sicurezza: rimuovo dati/config personali e gli script di scarico BPER (hanno i tuoi percorsi, non servono a chi riceve)
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $tmp 'appsettings.json')
Get-ChildItem -LiteralPath $tmp -Filter 'history.db*' -ErrorAction SilentlyContinue | Remove-Item -Force
foreach ($s in 'scarica-bper.cmd', 'scarica-bper.ps1', 'scarica-bper-auto.cmd', 'scarica-bper-auto.js') {
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $tmp $s)
}

# 4) crea lo zip in DistDir
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
$zip = Join-Path $DistDir "SplitwiseUploader_v${ver}_$stamp.zip"
Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $zip -Force
Remove-Item -Recurse -Force $tmp

Write-Host "FATTO." -ForegroundColor Green
Write-Host "Pacchetto: $zip"
Write-Host "Contiene appsettings.example.json (modello); l'utente configura le proprie chiavi da Opzioni al primo avvio."
Read-Host "Premi INVIO per chiudere"
