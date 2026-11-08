using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

namespace ATPrinterAgent;

public sealed partial class SnmpClient
{
    private readonly string _community;
    private readonly int _timeoutMs;
    private readonly string _snmpVersion;
    private readonly string _v3User;
    private readonly string _v3AuthProtocol;
    private readonly string _v3AuthPassword;
    private readonly string _v3PrivacyProtocol;
    private readonly string _v3PrivacyPassword;
    private readonly string _v3ContextName;
    private static readonly OidConstants Oids = new();

    public static class OidsPublic
    {
        public static string SysDescr => "1.3.6.1.2.1.1.1.0";
        public static string SysName => "1.3.6.1.2.1.1.5.0";
        public static string PrinterName => "1.3.6.1.2.1.43.5.1.1.16";
        public static string Serial => "1.3.6.1.2.1.43.5.1.1.17";
        public static string MarkerLifeCount1 => "1.3.6.1.2.1.43.10.2.1.4";
        public static string HrDeviceDescr => "1.3.6.1.2.1.25.3.2.1.3";
        // Vendor serial fallbacks for devices whose standard Printer-MIB is incomplete.
        public static string HpSerial => "1.3.6.1.4.1.11.2.3.9.4.2.2.5.1.1.17.0";
        public static string HpSerialAlt => "1.3.6.1.4.1.11.2.3.9.4.2.1.1.3.3";
        public static string SamsungSerial => "1.3.6.1.4.1.236.11.5.11.55.5.1.4.0";
        public static string RicohSerial => "1.3.6.1.4.1.367.3.2.1.2.1.4.0";
        public static string BrotherSerial => "1.3.6.1.4.1.2435.2.3.9.4.2.1.5.5.1";
        public static string BrotherPageCounter => "1.3.6.1.4.1.2435.2.3.9.4.2.1.5.5.10";
        public static string CanonTotalCounter => "1.3.6.1.4.1.1602.1.11.1.3.1.4.101";
        public static string PantumGlobalCounter => "1.3.6.1.4.1.40093.10.3.1.1";
        public static string PantumTonerPercent => "1.3.6.1.4.1.40093.6.3.1";
        public static string HpLifeCount => "1.3.6.1.4.1.11.2.3.9.4.2.2.10.2.1.4.0";
        public static string HpTotalPageCount => "1.3.6.1.4.1.11.2.3.9.4.2.1.4.1.2.5.0";
        public static string RicohTotalPageCount => "1.3.6.1.4.1.367.3.2.1.2.19.1.0";
        public static string CanonCounterTable => "1.3.6.1.4.1.1602.1.11.1.3.1.3.1.4";
        public static string PantumTonerLevel => "1.3.6.1.4.1.40093.1.1.3.13.2";
        // HP LaserJet Common MIB: ADF sheets and flatbed scans.
        public static string HpAdfScanCount => "1.3.6.1.4.1.11.2.3.9.4.2.1.2.2.1.20";
        public static string HpFlatbedScanCount => "1.3.6.1.4.1.11.2.3.9.4.2.1.2.2.1.21";
        // Ricoh counter table: names and values share the same row suffix.
        public static string RicohCounterNameTable => "1.3.6.1.4.1.367.3.2.1.2.19.5.1.5";
        public static string RicohCounterValueTable => "1.3.6.1.4.1.367.3.2.1.2.19.5.1.9";
    }

    public SnmpClient(string community, int timeoutMs)
        : this(new AgentConfig { SnmpCommunity = community, SnmpTimeoutMs = timeoutMs })
    {
    }

    public SnmpClient(AgentConfig config)
    {
        config ??= new AgentConfig();
        _community = string.IsNullOrWhiteSpace(config.SnmpCommunity) ? "public" : config.SnmpCommunity;
        _timeoutMs = Math.Clamp(config.SnmpTimeoutMs, 300, 5000);
        _snmpVersion = (config.SnmpVersion ?? "auto").Trim().ToLowerInvariant();
        _v3User = config.SnmpV3User ?? "";
        _v3AuthProtocol = string.IsNullOrWhiteSpace(config.SnmpV3AuthProtocol) ? "SHA256" : config.SnmpV3AuthProtocol;
        _v3AuthPassword = config.SnmpV3AuthPassword ?? "";
        _v3PrivacyProtocol = string.IsNullOrWhiteSpace(config.SnmpV3PrivacyProtocol) ? "AES128" : config.SnmpV3PrivacyProtocol;
        _v3PrivacyPassword = config.SnmpV3PrivacyPassword ?? "";
        _v3ContextName = config.SnmpV3ContextName ?? "";
    }

