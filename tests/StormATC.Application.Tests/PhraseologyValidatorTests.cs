using StormATC.Application.Phraseology;
using StormATC.Domain;
using StormATC.Domain.Atc;
using StormATC.Domain.Sessions;
using StormATC.Domain.ValueObjects;

namespace StormATC.Application.Tests;

/// <summary>
/// The gate that decides whether generated controller speech may be spoken.
/// </summary>
/// <remarks>
/// A language model that produces fluent, confident, wrong phraseology is the failure mode this
/// whole architecture exists to prevent. These tests are the proof that it cannot get through.
/// </remarks>
public class PhraseologyValidatorTests
{
    private readonly PhraseologyValidator _validator = new();

    private static AtcDecision TakeoffDecision() => new()
    {
        Accepted = true,
        ResponseType = AtcResponseType.TakeoffClearance,
        IssuedClearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.Takeoff,
            IssuedBy = ControllerPosition.Tower,
            IssuedAt = Fixture.Now,
            Runway = RunwayIdentifier.Create("18"),
        },
        ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["clearance"] = "takeoff",
            ["runway"] = "18",
        },
        ProhibitedFacts = ["landing clearance"],
        FallbackPhraseology = "Storm one two three, runway one eight, cleared for takeoff.",
    };

    private static AtcDecision ClearanceDecision() => new()
    {
        Accepted = true,
        ResponseType = AtcResponseType.IfrClearance,
        IssuedClearance = new Clearance
        {
            ClearanceId = Guid.CreateVersion7(),
            Type = ClearanceType.IfrClearance,
            IssuedBy = ControllerPosition.ClearanceDelivery,
            IssuedAt = Fixture.Now,
            InitialAltitude = Altitude.FromFeet(7_000),
            Squawk = SquawkCode.Create("4271"),
            Runway = RunwayIdentifier.Create("18"),
        },
        ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["clearance"] = "ifr",
            ["destination"] = "STMD",
            ["runway"] = "18",
            ["initialAltitude"] = "7000",
            ["squawk"] = "4271",
        },
        ProhibitedFacts = ["taxi clearance", "takeoff clearance"],
        FallbackPhraseology = "Storm one two three, cleared to Storm Delta, runway one eight, "
            + "climb initially seven thousand, squawk four two seven one.",
    };

    [Fact]
    public void The_deterministic_phraseology_validates_against_its_own_decision()
    {
        // If the engine's own output failed this gate, the gate would be wrong.
        AtcDecision decision = ClearanceDecision();

        PhraseologyValidationResult result = _validator.Validate(decision.FallbackPhraseology, decision);

        Assert.True(result.IsValid, string.Join("; ", result.Problems));
    }

    [Fact]
    public void An_empty_response_is_rejected()
    {
        PhraseologyValidationResult result = _validator.Validate("   ", TakeoffDecision());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void An_altitude_the_engine_never_approved_is_rejected()
    {
        // The dangerous case: fluent, well-formed, and wrong.
        AtcDecision decision = ClearanceDecision();

        PhraseologyValidationResult result = _validator.Validate(
            "Storm one two three, cleared to Storm Delta, runway one eight, "
            + "climb initially five thousand, squawk four two seven one.",
            decision);

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, problem => problem.Contains("5000", StringComparison.Ordinal));
    }

    [Fact]
    public void A_squawk_the_engine_never_approved_is_rejected()
    {
        AtcDecision decision = ClearanceDecision();

        PhraseologyValidationResult result = _validator.Validate(
            "Storm one two three, cleared to Storm Delta, runway one eight, "
            + "climb initially seven thousand, squawk four two seven two.",
            decision);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_takeoff_clearance_added_to_a_taxi_instruction_is_rejected()
    {
        var taxiDecision = new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.TaxiClearance,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["taxiRoute"] = "A,C",
                ["holdShort"] = "18",
            },
            ProhibitedFacts = ["takeoff clearance"],
            FallbackPhraseology = "Storm one two three, taxi via alfa charlie, hold short runway one eight.",
        };

        PhraseologyValidationResult result = _validator.Validate(
            "Storm one two three, taxi via alfa charlie, runway one eight cleared for takeoff.",
            taxiDecision);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Problems,
            problem => problem.Contains("cleared for takeoff", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_landing_clearance_added_to_an_approach_clearance_is_rejected()
    {
        var approachDecision = new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.ApproachClearance,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["approach"] = "ILS",
                ["runway"] = "36",
            },
            ProhibitedFacts = ["cleared to land"],
            FallbackPhraseology = "Storm one two three, cleared ILS runway three six approach.",
        };

        PhraseologyValidationResult result = _validator.Validate(
            "Storm one two three, cleared ILS runway three six approach, cleared to land.",
            approachDecision);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_permission_granted_without_authorisation_is_rejected()
    {
        var acknowledgement = new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Acknowledgement,
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal) { ["readback"] = "correct" },
            FallbackPhraseology = "Storm one two three, readback correct.",
        };

        PhraseologyValidationResult result = _validator.Validate(
            "Storm one two three, readback correct, line up and wait.",
            acknowledgement);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_response_that_omits_a_mandatory_fact_is_rejected()
    {
        AtcDecision decision = ClearanceDecision();

        PhraseologyValidationResult result = _validator.Validate(
            "Storm one two three, cleared to Storm Delta, climb initially seven thousand.",
            decision);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Natural_phrasing_that_says_the_same_things_is_accepted()
    {
        // The model is allowed to be more conversational, as long as every value is the
        // engine's. This is the whole point of letting it phrase at all.
        AtcDecision decision = ClearanceDecision();

        PhraseologyValidationResult result = _validator.Validate(
            "Storm one two three, you're cleared to Storm Delta via runway one eight, "
            + "climb and maintain seven thousand initially, and squawk four two seven one.",
            decision);

        Assert.True(result.IsValid, string.Join("; ", result.Problems));
    }

    [Fact]
    public void A_frequency_written_with_fewer_decimals_still_matches()
    {
        var handoffDecision = new AtcDecision
        {
            Accepted = true,
            ResponseType = AtcResponseType.Handoff,
            Handoff = new ControllerHandoff
            {
                FromController = ControllerPosition.ClearanceDelivery,
                ToController = ControllerPosition.Ground,
                ToFrequency = Frequency.FromMegahertz(121.700m),
                InstructedAt = Fixture.Now,
            },
            ApprovedFacts = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["frequency"] = "121.700",
                ["controller"] = "Ground",
            },
            FallbackPhraseology = "Storm one two three, contact Ground one two one decimal seven.",
        };

        PhraseologyValidationResult result = _validator.Validate(
            "Storm one two three, contact Ground on one two one decimal seven.",
            handoffDecision);

        Assert.True(result.IsValid, string.Join("; ", result.Problems));
    }

    [Fact]
    public void The_callsign_digits_are_not_treated_as_an_unapproved_value()
    {
        AtcDecision decision = TakeoffDecision();

        PhraseologyValidationResult result = _validator.Validate(
            "Storm one two three, runway one eight, cleared for takeoff.",
            decision);

        Assert.True(result.IsValid, string.Join("; ", result.Problems));
    }
}
