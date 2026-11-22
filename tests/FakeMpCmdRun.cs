// A stand-in for Microsoft Defender's MpCmdRun.exe (tests only): "-Scan -ScanType 3 -File <path> -DisableRemediation".
// Exit 2 + "Threat : ..." when the file contains HALLOW-TEST-THREAT, else exit 0. It writes its own priority class next to itself
// (priority.txt) so the tests can check Hallow Email starts scans at Idle priority. It never scans or changes anything.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

static class FakeMpCmdRun
{
    static int Main(string[] a)
    {
        Thread.Sleep(400);
        try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "priority.txt"), Process.GetCurrentProcess().PriorityClass.ToString()); } catch { }
        int i = Array.IndexOf(a, "-File"); if (i < 0 || i + 1 >= a.Length || Array.IndexOf(a, "-DisableRemediation") < 0) { Console.WriteLine("bad arguments"); return 1; }
        var text = File.Exists(a[i + 1]) ? File.ReadAllText(a[i + 1]) : "";
        if (text.Contains("HALLOW-TEST-THREAT")) { Console.WriteLine("Scan starting...\nThreat                  : Virus:DOS/Hallow_Test_Standin\nResources               : 1 total"); return 2; }
        Console.WriteLine("Scan starting...\nScan finished.\nScanning " + a[i + 1] + " found no threats."); return 0;
    }
}