    public Task<SnmpResult> GetAsync(string ip, string oid, CancellationToken ct)
        => QueryAsync(ip, oid, getNext: false, ct);

    private async Task<SnmpResult> QueryAsync(string ip, string oid, bool getNext, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var address))
            return new SnmpResult { Success = false, Error = "IP inválida" };

        if (_snmpVersion == "v3")
            return await QueryV3Async(ip, oid, getNext, ct).ConfigureAwait(false);

        SnmpResult? lastResult = null;
        // Automatic preserves the historical order: v2c first, then v1.
        // An explicit mode never silently changes protocol or community.
        var versions = _snmpVersion switch
        {
            "v1" => new[] { 0 },
            "v2c" => new[] { 1 },
            _ => new[] { 1, 0 }
        };
        foreach (var version in versions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var requestId = Random.Shared.Next(1, int.MaxValue);
                var packet = getNext
                    ? BuildGetNextRequest(requestId, oid, version)
                    : BuildGetRequest(requestId, oid, version);

                using var client = new UdpClient(AddressFamily.InterNetwork);
                await client.SendAsync(packet, packet.Length, new IPEndPoint(address, 161)).ConfigureAwait(false);

                using var ctr = CancellationTokenSource.CreateLinkedTokenSource(ct);
                ctr.CancelAfter(_timeoutMs + 250);
                var response = await client.ReceiveAsync(ctr.Token).AsTask().ConfigureAwait(false);
                var parsed = ParseResponse(response.Buffer);
                if (parsed.Success) return parsed;
                lastResult = parsed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                lastResult = new SnmpResult { Success = false, Error = "Tiempo de espera SNMP" };
            }
            catch (Exception ex)
            {
                lastResult = new SnmpResult { Success = false, Error = ex.Message };
            }
        }

        return lastResult ?? new SnmpResult { Success = false, Error = "Sin respuesta SNMP" };
    }

    public async Task EnrichAsync(PrinterInfo printer, CancellationToken ct)
    {
        if (printer.ConnectionType != "ip" || string.IsNullOrWhiteSpace(printer.IpAddress)) return;

        var errors = new List<string>();
        var ip = printer.IpAddress;

        // Standard MIB-2 / Printer-MIB values. GETNEXT is important because
        // printer tables are indexed differently between manufacturers.
        var name = await GetFirstTableValueAsync(ip, OidsPublic.PrinterName, ct);
        if (string.IsNullOrWhiteSpace(name.Text))
            name = await GetAsync(ip, "1.3.6.1.2.1.43.5.1.1.16.1.1", ct);
        if (name.Success && !string.IsNullOrWhiteSpace(name.Text))
        {
            printer.SnmpName = name.Text;
            printer.IdentitySource = "SNMP";
        }

        var serial = await GetFirstTableValueAsync(ip, OidsPublic.Serial, ct);
        var serialValue = NormalizeSerial(serial.Text);
        if (serialValue is null)
        {
            serial = await GetAsync(ip, "1.3.6.1.2.1.43.5.1.1.17.1.1", ct);
            serialValue = NormalizeSerial(serial.Text);
        }

        // Probe the likely manufacturer first, then other known vendor MIBs.
        // Some models answer only their private OID, not Printer-MIB::prtGeneralSerialNumber.
        var vendorText = $"{printer.Manufacturer} {printer.Model} {printer.DisplayName}".ToLowerInvariant();
        // Pantum serial OIDs vary by model and the BM5100FDW MIB data available
        // publicly does not document a serial OID. Its HTTP page is parsed by
        // NetworkPrinterScanner instead of guessing an OID that could return the wrong field.
        if (serialValue is null && (vendorText.Contains("samsung") || vendorText.Contains("sec30")))
        {
            serial = await GetAsync(ip, OidsPublic.SamsungSerial, ct);
            serialValue = NormalizeSerial(serial.Text);
        }
        if (serialValue is null && (vendorText.Contains("ricoh") || vendorText.Contains("mp c306")))
        {
            serial = await GetAsync(ip, OidsPublic.RicohSerial, ct);
            serialValue = NormalizeSerial(serial.Text);
        }
        if (serialValue is null && vendorText.Contains("brother"))
        {
            serial = await GetAsync(ip, OidsPublic.BrotherSerial, ct).ConfigureAwait(false);
            serialValue = NormalizeSerial(serial.Text);
        }
        if (serialValue is null && (vendorText.Contains("hp") || vendorText.Contains("hewlett")))
        {
            serial = await GetAsync(ip, OidsPublic.HpSerial, ct);
            serialValue = NormalizeSerial(serial.Text);
            if (serialValue is null)
            {
                serial = await GetAsync(ip, OidsPublic.HpSerialAlt, ct);
                serialValue = NormalizeSerial(serial.Text);
            }
        }
        // Generic fallbacks cover OEM devices whose manufacturer string is incomplete.
        if (serialValue is null)
        {
            serial = await GetAsync(ip, OidsPublic.HpSerial, ct);
            serialValue = NormalizeSerial(serial.Text);
        }
        if (serialValue is null)
        {
            serial = await GetAsync(ip, OidsPublic.SamsungSerial, ct);
            serialValue = NormalizeSerial(serial.Text);
        }
        if (serialValue is null)
        {
            serial = await GetAsync(ip, OidsPublic.RicohSerial, ct);
            serialValue = NormalizeSerial(serial.Text);
        }
        var serialFound = serialValue is not null;
        if (serialValue is not null)
        {
            printer.SerialNumber = serialValue;
            printer.SerialSource = "SNMP Printer-MIB/vendor MIB";
        }

        // Scanner counters are vendor-specific. We only save a numeric value
        // when the device exposes a known read-only meter; unsupported models
        // keep this field empty rather than inventing a value.
        long? scannerCounter = null;
        if (vendorText.Contains("hp") || vendorText.Contains("hewlett"))
        {
            var adf = await GetIndexedCounterAsync(ip, OidsPublic.HpAdfScanCount, ct).ConfigureAwait(false);
            var flatbed = await GetIndexedCounterAsync(ip, OidsPublic.HpFlatbedScanCount, ct).ConfigureAwait(false);
            if (adf.HasValue || flatbed.HasValue)
                scannerCounter = (adf ?? 0L) + (flatbed ?? 0L);
        }
        else if (vendorText.Contains("ricoh") || vendorText.Contains("mp c306"))
        {
            scannerCounter = await GetRicohScannerCounterAsync(ip, ct).ConfigureAwait(false);
        }
        if (scannerCounter.HasValue) printer.ScannerCount = scannerCounter;

        var counter = await GetFirstTableValueAsync(ip, OidsPublic.MarkerLifeCount1, ct);
        if (counter.Success && counter.Number is >= 0)
        {
            printer.PageCount = counter.Number;
            printer.CounterSource = "SNMP Printer-MIB";
        }
        else
        {
            counter = await GetAsync(ip, "1.3.6.1.2.1.43.10.2.1.4.1.1", ct);
            if (counter.Success && counter.Number is >= 0)
            {
                printer.PageCount = counter.Number;
                printer.CounterSource = "SNMP Printer-MIB";
            }
            else
            {
                counter = await GetAsync(ip, OidsPublic.HpLifeCount, ct);
                if (counter.Success && counter.Number is >= 0)
                {
                    printer.PageCount = counter.Number;
                    printer.CounterSource = "SNMP HP Life Count";
                }
            }
        }

        // Walk the complete Printer-MIB v2 consumable columns instead of assuming
        // every printer exposes exactly eight fixed toner indexes. GETNEXT remains
        // read-only and the OID prefix is checked on every row.
        var supplyDescriptions = await WalkColumnAsync(ip, "1.3.6.1.2.1.43.11.1.1.6", 24, ct).ConfigureAwait(false);
        foreach (var row in supplyDescriptions)
        {
            if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(row.Oid)) break;
            var suffix = row.Oid[("1.3.6.1.2.1.43.11.1.1.6".Length)..];
            var level = await GetAsync(ip, "1.3.6.1.2.1.43.11.1.1.9" + suffix, ct).ConfigureAwait(false);
            var max = await GetAsync(ip, "1.3.6.1.2.1.43.11.1.1.8" + suffix, ct).ConfigureAwait(false);
            var pct = CalculateSupplyPercent(level.Number, max.Number);
            if (pct is null) continue; // RFC 3805 -1/-2/-3 are not toner percentages.
            ApplyTonerReading(printer, row.Text, pct.Value);
        }

        // Pantum's registered enterprise branch exposes a black toner remainder
        // value on documented firmware. Accept it only when it is already a 0-100
        // percentage; never turn a status code or unknown value into a gauge.
        if (vendorText.Contains("pantum"))
        {
            var pantum = await GetAsync(ip, OidsPublic.PantumTonerLevel, ct).ConfigureAwait(false);
            if (!pantum.Success || pantum.Number is < 0 or > 100)
                pantum = await GetAsync(ip, OidsPublic.PantumTonerLevel + ".0", ct).ConfigureAwait(false);
            if (!pantum.Success || pantum.Number is < 0 or > 100)
                pantum = await GetAsync(ip, OidsPublic.PantumTonerPercent, ct).ConfigureAwait(false);
            if (!pantum.Success || pantum.Number is < 0 or > 100)
                pantum = await GetAsync(ip, OidsPublic.PantumTonerPercent + ".0", ct).ConfigureAwait(false);
            if (pantum.Number is >= 0 and <= 100)
                printer.TonerBlackPercent = pantum.Number.Value;
        }

        // Samsung Printer Extension MIB uses a separate table, indexed by
        // hrDeviceIndex + toner index. Its toner level is reported directly.
        if (vendorText.Contains("samsung") || vendorText.Contains("sec30"))
        {
            var samsungRows = await WalkColumnAsync(ip, "1.3.6.1.4.1.236.11.5.11.55.17.2.1.5", 16, ct).ConfigureAwait(false);
            foreach (var row in samsungRows)
            {
                if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(row.Oid)) break;
                var suffix = row.Oid[("1.3.6.1.4.1.236.11.5.11.55.17.2.1.5".Length)..];
                var toner = await GetAsync(ip, "1.3.6.1.4.1.236.11.5.11.55.17.2.1.6" + suffix, ct).ConfigureAwait(false);
                if (toner.Number is >= 0 and <= 100)
                    ApplyTonerReading(printer, row.Text, toner.Number.Value);
            }
        }

        // hrDeviceDescr often contains a much better model than sysName.
        var hrDescr = await GetFirstTableValueAsync(ip, OidsPublic.HrDeviceDescr, ct);
        if (hrDescr.Success && !string.IsNullOrWhiteSpace(hrDescr.Text))
        {
            var text = CleanValue(hrDescr.Text);
            if (!LooksLikeHostName(text)) printer.Model = text;
        }

        // Compatibility fallback for firmware that exposes only the first indexed
        // row by GET. Dynamic table walks above have priority and support >8 supplies.
        var legacySupplyFound = false;
        if (supplyDescriptions.Count == 0)
        {
            for (var i = 1; i <= 8; i++)
            {
                var d = await GetAsync(ip, Oids.SupplyDescription(i), ct).ConfigureAwait(false);
                if (!d.Success || string.IsNullOrWhiteSpace(d.Text)) continue;
                var level = await GetAsync(ip, Oids.SupplyLevel(i), ct).ConfigureAwait(false);
                var max = await GetAsync(ip, Oids.SupplyMax(i), ct).ConfigureAwait(false);
                var pct = CalculateSupplyPercent(level.Number, max.Number);
                if (pct is null) continue;
                ApplyTonerReading(printer, d.Text, pct.Value);
                legacySupplyFound = true;
            }
        }

        // Try documented manufacturer total meters only if Printer-MIB did not
        // produce a valid cumulative counter. Never replace a valid earlier value.
        if (printer.PageCount is null or < 0)
        {
            var vendorCounter = await GetVendorPageCounterAsync(ip, vendorText, ct).ConfigureAwait(false);
            if (vendorCounter is >= 0)
            {
                printer.PageCount = vendorCounter;
                printer.CounterSource = "SNMP vendor MIB";
                counter = new SnmpResult { Success = true, Number = vendorCounter };
            }
        }

        printer.SnmpReachable = name.Success || serial.Success || serialFound || counter.Success || hrDescr.Success
            || supplyDescriptions.Count > 0 || legacySupplyFound || printer.PageCount.HasValue
            || printer.ScannerCount.HasValue || printer.TonerBlackPercent.HasValue
            || printer.TonerCyanPercent.HasValue || printer.TonerMagentaPercent.HasValue
            || printer.TonerYellowPercent.HasValue;
        if (!printer.SnmpReachable)
        {
            foreach (var e in new[] { name.Error, serial.Error, counter.Error, hrDescr.Error })
                if (!string.IsNullOrWhiteSpace(e)) errors.Add(e!);
            printer.SnmpError = string.Join("; ", errors.Distinct());
        }
        else
        {
            printer.SnmpError = null;
        }
    }

    private async Task<List<SnmpResult>> WalkColumnAsync(string ip, string rootOid, int maxRows, CancellationToken ct)
    {
        var rows = new List<SnmpResult>();
        var root = rootOid.Trim().TrimEnd('.');
        var cursor = root;
        for (var i = 0; i < Math.Clamp(maxRows, 1, 64) && !ct.IsCancellationRequested; i++)
        {
            var next = await GetNextAsync(ip, cursor, ct).ConfigureAwait(false);
            if (!next.Success || string.IsNullOrWhiteSpace(next.Oid)) break;
            if (!IsOidInSubtree(next.Oid, root) || string.Equals(next.Oid, cursor, StringComparison.Ordinal)) break;
            rows.Add(next);
            cursor = next.Oid;
        }
        return rows;
    }

    internal static bool IsOidInSubtree(string? oid, string? root)
    {
        if (string.IsNullOrWhiteSpace(oid) || string.IsNullOrWhiteSpace(root)) return false;
        var prefix = root.Trim().TrimEnd('.');
        return oid.Equals(prefix, StringComparison.Ordinal) || oid.StartsWith(prefix + ".", StringComparison.Ordinal);
    }

    internal static double? CalculateSupplyPercent(long? level, long? maximum)
    {
        // Printer-MIB uses -1 (other), -2 (unknown), and -3 (some remaining).
        // Those are status values, not negative toner percentages.
        if (level is null or < 0 || maximum is null or <= 0) return null;
        return Math.Clamp(level.Value * 100.0 / maximum.Value, 0, 100);
    }

    internal static string? DetectTonerRole(string? description)
    {
        var text = (description ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Contains("black") || text.Contains("negro") || text.Contains("black toner") || text == "k" || text == "bk" || text.Contains("black (k)")) return "black";
        if (text.Contains("cyan") || text.Contains("cian") || text == "c" || text.Contains("cyan (c)")) return "cyan";
        if (text.Contains("magenta") || text == "m" || text.Contains("magenta (m)")) return "magenta";
        if (text.Contains("yellow") || text.Contains("amarillo") || text == "y" || text.Contains("yellow (y)")) return "yellow";
        return null;
    }

    private static void ApplyTonerReading(PrinterInfo printer, string? description, double percent)
    {
        var role = DetectTonerRole(description);
        if (role == "black") printer.TonerBlackPercent = percent;
        else if (role == "cyan") printer.TonerCyanPercent = percent;
        else if (role == "magenta") printer.TonerMagentaPercent = percent;
        else if (role == "yellow") printer.TonerYellowPercent = percent;
    }

    private async Task<long?> GetVendorPageCounterAsync(string ip, string vendorText, CancellationToken ct)
    {
        if (vendorText.Contains("hp") || vendorText.Contains("hewlett"))
        {
            foreach (var oid in new[] { OidsPublic.HpTotalPageCount, OidsPublic.HpLifeCount })
            {
                var value = await GetAsync(ip, oid, ct).ConfigureAwait(false);
                var count = ReadCounterNumber(value);
                if (count is >= 0) return count;
            }
        }
        if (vendorText.Contains("ricoh") || vendorText.Contains("mp c306"))
        {
            var value = await GetAsync(ip, OidsPublic.RicohTotalPageCount, ct).ConfigureAwait(false);
            var count = ReadCounterNumber(value);
            if (count is >= 0) return count;
        }
        if (vendorText.Contains("brother"))
        {
            var value = await GetAsync(ip, OidsPublic.BrotherPageCounter, ct).ConfigureAwait(false);
            var count = ReadCounterNumber(value);
            if (count is >= 0) return count;
        }
        if (vendorText.Contains("canon"))
        {
            // Canon's counter table identifies counters by a type suffix. Type 101 is
            // the cumulative total; don't sum colour, mono, or sub-function counters.
            var rows = await WalkColumnAsync(ip, OidsPublic.CanonCounterTable, 24, ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (row.Number is not >= 0 || string.IsNullOrWhiteSpace(row.Oid)) continue;
                var type = row.Oid.Split('.').LastOrDefault();
                if (type == "101") return row.Number;
            }
            // Some firmware exposes the total counter as a direct scalar.
            var direct = await GetAsync(ip, OidsPublic.CanonTotalCounter, ct).ConfigureAwait(false);
            var directCount = ReadCounterNumber(direct);
            if (directCount is >= 0) return directCount;
        }
        if (vendorText.Contains("pantum"))
        {
            var value = await GetAsync(ip, OidsPublic.PantumGlobalCounter, ct).ConfigureAwait(false);
            var count = ReadCounterNumber(value);
            if (count is >= 0) return count;
            value = await GetAsync(ip, OidsPublic.PantumGlobalCounter + ".0", ct).ConfigureAwait(false);
            count = ReadCounterNumber(value);
            if (count is >= 0) return count;
        }
        return null;
    }

    private static long? ReadCounterNumber(SnmpResult? value)
    {
        if (value is null || !value.Success) return null;
        if (value.Number is >= 0) return value.Number;
        var text = (value.Text ?? string.Empty).Trim();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed : null;
    }

    private async Task<long?> GetIndexedCounterAsync(string ip, string baseOid, CancellationToken ct)
    {
        foreach (var suffix in new[] { ".0", ".1" })
        {
            var value = await GetAsync(ip, baseOid + suffix, ct).ConfigureAwait(false);
            if (value.Success && value.Number is >= 0) return value.Number;
        }
        return null;
    }

    private async Task<long?> GetRicohScannerCounterAsync(string ip, CancellationToken ct)
    {
        var namesRoot = OidsPublic.RicohCounterNameTable;
        var valuesRoot = OidsPublic.RicohCounterValueTable;
        var cursor = namesRoot;
        long total = 0;
        var matched = 0;

        // Walk only this table's name column. Ricoh firmware uses row indexes
        // for function counters, so use each name's exact suffix to read its value.
        for (var i = 0; i < 48 && !ct.IsCancellationRequested; i++)
        {
            var item = await GetNextAsync(ip, cursor, ct).ConfigureAwait(false);
            if (!item.Success || string.IsNullOrWhiteSpace(item.Oid)) break;
            if (!item.Oid.StartsWith(namesRoot + ".", StringComparison.Ordinal)) break;
            if (string.Equals(item.Oid, cursor, StringComparison.Ordinal)) break;
            cursor = item.Oid;

            var counterName = (item.Text ?? string.Empty).Trim().ToLowerInvariant();
            if (!counterName.Contains("scan") && !counterName.Contains("scanner")) continue;

            var suffix = item.Oid[(namesRoot.Length + 1)..];
            var value = await GetAsync(ip, valuesRoot + "." + suffix, ct).ConfigureAwait(false);
            if (value.Success && value.Number is >= 0)
            {
                total += value.Number.Value;
                matched++;
            }
        }

        return matched > 0 ? total : null;
    }

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

    public Task<SnmpResult> GetNextAsync(string ip, string oid, CancellationToken ct)
        => QueryAsync(ip, oid, getNext: true, ct);

    private byte[] BuildGetNextRequest(int requestId, string oid, int version)
    {
        var varBind = Ber.Sequence(Ber.Oid(oid), Ber.Null());
        var varBinds = Ber.Sequence(varBind);
        var pdu = Ber.Tagged(0xA1, Ber.Integer(requestId), Ber.Integer(0), Ber.Integer(0), varBinds);
        return Ber.Sequence(Ber.Integer(version), Ber.OctetString(_community), pdu);
    }

    private static string CleanValue(string? text)
        => (text ?? string.Empty).Trim();

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

    private static bool LooksLikeHostName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var clean = text.Trim();
        return clean.Length >= 5 && clean.Length <= 24
               && clean.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
               && !clean.Contains(' ');
    }


    private byte[] BuildGetRequest(int requestId, string oid, int version)
    {
        var varBind = Ber.Sequence(Ber.Oid(oid), Ber.Null());
        var varBinds = Ber.Sequence(varBind);
        var pdu = Ber.Tagged(0xA0, Ber.Integer(requestId), Ber.Integer(0), Ber.Integer(0), varBinds);
        return Ber.Sequence(Ber.Integer(version), Ber.OctetString(_community), pdu);
    }

    private static SnmpResult ParseResponse(byte[] data)
    {
        var reader = new BerReader(data);
        try
        {
            var outer = reader.ReadSequence();
            _ = outer.ReadInteger();
            _ = outer.ReadOctetString();
            var pdu = outer.ReadTlv();
            if (pdu.Tag != 0xA2) return new SnmpResult { Success = false, Error = "Respuesta SNMP inesperada" };

            var pduReader = new BerReader(pdu.Value);
            var requestId = pduReader.ReadInteger();
            var errorStatus = pduReader.ReadInteger();
            _ = pduReader.ReadInteger();
            if (errorStatus != 0) return new SnmpResult { Success = false, Error = $"SNMP error-status {errorStatus}" };

            var vars = pduReader.ReadSequence();
            var vb = vars.ReadSequence();
            var responseOid = vb.ReadOid();
            var value = vb.ReadTlv();
            return value.Tag switch
            {
                0x04 => new SnmpResult { Success = true, Oid = responseOid, Text = DecodeText(value.Value) },
                0x02 or 0x41 or 0x42 or 0x43 or 0x46 => new SnmpResult { Success = true, Oid = responseOid, Number = DecodeIntegerLike(value.Value) },
                0x05 => new SnmpResult { Success = false, Oid = responseOid, Error = "SNMP NULL" },
                0x80 => new SnmpResult { Success = false, Oid = responseOid, Error = "SNMP NoSuchObject" },
                0x81 => new SnmpResult { Success = false, Oid = responseOid, Error = "SNMP NoSuchInstance" },
                0x82 => new SnmpResult { Success = false, Oid = responseOid, Error = "SNMP EndOfMibView" },
                _ => new SnmpResult { Success = true, Oid = responseOid, Text = Convert.ToHexString(value.Value) }
            };
        }
        catch (Exception ex)
        {
            return new SnmpResult { Success = false, Error = ex.Message };
        }
    }

    private static string DecodeText(byte[] value)
    {
        var text = Encoding.UTF8.GetString(value).Trim('\0', ' ', '\r', '\n');
        if (text.Any(c => c == '\ufffd')) text = Encoding.ASCII.GetString(value).Trim('\0', ' ', '\r', '\n');
        return text;
    }

    private static long DecodeIntegerLike(byte[] bytes)
    {
        if (bytes.Length == 0) return 0;
        ulong value = 0;
        foreach (var b in bytes) value = (value << 8) | b;
        if ((bytes[0] & 0x80) != 0 && bytes.Length < 8)
        {
            var bits = bytes.Length * 8;
            var signed = unchecked((long)(value | (~0UL << bits)));
            return signed;
        }
        return unchecked((long)value);
    }

    private sealed class OidConstants
    {
        public string SysName => OidsPublic.SysName;
        public string SysDescr => OidsPublic.SysDescr;
        public string PrinterName => OidsPublic.PrinterName;
        public string Serial => OidsPublic.Serial;
        public string MarkerLifeCount1 => OidsPublic.MarkerLifeCount1;
        public string SupplyDescription(int index) => $"1.3.6.1.2.1.43.11.1.1.6.1.{index}";
        public string SupplyMax(int index) => $"1.3.6.1.2.1.43.11.1.1.8.1.{index}";
        public string SupplyLevel(int index) => $"1.3.6.1.2.1.43.11.1.1.9.1.{index}";
    }

    private static class Ber
    {
        public static byte[] Integer(int value) => Tlv(0x02, EncodeInteger(value));
        public static byte[] OctetString(string value) => Tlv(0x04, Encoding.ASCII.GetBytes(value));
        public static byte[] Null() => Tlv(0x05, Array.Empty<byte>());
        public static byte[] Oid(string oid) => Tlv(0x06, EncodeOid(oid));
        public static byte[] Sequence(params byte[][] parts) => Tlv(0x30, parts.SelectMany(x => x).ToArray());
        public static byte[] Tagged(byte tag, params byte[][] parts) => Tlv(tag, parts.SelectMany(x => x).ToArray());

        private static byte[] EncodeInteger(int value)
        {
            var bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            var start = 0;
            while (start < bytes.Length - 1 && ((bytes[start] == 0 && (bytes[start + 1] & 0x80) == 0) || (bytes[start] == 0xFF && (bytes[start + 1] & 0x80) != 0))) start++;
            return bytes[start..];
        }

        private static byte[] EncodeOid(string oid)
        {
            var parts = oid.Split('.').Select(int.Parse).ToArray();
            if (parts.Length < 2) throw new ArgumentException("OID inválido");
            var result = new List<byte> { (byte)(parts[0] * 40 + parts[1]) };
            foreach (var n in parts.Skip(2))
            {
                var value = n;
                var stack = new Stack<byte>();
                stack.Push((byte)(value & 0x7F));
                value >>= 7;
                while (value > 0) { stack.Push((byte)((value & 0x7F) | 0x80)); value >>= 7; }
                result.AddRange(stack);
            }
            return result.ToArray();
        }

        private static byte[] Tlv(byte tag, byte[] value) => new[] { tag }.Concat(EncodeLength(value.Length)).Concat(value).ToArray();
        private static byte[] EncodeLength(int length)
        {
            if (length < 128) return new[] { (byte)length };
            var bytes = new List<byte>();
            var n = length;
            while (n > 0) { bytes.Insert(0, (byte)(n & 0xFF)); n >>= 8; }
            return new[] { (byte)(0x80 | bytes.Count) }.Concat(bytes).ToArray();
        }
    }

    private sealed class BerReader
    {
        private readonly byte[] _data;
        private int _offset;
        public BerReader(byte[] data) => _data = data;

        public BerReader ReadSequence()
        {
            var tlv = ReadTlv();
            if (tlv.Tag != 0x30) throw new InvalidOperationException("SEQUENCE esperado");
            return new BerReader(tlv.Value);
        }

        public Tlv ReadTlv()
        {
            if (_offset >= _data.Length) throw new InvalidOperationException("BER fuera de rango");
            var tag = _data[_offset++];
            var lenByte = _data[_offset++];
            var length = lenByte < 128 ? lenByte : ReadLength(lenByte & 0x7F);
            if (length < 0 || _offset + length > _data.Length) throw new InvalidOperationException("Longitud BER inválida");
            var value = _data[_offset..(_offset + length)];
            _offset += length;
            return new Tlv(tag, value);
        }

        public int ReadInteger() => checked((int)DecodeIntegerLike(ReadTlv().Value));
        public string ReadOctetString() => DecodeText(ReadTlv().Value);
        public string ReadOid()
        {
            var tlv = ReadTlv();
            if (tlv.Tag != 0x06) throw new InvalidOperationException("OID esperado");
            return string.Join('.', DecodeOid(tlv.Value));
        }

        private int ReadLength(int byteCount)
        {
            if (byteCount == 0) throw new InvalidOperationException("Longitud BER indefinida no soportada");
            if (_offset + byteCount > _data.Length) throw new InvalidOperationException("BER truncado");
            var len = 0;
            for (var i = 0; i < byteCount; i++) len = (len << 8) | _data[_offset++];
            return len;
        }

        private static IEnumerable<int> DecodeOid(byte[] bytes)
        {
            if (bytes.Length == 0) yield break;
            var first = bytes[0];
            yield return Math.Min(first / 40, 2);
            yield return first >= 80 ? first - 80 : first % 40;
            var value = 0;
            foreach (var b in bytes.Skip(1))
            {
                value = (value << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) { yield return value; value = 0; }
            }
        }
    }

    private readonly record struct Tlv(byte Tag, byte[] Value);
}
