using AgentBrain.Application.Abstractions.Proposals;
using AgentBrain.Application.Writing;
using AgentBrain.Domain.Identity;

namespace AgentBrain.Application.Operations.Routing;

/// <summary>
/// A proposal as shown to a reviewer, whether it lives in the local store or on a host. Remote proposals carry
/// their source, and their ID is shown as <c>source:id</c> so accept and reject reach the right host.
/// </summary>
public sealed record ProposalView(
    string Id,
    ProposalStatus Status,
    ChangeKind Kind,
    EntryRef Target,
    string? BaseRevision,
    string? Title,
    string? Summary,
    string ProposedBy,
    DateTimeOffset CreatedAt,
    string? Note = null,
    string? ReviewedBy = null,
    string? ReviewNote = null,
    string? Body = null,
    string? Source = null)
{
    /// <summary>The ID to type: local IDs as stored, remote ones prefixed with their connection.</summary>
    public string QualifiedId => Source is null ? Id : $"{Source}:{Id}";

    public static ProposalView From(Proposal proposal, bool withBody = false)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var c = proposal.Change;
        return new ProposalView(
            proposal.Id, proposal.Status, c.Kind, c.Target, c.BaseRevision?.Value, c.Metadata?.Title, c.Metadata?.Summary,
            proposal.ProposedBy.ToString(), proposal.CreatedAt, proposal.Note, proposal.ReviewedBy?.ToString(), proposal.ReviewNote,
            withBody ? c.Body : null);
    }

    /// <summary>Splits <c>source:id</c>; local IDs have no source.</summary>
    public static (string? Source, string Id) Parse(string qualified)
    {
        ArgumentNullException.ThrowIfNull(qualified);
        var colon = qualified.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? (qualified[..colon], qualified[(colon + 1)..]) : (null, qualified);
    }
}
