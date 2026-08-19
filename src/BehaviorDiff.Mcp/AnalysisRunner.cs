using System.Diagnostics;

namespace BehaviorDiff.Mcp;

/// <summary>
/// Invokes the existing BehaviorDiff CLI. It does not reimplement any part of the analysis; it shells
/// out, records the exit code, and copies the CLI's own artifacts into the run directory.
/// </summary>
public static class AnalysisRunner
{
    /// <summary>Absolute path to the built BehaviorDiff CLI. Overridable for tests and packaging.</summary>
    public static string CliPath { get; set; } =
        Environment.GetEnvironmentVariable("BEHAVIORDIFF_CLI") ?? "behaviordiff";

    public static void StartInBackground(RunRecord record)
    {
        _ = Task.Run(() => Run(record));
    }

    private static void Run(RunRecord record)
    {
        string runDir = RunStore.Directory_(record.RunId);
        string work = Path.Combine(runDir, "work");
        Directory.CreateDirectory(work);

        try
        {
            record.Status = "running";
            record.Phase = "building and tracing both worktrees";
            record.Progress = 10;
            RunStore.Save(record);

            var psi = new ProcessStartInfo(CliPath)
            {
                WorkingDirectory = record.RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(record.RepoPath);
            psi.ArgumentList.Add("--base");
            psi.ArgumentList.Add(record.BaseRef);
            psi.ArgumentList.Add("--pr");
            psi.ArgumentList.Add(record.PrRef);
            psi.ArgumentList.Add("--work");
            psi.ArgumentList.Add(work);
            psi.ArgumentList.Add("--keep");

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                Fail(record, "could not start the BehaviorDiff CLI at '" + CliPath + "'");
                return;
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            File.WriteAllText(Path.Combine(runDir, "cli.log"), stdout + Environment.NewLine + stderr);
            record.ExitCode = process.ExitCode;

            foreach (string name in new[] { "divergence-set.json", "frontier-report.json" })
            {
                string source = Path.Combine(work, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(runDir, name), overwrite: true);
                }
            }

            // Exit codes are the CLI's, unchanged. 3, 4 and 5 are all "no trustworthy answer" and must
            // never be presented as a clean run.
            switch (process.ExitCode)
            {
                case 0:
                case 1:
                    record.Status = "complete";
                    record.Phase = "done";
                    record.Progress = 100;
                    break;
                case 3:
                    Fail(record, "refused", "the analysis could not be trusted (coverage, volume, or call-tree refusal). Last CLI output: " + Tail(stdout));
                    return;
                case 4:
                    Fail(record, "failed", "BehaviorDiff could not instrument this repository. Last CLI output: " + Tail(stdout));
                    return;
                case 5:
                    Fail(record, "failed", "this repository does not build in this environment, before instrumentation. Last CLI output: " + Tail(stdout));
                    return;
                default:
                    Fail(record, "failed", "the CLI exited with " + process.ExitCode + ". Last output: " + Tail(stdout + stderr));
                    return;
            }

            record.CompletedUtc = DateTimeOffset.UtcNow;
            RunStore.Save(record);
        }
        catch (Exception ex)
        {
            Fail(record, "failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void Fail(RunRecord record, string error) => Fail(record, "failed", error);

    private static void Fail(RunRecord record, string status, string error)
    {
        record.Status = status;
        record.Phase = "stopped";
        record.Error = error;
        record.CompletedUtc = DateTimeOffset.UtcNow;
        RunStore.Save(record);
    }

    private static string Tail(string text)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.TakeLast(6).Select(l => l.TrimEnd('\r').Trim()));
    }
}
