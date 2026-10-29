param([Parameter(Mandatory=$true)][string]$AgentRoot)
$ErrorActionPreference = "Stop"
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Replace-Required {
    param([string]$Path, [string]$Old, [string]$New, [string]$Label)
    $text = [System.IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) { throw "Patch v0.3.5: no se encontró '$Label' en $Path" }
    $text = $text.Replace($Old, $New)
    [System.IO.File]::WriteAllText($Path, $text, $utf8)
}
function Replace-Block {
    param([string]$Path, [string]$StartMarker, [string]$EndMarker, [string]$NewBlock, [string]$Label)
    $text = [System.IO.File]::ReadAllText($Path)
    $start = $text.IndexOf($StartMarker, [System.StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Patch v0.3.5: inicio no encontrado para '$Label' en $Path" }
    $end = $text.IndexOf($EndMarker, $start, [System.StringComparison]::Ordinal)
    if ($end -lt 0) { throw "Patch v0.3.5: fin no encontrado para '$Label' en $Path" }
    $text = $text.Substring(0, $start) + $NewBlock + [Environment]::NewLine + [Environment]::NewLine + $text.Substring($end)
    [System.IO.File]::WriteAllText($Path, $text, $utf8)
}

$src = Join-Path $AgentRoot "src"
$snmpPath = Join-Path $src "SnmpClient.cs"
$networkPath = Join-Path $src "NetworkPrinterScanner.cs"
$syncPath = Join-Path $src "SupabaseSync.cs"
$mainPath = Join-Path $src "MainForm.cs"
$projPath = Join-Path $src "ATPrinterAgent.csproj"
$issPath = Join-Path $AgentRoot "AandTPrinterAgent.iss"

$oldGetFirst = @'
    public async Task<SnmpResult> GetFirstTableValueAsync(string ip, string columnOid, CancellationToken ct)
    {
        return await GetNextAsync(ip, columnOid, ct).ConfigureAwait(false);
    }
'@
$newGetFirst = @'
    public async Task<SnmpResult> GetFirstTableValueAsync(string ip, string columnOid, CancellationToken ct)
    {
        var result = await GetNextAsync(ip, columnOid, ct).ConfigureAwait(false);
        var root = columnOid.Trim().TrimEnd('.');
        if (!result.Success || string.IsNullOrWhiteSpace(result.Oid)) return result;
        if (!result.Oid.StartsWith(root + ".", StringComparison.Ordinal))
        {
            return new SnmpResult
            {
                Success = false,
                Oid = result.Oid,
                Error = "SNMP GETNEXT salió de la columna solicitada"
            };
        }
        return result;
    }
'@
Replace-Required $snmpPath $oldGetFirst $newGetFirst "SNMP GETNEXT OID validation"

$oldClean = @'
    private static string CleanValue(string? text)
        => (text ?? string.Empty).Replace('\0', ' ').Trim();
'@
$newClean = @'
    private static string CleanValue(string? text)
        => (text ?? string.Empty).Trim();
'@
Replace-Required $snmpPath $oldClean $newClean "SNMP control-character cleanup"

$oldNormalize = @'
    public static string? NormalizeSerial(string? text)
    {
        var value = CleanValue(text);
        value = Regex.Replace(value, @"^(?:S/?N|Serial(?:\s*(?:No\.?|Number|#))?)\s*[:=#]\s*", "", RegexOptions.IgnoreCase).Trim();
        if (value.Length < 4 || value.Length > 160) return null;
        if (Regex.IsMatch(value, @"^(?:unknown|none|null|n/?a|not available|not set|0+|x{4,}|-+)$", RegexOptions.IgnoreCase)) return null;
        if (value.Any(char.IsControl)) return null;
        return value;
    }
'@
$newNormalize = @'
    public static string? NormalizeSerial(string? text)
    {
        var value = CleanValue(text);
        if (value.Length == 0 || value.Any(char.IsControl)) return null;
        value = Regex.Replace(value, @"^(?:S/?N|Serial(?:\s*(?:No\.?|Number|#))?)\s*[:=#]\s*", "", RegexOptions.IgnoreCase).Trim();
        value = Regex.Replace(value, @"\s+", "");
        if (value.Length < 4 || value.Length > 80) return null;
        if (Regex.IsMatch(value, @"^(?:unknown|none|null|n/?a|notavailable|notset|0+|x{4,}|-+)$", RegexOptions.IgnoreCase)) return null;
        if (!Regex.IsMatch(value, @"^[A-Z0-9][A-Z0-9._/-]{3,79}$", RegexOptions.IgnoreCase)) return null;
        return value.ToUpperInvariant();
    }
'@
Replace-Required $snmpPath $oldNormalize $newNormalize "strict serial validation"

$httpPatchPath = Join-Path $PSScriptRoot "NetworkHttpProbe.txt"
$httpPatch = [System.IO.File]::ReadAllText($httpPatchPath).TrimEnd()
$startMarker = '    private static async Task<(string? Identity, string? Serial, long? PageCount)> ProbeHttpPrinterAsync('
$endMarker = '    private static async Task<PjlProbeResult?> ProbePjlAsync('
Replace-Block $networkPath $startMarker $endMarker $httpPatch "same-host HTTP serial probing"

$oldCounter = '                        if (number <= (ulong)long.MaxValue) return (long)number;'
$newCounter = @'
                        // Every UInt32 value fits safely inside Int64.
                        return (long)number;
'@.TrimEnd()
Replace-Required $networkPath $oldCounter $newCounter "UInt32 page-counter conversion"

Replace-Required $syncPath '        serial_number = x.SerialNumber,' '        serial_number = SnmpClient.NormalizeSerial(x.SerialNumber),' "payload serial revalidation"
Replace-Required $syncPath 'ATPrinterAgent/0.3.3 (+A&T Informatica)' 'ATPrinterAgent/0.3.5 (+A&T Informatica)' "sync user agent version"
Replace-Required $syncPath 'public static string AppVersion => "0.3.3";' 'public static string AppVersion => "0.3.5";' "sync app version"

Replace-Required $projPath '<Version>0.3.3</Version>' '<Version>0.3.5</Version>' "project version"
Replace-Required $projPath '<AssemblyVersion>0.3.3.0</AssemblyVersion>' '<AssemblyVersion>0.3.5.0</AssemblyVersion>' "assembly version"
Replace-Required $projPath '<FileVersion>0.3.3.0</FileVersion>' '<FileVersion>0.3.5.0</FileVersion>' "file version"
Replace-Required $projPath '<InformationalVersion>0.3.3-total-print-counter</InformationalVersion>' '<InformationalVersion>0.3.5-serial-fix-remote-updater</InformationalVersion>' "informational version"

$oldMainFields = @'
    private readonly ConfigStore _store = new();
    private readonly SupabaseSync _sync = new();
'@
$newMainFields = @'
    private readonly ConfigStore _store = new();
    private readonly SupabaseSync _sync = new();
    private readonly UpdateManager _updates = new();
    private System.Windows.Forms.Timer? _updateTimer;
'@
Replace-Required $mainPath $oldMainFields $newMainFields "MainForm updater fields"
Replace-Required $mainPath 'Text = "A&T Printer Agent 0.3.3";' 'Text = "A&T Printer Agent 0.3.5";' "window version"
Replace-Required $mainPath 'Text = "v0.3.3 · Windows"' 'Text = "v0.3.5 · Windows"' "visible version"
$oldShown = @'
            _showTimer.Start();
            await StartAsync();
        };
'@
$newShown = @'
            _showTimer.Start();
            await StartAsync();
            _ = CheckForUpdatesAsync(false);
            _updateTimer = new System.Windows.Forms.Timer { Interval = 6 * 60 * 60 * 1000 };
            _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
            _updateTimer.Start();
        };
