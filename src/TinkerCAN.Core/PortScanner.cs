using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;

namespace TinkerCAN.Core;

public static class PortScanner
{
    public static IReadOnlyList<PortInfo> List()
    {
        var result = new List<PortInfo>();
        foreach (var name in SerialPort.GetPortNames())
            result.Add(new PortInfo(name, GetDescription(name)));
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static string GetDescription(string portName)
    {
        try
        {
            if (OperatingSystem.IsLinux()) return GetLinuxDescription(portName);
            if (OperatingSystem.IsWindows()) return GetWindowsDescription(portName);
        }
        catch { }
        return "";
    }

    private static string GetLinuxDescription(string portName)
    {
        // Primary: udevadm (semantic, always accurate)
        string udev = GetUdevDescription(portName);
        if (!string.IsNullOrEmpty(udev)) return udev;

        // Fallback: sysfs manual traversal
        return GetSysfsDescription(portName);
    }

    private static string GetUdevDescription(string portName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "udevadm",
                Arguments = $"info -q property {portName}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";

            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) != null)
            {
                int eq = line.IndexOf('=');
                if (eq > 0) props[line[..eq]] = line[(eq + 1)..];
            }
            proc.WaitForExit(2000);

            if (props.TryGetValue("ID_MODEL", out var model) && !string.IsNullOrEmpty(model))
                return model.Replace('_', ' ');
            if (props.TryGetValue("ID_VENDOR", out var vendor) && !string.IsNullOrEmpty(vendor))
                return vendor.Replace('_', ' ');
            if (props.TryGetValue("ID_VENDOR_ID", out var vid) && props.TryGetValue("ID_MODEL_ID", out var pid))
                return $"{vid}:{pid}";
        }
        catch { }
        return "";
    }

    private static string GetSysfsDescription(string portName)
    {
        try
        {
            string ttyName = Path.GetFileName(portName);
            string deviceLink = $"/sys/class/tty/{ttyName}/device";

            string? linkTarget = new DirectoryInfo(deviceLink).LinkTarget;
            if (linkTarget == null) return "";

            string deviceDir = Path.IsPathRooted(linkTarget)
                ? linkTarget
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(deviceLink)!, linkTarget));

            string? dir = deviceDir;
            while (dir != null && dir.StartsWith("/sys", StringComparison.Ordinal))
            {
                if (File.Exists(Path.Combine(dir, "idVendor")))
                {
                    string product = ReadSys(Path.Combine(dir, "product"));
                    if (!string.IsNullOrEmpty(product)) return product;
                    string mfg = ReadSys(Path.Combine(dir, "manufacturer"));
                    if (!string.IsNullOrEmpty(mfg)) return mfg;
                    return $"{ReadSys(Path.Combine(dir, "idVendor"))}:{ReadSys(Path.Combine(dir, "idProduct"))}";
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        return "";
    }

    private static string ReadSys(string path)
    {
        try { return File.ReadAllText(path).Trim(); } catch { return ""; }
    }

    private static string GetWindowsDescription(string portName)
    {
        try { return SearchWindowsRegistry(portName); }
        catch { return ""; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string SearchWindowsRegistry(string portName)
    {
        string[] subtrees = ["USB", "FTDIBUS", "BTHENUM", "ACPI"];
        using var enumKey = Microsoft.Win32.Registry.LocalMachine
            .OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
        if (enumKey == null) return "";

        foreach (var subtree in subtrees)
        {
            using var sub = enumKey.OpenSubKey(subtree);
            if (sub == null) continue;
            foreach (var vid in sub.GetSubKeyNames())
            {
                using var vidKey = sub.OpenSubKey(vid);
                if (vidKey == null) continue;
                foreach (var serial in vidKey.GetSubKeyNames())
                {
                    using var devKey = vidKey.OpenSubKey(serial);
                    if (devKey == null) continue;
                    using var dp = devKey.OpenSubKey("Device Parameters");
                    if (dp?.GetValue("PortName") is string pn &&
                        string.Equals(pn, portName, StringComparison.OrdinalIgnoreCase))
                        return devKey.GetValue("FriendlyName") as string ?? "";
                }
            }
        }
        return "";
    }
}
