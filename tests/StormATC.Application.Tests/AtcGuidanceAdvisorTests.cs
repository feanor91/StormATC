using StormATC.Application.Guidance;
using StormATC.Domain;
using StormATC.Domain.Sessions;

namespace StormATC.Application.Tests;

public class AtcGuidanceAdvisorTests
{
    [Fact]
    public void Preflight_guidance_prompts_for_clearance()
    {
        AtcGuidance guidance = AtcGuidanceAdvisor.ForSession(Fixture.Session());

        Assert.Equal("Request IFR clearance", guidance.Title);
        Assert.Contains("Storm one two three", guidance.Example, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pending_readback_guidance_names_only_the_unsatisfied_items()
    {
        AtcSession session = Fixture.SessionAwaitingClearanceReadback();
        PendingReadback pending = session.PendingReadback! with
        {
            AttemptCount = 1,
            SatisfiedItems = [ReadbackItemType.Runway],
        };

        AtcGuidance guidance = AtcGuidanceAdvisor.ForSession(
            session.WithPendingReadback(pending));

        Assert.Equal("Correct the readback", guidance.Title);
        Assert.DoesNotContain("runway one eight", guidance.Example, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("seven thousand", guidance.Example, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("four two seven one", guidance.Example, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Acknowledged_clearance_guidance_moves_to_pushback()
    {
        AtcSession session = Fixture.Session() with
        {
            Phase = FlightPhase.IFRClearanceIssued,
        };

        AtcGuidance guidance = AtcGuidanceAdvisor.ForSession(session);

        Assert.Equal("Request pushback and start", guidance.Title);
        Assert.Contains("pushback", guidance.Example, StringComparison.OrdinalIgnoreCase);
    }
}
