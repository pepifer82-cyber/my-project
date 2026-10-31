param([Parameter(Mandatory=$true)][string]$AgentRoot)
$ErrorActionPreference = "Stop"
$utf8 = [System.Text.UTF8Encoding]::new($false)
$main = Join-Path (Join-Path $AgentRoot "src") "MainForm.cs"
$crlf = [string][char]13 + [string][char]10
$lf = [string][char]10
$text = ([System.IO.File]::ReadAllText($main)).Replace($crlf,$lf)

function Replace-Once([string]$Old,[string]$New,[string]$Label) {
    $Old=$Old.Replace($crlf,$lf); $New=$New.Replace($crlf,$lf)
    if (-not $script:text.Contains($Old)) { throw "Post-patch v0.3.7: no se encontró '$Label'." }
    $script:text=$script:text.Replace($Old,$New)
}

Replace-Once @'
            match.ScannerCount = network.ScannerCount ?? match.ScannerCount;
'@ @'
            match.ScannerCount = network.ScannerCount ?? match.ScannerCount;
            match.TonerBlackPercent = network.TonerBlackPercent ?? match.TonerBlackPercent;
'@ "preservar fusión de tóner negro USB/IP"

Replace-Once @'
        a.ScannerCount = b.ScannerCount ?? a.ScannerCount;
'@ @'
        a.ScannerCount = b.ScannerCount ?? a.ScannerCount;
        a.TonerBlackPercent = b.TonerBlackPercent ?? a.TonerBlackPercent;
'@ "preservar fusión agregada de tóner negro"

Replace-Once @'
            if (network.SnmpReachable) match.SnmpError = null;
'@ @'
            if (network.SnmpReachable) match.SnmpError = null;
            else if (!string.IsNullOrWhiteSpace(network.SnmpError)) match.SnmpError = network.SnmpError;
'@ "conservar errores nuevos cuando SNMP no responde"

if (-not $text.Contains('match.TonerBlackPercent = network.TonerBlackPercent ?? match.TonerBlackPercent;')) { throw "No se restauró el porcentaje de tóner negro en la fusión USB/IP." }
if (-not $text.Contains('a.TonerBlackPercent = b.TonerBlackPercent ?? a.TonerBlackPercent;')) { throw "No se restauró el porcentaje de tóner negro en la fusión agregada." }
if (-not $text.Contains('else if (!string.IsNullOrWhiteSpace(network.SnmpError))')) { throw "No se preserva el error SNMP vigente." }
[System.IO.File]::WriteAllText($main,$text,$utf8)
Write-Host "Post-patch v0.3.7 aplicado: mantiene tóner negro y conserva errores SNMP útiles."
