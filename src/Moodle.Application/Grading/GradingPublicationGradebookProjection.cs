using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Moodle.Application.Abstractions;

namespace Moodle.Application.Grading;

/// <summary>
/// Combines the assignment grade read with the gradebook's feedback field.
/// mod_assign_get_grades is not treated as complete evidence for feedback.
/// </summary>
public static class GradingPublicationGradebookProjection
{
    private static readonly Regex HtmlTagRegex = new("<[^>]*>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static AssignmentExistingGrade? Combine(
        string assignmentId,
        string studentId,
        AssignmentExistingGrade? assignmentGrade,
        CourseGradebook? gradebook)
    {
        if (gradebook is null)
        {
            return null;
        }

        var matches = gradebook.Items
            .Where(item =>
                string.Equals(item.ItemType, "mod", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ItemModule, "assign", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ItemInstance, assignmentId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 || !matches[0].FeedbackFieldPresent)
        {
            return null;
        }

        var gradebookItem = matches[0];
        var hasGrade = assignmentGrade?.HasGrade == true || gradebookItem.GradeRaw is >= 0m;
        var grade = gradebookItem.GradeRaw is >= 0m
            ? gradebookItem.GradeRaw
            : assignmentGrade?.HasGrade == true
                ? assignmentGrade.Grade
                : null;
        var graderId = assignmentGrade?.GraderId;
        if (graderId is null && long.TryParse(gradebookItem.GraderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedGraderId))
        {
            graderId = parsedGraderId;
        }

        return new AssignmentExistingGrade(
            AssignmentId: assignmentId,
            StudentId: studentId,
            Grade: grade,
            HasGrade: hasGrade,
            Feedback: gradebookItem.Feedback,
            GradeMax: assignmentGrade?.GradeMax ?? gradebookItem.GradeMax,
            GraderId: graderId,
            TimeModified: assignmentGrade?.TimeModified,
            FeedbackObserved: true);
    }

    public static bool MatchesApprovedValues(
        GradingLaunchPayloadItem payloadItem,
        AssignmentExistingGrade? existingGrade)
    {
        if (existingGrade is null || !existingGrade.FeedbackObserved)
        {
            return false;
        }

        var operation = ResolveGradeOperation(payloadItem);
        var gradeMatches = operation switch
        {
            "set_grade" => existingGrade.HasGrade && existingGrade.Grade == payloadItem.Grade,
            "preserve_grade" => existingGrade.HasGrade == payloadItem.BaselineHasGrade &&
                                 (!payloadItem.BaselineHasGrade || existingGrade.Grade == payloadItem.BaselineGrade),
            "no_grade" => !existingGrade.HasGrade,
            _ => false
        };
        return gradeMatches && string.Equals(
            NormalizeFeedback(existingGrade.Feedback),
            NormalizeFeedback(payloadItem.FeedbackText),
            StringComparison.Ordinal);
    }

    public static string ComputePreflightHash(
        string connectionKey,
        GradingLaunchPayloadItem payloadItem,
        AssignmentExistingGrade? existingGrade)
    {
        var canonical = string.Join("|",
            connectionKey,
            payloadItem.AssignmentId,
            payloadItem.StudentId,
            payloadItem.AttemptNumber ?? 0,
            existingGrade?.HasGrade == true
                ? existingGrade.Grade?.ToString("0.####", CultureInfo.InvariantCulture) ?? "null"
                : "none",
            NormalizeFeedback(existingGrade?.Feedback),
            existingGrade?.FeedbackObserved == true ? "feedback-observed" : "feedback-unobserved",
            !string.IsNullOrWhiteSpace(existingGrade?.Feedback) ? "feedback" : "no-feedback",
            payloadItem.SubmissionContentHash ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>
    /// Hash format written by previews before feedback normalization was
    /// centralized. Retained only to interpret already-authorized actions.
    /// </summary>
    public static string ComputeLegacyPreflightHash(
        string connectionKey,
        GradingLaunchPayloadItem payloadItem,
        AssignmentExistingGrade? existingGrade)
    {
        var normalizedFeedback = string.Join(
            " ",
            (existingGrade?.Feedback ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var canonical = string.Join("|",
            connectionKey,
            payloadItem.AssignmentId,
            payloadItem.StudentId,
            payloadItem.AttemptNumber ?? 0,
            existingGrade?.HasGrade == true
                ? existingGrade.Grade?.ToString("0.####", CultureInfo.InvariantCulture) ?? "null"
                : "none",
            normalizedFeedback,
            existingGrade?.FeedbackObserved == true ? "feedback-observed" : "feedback-unobserved",
            !string.IsNullOrWhiteSpace(existingGrade?.Feedback) ? "feedback" : "no-feedback",
            payloadItem.SubmissionContentHash ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string ResolveGradeOperation(GradingLaunchPayloadItem payloadItem)
    {
        if (!string.IsNullOrWhiteSpace(payloadItem.GradeOperation))
        {
            return payloadItem.GradeOperation;
        }

        if (payloadItem.Grade is not null)
        {
            return "set_grade";
        }

        return string.Equals(payloadItem.GradingMode, "feedback_only", StringComparison.OrdinalIgnoreCase)
            ? "no_grade"
            : string.Equals(payloadItem.GradingMode, "numeric", StringComparison.OrdinalIgnoreCase)
                ? "preserve_grade"
                : "unsupported";
    }

    private static string NormalizeFeedback(string? value)
    {
        var decoded = WebUtility.HtmlDecode(value ?? string.Empty).Replace('\u00A0', ' ');
        var withoutMarkup = HtmlTagRegex.Replace(decoded, " ");
        return string.Join(" ", withoutMarkup.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
