using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StormATC.Application.Abstractions;
using StormATC.Application.Charts;

namespace StormATC.Application.Tests;

/// <summary>Reading a chart with a model: what is checked, what the pilot confirms, what is written.</summary>
public sealed class VacProfileImportTests
{
    private static readonly VacChart Chart = new("AD-2.LFPM", "AD-2.LFPM", "LFPM");

    private static VacExtraction Melun() => new()
    {
        Icao = "LFPM",
        Name = "Melun-Villaroche",
        PreferredQfu = new VacNumberFact { Value = 282, Evidence = "QFU 282° préférentiel.", Page = 3, Confidence = "high" },
        CircuitHeightFeetAmsl = new VacNumberFact { Value = 1300, Evidence = "circuit 1300 ft", Page = 3, Confidence = "low" },
        CircuitSide = new VacSideFact { Side = "north", Evidence = "circuit drawn north of the runways", Page = 2, Confidence = "high" },
        ReportingPoints =
        [
            new VacPointFact { Name = "n", SpokenFrench = "Novembre", SpokenEnglish = "November", Latitude = 48.69417, Longitude = 2.72056, Evidence = "N 48° 41' 39\" N - Triangle TGV", Page = 5, Confidence = "high" },
            new VacPointFact { Name = "SM", SpokenFrench = "", SpokenEnglish = "", Evidence = "SM Chartrettes", Page = 5, Confidence = "high" },
        ],
    };

    [Fact]
    public void A_checked_reading_keeps_each_fact_with_its_evidence_and_asks_for_a_second_look_where_unsure()
    {
        VacImportResult result = VacProfileDraftBuilder.Build(Melun(), Chart);

        Assert.True(result.Success);
        VacProfileDraft draft = result.Draft!;
        Assert.Equal("LFPM", draft.Icao);
        Assert.Equal("Melun-Villaroche", draft.Name);

        VacProfileFact qfu = draft.Facts.Single(f => f.Kind == VacFactKind.PreferredQfu);
        Assert.Equal(282, qfu.Number);
        Assert.False(qfu.NeedsReview);
        Assert.Equal(3, qfu.Page);

        // Unsure of the height; and a side is always worth a look, since it is read off a drawing.
        Assert.True(draft.Facts.Single(f => f.Kind == VacFactKind.CircuitHeight).NeedsReview);
        VacProfileFact side = draft.Facts.Single(f => f.Kind == VacFactKind.CircuitSide);
        Assert.True(side.NeedsReview);
        Assert.Equal(0, side.Number);

        // The designator is upper-cased, and a missing spoken name falls back to the designator.
        VacPointFact[] points = [.. draft.Facts.Where(f => f.Kind == VacFactKind.ReportingPoint).Select(f => f.Point!)];
        Assert.Equal(["N", "SM"], points.Select(p => p.Name));
        Assert.Equal("SM", points[1].SpokenFrench);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(361)]
    [InlineData(-5)]
    public void A_runway_direction_no_chart_can_give_is_left_out(int value)
    {
        VacExtraction extraction = Melun() with
        {
            PreferredQfu = new VacNumberFact { Value = value, Evidence = "QFU", Confidence = "high" },
        };

        VacImportResult result = VacProfileDraftBuilder.Build(extraction, Chart);

        Assert.DoesNotContain(result.Draft!.Facts, f => f.Kind == VacFactKind.PreferredQfu);
        Assert.NotEmpty(result.Draft.Warnings);
    }

    [Fact]
    public void A_value_with_no_quoted_words_is_left_out()
    {
        VacExtraction extraction = Melun() with
        {
            PreferredQfu = new VacNumberFact { Value = 282, Evidence = "  ", Confidence = "high" },
            ReportingPoints = [new VacPointFact { Name = "E", Evidence = "", Confidence = "high" }],
        };

        VacImportResult result = VacProfileDraftBuilder.Build(extraction, Chart);

        Assert.DoesNotContain(result.Draft!.Facts, f => f.Kind is VacFactKind.PreferredQfu or VacFactKind.ReportingPoint);
    }

    [Theory]
    [InlineData("TOOLONGNAME")]
    [InlineData("E E")]
    [InlineData("")]
    [InlineData("é")]
    public void A_reporting_point_designator_has_one_to_six_letters_or_digits(string name)
    {
        VacExtraction extraction = Melun() with
        {
            ReportingPoints = [new VacPointFact { Name = name, Evidence = "x", Confidence = "high" }],
        };

        VacImportResult result = VacProfileDraftBuilder.Build(extraction, Chart);

        Assert.DoesNotContain(result.Draft!.Facts, f => f.Kind == VacFactKind.ReportingPoint);
    }

