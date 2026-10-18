using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WhisperNote.Services;

public enum HardwareBackend
{
    Unknown,
    NvidiaCuda,
    NvidiaCudaLegacy,
    Vulkan,
    IntelNpu,
    Cpu
}

public static class HardwareDetector
{
    // CUDA 13 ships no kernels (not even PTX) for pre-Turing GPUs; they abort with "no kernel image is available".
    const double Cuda13MinimumComputeCapability = 7.5;

    const int NvidiaSmiTimeoutMs = 5000;

    static readonly Regex LegacyNvidiaModel = new(
        @"\b(GTX\s*10\d{2}|GTX\s*9\d{2}|GTX\s*750|TITAN\s*X|Quadro\s*[PM]\d|Tesla\s*[PM]\d|Jetson\s*T[XM])\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static HardwareBackend Detect()
    {
        try
        {
            var gpus = GetGpuNames();
            if (gpus == null)
            {
                Logger.Warn("WMI unavailable; assuming an NVIDIA CUDA GPU");
                return HardwareBackend.NvidiaCuda;
            }

            if (gpus.Exists(name => name.Contains("Intel", StringComparison.OrdinalIgnoreCase)))
                return HardwareBackend.Vulkan;

            var nvidia = gpus.Find(name =>
                name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Quadro", StringComparison.OrdinalIgnoreCase));

            if (nvidia != null)
            {
                if (IsLegacyNvidia(nvidia))
                {
                    Logger.Info($"Legacy NVIDIA GPU detected: {nvidia} (pre-Turing), using the CUDA 12.4 backend");
                    return HardwareBackend.NvidiaCudaLegacy;
                }

                Logger.Info($"NVIDIA GPU detected: {nvidia}");
                return HardwareBackend.NvidiaCuda;
            }

            if (gpus.Exists(name => name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)))
            {
                Logger.Info($"AMD GPU detected: {gpus.Find(name => name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))}");
                return HardwareBackend.Vulkan;
            }

            if (HasIntelNpu())
                return HardwareBackend.IntelNpu;

            Logger.Warn("No supported GPU found, the server will run on the CPU");
            return HardwareBackend.Cpu;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Hardware detection failed, falling back to CUDA: {ex.Message}");
        }

        return HardwareBackend.NvidiaCuda;
    }

    static List<string>? GetGpuNames()
    {
        var names = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
                names.Add(obj["Name"] as string ?? "");
        }
        catch (COMException)
        {
            return null;
        }

        return names;
    }

    static bool IsLegacyNvidia(string gpuName)
    {
        var capability = ReadHighestComputeCapability();
        if (capability.HasValue)
        {
            Logger.Info($"NVIDIA compute capability: {capability.Value.ToString("0.0", CultureInfo.InvariantCulture)}");
            return capability.Value < Cuda13MinimumComputeCapability;
        }

        return LegacyNvidiaModel.IsMatch(gpuName);
    }

    static double? ReadHighestComputeCapability()
    {
        var output = RunProcess("nvidia-smi", "--query-gpu=compute_cap --format=csv,noheader", NvidiaSmiTimeoutMs);
        if (string.IsNullOrEmpty(output))
            return null;

        double? highest = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                highest = highest == null ? value : Math.Max(highest.Value, value);
        }

        return highest;
    }

    static string? RunProcess(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var task = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                TryKill(process);
                return null;
            }

            return task.Result;
        }
        catch (Exception ex)
        {
            Logger.Info($"{fileName} is not available: {ex.Message}");
            return null;
        }
    }

    static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { Logger.Info($"Killed {process.ProcessName}: {ex.Message}"); }
    }

    static bool HasIntelNpu()
    {
        try
        {
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
        }

        return false;
    }
}
