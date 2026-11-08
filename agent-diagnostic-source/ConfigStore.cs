using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ATPrinterAgent;

public sealed class ConfigStore
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ATPrinterAgent");

    private static readonly string PathName = System.IO.Path.Combine(Folder, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string ConfigPath => PathName;

    public AgentConfig Load()
    {
        Directory.CreateDirectory(Folder);
        AgentConfig? config = null;
        string? source = null;

        foreach (var candidate in LegacyPaths())
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var raw = File.ReadAllText(candidate);
                config = JsonSerializer.Deserialize<AgentConfig>(raw, JsonOptions);
                config ??= new AgentConfig();
                if (string.IsNullOrWhiteSpace(config.Serial)) config.Serial = ExtractLegacySerial(raw);
                source = candidate;
                break;
            }
            catch { }
        }

        config ??= new AgentConfig();
        config.InstallId = NormalizeInstallId(config.InstallId);
        config.RemoteConfigUrl = string.IsNullOrWhiteSpace(config.RemoteConfigUrl)
            ? "https://nxpdcwoypvlcgwuvjuro.supabase.co/functions/v1/printer-agent-remote-config"
            : config.RemoteConfigUrl.Trim();
        config.RemoteCommandAcks ??= new List<RemoteCommandAck>();
        config.SnmpVersion = string.IsNullOrWhiteSpace(config.SnmpVersion) ? "auto" : config.SnmpVersion.Trim().ToLowerInvariant();
        if (config.SnmpVersion is not ("auto" or "v1" or "v2c" or "v3")) config.SnmpVersion = "auto";
        config.SnmpV3User ??= "";
        config.SnmpV3AuthProtocol ??= "SHA256";
        config.SnmpV3PrivacyProtocol ??= "AES128";
        config.SnmpV3ContextName ??= "";
        config.Serial = UnprotectOrPlain(config.Serial);
        config.SnmpV3AuthPassword = UnprotectOrPlain(config.SnmpV3AuthPassword);
        config.SnmpV3PrivacyPassword = UnprotectOrPlain(config.SnmpV3PrivacyPassword);
        // Supported automatic upload schedules are 30 minutes, 1 hour, and 2 hours.
        config.SyncIntervalSeconds = config.SyncIntervalSeconds switch
        {
            3600 => 3600,
            7200 => 7200,
            _ => 1800
        };

        // Migrate the former single-IP setting into the new persistent IP list.
        config.NetworkIps ??= new List<string>();
        var legacyIp = config.NetworkIp?.Trim();
        if (!string.IsNullOrWhiteSpace(legacyIp) &&
            !config.NetworkIps.Any(ip => string.Equals(ip?.Trim(), legacyIp, StringComparison.OrdinalIgnoreCase)))
            config.NetworkIps.Add(legacyIp);
        config.NetworkIp = "";
        config.NetworkIps = config.NetworkIps
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(ip => ip.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Migrate an older config to the v0.2.1 location on the first successful load.
        if (!string.IsNullOrWhiteSpace(source) && !string.Equals(source, PathName, StringComparison.OrdinalIgnoreCase))
        {
            try { Save(config); } catch { }
        }

        return config;
    }

    private static IEnumerable<string> LegacyPaths()
    {
        yield return PathName;
        yield return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ATPrinterAgent", "config.json");
        yield return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATPrinterAgent", "config.json");
        yield return System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");
    }

    private static string ExtractLegacySerial(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var names = new[] { "serial", "activation_serial", "project_serial", "api_serial", "license_serial", "device_serial" };
            foreach (var name in names)
            {
                if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    public void Save(AgentConfig config)
    {
        Directory.CreateDirectory(Folder);
        config.InstallId = NormalizeInstallId(config.InstallId);
        config.Serial = Protect(config.Serial);
        config.SnmpV3AuthPassword = Protect(config.SnmpV3AuthPassword);
        config.SnmpV3PrivacyPassword = Protect(config.SnmpV3PrivacyPassword);
        try
        {
            File.WriteAllText(PathName, JsonSerializer.Serialize(config, JsonOptions), Encoding.UTF8);
        }
        finally
        {
            config.Serial = UnprotectOrPlain(config.Serial);
            config.SnmpV3AuthPassword = UnprotectOrPlain(config.SnmpV3AuthPassword);
            config.SnmpV3PrivacyPassword = UnprotectOrPlain(config.SnmpV3PrivacyPassword);
        }
    }

    private static string NormalizeInstallId(string? value)
    {
        if (Guid.TryParse(value, out var guid)) return guid.ToString();
        return Guid.NewGuid().ToString();
    }

    private static string Protect(string? value)
    {
        value ??= "";
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (value.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase)) return value;

        try
        {
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value),
                null,
                DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(bytes);
        }
        catch
        {
            // Preserve functionality even when DPAPI is unavailable.
            return value;
        }
    }

    private static string UnprotectOrPlain(string? value)
    {
        value ??= "";
        if (!value.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase)) return value;
        try
        {
            var bytes = Convert.FromBase64String(value[6..]);
            var clear = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch
        {
            return "";
        }
    }
}
