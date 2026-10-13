namespace StormATC.Domain.Phraseology;

/// <summary>
/// Chooses among equivalent wordings of the same transmission.
/// </summary>
/// <remarks>
/// Variants only ever differ in courtesy and cadence words ("reçu" or "bien reçu"). What an
/// instruction says — values, runways, frequencies, permissions — is fixed by the engine and
/// never varies, so a picker cannot change a decision.
/// </remarks>
public interface IVariantPicker
{
    /// <summary>Returns an index in <c>[0, count)</c>.</summary>
    int Pick(int count);
}

/// <summary>Always the first wording. The default, so tests and exports are reproducible.</summary>
public sealed class FirstVariantPicker : IVariantPicker
{
    /// <summary>The shared instance.</summary>
    public static FirstVariantPicker Instance { get; } = new();

    /// <inheritdoc />
    public int Pick(int count) => 0;
}

/// <summary>A uniformly random wording, for sessions that should not sound scripted.</summary>
public sealed class RandomVariantPicker : IVariantPicker
{
    /// <summary>The shared instance.</summary>
    public static RandomVariantPicker Instance { get; } = new();

    /// <inheritdoc />
    public int Pick(int count) => count <= 1 ? 0 : Random.Shared.Next(count);
}
