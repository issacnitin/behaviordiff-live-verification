using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BehaviorDiff.Engine;

namespace BehaviorDiff.Cli
{
    internal static class Program
    {
        internal static int Main(string[] args)
        {
            string? baseRef = null;
            string? prRef = null;
            string? work = null;
            bool keep = false;
            var positional = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--base": baseRef = Next(args, ref i); break;
                    case "--pr": prRef = Next(args, ref i); break;
                    case "--work": work = Next(args, ref i); break;
                    case "--keep": keep = true; break;
                    case "-h":
                    case "--help":
                        Usage();
                        return ExitCodes.NoUnexpected;
                    default:
                        positional.Add(args[i]);
                        break;
                }
            }

            string? repo = positional.FirstOrDefault();
            if (repo is null || baseRef is null || prRef is null)
            {
                Usage();
                return ExitCodes.BuildOrTestFailure;
            }

            string workDirectory = work ?? Path.Combine(
                Path.GetTempPath(), "behaviordiff", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

            try
            {
                return new Pipeline(Path.GetFullPath(repo), baseRef, prRef, workDirectory, keep).Run();
            }
            catch (CliException ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("FAILED: " + ex.Message);
                return ex.ExitCode;
            }
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
            {
                throw new CliException("Missing value for " + args[i]);
            }

            return args[++i];
        }

