using System;
using System.Collections.Generic;

/// <summary>Result bookkeeping: every check runs, failures are listed together at the end, exit code 1 on any.</summary>
static class TestRun
{
    public static int Passed;
    public static readonly List<string> Failures = new();

    public static void Record(bool ok, string name)
    {
        if (ok) { Passed++; Console.WriteLine("PASS: " + name); return; }
        Failures.Add(name);
        Environment.ExitCode = 1;
        Console.WriteLine("FAIL: " + name);
    }

    public static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine($"== {Passed} passed, {Failures.Count} failed ==");
        foreach (var f in Failures) Console.WriteLine("  FAILED: " + f);
    }
}
