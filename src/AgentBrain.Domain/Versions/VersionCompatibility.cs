namespace AgentBrain.Domain.Versions;

/// <summary>
/// Result of checking a declared version range against a context version. <see cref="Unknown"/>
/// means "no evidence": it neither excludes an entry nor counts as verified compatibility.
/// </summary>
public enum VersionCompatibility
{
    Unknown,
    Compatible,
    Incompatible,
}
