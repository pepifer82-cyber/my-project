using System.Text.Json;
using System.Text.Json.Serialization;

namespace ATPrinterAgent;

public sealed class AgentConfig
{
    [JsonPropertyName("sync_url")]
    public string SyncUrl { get; set; } = "https://nxpdcwoypvlcgwuvjuro.supabase.co/functions/v1/printer-agent-sync";

    [JsonPropertyName("remote_config_url")]
    public string RemoteConfigUrl { get; set; } = "https://nxpdcwoypvlcgwuvjuro.supabase.co/functions/v1/printer-agent-remote-config";

    [JsonPropertyName("publishable_key")]
    public string PublishableKey { get; set; } = "sb_publishable_fwz_qVPsE9lEp0v4XA1b2g_buuMHcin";

    // Old versions may use this name. It remains for migration/compatibility.
    [JsonPropertyName("serial")]
    public string Serial { get; set; } = "";

    [JsonPropertyName("install_id")]
    public string InstallId { get; set; } = "";

    [JsonPropertyName("snmp_enabled")]
    public bool SnmpEnabled { get; set; } = true;

    [JsonPropertyName("snmp_community")]
    public string SnmpCommunity { get; set; } = "public";

    [JsonPropertyName("snmp_timeout_ms")]
    public int SnmpTimeoutMs { get; set; } = 1200;

    // "auto" means SNMPv2c first and SNMPv1 fallback. SNMPv3 is opt-in because
    // every printer needs its own USM username and security credentials.
    [JsonPropertyName("snmp_version")]
    public string SnmpVersion { get; set; } = "auto";

    [JsonPropertyName("snmp_v3_user")]
    public string SnmpV3User { get; set; } = "";

    [JsonPropertyName("snmp_v3_auth_protocol")]
    public string SnmpV3AuthProtocol { get; set; } = "SHA256";

    [JsonPropertyName("snmp_v3_auth_password")]
    public string SnmpV3AuthPassword { get; set; } = "";

    [JsonPropertyName("snmp_v3_privacy_protocol")]
    public string SnmpV3PrivacyProtocol { get; set; } = "AES128";

    [JsonPropertyName("snmp_v3_privacy_password")]
    public string SnmpV3PrivacyPassword { get; set; } = "";

    [JsonPropertyName("snmp_v3_context_name")]
    public string SnmpV3ContextName { get; set; } = "";

    [JsonPropertyName("sync_interval_seconds")]
    public int SyncIntervalSeconds { get; set; } = 1800;

    [JsonPropertyName("usb_control_enabled")]
    public bool UsbControlEnabled { get; set; } = true;

    [JsonPropertyName("scan_on_start")]
    public bool ScanOnStart { get; set; } = true;

    [JsonPropertyName("start_with_windows")]
    public bool StartWithWindows { get; set; } = true;

    [JsonPropertyName("windows_scan_enabled")]
    public bool WindowsScanEnabled { get; set; } = true;

    [JsonPropertyName("network_scan_enabled")]
    public bool NetworkScanEnabled { get; set; } = true;

    [JsonPropertyName("network_scan_on_start")]
    public bool NetworkScanOnStart { get; set; } = true;

    // False on older configurations so the operator can perform the explicit first search.
    [JsonPropertyName("initial_discovery_complete")]
    public bool InitialDiscoveryComplete { get; set; } = false;

    // Legacy single-IP setting is retained so older config.json files continue to load.
    [JsonPropertyName("network_ip")]
    public string NetworkIp { get; set; } = "";

    [JsonPropertyName("network_ips")]
    public List<string> NetworkIps { get; set; } = new();

    [JsonPropertyName("network_range_start")]
    public string NetworkRangeStart { get; set; } = "";

    [JsonPropertyName("network_range_end")]
    public string NetworkRangeEnd { get; set; } = "";

    // Additional ranges, one per line in the UI, formatted as "start - end".
    // The legacy start/end pair above remains supported for old config files.
    [JsonPropertyName("network_ranges")]
    public List<string> NetworkRanges { get; set; } = new();

    // Acknowledgements persist until Supabase confirms they were recorded.
    [JsonPropertyName("remote_command_acks")]
    public List<RemoteCommandAck> RemoteCommandAcks { get; set; } = new();
}

