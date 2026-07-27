using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Application.Sessions;
using StormATC.Domain;
using StormATC.Domain.Sessions;

namespace StormATC.Desktop.ViewModels;

/// <summary>
/// The Transcript page: the running conversation, and the ability to review a past session.
/// </summary>
/// <remarks>
/// Controller turns are marked when they came from deterministic phraseology rather than the
/// language model, and any validation problem that caused the fallback is shown. Being able to
/// see why a response was phrased the way it was is the point of this page.
/// </remarks>
public sealed partial class TranscriptViewModel : ObservableObject, IRefreshablePage, IDisposable
{
    private readonly AtcSessionContext _sessionContext;
    private readonly ISessionRepository _sessionRepository;
    private readonly ILogger<TranscriptViewModel> _logger;

    private bool _disposed;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private SessionSummaryDisplay? _selectedSession;

    [ObservableProperty]
    private bool _isShowingHistory;

    /// <summary>Creates the transcript view model.</summary>
    public TranscriptViewModel(
        AtcSessionContext sessionContext,
        ISessionRepository sessionRepository,
        ILogger<TranscriptViewModel> logger)
    {
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Turns = [];
        PastSessions = [];

        _sessionContext.SessionChanged += OnSessionChanged;
        LoadTurns(_sessionContext.CurrentSession.Conversation);
    }

    /// <summary>The conversation, oldest first.</summary>
    public ObservableCollection<TranscriptTurnDisplay> Turns { get; }

    /// <summary>Recent sessions available for review.</summary>
    public ObservableCollection<SessionSummaryDisplay> PastSessions { get; }

    /// <inheritdoc />
    public async Task RefreshAsync()
    {
        if (!IsShowingHistory)
        {
            LoadTurns(_sessionContext.CurrentSession.Conversation);
        }

        await LoadSessionListAsync().ConfigureAwait(true);
    }

    /// <summary>Returns to the live session transcript.</summary>
    [RelayCommand]
    private void ShowCurrentSession()
    {
        IsShowingHistory = false;
        SelectedSession = null;
        LoadTurns(_sessionContext.CurrentSession.Conversation);
        StatusMessage = string.Empty;
    }

    /// <summary>Loads a stored session's conversation.</summary>
    [RelayCommand]
    private async Task LoadSessionAsync(SessionSummaryDisplay? summary)
    {
        if (summary is null)
        {
            return;
        }

        IsLoading = true;

        try
        {
            IReadOnlyList<ConversationTurn> turns = await _sessionRepository
                .LoadConversationAsync(summary.SessionId, CancellationToken.None)
                .ConfigureAwait(true);

            IsShowingHistory = true;
            SelectedSession = summary;
            LoadTurns(turns);

            StatusMessage = turns.Count == 0
                ? "That session has no stored transcript."
                : $"Showing {turns.Count} turns from {summary.StartedAt}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load session {SessionId}.", summary.SessionId);
            StatusMessage = "That session could not be loaded.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Copies the visible transcript to the clipboard as plain text.</summary>
    [RelayCommand]
    private void CopyTranscript()
    {
        if (Turns.Count == 0)
        {
            StatusMessage = "There is nothing to copy yet.";
            return;
        }

        string text = string.Join(
            Environment.NewLine,
            Turns.Select(turn => $"[{turn.Time}] {turn.SpeakerLabel}: {turn.Text}"));

        try
        {
            System.Windows.Clipboard.SetText(text);
            StatusMessage = $"Copied {Turns.Count} turns to the clipboard.";
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another process; that is not worth an error dialog.
            _logger.LogWarning(ex, "The transcript could not be copied to the clipboard.");
            StatusMessage = "The clipboard is unavailable right now.";
        }
    }

    private async Task LoadSessionListAsync()
    {
        try
        {
            IReadOnlyList<SessionSummary> summaries = await _sessionRepository
                .ListSessionsAsync(limit: 25, CancellationToken.None)
                .ConfigureAwait(true);

            PastSessions.Clear();

            foreach (SessionSummary summary in summaries)
            {
                PastSessions.Add(new SessionSummaryDisplay(
                    summary.SessionId,
                    summary.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                    summary.Callsign ?? "—",
                    $"{summary.Departure ?? "—"} → {summary.Destination ?? "—"}",
                    summary.Mode,
                    summary.TurnCount));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The session list could not be loaded.");
            StatusMessage = "Stored sessions could not be listed.";
        }
    }

    private void OnSessionChanged(object? sender, AtcSession session) =>
        RunOnUiThread(() =>
        {
            if (!IsShowingHistory)
            {
                LoadTurns(session.Conversation);
            }
        });

    private void LoadTurns(IReadOnlyList<ConversationTurn> turns)
    {
        Turns.Clear();

        foreach (ConversationTurn turn in turns)
        {
            Turns.Add(ToDisplay(turn));
        }
    }

    private static TranscriptTurnDisplay ToDisplay(ConversationTurn turn) => new(
        turn.TurnId,
        turn.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture),
        turn.Speaker == ConversationSpeaker.Pilot
            ? "Pilot"
            : turn.Controller == ControllerPosition.None
                ? "Controller"
                : Domain.Phraseology.DeterministicPhraseology.SpokenControllerName(turn.Controller),
        turn.Speaker == ConversationSpeaker.Pilot,
        turn.Text,
        turn.NormalizedText,
        turn.Intent?.ToString(),
        turn.UsedDeterministicFallback,
        turn.Warnings.Count > 0 ? string.Join(" · ", turn.Warnings) : null);

    private static void RunOnUiThread(Action action)
    {
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionContext.SessionChanged -= OnSessionChanged;
    }
}

/// <summary>One conversation turn, ready for display.</summary>
public sealed record TranscriptTurnDisplay(
    Guid TurnId,
    string Time,
    string SpeakerLabel,
    bool IsPilot,
    string Text,
    string? NormalizedText,
    string? Intent,
    bool UsedDeterministicFallback,
    string? Warnings)
{
    /// <summary>True when the normalised form differs and is worth showing.</summary>
    public bool HasNormalizedText => !string.IsNullOrWhiteSpace(NormalizedText);

    /// <summary>True when there is an intent label to show.</summary>
    public bool HasIntent => !string.IsNullOrWhiteSpace(Intent);

    /// <summary>True when a validation problem is attached to this turn.</summary>
    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);
}

/// <summary>One row in the stored-session list.</summary>
public sealed record SessionSummaryDisplay(
    Guid SessionId,
    string StartedAt,
    string Callsign,
    string Route,
    string Mode,
    int TurnCount);
