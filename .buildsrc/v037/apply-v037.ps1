param([Parameter(Mandatory=$true)][string]$AgentRoot)
$ErrorActionPreference = "Stop"
$utf8 = [System.Text.UTF8Encoding]::new($false)
$crlf = [string][char]13 + [string][char]10
$lf = [string][char]10

function Normalize([string]$Value) { return $Value.Replace($script:crlf, $script:lf) }
function Replace-Once([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $text = Normalize ([System.IO.File]::ReadAllText($Path))
    $Old = Normalize $Old
    $New = Normalize $New
    if (-not $text.Contains($Old)) { throw "Patch v0.3.7: no se encontró '$Label' en $Path" }
    $text = $text.Replace($Old, $New)
    [System.IO.File]::WriteAllText($Path, $text, $utf8)
}

$src = Join-Path $AgentRoot "src"
$snmpPath = Join-Path $src "SnmpClient.cs"
$networkPath = Join-Path $src "NetworkPrinterScanner.cs"
$mainPath = Join-Path $src "MainForm.cs"
$syncPath = Join-Path $src "SupabaseSync.cs"
$projPath = Join-Path $src "ATPrinterAgent.csproj"
$updatePath = Join-Path $src "UpdateManager.cs"
$issPath = Join-Path $AgentRoot "AandTPrinterAgent.iss"

# 1. SNMP v2c first, then v1 fallback. The version field is 1 for SNMPv2c
# and 0 for SNMPv1. Every request stays read-only and uses UDP/161.
$oldGet = @'
    public async Task<SnmpResult> GetAsync(string ip, string oid, CancellationToken ct)
    {
        try
        {
            if (!IPAddress.TryParse(ip, out var address))
                return new SnmpResult { Success = false, Error = "IP inválida" };

            var requestId = Random.Shared.Next(1, int.MaxValue);
            var packet = BuildGetRequest(requestId, oid);

            using var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.ReceiveTimeout = _timeoutMs;
            await client.SendAsync(packet, packet.Length, new IPEndPoint(address, 161));

            using var ctr = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ctr.CancelAfter(_timeoutMs + 250);
            var receiveTask = client.ReceiveAsync(ctr.Token).AsTask();
            var response = await receiveTask.ConfigureAwait(false);
            return ParseResponse(response.Buffer);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SnmpResult { Success = false, Error = "Tiempo de espera SNMP" };
        }
        catch (Exception ex)
        {
            return new SnmpResult { Success = false, Error = ex.Message };
        }
    }
'@
$newGet = @'
    public Task<SnmpResult> GetAsync(string ip, string oid, CancellationToken ct)
        => QueryAsync(ip, oid, getNext: false, ct);

    private async Task<SnmpResult> QueryAsync(string ip, string oid, bool getNext, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ip, out var address))
            return new SnmpResult { Success = false, Error = "IP inválida" };

        SnmpResult? lastResult = null;
        // Many current printers expose SNMPv2c by default; older models may only
        // answer SNMPv1. Try v2c first and fall back to v1 without changing community.
        foreach (var version in new[] { 1, 0 })
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
'@
Replace-Once $snmpPath $oldGet $newGet "SNMP GET version fallback"

$oldGetNext = @'
    public async Task<SnmpResult> GetNextAsync(string ip, string oid, CancellationToken ct)
    {
        try
        {
            if (!IPAddress.TryParse(ip, out var address))
                return new SnmpResult { Success = false, Error = "IP inválida" };

            var requestId = Random.Shared.Next(1, int.MaxValue);
            var packet = BuildGetNextRequest(requestId, oid);

            using var client = new UdpClient(AddressFamily.InterNetwork);
            client.Client.ReceiveTimeout = _timeoutMs;
            await client.SendAsync(packet, packet.Length, new IPEndPoint(address, 161));

            using var ctr = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ctr.CancelAfter(_timeoutMs + 250);
            var response = await client.ReceiveAsync(ctr.Token).AsTask().ConfigureAwait(false);
            return ParseResponse(response.Buffer);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SnmpResult { Success = false, Error = "Tiempo de espera SNMP" };
        }
        catch (Exception ex)
        {
            return new SnmpResult { Success = false, Error = ex.Message };
        }
    }

    private byte[] BuildGetNextRequest(int requestId, string oid)
    {
        var varBind = Ber.Sequence(Ber.Oid(oid), Ber.Null());
        var varBinds = Ber.Sequence(varBind);
        var pdu = Ber.Tagged(0xA1, Ber.Integer(requestId), Ber.Integer(0), Ber.Integer(0), varBinds);
        return Ber.Sequence(Ber.Integer(0), Ber.OctetString(_community), pdu);
    }
