using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ATPrinterAgent;

public sealed class SyncResult
{
    public bool Ok { get; init; }
    public bool TransientFailure { get; init; }
    public string Message { get; init; } = "";
    public string? AgentCode { get; init; }
    public string? ProjectName { get; init; }
    public List<RemoteAgentCommand> RemoteCommands { get; init; } = new();
    public List<long> AcknowledgedCommandIds { get; init; } = new();
}

public sealed class SupabaseSync
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly string OutboxFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ATPrinterAgent", "outbox");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SyncResult> PollRemoteCommandsAsync(AgentConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Serial))
            return new SyncResult { Ok = false, Message = "Falta el serial para recibir la configuración remota." };
        if (string.IsNullOrWhiteSpace(config.InstallId))
            return new SyncResult { Ok = false, Message = "Falta el identificador de instalación." };
        if (string.IsNullOrWhiteSpace(config.RemoteConfigUrl) ||
            !Uri.TryCreate(config.RemoteConfigUrl, UriKind.Absolute, out var remoteUri) || remoteUri.Scheme != Uri.UriSchemeHttps)
            return new SyncResult { Ok = false, Message = "La URL de configuración remota no es válida." };

        var payload = new RemoteCommandPollPayload
        {
            serial = config.Serial,
            install_id = config.InstallId,
            command_acks = (config.RemoteCommandAcks ?? new List<RemoteCommandAck>()).Take(50).ToList(),
            config_state = new RemoteConfigSnapshot
            {
                usb_control_enabled = config.UsbControlEnabled,
                windows_scan_enabled = config.WindowsScanEnabled,
                network_scan_enabled = config.NetworkScanEnabled,
                network_scan_on_start = config.NetworkScanOnStart,
                sync_interval_seconds = config.SyncIntervalSeconds,
                network_ips = (config.NetworkIps ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(128).ToList(),
                network_range_start = config.NetworkRangeStart ?? "",
                network_range_end = config.NetworkRangeEnd ?? "",
                network_ranges = (config.NetworkRanges ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(64).ToList(),
                reported_at = DateTimeOffset.UtcNow.ToString("O")
            }
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, config.RemoteConfigUrl);
        request.Headers.TryAddWithoutValidation("apikey", config.PublishableKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                return new SyncResult
                {
                    Ok = false,
                    TransientFailure = status >= 500 || status == 408 || status == 429,
                    Message = ExtractError(body, $"HTTP {status}")
                };
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
            var commands = new List<RemoteAgentCommand>();
            if (root.TryGetProperty("remote_commands", out var cmdArray) && cmdArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in cmdArray.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idValue) || !idValue.TryGetInt64(out var id)) continue;
                    var command = item.TryGetProperty("command", out var cmdValue) ? cmdValue.GetString() ?? "" : "";
                    var payloadValue = item.TryGetProperty("payload", out var pValue) ? pValue.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                    commands.Add(new RemoteAgentCommand { id = id, command = command, payload = payloadValue,
                        created_at = item.TryGetProperty("created_at", out var created) ? created.GetString() : null });
                }
            }
            var acknowledged = new List<long>();
            if (root.TryGetProperty("acknowledged_command_ids", out var ackArray) && ackArray.ValueKind == JsonValueKind.Array)
                foreach (var value in ackArray.EnumerateArray()) if (value.TryGetInt64(out var id)) acknowledged.Add(id);

            return new SyncResult
            {
                Ok = ok,
                Message = ok ? "Configuración remota consultada." : ExtractError(body, "Respuesta remota no válida."),
                AgentCode = root.TryGetProperty("agent_code", out var ac) ? ac.GetString() : null,
                ProjectName = root.TryGetProperty("project_name", out var pn) ? pn.GetString() : null,
                RemoteCommands = commands,
                AcknowledgedCommandIds = acknowledged.Distinct().ToList()
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new SyncResult { Ok = false, TransientFailure = true, Message = ex.Message };
        }
    }

    public async Task<SyncResult> SendAsync(AgentConfig config, List<PrinterInfo> printers, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Serial))
            return new SyncResult { Ok = false, Message = "Falta el serial de activación." };
        if (string.IsNullOrWhiteSpace(config.SyncUrl))
            return new SyncResult { Ok = false, Message = "Falta la URL de sincronización." };

        var captureTime = DateTimeOffset.UtcNow.ToString("O");
        var payload = new SyncPayload
        {
            serial = config.Serial,
            install_id = config.InstallId,
            machine_name = Environment.MachineName,
            windows_user = Environment.UserName,
            app_version = AppVersion,
            printers = printers.Select(ToPayload).ToList()
        };
        foreach (var printer in payload.printers) printer.observed_at = captureTime;
        var currentJson = JsonSerializer.Serialize(payload, JsonOptions);

        try
        {
            Directory.CreateDirectory(OutboxFolder);
            // Drain queued snapshots in capture-time order before sending the newest one.
            foreach (var queuedFile in Directory.EnumerateFiles(OutboxFolder, "*.json")
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList())
            {
                ct.ThrowIfCancellationRequested();
                string queuedJson;
                try { queuedJson = ReadQueuedJson(queuedFile); }
                catch
                {
                    // Preserve a damaged/unreadable file for manual diagnosis rather than silently discarding it.
                    continue;
                }

                var queuedResult = await SendJsonAsync(config, queuedJson, ct).ConfigureAwait(false);
                if (!queuedResult.Ok)
                {
                    if (queuedResult.TransientFailure)
                    {
                        QueueJson(currentJson, captureTime);
                        return new SyncResult
                        {
                            Ok = false,
                            TransientFailure = true,
                            Message = "Sin conexión: la lectura nueva quedó guardada localmente. Se enviará el historial pendiente cuando vuelva Internet."
                        };
                    }
                    return queuedResult;
                }
                try { File.Delete(queuedFile); } catch { }
            }

            var currentResult = await SendJsonAsync(config, currentJson, ct).ConfigureAwait(false);
            if (currentResult.Ok) return currentResult;

            if (currentResult.TransientFailure)
            {
                QueueJson(currentJson, captureTime);
                return new SyncResult
                {
                    Ok = false,
                    TransientFailure = true,
                    Message = "Sin conexión: la lectura quedó guardada localmente y se enviará cuando vuelva Internet."
                };
            }
            return currentResult;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            try { QueueJson(currentJson, captureTime); }
            catch { return new SyncResult { Ok = false, TransientFailure = true, Message = "No se pudo sincronizar ni guardar la cola local: " + ex.Message }; }
            return new SyncResult
            {
                Ok = false,
                TransientFailure = true,
                Message = "Conexión no disponible. La lectura se guardó localmente para reintentar: " + ex.Message
            };
        }
    }

    private static async Task<SyncResult> SendJsonAsync(AgentConfig config, string json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config.SyncUrl);
        request.Headers.TryAddWithoutValidation("apikey", config.PublishableKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                bool transient = status >= 500 || status == 408 || status == 429;
                return new SyncResult
                {
                    Ok = false,
                    TransientFailure = transient,
                    Message = ExtractError(body, $"HTTP {status}")
                };
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okValue) && okValue.GetBoolean();
            return new SyncResult
            {
                Ok = ok,
                TransientFailure = false,
                Message = ok ? "Sincronización correcta." : ExtractError(body, "Respuesta del servidor no válida."),
                AgentCode = root.TryGetProperty("agent_code", out var ac) ? ac.GetString() : null,
                ProjectName = root.TryGetProperty("project_name", out var pn) ? pn.GetString() : null,
                RemoteCommands = ParseRemoteCommands(root),
                AcknowledgedCommandIds = ParseAcknowledgedCommandIds(root)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new SyncResult { Ok = false, TransientFailure = true, Message = ex.Message };
        }
    }

    private static List<RemoteAgentCommand> ParseRemoteCommands(JsonElement root)
    {
        var list = new List<RemoteAgentCommand>();
        if (!root.TryGetProperty("remote_commands", out var array) || array.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in array.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idValue) || !idValue.TryGetInt64(out var id)) continue;
            list.Add(new RemoteAgentCommand
            {
                id = id,
                command = item.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "",
                payload = item.TryGetProperty("payload", out var p) ? p.Clone() : JsonDocument.Parse("{}").RootElement.Clone(),
                created_at = item.TryGetProperty("created_at", out var t) ? t.GetString() : null
            });
        }
        return list;
    }

    private static List<long> ParseAcknowledgedCommandIds(JsonElement root)
    {
        var list = new List<long>();
        if (root.TryGetProperty("acknowledged_command_ids", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var value in array.EnumerateArray()) if (value.TryGetInt64(out var id)) list.Add(id);
        return list.Distinct().ToList();
    }

    private static void QueueJson(string json, string captureTime)
    {
        Directory.CreateDirectory(OutboxFolder);
        var stamp = DateTimeOffset.TryParse(captureTime, out var dt)
            ? dt.UtcDateTime.ToString("yyyyMMddHHmmssfff")
            : DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var file = Path.Combine(OutboxFolder, stamp + "_" + Guid.NewGuid().ToString("N") + ".json");
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllText(file, "dpapi:" + Convert.ToBase64String(protectedBytes), Encoding.UTF8);
        }
        catch
        {
            // Keep the queued file usable if Windows DPAPI is unavailable on this profile.
            File.WriteAllText(file, json, Encoding.UTF8);
        }
    }

    private static string ReadQueuedJson(string path)
    {
        var value = File.ReadAllText(path, Encoding.UTF8).TrimStart('\uFEFF');
        if (!value.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase)) return value;
        var protectedBytes = Convert.FromBase64String(value[6..]);
        var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private static PrinterPayload ToPayload(PrinterInfo x) => new()
    {
        observed_at = DateTimeOffset.UtcNow.ToString("O"),
        printer_key = x.PrinterKey,
        display_name = x.DisplayName,
        manufacturer = x.Manufacturer,
        model = x.Model,
        connection_type = x.ConnectionType == "network" ? "ip" : x.ConnectionType,
        ip_address = x.IpAddress,
        windows_port = x.WindowsPort,
        usb_port = x.UsbPort,
        windows_printer_name = x.WindowsPrinterName,
        serial_number = SnmpClient.NormalizeSerial(x.SerialNumber),
        last_status = x.LastStatus,
        snmp_reachable = x.SnmpReachable,
        snmp_name = x.SnmpName,
        // The general counter is the printer's cumulative lifetime pages read by SNMP/PJL/IPP/HTTP.
        // Keep page_count as a legacy alias so old server code continues to work.
        total_print_count = x.PageCount,
        page_count = x.PageCount,
        scanner_count = x.ScannerCount,
        toner_black_percent = x.TonerBlackPercent,
        toner_cyan_percent = x.TonerCyanPercent,
        toner_magenta_percent = x.TonerMagentaPercent,
        toner_yellow_percent = x.TonerYellowPercent,
        snmp_error = x.SnmpError,
        counter_source = x.CounterSource,
        serial_source = x.SerialSource,
        identity_source = x.IdentitySource
    };

    private static string ExtractError(string body, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.GetString() ?? fallback;
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString() ?? fallback;
        }
        catch { }
        return fallback;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ATPrinterAgent/0.3.9 (+A&T Informatica)");
        return client;
    }

    public static string AppVersion => "0.3.9";
}
