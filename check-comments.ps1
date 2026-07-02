$root = 'D:\study\code\wechatDashboard'
Set-Location $root
$files = Get-ChildItem -Path 'src','tools' -Recurse -Include *.cs,*.py,*.xaml -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.Name -notmatch '\.g\.cs$|\.g\.i\.cs$' }

Write-Host ("Found {0} files" -f $files.Count)
$files | Select-Object -First 5 | ForEach-Object { Write-Host $_.FullName }

$totalLines = 0
$commentLines = 0
$byFile = @()

foreach ($f in $files) {
    $lines = Get-Content $f.FullName -ErrorAction SilentlyContinue
    if ($null -eq $lines) { continue }
    $fileTotal = 0
    $fileComments = 0
    $inBlock = $false
    foreach ($l in $lines) {
        $fileTotal++
        $trimmed = $l.Trim()
        if ($f.Extension -eq '.py') {
            if ($trimmed -match '^#' -or $trimmed -match '^"""' -or $trimmed -match "^'''") { $fileComments++ }
        }
        elseif ($f.Extension -eq '.xaml') {
            if ($trimmed -match '^<!--' -or $trimmed -match '-->$') { $fileComments++ }
        }
        else {
            if ($inBlock) {
                $fileComments++
                if ($trimmed -match '\*/$') { $inBlock = $false }
            }
            elseif ($trimmed -match '^//|^\s*///') {
                $fileComments++
            }
            elseif ($trimmed -match '^/\*') {
                $fileComments++
                if ($trimmed -notmatch '\*/$') { $inBlock = $true }
            }
        }
    }
    $totalLines += $fileTotal
    $commentLines += $fileComments
    $rate = if ($fileTotal -gt 0) { [math]::Round($fileComments / $fileTotal * 100, 1) } else { 0 }
    $byFile += [PSCustomObject]@{ File = $f.FullName.Replace($root + '\', ''); Total = $fileTotal; Comments = $fileComments; Rate = $rate }
}

Write-Host ("Total lines: {0}" -f $totalLines)
Write-Host ("Comment lines: {0}" -f $commentLines)
$overall = if ($totalLines -gt 0) { [math]::Round($commentLines / $totalLines * 100, 2) } else { 0 }
Write-Host ("Overall comment rate: {0}%" -f $overall)
Write-Host ""
Write-Host "Files below 20%:"
$byFile | Where-Object { $_.Rate -lt 20 } | Sort-Object Rate | ForEach-Object { Write-Host ("{0,6}%  {1,5}/{2,5}  {3}" -f $_.Rate, $_.Comments, $_.Total, $_.File) }
Write-Host ""
Write-Host ("Files below 20%: {0}" -f ($byFile | Where-Object { $_.Rate -lt 20 }).Count)
