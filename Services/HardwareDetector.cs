using System;
using System.Management;
using System.Runtime.InteropServices;

namespace WhisperNote.Services;

public enum HardwareBackend
{
    Unknown,
    NvidiaCuda,
    IntelNpu
}

public static class HardwareDetector
{
    public static HardwareBackend Detect()
    {
        try
        {
            if (HasIntelNpu())
                return HardwareBackend.IntelNpu;

            if (HasNvidiaGpu())
                return HardwareBackend.NvidiaCuda;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Hardware detection failed, falling back to CUDA: {ex.Message}");
        }

        return HardwareBackend.NvidiaCuda;
    }

    static bool HasIntelNpu()
    {
        try
        {
            // Method 1: Check processor for Intel Core Ultra (has integrated NPU)
            using var cpuSearcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject cpu in cpuSearcher.Get())
            {
                var cpuName = cpu["Name"] as string ?? "";
                if (cpuName.Contains("Ultra", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info($"Intel NPU detected via CPU: {cpuName}");
                    return true;
                }
            }

            // Method 2: Check for NPU device in PnP entities
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE Description LIKE '%NPU%' OR ClassGuid = '{50129DC1-04D0-4ACB-9883-3EEA181B84D2}'");

            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"] as string ?? "";
                var description = obj["Description"] as string ?? "";
                var combined = (name + " " + description).ToLowerInvariant();
                if (combined.Contains("intel") && combined.Contains("npu"))
                {
                    Logger.Info($"Intel NPU detected: {name}");
                    return true;
                }
            }
        }
        catch (COMException)
        {
            // WMI not available
        }

        return false;
    }

    static bool HasNvidiaGpu()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_VideoController WHERE Name LIKE '%NVIDIA%' OR Name LIKE '%GeForce%' OR Name LIKE '%Quadro%'");

            foreach (ManagementObject _ in searcher.Get())
            {
                Logger.Info("NVIDIA GPU detected");
                return true;
            }
        }
        catch (COMException)
        {
            // WMI not available
        }

        return false;
    }
}
