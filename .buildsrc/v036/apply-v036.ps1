param([Parameter(Mandatory=$true)][string]$AgentRoot)
$ErrorActionPreference = "Stop"
$utf8 = [System.Text.UTF8Encoding]::new($false)
function Replace-Once([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $crlf = [string][char]13 + [string][char]10
    $lf = [string][char]10
    $text = ([System.IO.File]::ReadAllText($Path)).Replace($crlf, $lf)
    $Old = $Old.Replace($crlf, $lf)
    $New = $New.Replace($crlf, $lf)
    if (-not $text.Contains($Old)) { throw "Patch v0.3.6: no se encontró '$Label' en $Path" }
    $text = $text.Replace($Old, $New)
    [System.IO.File]::WriteAllText($Path, $text, $utf8)
}
$src = Join-Path $AgentRoot "src"
$proj = Join-Path $src "ATPrinterAgent.csproj"
$main = Join-Path $src "MainForm.cs"
$update = Join-Path $src "UpdateManager.cs"
$sync = Join-Path $src "SupabaseSync.cs"
$iss = Join-Path $AgentRoot "AandTPrinterAgent.iss"
Replace-Once $proj '<Version>0.3.5</Version>' '<Version>0.3.6</Version>' "versión del proyecto"
Replace-Once $proj '<AssemblyVersion>0.3.5.0</AssemblyVersion>' '<AssemblyVersion>0.3.6.0</AssemblyVersion>' "versión del ensamblado"
Replace-Once $proj '<FileVersion>0.3.5.0</FileVersion>' '<FileVersion>0.3.6.0</FileVersion>' "versión del archivo"
Replace-Once $proj '<InformationalVersion>0.3.5-serial-fix-remote-updater</InformationalVersion>' '<InformationalVersion>0.3.6-remote-update-command</InformationalVersion>' "versión informativa"
Replace-Once $sync 'public static string AppVersion => "0.3.5";' 'public static string AppVersion => "0.3.6";' "versión informada a Supabase"
Replace-Once $sync 'ATPrinterAgent/0.3.5 (+A&T Informatica)' 'ATPrinterAgent/0.3.6 (+A&T Informatica)' "identificador de sincronización"
Replace-Once $iss '#define MyAppVersion "0.3.5"' '#define MyAppVersion "0.3.6"' "versión Inno Setup"
Replace-Once $iss 'OutputBaseFilename=Instalar_ATPrinterAgent_v0.3.5' 'OutputBaseFilename=Instalar_ATPrinterAgent_v0.3.6' "nombre del instalador"
Replace-Once $main 'Text = "A&T Printer Agent 0.3.5";' 'Text = "A&T Printer Agent 0.3.6";' "título"
Replace-Once $main 'Text = "v0.3.5 · Windows"' 'Text = "v0.3.6 · Windows"' "versión visible"
Replace-Once $update 'ATPrinterAgent-Updater/0.3.5' 'ATPrinterAgent-Updater/0.3.6' "identificador del actualizador"
Replace-Once $update 'public async Task<string?> CheckAndOfferAsync(IWin32Window owner, bool manual, CancellationToken ct)' 'public async Task<string?> CheckAndOfferAsync(IWin32Window owner, bool manual, CancellationToken ct, bool remoteRequest = false)' "parámetro de actualización remota"
$oldPrompt = @'
            var answer = MessageBox.Show(owner,
                $"Está disponible A&T Printer Agent {latest}.\n\nLa actualización conserva la configuración local y el historial de Supabase. Se solicitará permiso de administrador de Windows para reemplazar los archivos.\n\n¿Descargar/verificar e instalar ahora?",
                "Actualización de A&T Printer Agent", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer != DialogResult.Yes) return null;
'@
$newPrompt = @'
            if (!remoteRequest)
            {
                var answer = MessageBox.Show(owner,
                    $"Está disponible A&T Printer Agent {latest}.\n\nLa actualización conserva la configuración local y el historial de Supabase. Se solicitará permiso de administrador de Windows para reemplazar los archivos.\n\n¿Descargar/verificar e instalar ahora?",
                    "Actualización de A&T Printer Agent", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer != DialogResult.Yes) return null;
            }
'@
Replace-Once $update $oldPrompt $newPrompt "confirmación local/actualización remota"
$oldConfirmed = '            var confirmedIds = result.AcknowledgedCommandIds.ToHashSet();'
$newConfirmed = @'
            var confirmedIds = result.AcknowledgedCommandIds.ToHashSet();
            var remoteUpdateConfirmed = _config.RemoteCommandAcks.Any(a =>
                confirmedIds.Contains(a.id) &&
                a.status == "done" &&
                string.Equals(a.result_message, "REMOTE_UPDATE_PENDING", StringComparison.Ordinal));
'@
Replace-Once $main $oldConfirmed $newConfirmed "acuse confirmado de actualización remota"
$oldLoop = @'
            foreach (var command in result.RemoteCommands.GroupBy(c => c.id).Select(g => g.First()))
            {
                try
                {
                    var message = ApplyRemoteCommand(command, ref settingsChanged, ref intervalChanged, ref rescanRequested);
'@
$newLoop = @'
            foreach (var command in result.RemoteCommands.GroupBy(c => c.id).Select(g => g.First()))
            {
                if (string.Equals(command.command, "update_now", StringComparison.OrdinalIgnoreCase))
                {
                    acknowledgementBatch.Add(new RemoteCommandAck
                    {
                        id = command.id,
                        status = "done",
                        result_message = "REMOTE_UPDATE_PENDING"
                    });
                    continue;
                }

                try
                {
                    var message = ApplyRemoteCommand(command, ref settingsChanged, ref intervalChanged, ref rescanRequested);
'@
Replace-Once $main $oldLoop $newLoop "orden update_now"
$oldEnd = @'
            if (rescanRequested && _config.InitialDiscoveryComplete)
            {
                var scanned = await ScanAsync();
                if (scanned) await SendSyncCoreAsync();
            }
'@
$newEnd = @'
            if (rescanRequested && _config.InitialDiscoveryComplete)
            {
                var scanned = await ScanAsync();
                if (scanned) await SendSyncCoreAsync();
            }

            if (remoteUpdateConfirmed)
            {
                var updateResult = await _updates.CheckAndOfferAsync(this, false, _shutdown.Token, remoteRequest: true);
                if (updateResult == "UPDATE_STARTED")
                {
                    SetStatus("Actualización remota verificada e iniciada. El agente se cerrará para instalar la nueva versión.", false);
                    ExitApplication();
                    return;
                }
                SetStatus("Se recibió la orden remota, pero no hay una versión posterior disponible o no se pudo iniciar el instalador. Revisá la conexión, el registro del lanzamiento y los permisos de Windows.", true);
            }
'@
Replace-Once $main $oldEnd $newEnd "inicio de actualización luego de acuse"
if (-not ([System.IO.File]::ReadAllText($proj).Contains('<Version>0.3.6</Version>'))) { throw "El proyecto no quedó en 0.3.6." }
if (-not ([System.IO.File]::ReadAllText($sync).Contains('public static string AppVersion => "0.3.6";'))) { throw "SupabaseSync no reporta la versión 0.3.6." }
if (-not ([System.IO.File]::ReadAllText($sync).Contains('ATPrinterAgent/0.3.6 (+A&T Informatica)'))) { throw "El User-Agent de sincronización no quedó en 0.3.6." }
if (-not ([System.IO.File]::ReadAllText($main).Contains('"update_now"'))) { throw "No se agregó el comando remoto update_now." }
if (-not ([System.IO.File]::ReadAllText($main).Contains('REMOTE_UPDATE_PENDING'))) { throw "No se agregó el acuse de actualización remota." }
if (-not ([System.IO.File]::ReadAllText($update).Contains('remoteRequest = false'))) { throw "No quedó habilitado el modo remoto del actualizador." }
Write-Host "Patch v0.3.6 aplicado y verificado. Se conservan todas las correcciones de v0.3.5."
