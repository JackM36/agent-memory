using System.Text.Json.Serialization;

namespace AgentBrain.Contracts.V1;

/// <summary>Source-generated (AOT-safe) serialization for every v1 contract type.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SearchResponseDto))]
[JsonSerializable(typeof(ListResponseDto))]
[JsonSerializable(typeof(EntriesResponseDto))]
[JsonSerializable(typeof(ReferencesResponseDto))]
[JsonSerializable(typeof(BacklinksResponseDto))]
[JsonSerializable(typeof(CollectionsResponseDto))]
[JsonSerializable(typeof(IndexResponseDto))]
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(WriteResponseDto))]
[JsonSerializable(typeof(SimilarResponseDto))]
[JsonSerializable(typeof(ProposalsResponseDto))]
[JsonSerializable(typeof(PublishResponseDto))]
[JsonSerializable(typeof(BatchesResponseDto))]
[JsonSerializable(typeof(SearchRequestDto))]
[JsonSerializable(typeof(ListRequestDto))]
[JsonSerializable(typeof(BatchGetRequestDto))]
[JsonSerializable(typeof(ChangeRequestDto))]
[JsonSerializable(typeof(PublishRequestDto))]
[JsonSerializable(typeof(DecisionDto))]
[JsonSerializable(typeof(HealthDto))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
