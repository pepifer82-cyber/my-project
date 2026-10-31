param([Parameter(Mandatory=$true)][string]$AgentRoot)
$ErrorActionPreference = "Stop"
$utf8 = [System.Text.UTF8Encoding]::new($false)
$crlf = [string][char]13 + [string][char]10
$lf = [string][char]10
function Normalize([string]$Value) { return $Value.Replace($script:crlf,$script:lf) }
function Replace-Once([string]$Path,[string]$Old,[string]$New,[string]$Label) {
    $text=Normalize ([System.IO.File]::ReadAllText($Path))
    $Old=Normalize $Old; $New=Normalize $New
    if(-not $text.Contains($Old)){throw "Patch v0.3.8: no se encontró '$Label'."}
    $text=$text.Replace($Old,$New)
    [System.IO.File]::WriteAllText($Path,$text,$utf8)
}

$src=Join-Path $AgentRoot "src"
$update=Join-Path $src "UpdateManager.cs"
$main=Join-Path $src "MainForm.cs"
$proj=Join-Path $src "ATPrinterAgent.csproj"
$sync=Join-Path $src "SupabaseSync.cs"
$iss=Join-Path $AgentRoot "AandTPrinterAgent.iss"

# The remote command must never install silently. The person at the target PC
# must explicitly accept in the agent's own Yes/No dialog before the download.
$oldConditional = @'
            if (!remoteRequest)
            {
                var answer = MessageBox.Show(owner,
                    $"Está disponible A&T Printer Agent {latest}.\n\nLa actualización conserva la configuración local y el historial de Supabase. Se solicitará permiso de administrador de Windows para reemplazar los archivos.\n\n¿Descargar/verificar e instalar ahora?",
                    "Actualización de A&T Printer Agent", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer != DialogResult.Yes) return null;
            }
'@
$newEmpty = ''
Replace-Once $update $oldConditional $newEmpty "eliminar omisión de consentimiento remoto"

