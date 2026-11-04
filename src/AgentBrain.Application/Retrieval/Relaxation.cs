using System.Globalization;

namespace AgentBrain.Application.Retrieval;

/// <summary>
/// Admission relaxation in <c>[0, 1]</c> (spec §7.4): 0 is strict, 1 is the broadest supported
/// admission. It changes admission only, never ranking, filters, permissions or budgets. Named presets
/// map onto values (A8); their values are calibration items (plan §6.3).
/// </summary>
public readonly record struct Relaxation
{
    public const string StrictName = "strict";
    public const string DefaultName = "default";
    public const string BroadName = "broad";

    public static readonly Relaxation Strict = new(0.0);
    public static readonly Relaxation Default = new(0.35);
    public static readonly Relaxation Broad = new(0.8);

    private Relaxation(double value) => Value = value;

    public double Value { get; }

    public static bool TryParse(string? text, out Relaxation relaxation)
    {
        relaxation = Default;
        switch (text?.Trim())
        {
            case StrictName:
                relaxation = Strict;
                return true;
            case DefaultName:
                relaxation = Default;
                return true;
            case BroadName:
                relaxation = Broad;
                return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 1)
        {
            relaxation = new Relaxation(value);
            return true;
        }

        return false;
    }

    public static Relaxation FromValue(double value) =>
        value is >= 0 and <= 1 ? new Relaxation(value) : throw new ArgumentOutOfRangeException(nameof(value), value, "Relaxation must be within [0, 1].");

    public override string ToString() => Value.ToString("0.###", CultureInfo.InvariantCulture);
}
