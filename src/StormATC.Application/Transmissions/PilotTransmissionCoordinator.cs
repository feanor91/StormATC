using System.Diagnostics;
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Application.Intent;
using StormATC.Application.Phraseology;
using StormATC.Application.Sessions;
using StormATC.Domain;
using StormATC.Domain.Atc;
using StormATC.Domain.Intents;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Transmissions;

/// <summary>
/// Runs the pilot-to-controller pipeline for one transmission at a time.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline is strictly ordered and every stage is observable:
/// </para>
/// <list type="number">
/// <item>Deduplicate by transmission identifier</item>
/// <item>Record the original transmission</item>
/// <item>Normalise the transcript</item>
/// <item>Parse intent deterministically, escalating to the model only when unsure</item>
/// <item>Ask the deterministic ATC engine for a decision (which evaluates any readback)</item>
/// <item>Apply the approved state changes — and only those</item>
/// <item>Render deterministic phraseology</item>
/// <item>Optionally ask the model to phrase it more naturally, then validate that output</item>
/// <item>Persist the turn and the decision</item>
/// <item>Publish the result for the UI and the audio pipeline</item>
/// </list>
/// <para>
/// Transmissions are serialised through a semaphore. Two overlapping transmissions for the
/// same aircraft would otherwise race on the session snapshot and could apply two clearances
/// derived from the same starting state.
/// </para>
/// </remarks>
public sealed class PilotTransmissionCoordinator(
    IAtcStateEngine stateEngine,
    IPilotIntentParser intentParser,
    IPhraseologyRenderer phraseologyRenderer,
    AtcSessionContext sessionContext,
    IClock clock,
    ILogger<PilotTransmissionCoordinator> logger,
    ISessionRepository? sessionRepository = null,
    ILanguageModelPhraseologyRenderer? languageModelRenderer = null,
    PhraseologyValidator? phraseologyValidator = null) : IAsyncDisposable
{
    private readonly IAtcStateEngine _stateEngine =
        stateEngine ?? throw new ArgumentNullException(nameof(stateEngine));

    private readonly IPilotIntentParser _intentParser =
        intentParser ?? throw new ArgumentNullException(nameof(intentParser));

    private readonly IPhraseologyRenderer _phraseologyRenderer =
        phraseologyRenderer ?? throw new ArgumentNullException(nameof(phraseologyRenderer));

    private readonly AtcSessionContext _sessionContext =
        sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly ILogger<PilotTransmissionCoordinator> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly PhraseologyValidator _phraseologyValidator = phraseologyValidator ?? new();

    /// <summary>Serialises processing. One aircraft, one transmission at a time.</summary>
    private readonly SemaphoreSlim _processingLock = new(1, 1);

    /// <summary>
    /// Identifiers already processed. Bounded so a long session cannot grow it without limit.
    /// </summary>
    private readonly HashSet<Guid> _processedTransmissionIds = [];
    private readonly Queue<Guid> _processedOrder = new();
    private const int MaximumTrackedTransmissions = 512;

    private bool _disposed;

    /// <summary>Raised when a transmission has been fully processed.</summary>
    public event EventHandler<TransmissionProcessedEventArgs>? TransmissionProcessed;

    /// <summary>Raised when the session state changes for any reason.</summary>
    public event EventHandler<SessionUpdatedEventArgs>? SessionUpdated;

    /// <summary>Processes one pilot transmission through the full pipeline.</summary>
    public async Task<TransmissionProcessingResult> ProcessAsync(
        PilotTransmission transmission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transmission);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var totalStopwatch = Stopwatch.StartNew();

        await _processingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // --- 1. Deduplicate. -------------------------------------------------------------
            if (!_processedTransmissionIds.Add(transmission.TransmissionId))
            {
                LogDuplicateTransmission(_logger, transmission.CorrelationId, null);
                return TransmissionProcessingResult.Duplicate(transmission.CorrelationId);
            }

            TrackProcessedId(transmission.TransmissionId);

            return await ProcessCoreAsync(transmission, totalStopwatch, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Boundary handler: a failure anywhere in the pipeline must not take the session
            // down. The error is logged with its correlation id and surfaced to the UI.
            _logger.LogError(
                ex,
                "Transmission {CorrelationId} failed during processing.",
                transmission.CorrelationId);

            return TransmissionProcessingResult.Failed(
                transmission.CorrelationId,
                "The transmission could not be processed. See the Diagnostics page for details.");
        }
        finally
        {
            _processingLock.Release();
        }
    }

    private async Task<TransmissionProcessingResult> ProcessCoreAsync(
        PilotTransmission transmission,
        Stopwatch totalStopwatch,
        CancellationToken cancellationToken)
    {
        AtcSession session = _sessionContext.CurrentSession;
        DateTimeOffset now = _clock.UtcNow;

        // A transmission is heard only on the frequency selected for transmission in the
        // aircraft. ATIS is receive-only; every other staffed position requires the active
        // transmit channel to match the frequency assigned by the session. This gate runs
        // before transcript recording, parsing, language-model calls, or persistence.
        if (session.CurrentController == ControllerPosition.Atis)
        {
            totalStopwatch.Stop();
            LogRadioGatedTransmission(
                _logger,
                transmission.CorrelationId,
                session.AircraftState.Radio.ActiveTransmitFrequency.ToDisplay(),
                session.CurrentFrequency?.ToDisplay() ?? "ATIS",
                "ATIS is receive-only",
                null);
            return TransmissionProcessingResult.IgnoredByRadio(
                transmission.CorrelationId,
                session,
                onAtis: true);
        }

        if (session.CurrentFrequency is Frequency expected
            && !session.AircraftState.Radio.ActiveTransmitFrequency.IsSameChannel(expected))
        {
            totalStopwatch.Stop();
            LogRadioGatedTransmission(
                _logger,
                transmission.CorrelationId,
                session.AircraftState.Radio.ActiveTransmitFrequency.ToDisplay(),
                expected.ToDisplay(),
                "off frequency",
                null);
            return TransmissionProcessingResult.IgnoredByRadio(
                transmission.CorrelationId,
                session,
                onAtis: false);
        }

        // --- 2. Record the original transmission. --------------------------------------------
        var normalizationStopwatch = Stopwatch.StartNew();
        string displayTranscript =
            TranscriptNormalizer.FormatForDisplay(transmission.RawTranscript);
        // A French transmission is rewritten into the parser's canonical English keywords and
        // digit runs first. Only interpretation changes: the conversation keeps the French text.
        string interpreted = session.Language == Domain.Phraseology.PhraseologyLanguage.French
            ? FrenchTranscriptCanonicalizer.Canonicalize(displayTranscript)
            : displayTranscript;
        string normalized = TranscriptNormalizer.Normalize(interpreted);
        normalizationStopwatch.Stop();

        var pilotTurn = new ConversationTurn
        {
            TurnId = transmission.TransmissionId,
            Timestamp = transmission.Timestamp,
            Speaker = ConversationSpeaker.Pilot,
            Controller = session.CurrentController,
            Text = displayTranscript,
            NormalizedText = TranscriptNormalizer.DiffersMeaningfully(displayTranscript, normalized)
                ? normalized
                : null,
            CorrelationId = transmission.CorrelationId,
        };

        session = session.WithConversationTurn(pilotTurn);

        // --- 3-4. Parse intent. ---------------------------------------------------------------
        var parsingStopwatch = Stopwatch.StartNew();
        PilotIntent intent = await _intentParser
            .ParseAsync(normalized, session, cancellationToken)
            .ConfigureAwait(false);
        parsingStopwatch.Stop();

        pilotTurn = pilotTurn with { Intent = intent.Type };

        LogIntentParsed(
            _logger, transmission.CorrelationId, intent.Type, intent.Confidence, intent.Source, null);

        // --- 5. Ask the engine. ----------------------------------------------------------------
        var engineStopwatch = Stopwatch.StartNew();

        AtcEvaluationContext context = await _sessionContext
            .BuildEvaluationContextAsync(session, intent, normalized, now, cancellationToken)
            .ConfigureAwait(false);

        AtcDecision decision = _stateEngine.Evaluate(context);
        engineStopwatch.Stop();

        LogDecision(
            _logger,
            transmission.CorrelationId,
            decision.Accepted,
            decision.ResponseType,
            decision.RejectionReason,
            null);

        if (decision.ReadbackAssessment is ReadbackAssessment readback)
        {
            LogReadbackAssessment(
                _logger,
                transmission.CorrelationId,
                readback.Outcome,
                string.Join(',', readback.CorrectItems),
                string.Join(',', readback.MissingItems),
                string.Join(
                    ',',
                    readback.IncorrectItems.Select(item =>
                        $"{item.Type}:{item.ActualValue}->{item.ExpectedValue}")),
                null);
        }

        // --- 6. Apply approved state changes, and only those. -----------------------------------
        session = ApplyDecision(session, decision, now);

        // --- 7-8. Render the response. ----------------------------------------------------------
        var renderStopwatch = Stopwatch.StartNew();
        RenderedPhraseology rendered = await RenderResponseAsync(decision, session, cancellationToken)
            .ConfigureAwait(false);
        renderStopwatch.Stop();

        // A silent decision produces no controller turn at all.
        if (!string.IsNullOrWhiteSpace(rendered.SpokenText))
        {
            session = session.WithConversationTurn(new ConversationTurn
            {
                TurnId = Guid.CreateVersion7(),
                Timestamp = now,
                Speaker = ConversationSpeaker.Controller,
                Controller = decision.Handoff?.FromController ?? session.CurrentController,
                Text = rendered.DisplayText,
                CorrelationId = transmission.CorrelationId,
                UsedDeterministicFallback = rendered.UsedDeterministicFallback,
                Warnings = rendered.ValidationProblems,
            });
        }

        _sessionContext.UpdateSession(session);

        // --- 9. Persist. -------------------------------------------------------------------------
        var persistenceStopwatch = Stopwatch.StartNew();
        await PersistAsync(session, pilotTurn, rendered, decision, transmission, now, cancellationToken)
            .ConfigureAwait(false);
        persistenceStopwatch.Stop();

        totalStopwatch.Stop();

        var result = new TransmissionProcessingResult
        {
            Processed = true,
            UpdatedSession = session,
            Intent = intent,
            Decision = decision,
            ReadbackAssessment = decision.ReadbackAssessment,
            ControllerSpokenText = rendered.SpokenText,
            ControllerDisplayText = rendered.DisplayText,
            UsedDeterministicFallback = rendered.UsedDeterministicFallback,
            CorrelationId = transmission.CorrelationId,
            Timings = new TransmissionTimings
            {
                Normalization = normalizationStopwatch.Elapsed,
                IntentParsing = parsingStopwatch.Elapsed,
                EngineEvaluation = engineStopwatch.Elapsed,
                PhraseologyRendering = renderStopwatch.Elapsed,
                Persistence = persistenceStopwatch.Elapsed,
                Total = totalStopwatch.Elapsed,
            },
        };

        // --- 10. Publish. -------------------------------------------------------------------------
        SessionUpdated?.Invoke(this, new SessionUpdatedEventArgs(session));
        TransmissionProcessed?.Invoke(this, new TransmissionProcessedEventArgs(result));

        return result;
    }

    /// <summary>
    /// Applies a decision to the session.
    /// </summary>
    /// <remarks>
    /// The only place session state changes as a result of a transmission. It applies exactly
    /// what the engine authorised — no inference, no filling in of gaps. If the engine did not
    /// grant a clearance, none appears here.
    /// </remarks>
    private static AtcSession ApplyDecision(AtcSession session, AtcDecision decision, DateTimeOffset now)
    {
        if (decision.ClearsPendingReadback)
        {
            session = session.WithReadbackAccepted(now);
        }

        if (decision.IssuedClearance is Clearance clearance)
        {
            session = session.WithClearance(clearance);
        }

        if (decision.RequiredReadback is PendingReadback readback)
        {
            session = session.WithPendingReadback(readback);
        }

        if (decision.Handoff is ControllerHandoff handoff)
        {
            session = session.WithHandoff(handoff);
        }

        if (decision.NewPhase is FlightPhase phase)
        {
            session = session.WithPhase(phase);
        }

        foreach (AtcWarning warning in decision.Warnings)
        {
            session = session.WithWarning(warning);
        }

        return session;
    }

    /// <summary>
    /// Renders the controller response, preferring the language model where it is permitted and
    /// its output survives validation.
    /// </summary>
    private async Task<RenderedPhraseology> RenderResponseAsync(
        AtcDecision decision,
        AtcSession session,
        CancellationToken cancellationToken)
    {
        // Always compute the deterministic version first. It is the answer unless something
        // better is produced and proven safe.
        RenderedPhraseology deterministic = await _phraseologyRenderer
            .RenderAsync(decision, session, cancellationToken)
            .ConfigureAwait(false);

        if (!decision.AllowLanguageModelPhraseology
            || languageModelRenderer is null
            || !languageModelRenderer.IsAvailable
            || string.IsNullOrWhiteSpace(decision.FallbackPhraseology))
        {
            return deterministic;
        }

        RenderedPhraseology? modelRendered;
        try
        {
            modelRendered = await languageModelRenderer
                .RenderAsync(decision, session, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Language model phraseology failed; using deterministic output.");
            return deterministic with { FallbackReason = "The language model request failed." };
        }

        if (modelRendered is null)
        {
            return deterministic with { FallbackReason = "The language model returned no usable output." };
        }

        // The gate: model output is spoken only if it says exactly what the engine approved.
        PhraseologyValidationResult validation =
            _phraseologyValidator.Validate(modelRendered.SpokenText, decision);

        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "Generated phraseology failed validation and was discarded: {Problems}",
                string.Join("; ", validation.Problems));

            return deterministic with
            {
                FallbackReason = "Generated phraseology failed validation.",
                ValidationProblems = validation.Problems,
            };
        }

        return modelRendered;
    }

    private async Task PersistAsync(
        AtcSession session,
        ConversationTurn pilotTurn,
        RenderedPhraseology rendered,
        AtcDecision decision,
        PilotTransmission transmission,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (sessionRepository is null)
        {
            return;
        }

        try
        {
            await sessionRepository.SaveSessionAsync(session, cancellationToken).ConfigureAwait(false);
            await sessionRepository.SaveConversationTurnAsync(session.SessionId, pilotTurn, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(rendered.SpokenText))
            {
                await sessionRepository.SaveConversationTurnAsync(
                    session.SessionId,
                    new ConversationTurn
                    {
                        TurnId = Guid.CreateVersion7(),
                        Timestamp = now,
                        Speaker = ConversationSpeaker.Controller,
                        Controller = session.CurrentController,
                        Text = rendered.DisplayText,
                        CorrelationId = transmission.CorrelationId,
                        UsedDeterministicFallback = rendered.UsedDeterministicFallback,
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            await sessionRepository.SaveDecisionAsync(
                session.SessionId,
                new PersistedDecision
                {
                    DecisionId = Guid.CreateVersion7(),
                    TransmissionId = transmission.TransmissionId,
                    Timestamp = now,
                    Accepted = decision.Accepted,
                    ResponseType = decision.ResponseType.ToString(),
                    RejectionReason = decision.RejectionReason,
                    ReadbackOutcome = decision.ReadbackAssessment?.Outcome.ToString(),
                    CorrelationId = transmission.CorrelationId,
                    UsedDeterministicFallback = rendered.UsedDeterministicFallback,
                    ValidationFailureReason = rendered.FallbackReason,
                },
                cancellationToken).ConfigureAwait(false);

            if (decision.IssuedClearance is Clearance clearance)
            {
                await sessionRepository.SaveClearanceAsync(session.SessionId, clearance, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (decision.Handoff is ControllerHandoff handoff)
            {
                await sessionRepository.SaveHandoffAsync(session.SessionId, handoff, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Persistence is not on the critical path. A database problem must not stop the
            // pilot from getting a response.
            _logger.LogError(
                ex,
                "Failed to persist transmission {CorrelationId}. The session continues.",
                transmission.CorrelationId);
        }
    }

    /// <summary>
    /// Feeds a simulator state update through the engine so it can notice physical events —
    /// becoming airborne, touching down, entering a runway without clearance.
    /// </summary>
    public async Task<AtcDecision> ProcessStateChangeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _processingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AtcSession session = _sessionContext.CurrentSession;
            DateTimeOffset now = _clock.UtcNow;

            AtcEvaluationContext context = await _sessionContext
                .BuildEvaluationContextAsync(session, intent: null, transcript: string.Empty, now, cancellationToken)
                .ConfigureAwait(false);

            AtcDecision decision = _stateEngine.EvaluateStateChange(context);

            if (decision.ResponseType == AtcResponseType.None && !decision.ChangesClearanceState
                && decision.Warnings.Count == 0)
            {
                return decision;
            }

            session = ApplyDecision(session, decision, now);

            RenderedPhraseology rendered = await RenderResponseAsync(decision, session, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(rendered.SpokenText))
            {
                session = session.WithConversationTurn(new ConversationTurn
                {
                    TurnId = Guid.CreateVersion7(),
                    Timestamp = now,
                    Speaker = ConversationSpeaker.Controller,
                    Controller = session.CurrentController,
                    Text = rendered.DisplayText,
                    UsedDeterministicFallback = rendered.UsedDeterministicFallback,
                });
            }

            _sessionContext.UpdateSession(session);
            SessionUpdated?.Invoke(this, new SessionUpdatedEventArgs(session));

            if (!string.IsNullOrWhiteSpace(rendered.SpokenText))
            {
                TransmissionProcessed?.Invoke(this, new TransmissionProcessedEventArgs(
                    new TransmissionProcessingResult
                    {
                        Processed = true,
                        UpdatedSession = session,
                        Decision = decision,
                        ControllerSpokenText = rendered.SpokenText,
                        ControllerDisplayText = rendered.DisplayText,
                        UsedDeterministicFallback = rendered.UsedDeterministicFallback,
                        CorrelationId = "state-change",
                    }));
            }

            return decision;
        }
        finally
        {
            _processingLock.Release();
        }
    }

    // Compiled log delegates. These keep argument formatting off the hot path when the
    // corresponding level is disabled, which matters because the pipeline runs per-transmission.
    private static readonly Action<ILogger, string, Exception?> LogDuplicateTransmission =
        LoggerMessage.Define<string>(
            LogLevel.Debug,
            new EventId(1001, nameof(LogDuplicateTransmission)),
            "Ignoring duplicate transmission {CorrelationId}.");

    private static readonly Action<ILogger, string, PilotIntentType, double, IntentParserSource, Exception?>
        LogIntentParsed = LoggerMessage.Define<string, PilotIntentType, double, IntentParserSource>(
            LogLevel.Information,
            new EventId(1002, nameof(LogIntentParsed)),
            "Transmission {CorrelationId} parsed as {IntentType} (confidence {Confidence}, source {Source}).");

    private static readonly Action<ILogger, string, bool, AtcResponseType, string?, Exception?> LogDecision =
        LoggerMessage.Define<string, bool, AtcResponseType, string?>(
            LogLevel.Information,
            new EventId(1003, nameof(LogDecision)),
            "Transmission {CorrelationId} decided: accepted={Accepted}, response={ResponseType}, reason={Reason}.");

    private static readonly Action<
        ILogger,
        string,
        ReadbackOutcome,
        string,
        string,
        string,
        Exception?> LogReadbackAssessment =
        LoggerMessage.Define<string, ReadbackOutcome, string, string, string>(
            LogLevel.Information,
            new EventId(1004, nameof(LogReadbackAssessment)),
            "Readback {CorrelationId}: outcome={Outcome}, correct=[{Correct}], missing=[{Missing}], incorrect=[{Incorrect}].");

    private static readonly Action<ILogger, string, string, string, string, Exception?>
        LogRadioGatedTransmission = LoggerMessage.Define<string, string, string, string>(
            LogLevel.Information,
            new EventId(1005, nameof(LogRadioGatedTransmission)),
            "Transmission {CorrelationId} was not received: tuned={TunedFrequency}, "
                + "expected={ExpectedFrequency}, reason={Reason}.");

    /// <summary>Bounds the deduplication set so a long session does not leak memory.</summary>
    private void TrackProcessedId(Guid id)
    {
        _processedOrder.Enqueue(id);

        while (_processedOrder.Count > MaximumTrackedTransmissions)
        {
            _processedTransmissionIds.Remove(_processedOrder.Dequeue());
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _processingLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Carries a completed transmission result.</summary>
public sealed class TransmissionProcessedEventArgs(TransmissionProcessingResult result) : EventArgs
{
    /// <summary>The result.</summary>
    public TransmissionProcessingResult Result { get; } = result;
}

/// <summary>Carries an updated session snapshot.</summary>
public sealed class SessionUpdatedEventArgs(AtcSession session) : EventArgs
{
    /// <summary>The new session state.</summary>
    public AtcSession Session { get; } = session;
}

/// <summary>
/// A language model that rewrites approved facts into natural controller speech.
/// </summary>
/// <remarks>
/// Its output is always validated before it can be spoken. Returning null on failure keeps the
/// caller's fallback path simple.
/// </remarks>
public interface ILanguageModelPhraseologyRenderer
{
    /// <summary>True when a model is configured.</summary>
    bool IsAvailable { get; }

    /// <summary>Renders a decision, or returns null when nothing usable was produced.</summary>
    Task<RenderedPhraseology?> RenderAsync(
        AtcDecision decision,
        AtcSession session,
        CancellationToken cancellationToken);
}
