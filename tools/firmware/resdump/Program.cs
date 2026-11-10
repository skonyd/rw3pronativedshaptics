using System.Resources; using System.Text.RegularExpressions;
// Read-only: groups string entries "<Prefix>Line<N>" in line order, writes each group as text; prints other strings.
var outDir = args[1]; Directory.CreateDirectory(outDir);
using var rr = new ResourceReader(args[0]);
var groups = new Dictionary<string, SortedDictionary<int, string>>();
var e = rr.GetEnumerator();
while (e.MoveNext())
{
    var key = (string)e.Key; var val = e.Value as string;
    var m = Regex.Match(key, @"^(.*?)Line(\d+)$");
    if (m.Success && val != null) { if (!groups.TryGetValue(m.Groups[1].Value, out var g)) groups[m.Groups[1].Value] = g = new(); g[int.Parse(m.Groups[2].Value)] = val; }
    else Console.WriteLine($"{key} = {(val ?? e.Value?.GetType().Name)}");
}
foreach (var (p, g) in groups)
{
    File.WriteAllLines(Path.Combine(outDir, p + ".txt"), g.Values);
    Console.WriteLine($"group {p}: {g.Count} lines, first {g.First().Value}  last {g.Last().Value}");
}
