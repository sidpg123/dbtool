using System.Diagnostics;
using System.Text.RegularExpressions;
using SqlRepoAnalyzer.Core.Logging;

namespace SqlRepoAnalyzer.TypeScript.Node;

public static class NodeTooling
{
    private static readonly Regex NodeVersionRegex = new(@"v(?<maj>\d+)\.(?<min>\d+)\.(?<patch>\d+)", RegexOptions.Compiled);
    public static readonly Version MinimumNodeVersion = new(18, 0, 0);

    public static async Task<NodeVersionResult> CheckNodeAsync(Logger log, CancellationToken ct)
    {
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            p.Start();
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            if (p.ExitCode != 0)
            {
                return new NodeVersionResult(false, null, $"node --version failed (exit={p.ExitCode}): {stderr}".Trim());
            }

            var m = NodeVersionRegex.Match(stdout.Trim());
            if (!m.Success)
            {
                return new NodeVersionResult(false, null, $"Could not parse Node version output: {stdout}".Trim());
            }

            var version = new Version(
                int.Parse(m.Groups["maj"].Value),
                int.Parse(m.Groups["min"].Value),
                int.Parse(m.Groups["patch"].Value)
            );

            log.Info("Node detected", new Dictionary<string, object?>
            {
                ["nodeVersion"] = version.ToString()
            });

            if (version < MinimumNodeVersion)
            {
                return new NodeVersionResult(false, version, $"Node {version} is below required minimum {MinimumNodeVersion}");
            }

            return new NodeVersionResult(true, version, null);
        }
        catch (Exception ex)
        {
            return new NodeVersionResult(false, null, $"Node check failed: {ex.Message}");
        }
    }
}

public sealed record NodeVersionResult(bool Ok, Version? Version, string? Error);