public sealed class PrinterInfo
{
    public string PrinterKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string ConnectionType { get; set; } = "unknown";
    public string? IpAddress { get; set; }
    public string? WindowsPort { get; set; }
    public string? UsbPort { get; set; }
    public string? WindowsPrinterName { get; set; }
    // Technical Windows/SMB/WSD name. The visible DisplayName should be the physical printer model.
    public string? HostName { get; set; }
    public string? SerialNumber { get; set; }
    public string? LastStatus { get; set; }
    public bool SnmpReachable { get; set; }
    public string? SnmpName { get; set; }
    public long? PageCount { get; set; }
    public long? ScannerCount { get; set; }
    public double? TonerBlackPercent { get; set; }
    public double? TonerCyanPercent { get; set; }
    public double? TonerMagentaPercent { get; set; }
    public double? TonerYellowPercent { get; set; }
    public string? SnmpError { get; set; }

    // Provenance of the latest successful readings. These are stored in printer_readings.raw_data
    // so a report can tell which protocol actually supplied a counter or serial.
    public string? CounterSource { get; set; }
    public string? SerialSource { get; set; }
    public string? IdentitySource { get; set; }

    public string AcquisitionSummary
    {
        get
        {
            var parts = new List<string>();
            parts.Add("Contador: " + (string.IsNullOrWhiteSpace(CounterSource) ? "—" : CounterSource));
            parts.Add("Serie: " + (string.IsNullOrWhiteSpace(SerialSource) ? "—" : SerialSource));
            if (!string.IsNullOrWhiteSpace(IdentitySource)) parts.Add("Identidad: " + IdentitySource);
            return string.Join(" · ", parts);
        }
    }
}

public sealed class RemoteCommandAck
{
    public long id { get; set; }
    public string status { get; set; } = "done";
    public string? result_message { get; set; }
}

public sealed class RemoteAgentCommand
{
    public long id { get; set; }
    public string command { get; set; } = "";
    public JsonElement payload { get; set; }
    public string? created_at { get; set; }
}

public sealed class RemoteCommandPollPayload
{
    public string serial { get; set; } = "";
    public string install_id { get; set; } = "";
    public List<RemoteCommandAck> command_acks { get; set; } = new();
    public RemoteConfigSnapshot config_state { get; set; } = new();
}

public sealed class RemoteConfigSnapshot
{
    public bool usb_control_enabled { get; set; }
    public bool windows_scan_enabled { get; set; }
    public bool network_scan_enabled { get; set; }
    public bool network_scan_on_start { get; set; }
    public int sync_interval_seconds { get; set; }
    public List<string> network_ips { get; set; } = new();
    public string network_range_start { get; set; } = "";
    public string network_range_end { get; set; } = "";
    public List<string> network_ranges { get; set; } = new();
    public string reported_at { get; set; } = "";
}

public sealed class SyncPayload
{
    public string serial { get; set; } = "";
    public string install_id { get; set; } = "";
    public string? machine_name { get; set; }
    public string? windows_user { get; set; }
    public string? app_version { get; set; }
    public List<PrinterPayload> printers { get; set; } = new();
}

public sealed class PrinterPayload
{
    // Client capture time is preserved when an offline snapshot is queued and resent later.
    public string observed_at { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public string printer_key { get; set; } = "";
    public string display_name { get; set; } = "";
    public string? manufacturer { get; set; }
    public string? model { get; set; }
    public string connection_type { get; set; } = "unknown";
    public string? ip_address { get; set; }
    public string? windows_port { get; set; }
    public string? usb_port { get; set; }
    public string? windows_printer_name { get; set; }
    public string? serial_number { get; set; }
    public string? last_status { get; set; }
    public bool snmp_reachable { get; set; }
    public string? snmp_name { get; set; }
    // Explicit alias for the device's lifetime total print counter. Kept alongside
    // page_count for backwards compatibility with existing Supabase sync handlers.
    public long? total_print_count { get; set; }
    public long? page_count { get; set; }
    public long? scanner_count { get; set; }
    public double? toner_black_percent { get; set; }
    public double? toner_cyan_percent { get; set; }
    public double? toner_magenta_percent { get; set; }
    public double? toner_yellow_percent { get; set; }
    public string? snmp_error { get; set; }
    public string? counter_source { get; set; }
    public string? serial_source { get; set; }
    public string? identity_source { get; set; }
}

public sealed class SnmpResult
{
    public bool Success { get; init; }
    public string? Oid { get; init; }
    public string? Text { get; init; }
    public long? Number { get; init; }
    public string? Error { get; init; }
}