    [Fact]
    public void The_chart_files_name_wins_over_the_aerodrome_the_model_named()
    {
        VacImportResult result = VacProfileDraftBuilder.Build(Melun() with { Icao = "LFPN" }, Chart);

        Assert.Equal("LFPM", result.Draft!.Icao);
        Assert.Contains(result.Draft.Warnings, w => w.Contains("LFPN", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_any_usable_fact_there_is_no_draft()
    {
        VacImportResult result = VacProfileDraftBuilder.Build(new VacExtraction { Icao = "LFPM" }, Chart);

        Assert.False(result.Success);
        Assert.Null(result.Draft);
    }

    [Fact]
    public void A_chart_with_no_icao_in_its_name_needs_the_model_to_find_one()
    {
        var unnamed = new VacChart("notes", "notes", null);

        Assert.False(VacProfileDraftBuilder.Build(Melun() with { Icao = "x" }, unnamed).Success);
        Assert.True(VacProfileDraftBuilder.Build(Melun(), unnamed).Success);
    }

    [Fact]
    public void The_profile_written_holds_only_the_facts_the_pilot_kept_and_says_where_each_came_from()
    {
        VacProfileDraft draft = VacProfileDraftBuilder.Build(Melun(), Chart).Draft!;
        VacProfileFact[] kept = [.. draft.Facts.Where(f => f.Kind is VacFactKind.PreferredQfu or VacFactKind.ReportingPoint)];

        string json = VacProfileDraftBuilder.ToProfileJson(draft, kept, "OpenAI", new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal("LFPM", root.GetProperty("icao").GetString());
        Assert.Equal(282, root.GetProperty("preferredQfu").GetInt32());
        Assert.Equal("QFU 282° préférentiel.", root.GetProperty("preferredQfuEvidence").GetString());
        Assert.False(root.TryGetProperty("circuit", out _));
        JsonElement points = root.GetProperty("reportingPoints");
        Assert.Equal(2, points.GetArrayLength());
        Assert.Equal("Novembre", points[0].GetProperty("spoken").GetProperty("fr").GetString());
        Assert.Equal(48.69417, points[0].GetProperty("latitude").GetDouble());
        Assert.Equal("2026-10-22", root.GetProperty("source").GetProperty("importedAt").GetString());
    }

    // ---- the importer -----------------------------------------------------------------------------

    private sealed class FakeCharts(int pages = 3) : IVacChartLibrary
    {
        public string Directory => "C:\\charts";

        public void EnsureDirectory()
        {
        }

        public IReadOnlyList<VacChart> List() => [Chart];

        public Task<int?> GetPageCountAsync(string id, CancellationToken cancellationToken) => Task.FromResult<int?>(pages);

        public Task<VacChartPage?> RenderPageAsync(string id, int page, int width, CancellationToken cancellationToken) =>
            Task.FromResult<VacChartPage?>(new VacChartPage([(byte)page], page, pages));
    }

    private sealed class FakeModel(bool configured, VacExtraction? answer, string? error = null) : ILanguageModelClient
    {
        public ModelRequest? Request { get; private set; }

        public string ProviderName => "FakeProvider";

        public bool IsConfigured => configured;

        public Task<StructuredModelResponse<T>> GenerateStructuredAsync<T>(
            ModelRequest request,
            JsonSchemaDefinition schema,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(answer is T value
                ? StructuredModelResponse.Ok(value, TimeSpan.Zero, 1234)
                : StructuredModelResponse.Fail<T>(error ?? "It failed.", ModelErrorCategory.Transient, TimeSpan.Zero));
        }

        public IAsyncEnumerable<ModelTextDelta> StreamTextAsync(ModelRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task The_importer_sends_every_page_as_an_image_and_checks_the_answer()
    {
        var model = new FakeModel(configured: true, Melun());
        var importer = new VacProfileImporter(new FakeCharts(pages: 3), model, NullLogger<VacProfileImporter>.Instance);

        VacImportResult result = await importer.AnalyzeAsync("AD-2.LFPM", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, result.PagesSent);
        Assert.Equal("FakeProvider", result.Provider);
        Assert.Equal(1234, result.Tokens);
        Assert.Equal(3, model.Request!.Images.Count);
        Assert.Equal(0, model.Request.Temperature);
        Assert.Contains("AD-2.LFPM", model.Request.UserPrompt, StringComparison.Ordinal);
        Assert.NotNull(model.Request.TimeoutOverride);
        Assert.NotEmpty(result.Draft!.Facts);
    }

    [Fact]
    public async Task A_long_chart_is_cut_at_the_page_limit()
    {
        var model = new FakeModel(configured: true, Melun());
        var importer = new VacProfileImporter(new FakeCharts(pages: 40), model, NullLogger<VacProfileImporter>.Instance);

        VacImportResult result = await importer.AnalyzeAsync("AD-2.LFPM", CancellationToken.None);

        Assert.Equal(VacProfileImporter.MaximumPages, result.PagesSent);
    }

    [Fact]
    public async Task Without_a_configured_model_nothing_is_sent()
    {
        var model = new FakeModel(configured: false, Melun());
        var importer = new VacProfileImporter(new FakeCharts(), model, NullLogger<VacProfileImporter>.Instance);

        VacImportResult result = await importer.AnalyzeAsync("AD-2.LFPM", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(model.Request);
    }

    [Fact]
    public async Task A_chart_that_is_not_in_the_folder_is_not_read()
    {
        var model = new FakeModel(configured: true, Melun());
        var importer = new VacProfileImporter(new FakeCharts(), model, NullLogger<VacProfileImporter>.Instance);

        VacImportResult result = await importer.AnalyzeAsync("..\\secret", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(model.Request);
    }

    [Fact]
    public async Task A_model_failure_is_reported_with_a_hint_about_images()
    {
        var model = new FakeModel(configured: true, answer: null, error: "HTTP 400.");
        var importer = new VacProfileImporter(new FakeCharts(), model, NullLogger<VacProfileImporter>.Instance);

        VacImportResult result = await importer.AnalyzeAsync("AD-2.LFPM", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("HTTP 400.", result.Message, StringComparison.Ordinal);
        Assert.Equal(3, result.PagesSent);
    }

    [Fact]
    public void The_schema_the_model_must_answer_with_is_valid_json_with_every_field_required()
    {
        using JsonDocument schema = JsonDocument.Parse(VacProfileImporter.Schema().Schema);
        JsonElement root = schema.RootElement;

        string[] required = [.. root.GetProperty("required").EnumerateArray().Select(e => e.GetString()!)];
        string[] properties = [.. root.GetProperty("properties").EnumerateObject().Select(p => p.Name)];
        Assert.Equal(properties.Order(), required.Order());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
    }
}