'@
$newGetNext = @'
    public Task<SnmpResult> GetNextAsync(string ip, string oid, CancellationToken ct)
        => QueryAsync(ip, oid, getNext: true, ct);

    private byte[] BuildGetNextRequest(int requestId, string oid, int version)
    {
        var varBind = Ber.Sequence(Ber.Oid(oid), Ber.Null());
        var varBinds = Ber.Sequence(varBind);
        var pdu = Ber.Tagged(0xA1, Ber.Integer(requestId), Ber.Integer(0), Ber.Integer(0), varBinds);
        return Ber.Sequence(Ber.Integer(version), Ber.OctetString(_community), pdu);
    }
'@
Replace-Once $snmpPath $oldGetNext $newGetNext "SNMP GETNEXT version fallback"

$oldBuildGet = @'
    private byte[] BuildGetRequest(int requestId, string oid)
    {
        var varBind = Ber.Sequence(Ber.Oid(oid), Ber.Null());
        var varBinds = Ber.Sequence(varBind);
        var pdu = Ber.Tagged(0xA0, Ber.Integer(requestId), Ber.Integer(0), Ber.Integer(0), varBinds);
        return Ber.Sequence(Ber.Integer(0), Ber.OctetString(_community), pdu);
    }
'@
$newBuildGet = @'
    private byte[] BuildGetRequest(int requestId, string oid, int version)
    {
        var varBind = Ber.Sequence(Ber.Oid(oid), Ber.Null());
        var varBinds = Ber.Sequence(varBind);
        var pdu = Ber.Tagged(0xA0, Ber.Integer(requestId), Ber.Integer(0), Ber.Integer(0), varBinds);
        return Ber.Sequence(Ber.Integer(version), Ber.OctetString(_community), pdu);
    }
'@
Replace-Once $snmpPath $oldBuildGet $newBuildGet "SNMP GET version field"

# 2. Preserve the scanner reading already obtained through IPP when SNMP does
# not publish a scanner meter; do not replace a real value with null.
Replace-Once $snmpPath '        printer.ScannerCount = scannerCounter;' '        if (scannerCounter.HasValue) printer.ScannerCount = scannerCounter;' "preserve IPP scanner count"

# 3. A successful post-HTTP SNMP retry must update the computed reachability
# and printer classification before the data is assembled and uploaded.
$oldWakeEnd = @'
            if (counter?.Number is null or < 0)
            {
                var retryCounter = await snmp.GetFirstTableValueAsync(ip, SnmpClient.OidsPublic.MarkerLifeCount1, ct).ConfigureAwait(false);
                if (retryCounter.Number is >= 0) counter = retryCounter;
            }
        }

        // Alternate read-only protocols are only attempted when SNMP has not supplied a meter
'@
$newWakeEnd = @'
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
'@
Replace-Once $networkPath $oldWakeEnd $newWakeEnd "post-HTTP SNMP status recalculation"

# 4. Clear a stale SNMP error as soon as any supported SNMP identity/counter
# is successfully read by EnrichAsync.
$oldReachableEnd = @'
        if (!printer.SnmpReachable)
        {
            foreach (var e in new[] { name.Error, serial.Error, counter.Error, hrDescr.Error })
                if (!string.IsNullOrWhiteSpace(e)) errors.Add(e!);
            printer.SnmpError = string.Join("; ", errors.Distinct());
        }
'@
$newReachableEnd = @'
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
'@
Replace-Once $snmpPath $oldReachableEnd $newReachableEnd "clear stale SNMP error"

