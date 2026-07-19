#Requires -Version 5.1
<#
.SYNOPSIS
  Preview or run StarSensing SQL cleanup (non-training data).

.DESCRIPTION
  Deletes large non-ML tables (Measurements, Motion_Events, Environment_Batches)
  sooner than training tables (WiFi_Features, Zone_State).
  Never deletes RouterBearings or MapSettings.

.EXAMPLE
  .\Invoke-DataPurge.ps1 -DryRun

.EXAMPLE
  .\Invoke-DataPurge.ps1 -RawRetentionHours 12 -TrainingRetentionHours 168
#>
param(
    [int]$TrainingRetentionHours = 168,
    [int]$RawRetentionHours = 24,
    [int]$ReplayRetentionHours = 48,
    [int]$AccessPointMaxAgeDays = 30,
    [switch]$DryRun,
    [switch]$StatsOnly,
    [string]$Server = "localhost",
    [string]$Database = "StarSensing"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
$SqlFile = Join-Path $PSScriptRoot "sql\PurgeNonTrainingData.sql"

function Invoke-Sql {
    param([string]$Query)
    if (Get-Command sqlcmd -ErrorAction SilentlyContinue) {
        sqlcmd -S $Server -d $Database -E -b -Q $Query
        if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed ($LASTEXITCODE)" }
        return
    }

    $Py = Join-Path $Root "src\StarSensing.Python\venv\Scripts\python.exe"
    if (-not (Test-Path $Py)) { throw "sqlcmd not found and Python venv missing at $Py" }

    $escaped = $Query.Replace("'", "''")
    & $Py -c @"
import pyodbc
cs = 'DRIVER={ODBC Driver 17 for SQL Server};Server=$Server;Database=$Database;Trusted_Connection=yes;TrustServerCertificate=yes;Encrypt=no;'
conn = pyodbc.connect(cs)
cur = conn.cursor()
cur.execute('''$escaped''')
while True:
    if cur.description:
        cols = [c[0] for c in cur.description]
        for row in cur.fetchall():
            print(' | '.join(str(v) for v in row))
    if not cur.nextset():
        break
conn.commit()
conn.close()
"@
}

Write-Host "StarSensing data purge - server=$Server db=$Database" -ForegroundColor Cyan

if (Test-Path $SqlFile) {
    Write-Host "Deploying stored procedures from $SqlFile ..."
    if (Get-Command sqlcmd -ErrorAction SilentlyContinue) {
        sqlcmd -S $Server -d $Database -E -b -i $SqlFile | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sqlcmd deploy failed ($LASTEXITCODE)" }
    } else {
        Write-Host "(Procedures deploy skipped - use Engine startup or sqlcmd for first-time deploy.)"
    }
}

if ($StatsOnly) {
    Write-Host "`nTable stats:" -ForegroundColor Yellow
    Invoke-Sql "EXEC dbo.GetDataStorageStats;"
    exit 0
}

$dry = if ($DryRun) { 1 } else { 0 }
Write-Host "`nRunning PurgeNonTrainingData (DryRun=$dry) ..." -ForegroundColor Yellow
Write-Host "  Training retention: $TrainingRetentionHours h (WiFi_Features, Zone_State)"
Write-Host "  Raw retention:      $RawRetentionHours h (Measurements)"
Write-Host "  Replay retention:   $ReplayRetentionHours h (Motion_Events, Environment_Batches)"

Invoke-Sql @"
EXEC dbo.PurgeNonTrainingData
    @TrainingRetentionHours = $TrainingRetentionHours,
    @RawRetentionHours = $RawRetentionHours,
    @ReplayRetentionHours = $ReplayRetentionHours,
    @AccessPointMaxAgeDays = $AccessPointMaxAgeDays,
    @DryRun = $dry;
"@

Write-Host "`nAfter purge:" -ForegroundColor Yellow
Invoke-Sql "EXEC dbo.GetDataStorageStats;"

if ($DryRun) {
    Write-Host "`nDry-run only - no rows deleted. Re-run without -DryRun to apply." -ForegroundColor Green
} else {
    Write-Host "`nPurge complete." -ForegroundColor Green
}
