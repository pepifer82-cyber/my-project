using System.Management;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ATPrinterAgent;

public static class PrinterScanner
{
    private static readonly Regex Ipv4Regex = new(@"(?<!\d)(\d{1,3}(?:\.\d{1,3}){3})(?!\d)", RegexOptions.Compiled);

    public static List<PrinterInfo> ScanWindowsPrinters()
    {
        var list = new List<PrinterInfo>();
        using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Printer");
        using var printers = searcher.Get();

        foreach (ManagementObject p in printers)
        {
            var name = ReadString(p, "Name") ?? "Impresora";
            var driver = ReadString(p, "DriverName");
            var port = ReadString(p, "PortName");
            var status = ReadString(p, "Status");
            var printerStatus = ReadUInt16(p, "PrinterStatus");
            var workOffline = ReadBool(p, "WorkOffline");

            if (IsVirtualPrinter(name, driver, port))
                continue;

            var connection = DetectConnection(name, port);
            var ip = ExtractIp(port);
            var cleanDriver = CleanDriverModel(driver);
            var displayName = IsComputerLikePrinterName(name) && connection == "ip"
                ? (cleanDriver ?? name)
                : name;
            var key = BuildPrinterKey(name, port, driver);

            list.Add(new PrinterInfo
            {
                PrinterKey = key,
                DisplayName = displayName,
                Model = cleanDriver,
                Manufacturer = GuessManufacturer(name + " " + driver),
                ConnectionType = connection,
                IpAddress = ip,
                WindowsPort = port,
                UsbPort = connection == "usb" ? port : null,
                WindowsPrinterName = name,
                HostName = connection == "ip" && IsComputerLikePrinterName(name) ? name : null,
                LastStatus = MapStatus(workOffline, status, printerStatus),
                IdentitySource = "Windows WMI / cola"
            });
        }

        return list
            .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }


    private static bool IsVirtualPrinter(string name, string? driver, string? port)
    {
        var text = $"{name} {driver} {port}".ToLowerInvariant();
        string[] virtualMarkers =
        [
            "microsoft print to pdf", "microsoft xps document writer", "fax",
            "onenote", "send to kindle", "adobe pdf", "cute pdf", "nitro pdf",
            "bullzip pdf", "pdfcreator", "do pdf", "xps document", "portprompt"
        ];
        return virtualMarkers.Any(text.Contains);
    }

    private static string DetectConnection(string name, string? port)
    {
        if (!string.IsNullOrWhiteSpace(port))
        {
            if (port.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return "usb";
            if (ExtractIp(port) is not null) return "ip";
            if (port.StartsWith("WSD", StringComparison.OrdinalIgnoreCase)) return "ip";
        }

        if (name.Contains("\\\\", StringComparison.OrdinalIgnoreCase)) return "ip";
        return "unknown";
    }

    private static string? ExtractIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Ipv4Regex.Match(value);
        if (!match.Success) return null;
        return IPAddress.TryParse(match.Groups[1].Value, out _) ? match.Groups[1].Value : null;
    }

    private static string BuildPrinterKey(string name, string? port, string? driver)
    {
        var raw = $"{Environment.MachineName}|{name}|{port}|{driver}".Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return "win:" + Convert.ToHexString(hash)[..16];
    }

    private static bool IsComputerLikePrinterName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var clean = name.Trim();
        // Typical WSD host-style labels look like HPE066E4, PRINTER01, etc.
        return clean.Length >= 5 && clean.Length <= 24
               && Regex.IsMatch(clean, @"^[A-Z0-9_-]+$", RegexOptions.IgnoreCase)
               && !clean.Contains(' ');
    }

    private static string? CleanDriverModel(string? driver)
    {
        if (string.IsNullOrWhiteSpace(driver)) return null;
        var value = driver.Trim();
        value = Regex.Replace(value, @"\s*\((?:V|Version)\s*\d+\)\s*$", "", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\s+(?:PCL[- ]?5|PCL[- ]?6|PS|XPS|Class Driver|Universal Print Class Driver)\b.*$", "", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\s{2,}", " ").Trim();
        return value.Length == 0 ? null : value;
    }

    private static string? GuessManufacturer(string text)
    {
        string[] brands = ["HP", "Hewlett", "Canon", "Epson", "Brother", "Ricoh", "Konica", "Kyocera", "Xerox", "Lexmark", "Samsung", "Pantum", "OKI", "Dell", "Toshiba", "Sharp"];
        foreach (var brand in brands)
        {
            if (text.Contains(brand, StringComparison.OrdinalIgnoreCase)) return brand.Equals("Hewlett", StringComparison.OrdinalIgnoreCase) ? "HP" : brand;
        }
        return null;
    }

    private static string MapStatus(bool workOffline, string? status, ushort? printerStatus)
    {
        if (workOffline) return "offline";
        if (printerStatus is 4 or 5) return "printing";
        if (status?.Contains("Error", StringComparison.OrdinalIgnoreCase) == true) return "error";
        if (status?.Contains("Offline", StringComparison.OrdinalIgnoreCase) == true) return "offline";
        if (status?.Contains("Paused", StringComparison.OrdinalIgnoreCase) == true) return "paused";
        if (printerStatus is 3 or null) return "idle";
        return status?.ToLowerInvariant() ?? "unknown";
    }

    private static string? ReadString(ManagementObject p, string property)
        => p[property]?.ToString();

    private static ushort? ReadUInt16(ManagementObject p, string property)
    {
        try { return p[property] is null ? null : Convert.ToUInt16(p[property]); }
        catch { return null; }
    }

    private static bool ReadBool(ManagementObject p, string property)
    {
        try { return p[property] is bool b && b; }
        catch { return false; }
    }
}
