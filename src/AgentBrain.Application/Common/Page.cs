namespace AgentBrain.Application.Common;

/// <summary>
/// One page of an exhaustive, stably ordered traversal. <see cref="NextCursor"/> is opaque and null
/// on the last page. <see cref="Partial"/> is true when a source failed during this page.
/// </summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor, bool Partial = false);