'@
Replace-Required $mainPath $oldShown $newShown "startup update check"

$oldTray = @'
        _tray.ContextMenuStrip.Items.Add("Abrir", null, (_, _) => ShowFromTray());
        _tray.ContextMenuStrip.Items.Add("Sincronizar ahora", null, async (_, _) => await SyncNowAsync());
        _tray.ContextMenuStrip.Items.Add("Salir", null, (_, _) => ExitApplication());
'@
$newTray = @'
        _tray.ContextMenuStrip.Items.Add("Abrir", null, (_, _) => ShowFromTray());
        _tray.ContextMenuStrip.Items.Add("Sincronizar ahora", null, async (_, _) => await SyncNowAsync());
        _tray.ContextMenuStrip.Items.Add("Buscar actualizaciones", null, async (_, _) => await CheckForUpdatesAsync(true));
        _tray.ContextMenuStrip.Items.Add("Salir", null, (_, _) => ExitApplication());
'@
Replace-Required $mainPath $oldTray $newTray "tray update command"

$oldExitStart = @'
    private void ExitApplication()
    {
        _shutdown.Cancel();
'@
$newExitStart = @'
    private async Task CheckForUpdatesAsync(bool manual)
    {
        var result = await _updates.CheckAndOfferAsync(this, manual, _shutdown.Token);
        if (result == "UPDATE_STARTED") ExitApplication();
    }

    private void ExitApplication()
    {
        _updateTimer?.Stop();
        _updateTimer?.Dispose();
        _shutdown.Cancel();
'@
Replace-Required $mainPath $oldExitStart $newExitStart "update checker and timer cleanup"

Replace-Required $issPath '#define MyAppVersion "0.3.3"' '#define MyAppVersion "0.3.5"' "installer version"
Replace-Required $issPath 'OutputBaseFilename=Instalar_ATPrinterAgent_v0.3.3' 'OutputBaseFilename=Instalar_ATPrinterAgent_v0.3.5' "installer filename"
Replace-Required $issPath 'Filename: "{app}\{#MyAppExeName}"; Parameters: "--background"; Flags: nowait postinstall skipifsilent; Description: "Iniciar A&T Printer Agent en segundo plano"' 'Filename: "{app}\{#MyAppExeName}"; Parameters: "--background"; Flags: nowait runhidden; Description: "Iniciar A&T Printer Agent en segundo plano"' "silent background startup"
Replace-Required $issPath 'Name: "{autodesktop}\A&T Printer Agent"; Filename: "{app}\ATPrinterAgent.exe"; IconFilename: "{app}\ATPrinterAgent.exe"' '' "remove desktop shortcut"

$oldDeleteShortcut = @'
    DeleteFile(ExpandConstant('{userdesktop}\ATPrinterAgent.lnk'));
'@
$newDeleteShortcut = @'
    DeleteFile(ExpandConstant('{userdesktop}\ATPrinterAgent.lnk'));
    DeleteFile(ExpandConstant('{userdesktop}\A&T Printer Agent.lnk'));
'@
Replace-Required $issPath $oldDeleteShortcut $newDeleteShortcut "remove old desktop shortcut"

Copy-Item (Join-Path $PSScriptRoot "UpdateManager.cs") (Join-Path $src "UpdateManager.cs") -Force

$finalCsproj = [System.IO.File]::ReadAllText($projPath)
$finalIss = [System.IO.File]::ReadAllText($issPath)
$finalSnmp = [System.IO.File]::ReadAllText($snmpPath)
$finalMain = [System.IO.File]::ReadAllText($mainPath)
if ($finalCsproj -notmatch '<Version>0\.3\.5</Version>') { throw "Validación: el proyecto no quedó en v0.3.5" }
if ($finalIss -notmatch 'OutputBaseFilename=Instalar_ATPrinterAgent_v0\.3\.5') { throw "Validación: nombre del instalador incorrecto" }
if (-not $finalSnmp.Contains('SNMP GETNEXT salió de la columna solicitada')) { throw "Validación: falta la protección de OID" }
if (-not $finalMain.Contains('CheckForUpdatesAsync')) { throw "Validación: falta el actualizador remoto" }
Write-Host "Patch serial v0.3.5 aplicado y validado."