$oldUrls = @'
            if (!Uri.TryCreate(installerUrl, UriKind.Absolute, out var installerUri) || installerUri.Scheme != Uri.UriSchemeHttps ||
                !Uri.TryCreate(sumsUrl, UriKind.Absolute, out var sumsUri) || sumsUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Las direcciones de descarga no son HTTPS válidas.");

            var name = $"Instalar_ATPrinterAgent_v{latest.Major}.{latest.Minor}.{latest.Build}.exe";
'@
$newUrls = @'
            if (!Uri.TryCreate(installerUrl, UriKind.Absolute, out var installerUri) || installerUri.Scheme != Uri.UriSchemeHttps ||
                !Uri.TryCreate(sumsUrl, UriKind.Absolute, out var sumsUri) || sumsUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Las direcciones de descarga no son HTTPS válidas.");

            var answer = MessageBox.Show(owner,
                $"Está disponible A&T Printer Agent {latest}.\n\nEsta actualización requiere tu autorización en esta PC. Se conservarán la configuración local y el historial de Supabase. Después de aceptar, se descargará y verificará el instalador; Windows también solicitará permiso de administrador para reemplazar los archivos.\n\n¿Aceptás descargar e instalar la actualización?",
                "A&T Printer Agent - Confirmar actualización", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return "UPDATE_DECLINED";

            var name = $"Instalar_ATPrinterAgent_v{latest.Major}.{latest.Minor}.{latest.Build}.exe";
'@
Replace-Once $update $oldUrls $newUrls "consentimiento antes de descargar"

# A declined Windows UAC prompt is different from a failed install; report it
# to the local status rather than showing a false update failure.
Replace-Once $update '            return null;' '            return null;' "validar contenido del actualizador"
$oldUac = @'
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            if (manual) MessageBox.Show(owner, "Cancelaste el permiso de administrador. No se instaló ninguna actualización.", "A&T Printer Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
'@
$newUac = @'
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            if (manual) MessageBox.Show(owner, "Cancelaste el permiso de administrador. No se instaló ninguna actualización.", "A&T Printer Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return "UPDATE_ADMIN_DECLINED";
        }
'@
Replace-Once $update $oldUac $newUac "registrar rechazo de UAC"

$oldStatus = @'
                SetStatus("Se recibió la orden remota, pero no hay una versión posterior disponible o no se pudo iniciar el instalador. Revisá la conexión, el registro del lanzamiento y los permisos de Windows.", true);
'@
$newStatus = @'
                if (updateResult == "UPDATE_DECLINED")
                    SetStatus("Actualización rechazada en esta PC. A&T Printer Agent continúa funcionando con la versión actual.", false);
                else if (updateResult == "UPDATE_ADMIN_DECLINED")
                    SetStatus("Se canceló el permiso de Windows en esta PC. No se instaló la actualización y el agente continúa funcionando.", false);
                else
                    SetStatus("Se recibió la orden remota, pero no hay una versión posterior disponible o no se pudo iniciar el instalador. Revisá la conexión, el registro del lanzamiento y los permisos de Windows.", true);
'@
Replace-Once $main $oldStatus $newStatus "mostrar el resultado de la autorización"

# Bump the app version so existing 0.3.7 installations can discover this release.
Replace-Once $proj '<Version>0.3.7</Version>' '<Version>0.3.8</Version>' "project version"
Replace-Once $proj '<AssemblyVersion>0.3.7.0</AssemblyVersion>' '<AssemblyVersion>0.3.8.0</AssemblyVersion>' "assembly version"
Replace-Once $proj '<FileVersion>0.3.7.0</FileVersion>' '<FileVersion>0.3.8.0</FileVersion>' "file version"
Replace-Once $proj '<InformationalVersion>0.3.7-snmp-reader-fix</InformationalVersion>' '<InformationalVersion>0.3.8-pc-update-approval</InformationalVersion>' "informational version"
Replace-Once $sync 'public static string AppVersion => "0.3.7";' 'public static string AppVersion => "0.3.8";' "reported version"
Replace-Once $sync 'ATPrinterAgent/0.3.7 (+A&T Informatica)' 'ATPrinterAgent/0.3.8 (+A&T Informatica)' "sync user agent"
Replace-Once $update 'ATPrinterAgent-Updater/0.3.7' 'ATPrinterAgent-Updater/0.3.8' "updater user agent"
Replace-Once $main 'Text = "A&T Printer Agent 0.3.7";' 'Text = "A&T Printer Agent 0.3.8";' "window title"
Replace-Once $main 'Text = "v0.3.7 · Windows"' 'Text = "v0.3.8 · Windows"' "visible version"
Replace-Once $iss '#define MyAppVersion "0.3.7"' '#define MyAppVersion "0.3.8"' "installer version"
Replace-Once $iss 'OutputBaseFilename=Instalar_ATPrinterAgent_v0.3.7' 'OutputBaseFilename=Instalar_ATPrinterAgent_v0.3.8' "installer file name"

# Fail the build if a remote flow could still bypass the explicit PC approval.
$updateText=[System.IO.File]::ReadAllText($update)
$mainText=[System.IO.File]::ReadAllText($main)
if(-not $updateText.Contains('return "UPDATE_DECLINED";')){throw "Falta el estado de rechazo de la PC."}
if(-not $updateText.Contains('Confirmar actualización')){throw "Falta el diálogo de autorización en la PC."}
if($updateText.Contains('if (!remoteRequest)')){throw "La actualización remota todavía puede omitir el consentimiento."}
if(-not $mainText.Contains('updateResult == "UPDATE_DECLINED"')){throw "La PC no informa el rechazo remoto."}
if(-not ([System.IO.File]::ReadAllText($proj).Contains('<Version>0.3.8</Version>'))){throw "La versión no quedó en 0.3.8."}
Write-Host "Patch v0.3.8 validado: ninguna actualización remota se instala sin aceptar en la PC destino."
