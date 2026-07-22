namespace Rodent.Core.Model;

public enum SettingKind
{
    Toggle,
    Choice,
    Range,
}

/// <summary>
/// A data-driven device setting. The GUI renders a control purely from Kind +
/// Choices/Range, and calls Read/Write — it never needs feature-specific code.
/// Mirrors Solaar's settings model so the UI can stay generic.
/// </summary>
public abstract class Setting
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public string Description { get; init; } = "";
    public abstract SettingKind Kind { get; }
}

public sealed class ChoiceSetting : Setting
{
    public override SettingKind Kind => SettingKind.Choice;

    /// <summary>Ordered choices: raw device value -> display label.</summary>
    public required IReadOnlyList<Choice> Choices { get; init; }
    public required Func<int?> Read { get; init; }   // returns current raw value
    public required Action<int> Write { get; init; }  // writes a raw value
}

public readonly record struct Choice(int Value, string Label);

/// <summary>A read-only piece of device information (kind, firmware, battery, ...).</summary>
public sealed record InfoItem(string Label, string Value);

public sealed class RangeSetting : Setting
{
    public override SettingKind Kind => SettingKind.Range;
    public required int Min { get; init; }
    public required int Max { get; init; }
    public int Step { get; init; } = 1;
    public required Func<int?> Read { get; init; }
    public required Action<int> Write { get; init; }
}

public sealed class ToggleSetting : Setting
{
    public override SettingKind Kind => SettingKind.Toggle;
    public required Func<bool?> Read { get; init; }
    public required Action<bool> Write { get; init; }
}
