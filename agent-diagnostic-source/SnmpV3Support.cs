using System.Globalization;
using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

namespace ATPrinterAgent;

public sealed partial class SnmpClient
{
    private readonly SemaphoreSlim _v3DiscoveryGate = new(1, 1);
    private ReportMessage? _v3CachedReport;
    private string? _v3CachedIp;
    private DateTimeOffset _v3CachedAt;

    private async Task<SnmpResult> QueryV3Async(string ip, string oid, bool getNext, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_v3User))
            return new SnmpResult { Success = false, Error = "SNMPv3 requiere un usuario configurado." };
        if (!IPAddress.TryParse(ip, out var address))
            return new SnmpResult { Success = false, Error = "IP inválida" };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeoutMs + 700);
        var token = timeout.Token;
        try
        {
            var endpoint = new IPEndPoint(address, 161);
            var report = await GetV3DiscoveryReportAsync(ip, endpoint, token).ConfigureAwait(false);
            var privacy = CreateV3PrivacyProvider();
            var variables = new List<Variable> { new(new ObjectIdentifier(oid)) };
            var username = new OctetString(_v3User);
            var context = new OctetString(_v3ContextName ?? string.Empty);
            ISnmpMessage request = getNext
                ? new GetNextRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId,
                    username, context, variables, privacy, Messenger.MaxMessageSize, report)
                : new GetRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId,
                    username, context, variables, privacy, Messenger.MaxMessageSize, report);

            var registry = new UserRegistry().Add(username, privacy);
            var response = await request.GetResponseAsync(endpoint, registry, token).ConfigureAwait(false);
            var variable = response.Pdu().Variables.FirstOrDefault();
            if (variable is null)
                return new SnmpResult { Success = false, Error = "SNMPv3 respondió sin variables." };

            var text = variable.Data.ToString()?.Trim() ?? string.Empty;
            if (text.Contains("NoSuchObject", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("NoSuchInstance", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("EndOfMibView", StringComparison.OrdinalIgnoreCase))
                return new SnmpResult { Success = false, Oid = variable.Id.ToString(), Error = text };

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                return new SnmpResult { Success = true, Oid = variable.Id.ToString(), Number = number, Text = text };

            return new SnmpResult { Success = true, Oid = variable.Id.ToString(), Text = text };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new SnmpResult { Success = false, Error = "Tiempo de espera SNMPv3" };
        }
        catch (Exception ex)
        {
            return new SnmpResult { Success = false, Error = "SNMPv3: " + ex.Message };
        }
    }

    private async Task<ReportMessage> GetV3DiscoveryReportAsync(string ip, IPEndPoint endpoint, CancellationToken ct)
    {
        await _v3DiscoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_v3CachedReport is not null &&
                string.Equals(_v3CachedIp, ip, StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.UtcNow - _v3CachedAt < TimeSpan.FromSeconds(45))
                return _v3CachedReport;

            var discovery = new Discovery(Messenger.NextMessageId, Messenger.NextRequestId, Messenger.MaxMessageSize);
            var report = await discovery.GetResponseAsync(endpoint, ct).ConfigureAwait(false);
            _v3CachedIp = ip;
            _v3CachedAt = DateTimeOffset.UtcNow;
            _v3CachedReport = report;
            return report;
        }
        finally
        {
            _v3DiscoveryGate.Release();
        }
    }

    internal IPrivacyProvider CreateV3PrivacyProvider()
    {
        var authName = (_v3AuthProtocol ?? "SHA256").Trim().ToUpperInvariant();
        var privacyName = (_v3PrivacyProtocol ?? "AES128").Trim().ToUpperInvariant();
        if (authName == "NONE")
        {
            if (privacyName != "NONE")
                throw new InvalidOperationException("SNMPv3 no permite cifrado sin autenticación (authPriv requiere auth). Seleccioná autenticación o privacidad None.");
            return DefaultPrivacyProvider.DefaultPair;
        }

        if (string.IsNullOrWhiteSpace(_v3AuthPassword) || _v3AuthPassword.Length < 8)
            throw new InvalidOperationException("La contraseña SNMPv3 de autenticación debe tener al menos 8 caracteres.");

        var phrase = new OctetString(_v3AuthPassword);
        IAuthenticationProvider auth = authName switch
        {
            "SHA256" => new SHA256AuthenticationProvider(phrase),
            "SHA384" => new SHA384AuthenticationProvider(phrase),
            "SHA512" => new SHA512AuthenticationProvider(phrase),
            "SHA1" => new SHA1AuthenticationProvider(phrase),
            "MD5" => new MD5AuthenticationProvider(phrase),
            _ => throw new InvalidOperationException("Protocolo de autenticación SNMPv3 no reconocido: " + authName)
        };

        if (privacyName == "NONE") return new DefaultPrivacyProvider(auth);
        if (string.IsNullOrWhiteSpace(_v3PrivacyPassword) || _v3PrivacyPassword.Length < 8)
            throw new InvalidOperationException("La contraseña SNMPv3 de privacidad debe tener al menos 8 caracteres.");

        var privacyPhrase = new OctetString(_v3PrivacyPassword);
        return privacyName switch
        {
            "AES128" => new AESPrivacyProvider(privacyPhrase, auth),
            "AES192" => new AES192PrivacyProvider(privacyPhrase, auth),
            "AES256" => new AES256PrivacyProvider(privacyPhrase, auth),
            "DES" => new DESPrivacyProvider(privacyPhrase, auth),
            _ => throw new InvalidOperationException("Protocolo de privacidad SNMPv3 no reconocido: " + privacyName)
        };
    }
}
