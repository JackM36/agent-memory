namespace AgentBrain.Contracts.V1;

public sealed record SimilarDto(string Ref, string Title, string Summary, string? Verdict = null, double? Confidence = null);

public sealed record WriteResponseDto(int Protocol, string Ref, string? Rev, IReadOnlyList<SimilarDto> Similar, IReadOnlyList<string>? Notes = null);

public sealed record SimilarResponseDto(int Protocol, IReadOnlyList<SimilarDto> Similar);

public sealed record ProposalDto(
    string Id,
    string Status,
    string Kind,
    string Target,
    string? BaseRevision,
    string? Title,
    string? Summary,
    string ProposedBy,
    string CreatedAt,
    string? Note,
    string? ReviewedBy,
    string? ReviewNote,
    string? Body);

public sealed record ProposalsResponseDto(int Protocol, IReadOnlyList<ProposalDto> Proposals);

public sealed record PublishedDto(string Ref, string Kind, string? Rev);

public sealed record PublishResponseDto(int Protocol, IReadOnlyList<PublishedDto> Published);

public sealed record BatchItemDto(string Ref, string Kind, string State);

public sealed record BatchDto(string Id, string Collection, string StartedAt, string By, IReadOnlyList<BatchItemDto> Items);

public sealed record BatchesResponseDto(int Protocol, IReadOnlyList<BatchDto> Batches);
