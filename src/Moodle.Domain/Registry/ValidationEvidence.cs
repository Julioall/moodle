using System;

namespace Moodle.Domain.Registry;

public sealed record ValidationEvidence(
    string OperationName,
    Guid ConnectionId,
    string AliasAtValidation,
    string NormalizationProfile,
    string MoodleVersion,
    double SemanticParityPercent,
    DateTimeOffset ValidatedAt
);
