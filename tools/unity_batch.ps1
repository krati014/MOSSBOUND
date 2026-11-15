# Sync scripts into a private copy of the Unity project and run a MOSSBOUND batch-mode tool there
# (so it works even while the real project is open in the Unity Editor).
# Usage: tools\unity_batch.ps1 preview [-day 1]
#        tools\unity_batch.ps1 mission "I'm hungry || share it with the friends who glow" snack -quest 0 [-day 1] [-start x,z,yaw]
#        tools\unity_batch.ps1 pipeline "Find the blue crystal" crystal [x,z,yaw]
param([string]$what = "preview", [string]$instruction = "", [string]$tag = "test", [string]$start = "", [string]$quest = "0", [string]$day = "0")
$root = Split-Path $PSScriptRoot -Parent
$main = Join-Path $root "MossboundUnity"
$proj = Join-Path $PSScriptRoot "_batch_project"
$unity = Get-ChildItem "C:\Program Files\Unity\Hub\Editor\*\Editor\Unity.exe" | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not (Test-Path $proj)) { robocopy $main $proj /E /XD Temp Logs UserSettings Library /NFL /NDL /NJH /NJS /NP | Out-Null }
robocopy (Join-Path $main "Assets\Mossbound") (Join-Path $proj "Assets\Mossbound") /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
$env:MOSS_OUT = Join-Path $root "docs\screenshots"
$env:MOSS_INSTRUCTION = $instruction
$env:MOSS_TAG = $tag
$env:MOSS_START = $start
$env:MOSS_QUEST = $quest
$env:MOSS_DAY = $day
$method = switch ($what) { "pipeline" { "PipelineTest" } "mission" { "MissionTest" } default { "RenderPreview" } }
$log = Join-Path $PSScriptRoot "_batch_$what-$tag.log"
& $unity -batchmode -quit -projectPath $proj -executeMethod "Mossbound.EditorTools.MossboundEditor.$method" -logFile $log | Out-Null
Select-String -Path $log -Pattern "error CS|\[MOSS\]|NullReference|Exception:" | Where-Object { $_.Line -notmatch "PlasticSCM|PackageCache|collab" } | ForEach-Object { $_.Line }
