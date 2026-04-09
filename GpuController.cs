using System.Diagnostics;
using System.Management;

namespace GPUKill;

public enum GpuState
{
    Unknown,
    Enabled,
    Disabled,
    NotFound,
}

/// <summary>
/// Locates the NVIDIA discrete GPU and toggles its PnP state via pnputil.
/// On the first-gen Zephyrus G14 (GA401) there is no firmware MUX or ACPI
/// power-gate path, so disabling at the PnP level is the only useful lever.
/// </summary>
public sealed class GpuController
{
    private const string NvidiaVendorId = "VEN_10DE";

    /// <summary>
    /// Returns the PnP instance ID of the first NVIDIA Display device found,
    /// or null if no NVIDIA dGPU is present.
    /// </summary>
    public string? FindNvidiaInstanceId()
    {
        // PNPClass = "Display" filters to actual GPU devices (not audio, USB-C, etc.).
        using var searcher = new ManagementObjectSearcher(
            @"SELECT DeviceID, PNPDeviceID, Status FROM Win32_PnPEntity WHERE PNPClass = 'Display'");

        foreach (ManagementObject mo in searcher.Get())
        {
            var pnpId = mo["PNPDeviceID"] as string;
            if (pnpId != null && pnpId.Contains(NvidiaVendorId, StringComparison.OrdinalIgnoreCase))
            {
                return pnpId;
            }
        }
        return null;
    }

    public GpuState GetState(string instanceId)
    {
        // ConfigManagerErrorCode == 22 means "Device disabled".
        // Status "OK" with no error means enabled.
        using var searcher = new ManagementObjectSearcher(
            $"SELECT ConfigManagerErrorCode, Status FROM Win32_PnPEntity WHERE PNPDeviceID = '{EscapeWql(instanceId)}'");

        foreach (ManagementObject mo in searcher.Get())
        {
            var err = Convert.ToUInt32(mo["ConfigManagerErrorCode"] ?? 0u);
            return err == 22 ? GpuState.Disabled : GpuState.Enabled;
        }
        return GpuState.NotFound;
    }

    public Task<bool> DisableAsync(string instanceId) =>
        RunPnpUtilAsync("/disable-device", instanceId);

    public Task<bool> EnableAsync(string instanceId) =>
        RunPnpUtilAsync("/enable-device", instanceId);

    public async Task<bool> RestartAsync(string instanceId)
    {
        // Mirrors the user's existing "restartGPU" behavior: disable then re-enable.
        if (!await RunPnpUtilAsync("/restart-device", instanceId))
        {
            // Older pnputil builds lack /restart-device, fall back to disable+enable.
            if (!await DisableAsync(instanceId)) return false;
            await Task.Delay(500);
            return await EnableAsync(instanceId);
        }
        return true;
    }

    private static async Task<bool> RunPnpUtilAsync(string verb, string instanceId)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pnputil.exe",
            // pnputil expects the instance ID quoted; backslashes are fine inside quotes.
            Arguments = $"{verb} \"{instanceId}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeWql(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
}
