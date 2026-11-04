using System.Text;

namespace AgentBrain.Application.Common;

/// <summary>Opaque, URL-safe cursors (base64url of a position). Every source uses the same encoding, so a federated cursor can carry each source's position.</summary>
public static class Cursors
{
    public static string Encode(string position) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(position)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string? Decode(string cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        var padded = cursor.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
