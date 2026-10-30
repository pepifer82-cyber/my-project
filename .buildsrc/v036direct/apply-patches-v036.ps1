param([Parameter(Mandatory=$true)][string]$AgentRoot)
$ErrorActionPreference = "Stop"
$utf8 = [System.Text.UTF8Encoding]::new($false)
$crlf = [string][char]13 + [string][char]10
$lf = [string][char]10
function Normalize-Lines([string]$Value) { return $Value.Replace($script:crlf,$script:lf) }
function Replace-Required {
  param([string]$Path,[string]$Old,[string]$New,[string]$Label)
  $text=Normalize-Lines ([IO.File]::ReadAllText($Path));$Old=Normalize-Lines $Old;$New=Normalize-Lines $New
  if(-not $text.Contains($Old)){throw "Patch v0.3.6: no se encontró '$Label' en $Path"}
  $text=$text.Replace($Old,$New);[IO.File]::WriteAllText($Path,$text,$utf8)
}
$src=Join-Path $AgentRoot "src"
$models=Join-Path $src "Models.cs"
$sync=Join-Path $src "SupabaseSync.cs"
$network=Join-Path $src "NetworkPrinterScanner.cs"
$snmp=Join-Path $src "SnmpClient.cs"
$main=Join-Path $src "MainForm.cs"
$proj=Join-Path $src "ATPrinterAgent.csproj"
$iss=Join-Path $AgentRoot "AandTPrinterAgent.iss"

Replace-Required $models '    public string? SerialNumber { get; set; }' '    public string? SerialNumber { get; set; }
    public string? MacAddress { get; set; }' "PrinterInfo MAC field"
Replace-Required $models '    public string? serial_number { get; set; }' '    public string? serial_number { get; set; }
    public string? mac_address { get; set; }' "payload MAC field"

$oldPayload='''        serial_number = SnmpClient.NormalizeSerial(x.SerialNumber),
'''
$newPayload='''        serial_number = SnmpClient.NormalizeSerial(x.SerialNumber),
        mac_address = NormalizeMacAddress(x.MacAddress),
'''
Replace-Required $sync $oldPayload $newPayload "sync MAC payload"
$helper='''    private static string? NormalizeMacAddress(string? value)
    {
        var hex = new string((value ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12 || hex.All(c => c == '0') || hex.All(c => c == 'F')) return null;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

'''
Replace-Required $sync '    private static PrinterPayload ToPayload(PrinterInfo x) => new()' ($helper+'    private static PrinterPayload ToPayload(PrinterInfo x) => new()') "MAC normalization"

