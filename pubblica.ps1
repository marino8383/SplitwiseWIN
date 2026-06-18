# pubblica.ps1
# Pubblica la versione Release in $Target, facendo PRIMA il backup del contenuto attuale
# in $Target\backup\yyyyMMddHHmmss.zip. Poi pulisce il target (tranne 'backup') e copia la nuova build.

$ErrorActionPreference = 'Stop'

# ---------------- CONFIG ----------------
$Proj   = 'C:\GIT2\splitwise\SplitwiseUploader.csproj'
$Target = 'G:\Il mio Drive\Splitwise\SW'
# ----------------------------------------

$stamp     = Get-Date -Format 'yyyyMMddHHmmss'
$backupDir = Join-Path $Target 'backup'

Write-Host "=== Pubblicazione SplitwiseUploader ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

# 1) BACKUP del contenuto attuale (escludendo la cartella 'backup' stessa)
$items = Get-ChildItem -LiteralPath $Target -Force | Where-Object { $_.Name -ne 'backup' }
if ($items) {
    $zip = Join-Path $backupDir "$stamp.zip"
    Write-Host "Backup di $($items.Count) elementi → $zip"
    Compress-Archive -Path $items.FullName -DestinationPath $zip -Force
} else {
    Write-Host "Target vuoto: nessun backup da fare."
}

# 1b) INCREMENTA la build (ultimo numero di <Version> nel .csproj)
$csproj = Get-Content -Raw -LiteralPath $Proj
$m = [regex]::Match($csproj, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
if ($m.Success) {
    $newVer = "{0}.{1}.{2}" -f $m.Groups[1].Value, $m.Groups[2].Value, ([int]$m.Groups[3].Value + 1)
    $csproj = $csproj.Remove($m.Index, $m.Length).Insert($m.Index, "<Version>$newVer</Version>")
    Set-Content -LiteralPath $Proj -Value $csproj -NoNewline -Encoding UTF8
    Write-Host "Versione incrementata → $newVer" -ForegroundColor Yellow
} else {
    Write-Host "Tag <Version> non trovato nel .csproj: incremento saltato." -ForegroundColor Yellow
}

# 2) PUBLISH in cartella temporanea
$tmp = Join-Path $env:TEMP "swpub_$stamp"
Write-Host "dotnet publish (Release) → $tmp"
dotnet publish "$Proj" -c Release -o "$tmp"
if ($LASTEXITCODE -ne 0) { Write-Host "PUBLISH FALLITA." -ForegroundColor Red; exit 1 }

# 3) Aggiorna il target SOSTITUENDO i soli file di programma, PRESERVANDO dati e configurazione.
#    Non vengono toccati: backup, appsettings.json (config), history.db* (dati), logs.
$preserve = @('backup', 'appsettings.json', 'history.db', 'history.db-wal', 'history.db-shm', 'logs')
Write-Host "Aggiorno $Target (preservo: $($preserve -join ', ')) ..."
Get-ChildItem -LiteralPath $Target -Force | Where-Object { $preserve -notcontains $_.Name } | Remove-Item -Recurse -Force
# La build NON contiene appsettings.json né history.db (csproj), quindi la copia non sovrascrive i tuoi.
Copy-Item -Path (Join-Path $tmp '*') -Destination $Target -Recurse -Force
Remove-Item -Recurse -Force $tmp

Write-Host "FATTO." -ForegroundColor Green
Write-Host "Pubblicato in: $Target"
Write-Host "Backup in:     $(Join-Path $backupDir "$stamp.zip")"
Read-Host "Premi INVIO per chiudere"
