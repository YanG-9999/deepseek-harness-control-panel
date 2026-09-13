// State-refresh cost baseline for the control panel.
//
// Measured on this machine while Harness was listening on 3080, with the listener
// (node.exe apps/cli/lib/bin.js web --no-open) present:
//
//   FindPortOwner (spawns netstat.exe)                24 ms
//   WMI scoped query for one process command line     40-166 ms
//   WMI scoped query for one process parent pid       69 ms
//   full IsLikelyHarnessProcess chain (5 hops)       689 ms
//   one RunAsync completion (3 refreshes)            854 ms
//   projected 3s poll on the UI thread            ~319 ms per poll
//
// The chain walk stops at hop 0 in practice: the port owner is the node process
// itself, so the parent traversal only ever runs as unused insurance. Batch
// snapshots were rejected as a replacement because a full-table query returned an
// empty CommandLine intermittently, which would misreport a real Harness as a
// foreign listener.
//
// Build: csc /target:exe /r:System.Management.dll tools\Baseline.cs
using System;
using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

/// <summary>
/// Measures the two operations that dominate a state refresh, mirroring
/// ManagerForm.FindPortOwner, GetProcessCommandLine, GetParentProcessId, and
/// IsLikelyHarnessProcess exactly so the numbers transfer to the panel.
/// </summary>
public static class Baseline
{
    /// <summary>Mirrors ManagerForm.FindPortOwner.</summary>
    private static int FindPortOwner(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            using (var process = Process.Start(psi))
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                foreach (string line in output.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!line.Contains("LISTENING"))
                        continue;
                    string[] parts = Regex.Split(line.Trim(), "\\s+");
                    int pid;
                    if (parts.Length >= 5 && parts[1].EndsWith(":" + port) && Int32.TryParse(parts[parts.Length - 1], out pid))
                        return pid;
                }
            }
        }
        catch { }
        return 0;
    }

    /// <summary>Mirrors ManagerForm.GetProcessCommandLine.</summary>
    private static string GetProcessCommandLine(int pid)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                    return item["CommandLine"] == null ? "" : item["CommandLine"].ToString();
            }
        }
        catch { }
        return "";
    }

    /// <summary>Mirrors ManagerForm.GetParentProcessId.</summary>
    private static int GetParentProcessId(int pid)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = " + pid))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                    return item["ParentProcessId"] == null ? 0 : Convert.ToInt32(item["ParentProcessId"]);
            }
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// Mirrors ManagerForm.IsLikelyHarnessProcess: up to 8 hops up the parent chain,
    /// one WMI query for the command line and one for the parent pid per hop.
    /// </summary>
    private static bool IsLikelyHarnessProcess(int pid, out int hops, out int wmiCalls)
    {
        hops = 0;
        wmiCalls = 0;
        int current = pid;
        for (int depth = 0; depth < 8 && current > 0; depth++)
        {
            hops++;
            wmiCalls++;
            string commandLine = GetProcessCommandLine(current);
            if (commandLine.IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            wmiCalls++;
            int parent = GetParentProcessId(current);
            if (parent == current)
                break;
            current = parent;
        }
        return false;
    }

    public static int Main()
    {
        int probe = Process.GetCurrentProcess().Id;
        var watch = new Stopwatch();

        Console.WriteLine("=== cost of one FindPortOwner (spawns netstat.exe) ===");
        watch.Restart();
        int owner = FindPortOwner(3080);
        watch.Stop();
        Console.WriteLine("  port owner pid={0}  elapsed={1}ms", owner, watch.ElapsedMilliseconds);

        Console.WriteLine("=== cost of one WMI command-line query ===");
        watch.Restart();
        GetProcessCommandLine(probe);
        watch.Stop();
        Console.WriteLine("  elapsed={0}ms", watch.ElapsedMilliseconds);

        Console.WriteLine("=== cost of one WMI parent-pid query ===");
        watch.Restart();
        GetParentProcessId(probe);
        watch.Stop();
        Console.WriteLine("  elapsed={0}ms", watch.ElapsedMilliseconds);

        Console.WriteLine("=== cost of one full IsLikelyHarnessProcess chain ===");
        int hops, wmiCalls;
        watch.Restart();
        IsLikelyHarnessProcess(probe, out hops, out wmiCalls);
        watch.Stop();
        Console.WriteLine("  hops={0}  wmiCalls={1}  elapsed={2}ms", hops, wmiCalls, watch.ElapsedMilliseconds);

        // A RunAsync completion runs SetButtons(false), SetButtons(true), then
        // RefreshState(); the first two each do netstat + chain, the third too.
        Console.WriteLine();
        Console.WriteLine("=== simulated RunAsync completion (3 refreshes) ===");
        int netstatSpawns = 0;
        int totalWmi = 0;
        watch.Restart();
        for (int pass = 0; pass < 3; pass++)
        {
            netstatSpawns++;
            int pid = FindPortOwner(3080);
            if (pid > 0)
            {
                int h, w;
                IsLikelyHarnessProcess(pid, out h, out w);
                totalWmi += w;
            }
            else
            {
                // With no listener the chain is skipped, which is the common idle case.
            }
        }
        watch.Stop();
        Console.WriteLine("  netstat spawns={0}  wmi calls={1}  elapsed={2}ms", netstatSpawns, totalWmi, watch.ElapsedMilliseconds);

        // Project the same work at a 3 second poll interval.
        Console.WriteLine();
        Console.WriteLine("=== projection: same refresh every 3s, measured over 5 polls ===");
        watch.Restart();
        for (int poll = 0; poll < 5; poll++)
        {
            int pid = FindPortOwner(3080);
            if (pid > 0)
            {
                int h, w;
                IsLikelyHarnessProcess(pid, out h, out w);
            }
        }
        watch.Stop();
        Console.WriteLine("  5 polls elapsed={0}ms  -> per poll ~{1}ms of blocking work", watch.ElapsedMilliseconds, watch.ElapsedMilliseconds / 5);

        return 0;
    }
}
