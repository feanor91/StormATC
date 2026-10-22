using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StormATC.Application.Abstractions;
using StormATC.Application.Charts;
using StormATC.Application.Localization;

namespace StormATC.Desktop.ViewModels;

/// <summary>
/// Reading a chart in the charts folder with the pilot's language model, checking what it found fact
/// by fact, and saving the profile.
/// </summary>
/// <remarks>
/// The model only proposes. Each fact is shown with the words of the chart it was read from, the
/// pilot keeps or drops each one, and nothing is saved until the pilot confirms.
/// </remarks>
public sealed partial class VacProfileImportViewModel : ObservableObject
{
    private readonly IVacChartLibrary _charts;
    private readonly VacProfileImporter _importer;
    private readonly IVacProfileStore _store;
    private readonly ILanguageModelClient _languageModel;
    private readonly IClock _clock;
    private readonly ILogger<VacProfileImportViewModel> _logger;
    private VacProfileDraft? _draft;
    private string _provider = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private VacChart? _selectedChart;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _hasDraft;

    [ObservableProperty]
    private string _draftTitle = string.Empty;

    [ObservableProperty]
    private string _warnings = string.Empty;

    [ObservableProperty]
    private string _existingNote = string.Empty;

    [ObservableProperty]
    private string _folder = string.Empty;

    /// <summary>Creates the view model.</summary>
    public VacProfileImportViewModel(
        IVacChartLibrary charts,
        VacProfileImporter importer,
        IVacProfileStore store,
        ILanguageModelClient languageModel,
        IClock clock,
        ILogger<VacProfileImportViewModel> logger)
    {
        _charts = charts ?? throw new ArgumentNullException(nameof(charts));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _languageModel = languageModel ?? throw new ArgumentNullException(nameof(languageModel));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Refresh();
    }

    /// <summary>The charts in the charts folder.</summary>
    public ObservableCollection<VacChart> Charts { get; } = [];

    /// <summary>The facts the model found, to keep or drop.</summary>
    public ObservableCollection<VacFactViewModel> Facts { get; } = [];

    /// <summary>Lists the charts again.</summary>
    [RelayCommand]
    private void Refresh()
    {
        string? selected = SelectedChart?.Id;
        Charts.Clear();
        foreach (VacChart chart in _charts.List())
        {
            Charts.Add(chart);
        }

        SelectedChart = Charts.FirstOrDefault(c => c.Id == selected) ?? Charts.FirstOrDefault();
        Folder = _charts.Directory;
        if (Charts.Count == 0 && !IsBusy)
        {
            Status = UiText.T("No chart yet: put your charts (PDF) in the charts folder, then refresh the list.");
        }
    }

    private bool CanAnalyze() => !IsBusy && SelectedChart is not null;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (SelectedChart is not VacChart chart)
        {
            return;
        }

        Discard();
        IsBusy = true;
        Status = UiText.T($"Reading {chart.Name} with {_languageModel.ProviderName}: its pages are sent to that provider. This takes a minute…");
        try
        {
            VacImportResult result = await _importer.AnalyzeAsync(chart.Id, CancellationToken.None).ConfigureAwait(true);
            if (!result.Success || result.Draft is not VacProfileDraft draft)
            {
                Status = result.Message;
                return;
            }

            _draft = draft;
            _provider = result.Provider;
            foreach (VacProfileFact fact in draft.Facts)
            {
                Facts.Add(new VacFactViewModel(fact));
            }

            DraftTitle = string.IsNullOrWhiteSpace(draft.Name) ? draft.Icao : $"{draft.Icao} — {draft.Name}";
            Warnings = string.Join(Environment.NewLine, draft.Warnings);
            ExistingNote = _store.Origin(draft.Icao) switch
            {
                VacProfileOrigin.Bundled => UiText.T("A profile for this aerodrome comes with StormATC. Saving yours replaces it for you; the bundled one is not changed."),
                VacProfileOrigin.User => UiText.T("You already saved a profile for this aerodrome. Saving again keeps the old one as a .bak file."),
                _ => string.Empty,
            };
            HasDraft = true;
            Status = UiText.T($"Read {result.PagesSent} pages. Check each fact against the chart, keep the ones that are right, then save.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading the chart {Chart} failed.", chart.Name);
            Status = UiText.T("The chart could not be read. See Diagnostics for the details.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => !IsBusy && HasDraft;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_draft is not VacProfileDraft draft)
        {
            return;
        }

        VacProfileFact[] kept = [.. Facts.Where(f => f.Include).Select(f => f.Fact)];
        if (kept.Length == 0)
        {
            Status = UiText.T("Keep at least one fact to save a profile.");
            return;
        }

        IsBusy = true;
        try
        {
            string json = VacProfileDraftBuilder.ToProfileJson(draft, kept, _provider, _clock.UtcNow);
            string path = await _store.SaveAsync(draft.Icao, json, CancellationToken.None).ConfigureAwait(true);
            Status = UiText.T($"Profile saved: {path}. It is used the next time a flight is loaded.");
            Discard(keepStatus: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
        {
            _logger.LogWarning(ex, "The VFR profile of {Icao} could not be saved.", draft.Icao);
            Status = UiText.T("The profile could not be saved. See Diagnostics for the details.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Drops the draft without saving anything.</summary>
    [RelayCommand]
    private void Discard() => Discard(keepStatus: false);

    private void Discard(bool keepStatus)
    {
        _draft = null;
        Facts.Clear();
        HasDraft = false;
        DraftTitle = string.Empty;
        Warnings = string.Empty;
        ExistingNote = string.Empty;
        if (!keepStatus && !IsBusy)
        {
            Status = string.Empty;
        }

        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasDraftChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();
}

/// <summary>A fact of a draft profile on the page: the pilot keeps it or drops it.</summary>
public sealed partial class VacFactViewModel : ObservableObject
{
    /// <summary>Creates the row.</summary>
    /// <param name="fact">The fact.</param>
    public VacFactViewModel(VacProfileFact fact)
    {
        Fact = fact ?? throw new ArgumentNullException(nameof(fact));

        // What the model was not sure of is not kept until the pilot has looked at it.
        _include = !fact.NeedsReview;
    }

    /// <summary>The fact.</summary>
    public VacProfileFact Fact { get; }

    /// <summary>Its name.</summary>
    public string Label => Fact.Label;

    /// <summary>Its value.</summary>
    public string Value => Fact.Value;

    /// <summary>The words of the chart it was read from, with the page.</summary>
    public string Evidence => Fact.Page is int page
        ? string.Create(CultureInfo.CurrentCulture, $"{UiText.T("Page")} {page} : « {Fact.Evidence} »")
        : $"« {Fact.Evidence} »";

    /// <summary>True when the model was not sure.</summary>
    public bool NeedsReview => Fact.NeedsReview;

    /// <summary>Whether the pilot keeps it.</summary>
    [ObservableProperty]
    private bool _include;
}