        private static void Usage()
        {
            Console.WriteLine("usage: behaviordiff <repo> --base <ref> --pr <ref> [--work <dir>] [--keep]");
            Console.WriteLine();
            Console.WriteLine("  exit 0  analyzed, no unexpected divergences");
            Console.WriteLine("  exit 1  analyzed, unexpected divergences found");
            Console.WriteLine("  exit 3  could not be trusted (coverage, volume, or call-tree refusal)");
            Console.WriteLine("  exit 4  BehaviorDiff could not instrument this repository");
            Console.WriteLine("  exit 5  this repository does not build in this environment, before instrumentation");
        }
    }

    internal sealed class Pipeline
    {
        private readonly string _repo;
        private readonly string _baseRef;
        private readonly string _prRef;
        private readonly string _work;
        private readonly bool _keep;

        internal Pipeline(string repo, string baseRef, string prRef, string work, bool keep)
        {
            _repo = repo;
            _baseRef = baseRef;
            _prRef = prRef;
            _work = work;
            _keep = keep;
        }

        internal int Run()
        {
            Directory.CreateDirectory(_work);
            Console.WriteLine("behaviordiff");
            Console.WriteLine("  repo : " + _repo);
            Console.WriteLine("  work : " + _work);

            if (!Directory.Exists(Path.Combine(_repo, ".git")) && !File.Exists(Path.Combine(_repo, ".git")))
            {
                throw new CliException(_repo + " is not a git repository.");
            }

            string baseSha = Shell.Git(_repo, "rev-parse", _baseRef);
            string prSha = Shell.Git(_repo, "rev-parse", _prRef);
            Console.WriteLine("  base : " + _baseRef + " -> " + baseSha);
            Console.WriteLine("  pr   : " + _prRef + " -> " + prSha);

            string baseTree = Path.Combine(_work, "base");
            string prTree = Path.Combine(_work, "pr");

            try
            {
                Console.WriteLine();
                Console.WriteLine("=== 1. worktrees ===");
                Shell.Git(_repo, "worktree", "add", "--detach", baseTree, baseSha);
                Shell.Git(_repo, "worktree", "add", "--detach", prTree, prSha);
                Console.WriteLine("  stale bin/obj removed: " + (StripBuildOutput(baseTree) + StripBuildOutput(prTree)));

                Console.WriteLine();
                Console.WriteLine("=== 2. scan ===");
                RepoScanResult scan = RepoScan.Scan(baseTree);
                ReportScan(scan);

                Console.WriteLine();
                Console.WriteLine("=== 3. repo builds unmodified ===");
                BuildUnmodified("base", baseTree);
                BuildUnmodified("pr", prTree);
                Console.WriteLine("  both worktrees build without instrumentation");

                Console.WriteLine();
                Console.WriteLine("=== 4. resolve xunit versions and TFMs ===");
                var baseProjects = scan.XunitProjects.Select(p => Assets.Read(p.Path)).ToList();
                var prProjects = baseProjects
                    .Select(p => Path.Combine(prTree, Path.GetRelativePath(baseTree, p.Path)))
                    .Where(File.Exists)
                    .Select(Assets.Read)
                    .ToList();

                ReportResolved(baseProjects);
                AssertSymmetry(baseProjects, prProjects);

                string kit = InjectionKit.Build(_work);
                Console.WriteLine();
                Console.WriteLine("=== 5. trace adapters (one per test project, per resolved xunit version) ===");
                AdapterBuilder.BuildAll(Path.Combine(_work, "base-adapters"), kit, baseProjects);
                AdapterBuilder.BuildAll(Path.Combine(_work, "pr-adapters"), kit, prProjects);
                foreach (ResolvedTestProject project in baseProjects)
                {
                    Console.WriteLine("  " + project.Name + " -> " + project.XunitPackage + " " + project.XunitVersion + " / " + project.TraceTfm);
                }

                Console.WriteLine();
                Console.WriteLine("=== 6. instrumented build ===");
                BuildInstrumented("base", baseTree, kit, baseProjects);
                BuildInstrumented("pr", prTree, kit, prProjects);

                Console.WriteLine();
                Console.WriteLine("=== 7. test runs ===");
                string scope = string.Join(";", scan.NamespacePrefixes);
                Console.WriteLine("  tracer namespace scope: " + (scope.Length == 0 ? "<empty>" : scope));
                if (scope.Length == 0)
                {
                    throw new CliException("Could not derive any namespace scope from the repository's project names.");
                }

                string base1 = RunTests("base_run1", baseTree, baseProjects, scope);
                string base2 = RunTests("base_run2", baseTree, baseProjects, scope);
                string pr = RunTests("pr_run", prTree, prProjects, scope);

                AssertTestIdsPresent(base1);

                Console.WriteLine();
                Console.WriteLine("=== 8. changed files ===");
                string changedList = WriteChangedFiles(baseSha, prSha);

                Console.WriteLine();
                Console.WriteLine("=== 9. engine part 1 ===");
                string divergenceSet = Path.Combine(_work, "divergence-set.json");
                if (DiffCommand.Run(new DiffOptions
                {
                    Base1 = base1,
                    Base2 = base2,
                    Pr = pr,
                    BaseRoot = baseTree,
                    PrRoot = prTree,
                    Output = divergenceSet,
                }) != 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("RESULT: COULD NOT ANALYZE. The comparison was refused before any finding was produced;");
                    Console.WriteLine("        this is not a statement that the PR is clean.");
                    return ExitCodes.RunInvalid;
                }

                Console.WriteLine();
                Console.WriteLine("=== 10. engine part 2 ===");
                string report = Path.Combine(_work, "frontier-report.json");
                if (FrontierCommand.Run(new FrontierOptions
                {
                    Input = divergenceSet,
                    ChangedFiles = changedList,
                    Output = report,
                }) != 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("RESULT: COULD NOT ANALYZE. Frontier detection was refused; no verdict was produced.");
                    return ExitCodes.RunInvalid;
                }

                return Summarize(report);
            }
            finally
            {
                if (_keep)
                {
                    Console.WriteLine();
                    Console.WriteLine("worktrees kept at " + _work);
                }
                else
                {
                    Cleanup(baseTree);
                    Cleanup(prTree);
                }
            }
        }

        private static void ReportScan(RepoScanResult scan)
        {
            Console.WriteLine("  xunit test projects : " + scan.XunitProjects.Count);
            foreach (TestProject project in scan.XunitProjects)
            {
                Console.WriteLine("    " + Path.GetFileName(project.Path));
            }

            foreach (TestProject project in scan.OtherFrameworks)
            {
                Console.WriteLine("    SKIPPED " + Path.GetFileName(project.Path) + "  [" + project.Framework + "]");
            }

            if (scan.XunitProjects.Count == 0)
            {
                string detail = scan.OtherFrameworks.Count == 0
                    ? "No test projects were found at all."
                    : "Found test projects using: " + string.Join(", ", scan.OtherFrameworks.Select(p => p.Framework).Distinct()) + ".";

                throw new CliException(
                    "No xunit test projects found. " + detail + Environment.NewLine
                    + "    The tracer stamps events with a TestId through an xunit BeforeAfterTestAttribute, so a "
                    + "non-xunit suite would run and produce a trace with no test identity - indistinguishable from "
                    + "a clean result.");
            }

            if (scan.DebugTypeOverrides.Count > 0)
            {
                // Reported, not refused. The build passes -p:DebugType=portable as a global property, which
                // an MSBuild project cannot override, so these settings do not survive. The real check is
                // downstream and measured rather than guessed: the engine refuses any assembly whose
                // members failed to resolve source lines, whatever the reason.
                Console.WriteLine("  NOTE: DebugType is set away from portable in the repository:");
                foreach (string over in scan.DebugTypeOverrides.Distinct())
                {
                    Console.WriteLine("    " + over);
                }

                Console.WriteLine("    Overridden by -p:DebugType=portable; source resolution is verified from the manifest.");
            }
        }

        private static void ReportResolved(List<ResolvedTestProject> projects)
        {
            foreach (ResolvedTestProject project in projects)
            {
                Console.WriteLine("  " + project.Name);
                Console.WriteLine("    xunit     : " + (project.XunitVersion.Length == 0 ? "<unresolved>" : project.XunitPackage + " " + project.XunitVersion));
                Console.WriteLine("    tfms      : " + string.Join(", ", project.AllTfms));
                Console.WriteLine("    tracing   : " + (project.TraceTfm.Length == 0 ? "<none>" : project.TraceTfm) + "  (highest traceable)");
                foreach ((string tfm, string reason) in project.RejectedTfms)
                {
                    Console.WriteLine("    rejected  : " + tfm + " - " + reason);
                }
            }

            var untraceable = projects.Where(p => p.TraceTfm.Length == 0).ToList();
            if (untraceable.Count > 0)
            {
                throw new CliException(
                    "These test projects have no traceable target framework:" + Environment.NewLine
                    + string.Join(Environment.NewLine, untraceable.Select(p => "      " + p.Name + " targets " + string.Join(", ", p.AllTfms)))
                    + Environment.NewLine
                    + "    The tracer requires net5.0 or later. Refusing rather than producing an empty trace.");
            }

            var noVersion = projects.Where(p => p.XunitVersion.Length == 0).ToList();
            if (noVersion.Count > 0)
            {
                throw new CliException(
                    "Could not resolve an xunit version for: " + string.Join(", ", noVersion.Select(p => p.Name)));
            }
        }

        /// <summary>
        /// Base and PR must trace the same target framework. A difference changes which members are
        /// instrumented, which surfaces as manifest gaps rather than as findings.
        /// </summary>
        private static void AssertSymmetry(List<ResolvedTestProject> baseProjects, List<ResolvedTestProject> prProjects)
        {
            if (baseProjects.Count != prProjects.Count)
            {
                throw new CliException(
                    "Base has " + baseProjects.Count + " xunit test project(s), PR has " + prProjects.Count
                    + ". An asymmetric project set produces coverage gaps, not findings.");
            }

            foreach (ResolvedTestProject baseProject in baseProjects)
            {
                ResolvedTestProject? prProject = prProjects.FirstOrDefault(p => p.Name == baseProject.Name);
                if (prProject is null)
                {
                    throw new CliException("Test project " + baseProject.Name + " exists in base but not in PR.");
                }

                if (baseProject.TraceTfm != prProject.TraceTfm)
                {
                    throw new CliException(
                        baseProject.Name + " would be traced on " + baseProject.TraceTfm + " in base but "
                        + prProject.TraceTfm + " in PR. Different frameworks instrument different members, "
                        + "so the comparison would report coverage differences as behavior differences.");
                }
            }

            Console.WriteLine("  base/PR trace framework symmetry: OK");
        }

        private static int StripBuildOutput(string tree)
        {
            int removed = 0;
            foreach (string directory in Directory.EnumerateDirectories(tree, "*", SearchOption.AllDirectories)
                .Where(d => Path.GetFileName(d) is "bin" or "obj")
                .OrderByDescending(d => d.Length)
                .ToList())
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    removed++;
                }
                catch (IOException)
                {
                }
            }

            return removed;
        }

        /// <summary>
        /// The repo must build before anything is injected, so a failure can be attributed. A break that
        /// appears only after injection is ours; a break in both is the repository's.
        /// </summary>
        private static void BuildUnmodified(string label, string tree)
        {
            ProcessResult result = Shell.Run(
                "dotnet",
                new[] { "build", tree, "-c", "Release", "--nologo", "-v", "quiet", "-p:DebugType=portable" },
                tree);

            if (!result.Ok)
            {
                throw new CliException(
                    "This repository does not build in this environment, before any instrumentation." + Environment.NewLine
                    + "    Worktree: " + label + Environment.NewLine
                    + "    BehaviorDiff has changed nothing at this point; the failure below is the repository's."
                    + Environment.NewLine + Shell.Tail(result.Output, 25),
                    ExitCodes.RepoDoesNotBuild);
            }
        }

        private void BuildInstrumented(string label, string tree, string kit, List<ResolvedTestProject> projects)
        {
            foreach (ResolvedTestProject project in projects)
            {
                ProcessResult result = Shell.Run(
                    "dotnet",
                    new[]
                    {
                        "build", project.Path, "-c", "Release", "--nologo", "-v", "quiet",
                        "-p:DebugType=portable",
                        "-p:CustomAfterMicrosoftCommonTargets=" + Path.Combine(kit, "BehaviorDiff.Inject.targets"),
                        "-p:BehaviorDiffKitDir=" + kit + Path.DirectorySeparatorChar,
                        "-p:BehaviorDiffAdapterPath=" + project.AdapterAssemblyPath,
                        "-p:BehaviorDiffTraceTfm=" + project.TraceTfm,
                        "-p:BehaviorDiffTestProjects=" + project.Path,
                    },
                    tree);

                if (!result.Ok)
                {
                    throw new CliException(
                        "The " + label + " worktree built clean unmodified but failed with instrumentation injected into "
                        + project.Name + ". This failure is BehaviorDiff's, not the repository's."
                        + Environment.NewLine + Shell.Tail(result.Output, 25));
                }
            }

            Console.WriteLine("  " + label + " built with instrumentation");
        }

        private string RunTests(string label, string tree, List<ResolvedTestProject> projects, string scope)
        {
            string directory = Path.Combine(_work, label);
            Directory.CreateDirectory(directory);

            var environment = new Dictionary<string, string>
            {
                ["BEHAVIORDIFF_TRACE"] = Path.Combine(directory, "run.ndjson"),
                ["BEHAVIORDIFF_NAMESPACES"] = scope,
            };

            foreach (ResolvedTestProject project in projects)
            {
                ProcessResult result = Shell.Run(
                    "dotnet",
                    new[] { "test", project.Path, "-c", "Release", "-f", project.TraceTfm, "--no-build", "--nologo" },
                    tree,
                    environment);

                // A failing assertion is an observation, not a pipeline failure: the PR may have changed
                // behavior a test asserts on. Only a host that never started is fatal.
                if (!result.Ok && result.Output.Contains("MSB", StringComparison.Ordinal))
                {
                    throw new CliException(
                        "Test host failed to start for " + project.Name + " in " + label + "."
                        + Environment.NewLine + Shell.Tail(result.Output, 20));
                }
            }

            var traces = Directory.GetFiles(directory, "run.*.ndjson")
                .Where(f => !f.Contains(".manifest.", StringComparison.Ordinal))
                .ToList();

            long bytes = traces.Sum(f => new FileInfo(f).Length);
            Console.WriteLine("  " + label.PadRight(10) + " traces=" + traces.Count + " bytes=" + bytes);

            // The tracer runs inside a test host whose exit code belongs to xunit, so it reports
            // run-invalidating conditions through a marker file rather than by failing the process.
            var markers = Directory.GetFiles(directory, "*.FAILED");
            if (markers.Length > 0)
            {
                throw new CliException(
                    "The tracer reported a run-invalidating condition during " + label + ":" + Environment.NewLine
                    + string.Join(Environment.NewLine, markers.SelectMany(File.ReadAllLines).Distinct().Select(l => "    " + l)),
                    ExitCodes.RunInvalid);
            }

            if (traces.Count == 0 || bytes == 0)
            {
                throw new CliException(
                    "NO EVENTS: " + label + " produced " + traces.Count + " trace file(s) totalling " + bytes
                    + " bytes. The tracer initialized but recorded nothing, so either no test executed or "
                    + "no member was instrumented. This is not a question of test identity - see the tracer "
                    + "log and the coverage manifest in " + directory + ".",
                    ExitCodes.RunInvalid);
            }

            return directory;
        }

        /// <summary>
        /// Distinct from the no-events case on purpose. "Nothing ran" and "things ran but are unlabelled"
        /// live in different layers, and a guard that names the wrong one sends you to the wrong code.
        /// </summary>
        private static void AssertTestIdsPresent(string runDirectory)
        {
            int withTestId = 0;
            int total = 0;

            foreach (string file in Directory.GetFiles(runDirectory, "run.*.ndjson")
                .Where(f => !f.Contains(".manifest.", StringComparison.Ordinal)))
            {
                foreach (string line in File.ReadLines(file))
                {
                    total++;
                    if (line.Contains("\"testId\":\"", StringComparison.Ordinal)
                        && !line.Contains("\"testId\":\"(no-test)\"", StringComparison.Ordinal))
                    {
                        withTestId++;
                    }

                    if (total >= 20000)
                    {
                        break;
                    }
                }
            }

            double share = total == 0 ? 0 : withTestId * 100.0 / total;
            Console.WriteLine("  events carrying a TestId: " + share.ToString("F1", CultureInfo.InvariantCulture) + "%  (of " + total + " events)");

            if (total > 0 && share < 50)
            {
                throw new CliException(
                    "UNLABELLED EVENTS: " + total + " events were produced but only "
                    + share.ToString("F1", CultureInfo.InvariantCulture) + "% carry a TestId. Instrumentation "
                    + "worked; test identity did not. The assembly-level [TraceTest] registration is not being "
                    + "honoured by this repo's xunit version, so events cannot be correlated across runs.",
                    ExitCodes.RunInvalid);
            }
        }

        private string WriteChangedFiles(string baseSha, string prSha)
        {
            string raw = Shell.Git(_repo, "diff", "--name-only", baseSha, prSha);
            var changed = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().Replace('\\', '/'))
                .Where(l => l.Length > 0)
                .ToList();

            string path = Path.Combine(_work, "changed-files.txt");
            File.WriteAllLines(path, changed);

            Console.WriteLine("  changed files: " + changed.Count);
            foreach (string file in changed.Take(15))
            {
                Console.WriteLine("    " + file);
            }

            return path;
        }

        private static int Summarize(string reportPath)
        {
            var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(reportPath));
            System.Text.Json.JsonElement counts = report.RootElement.GetProperty("counts");
            int unexpected = counts.GetProperty("unexpected").GetInt32();
            int expected = counts.GetProperty("expected").GetInt32();
            int untested = counts.GetProperty("untested").GetInt32();

            Console.WriteLine();
            if (unexpected == 0)
            {
                Console.WriteLine("RESULT: ANALYZED, no unexpected behavior changes.");
                Console.WriteLine("        " + expected + " change(s) confined to edited files; " + untested + " untested.");
                return ExitCodes.NoUnexpected;
            }

            Console.WriteLine("RESULT: ANALYZED, " + unexpected + " unexpected behavior change(s) in files the PR did not edit.");
            return ExitCodes.UnexpectedFound;
        }

        private void Cleanup(string tree)
        {
            try
            {
                Shell.Run("git", new[] { "worktree", "remove", "--force", tree }, _repo);
            }
            catch (Exception)
            {
            }
        }
    }
}