Replace-Required $network 'using System.Net.Sockets;' 'using System.Net.Sockets;
using System.Runtime.InteropServices;' "ARP interop using"
$arp='''    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destinationIp, uint sourceIp, [Out] byte[] macAddress, ref uint physicalAddressLength);

    /// <summary>Obtiene la MAC del vecino IPv4 desde la tabla ARP de Windows.</summary>
    public static string? ResolveMacAddress(string? ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork) return null;
        try
        {
            var destination = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
            var mac = new byte[8];
            uint length = (uint)mac.Length;
            var result = SendARP(destination, 0, mac, ref length);
            if (result != 0 || length < 6) return null;
            var hex = Convert.ToHexString(mac.AsSpan(0, (int)length));
            if (hex.Length != 12 || hex.All(c => c == '0') || hex.All(c => c == 'F')) return null;
            return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
        }
        catch { return null; }
    }

'''
Replace-Required $network '    private const int DefaultConcurrency = 32;' ('    private const int DefaultConcurrency = 32;

'+$arp.TrimEnd()) "Windows ARP resolver"
Replace-Required $network '            IpAddress = ip,
            WindowsPrinterName = null,' '            IpAddress = ip,
            MacAddress = ResolveMacAddress(ip),
            WindowsPrinterName = null,' "network printer MAC"

Replace-Required $snmp '        public static string RicohSerial => "1.3.6.1.4.1.367.3.2.1.2.1.4.0";' '        public static string RicohSerial => "1.3.6.1.4.1.367.3.2.1.2.1.4.0";
        public static string RicohTotalCounter => "1.3.6.1.4.1.367.3.2.1.2.19.1.0";
        public static string PantumTotalCounter => "1.3.6.1.4.1.40093.10.3.1.1";' "vendor counter OIDs"
$vendorCounters='''        // Vendor global counters: Pantum BM5100FDW and Ricoh expose dedicated
        // read-only lifetime counter OIDs. Prefer them to generic table fallbacks.
        var vendorTextForCounter = $"{printer.Manufacturer} {printer.Model} {printer.DisplayName}".ToLowerInvariant();
        if (vendorTextForCounter.Contains("pantum"))
        {
            var vendorCounter = await GetAsync(ip, OidsPublic.PantumTotalCounter, ct).ConfigureAwait(false);
            if (vendorCounter.Success && vendorCounter.Number is >= 0)
            {
                printer.PageCount = vendorCounter.Number;
                printer.CounterSource = "SNMP Pantum global counter";
            }
            else if (printer.PageCount == 0)
            {
                // Pantum's generic table can expose a misleading zero on some firmware.
                // Do not publish a false lifetime meter when the vendor OID is unavailable.
                printer.PageCount = null;
                printer.CounterSource = null;
            }
        }
        else if (vendorTextForCounter.Contains("ricoh") || vendorTextForCounter.Contains("mp c306"))
        {
            var vendorCounter = await GetAsync(ip, OidsPublic.RicohTotalCounter, ct).ConfigureAwait(false);
            if (vendorCounter.Success && vendorCounter.Number is >= 0)
            {
                printer.PageCount = vendorCounter.Number;
                printer.CounterSource = "SNMP Ricoh total counter";
            }
        }

'''
Replace-Required $snmp '        // hrDeviceDescr often contains a much better model than sysName.' ($vendorCounters+'        // hrDeviceDescr often contains a much better model than sysName.') "Pantum and Ricoh counter probing"

$oldEnrich='''    private async Task EnrichIpPrintersAsync(List<PrinterInfo> printers)
    {
        if (!_config.SnmpEnabled) return;
        using var gate = new SemaphoreSlim(12);
        var tasks = printers.Where(p => !string.IsNullOrWhiteSpace(p.IpAddress)).Select(async printer =>
        {
            await gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                var client = new SnmpClient(_config.SnmpCommunity, _config.SnmpTimeoutMs);
                await client.EnrichAsync(printer, _shutdown.Token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
'''
$newEnrich='''    private async Task EnrichIpPrintersAsync(List<PrinterInfo> printers)
    {
        using var gate = new SemaphoreSlim(12);
        var tasks = printers.Where(p => !string.IsNullOrWhiteSpace(p.IpAddress)).Select(async printer =>
        {
            await gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                // MAC identity is collected independently of SNMP so duplicate
                // Windows queues can still be tied to one physical network device.
                printer.MacAddress ??= NetworkPrinterScanner.ResolveMacAddress(printer.IpAddress);
                if (_config.SnmpEnabled)
                {
                    var client = new SnmpClient(_config.SnmpCommunity, _config.SnmpTimeoutMs);
                    await client.EnrichAsync(printer, _shutdown.Token).ConfigureAwait(false);
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
'''
Replace-Required $main $oldEnrich $newEnrich "MAC resolution independent of SNMP"

Replace-Required $sync 'ATPrinterAgent/0.3.5 (+A&T Informatica)' 'ATPrinterAgent/0.3.6 (+A&T Informatica)' "agent user agent version"
Replace-Required $sync 'public static string AppVersion => "0.3.5";' 'public static string AppVersion => "0.3.6";' "agent app version"
Replace-Required $proj '<Version>0.3.5</Version>' '<Version>0.3.6</Version>' "project version"
Replace-Required $proj '<AssemblyVersion>0.3.5.0</AssemblyVersion>' '<AssemblyVersion>0.3.6.0</AssemblyVersion>' "assembly version"
Replace-Required $proj '<FileVersion>0.3.5.0</FileVersion>' '<FileVersion>0.3.6.0</FileVersion>' "file version"
Replace-Required $proj '<InformationalVersion>0.3.5-serial-fix-remote-updater</InformationalVersion>' '<InformationalVersion>0.3.6-mac-identity-vendor-counters</InformationalVersion>' "informational version"
Replace-Required $main 'Text = "A&T Printer Agent 0.3.5";' 'Text = "A&T Printer Agent 0.3.6";' "window title version"
Replace-Required $main 'Text = "v0.3.5 · Windows"' 'Text = "v0.3.6 · Windows"' "visible version"
Replace-Required $iss '#define MyAppVersion "0.3.5"' '#define MyAppVersion "0.3.6"' "installer version"
Replace-Required $iss 'OutputBaseFilename=Instalar_ATPrinterAgent_v0.3.5' 'OutputBaseFilename=Instalar_ATPrinterAgent_v0.3.6' "installer output filename"

$final= [IO.File]::ReadAllText($network)+[IO.File]::ReadAllText($snmp)+[IO.File]::ReadAllText($sync)
if(-not $final.Contains("ResolveMacAddress") -or -not $final.Contains("PantumTotalCounter") -or -not $final.Contains("mac_address = NormalizeMacAddress")){throw "Validación v0.3.6 incompleta"}
Write-Host "Patch v0.3.6 aplicado: identidad MAC, contador Pantum/Ricoh y versión."
