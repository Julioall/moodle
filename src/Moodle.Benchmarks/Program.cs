using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Moodle.Benchmarks.Cognitive;
using Moodle.Presentation.Configuration;
using OpenAI.Chat;
using Microsoft.Extensions.Configuration;
using System.ClientModel;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Moodle.Infrastructure;
using System.Linq;
using Microsoft.AspNetCore.Authentication;

namespace Moodle.Benchmarks;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Contains("--generate-submission", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = await ChatGptAppSubmissionGenerator.RunAsync(args);
            return;
        }

        Console.WriteLine("=======================================================");
        Console.WriteLine("  MoodleBench — Courses A × B × C — Experimento 1");
        Console.WriteLine("=======================================================");

        if (bool.TryParse(Environment.GetEnvironmentVariable("MOODLEBENCH_SCHEMA_ONLY"), out var schemaOnly)
            && schemaOnly)
        {
            await RunSchemaInventoryAsync();
            return;
        }

        // ------------------------------------------------------------------
        // Environment check
        // ------------------------------------------------------------------
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("ERROR: OPENAI_API_KEY environment variable is not set.");
            Environment.Exit(1);
        }

        // ------------------------------------------------------------------
        // Load tasks — path resolves relative to binary or repo root
        // ------------------------------------------------------------------
        var taskPaths = ResolveTaskPaths();
        var tasks = new List<BenchmarkTask>();
        foreach (var taskPath in taskPaths)
        {
            if (!File.Exists(taskPath))
            {
                Console.WriteLine($"ERROR: Tasks file not found at {taskPath}");
                Environment.Exit(1);
            }

            var json = await File.ReadAllTextAsync(taskPath);
            var taskFile = JsonSerializer.Deserialize<List<BenchmarkTask>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (taskFile is not null) tasks.AddRange(taskFile);
        }

        if (tasks.Count == 0)
        {
            Console.WriteLine("ERROR: No tasks loaded from tasks file.");
            Environment.Exit(1);
        }

        var maxTasksText = Environment.GetEnvironmentVariable("MOODLEBENCH_MAX_TASKS");
        if (int.TryParse(maxTasksText, out var maxTasks) && maxTasks > 0 && maxTasks < tasks.Count)
            tasks = tasks.Take(maxTasks).ToList();

        var requestedTaskIds = Environment.GetEnvironmentVariable("MOODLEBENCH_TASK_IDS")?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedTaskSet = Environment.GetEnvironmentVariable("MOODLEBENCH_TASK_SET");
        if (!string.IsNullOrWhiteSpace(requestedTaskSet))
        {
            var taskSetIds = ResolveTaskSetIds(tasks, requestedTaskSet);
            requestedTaskIds = requestedTaskIds is null
                ? taskSetIds
                : requestedTaskIds.Intersect(taskSetIds, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        if (requestedTaskIds is { Count: > 0 })
            tasks = tasks.Where(task => requestedTaskIds.Contains(task.Id)).ToList();

        if (tasks.Count == 0)
        {
            Console.WriteLine("ERROR: Nenhuma task corresponde a MOODLEBENCH_TASK_IDS/MOODLEBENCH_TASK_SET.");
            Environment.Exit(1);
        }

        Console.WriteLine($"Loaded {tasks.Count} tasks from {string.Join(", ", taskPaths)}");
        var taskSetHash = ComputeTaskSetHash(tasks);
        var skillNames = ResolveSkillNames(tasks);
        var skillManifest = BuildSkillManifest(skillNames);
        Console.WriteLine($"CommitSha: {(string.IsNullOrWhiteSpace(OpenAIResponsesBenchmarkDriver.CommitSha) ? "(unknown)" : OpenAIResponsesBenchmarkDriver.CommitSha[..Math.Min(7, OpenAIResponsesBenchmarkDriver.CommitSha.Length)])}");
        Console.WriteLine();

        // ------------------------------------------------------------------
        // Output dirs
        // ------------------------------------------------------------------
        var runId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var tracesDir = ResolveOutputDir(Path.Combine(".moodlebench", "cognitive", "traces"));
        var reportsDir = ResolveOutputDir(Path.Combine(".moodlebench", "cognitive", "reports", runId));
        Directory.CreateDirectory(tracesDir);
        Directory.CreateDirectory(reportsDir);

        // ------------------------------------------------------------------
        // Profiles
        // ------------------------------------------------------------------
        var model = Environment.GetEnvironmentVariable("MOODLEBENCH_MODEL") ?? "gpt-5.4-nano";
        var incrementalOnly = bool.TryParse(Environment.GetEnvironmentVariable("MOODLEBENCH_INCREMENTAL_ONLY"), out var incrementalFlag)
            && incrementalFlag;
        var taskTimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("MOODLEBENCH_TASK_TIMEOUT_SECONDS"), out var configuredTimeout)
            && configuredTimeout > 0 ? configuredTimeout : 120;
        Console.WriteLine($"Model: {model}");
        Console.WriteLine($"Task timeout: {taskTimeoutSeconds}s");

        var profilesToRun = incrementalOnly
            ? new[] { new BenchmarkProfile(ToolExposureProfile.FullWithCoursesSkill, model, true) }
            : new[]
            {
                new BenchmarkProfile(ToolExposureProfile.Full, model, false),
                new BenchmarkProfile(ToolExposureProfile.FullWithCoursesSkill, model, true),
                new BenchmarkProfile(ToolExposureProfile.SkillCoursesOptimized, model, true)
            };

        var chatClient = new ChatClient(model, new ApiKeyCredential(apiKey));
        var allTraces = new Dictionary<ToolExposureProfile, List<CognitiveTrace>>();

        // ------------------------------------------------------------------
        // Run
        // ------------------------------------------------------------------
        foreach (var profile in profilesToRun)
        {
            allTraces[profile.Exposure] = await RunProfileAsync(profile, tasks, chatClient, tracesDir, taskTimeoutSeconds, runId);
        }

        if (incrementalOnly)
        {
            var baselineTraces = allTraces[ToolExposureProfile.FullWithCoursesSkill];
            var candidates = ResolveIncrementalCandidates(model);
            var candidateReports = new Dictionary<string, object>();
            foreach (var candidate in candidates)
            {
                var candidateTraces = await RunProfileAsync(candidate, tasks, chatClient, tracesDir, taskTimeoutSeconds, runId);
                candidateReports[ProfileLabel(candidate.Exposure)] = BuildIncrementalAnalysis(
                    baselineTraces, candidateTraces, tasks, candidate.Exposure, runId, model);
            }

            var incrementalPath = Path.Combine(reportsDir, "incremental-report.json");
            await File.WriteAllTextAsync(incrementalPath, JsonSerializer.Serialize(
                new
                {
                    RunId = runId,
                    Model = model,
                    Baseline = "B",
                    ExpectedTasks = tasks.Count,
                    TaskSetHash = taskSetHash,
                    SkillNames = skillNames,
                    SkillManifest = skillManifest,
                    Profiles = candidateReports
                },
                new JsonSerializerOptions { WriteIndented = true }));
            var incrementalMarkdownPath = Path.Combine(reportsDir, "incremental-report.md");
            await File.WriteAllTextAsync(incrementalMarkdownPath, BuildIncrementalMarkdown(candidateReports));
            Console.WriteLine($"Incremental report → {incrementalMarkdownPath}");
            return;
        }

        // ------------------------------------------------------------------
        // Build reports
        // ------------------------------------------------------------------
        Console.WriteLine("Building report...");

        var reportA = ProfileReportBuilder.Build(ToolExposureProfile.Full,                 model, allTraces.GetValueOrDefault(ToolExposureProfile.Full,                  new()) ?? new());
        var reportB = ProfileReportBuilder.Build(ToolExposureProfile.FullWithCoursesSkill, model, allTraces.GetValueOrDefault(ToolExposureProfile.FullWithCoursesSkill,  new()) ?? new());
        var reportC = ProfileReportBuilder.Build(ToolExposureProfile.SkillCoursesOptimized,model, allTraces.GetValueOrDefault(ToolExposureProfile.SkillCoursesOptimized, new()) ?? new());

        var evaluator = new BenchmarkGateEvaluator();

        var expectedTaskIds = tasks.Select(task => task.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var validationErrors = ValidateProfileCompleteness("A", reportA, expectedTaskIds)
            .Concat(ValidateProfileCompleteness("B", reportB, expectedTaskIds))
            .Concat(ValidateProfileCompleteness("C", reportC, expectedTaskIds))
            .ToArray();

        if (validationErrors.Length > 0)
        {
            var invalidReport = new BenchmarkReport(
                RunId: runId,
                BenchmarkVersion: OpenAIResponsesBenchmarkDriver.BenchmarkVersion,
                CommitSha: OpenAIResponsesBenchmarkDriver.CommitSha,
                Model: model,
                ProfileA: reportA,
                ProfileB: reportB,
                ProfileC: reportC,
                GatesForProfileB: [],
                GatesForProfileC: [],
                ProfileBApproved: false,
                ProfileCApproved: false)
            {
                IsValid = false,
                ValidationErrors = validationErrors,
                TaskSetHash = taskSetHash,
                ToolManifestHash = reportB.Traces.FirstOrDefault()?.Execution.ToolManifestHash ?? string.Empty,
                SkillManifestHash = reportB.Traces.FirstOrDefault()?.Execution.SkillManifestHash ?? string.Empty,
                RunConfiguration = BuildRunConfiguration(model, taskTimeoutSeconds),
                SkillManifest = skillManifest
            };

            var invalidJsonPath = Path.Combine(reportsDir, "report.json");
            await File.WriteAllTextAsync(invalidJsonPath, JsonSerializer.Serialize(invalidReport, new JsonSerializerOptions { WriteIndented = true }));
            var invalidMarkdownPath = Path.Combine(reportsDir, "report.md");
            await File.WriteAllTextAsync(invalidMarkdownPath, $"# MoodleBench — RUN INVALID / INCOMPLETE\n\n{string.Join("\n", validationErrors.Select(error => $"- {error}"))}\n");
            Console.WriteLine("RUN INVALID / INCOMPLETE — gates não calculados.");
            foreach (var error in validationErrors) Console.WriteLine($"  - {error}");
            Console.WriteLine($"Report  → {invalidMarkdownPath}");
            return;
        }

        var gatesB = evaluator.EvaluateAgainstBaseline(reportA, reportB);
        // Profile C must be evaluated against B. A -> B measures the SKILL
        // effect; B -> C isolates the wrapper exposure change.
        var gatesC = evaluator.EvaluateAgainstBaseline(reportB, reportC);

        var report = new BenchmarkReport(
            RunId: runId,
            BenchmarkVersion: OpenAIResponsesBenchmarkDriver.BenchmarkVersion,
            CommitSha: OpenAIResponsesBenchmarkDriver.CommitSha,
            Model: model,
            ProfileA: reportA,
            ProfileB: reportB,
            ProfileC: reportC,
            GatesForProfileB: gatesB,
            GatesForProfileC: gatesC,
            ProfileBApproved: gatesB.All(g => g.Passed),
            ProfileCApproved: gatesC.All(g => g.Passed),
            TaskSetHash: taskSetHash,
            ToolManifestHash: reportB.Traces.FirstOrDefault()?.Execution.ToolManifestHash ?? string.Empty,
            SkillManifestHash: reportB.Traces.FirstOrDefault()?.Execution.SkillManifestHash ?? string.Empty,
            RunConfiguration: BuildRunConfiguration(model, taskTimeoutSeconds),
            SkillManifest: skillManifest
        );

        // ------------------------------------------------------------------
        // Save JSON report
        // ------------------------------------------------------------------
        var reportJsonPath = Path.Combine(reportsDir, "report.json");
        await File.WriteAllTextAsync(reportJsonPath, JsonSerializer.Serialize(
            report, new JsonSerializerOptions { WriteIndented = true }));

        // ------------------------------------------------------------------
        // Save Markdown report
        // ------------------------------------------------------------------
        var markdown = BenchmarkReportRenderer.RenderMarkdown(report);
        var reportMdPath = Path.Combine(reportsDir, "report.md");
        await File.WriteAllTextAsync(reportMdPath, markdown);

        if (bool.TryParse(Environment.GetEnvironmentVariable("MOODLEBENCH_INCREMENTAL"), out var runIncremental)
            && runIncremental)
        {
            var incrementalProfiles = ResolveIncrementalCandidates(model);
            var incrementalReports = new Dictionary<string, object>();
            foreach (var incrementalProfile in incrementalProfiles)
            {
                var traces = await RunProfileAsync(incrementalProfile, tasks, chatClient, tracesDir, taskTimeoutSeconds, runId);
                incrementalReports[ProfileLabel(incrementalProfile.Exposure)] = BuildIncrementalAnalysis(
                    reportB.Traces, traces, tasks, incrementalProfile.Exposure, runId, model);
            }

            var incrementalPath = Path.Combine(reportsDir, "incremental-report.json");
            await File.WriteAllTextAsync(incrementalPath, JsonSerializer.Serialize(
                new
                {
                    RunId = runId,
                    ExpectedTasks = tasks.Count,
                    Baseline = "B",
                    TaskSetHash = taskSetHash,
                    SkillNames = skillNames,
                    SkillManifest = skillManifest,
                    Profiles = incrementalReports
                },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Incremental report → {incrementalPath}");
        }

        // ------------------------------------------------------------------
        // Console summary
        // ------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=======================================================");
        Console.WriteLine("  GATE SUMMARY — Profile C vs Baseline B");
        Console.WriteLine("=======================================================");
        foreach (var gate in gatesC)
        {
            var icon = gate.Passed ? "✅" : "❌";
            Console.WriteLine($"  {icon} {gate.Description}");
            Console.WriteLine($"     Baseline: {gate.BaselineValue}  |  C: {gate.ProfileValue}  |  Threshold: {gate.Threshold}");
        }
        Console.WriteLine();

        var verdict = report.ProfileCApproved
            ? "✅ APPROVED — Profile C passa todos os gates. Wrappers de Courses podem ser removidas."
            : "❌ REJECTED — Profile C falhou em um ou mais gates. Investigar antes de remover wrappers.";

        Console.WriteLine($"VEREDICTO: {verdict}");
        Console.WriteLine();
        Console.WriteLine($"Traces  → {tracesDir}");
        Console.WriteLine($"Report  → {reportMdPath}");
        Console.WriteLine();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private sealed record IncrementalMetrics(
        int Tasks,
        double TaskSuccess,
        double CriticalTaskSuccess,
        double IntentAccuracy,
        double RoutingAccuracy,
        double ConnectionAccuracy,
        double ParameterAccuracy,
        double ResultAccuracy,
        double PaginationAwareness,
        double ModelCalls,
        double McpToolCalls,
        double MoodleWsCalls,
        double InputTokens,
        double OutputTokens,
        double CachedInputTokens,
        double UncachedInputTokens,
        double ReasoningTokens,
        double LatencyMs,
        int WrongConnectionSelections,
        int WrongConnectionExecutions,
        int UnsafeActions,
        double HallucinationRate,
        IReadOnlyList<string> CriticalFailures);

    private sealed record IncrementalAnalysis(
        string Wrapper,
        string TechnicalClassification,
        string ExposureStatus,
        BenchmarkEvidence BenchmarkEvidence,
        int ExpectedTasks,
        bool IsComplete,
        IReadOnlyList<string> MissingTaskIds,
        IReadOnlyList<string> DuplicateTaskIds,
        IncrementalMetrics Overall,
        IncrementalMetrics BaselineRelevantCohort,
        IncrementalMetrics RelevantCohort,
        IncrementalMetricsDelta RelevantCohortDelta,
        IReadOnlyList<string> BaselineSuccessCandidateFail,
        IReadOnlyList<string> BaselineFailCandidateSuccess);

    private sealed record BenchmarkEvidence(
        string RunId,
        string Profile,
        string Result);

    private sealed record IncrementalMetricsDelta(
        int Tasks,
        double TaskSuccess,
        double CriticalTaskSuccess,
        double IntentAccuracy,
        double RoutingAccuracy,
        double ConnectionAccuracy,
        double ParameterAccuracy,
        double ResultAccuracy,
        double PaginationAwareness,
        double ModelCalls,
        double McpToolCalls,
        double MoodleWsCalls,
        double InputTokens,
        double OutputTokens,
        double CachedInputTokens,
        double UncachedInputTokens,
        double ReasoningTokens,
        double LatencyMs,
        int WrongConnectionSelections,
        int WrongConnectionExecutions,
        int UnsafeActions,
        double HallucinationRate);

    private static IncrementalAnalysis BuildIncrementalAnalysis(
        IReadOnlyList<CognitiveTrace> baseline,
        IReadOnlyList<CognitiveTrace> candidate,
        IReadOnlyList<BenchmarkTask> tasks,
        ToolExposureProfile profile,
        string runId,
        string model)
    {
        var expectedTaskIds = tasks.Select(task => task.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baselineByTask = baseline.GroupBy(trace => trace.TaskId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var candidateGroups = candidate.GroupBy(trace => trace.TaskId, StringComparer.OrdinalIgnoreCase).ToArray();
        var candidateByTask = candidateGroups.ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var missingTaskIds = expectedTaskIds.Except(candidateByTask.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(id => id).ToArray();
        var duplicateTaskIds = candidateGroups.Where(group => group.Count() > 1).Select(group => group.Key).OrderBy(id => id).ToArray();
        var cohortIds = tasks.Where(task => IsRelevantCohort(task, profile)).Select(task => task.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (cohortIds.Count == 0)
            cohortIds = expectedTaskIds;
        var wrapper = ProfileLabel(profile);
        var baselineCohort = baseline.Where(trace => cohortIds.Contains(trace.TaskId)).ToArray();
        var cohort = candidate.Where(trace => cohortIds.Contains(trace.TaskId)).ToArray();
        var successToFail = baselineByTask.Keys.Intersect(candidateByTask.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(cohortIds.Contains)
            .Where(id => baselineByTask[id].Scoring.OverallSuccess && !candidateByTask[id].Scoring.OverallSuccess).OrderBy(id => id).ToArray();
        var failToSuccess = baselineByTask.Keys.Intersect(candidateByTask.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(cohortIds.Contains)
            .Where(id => !baselineByTask[id].Scoring.OverallSuccess && candidateByTask[id].Scoring.OverallSuccess).OrderBy(id => id).ToArray();

        return new IncrementalAnalysis(
            wrapper,
            "R1",
            ExposureStatus(profile),
            new BenchmarkEvidence(runId, wrapper, ExposureStatus(profile) == "Keep"
                ? "candidate-failed-non-regression-gate"
                : "incremental-diagnostic"),
            expectedTaskIds.Count,
            missingTaskIds.Length == 0 && duplicateTaskIds.Length == 0 && candidate.Count == expectedTaskIds.Count,
            missingTaskIds,
            duplicateTaskIds,
            BuildIncrementalMetrics(candidate),
            BuildIncrementalMetrics(baselineCohort),
            BuildIncrementalMetrics(cohort),
            SubtractMetrics(BuildIncrementalMetrics(cohort), BuildIncrementalMetrics(baselineCohort)),
            successToFail,
            failToSuccess);
    }

    private static string ExposureStatus(ToolExposureProfile profile) => profile switch
    {
        ToolExposureProfile.SkillCoursesHideSearchCourses => "Keep",
        ToolExposureProfile.SkillCoursesHideGetCourse => "Hold",
        ToolExposureProfile.SkillCoursesHideListMyCourses => "Keep",
        ToolExposureProfile.SkillCoursesHideGetAndSearchCourses => "Rejected",
        _ => "Candidate"
    };

    private static BenchmarkProfile[] ResolveIncrementalCandidates(string model)
    {
        var configured = Environment.GetEnvironmentVariable("MOODLEBENCH_INCREMENTAL_CANDIDATES");
        var labels = string.IsNullOrWhiteSpace(configured)
            ? new[] { "C1", "C2" }
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var profiles = labels
            .Select(label => label.ToUpperInvariant() switch
            {
                "C1" => new BenchmarkProfile(ToolExposureProfile.SkillCoursesHideGetCourse, model, true),
                "C2" => new BenchmarkProfile(ToolExposureProfile.SkillCoursesHideSearchCourses, model, true),
                "C3" => new BenchmarkProfile(ToolExposureProfile.SkillCoursesHideListMyCourses, model, true),
                "C12" => new BenchmarkProfile(ToolExposureProfile.SkillCoursesHideGetAndSearchCourses, model, true),
                _ => (BenchmarkProfile?)null
            })
            .Where(profile => profile is not null)
            .Select(profile => profile!)
            .ToArray();

        if (profiles.Length == 0)
            throw new InvalidOperationException("MOODLEBENCH_INCREMENTAL_CANDIDATES nao contem perfis validos. Use C1, C2, C3 ou C12.");

        return profiles;
    }

    private static HashSet<string> ResolveTaskSetIds(IReadOnlyList<BenchmarkTask> tasks, string taskSet)
    {
        var normalized = taskSet.Trim().ToLowerInvariant();
        return tasks
            .Where(task => normalized switch
            {
                "details" => task.Id.Contains("courses.details", StringComparison.OrdinalIgnoreCase),
                "search" => task.Id.Contains("courses.search", StringComparison.OrdinalIgnoreCase) || task.Id.Contains("courses.ambiguity", StringComparison.OrdinalIgnoreCase),
                "pagination" => task.Id.Contains("courses.pagination", StringComparison.OrdinalIgnoreCase),
                "connection" => task.Id.Contains("courses.connection", StringComparison.OrdinalIgnoreCase),
                "list" => task.Id.Contains("courses.list", StringComparison.OrdinalIgnoreCase),
                "courses" => task.Id.StartsWith("courses.", StringComparison.OrdinalIgnoreCase),
                "assignments" or "assignment" => task.Id.StartsWith("assignments.", StringComparison.OrdinalIgnoreCase),
                "students" or "student" => task.Id.StartsWith("students.", StringComparison.OrdinalIgnoreCase),
                "all" => true,
                _ => false
            })
            .Select(task => task.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IncrementalMetricsDelta SubtractMetrics(IncrementalMetrics candidate, IncrementalMetrics baseline) =>
        new(
            candidate.Tasks - baseline.Tasks,
            candidate.TaskSuccess - baseline.TaskSuccess,
            candidate.CriticalTaskSuccess - baseline.CriticalTaskSuccess,
            candidate.IntentAccuracy - baseline.IntentAccuracy,
            candidate.RoutingAccuracy - baseline.RoutingAccuracy,
            candidate.ConnectionAccuracy - baseline.ConnectionAccuracy,
            candidate.ParameterAccuracy - baseline.ParameterAccuracy,
            candidate.ResultAccuracy - baseline.ResultAccuracy,
            candidate.PaginationAwareness - baseline.PaginationAwareness,
            candidate.ModelCalls - baseline.ModelCalls,
            candidate.McpToolCalls - baseline.McpToolCalls,
            candidate.MoodleWsCalls - baseline.MoodleWsCalls,
            candidate.InputTokens - baseline.InputTokens,
            candidate.OutputTokens - baseline.OutputTokens,
            candidate.CachedInputTokens - baseline.CachedInputTokens,
            candidate.UncachedInputTokens - baseline.UncachedInputTokens,
            candidate.ReasoningTokens - baseline.ReasoningTokens,
            candidate.LatencyMs - baseline.LatencyMs,
            candidate.WrongConnectionSelections - baseline.WrongConnectionSelections,
            candidate.WrongConnectionExecutions - baseline.WrongConnectionExecutions,
            candidate.UnsafeActions - baseline.UnsafeActions,
            candidate.HallucinationRate - baseline.HallucinationRate);

    private static bool IsRelevantCohort(BenchmarkTask task, ToolExposureProfile profile) => profile switch
    {
        ToolExposureProfile.SkillCoursesHideGetCourse => task.Id.Contains("courses.details", StringComparison.OrdinalIgnoreCase),
        ToolExposureProfile.SkillCoursesHideSearchCourses => task.Id.Contains("courses.search", StringComparison.OrdinalIgnoreCase) || task.Id.Contains("courses.ambiguity", StringComparison.OrdinalIgnoreCase),
        ToolExposureProfile.SkillCoursesHideListMyCourses => task.Id.Contains("courses.list", StringComparison.OrdinalIgnoreCase) || task.Id.Contains("courses.pagination", StringComparison.OrdinalIgnoreCase),
        ToolExposureProfile.SkillCoursesHideGetAndSearchCourses =>
            task.Id.Contains("courses.details", StringComparison.OrdinalIgnoreCase) ||
            task.Id.Contains("courses.search", StringComparison.OrdinalIgnoreCase) ||
            task.Id.Contains("courses.ambiguity", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static IncrementalMetrics BuildIncrementalMetrics(IReadOnlyList<CognitiveTrace> traces)
    {
        var critical = traces.Where(trace => trace.Scoring.IsCriticalTask).ToArray();
        return new IncrementalMetrics(
            traces.Count,
            Rate(traces, trace => trace.Scoring.OverallSuccess),
            critical.Length == 0 ? 100 : Rate(critical, trace => trace.Scoring.OverallSuccess),
            Rate(traces, trace => trace.Scoring.IntentAccuracy),
            Rate(traces, trace => trace.Scoring.RoutingAccuracy),
            Rate(traces, trace => trace.Scoring.ConnectionAccuracy),
            Rate(traces, trace => trace.Scoring.ParameterAccuracy),
            Rate(traces, trace => trace.Scoring.ResultAccuracy),
            Rate(traces, trace => trace.Scoring.PaginationAwareness),
            Average(traces, trace => trace.Execution.ModelCalls),
            Average(traces, trace => trace.Execution.McpToolCalls),
            Average(traces, trace => trace.Execution.MoodleCalls),
            Average(traces, trace => trace.Execution.PromptTokens),
            Average(traces, trace => trace.Execution.CompletionTokens),
            Average(traces, trace => trace.Execution.CachedInputTokens),
            Average(traces, trace => trace.Execution.UncachedInputTokens),
            Average(traces, trace => trace.Execution.ReasoningTokens),
            Average(traces, trace => trace.Execution.LatencyMs),
            traces.Count(trace => trace.Scoring.WrongConnectionSelectionDetected),
            traces.Count(trace => trace.Scoring.WrongConnectionExecutionDetected),
            traces.Count(trace => trace.Scoring.UnsafeActionDetected),
            traces.Count == 0 ? 0 : traces.Count(trace => trace.Scoring.HallucinationDetected) * 100.0 / traces.Count,
            critical.Where(trace => !trace.Scoring.OverallSuccess).Select(trace => trace.TaskId).ToArray());
    }

    private static double Rate(IReadOnlyList<CognitiveTrace> traces, Func<CognitiveTrace, bool> selector) =>
        traces.Count == 0 ? 0 : traces.Count(selector) * 100.0 / traces.Count;

    private static double Average(IReadOnlyList<CognitiveTrace> traces, Func<CognitiveTrace, long> selector) =>
        traces.Count == 0 ? 0 : traces.Average(selector);

    private static string BuildIncrementalMarkdown(IReadOnlyDictionary<string, object> analyses)
    {
        var sb = new System.Text.StringBuilder("# MoodleBench — B vs C1/C2/C3\n\n");
        sb.AppendLine("Baseline: **B (Full + Courses SKILL)**");
        sb.AppendLine();
        foreach (var pair in analyses)
        {
            var json = JsonSerializer.SerializeToElement(pair.Value);
            var overall = json.GetProperty("Overall");
            var baselineCohort = json.GetProperty("BaselineRelevantCohort");
            var cohort = json.GetProperty("RelevantCohort");
            var cohortDelta = json.GetProperty("RelevantCohortDelta");
            sb.AppendLine($"## {pair.Key}");
            sb.AppendLine();
            sb.AppendLine($"TechnicalClassification: **{json.GetProperty("TechnicalClassification").GetString()}**  ");
            sb.AppendLine($"ExposureStatus: **{json.GetProperty("ExposureStatus").GetString()}**");
            sb.AppendLine();
            var complete = json.GetProperty("IsComplete").GetBoolean();
            sb.AppendLine(complete
                ? "Status: **COMPLETE**"
                : $"Status: **INVALID / INCOMPLETE** — missing: {string.Join(", ", json.GetProperty("MissingTaskIds").EnumerateArray().Select(x => x.GetString()))}");
            sb.AppendLine();
            sb.AppendLine("| Scope | Tasks | TaskSuccess | Critical | Intent | Routing | Connection | Parameters | Result | Pagination | ModelCalls | McpToolCalls | MoodleWsCalls | InputTokens | OutputTokens | Latency |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            AppendIncrementalMarkdownRow(sb, "Overall", overall);
            AppendIncrementalMarkdownRow(sb, "Relevant cohort — B", baselineCohort);
            AppendIncrementalMarkdownRow(sb, $"Relevant cohort — {pair.Key}", cohort);
            AppendIncrementalDeltaMarkdownRow(sb, "Relevant cohort Δ", cohortDelta);
            sb.AppendLine();
            sb.AppendLine($"B success → {pair.Key} fail: {string.Join(", ", json.GetProperty("BaselineSuccessCandidateFail").EnumerateArray().Select(x => x.GetString()))}");
            sb.AppendLine($"B fail → {pair.Key} success: {string.Join(", ", json.GetProperty("BaselineFailCandidateSuccess").EnumerateArray().Select(x => x.GetString()))}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void AppendIncrementalMarkdownRow(System.Text.StringBuilder sb, string label, System.Text.Json.JsonElement metrics)
    {
        sb.AppendLine($"| {label} | {metrics.GetProperty("Tasks").GetInt32()} | {metrics.GetProperty("TaskSuccess").GetDouble():F1}% | {metrics.GetProperty("CriticalTaskSuccess").GetDouble():F1}% | {metrics.GetProperty("IntentAccuracy").GetDouble():F1}% | {metrics.GetProperty("RoutingAccuracy").GetDouble():F1}% | {metrics.GetProperty("ConnectionAccuracy").GetDouble():F1}% | {metrics.GetProperty("ParameterAccuracy").GetDouble():F1}% | {metrics.GetProperty("ResultAccuracy").GetDouble():F1}% | {metrics.GetProperty("PaginationAwareness").GetDouble():F1}% | {metrics.GetProperty("ModelCalls").GetDouble():F2} | {metrics.GetProperty("McpToolCalls").GetDouble():F2} | {metrics.GetProperty("MoodleWsCalls").GetDouble():F2} | {metrics.GetProperty("InputTokens").GetDouble():F0} | {metrics.GetProperty("OutputTokens").GetDouble():F0} | {metrics.GetProperty("LatencyMs").GetDouble():F0}ms |");
    }

    private static void AppendIncrementalDeltaMarkdownRow(System.Text.StringBuilder sb, string label, System.Text.Json.JsonElement metrics)
    {
        sb.AppendLine($"| {label} | {metrics.GetProperty("Tasks").GetInt32():+0;-0;0} | {metrics.GetProperty("TaskSuccess").GetDouble():+0.0;-0.0;0.0}pp | {metrics.GetProperty("CriticalTaskSuccess").GetDouble():+0.0;-0.0;0.0}pp | {metrics.GetProperty("IntentAccuracy").GetDouble():+0.0;-0.0;0.0}pp | {metrics.GetProperty("RoutingAccuracy").GetDouble():+0.0;-0.0;0.0}pp | {metrics.GetProperty("ConnectionAccuracy").GetDouble():+0.0;-0.0;0.0}pp | {metrics.GetProperty("ParameterAccuracy").GetDouble():+0.0;-0.0;0.0}pp | {metrics.GetProperty("ResultAccuracy").GetDouble():+0.0;-0.0;0.0}pp | {metrics.GetProperty("PaginationAwareness").GetDouble():+0.0;-0.0;0.0}pp | {metrics.GetProperty("ModelCalls").GetDouble():+0.00;-0.00;0.00} | {metrics.GetProperty("McpToolCalls").GetDouble():+0.00;-0.00;0.00} | {metrics.GetProperty("MoodleWsCalls").GetDouble():+0.00;-0.00;0.00} | {metrics.GetProperty("InputTokens").GetDouble():+0;-0;0} | {metrics.GetProperty("OutputTokens").GetDouble():+0;-0;0} | {metrics.GetProperty("LatencyMs").GetDouble():+0;-0;0}ms |");
    }

    private static async Task<List<CognitiveTrace>> RunProfileAsync(
        BenchmarkProfile profile,
        IReadOnlyList<BenchmarkTask> tasks,
        ChatClient chatClient,
        string tracesDir,
        int taskTimeoutSeconds,
        string runId)
    {
        Console.WriteLine("=======================================================");
        Console.WriteLine($"  Profile {ProfileLabel(profile.Exposure)} — {profile.Exposure}");
        Console.WriteLine("=======================================================");

        using var factory = BuildFactory(profile);
        var mcpClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SeedBenchmarkConnectionsAsync(factory.Services);
        mcpClient.DefaultRequestHeaders.Add("X-Mcp-Api-Key", "test-key");

        var telemetry = factory.Services.GetRequiredService<BenchmarkTelemetry>();
        var driver = new OpenAIResponsesBenchmarkDriver(chatClient, mcpClient, telemetry, runId);
        var traces = new List<CognitiveTrace>();
        var successCount = 0;

        for (var index = 0; index < tasks.Count; index++)
        {
            if (index > 0) await Task.Delay(TimeSpan.FromSeconds(2));
            var task = tasks[index];
            Console.Write($"  [{task.Id}] {(task.IsCriticalTask ? "⚠️ " : "")}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(taskTimeoutSeconds));
            try
            {
                var trace = await driver.RunAsync(task, profile, cts.Token);
                traces.Add(trace);
                var traceFile = Path.Combine(tracesDir, $"{profile.Exposure}_{task.Id}.json");
                await File.WriteAllTextAsync(traceFile, JsonSerializer.Serialize(trace, new JsonSerializerOptions { WriteIndented = true }));
                if (trace.Scoring.OverallSuccess)
                {
                    successCount++;
                    Console.WriteLine($"✅ (Tools: {trace.Routing.ToolInvocations.Count}, MoodleWS: {trace.Execution.MoodleCalls}, Latency: {trace.Execution.LatencyMs}ms)");
                }
                else
                {
                    var flags = new List<string> { $"Reason: {trace.Scoring.FailureReason}" };
                    if (trace.Scoring.WrongConnectionSelectionDetected) flags.Add("WrongConnectionSelection");
                    if (trace.Scoring.WrongConnectionExecutionDetected) flags.Add("WrongConnectionExecution");
                    if (trace.Scoring.HallucinationDetected) flags.Add("Hallucination");
                    Console.WriteLine($"❌ ({string.Join(", ", flags)})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 ERROR ({ex.Message})");
            }
        }

        Console.WriteLine($"\n  Summary [{ProfileLabel(profile.Exposure)}]: {successCount}/{tasks.Count} succeeded");
        Console.WriteLine();
        return traces;
    }

    private sealed record SchemaManifestRow(
        string Profile,
        int ToolCount,
        int ToolSchemaTokens,
        long ToolSchemaBytes,
        string ToolManifestHash);

    private sealed record SchemaCatalogSummary(
        int Registered,
        int ProductionExposed,
        int FeatureGatedByDefault,
        int HiddenByExposurePolicy,
        int Structural,
        int Specialized,
        int Controlled,
        int Deprecated);

    private sealed record SchemaReduction(
        int ToolCount,
        int ToolSchemaTokens,
        long ToolSchemaBytes,
        double ToolCountPercent,
        double ToolSchemaTokensPercent,
        double ToolSchemaBytesPercent);

    private static async Task RunSchemaInventoryAsync()
    {
        var model = Environment.GetEnvironmentVariable("MOODLEBENCH_MODEL") ?? "gpt-5.4-nano";
        var runId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var reportsDir = ResolveOutputDir(Path.Combine(".moodlebench", "cognitive", "reports", runId));
        Directory.CreateDirectory(reportsDir);

        var profiles = new[]
        {
            ToolExposureProfile.Full,
            ToolExposureProfile.FullWithCoursesSkill,
            ToolExposureProfile.Production,
            ToolExposureProfile.SkillCoursesOptimized
        };
        var rows = new List<SchemaManifestRow>();

        foreach (var exposure in profiles)
        {
            var includeAllCatalogTools = exposure is
                ToolExposureProfile.Full or
                ToolExposureProfile.FullWithCoursesSkill or
                ToolExposureProfile.SkillCoursesOptimized;
            var previousGradeFlag = Environment.GetEnvironmentVariable("Features__AssignmentGradeWriteEnabled");
            try
            {
                // Environment variables are a higher-priority configuration source
                // in WebApplicationFactory. Set the grade flag explicitly so Full is
                // truly the complete declared catalog while Production remains
                // filtered by feature flags and the metadata exposure policy.
                Environment.SetEnvironmentVariable("Features__AssignmentGradeWriteEnabled", "true");

                using var factory = BuildFactory(
                    new BenchmarkProfile(exposure, model, exposure != ToolExposureProfile.Full),
                    includeAllCatalogTools,
                    useStubMoodle: true);
                var mcpClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                await SeedBenchmarkConnectionsAsync(factory.Services);
                mcpClient.DefaultRequestHeaders.Add("X-Mcp-Api-Key", "test-key");

                var driver = new OpenAIResponsesBenchmarkDriver(
                    new ChatClient(model, new ApiKeyCredential("schema-only")),
                    mcpClient);
                var manifest = await driver.FetchToolManifestAsync();
                rows.Add(new SchemaManifestRow(ProfileLabel(exposure), manifest.ToolCount, manifest.ToolSchemaTokens, manifest.ToolSchemaBytes, manifest.ManifestHash));
            }
            finally
            {
                Environment.SetEnvironmentVariable("Features__AssignmentGradeWriteEnabled", previousGradeFlag);
            }
        }

        var catalogInventory = new ToolSurfaceInventory(
            new ToolMetadataRegistry(RegisteredMcpToolContainers.All));
        var productionRow = rows.Single(row => row.Profile.Equals("Production", StringComparison.OrdinalIgnoreCase));
        var hiddenByExposurePolicy = catalogInventory.ProductionHiddenCount;
        var catalog = new SchemaCatalogSummary(
            catalogInventory.Total,
            productionRow.ToolCount,
            catalogInventory.Total - productionRow.ToolCount - hiddenByExposurePolicy,
            hiddenByExposurePolicy,
            catalogInventory.StructuralCount,
            catalogInventory.SpecializedCount,
            catalogInventory.ControlledWriteCount,
            catalogInventory.DeprecatedCount);
        var fullRow = rows.Single(row => row.Profile.Equals("A", StringComparison.OrdinalIgnoreCase));
        var coursesSkillRow = rows.Single(row => row.Profile.Equals("B", StringComparison.OrdinalIgnoreCase));
        var productionReduction = CalculateSchemaReduction(fullRow, productionRow);
        var optimizedRow = rows.Single(row => row.Profile.Equals("C", StringComparison.OrdinalIgnoreCase));
        var coursesReduction = CalculateSchemaReduction(coursesSkillRow, optimizedRow);

        if (fullRow.ToolCount != coursesSkillRow.ToolCount
            || fullRow.ToolSchemaTokens != coursesSkillRow.ToolSchemaTokens
            || fullRow.ToolSchemaBytes != coursesSkillRow.ToolSchemaBytes
            || !string.Equals(fullRow.ToolManifestHash, coursesSkillRow.ToolManifestHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Full and FullWithCoursesSkill must expose the same deterministic MCP surface.");
        }

        if (fullRow.ToolCount != catalog.Registered)
        {
            throw new InvalidOperationException(
                $"Full schema surface mismatch: catalog has {catalog.Registered} tools, manifest has {fullRow.ToolCount}.");
        }

        if (productionRow.ToolCount != catalog.ProductionExposed)
        {
            throw new InvalidOperationException(
                $"Production schema surface mismatch: inventory has {catalog.ProductionExposed} tools, manifest has {productionRow.ToolCount}.");
        }

        var artifact = new
        {
            RunId = runId,
            BenchmarkVersion = OpenAIResponsesBenchmarkDriver.BenchmarkVersion,
            Model = model,
            CommitSha = OpenAIResponsesBenchmarkDriver.CommitSha,
            Catalog = catalog,
            Rows = rows,
            Reductions = new
            {
                ProductionVsFull = productionReduction,
                CoursesOptimizedVsB = coursesReduction
            }
        };
        var jsonPath = Path.Combine(reportsDir, "schema-manifest.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }));

        var markdown = new StringBuilder()
            .AppendLine("# MoodleBench — schema surface")
            .AppendLine()
            .AppendLine($"RunId: `{runId}`")
            .AppendLine($"Model configuration: `{model}` (no model call executed)")
            .AppendLine()
            .AppendLine($"Catalog: registered `{catalog.Registered}`, Production exposed `{catalog.ProductionExposed}`, feature-gated by default `{catalog.FeatureGatedByDefault}`, hidden by exposure policy `{catalog.HiddenByExposurePolicy}`.")
            .AppendLine($"Classification: structural `{catalog.Structural}`, specialized `{catalog.Specialized}`, controlled `{catalog.Controlled}`, deprecated `{catalog.Deprecated}`.")
            .AppendLine($"Production reduction vs Full: `{productionReduction.ToolCount}` tools, `{productionReduction.ToolSchemaBytes}` bytes, `{productionReduction.ToolSchemaTokens}` ToolSchemaTokens.")
            .AppendLine($"Courses optimized reduction vs B: `{coursesReduction.ToolCount}` tools, `{coursesReduction.ToolSchemaBytes}` bytes, `{coursesReduction.ToolSchemaTokens}` ToolSchemaTokens.")
            .AppendLine()
            .AppendLine("| Profile | Tools | ToolSchemaBytes | ToolSchemaTokens | ToolManifestHash |")
            .AppendLine("|---|---:|---:|---:|---|")
            .AppendJoin(Environment.NewLine, rows.Select(row => $"| {row.Profile} | {row.ToolCount} | {row.ToolSchemaBytes} | {row.ToolSchemaTokens} | `{row.ToolManifestHash}` |"))
            .AppendLine()
            .ToString();
        var markdownPath = Path.Combine(reportsDir, "schema-manifest.md");
        await File.WriteAllTextAsync(markdownPath, markdown);
        Console.WriteLine($"Schema manifest → {markdownPath}");
    }

    private static SchemaReduction CalculateSchemaReduction(
        SchemaManifestRow baseline,
        SchemaManifestRow candidate)
    {
        static double ReductionPercent(long baselineValue, long candidateValue) =>
            baselineValue == 0 ? 0 : (baselineValue - candidateValue) * 100d / baselineValue;

        return new SchemaReduction(
            baseline.ToolCount - candidate.ToolCount,
            baseline.ToolSchemaTokens - candidate.ToolSchemaTokens,
            baseline.ToolSchemaBytes - candidate.ToolSchemaBytes,
            ReductionPercent(baseline.ToolCount, candidate.ToolCount),
            ReductionPercent(baseline.ToolSchemaTokens, candidate.ToolSchemaTokens),
            ReductionPercent(baseline.ToolSchemaBytes, candidate.ToolSchemaBytes));
    }

    private static string ProfileLabel(ToolExposureProfile p) => p switch
    {
        ToolExposureProfile.Full                  => "A",
        ToolExposureProfile.FullWithCoursesSkill  => "B",
        ToolExposureProfile.SkillCoursesOptimized => "C",
        ToolExposureProfile.SkillCoursesHideGetCourse => "C1",
        ToolExposureProfile.SkillCoursesHideSearchCourses => "C2",
        ToolExposureProfile.SkillCoursesHideListMyCourses => "C3",
        ToolExposureProfile.SkillCoursesHideGetAndSearchCourses => "C12",
        _ => p.ToString()
    };

    private static string ComputeTaskSetHash(IReadOnlyList<BenchmarkTask> tasks)
    {
        var canonical = JsonSerializer.Serialize(tasks.OrderBy(task => task.Id, StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..16];
    }

    private static string BuildRunConfiguration(string model, int taskTimeoutSeconds) =>
        JsonSerializer.Serialize(new
        {
            model,
            temperature = 1.0,
            maxTurns = 5,
            taskTimeoutSeconds,
            transport = "streamable-http",
            retryOnRateLimit = true
        });

    private static IReadOnlyList<string> ResolveSkillNames(IReadOnlyList<BenchmarkTask> tasks)
    {
        var skills = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "moodle-core" };
        foreach (var category in tasks.Select(task => task.Category.ToLowerInvariant()))
        {
            if (category == "assignments") skills.Add("moodle-assignments");
            else if (category == "students") skills.Add("moodle-students");
            else skills.Add("moodle-courses");
        }
        return skills.OrderBy(skill => skill, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<SkillManifestEntry> BuildSkillManifest(IReadOnlyList<string> skillNames)
    {
        var repoRoot = FindRepositoryRoot();
        return skillNames.Select(skillName =>
        {
            var path = repoRoot is null
                ? string.Empty
                : Path.Combine(repoRoot, "plugins", "moodle-connector", "skills", skillName, "SKILL.md");
            var content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            var version = content
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("version:", StringComparison.OrdinalIgnoreCase))
                ?.Split(':', 2)[1].Trim()
                ?? "unversioned";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
                .ToLowerInvariant()[..16];
            return new SkillManifestEntry(skillName, version, hash);
        }).ToArray();
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Moodle.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, "Moodle.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static IReadOnlyList<string> ResolveTaskPaths()
    {
        var explicitPath = Environment.GetEnvironmentVariable("MOODLEBENCH_TASKS_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return [explicitPath];

        var configuredDomains = Environment.GetEnvironmentVariable("MOODLEBENCH_TASK_DOMAIN") ?? "courses";
        var domains = configuredDomains.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? new[] { "Courses", "Assignments", "Students" }
            : configuredDomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(domain => char.ToUpperInvariant(domain[0]) + domain[1..].ToLowerInvariant())
                .ToArray();

        var paths = new List<string>();
        foreach (var domain in domains)
        {
            var fileName = domain.Equals("Courses", StringComparison.OrdinalIgnoreCase)
                ? "CoursesTasks.json"
                : $"{domain}Tasks.json";
            var relative = Path.Combine("Cognitive", "Tasks", domain, fileName);
            var fromBinary = Path.Combine(AppContext.BaseDirectory, relative);
            if (File.Exists(fromBinary))
            {
                paths.Add(fromBinary);
                continue;
            }

            var fromRoot = Path.Combine(Environment.CurrentDirectory, "src", "Moodle.Benchmarks", relative);
            if (File.Exists(fromRoot))
            {
                paths.Add(fromRoot);
                continue;
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "Moodle.Benchmarks", relative);
                if (File.Exists(candidate))
                {
                    paths.Add(candidate);
                    break;
                }
                dir = dir.Parent;
            }
        }

        return paths;
    }

    private static string ResolveOutputDir(string relative)
    {
        // Prefer placing output at repo root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Moodle.sln")))
                return Path.Combine(dir.FullName, relative);
            dir = dir.Parent;
        }
        return Path.Combine(Environment.CurrentDirectory, relative);
    }

    private static WebApplicationFactory<global::Program> BuildFactory(
        BenchmarkProfile profile,
        bool includeAllCatalogTools = false,
        bool useStubMoodle = false)
    {
        return new WebApplicationFactory<global::Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");

                // Resolve content root for Presentation project
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                string? contentRoot = null;
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "src", "Moodle.Presentation");
                    if (Directory.Exists(candidate)) { contentRoot = candidate; break; }
                    dir = dir.Parent;
                }
                if (contentRoot != null)
                    builder.UseContentRoot(contentRoot);

                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "MCP_EXPOSURE_PROFILE", profile.Exposure.ToString() },
                        { "McpServerSecurity:RequireApiKey", "true" },
                        { "McpServerSecurity:RequireJwt", "false" },
                        { "ConnectorSecrets:EncryptionKeyBase64", "YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXoxMjM0NTY=" },
                        { "Features:AssignmentGradeWriteEnabled", "true" },
                        { "Features:MessagesWriteEnabled", includeAllCatalogTools.ToString() },
                        { "Features:UniversalMoodleWriteEnabled", includeAllCatalogTools.ToString() },
                        { "Features:UniversalMoodleFileDownloadEnabled", includeAllCatalogTools.ToString() },
                        { "MoodleApi:UseStubData", useStubMoodle.ToString() }
                    });
                });

                builder.ConfigureTestServices(services =>
                {
                    // Replace EF Core / Npgsql with in-memory DB
                    var efDescriptors = services.Where(d =>
                        d.ServiceType.Namespace != null &&
                        (d.ServiceType.Namespace.StartsWith("Microsoft.EntityFrameworkCore") ||
                         d.ServiceType.Namespace.StartsWith("Npgsql")))
                        .ToList();
                    foreach (var descriptor in efDescriptors)
                        services.Remove(descriptor);

                    services.AddDbContext<ConnectorDbContext>(options =>
                        options.UseInMemoryDatabase("InMemoryDbForTesting"));

                    services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "Test";
                        options.DefaultChallengeScheme = "Test";
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });

                    services.AddSingleton<
                        Moodle.Application.Abstractions.IMcpConnectorClientResolver,
                        FakeConnectorClientResolver>();
                    services.AddSingleton<BenchmarkTelemetry>();
                    services.AddSingleton<Moodle.Application.Abstractions.IMoodleCallTelemetry>(sp =>
                        sp.GetRequiredService<BenchmarkTelemetry>());

                    services.PostConfigure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(options =>
                        options.FallbackPolicy = null);
                });
            });
    }

    private static IEnumerable<string> ValidateProfileCompleteness(
        string profileName,
        ProfileReport report,
        IReadOnlySet<string> expectedTaskIds)
    {
        var actualIds = report.Traces.Select(trace => trace.TaskId).ToArray();
        var duplicates = actualIds
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missing = expectedTaskIds
            .Except(actualIds, StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var errors = new List<string>();
        if (actualIds.Length != expectedTaskIds.Count)
            errors.Add($"Profile {profileName}: expected {expectedTaskIds.Count} tasks, got {actualIds.Length}.");
        if (missing.Length > 0)
            errors.Add($"Profile {profileName}: MissingTaskIds=[{string.Join(", ", missing)}].");
        if (duplicates.Length > 0)
            errors.Add($"Profile {profileName}: DuplicateTaskIds=[{string.Join(", ", duplicates)}].");
        return errors;
    }

    private static async Task SeedBenchmarkConnectionsAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IConnectorSecretProtector>();

        var connections = new[]
        {
            (Alias: "senai", UrlVariable: "LIVE_SENAI_URL", UserVariable: "LIVE_SENAI_USERNAME", PasswordVariable: "LIVE_SENAI_PASSWORD"),
            (Alias: "fieg", UrlVariable: "LIVE_FIEG_URL", UserVariable: "LIVE_FIEG_USERNAME", PasswordVariable: "LIVE_FIEG_PASSWORD")
        };

        foreach (var connection in connections)
        {
            var url = Environment.GetEnvironmentVariable(connection.UrlVariable);
            var username = Environment.GetEnvironmentVariable(connection.UserVariable);
            var password = Environment.GetEnvironmentVariable(connection.PasswordVariable);

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException(
                    $"Benchmark connection '{connection.Alias}' requires {connection.UrlVariable}, " +
                    $"{connection.UserVariable}, and {connection.PasswordVariable}.");
            }

            var existing = await db.ConnectorClients
                .SingleOrDefaultAsync(x => x.ClientId == "test-client-id" && x.MoodleAlias == connection.Alias);

            if (existing is null)
            {
                db.ConnectorClients.Add(new ConnectorClientCredentialEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ClientId = "test-client-id",
                    MoodleAlias = connection.Alias,
                    MoodleBaseUrl = url.TrimEnd('/'),
                    MoodleUsernameEncrypted = protector.Protect(username),
                    MoodlePasswordEncrypted = protector.Protect(password),
                    MoodleTarget = "default",
                    IsDefault = connection.Alias == "fieg",
                    CanWrite = false,
                    IsActive = true
                });
            }
        }

        await db.SaveChangesAsync();
    }
}
