using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StormATC.Application.Abstractions;
using StormATC.Application.Options;

namespace StormATC.Desktop.Services;

/// <summary>
/// Sets aside the controller voice that spoke last by adding it to
/// <c>LocalVoice:PiperExcludedVoices</c> in the user's settings.
/// </summary>
/// <remarks>
/// Shared by the dashboard's button and the in-simulator panel's, so a pilot in VR does not have
/// to take the headset off to say that a voice is poor.
/// </remarks>
public sealed class VoiceRejectionService(
    ITextToSpeechService textToSpeech,
    IOptionsMonitor<LocalVoiceOptions> localVoiceOptions,
    UserSettingsService userSettings,
    ILogger<VoiceRejectionService> logger) : IVoiceRejection, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <inheritdoc />
    public SpokenVoice? LastVoice => (textToSpeech as ILastVoiceReporter)?.LastVoice;

    /// <inheritdoc />
    public async Task<VoiceRejectionResult> RejectLastVoiceAsync(CancellationToken cancellationToken)
    {
        if (LastVoice is not SpokenVoice voice)
        {
            return new VoiceRejectionResult(VoiceRejectionOutcome.NoVoiceYet, string.Empty);
        }

        if (!voice.CanExclude)
        {
            return new VoiceRejectionResult(VoiceRejectionOutcome.ChosenInSettings, voice.Description);
        }

        // Two quick taps must not both read the old list and lose one of the entries.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var array = new JsonArray();
            foreach (string entry in VoiceExclusions.With(
                         localVoiceOptions.CurrentValue.PiperExcludedVoices,
                         voice.ExclusionEntry))
            {
                array.Add(entry);
            }

            await userSettings
                .SaveAsync(LocalVoiceOptions.SectionName, "PiperExcludedVoices", array, cancellationToken)
                .ConfigureAwait(false);

            return new VoiceRejectionResult(VoiceRejectionOutcome.SetAside, voice.Description);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(ex, "Setting the voice aside failed.");
            return new VoiceRejectionResult(VoiceRejectionOutcome.Failed, voice.Description);
        }
        finally
        {
            _gate.Release();
        }
    }
}
