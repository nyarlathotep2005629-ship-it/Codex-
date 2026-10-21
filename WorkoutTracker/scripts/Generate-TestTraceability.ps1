[CmdletBinding()]
param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$OutputPath = (Join-Path (Join-Path $PSScriptRoot '..') 'docs\TEST_TRACEABILITY.csv')
)

$testsRoot = Join-Path $ProjectRoot 'tests'
if (-not (Test-Path -LiteralPath $testsRoot -PathType Container)) {
    throw "Tests directory was not found: $testsRoot"
}

$records = [System.Collections.Generic.List[object]]::new()
$attributePattern = '^\s*\[(Fact|Theory)(?:\(DisplayName\s*=\s*"([^"]*)"\))?\]'
$methodPattern = '\b(?:public|private|protected|internal)\s+(?:(?:static|async|virtual|override|sealed|new)\s+)*(?:[\w<>,.?\[\]]+)\s+(\w+)\s*\('
$idPattern = '\b[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{3}\b'

foreach ($file in Get-ChildItem -LiteralPath $testsRoot -Recurse -Filter '*.cs' | Sort-Object FullName) {
    $relativePath = [System.IO.Path]::GetRelativePath($ProjectRoot, $file.FullName).Replace('\', '/')
    $project = Split-Path ([System.IO.Path]::GetRelativePath($testsRoot, $file.FullName)) -Parent
    $project = $project.Replace('\', '/')
    $pending = $null
    $lineNumber = 0

    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        $lineNumber++
        if ($line -match $attributePattern) {
            $pending = [ordered]@{
                Attribute = $Matches[1]
                DisplayName = if ($null -eq $Matches[2]) { '' } else { $Matches[2] }
                InlineDataRows = 0
                AttributeLine = $lineNumber
            }
            continue
        }

        if ($null -ne $pending -and $line -match '^\s*\[InlineData\(') {
            $pending.InlineDataRows++
            continue
        }

        if ($null -ne $pending -and $line -match $methodPattern) {
            $ids = [regex]::Matches($pending.DisplayName, $idPattern) | ForEach-Object Value
            $records.Add([pscustomobject]@{
                Project = $project
                File = $relativePath
                Attribute = $pending.Attribute
                TestMethod = $Matches[1]
                TestIds = ($ids -join ' ')
                DisplayName = $pending.DisplayName
                InlineDataRows = $pending.InlineDataRows
                AttributeLine = $pending.AttributeLine
            })
            $pending = $null
        }
    }
}

$parent = Split-Path $OutputPath -Parent
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$records | Sort-Object Project, File, AttributeLine | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding utf8
Write-Output ("Generated {0} records: {1}" -f $records.Count, $OutputPath)
