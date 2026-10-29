using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ATPrinterAgent;

/// <summary>
/// Checks the latest GitHub Release for this agent. Releases must contain an installer
/// named Instalar_ATPrinterAgent_vX.Y.Z.exe and a SHA256SUMS.txt asset containing
/// the SHA-256 and filename. The installer is always launched elevated (UAC) and
/// configuration/history remain outside the installation folder.
/// </summary>
internal sealed class UpdateManager
{
    private const string ReleasesApi = "https://api.github.com/repos/pepifer82-cyber/my-project/releases?per_page=100";
    private static readonly HttpClient Http = CreateClient();
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ATPrinterAgent", "updates");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ATPrinterAgent-Updater/0.3.5");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<string?> CheckAndOfferAsync(IWin32Window owner, bool manual, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(ReleasesApi, ct);
            if (!response.IsSuccessStatusCode)
            {
                if (manual) MessageBox.Show(owner, $"No se pudo consultar el servidor de actualizaciones (HTTP {(int)response.StatusCode}).", "A&T Printer Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            JsonElement selectedRelease = default;
            Version? latest = null;
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                var tag = release.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
                const string prefix = "agent-v";
                if (tag is null || !tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Version.TryParse(tag[prefix.Length..], out var parsed)) continue;
                if (latest is null || parsed.CompareTo(latest) > 0)
                {
                    latest = parsed;
                    selectedRelease = release;
                }
            }

            if (latest is null || selectedRelease.ValueKind != JsonValueKind.Object)
            {
                if (manual) MessageBox.Show(owner, "No hay una versión nueva de A&T Printer Agent publicada en el canal de actualizaciones.", "A&T Printer Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            var current = typeof(UpdateManager).Assembly.GetName().Version ?? new Version(0, 0);
            if (latest <= current)
            {
                if (manual) MessageBox.Show(owner, $"Ya tenés la versión más reciente ({current.Major}.{current.Minor}.{current.Build}).", "A&T Printer Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            var assets = selectedRelease.GetProperty("assets").EnumerateArray().ToList();
            var installer = assets.FirstOrDefault(a =>
            {
                var n = a.TryGetProperty("name", out var nv) ? nv.GetString() ?? "" : "";
                return n.Equals($"Instalar_ATPrinterAgent_v{latest.Major}.{latest.Minor}.{latest.Build}.exe", StringComparison.OrdinalIgnoreCase);
            });
            var sums = assets.FirstOrDefault(a => (a.TryGetProperty("name", out var nv) ? nv.GetString() : null) == "SHA256SUMS.txt");
            if (installer.ValueKind == JsonValueKind.Undefined || sums.ValueKind == JsonValueKind.Undefined)
            {
                if (manual) MessageBox.Show(owner, $"Se encontró la versión {latest}, pero el lanzamiento no contiene el instalador y el archivo SHA256SUMS.txt requeridos. No se instalará un archivo sin verificar.", "A&T Printer Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            var installerUrl = installer.GetProperty("browser_download_url").GetString();
            var sumsUrl = sums.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(installerUrl, UriKind.Absolute, out var installerUri) || installerUri.Scheme != Uri.UriSchemeHttps ||
                !Uri.TryCreate(sumsUrl, UriKind.Absolute, out var sumsUri) || sumsUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Las direcciones de descarga no son HTTPS válidas.");

            var name = $"Instalar_ATPrinterAgent_v{latest.Major}.{latest.Minor}.{latest.Build}.exe";
            Directory.CreateDirectory(CacheDir);
            var installerPath = Path.Combine(CacheDir, name);
            var sumsText = await Http.GetStringAsync(sumsUri, ct);
            var expectedHash = ParseSha256(sumsText, name);
            if (expectedHash is null) throw new InvalidDataException("El archivo SHA256SUMS.txt no contiene el hash esperado del instalador.");

            if (!File.Exists(installerPath) || !HashMatches(installerPath, expectedHash))
            {
                var tempPath = installerPath + ".download";
                using (var stream = await Http.GetStreamAsync(installerUri, ct))
                using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    await stream.CopyToAsync(output, ct);
                if (!HashMatches(tempPath, expectedHash))
                {
                    try { File.Delete(tempPath); } catch { }
                    throw new InvalidDataException("La verificación SHA-256 falló. El instalador fue rechazado.");
                }
                File.Move(tempPath, installerPath, true);
            }

            var answer = MessageBox.Show(owner,
                $"Está disponible A&T Printer Agent {latest}.\n\nLa actualización conserva la configuración local y el historial de Supabase. Se solicitará permiso de administrador de Windows para reemplazar los archivos.\n\n¿Descargar/verificar e instalar ahora?",
                "Actualización de A&T Printer Agent", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer != DialogResult.Yes) return null;

            var start = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = CacheDir
            };
            Process.Start(start);
            return "UPDATE_STARTED";
        }
        catch (OperationCanceledException) { return null; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            if (manual) MessageBox.Show(owner, "Cancelaste el permiso de administrador. No se instaló ninguna actualización.", "A&T Printer Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        catch (Exception ex)
        {
            if (manual) MessageBox.Show(owner, "No se pudo completar la actualización:\n" + ex.Message, "A&T Printer Agent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
    }

    private static string? ParseSha256(string text, string expectedFileName)
    {
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 64 && parts[^1].TrimStart('*').Equals(expectedFileName, StringComparison.OrdinalIgnoreCase) && parts[0].All(Uri.IsHexDigit))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    private static bool HashMatches(string path, string expected)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(expected));
    }
}