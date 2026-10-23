using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Moodle.Domain.Grading;

namespace Moodle.Application.Grading;

public static class GradingDraftVersionHash
{
    public static string Compute(AssistedGradingItem item)
    {
        var approvedGrade = item.ReviewStatus == GradingReviewStatus.Reviewed
            ? item.FinalGrade
            : item.SuggestedGrade;
        var approvedFeedback = item.ReviewStatus == GradingReviewStatus.Reviewed
            ? item.FinalFeedback
            : item.DraftFeedback;
        var values = new string?[]
        {
            item.Id.ToString("N"),
            item.BatchId.ToString("N"),
            item.SuggestedGrade?.ToString(CultureInfo.InvariantCulture),
            item.Confidence?.ToString(CultureInfo.InvariantCulture),
            item.DraftFeedback,
            item.PrivateNotesToTeacher,
            approvedGrade?.ToString(CultureInfo.InvariantCulture),
            approvedFeedback,
            item.SubmissionContentHash,
            item.SubmissionResourceIdsJson,
            item.SubmissionRevisionHash,
            item.ContextVersion?.ToString(CultureInfo.InvariantCulture),
            item.ContextHash
        };
        // Confirmation can promote an AI draft to Reviewed without changing
        // the grade, feedback or evidence accepted in the preview. Later
        // changes to those approved values still invalidate the fingerprint.
        return Hash(values);
    }

    /// <summary>
    /// Verifies previews created before operational commit state was removed
    /// from the draft fingerprint. This is a compatibility check only; new
    /// previews always use <see cref="Compute"/>.
    /// </summary>
    public static string ComputeLegacy(AssistedGradingItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Hash(
        [
            item.Id.ToString("N"),
            item.BatchId.ToString("N"),
            item.SuggestedGrade?.ToString(CultureInfo.InvariantCulture),
            item.Confidence?.ToString(CultureInfo.InvariantCulture),
            item.DraftFeedback,
            item.PrivateNotesToTeacher,
            item.FinalGrade?.ToString(CultureInfo.InvariantCulture),
            item.FinalFeedback,
            item.TeacherDecision,
            item.ReviewNotes,
            item.Status.ToString(),
            item.SubmissionContentHash,
            item.SubmissionResourceIdsJson,
            item.SubmissionRevisionHash,
            item.ContextVersion?.ToString(CultureInfo.InvariantCulture),
            item.ContextHash,
            item.ReviewStatus.ToString(),
            item.CommitStatus.ToString(),
            item.UpdatedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
        ]);
    }

    private static string Hash(IEnumerable<string?> values)
    {
        var payload = string.Join(
            "\n",
            values.Select(value => value is null
                ? "-"
                : $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}"));
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
