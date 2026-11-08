using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ATPrinterAgent;

/// <summary>
/// Discovers printers directly on the LAN instead of relying on printers installed in Windows.
/// SNMP v2c is the primary discovery method; common printer TCP ports are used as a fallback
/// so an IP can still be reported when SNMP is disabled.
/// </summary>
public static class NetworkPrinterScanner
{
    private sealed record PjlProbeResult(long? PageCount, string? Serial, string? Identity);
    private sealed record IppProbeResult(long? PageCount, long? ScannerCount, string? Serial, string? Identity, string? CounterSource);

    private static readonly int[] PrinterTcpPorts = [9100, 631, 515, 80, 443];
    private const int MaxTargets = 4096;
    private const int DefaultConcurrency = 32;

    public static async Task<List<PrinterInfo>> ScanAsync(
        AgentConfig config,
        CancellationToken ct,
        IProgress<string>? progress = null)
    {
        // Scan every manually-added individual IP, then the optional range. Old
        // config.json files are migrated by ConfigStore, but NetworkIp remains as fallback.
        // Build one unique target set while reading the configuration. This avoids allocating
        // a huge list when many router ranges overlap, and enforces the total limit immediately.
        var targets = new List<string>();
        var uniqueTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddTargets(IEnumerable<string> batch)
        {
            foreach (var target in batch)
            {
                if (!uniqueTargets.Add(target)) continue;
                targets.Add(target);
                if (targets.Count > MaxTargets)
                    throw new InvalidOperationException($"La búsqueda combinada supera el máximo de {MaxTargets} direcciones IPv4 únicas. Reducí o quitá alguno de los rangos de routers.");
            }
        }

        var individualIps = (config.NetworkIps ?? new List<string>())
            .Append(config.NetworkIp)
            .Where(ip => !string.IsNullOrWhiteSpace(ip));
        foreach (var ip in individualIps)
            AddTargets(BuildTargets(ip, null, null));

        AddTargets(BuildTargets(null, config.NetworkRangeStart, config.NetworkRangeEnd));
        foreach (var range in config.NetworkRanges ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(range)) continue;
            var parts = range.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                throw new InvalidOperationException($"Rango adicional no válido: {range}. Usá formato IP inicial - IP final.");
            AddTargets(BuildTargets(null, parts[0], parts[1]));
        }

        if (targets.Count == 0)
        {
            // When no explicit target is supplied, use the first usable local subnet.
            targets = GetDefaultLocalSubnetTargets();
        }

        if (targets.Count == 0)
            return [];

        if (targets.Count > MaxTargets)
            throw new InvalidOperationException($"La búsqueda combinada contiene {targets.Count} IP. El máximo permitido es {MaxTargets}.");

        var snmp = config.SnmpEnabled ? new SnmpClient(config) : null;
        var results = new List<PrinterInfo>();
        var gate = new SemaphoreSlim(Math.Min(DefaultConcurrency, Math.Max(1, targets.Count)));
        var sync = new object();
        var done = 0;

        try
        {
            var tasks = targets.Select(async ip =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var printer = await ProbeIpAsync(ip, snmp, config.SnmpTimeoutMs, ct).ConfigureAwait(false);
                    if (printer is not null)
                    {
                        lock (sync) results.Add(printer);
                    }
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref done);
                    progress?.Report($"Escaneando red… {n}/{targets.Count} · {NetworkRangeHelper.ToString(targets[Math.Min(n - 1, targets.Count - 1)])}");
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            gate.Dispose();
        }

