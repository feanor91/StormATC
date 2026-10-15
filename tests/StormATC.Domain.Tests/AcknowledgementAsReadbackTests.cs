using StormATC.Domain.Atc;
using StormATC.Domain.Intents;
using StormATC.Domain.Readback;
using StormATC.Domain.Sessions;

namespace StormATC.Domain.Tests;

/// <summary>A plain "roger" settles an outstanding readback, except on the Strict profile.</summary>
public class AcknowledgementAsReadbackTests
{
    private readonly StormAtcStateEngine _engine = new(new DeterministicReadbackEvaluator());

    /// <summary>A session holding a taxi clearance that has not been read back.</summary>
    private AtcSession WithTaxiReadbackPending(out AtcSession fresh)
    {
        fresh = TestScenario.NewSession() with
        {
            CurrentController = ControllerPosition.Ground,
            CurrentFrequency = TestScenario.Ground,
        };

        AtcDecision taxi = _engine.Evaluate(TestScenario.Context(
            fresh, TestScenario.Intent(PilotIntentType.RequestTaxi, "request taxi")));
        Assert.NotNull(taxi.RequiredReadback);

        return fresh with
        {
            PendingReadback = taxi.RequiredReadback,
            Phase = taxi.NewPhase ?? fresh.Phase,
        };
    }

    /// <summary>The same session after one failed attempt to read the clearance back.</summary>
    private static AtcSession AfterAFailedAttempt(AtcSession pending) =>
        pending with { PendingReadback = pending.PendingReadback!.WithAttempt([]) };

    [Fact]
    public void The_first_roger_is_sent_back_for_a_real_readback()
    {
        AtcSession pending = WithTaxiReadbackPending(out _);

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            pending, TestScenario.Intent(PilotIntentType.PilotAcknowledgement, "roger")));

        Assert.False(decision.Accepted);
        Assert.False(decision.ClearsPendingReadback);
    }

    [Fact]
    public void A_roger_after_a_failed_attempt_settles_the_readback()
    {
        AtcSession stuck = AfterAFailedAttempt(WithTaxiReadbackPending(out _));

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            stuck, TestScenario.Intent(PilotIntentType.PilotAcknowledgement, "roger")));

        Assert.True(decision.Accepted);
        Assert.True(decision.ClearsPendingReadback);
    }

    [Fact]
    public void The_strict_profile_still_demands_the_readback()
    {
        AtcSession pending = AfterAFailedAttempt(WithTaxiReadbackPending(out _));

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            pending,
            TestScenario.Intent(PilotIntentType.PilotAcknowledgement, "roger"),
            realism: RealismProfile.Strict));

        Assert.False(decision.ClearsPendingReadback);
        Assert.False(decision.Accepted);
    }

    [Fact]
    public void A_roger_with_nothing_outstanding_is_just_acknowledged()
    {
        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            TestScenario.NewSession(),
            TestScenario.Intent(PilotIntentType.PilotAcknowledgement, "roger")));

        Assert.True(decision.Accepted);
        Assert.False(decision.ClearsPendingReadback);
    }

    [Fact]
    public void The_option_can_turn_it_off_on_any_profile()
    {
        AtcSession pending = AfterAFailedAttempt(WithTaxiReadbackPending(out _));

        AtcDecision decision = _engine.Evaluate(TestScenario.Context(
            pending,
            TestScenario.Intent(PilotIntentType.PilotAcknowledgement, "roger"),
            realism: RealismProfile.Standard with { AcceptAcknowledgementAsReadback = false }));

        Assert.False(decision.ClearsPendingReadback);
    }
}
