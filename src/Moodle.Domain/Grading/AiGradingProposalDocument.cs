namespace Moodle.Domain.Grading;

/// <summary>
/// Projeção persistível de uma proposta IA. O payload é um contrato canônico
/// bounded e não contém o texto bruto da submissão nem tokens de conexão.
/// </summary>
public sealed class AiGradingProposalDocument
{
    private AiGradingProposalDocument()
    {
    }

    public Guid Id { get; private init; }

    public Guid GradingItemId { get; private init; }

    public Guid BatchId { get; private init; }

    public int Version { get; private init; }

    public string SchemaVersion { get; private init; } = string.Empty;

    public string? ContextHash { get; private init; }

    public string? SubmissionContentHash { get; private init; }

    public string ProposalHash { get; private init; } = string.Empty;

    public string Status { get; private init; } = string.Empty;

    public decimal Confidence { get; private init; }

    public bool ReviewRequired { get; private init; }

    public string PayloadJson { get; private init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private init; }

    public static AiGradingProposalDocument FromProposal(AiGradingProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return new AiGradingProposalDocument
        {
            Id = Guid.NewGuid(),
            GradingItemId = proposal.ItemId,
            BatchId = proposal.BatchId,
            Version = proposal.Version,
            SchemaVersion = proposal.SchemaVersion,
            ContextHash = proposal.ContextHash,
            SubmissionContentHash = proposal.SubmissionContentHash,
            ProposalHash = proposal.ProposalHash,
            Status = proposal.Status,
            Confidence = proposal.Confidence,
            ReviewRequired = proposal.ReviewRequired,
            PayloadJson = AiGradingProposal.SerializeCanonicalPayload(proposal),
            CreatedAt = proposal.CreatedAt
        };
    }
}