        return results
            .GroupBy(p => p.IpAddress ?? p.PrinterKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static string? DetectLocalNetworkRange()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(x => x.OperationalStatus == OperationalStatus.Up)
                     .OrderByDescending(x => x.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                     .ThenByDescending(x => x.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
        {
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }

            foreach (var uni in props.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (!TryGetSubnet(uni.Address, uni.IPv4Mask, out var start, out var end)) continue;
                var startUInt = IPToUInt(start);
                var endUInt = IPToUInt(end);
                if (endUInt > startUInt + 1)
                {
                    var usableStart = NetworkRangeHelper.FromUInt(startUInt + 1);
                    var usableEnd = NetworkRangeHelper.FromUInt(endUInt - 1);
                    var count = endUInt - startUInt - 1;
                    if (count is >= 1 and <= MaxTargets)
                        return $"{usableStart} - {usableEnd}";
                }
            }
        }

        return null;
    }

    private static async Task<PrinterInfo?> ProbeIpAsync(
        string ip,
        SnmpClient? snmp,
        int snmpTimeoutMs,
        CancellationToken ct)
    {
        SnmpResult? sysDescr = null;
        SnmpResult? sysName = null;
        SnmpResult? printerName = null;
        SnmpResult? hrDescr = null;
        SnmpResult? serial = null;
        SnmpResult? counter = null;

        if (snmp is not null)
        {
            var probes = await Task.WhenAll(
                snmp.GetAsync(ip, SnmpClient.OidsPublic.SysDescr, ct),
                snmp.GetAsync(ip, SnmpClient.OidsPublic.SysName, ct),
                snmp.GetFirstTableValueAsync(ip, SnmpClient.OidsPublic.PrinterName, ct),
                snmp.GetFirstTableValueAsync(ip, SnmpClient.OidsPublic.HrDeviceDescr, ct)
            ).ConfigureAwait(false);

            sysDescr = probes[0];
            sysName = probes[1];
            printerName = probes[2];
            hrDescr = probes[3];

            // Only make the more specific requests after the device looks printer-like.
            if (IsPrinterLike(sysDescr, sysName, printerName, hrDescr))
            {
                var details = await Task.WhenAll(
                    snmp.GetFirstTableValueAsync(ip, SnmpClient.OidsPublic.Serial, ct),
                    snmp.GetFirstTableValueAsync(ip, SnmpClient.OidsPublic.MarkerLifeCount1, ct)
                ).ConfigureAwait(false);
                serial = details[0];
                counter = details[1];
            }
        }

        var tcpPorts = await ProbePrinterTcpPortsAsync(ip, snmpTimeoutMs, ct).ConfigureAwait(false);
        var snmpPrinter = IsPrinterLike(sysDescr, sysName, printerName, hrDescr);
        var snmpReachable = snmpPrinter || serial is { Success: true } || counter is { Success: true };
        var printPortOpen = tcpPorts.Overlaps(new[] { 9100, 631, 515 });
        var httpProbe = tcpPorts.Overlaps(new[] { 80, 443 })
            ? await ProbeHttpPrinterAsync(ip, tcpPorts, snmpTimeoutMs, ct).ConfigureAwait(false)
            : (Identity: (string?)null, Serial: (string?)null, PageCount: (long?)null);

        // Certain Pantum models respond to SNMP only after their web interface is touched.
        // We have already made an HTTP request above; retry identification and standard
        // serial/counter fields afterwards rather than recording a false SNMP-unreachable state.
        if (snmp is not null && !string.IsNullOrWhiteSpace(httpProbe.Identity))
        {
            await Task.Delay(120, ct).ConfigureAwait(false);
            var wakeInfo = await Task.WhenAll(
                snmp.GetAsync(ip, SnmpClient.OidsPublic.SysDescr, ct),
                snmp.GetAsync(ip, SnmpClient.OidsPublic.SysName, ct),
                snmp.GetFirstTableValueAsync(ip, SnmpClient.OidsPublic.PrinterName, ct),
                snmp.GetFirstTableValueAsync(ip, SnmpClient.OidsPublic.HrDeviceDescr, ct)
            ).ConfigureAwait(false);
            if (wakeInfo[0].Success && !string.IsNullOrWhiteSpace(wakeInfo[0].Text)) sysDescr = wakeInfo[0];
            if (wakeInfo[1].Success && !string.IsNullOrWhiteSpace(wakeInfo[1].Text)) sysName = wakeInfo[1];
            if (wakeInfo[2].Success && !string.IsNullOrWhiteSpace(wakeInfo[2].Text)) printerName = wakeInfo[2];
            if (wakeInfo[3].Success && !string.IsNullOrWhiteSpace(wakeInfo[3].Text)) hrDescr = wakeInfo[3];

            if (SnmpClient.NormalizeSerial(serial?.Text) is null)
            {
                var retrySerial = await snmp.GetFirstTableValueAsync(ip, SnmpClient.OidsPublic.Serial, ct).ConfigureAwait(false);
                if (SnmpClient.NormalizeSerial(retrySerial.Text) is not null) serial = retrySerial;
                else
                {
                    retrySerial = await snmp.GetAsync(ip, "1.3.6.1.2.1.43.5.1.1.17.1.1", ct).ConfigureAwait(false);
                    if (SnmpClient.NormalizeSerial(retrySerial.Text) is not null) serial = retrySerial;
                }
            }
            if (counter?.Number is null or < 0)
            {
                var retryCounter = await snmp.GetFirstTableValueAsync(ip, SnmpClient.OidsPublic.MarkerLifeCount1, ct).ConfigureAwait(false);
                if (retryCounter.Number is >= 0) counter = retryCounter;
            }

            // Recalculate after the wake-up retries; the earlier values were
            // computed before the printer's web interface was contacted.
            snmpPrinter = IsPrinterLike(sysDescr, sysName, printerName, hrDescr);
            snmpReachable = snmpPrinter || serial is { Success: true } || counter is { Success: true };
        }

        // Alternate read-only protocols are only attempted when SNMP has not supplied a meter
        // or physical serial, and only after their TCP port has been found open.
        PjlProbeResult? pjlProbe = null;
        var snmpPageCount = counter is { Success: true, Number: >= 0 } ? counter.Number : (long?)null;
        var snmpSerial = serial is { Success: true } ? SnmpClient.NormalizeSerial(serial.Text) : null;
        if ((snmpPageCount is null || snmpSerial is null) && tcpPorts.Contains(9100))
            pjlProbe = await ProbePjlAsync(ip, snmpTimeoutMs, ct).ConfigureAwait(false);

        IppProbeResult? ippProbe = null;
        if ((snmpPageCount is null || snmpSerial is null || !snmpPrinter) && tcpPorts.Contains(631))
            ippProbe = await ProbeIppAsync(ip, snmpTimeoutMs, ct).ConfigureAwait(false);

        var httpIdentity = httpProbe.Identity;
        var httpPrinter = !string.IsNullOrWhiteSpace(httpIdentity);
        var ippIdentity = ippProbe?.Identity;
        var pjlIdentity = pjlProbe?.Identity;
        var tcpPrinter = printPortOpen || httpPrinter || !string.IsNullOrWhiteSpace(ippIdentity) || !string.IsNullOrWhiteSpace(pjlIdentity);

        if (!snmpPrinter && !tcpPrinter)
            return null;

        var rawIdentity = string.Join(" | ", new[] { sysDescr?.Text, hrDescr?.Text, printerName?.Text, sysName?.Text, ippIdentity, pjlIdentity, httpIdentity }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var model = FirstNonBlank(
            ExtractFriendlyModel(rawIdentity),
            ExtractModel(hrDescr?.Text),
            ExtractModel(sysDescr?.Text),
            NormalizeSysDescr(sysDescr?.Text),
            printerName?.Text,
            IsUsablePrinterName(ippIdentity) ? ippIdentity : null,
            IsUsablePrinterName(pjlIdentity) ? pjlIdentity : null,
            IsUsablePrinterName(httpIdentity) ? httpIdentity : null);
        var displayName = IsUsablePrinterName(model)
            ? model
            : FirstNonBlank(printerName?.Text, model, sysName?.Text, $"Impresora de red {ip}");
        if (string.IsNullOrWhiteSpace(model) && IsUsablePrinterName(displayName))
            model = displayName;
        var manufacturer = GuessManufacturer($"{displayName} {model} {sysDescr?.Text} {hrDescr?.Text}");

        var snmpSerialText = serial is { Success: true } ? SnmpClient.NormalizeSerial(serial.Text) : null;
        var serialFromSnmpText = SnmpClient.NormalizeSerial(ExtractSerialFromText(string.Join(" | ", new[] { sysDescr?.Text, hrDescr?.Text, printerName?.Text, sysName?.Text }.Where(x => !string.IsNullOrWhiteSpace(x)))));
        var pjlSerial = SnmpClient.NormalizeSerial(pjlProbe?.Serial);
        var ippSerial = SnmpClient.NormalizeSerial(ippProbe?.Serial);
        var httpSerial = SnmpClient.NormalizeSerial(httpProbe.Serial);
        var serialText = snmpSerialText ?? serialFromSnmpText ?? pjlSerial ?? ippSerial ?? httpSerial;
        var serialSource = snmpSerialText is not null ? "SNMP Printer-MIB/vendor MIB"
            : serialFromSnmpText is not null ? "SNMP identity text"
            : pjlSerial is not null ? "PJL INFO ID (TCP/9100)"
            : ippSerial is not null ? "IPP printer attributes (TCP/631)"
            : httpSerial is not null ? "HTTP/HTTPS printer page"
            : null;

        var snmpName = sysName is { Success: true } ? sysName.Text : null;
        var pageCount = snmpPageCount ?? pjlProbe?.PageCount ?? ippProbe?.PageCount ?? httpProbe.PageCount;
        var counterSource = snmpPageCount.HasValue ? "SNMP Printer-MIB"
            : pjlProbe?.PageCount.HasValue == true ? "PJL INFO PAGECOUNT (TCP/9100)"
            : ippProbe?.PageCount.HasValue == true ? (ippProbe.CounterSource ?? "IPP (TCP/631)")
            : httpProbe.PageCount.HasValue ? "HTTP/HTTPS printer page"
            : null;
        var identitySource = snmpPrinter ? "SNMP"
            : !string.IsNullOrWhiteSpace(ippIdentity) ? "IPP (TCP/631)"
            : !string.IsNullOrWhiteSpace(pjlIdentity) ? "PJL INFO ID (TCP/9100)"
            : !string.IsNullOrWhiteSpace(httpIdentity) ? "HTTP/HTTPS"
            : "TCP printer port";

        var info = new PrinterInfo
        {
            PrinterKey = BuildNetworkKey(ip, serialText, model),
            DisplayName = displayName,
            Manufacturer = manufacturer,
            Model = model,
            // The server accepts ip/usb/unknown. A network-discovered printer is an IP printer.
            ConnectionType = "ip",
            IpAddress = ip,
            WindowsPrinterName = null,
            SerialNumber = serialText,
            LastStatus = "online",
            SnmpReachable = snmpReachable,
            SnmpName = snmpName,
            PageCount = pageCount,
            ScannerCount = ippProbe?.ScannerCount,
            CounterSource = counterSource,
            SerialSource = serialSource,
            IdentitySource = identitySource,
            SnmpError = snmpReachable ? null : "SNMP no responde; detectada por puerto de impresión."
        };

        return info;
    }

    private static bool IsPrinterLike(SnmpResult? sysDescr, SnmpResult? sysName, SnmpResult? printerName, SnmpResult? hrDescr = null)
    {
        if (printerName?.Success == true && !string.IsNullOrWhiteSpace(printerName.Text))
            return true;

        var text = string.Join(' ', new[] { sysDescr?.Text, sysName?.Text, hrDescr?.Text }.Where(x => !string.IsNullOrWhiteSpace(x)))
            .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] printerWords =
        [
            "printer", "laserjet", "officejet", "deskjet", "inkjet", "pagewide",
            "multifunction", "mfp", "copier", "plotter", "ricoh", "brother", "epson",
            "canon", "kyocera", "konica", "xerox", "lexmark", "pantum", "oki", "sharp",
            "toshiba", "hp ", "hewlett"
        ];

        return printerWords.Any(text.Contains);
    }

    private static async Task<(string? Identity, string? Serial, long? PageCount)> ProbeHttpPrinterAsync(string ip, HashSet<int> openPorts, int timeoutMs, CancellationToken ct)
    {
        string[] printerTerms = ["printer", "laserjet", "officejet", "deskjet", "pagewide", "mfp", "multifunction", "ricoh", "brother", "epson", "canon", "kyocera", "xerox", "lexmark", "pantum", "samsung", "konica", "hewlett", "hp inc", "web image monitor", "bm5100", "sec30"];
        foreach (var port in new[] { 80, 443 }.Where(openPorts.Contains))
        {
            try
            {
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 1800)) };
                var scheme = port == 443 ? "https" : "http";
                var baseUri = new Uri($"{scheme}://{ip}/");
                var body = await FetchSameHostPageAsync(client, baseUri, ip, ct).ConfigureAwait(false) ?? string.Empty;
                var sample = body.Length > 160_000 ? body[..160_000] : body;
                var titleRaw = Regex.Match(sample, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value;
                var title = Regex.Replace(WebUtility.HtmlDecode(titleRaw), @"\s+", " ").Trim();
                var server = string.Empty; // The safe same-host fetch helper does not expose headers.
                var searchable = (title + " " + server + " " + sample).ToLowerInvariant();
                var serial = ExtractSerialFromText(sample);
                var pageCount = ExtractPageCountFromText(sample);

                // Some printer web interfaces expose the serial only on a local
                // Product/Device Information page, not on the home page.
                if (serial is null && printerTerms.Any(searchable.Contains))
                    serial = await ProbeHttpSerialPagesAsync(client, baseUri, ip, sample, ct).ConfigureAwait(false);

                // Keep the HTTP source non-authoritative when it is a generic router page.
                // A known printer word in the title/header/page is required before this IP
                // is accepted as a printer by the HTTP fallback.
                if (!printerTerms.Any(searchable.Contains)) continue;

                var modelFromText = ExtractFriendlyModel(WebUtility.HtmlDecode(sample));
                var identity = FirstNonBlank(
                    IsUsablePrinterName(title) ? title : null,
                    modelFromText,
                    IsUsablePrinterName(server) ? server : null,
                    title.Length >= 4 ? title : null,
                    $"Impresora de red {ip}");
                return (identity, serial, pageCount);
            }
            catch { /* Web UI may reject unauthenticated requests; continue with other detection methods. */ }
        }
        return (null, null, null);
    }

    private static async Task<string?> ProbeHttpSerialPagesAsync(HttpClient client, Uri baseUri, string expectedHost, string homeHtml, CancellationToken ct)
    {
        var candidates = new List<Uri>();
        var relevant = new[] { "device", "product", "serial", "information", "system", "printer", "status", "about", "general" };

        foreach (Match match in Regex.Matches(homeHtml,
            "<a\\b[^>]*\\bhref\\s*=\\s*(['\"])(.*?)\\1[^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var href = WebUtility.HtmlDecode(match.Groups[2].Value).Trim();
            var anchorText = Regex.Replace(WebUtility.HtmlDecode(match.Groups[3].Value), "<[^>]+>", " ");
            var hint = (href + " " + anchorText).ToLowerInvariant();
            if (!relevant.Any(hint.Contains)) continue;
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith("#", StringComparison.Ordinal) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(baseUri, href, out var uri)) continue;
            if (!string.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) continue;
            if (!candidates.Any(x => x.AbsoluteUri.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) candidates.Add(uri);
            if (candidates.Count >= 4) break;
        }

        // Common read-only information routes used by printer web servers.
        foreach (var path in new[] {
            "/hp/device/DeviceInformation/View",
            "/web/guest/en/websys/status/getUnificationStatus.cgi",
            "/general/information.html",
            "/product_info.html"
        })
        {
            if (candidates.Count >= 4) break;
            var uri = new Uri(baseUri, path);
            if (!candidates.Any(x => x.AbsolutePath.Equals(uri.AbsolutePath, StringComparison.OrdinalIgnoreCase))) candidates.Add(uri);
        }

        foreach (var uri in candidates.Take(4))
        {
            ct.ThrowIfCancellationRequested();
            var page = await FetchSameHostPageAsync(client, uri, expectedHost, ct).ConfigureAwait(false);
            var serial = ExtractSerialFromText(page);
            if (serial is not null) return serial;
        }
        return null;
    }

    private static async Task<string?> FetchSameHostPageAsync(HttpClient client, Uri startUri, string expectedHost, CancellationToken ct)
    {
        var current = startUri;
        for (var redirect = 0; redirect < 3; redirect++)
        {
            if (!string.Equals(current.Host, expectedHost, StringComparison.OrdinalIgnoreCase)) return null;
            using var response = await client.GetAsync(current, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
            {
                if (!Uri.TryCreate(current, response.Headers.Location, out var next)) return null;
                if (!string.Equals(next.Host, expectedHost, StringComparison.OrdinalIgnoreCase)) return null;
                current = next;
                continue;
            }
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length > 160_000 ? body[..160_000] : body;
        }
        return null;
    }

    private static async Task<PjlProbeResult?> ProbePjlAsync(string ip, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(Math.Clamp(timeoutMs + 200, 500, 1800));
            await tcp.ConnectAsync(ip, 9100, connectTimeout.Token).ConfigureAwait(false);
            await using var stream = tcp.GetStream();

            // Read-only PJL queries; no print job is sent. INFO ID may expose an IEEE-1284
            // device ID (including SN), and INFO PAGECOUNT is supported by many PCL/PS devices.
            var request = Encoding.ASCII.GetBytes("\u001b%-12345X@PJL INFO ID\r\n@PJL INFO PAGECOUNT\r\n\u001b%-12345X\r\n");
            await stream.WriteAsync(request, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            var buffer = new byte[8192];
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readTimeout.CancelAfter(Math.Clamp(timeoutMs + 300, 700, 2000));
            int count;
            try { count = await stream.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { count = 0; }
            if (count <= 0) return null;

            var text = Encoding.ASCII.GetString(buffer, 0, count).Replace('\0', ' ');
            var pageCount = ExtractPjlPageCount(text);
            var serialMatch = Regex.Match(text,
                @"(?:^|[;,\r\n])\s*(?:SN|SERIAL(?:NUMBER)?|S/N)\s*[:=]\s*([^;,\r\n]+)",
                RegexOptions.IgnoreCase);
            var serial = serialMatch.Success ? SnmpClient.NormalizeSerial(serialMatch.Groups[1].Value) : null;
            var identityParts = new List<string>();
            foreach (var label in new[] { "MFG", "MDL", "DES" })
            {
                var match = Regex.Match(text, $@"(?:^|[;,\r\n])\s*{label}\s*:\s*([^;,\r\n]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var part = match.Groups[1].Value.Trim();
                    if (part.Length > 0) identityParts.Add(label + ":" + part);
                }
            }
            var identity = identityParts.Count == 0 ? null : string.Join("; ", identityParts);
            return pageCount.HasValue || serial is not null || identity is not null
                ? new PjlProbeResult(pageCount, serial, identity)
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }

    private static long? ExtractPjlPageCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"(?im)@PJL\s+INFO\s+PAGECOUNT\s*\r?\n[\x00-\x20]*([0-9]{1,15})\s*(?:\r?\n|$)");
        return match.Success && long.TryParse(match.Groups[1].Value, out var value) && value >= 0 ? value : null;
    }

    private static async Task<IppProbeResult?> ProbeIppAsync(string ip, int timeoutMs, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs + 300, 800, 1700)) };
        foreach (var path in new[] { "/ipp/print", "/ipp" })
        {
            try
            {
                var printerUri = $"ipp://{ip}:631{path}";
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ip}:631{path}");
                request.Content = new ByteArrayContent(BuildIppGetPrinterAttributesRequest(printerUri));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ipp"));

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                var result = ParseIppResponse(bytes);
                if (result is not null && (result.PageCount.HasValue || result.Serial is not null || result.Identity is not null || result.ScannerCount.HasValue))
                    return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { }
            catch { /* A printer may expose IPP on a vendor-specific URI or not support Get-Printer-Attributes. */ }
        }
        return null;
    }

    private static byte[] BuildIppGetPrinterAttributesRequest(string printerUri)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(2); // IPP/2.0 request
        stream.WriteByte(0);
        WriteIppUInt16(stream, 0x000B); // Get-Printer-Attributes
        WriteIppUInt32(stream, 1);
        stream.WriteByte(0x01); // operation-attributes-tag
        WriteIppAttribute(stream, 0x47, "attributes-charset", "utf-8");
        WriteIppAttribute(stream, 0x48, "attributes-natural-language", "en");
        WriteIppAttribute(stream, 0x45, "printer-uri", printerUri);

        var requested = new[]
        {
            "printer-name", "printer-info", "printer-make-and-model", "printer-serial-number",
            "printer-device-id", "printer-impressions-completed", "printer-media-sheets-completed",
            "printer-current-status", "printer-scan-count"
        };
        var first = true;
        foreach (var attribute in requested)
        {
            stream.WriteByte(0x44); // keyword
            if (first) WriteIppString(stream, "requested-attributes");
            else WriteIppUInt16(stream, 0); // same attribute, additional value
            WriteIppString(stream, attribute);
            first = false;
        }
        stream.WriteByte(0x03); // end-of-attributes-tag
        return stream.ToArray();
    }

    private static void WriteIppAttribute(Stream stream, byte valueTag, string name, string value)
    {
        stream.WriteByte(valueTag);
        WriteIppString(stream, name);
        WriteIppString(stream, value);
    }

    private static void WriteIppString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue) throw new InvalidOperationException("Atributo IPP demasiado largo.");
        WriteIppUInt16(stream, (ushort)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteIppUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteIppUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static IppProbeResult? ParseIppResponse(byte[] bytes)
    {
        if (bytes.Length < 8 || bytes[0] is < 1 or > 2) return null;
        var status = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2, 2));
        if (status > 0x0002) return null;

        var values = new Dictionary<string, List<(byte Tag, byte[] Value)>>(StringComparer.OrdinalIgnoreCase);
        var offset = 8;
        var currentName = string.Empty;
        while (offset < bytes.Length)
        {
            var tag = bytes[offset++];
            if (tag == 0x03) break; // end-of-attributes
            if (tag <= 0x0F)
            {
                currentName = string.Empty;
                continue; // delimiter tag / group boundary
            }
            if (offset + 2 > bytes.Length) break;
            var nameLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
            offset += 2;
            if (nameLength > bytes.Length - offset) break;
            if (nameLength > 0)
            {
                currentName = Encoding.UTF8.GetString(bytes, offset, nameLength);
                offset += nameLength;
            }
            if (string.IsNullOrEmpty(currentName) || offset + 2 > bytes.Length) break;
            var valueLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
            offset += 2;
            if (valueLength > bytes.Length - offset) break;
            var value = bytes.AsSpan(offset, valueLength).ToArray();
            offset += valueLength;
            if (!values.TryGetValue(currentName, out var list)) values[currentName] = list = new List<(byte Tag, byte[] Value)>();
            list.Add((tag, value));
        }

        string? TextValue(params string[] names)
        {
            foreach (var name in names)
            {
                if (!values.TryGetValue(name, out var list)) continue;
                foreach (var item in list)
                {
                    if (item.Tag == 0x21 || item.Tag == 0x23 || item.Tag == 0x22) continue;
                    var value = Encoding.UTF8.GetString(item.Value).Replace('\0', ' ').Trim();
                    if (value.Length > 0) return value;
                }
            }
            return null;
        }

        long? NumberValue(params string[] names)
        {
            foreach (var name in names)
            {
                if (!values.TryGetValue(name, out var list)) continue;
                foreach (var item in list)
                {
                    if (item.Value.Length == 4 && item.Tag is 0x21 or 0x23)
                    {
                        var number = BinaryPrimitives.ReadUInt32BigEndian(item.Value);
                        // Every UInt32 value fits safely inside Int64.
                        return (long)number;
                    }
                    if (item.Value.Length == 8 && item.Tag is 0x21 or 0x23)
                    {
                        var number = BinaryPrimitives.ReadUInt64BigEndian(item.Value);
                        if (number <= (ulong)long.MaxValue) return (long)number;
                    }
                    var textValue = Encoding.ASCII.GetString(item.Value).Trim();
                    if (long.TryParse(textValue, out var parsed) && parsed >= 0) return parsed;
                }
            }
            return null;
        }

        var deviceId = TextValue("printer-device-id");
        var serial = SnmpClient.NormalizeSerial(TextValue("printer-serial-number"));
        serial ??= SnmpClient.NormalizeSerial(ExtractSerialFromText(deviceId));
        var pageCount = NumberValue("printer-impressions-completed");
        var source = pageCount.HasValue ? "IPP printer-impressions-completed (TCP/631)" : null;
        if (!pageCount.HasValue)
        {
            pageCount = NumberValue("printer-media-sheets-completed");
            if (pageCount.HasValue) source = "IPP printer-media-sheets-completed (TCP/631)";
        }
        var scannerCount = NumberValue("printer-scan-count");
        var identity = TextValue("printer-make-and-model", "printer-name", "printer-info");
        if (string.IsNullOrWhiteSpace(identity)) identity = ExtractFriendlyModel(deviceId ?? string.Empty);
        return pageCount.HasValue || serial is not null || !string.IsNullOrWhiteSpace(identity) || scannerCount.HasValue
            ? new IppProbeResult(pageCount, scannerCount, serial, identity, source)
            : null;
    }

    private static long? ExtractPageCountFromText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = WebUtility.HtmlDecode(raw.Replace('\0', ' '));
        text = Regex.Replace(text, @"<script\b[^>]*>.*?</script>|<style\b[^>]*>.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        var match = Regex.Match(text,
            @"(?:total\s+(?:number\s+of\s+)?pages?|(?:number\s+of\s+)?pages?\s*(?:printed|count|total)?|total\s+impressions?|print\s+count|contador\s+(?:de\s+)?p[aá]ginas)\s*(?:[:=#]\s*|\s+)([0-9][0-9.,\s]{0,20})",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var digits = Regex.Replace(match.Groups[1].Value, @"[^0-9]", "");
        return long.TryParse(digits, out var value) && value >= 0 ? value : null;
    }

    private static string? ExtractSerialFromText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var decoded = WebUtility.HtmlDecode(raw.Replace('\0', ' '));
        // Keep embedded text/JSON so explicit serial fields in firmware scripts remain searchable.
        decoded = Regex.Replace(decoded, @"<[^>]+>", " ");
        decoded = Regex.Replace(decoded, @"\s+", " ").Trim();
        var match = Regex.Match(decoded,
            @"(?:\bserial(?:\s*(?:no\.?|number|#))?|\bs\s*/\s*n\b|\bsn\b|\bn[úu]mero\s+(?:de\s+)?serie)\s*(?:[:：=#]|\s+)\s*([A-Z0-9][A-Z0-9._/-]{3,79})",
            RegexOptions.IgnoreCase);
        return match.Success ? SnmpClient.NormalizeSerial(match.Groups[1].Value) : null;
    }

    private static async Task<HashSet<int>> ProbePrinterTcpPortsAsync(string ip, int timeoutMs, CancellationToken ct)
    {
        var result = new ConcurrentHashSet<int>();
        var timeout = Math.Clamp(timeoutMs, 300, 3000);
        var tasks = PrinterTcpPorts.Select(async port =>
        {
            if (await IsTcpOpenAsync(ip, port, timeout, ct).ConfigureAwait(false))
                result.Add(port);
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result.ToHashSet();
    }

    private static async Task<bool> IsTcpOpenAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(ip, port).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> BuildTargets(string? single, string? start, string? end)
    {
        var targets = new List<string>();

        var hasSingle = !string.IsNullOrWhiteSpace(single);
        var hasStart = !string.IsNullOrWhiteSpace(start);
        var hasEnd = !string.IsNullOrWhiteSpace(end);

        if (hasSingle)
        {
            if (!TryParseIpv4(single, out var singleIp) || singleIp is null)
                throw new InvalidOperationException("La IP individual no es válida.");
            targets.Add(singleIp.ToString());
        }

        if (hasStart != hasEnd)
            throw new InvalidOperationException("Para usar un rango, completá la IP inicial y la IP final.");

        IPAddress? startIp = null;
        IPAddress? endIp = null;

        if (hasStart && hasEnd)
        {
            if (!TryParseIpv4(start, out startIp) || !TryParseIpv4(end, out endIp) || startIp is null || endIp is null)
                throw new InvalidOperationException("El rango de IP no es válido.");

            var a = IPToUInt(startIp);
            var b = IPToUInt(endIp);
            if (a > b) (a, b) = (b, a);

            // Use 64-bit arithmetic: uint subtraction/addition overflows for 0.0.0.0-255.255.255.255
            // and could otherwise turn a huge range into an infinite loop.
            var count = (ulong)b - a + 1UL;
            if (count > MaxTargets)
                throw new InvalidOperationException($"El rango {startIp} - {endIp} contiene {count:N0} direcciones y supera el límite de {MaxTargets}. Dividilo en rangos más pequeños.");
            for (ulong n = a; n <= b; n++)
                targets.Add(NetworkRangeHelper.FromUInt((uint)n));
        }

        return targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> GetDefaultLocalSubnetTargets()
    {
        var range = DetectLocalNetworkRange();
        if (string.IsNullOrWhiteSpace(range)) return [];
        var parts = range.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? BuildTargets(null, parts[0], parts[1]) : [];
    }

    private static bool TryParseIpv4(string? value, out IPAddress? ip)
    {
        ip = null;
        return IPAddress.TryParse(value?.Trim(), out var parsed)
               && parsed.AddressFamily == AddressFamily.InterNetwork
               && (ip = parsed) is not null;
    }

    private static bool TryGetSubnet(IPAddress address, IPAddress mask, out IPAddress start, out IPAddress end)
    {
        var ip = IPToUInt(address);
        var m = IPToUInt(mask);
        var network = ip & m;
        var broadcast = network | ~m;
        start = IPAddress.Parse(NetworkRangeHelper.FromUInt(network));
        end = IPAddress.Parse(NetworkRangeHelper.FromUInt(broadcast));
        return true;
    }

    private static uint IPToUInt(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static string FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "Impresora";

    private static string? NormalizeSysDescr(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = text.Replace('\0', ' ').Trim();
        return clean.Length > 180 ? clean[..180] : clean;
    }

    private static string? ExtractModel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = text.Replace('\0', ' ').Trim();
        var paren = clean.IndexOf('(');
        if (paren > 0) clean = clean[..paren].Trim();
        return clean.Length > 120 ? clean[..120] : clean;
    }

    private static bool IsUsablePrinterName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (v.Length < 4) return false;
        var low = v.ToLowerInvariant();
        return low.Contains("printer") || low.Contains("laserjet") || low.Contains("officejet") ||
               low.Contains("deskjet") || low.Contains("pagewide") || low.Contains("mfp") ||
               low.Contains("ricoh") || low.Contains("brother") || low.Contains("epson") ||
               low.Contains("canon") || low.Contains("kyocera") || low.Contains("xerox") ||
               low.Contains("lexmark") || low.Contains("samsung") || low.Contains("pantum") ||
               low.Contains("bm5100") || low.Contains("m332") || low.Contains("sec30") ||
               low.Contains("color laser") || low.Contains("laser printer") ||
               low.Contains("hp ") || low.StartsWith("hp");
    }

    private static string? ExtractFriendlyModel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = text.Replace('\0', ' ');
        // Preserve the exact series name the customer wants when the device reports it.
        var hp = Regex.Match(clean, @"HP\s+LaserJet\s+Pro\s+(?:MFP\s*)?M428[A-Za-z]*\s*[-/]\s*M429[A-Za-z]*", RegexOptions.IgnoreCase);
        if (hp.Success) return "HP LaserJet Pro M428f-M429f";

        var generic = Regex.Match(clean, @"(?:HP|Hewlett[- ]?Packard)[^|;]*(?:LaserJet|OfficeJet|DeskJet)[^|;]*", RegexOptions.IgnoreCase);
        if (generic.Success)
        {
            var value = Regex.Replace(generic.Value, @"\s{2,}", " ").Trim();
            value = Regex.Replace(value, @"\b(?:PCL[- ]?[456]|PS|V4|V3|Class Driver)\b.*$", "", RegexOptions.IgnoreCase).Trim();
            if (value.Length > 0) return value.Length > 100 ? value[..100] : value;
        }

        return null;
    }


    private static string? GuessManufacturer(string text)
    {
        string[] brands = ["HP", "Hewlett", "Canon", "Epson", "Brother", "Ricoh", "Konica", "Kyocera", "Xerox", "Lexmark", "Samsung", "Pantum", "OKI", "Dell", "Toshiba", "Sharp"];
        foreach (var brand in brands)
        {
            if (text.Contains(brand, StringComparison.OrdinalIgnoreCase))
                return brand.Equals("Hewlett", StringComparison.OrdinalIgnoreCase) ? "HP" : brand;
        }
        return null;
    }

    private static string BuildNetworkKey(string ip, string? serial, string? model)
    {
        // Keep the network key stable across scans. Using the model/serial here
        // could create a new server row when SNMP starts returning that value.
        var raw = $"network|{ip}".Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return "net:" + Convert.ToHexString(hash)[..20];
    }

    private static class NetworkRangeHelper
    {
        public static string FromUInt(uint value)
            => $"{value >> 24}.{(value >> 16) & 255}.{(value >> 8) & 255}.{value & 255}";

        public static string ToString(string value) => value;
    }

    private sealed class ConcurrentHashSet<T>
    {
        private readonly HashSet<T> _set = [];
        private readonly object _gate = new();
        public void Add(T value) { lock (_gate) _set.Add(value); }
        public IEnumerable<T> Values { get { lock (_gate) return _set.ToArray(); } }
        public HashSet<T> ToHashSet() => Values.ToHashSet();
    }
}
