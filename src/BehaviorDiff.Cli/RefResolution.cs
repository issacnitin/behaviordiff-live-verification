using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BehaviorDiff.Cli
{
    internal sealed class ResolvedRefs
    {
        internal required string BaseLabel { get; init; }

        internal required string PrLabel { get; init; }

        internal required string BaseSha { get; init; }

        internal required string PrSha { get; init; }

        internal required string MergeBaseSha { get; init; }

        internal required IReadOnlyList<string> ChangedFiles { get; init; }

        internal int PrCommitCount { get; init; }

        internal bool IsCi { get; init; }

        internal string? PullRequestId { get; init; }
    }

    internal static class RefResolution
    {
        internal const string AzureDevOps = "azuredevops";

        internal static string ResolveRepository(string? explicitRepository, string? ciProvider)
        {
            if (explicitRepository is not null)
            {
                return Path.GetFullPath(explicitRepository);
            }

            if (ciProvider == AzureDevOps)
            {
                return Path.GetFullPath(RequiredEnvironment("BUILD_REPOSITORY_LOCALPATH"));
            }

            throw new CliException("A repository path is required unless --ci=azuredevops supplies BUILD_REPOSITORY_LOCALPATH.");
        }

        internal static ResolvedRefs Resolve(
            string repository,
            string? baseRef,
            string? prRef,
            string? ciProvider)
        {
            if (ciProvider is null)
            {
                if (baseRef is null || prRef is null)
                {
                    throw new CliException("Explicit mode requires both --base and --pr.");
                }

                string baseSha = Commit(repository, baseRef);
                string prSha = Commit(repository, prRef);
                return Create(repository, baseRef, prRef, baseSha, prSha, isCi: false, pullRequestId: null);
            }

            if (ciProvider != AzureDevOps)
            {
                throw new CliException("Unknown CI provider '" + ciProvider + "'. Supported: azuredevops.");
            }

            if (baseRef is not null || prRef is not null)
            {
                throw new CliException("--ci=azuredevops derives refs; do not combine it with --base or --pr.");
            }

            return ResolveAzureDevOps(repository);
        }

        private static ResolvedRefs ResolveAzureDevOps(string repository)
        {
            string sourceBranch = RequiredEnvironment("SYSTEM_PULLREQUEST_SOURCEBRANCH");
            string targetBranch = RequiredEnvironment("SYSTEM_PULLREQUEST_TARGETBRANCH");
            string pullRequestId = RequiredEnvironment("SYSTEM_PULLREQUEST_PULLREQUESTID");
            string mergeRef = RequiredEnvironment("BUILD_SOURCEVERSION");

            // Read and report the repository identity too. These are the documented Build.Repository.*
            // variables, and make it visible when a multi-checkout job points BehaviorDiff at the wrong repo.
            string repositoryId = RequiredEnvironment("BUILD_REPOSITORY_ID");
            string repositoryName = RequiredEnvironment("BUILD_REPOSITORY_NAME");
            string repositoryProvider = RequiredEnvironment("BUILD_REPOSITORY_PROVIDER");
            string repositoryUri = RequiredEnvironment("BUILD_REPOSITORY_URI");

            string mergeSha = Commit(repository, mergeRef);
            string[] parents = Shell.Git(repository, "rev-list", "--parents", "-n", "1", mergeSha)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Microsoft documents Build.SourceVersion as the merge commit for a PR build. Parent one is
            // the target snapshot and parent two is the reviewed source tip. Using HEAD itself would compare
            // against the synthetic merge result rather than the PR branch.
            if (parents.Length != 3)
            {
                throw new CliException(
                    "BUILD_SOURCEVERSION " + mergeSha + " is not a two-parent PR merge commit. "
                    + "Ref resolution cannot distinguish the target snapshot from the reviewed source tip.",
                    ExitCodes.RunInvalid);
            }

            string baseSha = parents[1];
            string prSha = parents[2];
            ValidateFetchedBranch(repository, targetBranch, baseSha, "target");
            ValidateFetchedBranch(repository, sourceBranch, prSha, "source");

            Console.WriteLine("  CI provider : Azure DevOps");
            Console.WriteLine("  PR id       : " + pullRequestId);
            Console.WriteLine("  repository  : " + repositoryName + " (" + repositoryId + ", " + repositoryProvider + ")");
            Console.WriteLine("  repository URI: " + repositoryUri);
            Console.WriteLine("  merge commit: " + mergeSha);

            ResolvedRefs resolved = Create(
                repository,
                targetBranch,
                sourceBranch,
                baseSha,
                prSha,
                isCi: true,
                pullRequestId);

            int maximum = MaximumChangedFiles();
            Console.WriteLine("  changed-file plausibility limit: " + maximum + " for a PR with at most 3 commits");
            if (resolved.PrCommitCount <= 3 && resolved.ChangedFiles.Count > maximum)
            {
                throw new CliException(
                    "IMPLAUSIBLE CHANGED-FILE SET: a " + resolved.PrCommitCount + "-commit PR resolved to "
                    + resolved.ChangedFiles.Count + " files (limit " + maximum + "). Refusing because a wrong base silently "
                    + "classifies unrelated files as EXPECTED. Verify the printed base, PR, and merge-base SHAs; "
                    + "set BEHAVIORDIFF_MAX_CHANGED_FILES only for a reviewed bulk change.",
                    ExitCodes.RunInvalid);
            }

            return resolved;
        }

        private static ResolvedRefs Create(
            string repository,
            string baseLabel,
            string prLabel,
            string baseSha,
            string prSha,
            bool isCi,
            string? pullRequestId)
        {
            string mergeBaseSha = Shell.Git(repository, "merge-base", baseSha, prSha);
            IReadOnlyList<string> changedFiles = Shell.Git(
                    repository,
                    "diff",
                    "--name-only",
                    mergeBaseSha,
                    prSha)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().Replace('\\', '/'))
                .Where(line => line.Length > 0)
                .ToList();

            int prCommitCount = int.Parse(
                Shell.Git(repository, "rev-list", "--count", mergeBaseSha + ".." + prSha),
                CultureInfo.InvariantCulture);

            return new ResolvedRefs
            {
                BaseLabel = baseLabel,
                PrLabel = prLabel,
                BaseSha = baseSha,
                PrSha = prSha,
                MergeBaseSha = mergeBaseSha,
                ChangedFiles = changedFiles,
                PrCommitCount = prCommitCount,
                IsCi = isCi,
                PullRequestId = pullRequestId,
            };
        }

        private static int MaximumChangedFiles()
        {
            const int defaultMaximum = 250;
            string? configured = Environment.GetEnvironmentVariable("BEHAVIORDIFF_MAX_CHANGED_FILES");
            if (configured is null)
            {
                return defaultMaximum;
            }

            if (!int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out int maximum)
                || maximum < 1)
            {
                throw new CliException("BEHAVIORDIFF_MAX_CHANGED_FILES must be a positive integer.", ExitCodes.RunInvalid);
            }

            return maximum;
        }

        private static string Commit(string repository, string reference) =>
            Shell.Git(repository, "rev-parse", reference + "^{commit}");

        private static void ValidateFetchedBranch(
            string repository,
            string branch,
            string expectedSha,
            string role)
        {
            foreach (string candidate in BranchCandidates(branch))
            {
                ProcessResult result = Shell.Run(
                    "git",
                    new[] { "rev-parse", "--verify", candidate + "^{commit}" },
                    repository);

                if (!result.Ok)
                {
                    continue;
                }

                string actualSha = result.Output.Trim();
                if (!string.Equals(actualSha, expectedSha, StringComparison.Ordinal))
                {
                    throw new CliException(
                        "The fetched " + role + " branch " + candidate + " resolves to " + actualSha
                        + " but the PR merge commit names " + expectedSha + ". Refusing stale or mismatched refs.",
                        ExitCodes.RunInvalid);
                }

                return;
            }

            Console.WriteLine("  NOTE: " + role + " branch " + branch
                + " is not fetched; using its immutable parent from BUILD_SOURCEVERSION.");
        }

        private static IEnumerable<string> BranchCandidates(string branch)
        {
            yield return branch;
            const string heads = "refs/heads/";
            if (branch.StartsWith(heads, StringComparison.Ordinal))
            {
                yield return "refs/remotes/origin/" + branch.Substring(heads.Length);
            }
        }

        private static string RequiredEnvironment(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CliException(
                    "--ci=azuredevops requires " + name + ". Azure Pipelines maps dotted predefined "
                    + "variables to uppercase environment names with periods removed in favor of underscores.",
                    ExitCodes.RunInvalid);
            }

            return value;
        }
    }
}