# 5. Keep scanner counters and successful SNMP state while USB/IP entries merge.
Replace-Once $mainPath '            match.PageCount = network.PageCount ?? match.PageCount;' '            match.PageCount = network.PageCount ?? match.PageCount;' "page counter merge anchor"
Replace-Once $mainPath '            match.TonerBlackPercent = network.TonerBlackPercent ?? match.TonerBlackPercent;' '            match.ScannerCount = network.ScannerCount ?? match.ScannerCount;' "scanner counter merge"
Replace-Once $mainPath '            match.SnmpError = string.IsNullOrWhiteSpace(network.SnmpError) ? match.SnmpError : network.SnmpError;' '            if (network.SnmpReachable) match.SnmpError = null;' "clear merged SNMP error"
Replace-Once $mainPath '        a.PageCount = b.PageCount ?? a.PageCount;' '        a.PageCount = b.PageCount ?? a.PageCount;' "page count aggregate anchor"
Replace-Once $mainPath '        a.TonerBlackPercent = b.TonerBlackPercent ?? a.TonerBlackPercent;' '        a.ScannerCount = b.ScannerCount ?? a.ScannerCount;' "scanner count aggregate merge"
Replace-Once $mainPath '        if (b.LastStatus != null) a.LastStatus = b.LastStatus;' '        if (b.SnmpReachable) a.SnmpError = null; else if (!string.IsNullOrWhiteSpace(b.SnmpError)) a.SnmpError = b.SnmpError;' "aggregate SNMP error"
Replace-Once $mainPath '        a.SnmpReachable |= b.SnmpReachable;' '        a.SnmpReachable |= b.SnmpReachable;' "aggregate reachability anchor"

# 6. Bump to a new release, leaving the already-published v0.3.6 installer intact.
Replace-Once $projPath '<Version>0.3.6</Version>' '<Version>0.3.7</Version>' "project version"
Replace-Once $projPath '<AssemblyVersion>0.3.6.0</AssemblyVersion>' '<AssemblyVersion>0.3.7.0</AssemblyVersion>' "assembly version"
Replace-Once $projPath '<FileVersion>0.3.6.0</FileVersion>' '<FileVersion>0.3.7.0</FileVersion>' "file version"
Replace-Once $projPath '<InformationalVersion>0.3.6-remote-update-command</InformationalVersion>' '<InformationalVersion>0.3.7-snmp-reader-fix</InformationalVersion>' "informational version"
Replace-Once $syncPath 'public static string AppVersion => "0.3.6";' 'public static string AppVersion => "0.3.7";' "reported app version"
Replace-Once $syncPath 'ATPrinterAgent/0.3.6 (+A&T Informatica)' 'ATPrinterAgent/0.3.7 (+A&T Informatica)' "sync user agent"
Replace-Once $updatePath 'ATPrinterAgent-Updater/0.3.6' 'ATPrinterAgent-Updater/0.3.7' "updater user agent"
Replace-Once $mainPath 'Text = "A&T Printer Agent 0.3.6";' 'Text = "A&T Printer Agent 0.3.7";' "window version"
Replace-Once $mainPath 'Text = "v0.3.6 · Windows"' 'Text = "v0.3.7 · Windows"' "visible version"
Replace-Once $issPath '#define MyAppVersion "0.3.6"' '#define MyAppVersion "0.3.7"' "installer version"
Replace-Once $issPath 'OutputBaseFilename=Instalar_ATPrinterAgent_v0.3.6' 'OutputBaseFilename=Instalar_ATPrinterAgent_v0.3.7' "installer filename"

# Assertions: fail the build instead of publishing if any critical patch is missing.
$snmp = [System.IO.File]::ReadAllText($snmpPath)
$network = [System.IO.File]::ReadAllText($networkPath)
$main = [System.IO.File]::ReadAllText($mainPath)
if (-not $snmp.Contains('BuildGetRequest(requestId, oid, version)')) { throw "Falta SNMPv2c/v1 para GET." }
if (-not $snmp.Contains('BuildGetNextRequest(requestId, oid, version)')) { throw "Falta SNMPv2c/v1 para GETNEXT." }
if (-not $snmp.Contains('if (scannerCounter.HasValue) printer.ScannerCount = scannerCounter;')) { throw "No se preserva scanner_count." }
if (-not $network.Contains('snmpReachable = snmpPrinter || serial is { Success: true } || counter is { Success: true };')) { throw "No se recalcula SNMP después del despertar." }
if (-not $main.Contains('match.ScannerCount = network.ScannerCount ?? match.ScannerCount;')) { throw "No se fusiona scanner_count." }
if (-not $main.Contains('a.ScannerCount = b.ScannerCount ?? a.ScannerCount;')) { throw "No se agrega scanner_count." }
if (-not ([System.IO.File]::ReadAllText($projPath).Contains('<Version>0.3.7</Version>'))) { throw "La versión del proyecto no quedó en 0.3.7." }
Write-Host "Patch v0.3.7 aplicado y validado: SNMPv2c/v1, reintento tras HTTP, contador de escáner preservado y errores de SNMP limpiados."
